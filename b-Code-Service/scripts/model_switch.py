"""ECHO-MODEL-001: provider-owned, single-model switching with durable jobs.

Only configured local manifests are accepted. The proxy stays up while a
separate worker saves/stops/loads/restores; a failed load rolls back to the
previous model. No downloaded weights or shell commands come from HTTP input.
"""
import argparse
import fcntl
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
import urllib.request
import uuid

from service_root import discover_root

ROOT = discover_root()
UPSTREAM = 'http://127.0.0.1:18769'


def atomic(path, value):
    temporary = path.with_suffix('.tmp-' + uuid.uuid4().hex)
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')
    os.replace(temporary, path)


def api(path, body=None, timeout=5):
    request = urllib.request.Request(UPSTREAM + path,
        None if body is None else json.dumps(body).encode(), {'Content-Type':'application/json'})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


class ModelError(ValueError):
    def __init__(self, message, status=409):
        super().__init__(message)
        self.status = status


class Controller:
    def __init__(self, root=ROOT):
        self.root = Path(root)
        self.job_path = self.root/'status/model-switch.json'
        self.selection = self.root/'status/active-model.json'

    def config(self):
        return json.loads((self.root/'config/echo-models.json').read_text())

    def selected(self):
        try:
            return json.loads(self.selection.read_text())['name']
        except (OSError, ValueError, KeyError):
            return self.config()['default']

    def profile(self, name):
        for item in self.config()['models']:
            if item['name'] == name:
                return item
        raise ModelError('未登记的模型', 404)

    def inspect(self, profile):
        info = {k:profile[k] for k in ('name','family','context')}
        info.update(size=0, installed=False, supported=False)
        try:
            manifest = self.root.parent.parent/'Models/Ollama/manifests/registry.ollama.ai/library'/profile['manifest']
            data = json.loads(manifest.read_text())
            layer = next(v for v in data['layers'] if v['mediaType'] == 'application/vnd.ollama.image.model')
            if not re.fullmatch(r'sha256:[a-f0-9]{64}', layer['digest']):
                raise ValueError('invalid digest')
            blob = self.root.parent.parent/'Models/Ollama/blobs'/layer['digest'].replace(':','-')
            info['size'] = layer['size']
            if not blob.is_file() or blob.stat().st_size != layer['size']:
                info['reason'] = '模型文件未下载完整'
                return info, None
            with blob.open('rb') as source:
                if source.read(4) != b'GGUF':
                    raise ValueError('invalid model format')
            info.update(installed=True, supported=not profile.get('disabled_reason'),
                reason=profile.get('disabled_reason', ''))
            return info, blob
        except (OSError, ValueError, KeyError, StopIteration):
            info['reason'] = '模型文件或清单不可用'
            return info, None

    def job(self):
        try:
            job = json.loads(self.job_path.read_text())
        except (OSError, ValueError):
            return {'state':'ready', 'phase':'ready', 'current':self.selected()}
        if job['state'] == 'switching' and time.time()-job['updated_at'] > 10:
            try:
                cmd = (Path('/proc')/str(job['pid'])/'cmdline').read_bytes()
                alive = b'model_switch.py' in cmd and job['job_id'].encode() in cmd
            except (OSError, KeyError):
                alive = False
            if not alive:
                job.update(state='failed', phase='failed', error='模型切换进程已中断，请重试')
        return job

    def switching(self):
        return self.job()['state'] == 'switching'

    def native_supported(self):
        return self.profile(self.selected()).get('shared_kv',True)

    def inventory(self):
        return {'managed':True, 'current':self.selected(), 'switch':self.job(),
            'models':[self.inspect(p)[0] for p in self.config()['models']]}

    def start(self, name):
        profile = self.profile(name)
        info, _ = self.inspect(profile)
        if not info['supported']:
            raise ModelError(info['reason'], 422)
        descriptor = os.open(self.root/'status/model-switch.lock', os.O_CREAT | os.O_RDWR, 0o600)
        try:
            try:
                fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise ModelError('另一个模型正在切换，请稍后再试')
            if name == self.selected():
                try:
                    if api('/v1/models')['data'][0]['id'] == name:
                        return {'state':'ready','phase':'ready','current':name,'managed':True}
                except (OSError, ValueError, KeyError, IndexError):
                    pass
            job = {'state':'switching','phase':'waiting','current':self.selected(),
                'target':name,'job_id':uuid.uuid4().hex,'updated_at':time.time()}
            atomic(self.job_path, job)
            with (self.root/'linux-logs/model-switch.log').open('ab') as log:
                subprocess.Popen([sys.executable, str(Path(__file__).resolve()), 'switch', name,
                    job['job_id'], '--lock-fd', str(descriptor)], pass_fds=(descriptor,),
                    start_new_session=True, stdin=subprocess.DEVNULL, stdout=log, stderr=log)
            return {**job, 'managed':True}
        except Exception:
            # If process creation failed, do not leave a phantom switching job.
            if 'job' in locals():
                atomic(self.job_path, {**job,'state':'failed','phase':'failed','error':'无法启动模型切换进程'})
            raise
        finally:
            os.close(descriptor)  # The worker inherits the same locked file description.

    def update(self, job, phase, **fields):
        job.update(phase=phase, updated_at=time.time(), pid=os.getpid(), **fields)
        atomic(self.job_path, job)

    def stop_pid(self, filename, expected):
        path = self.root/'status'/filename
        if not path.exists():
            return
        pid = int(path.read_text().strip())
        proc = Path('/proc')/str(pid)
        try:
            command = (proc/'cmdline').read_bytes()
        except FileNotFoundError:
            path.unlink(missing_ok=True)
            return
        if not command:  # Exited child awaiting reaping.
            path.unlink(missing_ok=True)
            return
        if expected.encode() not in command:
            raise RuntimeError('进程身份与服务 PID 记录不一致，未终止该进程')
        os.kill(pid, signal.SIGTERM)
        def alive():
            try:
                return bool((proc/'cmdline').read_bytes())
            except FileNotFoundError:
                return False
        deadline = time.monotonic()+10
        while alive() and time.monotonic()<deadline:
            time.sleep(.1)
        if alive():
            os.kill(pid, signal.SIGKILL)
        deadline = time.monotonic()+5
        while alive():
            if time.monotonic()>deadline:
                raise RuntimeError('旧模型尚未退出，未加载新模型')
            time.sleep(.1)
        path.unlink(missing_ok=True)

    def cache(self, action):
        subprocess.run([sys.executable, str(self.root/'scripts/kv_disk_cache.py'), action],
            check=True, timeout=150)

    def launch(self, name):
        with (self.root/'linux-logs/fast-path-18769.log').open('ab') as log:
            process = subprocess.Popen(['bash', str(self.root/'scripts/fast-path-35b.sh'), name],
                stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
        (self.root/'status/fast-path-18769.pid').write_text(str(process.pid)+'\n')
        deadline = time.monotonic()+240
        while time.monotonic()<deadline:
            if process.poll() is not None:
                raise RuntimeError('模型启动失败，请检查服务模型日志')
            try:
                if api('/v1/models')['data'][0]['id'] == name:
                    return
            except (OSError, ValueError, KeyError, IndexError):
                pass
            time.sleep(1)
        raise RuntimeError('模型加载超时')

    def watcher(self):
        with (self.root/'linux-logs/kv-disk-cache.log').open('ab') as log:
            process = subprocess.Popen([sys.executable,str(self.root/'scripts/kv_disk_cache.py'),'watch'],
                stdin=subprocess.DEVNULL,stdout=log,stderr=log,start_new_session=True)
        (self.root/'status/kv-disk-cache.pid').write_text(str(process.pid)+'\n')

    def run(self, name, job_id):
        job = json.loads(self.job_path.read_text())
        if job.get('job_id') != job_id:
            raise RuntimeError('切换任务已过期')
        previous = self.selected()
        stopped = False
        watcher_stopped = False
        try:
            self.update(job, 'waiting')
            deadline = time.monotonic()+180
            while True:
                public = any(slot['is_processing'] for slot in api('/slots'))
                native = api('/v1/kv/op', {'op':'runtime'})
                if not public and native['active_sequences'] == native['active_branches'] == 0:
                    break
                if time.monotonic() > deadline:
                    raise RuntimeError('现有请求仍在运行，未切换模型，请稍后再试')
                time.sleep(.5)
            self.update(job, 'saving')
            self.stop_pid('kv-disk-cache.pid', str(self.root/'scripts/kv_disk_cache.py'))
            watcher_stopped = True
            self.cache('save')
            self.update(job, 'stopping')
            self.stop_pid('fast-path-18769.pid', str(self.root/'linux-cache/echo-kv-build/build/llama-server'))
            stopped = True
            self.update(job, 'loading')
            self.launch(name)
            self.update(job, 'restoring')
            self.cache('restore')
            atomic(self.selection, {'name':name})
            self.update(job, 'ready', state='ready', current=name)
        except Exception as error:
            message = str(error)
            if stopped:
                try:
                    self.update(job, 'rollback')
                    self.stop_pid('fast-path-18769.pid', str(self.root/'linux-cache/echo-kv-build/build/llama-server'))
                    self.launch(previous)
                    self.cache('restore')
                    atomic(self.selection, {'name':previous})
                    message += '；已恢复原模型'
                except Exception:
                    message += '；原模型恢复失败，请检查服务日志'
            self.update(job, 'failed', state='failed', current=previous, error=message)
        finally:
            if watcher_stopped:
                self.watcher()

    def command(self, name):
        p = self.profile(name)
        info, blob = self.inspect(p)
        if not info['supported']:
            raise ModelError(info['reason'],422)
        context = int(os.environ.get('LOCAL_MODEL_CONTEXT',p['context']))
        moe = int(os.environ.get('LOCAL_MODEL_CPU_MOE_LAYERS',p['cpu_moe_layers']))
        # Keep two provider slots: Echo's A token request can be queued beside
        # a short-lived B sense branch, while the extension reserves its own
        # two sequence IDs.  This is bounded and still one model instance.
        try:
            parallel = int(os.environ.get('LOCAL_MODEL_PARALLEL', '2'))
        except ValueError:
            raise ModelError('LOCAL_MODEL_PARALLEL 必须为 1 或 2', 422)
        if parallel not in (1, 2):
            raise ModelError('LOCAL_MODEL_PARALLEL 必须为 1 或 2', 422)
        # 批量选义的分叉序列数；随后 execv 的 llama-server 从环境读取。
        os.environ['ECHO_KV_BRANCHES'] = str(int(p.get('kv_branches', 1)))
        args = [str(self.root/'linux-cache/echo-kv-build/build/llama-server'), '-m',str(blob),
            '--host','127.0.0.1','--port','18769','--alias',name,'--device','CUDA0',
            '-ngl','all','-ncmoe',str(moe),'--fit','off','--load-mode','none',
            *p['extra'], '-c',str(context),'-np',str(parallel),'--kv-unified','--cache-prompt','--cache-ram','1024',
            '--ctx-checkpoints','8','--checkpoint-min-step','1024','--slot-save-path',
            str(self.root/'prefix-cache/runtime-slots'),'--metrics',
            '-b',str(int(p.get('batch',256))),'-ub',str(int(p.get('ubatch',128))),
            '-ctk',str(p.get('kv_type','q8_0')),'-ctv',str(p.get('kv_type','q8_0')),'-fa','on','-t','8','-tb','16','--jinja']
        if p.get('template'):
            args += ['--chat-template-file',str(self.root/'config'/p['template'])]
        return args + ['--chat-template-kwargs','{"enable_thinking":false}','--reasoning-budget','0',
            '--no-webui','--offline','--no-context-shift','--perf','-lv',str(int(p.get('verbosity',4)))]


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action',choices=['launch','switch','inventory','http-job','http-start'])
    parser.add_argument('name',nargs='?')
    parser.add_argument('job_id',nargs='?')
    parser.add_argument('--lock-fd',type=int)
    args = parser.parse_args()
    controller = Controller()
    if args.action == 'launch':
        command = controller.command(args.name or controller.selected())
        os.execv(command[0],command)
    elif args.action == 'switch':
        if args.lock_fd is None:
            parser.error('switch requires a provider job lock')
        controller.run(args.name,args.job_id)
    elif args.action in ('http-job', 'http-start'):
        # Cold control-plane bridge for the C++ HTTP listener. Never invoked
        # by token generation, prediction, KV operations, or SSE forwarding.
        try:
            result = ({'managed': True, **controller.job()} if args.action == 'http-job'
                      else controller.start(args.name))
            print(json.dumps({'status': 202 if result['state'] == 'switching' and args.action == 'http-start' else 200,
                              'body': result}, ensure_ascii=False))
        except ModelError as error:
            print(json.dumps({'status': error.status, 'body': {'error': {'message': str(error)}}}, ensure_ascii=False))
        except (OSError, ValueError, TypeError):
            print(json.dumps({'status': 503, 'body': {'error': {'message': '模型切换服务不可用'}}}, ensure_ascii=False))
    else:
        print(json.dumps(controller.inventory(),ensure_ascii=False,indent=2))
