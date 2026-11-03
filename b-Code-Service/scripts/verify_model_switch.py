"""ECHO-MODEL-001 live model round trip; restores the original selection."""
import argparse
import json
from pathlib import Path
import time
import urllib.error
import urllib.request


def verify(base, targets, restore_model=None):
    def call(path, body=None):
        request = urllib.request.Request(base+path,None if body is None else json.dumps(body).encode(),
            {'Content-Type':'application/json'})
        for attempt in range(20):
            try:
                with urllib.request.urlopen(request,timeout=180) as response:
                    return json.load(response)
            except urllib.error.HTTPError as error:
                raise RuntimeError(error.read().decode()) from error
            except urllib.error.URLError:
                if body is not None or attempt == 19:
                    raise
                time.sleep(1)
    original = restore_model or call('/v1/echo/models')['current']
    report = {'original':original,'models':[]}
    def select(name):
        started = time.monotonic()
        state = call('/v1/echo/model')
        deadline = started+900
        while state['state'] == 'switching' and state.get('target') != name:
            if time.monotonic()>deadline:
                raise RuntimeError('Previous switch stayed busy')
            time.sleep(1)
            state = call('/v1/echo/model')
        if state['state'] != 'switching':
            state = call('/v1/echo/model',{'name':name})
        last = None
        while state['state'] == 'switching':
            if state['phase'] != last:
                print(json.dumps({'target':name,'phase':state['phase']}),flush=True)
                last = state['phase']
            if time.monotonic()>deadline:
                raise RuntimeError('switch timed out')
            time.sleep(1)
            state = call('/v1/echo/model')
        assert state['state'] == 'ready' and state['current'] == name, state
        assert call('/v1/models')['data'][0]['id'] == name
        return round(time.monotonic()-started,2)
    try:
        for name in targets:
            elapsed = select(name)
            chat = call('/v1/chat/completions',{'model':name,'messages':[{'role':'user','content':'Say hello in English.'}],
                'max_tokens':16,'stream':False,'chat_template_kwargs':{'enable_thinking':False}})
            assert chat['choices'][0]['message'].get('content'), chat
            prediction = call('/completion',{'model':name,'prompt':'I like to learn',
                'n_predict':1,'n_probs':5,'temperature':0,'stream':False})
            assert prediction['completion_probabilities'][0]['top_logprobs'], prediction
            def op(operation, **body):
                return call('/v1/kv/op',{'op':operation,**body})
            native = op('capabilities')['supported']
            if native:
                prefix = 'Ordinary neutral background. '*80+'The bank accepted my money.'
                a = op('create',text=prefix+' Later text about a river. '*20)
                fields = {'sequence_id':a['sequence_id'],'cache_epoch':a['cache_epoch']}
                try:
                    position = op('prefix',**fields,text=prefix)
                    suffix = op('tokenize',text=position['remainder']+' Choose bank meaning. A: financial institution. B: river shore. Label:',add_special=False)['tokens']
                    result = op('select',**fields,jobs=[{'prefix_tokens':position['prefix_tokens'],
                        'suffix_tokens':suffix,'allowed_labels':[' A',' B','0'],'budget':1,'verify_source':True}])['results'][0]
                    assert result['decision_steps'] == 1 and result['source_state_unchanged']
                    assert result['shared_prefix_tokens'] > 0 and result['recurrent_checkpoint_verified']
                finally:
                    op('release',**fields)
                runtime = op('runtime')
                assert runtime['active_sequences'] == runtime['active_branches'] == 0
            else:
                assert name.startswith('gemma4'), 'Unexpected native capability loss'
                schema = {'type':'object','properties':{'label':{'type':'string','enum':['A','B']}},
                    'required':['label'],'additionalProperties':False}
                selection = call('/v1/chat/completions',{'model':name,'messages':[{'role':'user',
                    'content':'She deposited money at the bank. Choose bank meaning: A financial institution; B river shore. Return label JSON.'}],
                    'max_tokens':32,'stream':False,'temperature':0,'chat_template_kwargs':{'enable_thinking':False},
                    'response_format':{'type':'json_schema','json_schema':{'name':'sense','strict':True,'schema':schema}}})
                assert json.loads(selection['choices'][0]['message']['content'])['label'] == 'A'
            row = {'name':name,'switch_seconds':elapsed,'chat':True,'prediction':True,'shared_kv':True,
                'source_state_unchanged':True,'active_sequences':0,'active_branches':0} if native else {
                'name':name,'switch_seconds':elapsed,'chat':True,'prediction':True,'shared_kv':False,'structured_selection':True}
            report['models'].append(row)
            print(json.dumps(row),flush=True)
    finally:
        report['restore_seconds'] = select(original)
        report['restored'] = original
    report['passed'] = True
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--live',action='store_true')
    parser.add_argument('--base-url',default='http://127.0.0.1:11435')
    parser.add_argument('--models',nargs='+',default=['qwen3:8b','gemma4:12b'])
    parser.add_argument('--out',type=Path)
    parser.add_argument('--restore-model',help='Explicit original selection when resuming an interrupted live check')
    args = parser.parse_args()
    if not args.live:
        parser.error('--live required')
    report = verify(args.base_url.rstrip('/'),args.models,args.restore_model)
    if args.out:
        args.out.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report),flush=True)
