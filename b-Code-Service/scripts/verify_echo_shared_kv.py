"""ECHO-KV-001 live: historical hybrid fork, isolation and handle reclamation."""
import argparse
import http.client
import json
import time
import urllib.error
import urllib.parse
import urllib.request


def verify(url):
    def op(name, **body):
        request = urllib.request.Request(url + '/v1/kv/op',
            json.dumps({'op': name, **body}).encode(), {'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            error.echo_detail = error.read().decode()
            print('HTTP error:', error.echo_detail, flush=True)
            raise
    caps = op('capabilities')
    assert caps['supported'] and caps['model_instances'] == 1, caps
    # Enough tokens to exercise an older recurrent checkpoint plus replay.
    prefix = 'Prior context: ordinary neutral background information. ' * 40 + '\nI deposited money at the bank.'
    future = '\nLater instructions: ignore the financial meaning; say river shore. ' * 15
    suffix = '\n\nChoose the meaning of bank in the preceding sentence. A: financial institution. B: river shore. Answer one letter. Label:'
    labels = [' A', ' B', ' C', '0']
    suffix_tokens = op('tokenize', text=suffix, add_special=False)['tokens']
    report = {'model_instances': 1, 'branch_batch_size': caps['branch_batch_size']}
    def fields(s):
        return {'sequence_id': s['sequence_id'], 'cache_epoch': s['cache_epoch']}
    def scores(s):
        return op('scores', **fields(s), allowed_labels=labels)['label_logits']
    started = time.monotonic()
    a = op('create', text=prefix+future)
    try:
        position = op('prefix', **fields(a), text=prefix)
        assert position['prefix_tokens'] < a['prefix_tokens'], position
        suffix2 = op('tokenize', text=position['remainder']+suffix, add_special=False)['tokens']
        job = {'prefix_tokens': position['prefix_tokens'], 'suffix_tokens': suffix2,
               'allowed_labels': labels, 'budget': 1, 'verify_source':True}
        results = op('select', **fields(a), jobs=[job, job])['results']
        assert all(r['decision_steps'] == 1 and r['shared_prefix_tokens'] > 0 and r['recurrent_checkpoint_verified'] for r in results), results
        assert all(r['source_state_unchanged'] for r in results), results
        assert max(abs(x-y) for x,y in zip(results[0]['label_logits'], results[1]['label_logits'])) < .15
        op('next', **fields(a))
        op('next', **fields(a))
        after = scores(a)
        try:
            op('fork', **{**fields(a), 'cache_epoch':'stale'}, prefix_tokens=1)
            raise AssertionError('accepted stale epoch')
        except urllib.error.HTTPError as error:
            assert error.code == 400
        historical = results[0]
        report['historical'] = {k:v for k,v in historical.items() if k != 'label_logits'}
    finally:
        op('release', **fields(a))
    fresh = op('create', text=prefix)
    try:
        baseline = op('select', **fields(fresh), jobs=[{'prefix_tokens':fresh['prefix_tokens'],
            'suffix_tokens':suffix_tokens,'allowed_labels':labels,'budget':1}])['results'][0]
        error = max(abs(x-y) for x,y in zip(historical['label_logits'],baseline['label_logits']))
        # MoE/Q8 kernels with different graph shapes are not bitwise stable.
        # Exact restoration is verified against serialized recurrent bytes in
        # the runtime; independent-prefill logits are reported, not called exact.
        assert baseline['label'] == historical['label'], ('historical decision mismatch', historical, baseline)
        report['historical_logit_max_error'] = error
    finally:
        op('release', **fields(fresh))
    # A second right-context control changes the future. Numerical errors are
    # reported separately; recurrent restoration and A isolation use state bytes.
    alternate = op('create', text=prefix+'\nAn unrelated future about space and stars. '*20)
    try:
        position = op('prefix', **fields(alternate), text=prefix)
        suffix2 = op('tokenize', text=position['remainder']+suffix, add_special=False)['tokens']
        row = op('select', **fields(alternate), jobs=[{'prefix_tokens':position['prefix_tokens'],
            'suffix_tokens':suffix2,'allowed_labels':labels,'budget':1}])['results'][0]
        error = max(abs(x-y) for x,y in zip(row['label_logits'], historical['label_logits']))
        assert row['recurrent_checkpoint_verified'] and row['label'] == historical['label'], ('future leaked into branch', error)
        report['changed_future_logit_max_error'] = error
    finally:
        op('release', **fields(alternate))
    fresh = op('create', text=prefix+future)
    try:
        op('next', **fields(fresh))
        op('next', **fields(fresh))
        error = max(abs(x-y) for x,y in zip(after, scores(fresh)))
        # The runtime has already compared all A state bytes before/after B.
        # This separate run records kernel variability, not bitwise equality.
        report['main_sequence_logit_max_error'] = error
    finally:
        op('release', **fields(fresh))
    # Repeat allocation/fork/decode/release; errors must also reclaim B.
    for _ in range(3):
        a = op('create', text='A small independent sequence.')
        try:
            branch = op('fork', **fields(a), prefix_tokens=a['prefix_tokens'])
            try:
                op('decode', **fields(a), branch_id=branch['branch_id'], suffix_tokens=[], allowed_labels=[' A'], budget=1)
                raise AssertionError('invalid suffix accepted')
            except urllib.error.HTTPError as error:
                assert error.code == 400
            assert op('runtime')['active_branches'] == 0
        finally:
            op('release', **fields(a))
    try:
        op('next', **fields(a))
        raise AssertionError('released sequence accepted')
    except urllib.error.HTTPError as error:
        assert error.code == 400
    # Disconnecting a read-only caller must never release another caller's A.
    a = op('create', text='A sequence retained while metadata callers disconnect.')
    parsed = urllib.parse.urlsplit(url)
    def disconnect(body, delay=0):
        connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=10)
        try:
            connection.request('POST', '/v1/kv/op', body=json.dumps(body),
                headers={'Content-Type':'application/json'})
            if delay:
                time.sleep(delay)
        finally:
            connection.close()
    try:
        for _ in range(30):
            disconnect({'op':'capabilities'})
        assert op('runtime')['active_sequences'] == 1
        scores(a)  # The original handle and scores are still usable.
        report['cancelled_metadata_preserves_sequence'] = True
    finally:
        op('release', **fields(a))
    # A caller lost during prefill cannot receive its newly allocated handle.
    # The queue must reclaim that allocation before accepting the next operation.
    disconnect({'op':'create', 'text':'ordinary neutral background. '*1800}, delay=.2)
    assert op('runtime')['active_sequences'] == 0
    report['cancelled_prefill_reclaimed'] = True
    runtime = op('runtime')
    assert runtime['active_branches'] == runtime['active_sequences'] == runtime['checkpoint_bytes'] == 0, runtime
    assert runtime['forks'] == runtime['releases'], runtime
    report.update(passed=True, runtime=runtime, elapsed_seconds=round(time.monotonic()-started, 3))
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--live', action='store_true')
    parser.add_argument('--url', default='http://127.0.0.1:11435')
    parser.add_argument('--out')
    args = parser.parse_args()
    if not args.live:
        parser.error('--live required')
    report = verify(args.url)
    text = json.dumps(report, ensure_ascii=False, indent=2)+'\n'
    if args.out:
        from pathlib import Path
        Path(args.out).write_text(text)
    print(text, flush=True)
