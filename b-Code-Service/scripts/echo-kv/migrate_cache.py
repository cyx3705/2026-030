"""One explicit server upgrade: preserve slot zero across a server-only build.

Does not relax ordinary snapshot compatibility. Prepare after draining callers;
apply before exposing the new proxy. Model, context options and shared runtime
libraries must be unchanged. The original snapshot remains intact.
"""
import argparse
import fcntl
import json
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import kv_disk_cache as cache

PENDING = cache.ROOT/'status/echo-kv-cache-upgrade.json'


def facts():
    props = cache.api('/props')
    model = Path(props['model_path'])
    lib = cache.ROOT/'linux-ollama/lib/ollama'
    pid = cache.runtime()[1].rsplit(':',2)[1]
    arguments = (Path('/proc')/pid/'cmdline').read_bytes().decode().strip('\0').split('\0')[1:]
    return {'model': [str(model), model.stat().st_size, model.stat().st_mtime_ns],
        'arguments':arguments,
        'context':props['default_generation_settings']['n_ctx'], 'template':props['chat_template'],
        'libraries':sorted((p.name,p.stat().st_size,p.stat().st_mtime_ns)
            for p in lib.glob('*.so*') if p.is_file())}


def prepare():
    deadline = time.monotonic()+30
    while cache.api('/slots')[0]['is_processing']:
        if time.monotonic() >= deadline:
            raise RuntimeError('Model still busy; snapshot upgrade not prepared')
        time.sleep(.5)
    cache.save()
    incarnation = cache.runtime()[1]
    snapshots = [s for s in cache.load_manifest()['snapshots'] if s['incarnation'] == incarnation]
    if not snapshots:
        raise RuntimeError('No completed snapshot from current model process')
    source = snapshots[-1]
    data = {'facts':facts(), 'filename':source['filename'], 'tokens':source['tokens'], 'sha256':source['sha256']}
    cache.atomic_json(PENDING, data)
    print(json.dumps({'prepared':source['filename'], 'tokens':source['tokens']}),flush=True)


def apply():
    if not PENDING.exists():
        return
    data = json.loads(PENDING.read_text())
    # JSON turns tuples into lists; canonicalize both sides for comparison.
    if json.dumps(facts(),sort_keys=True) != json.dumps(data['facts'],sort_keys=True):
        raise RuntimeError('Model/options/libraries changed; original snapshot preserved')
    slot = cache.api('/slots')[0]
    if slot['is_processing'] or slot.get('n_prompt_tokens',0):
        raise RuntimeError('Apply cache upgrade before exposing client proxy')
    name = data['filename']
    if Path(name).name != name or cache.file_digest(cache.SLOTS/name) != data['sha256']:
        raise RuntimeError('Snapshot identity mismatch')
    native = cache.api('/v1/kv/op', {'op':'capabilities'})
    if native.get('backend') != 'llama-server-shared-kv':
        raise RuntimeError('Expected shared KV server extension')
    result = cache.api('/slots/0?action=restore', {'filename':name}, timeout=120)
    if result['n_restored'] != data['tokens']:
        raise RuntimeError('Snapshot token count mismatch')
    cache.save()
    cache.atomic_json(cache.ROOT/'status/echo-kv-cache-upgraded.json', {
        'restored':name,'tokens':data['tokens'],'result':result,'original_preserved':True})
    PENDING.unlink()


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action',choices=['prepare','apply'])
    args=parser.parse_args()
    with (cache.SLOTS/'operation.lock').open('a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        (prepare if args.action == 'prepare' else apply)()
