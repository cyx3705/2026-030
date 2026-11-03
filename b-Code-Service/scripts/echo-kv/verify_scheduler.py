"""ECHO-KV-004 live stream limits, EOS, cancel and snapshot admission checks."""
import argparse
import asyncio
import json
from pathlib import Path
import httpx

async def verify():
    from echo_app import predict
    url='http://127.0.0.1:11435'
    async with httpx.AsyncClient(timeout=180,trust_env=False) as client:
        async def op(name, **body):
            r=await client.post(url+'/v1/kv/op',json={'op':name,**body})
            r.raise_for_status()
            return r.json()
        initial=await op('runtime')
        assert initial['generation_streaming'] and initial['active_sequences']==0
        prompt=predict.assistant_prompt('chatml','Reply in English.',[{'role':'user','content':'Describe a library in 100 words.'}])
        def fields(a): return {k:a[k] for k in ('sequence_id','cache_epoch')}
        async def stream(a,limit, cancel=False):
            rows=[]; marker=False
            async with client.stream('POST',url+'/v1/kv/op',json={'op':'stream',**fields(a),'max_tokens':limit}) as r:
                r.raise_for_status()
                async for line in r.aiter_lines():
                    if not line.startswith('data: '): continue
                    if line[6:]=='[DONE]': marker=True; break
                    row=json.loads(line[6:]); assert not row.get('error'),row
                    rows.append(row)
                    if cancel and row.get('delta'): break
            return rows,marker
        a=await op('create',text=prompt)
        try:
            baseline=[(await op('next',**fields(a)))['delta'] for _ in range(10)]
        finally: await op('release',**fields(a))
        a=await op('create',text=prompt)
        try:
            for action in ('save','restore'):
                r=await client.post('http://127.0.0.1:18769/slots/0?action='+action,json={'filename':'echo-busy-admission-probe.bin'})
                assert r.status_code==400 and 'model_busy_for_snapshot' in r.text,(r.status_code,r.text)
            rows,marker=await stream(a,9)
            assert marker and rows[-1]['done']
            assert ''.join(x.get('delta','') for x in rows)==''.join(baseline[:9])
            assert rows[-1].get('sequence', rows[-1])['prefix_tokens']==a['prefix_tokens']+9
            assert (await op('next',**fields(a)))['delta']==baseline[9], 'stream generated an extra token'
        finally: await op('release',**fields(a))
        a=await op('create',text=prompt)
        try:
            rows,marker=await stream(a,1024,cancel=True)
            assert not marker
            # Closing a stream ends its production; sequence ownership remains
            # with the caller, which can still select before explicit release.
            await asyncio.sleep(.15)
            first=(await op('next',**fields(a)))['sequence']['prefix_tokens']
            await asyncio.sleep(.15)
            second=(await op('next',**fields(a)))['sequence']['prefix_tokens']
            assert second==first+1,(first,second)
            suffix=' neutral background'*850+'\nPick A or B. Label:'
            b=asyncio.create_task(op('select_text',**fields(a),prefix_text=prompt,
                suffix_text=suffix,allowed_labels=[' A',' B']))
            saw_branch=False
            for _ in range(30):
                state=await op('runtime')
                if state['active_branches']:
                    saw_branch=True; b.cancel(); break
                if b.done(): break
            assert saw_branch, 'B completed before cancellation could be exercised'
            await asyncio.gather(b,return_exceptions=True)
            for _ in range(30):
                state=await op('runtime')
                if not state['active_branches']: break
                await asyncio.sleep(.02)
            assert state['active_branches']==0,state
            await op('next',**fields(a))
        finally: await op('release',**fields(a))
        short=predict.assistant_prompt('chatml','Answer briefly.',[{'role':'user','content':'Reply with only OK.'}])
        a=await op('create',text=short)
        try:
            rows,marker=await stream(a,128)
            assert marker and rows[-1]['done'] and len(rows)<128
        finally: await op('release',**fields(a))
        final=await op('runtime')
        assert final['active_sequences']==final['active_branches']==final['checkpoint_bytes']==0
        assert final['forks']==final['releases'],final
        return {'checks':['exact_limit_no_extra_token','snapshot_save_restore_deferred','stream_disconnect_stops_production',
            'cancelled_resumable_b_released','eos_marker','zero_leases'], 'runtime_after':final}

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--live',action='store_true');p.add_argument('--out',type=Path)
    args=p.parse_args()
    if not args.live:p.error('--live required')
    report=asyncio.run(verify())
    if args.out:args.out.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report))

