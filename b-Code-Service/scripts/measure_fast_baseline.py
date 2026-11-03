"""Direct baseline measurement; never changes or sends an OpenClaw conversation."""
import json
import time
import kv_disk_cache as cache

report = cache.ROOT/'status/fast-baseline.json'
for _ in range(180):
    try:
        cache.api('/health', timeout=2)
        break
    except Exception:
        time.sleep(1)
else:
    raise RuntimeError('Baseline model failed to become ready')
results = []
for index in range(2):
    response = cache.api('/completion', {
        'prompt': '从1数到40，每个数字一行。', 'n_predict': 128,
        'temperature': 0, 'ignore_eos': True, 'cache_prompt': True,
    }, timeout=180)
    item = {'run': index+1, 'timings': response.get('timings')}
    results.append(item)
    cache.atomic_json(report, {'context': cache.api('/props')['default_generation_settings']['n_ctx'],
                              'results': results})
    print(json.dumps(item), flush=True)
