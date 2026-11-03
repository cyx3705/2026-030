"""Opt-in, alternating same-model measurements of old Python vs C++ HTTP.

Starts only temporary HTTP listeners, never another model. Saves counts/times,
not prompts, generated content, or credentials. Run while Echo is idle.
"""
import argparse
import http.client
import json
import os
from pathlib import Path
import socket
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]


def measure(call, path, body, stream=False):
    started = time.perf_counter()
    call.request('POST', path, json.dumps(body), {'Content-Type': 'application/json', 'X-Local-AI-Client': 'echo'})
    response = call.getresponse()
    headers = time.perf_counter()
    if stream and response.status == 200:
        content_times = []
        done = False
        for line in response:
            if line.strip() == b'data: [DONE]':
                done = True
            if line.startswith(b'data: {'):
                event = json.loads(line[6:])
                if any(choice.get('delta', {}).get('content') for choice in event.get('choices', [])):
                    content_times.append(time.perf_counter())
        if not done or not content_times:
            raise RuntimeError('incomplete SSE')
        value = {'headers_ms': (headers-started)*1000, 'first_content_ms': (content_times[0]-started)*1000,
                 'total_ms': (time.perf_counter()-started)*1000,
                 'max_gap_ms': max((b-a)*1000 for a, b in zip(content_times, content_times[1:])) if len(content_times)>1 else 0}
        call.close()
        return value, {}
    data = json.loads(response.read())
    call.close()
    if response.status != 200:
        raise RuntimeError((response.status, data))
    return {'total_ms': (time.perf_counter()-started)*1000}, data


def main(rounds):
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    code = "import sys;sys.path.insert(0,sys.argv[1]);import local_ai_cache_proxy as p;p.LISTEN_PORT=int(sys.argv[2]);p.main()"
    old = subprocess.Popen([sys.executable, '-c', code, str(ROOT/'scripts'), str(port)],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    calls = {'direct': http.client.HTTPConnection('127.0.0.1', 18769, timeout=60),
             'python': http.client.HTTPConnection('127.0.0.1', port, timeout=60),
             'cpp': http.client.HTTPConnection('127.0.0.1', 11435, timeout=60)}
    sequence = None
    try:
        for _ in range(100):
            try:
                with socket.create_connection(('127.0.0.1', port), timeout=.1): break
            except OSError: time.sleep(.02)
        _, runtime = measure(calls['direct'], '/v1/kv/op', {'op': 'runtime'})
        if runtime['active_sequences'] or runtime['active_branches']:
            raise RuntimeError('model KV is busy; close the active conversation before benchmarking')
        _, models = measure_models(calls['direct'])
        model = models['data'][0]['id']
        _, sequence = measure(calls['direct'], '/v1/kv/op', {'op': 'create', 'model': model, 'text': 'The bank holds my savings.'})
        fields = {key: sequence[key] for key in ('sequence_id', 'cache_epoch')}
        jobs = {
            'metadata': ('/v1/kv/op', {'op': 'capabilities'}, False),
            'next_token': ('/v1/kv/op', {'op': 'next', **fields}, False),
            'sense_branch': ('/v1/kv/op', {'op': 'select_text', **fields,
                'prefix_text': 'The bank holds my savings.',
                'suffix_text': '\nMeaning of bank? A: financial institution. B: river edge. Label:',
                'allowed_labels': [' A', ' B', '0']}, False),
            'prediction': ('/completion', {'model': model, 'prompt': 'I want to learn ', 'n_predict': 1,
                'n_probs': 20, 'cache_prompt': True, 'temperature': 0}, False),
            'stream_chat': ('/v1/chat/completions', {'model': model, 'messages': [{'role': 'user',
                'content': 'Say one short English sentence about books.'}], 'max_tokens': 16,
                'stream': True, 'temperature': 0}, True),
        }
        report = {'model': model, 'rounds': rounds, 'same_model_process': True, 'results': {}}
        for name, (path, body, streaming) in jobs.items():
            rows = {key: [] for key in calls}
            for turn in range(rounds+1):
                order = list(calls) if turn % 2 == 0 else list(reversed(calls))
                for provider in order:
                    row, _ = measure(calls[provider], path, body, streaming)
                    if turn: rows[provider].append(row)
            report['results'][name] = {provider: {metric: {
                'median': round(statistics.median(row[metric] for row in values), 3),
                'max': round(max(row[metric] for row in values), 3)}
                for metric in values[0]} for provider, values in rows.items()}
            print(json.dumps({name: report['results'][name]}), flush=True)
        return report
    finally:
        try:
            if sequence:
                calls['direct'].close()
                measure(calls['direct'], '/v1/kv/op', {'op': 'release', **{key: sequence[key]
                    for key in ('sequence_id', 'cache_epoch')}})
        finally:
            for call in calls.values(): call.close()
            old.terminate()
            old.wait(timeout=5)


def measure_models(call):
    call.request('GET', '/v1/models')
    response = call.getresponse()
    data = json.loads(response.read())
    call.close()
    return response.status, data


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--live', action='store_true')
    parser.add_argument('--rounds', type=int, default=8)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    if not args.live: parser.error('--live is required')
    report = main(args.rounds)
    if args.out: args.out.write_text(json.dumps(report, indent=2)+'\n')
