"""ECHO-SVC-002: verify a non-streaming disconnect releases its live model task."""
import argparse
import http.client
import json
from pathlib import Path
import time
import urllib.request


def verify(base):
    def slots():
        with urllib.request.urlopen(base+'/slots',timeout=3) as response:
            return json.load(response)
    deadline=time.monotonic()+30
    while any(slot['is_processing'] for slot in slots()):
        if time.monotonic()>=deadline:
            raise RuntimeError('Model stayed busy; no cancellation probe was sent')
        time.sleep(0.25)
    parsed=urllib.parse.urlsplit(base)
    call=http.client.HTTPConnection(parsed.hostname,parsed.port,timeout=5)
    payload={'prompt':'Write the numbers from one to one thousand, with one number per line.',
             'n_predict':256,'ignore_eos':True,'stream':False,'temperature':0,'cache_prompt':True}
    call.request('POST','/completion',body=json.dumps(payload),headers={
        'Content-Type':'application/json','X-Local-AI-Client':'echo'})
    try:
        deadline=time.monotonic()+10
        while True:
            current=next((slot for slot in slots() if slot['is_processing']
                          and slot.get('params',{}).get('n_predict')==256),None)
            if current:
                task=current['id_task']
                break
            if time.monotonic()>=deadline:
                raise RuntimeError('Could not identify the probe task; probe connection closed')
            time.sleep(0.05)
    finally:
        # Intentionally disconnect before getresponse(): no SSE headers needed.
        call.close()
    started=time.monotonic()
    deadline=started+5
    while True:
        if not any(slot['is_processing'] and slot['id_task']==task for slot in slots()):
            return {'passed':True,'cancelled_task_id':task,
                    'release_ms':round((time.monotonic()-started)*1000),
                    'non_streaming':True,'cancelled_before_headers':True}
        if time.monotonic()>=deadline:
            raise RuntimeError('Probe task did not release within five seconds')
        time.sleep(0.05)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--live',action='store_true')
    parser.add_argument('--base-url',default='http://127.0.0.1:11435')
    parser.add_argument('--out',type=Path)
    args=parser.parse_args()
    if not args.live:
        parser.error('--live is required')
    report=verify(args.base_url.rstrip('/'))
    if args.out:
        args.out.write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report))
