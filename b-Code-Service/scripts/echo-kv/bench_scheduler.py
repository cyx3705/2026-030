"""Explicit live A/B scheduling probe. All paths use the same model/prompt.

Does not load models or write Echo's user database. Run with Echo idle.
"""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import statistics
import time

import httpx


def summary(values):
    values = sorted(values)
    return {'median_ms': round(statistics.median(values), 3),
            'p95_ms': round(values[min(len(values)-1, int(len(values)*.95))], 3),
            'max_ms': round(values[-1], 3)} if values else {}


async def run(rounds, count):
    from echo_app import predict
    prompt = predict.assistant_prompt('chatml', 'You are a helpful assistant. Reply in English.',
        [{'role': 'user', 'content': 'Describe in about 200 words how a student can learn English through reading books. Start directly.'}])
    suffix = predict.sense_suffix('chatml', '', {
        'occurrence_id': 'probe:bank', 'surface': 'bank', 'lemma': 'bank',
        'candidates': [{'label': 'A', 'pos': 'n.', 'meaning': '银行'},
                       {'label': 'B', 'pos': 'n.', 'meaning': '河岸'},
                       {'label': 'C', 'pos': 'v.', 'meaning': '存款'}],
        'allowed_labels': ['0', 'A', 'B', 'C']})
    async with httpx.AsyncClient(timeout=180, trust_env=False) as client:
        direct = 'http://127.0.0.1:18769'
        proxy = 'http://127.0.0.1:11435'
        async def op(base, name, **body):
            response = await client.post(base+'/v1/kv/op', json={'op': name, **body})
            response.raise_for_status()
            return response.json()
        caps = await op(direct, 'runtime')
        if caps['active_sequences'] or caps['active_branches']:
            raise RuntimeError('A/B is already leased; leave the active conversation idle before probing')
        model = (await client.get(direct+'/v1/models')).json()['data'][0]['id']
        async def trial(base, branch=False, stream=False, kv_stream=False):
            times, durations, branches, pending = [], [], [], set()
            text = ''
            started = time.perf_counter()
            if stream:
                async with client.stream('POST', base+'/completion', json={'model': model, 'prompt': prompt,
                        'n_predict': count, 'temperature': 0, 'stream': True, 'cache_prompt': True}) as response:
                    response.raise_for_status()
                    async for line in response.aiter_lines():
                        if line.startswith('data: {'):
                            event = json.loads(line[6:])
                            if event.get('content'):
                                text += event['content']
                                times.append(time.perf_counter())
            else:
                handle = await op(base, 'create', model=model, text=prompt)
                fields = {key: handle[key] for key in ('sequence_id', 'cache_epoch')}
                async def select(prefix):
                    t0 = time.perf_counter()
                    row = (await op(base, 'select_text', **fields, prefix_text=prefix,
                        suffix_text=suffix, allowed_labels=['0', ' A', ' B', ' C']))['results'][0]
                    branches.append({'ms': round((time.perf_counter()-t0)*1000, 3),
                        **{key: row[key] for key in ('suffix_tokens', 'shared_prefix_tokens', 'replayed_tokens')}})
                next_token = None
                try:
                    if kv_stream:
                        async with client.stream('POST', base+'/v1/kv/op', json={
                                'op':'stream', **fields, 'max_tokens': count}) as response:
                            response.raise_for_status()
                            finished = False
                            async for line in response.aiter_lines():
                                if not line.startswith('data: '): continue
                                payload = line[6:]
                                if payload == '[DONE]':
                                    assert finished
                                    break
                                event = json.loads(payload)
                                if event.get('error'): raise RuntimeError(event['error'])
                                if event.get('done'): finished = True
                                if not event.get('delta'): continue
                                times.append(time.perf_counter())
                                text += event['delta']
                                i = len(times)-1
                                if branch and i in (3,11,19,27,35,43,51,59):
                                    pending.add(asyncio.create_task(select(prompt+text)))
                            assert finished, 'truncated KV stream'
                    else:
                        next_token = asyncio.create_task(op(base, 'next', **fields))
                        for i in range(count):
                            t0 = time.perf_counter()
                            event = await next_token
                            next_token = None
                            durations.append((time.perf_counter()-t0)*1000)
                            if event.get('done'): break
                            times.append(time.perf_counter())
                            text += event.get('delta', '')
                            if i+1 < count: next_token = asyncio.create_task(op(base, 'next', **fields))
                            if branch and i in (3, 11, 19, 27, 35, 43, 51, 59):
                                task = asyncio.create_task(select(prompt+text))
                                pending.add(task)
                    output_finished = time.perf_counter()
                    if pending: await asyncio.gather(*pending)
                finally:
                    if next_token:
                        await asyncio.gather(next_token, return_exceptions=True)
                    if pending: await asyncio.gather(*pending, return_exceptions=True)
                    await op(base, 'release', **fields)
            output_finished = times[-1] if times else time.perf_counter()
            gaps = [(b-a)*1000 for a,b in zip(times, times[1:])]
            return {'tokens': len(times), 'output_ms': round((output_finished-started)*1000, 3),
                    'steady_tokens_per_second': round((len(times)-1)/(times[-1]-times[0]), 3) if len(times)>1 else 0,
                    'gaps': summary(gaps), 'first_token_ms': round((times[0]-started)*1000, 3) if times else None,
                    'branch_times': branches, 'text_hash': hashlib.sha256(text.encode()).hexdigest()[:16]}
        variants = [('native_stream', direct, False, True), ('kv_direct', direct, False, False),
                    ('kv_cpp_proxy', proxy, False, False), ('kv_direct_with_b', direct, True, False),
                    ('kv_cpp_proxy_with_b', proxy, True, False),
                    ('kv_stream_direct', direct, False, False, True),
                    ('kv_stream_proxy', proxy, False, False, True),
                    ('kv_stream_direct_with_b', direct, True, False, True),
                    ('kv_stream_proxy_with_b', proxy, True, False, True)]
        report = {'model': model, 'rounds': rounds, 'token_limit': count, 'variants': {name: [] for name,*_ in variants}}
        for turn in range(rounds):
            for name, *options in variants if turn%2==0 else reversed(variants):
                result = await trial(*options)
                report['variants'][name].append(result)
                print(json.dumps({'round': turn+1, 'variant': name, **result}), flush=True)
        report['runtime_after'] = await op(direct, 'runtime')
        return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--live', action='store_true')
    parser.add_argument('--rounds', type=int, default=3)
    parser.add_argument('--tokens', type=int, default=64)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    if not args.live: parser.error('--live is required')
    report = asyncio.run(run(args.rounds, args.tokens))
    if args.out: args.out.write_text(json.dumps(report, indent=2)+'\n')
