"""Exercise the real HTTP forwarding and cancellation paths, without a model."""
import http.server
import json
import pathlib
import tempfile
import threading
import time
import unittest
import unittest.mock
import urllib.request

import local_ai_cache_proxy as proxy

# Fork selection posts to the model before the chat body. These tests use a
# fake upstream that only understands the completion itself.
REAL_ALIGN_FORK = proxy.align_fork
proxy.align_fork = lambda body: None
proxy.note_reuse = lambda *_args: None


class ProxyTests(unittest.TestCase):
    def setUp(self):
        proxy.reset_tool_surface()

    def test_openclaw_keeps_all_instructions_and_history(self):
        stable = "## OpenClaw Control\ncontrol\n## Workspace Files (injected)\nworkspace"
        tail = "\n## Temporal Context\nchanging time\n## Authorized Senders\nowner"
        history = [{"role": "user", "content": "hello"},
                   {"role": "assistant", "content": "reply"},
                   {"role": "user", "content": "next"}]
        body = {"messages": [{"role": "system", "content": stable + tail}] + history}
        prepared, metrics = proxy.prepare_request(body)
        self.assertEqual(prepared["messages"][0]["content"], stable)
        self.assertEqual(prepared["messages"][1:-1], history)
        self.assertEqual(prepared["messages"][-1], {"role": "system", "content": tail})
        self.assertEqual(body["messages"][0]["content"], stable + tail)
        self.assertEqual(metrics["moved_system_tail_chars"], len(tail))
        self.assertTrue(prepared["cache_prompt"])

    def test_other_clients_and_explicit_cache_choice_are_preserved(self):
        messages = [{"role": "system", "content": "ordinary instructions"},
                    {"role": "user", "content": "hello"}]
        prepared, _ = proxy.prepare_request({"messages": messages, "cache_prompt": False})
        self.assertEqual(prepared["messages"], messages)
        self.assertFalse(prepared["cache_prompt"])

    def _openclaw_turn(self, names, history, workspace="workspace"):
        tooling = "\n".join([
            "## Tooling",
            "Tools policy-filtered. Names case-sensitive; call exact.",
            *[f"- {name}: does {name}" for name in names],
            "### Deferred Tool Schemas",
            "Available deferred-schema tools:",
            *[f"- {name}: deferred {name}" for name in names],
            "The AGENTS.md Tools section guides usage; it never grants availability.",
        ])
        stable = f"## OpenClaw Control\ncontrol\n## Workspace Files (injected)\n{workspace}"
        tail = "\n## Temporal Context\nclock"
        return {
            "messages": [{"role": "system", "content": tooling + "\n" + stable + tail}, *history],
            "tools": [{"type": "function", "function": {"name": name}} for name in names],
        }

    def test_tool_changes_keep_history_and_move_to_the_suffix(self):
        history = [{"role": "user", "content": "hello"}]
        first, _ = proxy.prepare_request(self._openclaw_turn(["read"], history))
        self.assertNotIn("Tool Surface Update", json.dumps(first["messages"]))
        continued = history + [{"role": "assistant", "content": "reply"},
                               {"role": "user", "content": "next"}]
        prepared, metrics = proxy.prepare_request(self._openclaw_turn(
            ["read", "screen", "show_widget"], continued))
        self.assertIn("- read: does read", prepared["messages"][0]["content"])
        self.assertNotIn("screen", prepared["messages"][0]["content"])
        self.assertEqual([tool["function"]["name"] for tool in prepared["tools"]], ["read"])
        self.assertEqual(prepared["messages"][1:4], continued)
        suffix = prepared["messages"][-2]["content"]
        self.assertTrue(prepared["messages"][-1]["content"].startswith("\n## Temporal Context\nclock"))
        self.assertIn("Additional tools:", suffix)
        self.assertIn("- screen: does screen", suffix)
        self.assertIn("- show_widget: deferred show_widget", suffix)
        self.assertNotIn("Unavailable now:", suffix)
        self.assertEqual(metrics["tool_surface_added"], 2)
        self.assertEqual(metrics["tool_surface_removed"], 0)
        self.assertGreater(metrics["tool_surface_suffix_chars"], 0)

        shorter = [{"role": "user", "content": "other branch"}]
        branched, metrics = proxy.prepare_request(self._openclaw_turn(
            ["read", "screen", "show_widget"], shorter))
        self.assertEqual(branched["messages"][1]["content"], "other branch")
        self.assertIn("- screen: does screen", branched["messages"][-2]["content"])
        self.assertNotIn("screen", branched["messages"][0]["content"])
        self.assertEqual([tool["function"]["name"] for tool in branched["tools"]], ["read"])
        self.assertGreater(metrics["tool_surface_suffix_chars"], 0)

        proxy.reset_tool_surface()
        proxy.prepare_request(self._openclaw_turn(
            ["read", "screen", "show_widget"], history))
        reduced, metrics = proxy.prepare_request(self._openclaw_turn(["read"], continued))
        self.assertIn("- screen: does screen", reduced["messages"][0]["content"])
        self.assertIn("Unavailable now: screen, show_widget.", reduced["messages"][-2]["content"])
        self.assertNotIn("Additional tools:", reduced["messages"][-2]["content"])
        self.assertTrue(reduced["messages"][-1]["content"].startswith("\n## Temporal Context\n"))
        self.assertEqual(metrics["tool_surface_removed"], 2)
        self.assertEqual([tool["function"]["name"] for tool in reduced["tools"]],
                         ["read", "screen", "show_widget"])

    def test_changed_history_still_moves_a_short_tool_delta(self):
        proxy.prepare_request(self._openclaw_turn(
            ["read"], [{"role": "user", "content": "hello"}]))
        changed_history, metrics = proxy.prepare_request(self._openclaw_turn(
            ["screen"], [{"role": "user", "content": "different"}]))
        self.assertIn("- read: does read", changed_history["messages"][0]["content"])
        self.assertNotIn("screen", changed_history["messages"][0]["content"])
        self.assertIn("- screen: does screen", changed_history["messages"][-2]["content"])
        self.assertEqual(changed_history["messages"][1]["content"], "different")
        self.assertEqual(metrics["tool_surface_added"], 1)
        self.assertEqual([tool["function"]["name"] for tool in changed_history["tools"]], ["read"])

    def test_a_long_tool_suffix_stays_in_front(self):
        previous = proxy.MAX_TOOL_SURFACE_SUFFIX_CHARS
        proxy.MAX_TOOL_SURFACE_SUFFIX_CHARS = 40
        try:
            proxy.prepare_request(self._openclaw_turn(
                ["read"], [{"role": "user", "content": "hello"}]))
            prepared, metrics = proxy.prepare_request(self._openclaw_turn(
                ["read", "screen"], [{"role": "user", "content": "shorter"}]))
        finally:
            proxy.MAX_TOOL_SURFACE_SUFFIX_CHARS = previous
        self.assertIn("- screen: does screen", prepared["messages"][0]["content"])
        self.assertNotIn("Tool Surface Update", json.dumps(prepared["messages"]))
        self.assertEqual(metrics["tool_surface_suffix_chars"], 0)
        self.assertEqual([tool["function"]["name"] for tool in prepared["tools"]],
                         ["read", "screen"])

    def test_changed_system_text_is_not_rewritten(self):
        proxy.prepare_request(self._openclaw_turn(
            ["read"], [{"role": "user", "content": "hello"}]))
        changed_system, _ = proxy.prepare_request(self._openclaw_turn(
            ["screen"], [{"role": "user", "content": "hello"},
                         {"role": "assistant", "content": "reply"},
                         {"role": "user", "content": "next"}],
            workspace="other"))
        self.assertIn("- screen: does screen", changed_system["messages"][0]["content"])
        self.assertNotIn("Tool Surface Update", changed_system["messages"][-1]["content"])

    def test_sse_is_immediate_and_disconnect_cancels_upstream(self):
        disconnected = threading.Event()
        received = []

        class Upstream(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                received.append(body)
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.flush()
                if body["messages"][-1]["content"] == "disconnect":
                    self.connection.settimeout(3)
                    if self.connection.recv(1) == b"":
                        disconnected.set()
                    return
                self.wfile.write(b'data: {"choices":[{"delta":{"content":"first"}}]}\n\n')
                self.wfile.flush()
                time.sleep(0.5)
                self.wfile.write(b'data: {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":2,"prompt_tokens_details":{"cached_tokens":80}},"timings":{"cache_n":80,"prompt_n":20}}\n\ndata: [DONE]\n\n')
                self.wfile.flush()

        class QuietProxy(proxy.Handler):
            def record_prefix(self, _):
                pass

            def log_message(self, *_):
                pass

        upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
        downstream = proxy.Server(("127.0.0.1", 0), QuietProxy)
        old_upstream, old_metrics, old_requests = proxy.UPSTREAM, proxy.METRICS_PATH, proxy.RECENT_REQUESTS
        with tempfile.TemporaryDirectory() as directory:
            proxy.UPSTREAM = f"http://127.0.0.1:{upstream.server_port}"
            proxy.METRICS_PATH = pathlib.Path(directory) / "metrics.json"
            proxy.RECENT_REQUESTS = []
            threads = [threading.Thread(target=s.serve_forever, daemon=True)
                       for s in (upstream, downstream)]
            for thread in threads:
                thread.start()
            try:
                def request(content):
                    return urllib.request.Request(
                        f"http://127.0.0.1:{downstream.server_port}/v1/chat/completions",
                        data=json.dumps({"model": "test", "stream": True,
                                         "messages": [{"role": "user", "content": content}]}).encode(),
                        headers={"Content-Type": "application/json"})

                started = time.monotonic()
                with urllib.request.urlopen(request("stream"), timeout=3) as response:
                    first = response.readline()
                    self.assertIn(b"first", first)
                    self.assertLess(time.monotonic() - started, 0.4)
                    self.assertIn(b"[DONE]", response.read())
                self.assertTrue(received[0]["cache_prompt"])
                self.assertTrue(received[0]["stream_options"]["include_usage"])
                response = urllib.request.urlopen(request("disconnect"), timeout=3)
                response.close()
                self.assertTrue(disconnected.wait(2), "upstream kept processing a disconnected client")
                # Completion metrics contain token accounting, never generated content.
                deadline = time.monotonic() + 2
                while not proxy.METRICS_PATH.exists() and time.monotonic() < deadline:
                    time.sleep(0.01)
                records = json.loads(proxy.METRICS_PATH.read_text())
                complete = next(r for r in records if r.get("usage"))
                self.assertEqual(complete["usage"]["prompt_tokens_details"]["cached_tokens"], 80)
                self.assertNotIn("first", json.dumps(records).replace("first_content_seconds", "").replace("first_event_seconds", ""))
            finally:
                for server in (downstream, upstream):
                    server.shutdown()
                    server.server_close()
                proxy.UPSTREAM, proxy.METRICS_PATH, proxy.RECENT_REQUESTS = old_upstream, old_metrics, old_requests


    def test_chat_completion_asks_for_the_matching_fork(self):
        seen = []
        previous = proxy.align_fork
        proxy.align_fork = seen.append

        class Upstream(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                self.rfile.read(int(self.headers.get("Content-Length", "0")))
                body = b'{"choices":[{"message":{"content":"ok"}}]}'
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        class QuietProxy(proxy.Handler):
            def record_prefix(self, _):
                pass

            def log_message(self, *_):
                pass

        upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
        downstream = proxy.Server(("127.0.0.1", 0), QuietProxy)
        old_upstream = proxy.UPSTREAM
        proxy.UPSTREAM = f"http://127.0.0.1:{upstream.server_port}"
        threads = [threading.Thread(target=server.serve_forever, daemon=True)
                   for server in (upstream, downstream)]
        for thread in threads:
            thread.start()
        try:
            request = urllib.request.Request(
                f"http://127.0.0.1:{downstream.server_port}/v1/chat/completions",
                data=json.dumps({"model": "test", "messages": [
                    {"role": "user", "content": "hello"}]}).encode(),
                headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=3) as response:
                self.assertEqual(response.status, 200)
            self.assertEqual(len(seen), 1)
            self.assertTrue(seen[0]["cache_prompt"])
            self.assertEqual(seen[0]["messages"][-1]["content"], "hello")
        finally:
            proxy.align_fork = previous
            proxy.UPSTREAM = old_upstream
            for server in (downstream, upstream):
                server.shutdown()
                server.server_close()

    def test_align_fork_tokenizes_the_rendered_prompt(self):
        posted = []

        class Response:
            def __init__(self, payload):
                self.payload = payload

            def __enter__(self):
                return self

            def __exit__(self, *_):
                return False

            def read(self):
                return json.dumps(self.payload).encode()

        def urlopen(request, timeout=30):
            posted.append(json.loads(request.data.decode()))
            if request.full_url.endswith("/tokenize"):
                return Response({"tokens": [4, 5, 6]})
            return Response({"prompt": "rendered prompt"})

        aligned = []
        with unittest.mock.patch.object(proxy.urllib.request, "urlopen", urlopen):
            with unittest.mock.patch.dict("sys.modules", {"kv_disk_cache": unittest.mock.Mock(
                    align_request=aligned.append)}):
                REAL_ALIGN_FORK({"messages": [{"role": "user", "content": "hello"}]})
        self.assertEqual(posted[0]["messages"][0]["content"], "hello")
        self.assertEqual(posted[1], {"content": "rendered prompt", "add_special": False, "parse_special": True})
        self.assertEqual(aligned, [[4, 5, 6]])

    def test_liveness_policy_fails_only_when_the_slot_is_idle(self):
        self.assertEqual(proxy.liveness_action(True, 3, True, 2), ("wait", 0))
        self.assertEqual(proxy.liveness_action(False, 0, True, 0), ("fail", 0))
        self.assertEqual(proxy.liveness_action(False, 2, True, 0), ("wake", 0))
        self.assertEqual(proxy.liveness_action(False, 2, True, 4), ("fail", 4))
        self.assertEqual(proxy.liveness_action(None, 9, True, 1), ("wait", 1))
        self.assertEqual(proxy.liveness_action(False, 2, False, 3), ("wait", 0))

    def test_idle_request_fails_and_real_work_is_not_interrupted(self):
        self._assert_proxy_outcome("hang", b"model_idle", 503)
        self._assert_proxy_outcome("slow", b"kept", 200)

    def test_stranded_request_is_resumed_instead_of_left_pending(self):
        self._assert_proxy_outcome("wake", b"resumed", 200)
        self.assertTrue(RuntimeUpstream.seen_wake)

    def _assert_proxy_outcome(self, mode, expected, status):
        RuntimeUpstream.mode = mode
        RuntimeUpstream.processing = mode == "slow"
        RuntimeUpstream.deferred = 1 if mode == "wake" else 0
        RuntimeUpstream.release = threading.Event()
        RuntimeUpstream.seen_wake = False

        class QuietProxy(proxy.Handler):
            def record_prefix(self, _):
                pass

            def log_message(self, *_):
                pass

        upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), RuntimeUpstream)
        downstream = proxy.Server(("127.0.0.1", 0), QuietProxy)
        old = (proxy.UPSTREAM, proxy.METRICS_PATH, proxy.RECENT_REQUESTS,
               proxy.IDLE_STALL_SECONDS, proxy.LIVENESS_POLL_SECONDS)
        with tempfile.TemporaryDirectory() as directory:
            proxy.UPSTREAM = f"http://127.0.0.1:{upstream.server_port}"
            proxy.METRICS_PATH = pathlib.Path(directory) / "metrics.json"
            proxy.RECENT_REQUESTS = []
            proxy.IDLE_STALL_SECONDS = 0.4
            proxy.LIVENESS_POLL_SECONDS = 0.05
            threads = [threading.Thread(target=s.serve_forever, daemon=True)
                       for s in (upstream, downstream)]
            for thread in threads:
                thread.start()
            try:
                started = time.monotonic()
                request = urllib.request.Request(
                    f"http://127.0.0.1:{downstream.server_port}/v1/chat/completions",
                    data=json.dumps({"model": "test", "stream": True,
                                     "messages": [{"role": "user", "content": mode}]}).encode(),
                    headers={"Content-Type": "application/json"})
                try:
                    with urllib.request.urlopen(request, timeout=5) as response:
                        body = response.read()
                        code = response.status
                except urllib.error.HTTPError as exc:
                    body = exc.read()
                    code = exc.code
                self.assertEqual(code, status)
                self.assertIn(expected, body)
                self.assertLess(time.monotonic() - started, 4)
            finally:
                for server in (downstream, upstream):
                    server.shutdown()
                    server.server_close()
                (proxy.UPSTREAM, proxy.METRICS_PATH, proxy.RECENT_REQUESTS,
                 proxy.IDLE_STALL_SECONDS, proxy.LIVENESS_POLL_SECONDS) = old


