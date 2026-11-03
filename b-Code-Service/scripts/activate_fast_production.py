"""Restore the main KV and verify 256k operation before releasing OpenClaw."""
import json
import os
from pathlib import Path
import signal
import struct
import subprocess
import time
import kv_disk_cache as cache

ROOT = cache.ROOT
REPORT = ROOT/'status/fast-production.json'
SOURCE = 'kv-1793125777655139481-f4789f0c.bin'

def phase(name, **data):
    cache.atomic_json(REPORT, {'phase': name, **data})
    print(json.dumps({'phase': name, **data}), flush=True)

def stop(pid):
    os.kill(pid, signal.SIGTERM)
    for _ in range(600):
        path = Path('/proc')/str(pid)/'stat'
        if not path.exists() or path.read_text().split()[2] == 'Z':
            return
        time.sleep(0.2)
    raise RuntimeError('Model did not finish shutdown')

def main():
    proxy = int((ROOT/'status/cache-proxy.pid').read_text())
    assert b'local_ai_cache_proxy.py' in (Path('/proc')/str(proxy)/'cmdline').read_bytes()
    assert 'T (stopped)' in (Path('/proc')/str(proxy)/'status').read_text()
    baseline = json.loads((ROOT/'status/fast-baseline.json').read_text())
    assert baseline['context'] == 8192 and len(baseline['results']) == 2
    assert not cache.api('/slots')[0]['is_processing']
    source = next(s for s in cache.load_manifest()['snapshots'] if s['filename'] == SOURCE)
    assert cache.file_digest(cache.SLOTS/SOURCE) == source['sha256']
    with (cache.SLOTS/SOURCE).open('rb') as stream:
        magic, version, count = struct.unpack('<III', stream.read(12))
        packed = struct.unpack('<'+str(count)+'i', stream.read(count*4))
    assert version == 4 and packed[:3] == (-1, 1, source['tokens'])
    tokens = list(packed[3:3+source['tokens']])
    phase('loading_256k', preserved_tokens=source['tokens'], cpu_moe_layers=30)
    old_pid = int((ROOT/'status/fast-path-18769.pid').read_text())
    assert b'llama-server' in (Path('/proc')/str(old_pid)/'cmdline').read_bytes()
    stop(old_pid)
    env = os.environ.copy()
    env.update(LOCAL_MODEL_CONTEXT='262144', LOCAL_MODEL_CPU_MOE_LAYERS='30')
    with (ROOT/'linux-logs/fast-production-activation.log').open('ab') as log:
        model = subprocess.Popen(['bash', str(ROOT/'scripts/fast-path-35b.sh')], env=env,
                                 stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
    (ROOT/'status/fast-path-18769.pid').write_text(str(model.pid))
    for _ in range(240):
        if model.poll() is not None:
            raise RuntimeError('Production model exited during loading')
        try:
            cache.api('/health', timeout=2)
            break
        except Exception:
            time.sleep(1)
    else:
        raise RuntimeError('Production model loading timed out')
    assert cache.runtime(details=True)['context_capacity'] == 262144
    phase('restoring_main_kv', model_pid=model.pid)
    restored = cache.api('/slots/0?action=restore', {'filename': SOURCE}, timeout=180)
    assert restored['n_restored'] == source['tokens']
    phase('measuring_long_prefix', restored_tokens=source['tokens'])
    response = cache.api('/completion', {'prompt': tokens, 'n_predict': 128, 'ignore_eos': True,
                                        'cache_prompt': True, 'temperature': 0}, timeout=300)
    timings = response['timings']
    phase('restoring_after_measurement', timings=timings)
    restored = cache.api('/slots/0?action=restore', {'filename': SOURCE}, timeout=180)
    assert restored['n_restored'] == source['tokens']
    # Save under the verified production fingerprint before enabling clients.
    assert cache.save(wait=True)
    saved = json.loads(cache.STATUS.read_text())
    assert saved['tokens'] == source['tokens']
    with (ROOT/'linux-logs/kv-disk-cache.log').open('ab') as log:
        watcher = subprocess.Popen(['python3', str(ROOT/'scripts/kv_disk_cache.py'), 'watch'],
                                   stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
    (ROOT/'status/kv-disk-cache.pid').write_text(str(watcher.pid))
    os.kill(proxy, signal.SIGCONT)
    phase('complete', model_pid=model.pid, context=262144, cpu_moe_layers=30,
          baseline=baseline, long_prefix_timings=timings, restored_tokens=source['tokens'],
          snapshot=saved['snapshot'], proxy_resumed=True, watcher_pid=watcher.pid)

if __name__ == '__main__':
    try:
        main()
    except BaseException as exc:
        phase('error', error=str(exc), proxy_resumed=False)
        raise
