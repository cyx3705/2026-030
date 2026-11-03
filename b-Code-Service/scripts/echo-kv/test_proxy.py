"""Hermetic wire tests for the compiled C++ listener; never loads a model."""
import http.client
import http.server
import json
import os
from pathlib import Path
import select
import socket
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
BINARY = ROOT / 'linux-cache/echo-kv-build/build/echo-proxy'


class Upstream(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, *args):
        pass

    def do_GET(self):
        self.reply(200, self.path.encode())

    def reply(self, status, body):
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        value = json.loads(body)
        mode = value.get('mode')
        if mode == 'cancel':
            self.server.entered.set()
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                if select.select([self.connection], [], [], .01)[0]:
                    if not self.connection.recv(1, socket.MSG_PEEK):
                        self.server.cancelled.set()
                        return
            self.reply(200, b'{}')
        elif mode in ('stream', 'broken_stream'):
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream')
            self.send_header('Transfer-Encoding', 'chunked')
            self.end_headers()
            try:
                for chunk in (b'data: first\n\n', b'data: second\n\n', b'data: [DONE]\n\n'):
                    self.wfile.write(f'{len(chunk):X}\r\n'.encode() + chunk + b'\r\n')
                    self.wfile.flush()
                    time.sleep(.15)
                    if mode == 'broken_stream':
                        self.connection.shutdown(socket.SHUT_RDWR)
                        self.close_connection = True
                        return
                self.wfile.write(b'0\r\n\r\n')
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                self.server.cancelled.set()
        elif mode == 'delay':
            self.server.entered.set()
            time.sleep(.4)
            self.reply(200, body)
        else:
            self.reply(value.get('status', 200), body)


class ProxyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.upstream = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
        cls.upstream.daemon_threads = True
        cls.upstream.entered = threading.Event()
        cls.upstream.cancelled = threading.Event()
        cls.thread = threading.Thread(target=cls.upstream.serve_forever, daemon=True)
        cls.thread.start()
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        (cls.root / 'status').mkdir()
        (cls.root / 'config').mkdir()
        (cls.root / 'config/echo-models.json').write_text(json.dumps({
            'default': 'qwen', 'models': [{'name': 'qwen'}, {'name': 'gemma', 'shared_kv': False}]}))
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0))
            cls.port = probe.getsockname()[1]
        cls.process = subprocess.Popen([str(BINARY), str(cls.port)], env={**os.environ,
            'LOCAL_AI_ROOT': str(cls.root), 'LOCAL_AI_UPSTREAM': f'http://127.0.0.1:{cls.upstream.server_port}'},
            stdout=subprocess.DEVNULL)
        for _ in range(100):
            try:
                cls.request('GET', '/cache/stats')
                break
            except OSError:
                time.sleep(.01)
        else:
            raise RuntimeError('C++ listener did not start')

    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        cls.process.wait(timeout=5)
        cls.upstream.shutdown()
        cls.upstream.server_close()
        cls.thread.join()
        cls.temp.cleanup()

    def setUp(self):
        for name in ('model-switch.json', 'active-model.json'):
            (self.root / 'status' / name).unlink(missing_ok=True)
        self.upstream.entered.clear()
        self.upstream.cancelled.clear()

    @classmethod
    def request(cls, method, path, value=None, headers=None):
        call = http.client.HTTPConnection('127.0.0.1', cls.port, timeout=3)
        try:
            call.request(method, path, None if value is None else json.dumps(value).encode(),
                         headers or {'Content-Type': 'application/json'})
            response = call.getresponse()
            return response.status, response.read()
        finally:
            call.close()

    def test_unicode_body_and_query_preserved(self):
        value = {'text': '中文 bank', 'stream': False}
        code, body = self.request('POST', '/completion', value)
        self.assertEqual((code, json.loads(body)), (200, value))
        self.assertEqual(self.request('GET', '/slots?fail_on_no_slot=1'),
                         (200, b'/slots?fail_on_no_slot=1'))

    def test_error_status_and_body_preserved_even_for_stream_request(self):
        value = {'error': 'model_not_served', 'status': 422, 'stream': True}
        code, body = self.request('POST', '/v1/chat/completions', value)
        self.assertEqual(code, 422)
        self.assertEqual(json.loads(body), value)

    def test_sse_is_incremental_and_finishes(self):
        call = http.client.HTTPConnection('127.0.0.1', self.port, timeout=3)
        try:
            started = time.monotonic()
            call.request('POST', '/v1/chat/completions', json.dumps({'mode': 'stream', 'stream': True}),
                         {'Content-Type': 'application/json'})
            response = call.getresponse()
            self.assertEqual(response.readline(), b'data: first\n')
            self.assertLess(time.monotonic() - started, .30)
            rest = response.read()
            self.assertIn(b'data: second', rest)
            self.assertIn(b'data: [DONE]', rest)
        finally:
            call.close()

    def test_broken_stream_remains_incomplete(self):
        with self.assertRaises(http.client.IncompleteRead):
            self.request('POST', '/v1/chat/completions', {'mode': 'broken_stream', 'stream': True})

    def test_cancel_before_headers_closes_upstream(self):
        call = http.client.HTTPConnection('127.0.0.1', self.port, timeout=3)
        call.request('POST', '/completion', json.dumps({'mode': 'cancel'}), {'Content-Type': 'application/json'})
        self.assertTrue(self.upstream.entered.wait(1))
        call.close()
        self.assertTrue(self.upstream.cancelled.wait(1), 'upstream socket stayed open')
        self.assertEqual(self.request('POST', '/completion', {'text': 'next'})[0], 200)

    def test_slow_request_does_not_serialize_another_request(self):
        errors = []
        def slow():
            try: self.request('POST', '/completion', {'mode': 'delay'})
            except Exception as error: errors.append(error)
        worker = threading.Thread(target=slow)
        worker.start()
        try:
            self.assertTrue(self.upstream.entered.wait(1))
            start = time.monotonic()
            self.assertEqual(self.request('POST', '/v1/kv/op', {'op': 'next'})[0], 200)
            self.assertLess(time.monotonic()-start, .25)
        finally:
            worker.join()
        self.assertEqual(errors, [])

    def test_model_switch_blocks_inference_but_allows_release(self):
        (self.root/'status/model-switch.json').write_text('{"state":"switching"}')
        self.assertEqual(self.request('POST', '/completion', {})[0], 503)
        self.assertEqual(self.request('POST', '/v1/kv/op', {'op': 'release'})[0], 200)

    def test_unsupported_model_never_claims_native_kv(self):
        (self.root/'status/active-model.json').write_text('{"name":"gemma"}')
        code, body = self.request('POST', '/v1/kv/op', {'op': 'capabilities'})
        self.assertEqual(code, 200)
        self.assertFalse(json.loads(body)['supported'])
        self.assertEqual(self.request('POST', '/v1/kv/op', {'op': 'create'})[0], 501)

    def test_browser_origin_cannot_switch_model(self):
        self.assertEqual(self.request('POST', '/v1/echo/model', {'name': 'qwen'},
            {'Origin': 'https://example.com', 'Content-Type': 'application/json'})[0], 403)

    def test_openclaw_temporal_tail_keeps_system_role_after_history(self):
        system = '## OpenClaw Control\n## Workspace Files (injected)\nstable\n## Temporal Context\nnow'
        value = {'messages': [{'role': 'system', 'content': system}, {'role': 'user', 'content': 'hello'}]}
        code, body = self.request('POST', '/v1/chat/completions', value)
        result = json.loads(body)
        self.assertEqual(code, 200)
        self.assertEqual(result['messages'][-1], {'role': 'system', 'content': '\n## Temporal Context\nnow'})
        self.assertEqual(result['messages'][1]['content'], 'hello')
        self.assertTrue(result['cache_prompt'])
        _, body = self.request('POST', '/v1/chat/completions', value,
            {'Content-Type': 'application/json', 'X-Local-AI-Client': 'echo'})
        self.assertEqual(json.loads(body), value)

    def test_openclaw_tool_delta_retains_unavailable_and_added_descriptions(self):
        def payload(name):
            system = ('## OpenClaw Control\n## Workspace Files (injected)\ntool-test\n## Tooling\n- '
                + name + ': description\nThe AGENTS.md Tools section guides usage; it never grants availability.')
            return {'messages': [{'role': 'system', 'content': system}, {'role': 'user', 'content': 'hello'}],
                'tools': [{'type': 'function', 'function': {'name': name, 'description': 'description'}}]}
        self.request('POST', '/v1/chat/completions', payload('old_tool'))
        _, body = self.request('POST', '/v1/chat/completions', payload('new_tool'))
        value = json.loads(body)
        self.assertEqual(value['tools'][0]['function']['name'], 'old_tool')
        suffix = value['messages'][-1]['content']
        self.assertIn('Unavailable now: old_tool', suffix)
        self.assertIn('- new_tool: description', suffix)
        self.assertIn('"name":"new_tool"', suffix)


if __name__ == '__main__':
    unittest.main()