class RuntimeUpstream(http.server.BaseHTTPRequestHandler):
    mode = "hang"
    processing = False
    deferred = 0
    release = None
    seen_wake = False

    def log_message(self, *_):
        pass

    def do_GET(self):
        if self.path.startswith("/slots"):
            body = json.dumps([{"is_processing": type(self).processing}]).encode()
            content_type = "application/json"
        elif self.path.startswith("/metrics"):
            body = f"requests_deferred {type(self).deferred}\n".encode()
            content_type = "text/plain"
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        content = body["messages"][-1]["content"]
        if content == proxy.WAKE_CONTENT:
            type(self).seen_wake = True
            type(self).deferred = 0
            type(self).processing = True
            type(self).release.set()
            payload = json.dumps({"choices": [{"message": {"content": "w"}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return
        if type(self).mode == "slow":
            time.sleep(1.0)
            self._sse("kept")
            type(self).processing = False
            return
        if type(self).mode == "wake":
            self.connection.settimeout(5)
            type(self).release.wait(5)
            self._sse("resumed")
            type(self).processing = False
            return
        self.connection.settimeout(5)
        try:
            self.connection.recv(1)
        except OSError:
            return

    def _sse(self, word):
        payload = f'data: {{"choices":[{{"delta":{{"content":"{word}"}}}}]}}\n\ndata: [DONE]\n\n'.encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)
        self.wfile.flush()


class EchoServiceTests(unittest.TestCase):
    """ECHO-SVC-001/002: real proxy sockets, isolated fake model and metrics."""
    def setUp(self):
        import socket
        self.socket = socket
        self.received = []
        self.entered = threading.Event()
        self.cancelled = threading.Event()
        test = self
        class Upstream(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass
            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                test.received.append((self.path, body))
                if body.get('prompt') == 'cancel' or any(m.get('content') == 'cancel' for m in body.get('messages', [])):
                    test.entered.set()
                    self.connection.settimeout(3)
                    if self.connection.recv(1) == b'':
                        test.cancelled.set()
                    return
                payload = json.dumps({'content':' English', 'tokens_evaluated':80,
                    'tokens_predicted':1, 'timings':{'cache_n':70,'prompt_n':10},
                    'completion_probabilities':[{'top_logprobs':[{'token':' English','logprob':-1}]}]}).encode()
                self.send_response(200)
                self.send_header('Content-Type','application/json')
                self.send_header('Content-Length',str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
        class Quiet(proxy.Handler):
            def log_message(self, *_):
                pass
            def record_prefix(self, _):
                pass
        self.upstream = http.server.ThreadingHTTPServer(('127.0.0.1',0),Upstream)
        self.downstream = proxy.Server(('127.0.0.1',0),Quiet)
        self.temp = tempfile.TemporaryDirectory()
        self.patches = [
            unittest.mock.patch.object(proxy,'UPSTREAM',f'http://127.0.0.1:{self.upstream.server_port}'),
            unittest.mock.patch.object(proxy,'METRICS_PATH',pathlib.Path(self.temp.name)/'metrics.json'),
            unittest.mock.patch.object(proxy,'RECENT_REQUESTS',[]),
            unittest.mock.patch.object(proxy,'align_fork'),
        ]
        for patch in self.patches:
            patch.start()
        self.aligned = proxy.align_fork
        for server in (self.upstream,self.downstream):
            threading.Thread(target=server.serve_forever,daemon=True).start()

    def tearDown(self):
        for server in (self.downstream,self.upstream):
            server.shutdown()
            server.server_close()
        for patch in reversed(self.patches):
            patch.stop()
        self.temp.cleanup()

    def request(self,path,body=None):
        return urllib.request.Request(f'http://127.0.0.1:{self.downstream.server_port}'+path,
            data=None if body is None else json.dumps(body).encode(),
            headers={'Content-Type':'application/json','X-Local-AI-Client':'echo'})

    def test_raw_prediction_preserves_prompt_probabilities_and_explicit_cache_choice(self):
        for cache in [True,False]:
            body={'prompt':'<|im_start|>user\nI want to learn','n_predict':1,'n_probs':20,'cache_prompt':cache}
            with urllib.request.urlopen(self.request('/completion',body),timeout=3) as response:
                result=json.load(response)
            self.assertEqual(result['completion_probabilities'][0]['top_logprobs'][0]['token'],' English')
            self.assertEqual(self.received[-1],('/completion',body))
        self.assertEqual(self.aligned.call_count,2)

    def test_raw_metrics_have_usage_without_prompt_or_generated_text(self):
        with urllib.request.urlopen(self.request('/completion',{'prompt':'private-input','n_probs':20}),timeout=3) as response:
            response.read()
        deadline=time.monotonic()+2
        while not proxy.RECENT_REQUESTS and time.monotonic()<deadline:
            time.sleep(0.01)
        record=proxy.RECENT_REQUESTS[-1]
        self.assertEqual(record['request_kind'],'raw_completion')
        self.assertEqual(record['client'],'echo')
        self.assertEqual(record['usage']['prompt_tokens_details']['cached_tokens'],70)
        self.assertEqual(record['usage']['prompt_tokens'],80)
        self.assertNotIn('private-input',json.dumps(record))
        self.assertNotIn('English',json.dumps(record))
        self.assertNotIn('completion_probabilities',record)

    def test_readonly_capabilities_only_probe_metadata(self):
        with urllib.request.urlopen(self.request('/v1/echo/capabilities'),timeout=3) as response:
            caps=json.load(response)
        self.assertFalse(caps['kv']['fork_at'])
        self.assertTrue(caps['cancellation']['non_streaming'])
        self.assertEqual(caps['prediction']['endpoint'],'/completion')
        self.assertEqual(self.received,[('/v1/kv/op', {'op':'capabilities'})])

    def test_capabilities_only_advertise_verified_shared_primitives(self):
        primitive = {key:True for key in ('supported','shared_prefix','fork_at','label_constraint','release')}
        primitive.update(branch_batch_size=1,max_tokens=8192)
        with unittest.mock.patch.object(proxy, 'upstream_json', return_value=primitive):
            with urllib.request.urlopen(self.request('/v1/echo/capabilities'),timeout=3) as response:
                caps=json.load(response)
        self.assertTrue(caps['kv']['fork_at'])
        self.assertEqual(caps['kv']['endpoint'],'/v1/kv/op')
        self.assertEqual(self.received,[])

    def cancel_before_headers(self,path,body):
        connection=self.socket.create_connection(('127.0.0.1',self.downstream.server_port))
        payload=json.dumps(body).encode()
        headers=f'POST {path} HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: {len(payload)}\r\n\r\n'.encode()
        connection.sendall(headers+payload)
        self.assertTrue(self.entered.wait(2))
        connection.close()
        self.assertTrue(self.cancelled.wait(2),'upstream retained a cancelled non-streaming request')

    def test_raw_completion_cancels_while_waiting_for_headers(self):
        self.cancel_before_headers('/completion',{'prompt':'cancel','n_predict':1,'n_probs':20})

    def test_json_selector_cancels_while_waiting_for_headers(self):
        self.cancel_before_headers('/v1/chat/completions',{'stream':False,
            'messages':[{'role':'user','content':'cancel'}],
            'response_format':{'type':'json_object'}})

    def test_raw_fork_alignment_uses_tokenize_without_apply_template(self):
        calls=[]
        def upstream(path,body):
            calls.append((path,body))
            return {'tokens':[4,5,6]}
        aligned=[]
        with unittest.mock.patch.object(proxy,'upstream_json',side_effect=upstream), \
                unittest.mock.patch.dict('sys.modules',{'kv_disk_cache':unittest.mock.Mock(align_request=aligned.append)}):
            REAL_ALIGN_FORK({'prompt':'raw ChatML'})
        self.assertEqual([path for path,_ in calls],['/tokenize'])
        self.assertTrue(calls[0][1]['parse_special'])
        self.assertEqual(aligned,[[4,5,6]])

    def test_token_id_prompt_is_not_retokenized(self):
        aligned=[]
        with unittest.mock.patch.object(proxy,'upstream_json') as tokenize, \
                unittest.mock.patch.dict('sys.modules',{'kv_disk_cache':unittest.mock.Mock(align_request=aligned.append)}):
            REAL_ALIGN_FORK({'prompt':[4,5,6]})
        tokenize.assert_not_called()
        self.assertEqual(aligned,[[4,5,6]])


if __name__ == "__main__":
    unittest.main()
