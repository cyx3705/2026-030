"""ECHO-MODEL-001: isolated inventory, admission and rollback invariants."""
import fcntl
import json
from pathlib import Path
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from unittest.mock import Mock, patch

import model_switch as switch


class ModelSwitchTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)/'ModuleServicesData'/'service'
        for folder in ('config','status','linux-logs'):
            (self.root/folder).mkdir(parents=True)
        self.models = self.root.parent.parent/'Models/Ollama'
        (self.models/'blobs').mkdir(parents=True)
        self.profiles = [{'name':n,'manifest':n+'/tag','family':'qwen','context':8192,
            'cpu_moe_layers':0,'extra':[]} for n in ('old','new')]
        (self.root/'config/echo-models.json').write_text(json.dumps({'default':'old','models':self.profiles}))
        for index, name in enumerate(('old','new')):
            folder = self.models/'manifests/registry.ollama.ai/library'/name
            folder.mkdir(parents=True)
            digest = str(index)*64
            (folder/'tag').write_text(json.dumps({'layers':[{'mediaType':'application/vnd.ollama.image.model',
                'digest':'sha256:'+digest,'size':8}]}))
            (self.models/'blobs'/('sha256-'+digest)).write_bytes(b'GGUFtest')
        self.controller = switch.Controller(self.root)

    def job(self):
        switch.atomic(self.controller.job_path, {'state':'switching','phase':'waiting',
            'current':'old','target':'new','job_id':'test-job','updated_at':0})

    def test_incomplete_file_and_unknown_name_cannot_launch(self):
        (self.models/'blobs'/('sha256-'+'1'*64)).write_bytes(b'GGUF')
        with patch.object(switch.subprocess,'Popen') as launch:
            self.assertFalse(self.controller.inventory()['models'][1]['supported'])
            for name, status in [('new',422),('../elsewhere',404)]:
                with self.assertRaises(switch.ModelError) as error:
                    self.controller.start(name)
                self.assertEqual(error.exception.status,status)
            launch.assert_not_called()

    def test_switch_lock_prevents_second_worker(self):
        with (self.root/'status/model-switch.lock').open('a') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX)
            with patch.object(switch.subprocess,'Popen') as launch, self.assertRaises(switch.ModelError):
                self.controller.start('new')
            launch.assert_not_called()

    def test_failed_load_restores_previous_selection_and_cache(self):
        self.job()
        events = []
        def launch(name):
            events.append('load:'+name)
            if name == 'new':
                raise RuntimeError('load failed')
        def api(path, *_):
            return [{'is_processing':False}] if path == '/slots' else {'active_sequences':0,'active_branches':0}
        with patch.object(switch,'api',side_effect=api), \
                patch.object(self.controller,'stop_pid',side_effect=lambda filename,*_:events.append('stop:'+filename)), \
                patch.object(self.controller,'cache',side_effect=lambda action:events.append('cache:'+action)), \
                patch.object(self.controller,'launch',side_effect=launch), patch.object(self.controller,'watcher') as watcher:
            self.controller.run('new','test-job')
        self.assertEqual(self.controller.selected(),'old')
        self.assertEqual(self.controller.job()['state'],'failed')
        self.assertIn('已恢复原模型',self.controller.job()['error'])
        self.assertLess(events.index('cache:save'),events.index('stop:fast-path-18769.pid'))
        self.assertLess(events.index('stop:fast-path-18769.pid'),events.index('load:new'))
        self.assertEqual(events[-2:],['load:old','cache:restore'])
        watcher.assert_called_once()

    def test_busy_shared_sequence_aborts_without_stopping_model(self):
        self.job()
        def api(path,*_):
            return [{'is_processing':False}] if path == '/slots' else {'active_sequences':1,'active_branches':0}
        with patch.object(switch,'api',side_effect=api), patch.object(switch.time,'monotonic',side_effect=[0,181]), \
                patch.object(self.controller,'stop_pid') as stop, patch.object(self.controller,'launch') as launch:
            self.controller.run('new','test-job')
        stop.assert_not_called()
        launch.assert_not_called()
        self.assertEqual(self.controller.selected(),'old')
        self.assertEqual(self.controller.job()['state'],'failed')

    def test_success_is_recorded_only_after_load_and_restore(self):
        self.job()
        events = []
        def api(path,*_):
            return [{'is_processing':False}] if path == '/slots' else {'active_sequences':0,'active_branches':0}
        with patch.object(switch,'api',side_effect=api), patch.object(self.controller,'stop_pid'), \
                patch.object(self.controller,'cache',side_effect=lambda action:events.append(action)), \
                patch.object(self.controller,'launch',side_effect=lambda name:events.append(name)), \
                patch.object(self.controller,'watcher'):
            self.controller.run('new','test-job')
        self.assertEqual(events,['save','new','restore'])
        self.assertEqual(self.controller.selected(),'new')
        self.assertEqual(self.controller.job()['state'],'ready')

    def test_busy_second_public_slot_aborts_without_stopping_model(self):
        self.job()
        def api(path, *_):
            return ([{'is_processing': False}, {'is_processing': True}] if path == '/slots'
                    else {'active_sequences': 0, 'active_branches': 0})
        with patch.object(switch, 'api', side_effect=api), \
                patch.object(switch.time, 'monotonic', side_effect=[0, 181]), \
                patch.object(self.controller, 'stop_pid') as stop, patch.object(self.controller, 'launch') as launch:
            self.controller.run('new', 'test-job')
        stop.assert_not_called()
        launch.assert_not_called()
        self.assertEqual(self.controller.job()['state'], 'failed')

    def test_proxy_keeps_control_available_and_blocks_new_inference_during_switch(self):
        import local_ai_cache_proxy as proxy
        controller = Mock()
        controller.switching.return_value = True
        controller.inventory.return_value = {'managed':True,'models':[]}
        controller.start.return_value = {'state':'switching','job_id':'j'}
        server = proxy.Server(('127.0.0.1',0),proxy.Handler)
        server.model_controller = controller
        thread = threading.Thread(target=server.serve_forever,daemon=True)
        thread.start()
        base = 'http://127.0.0.1:'+str(server.server_port)
        align = patch.object(proxy,'align_fork')
        mocked_align = align.start()
        try:
            with urllib.request.urlopen(base+'/v1/echo/models') as response:
                self.assertTrue(json.load(response)['managed'])
            def request(path, body, **headers):
                return urllib.request.Request(base+path,json.dumps(body).encode(),
                    {'Content-Type':'application/json',**headers})
            with urllib.request.urlopen(request('/v1/echo/model',{'name':'new'})) as response:
                self.assertEqual(response.status,202)
            controller.start.assert_called_once_with('new')
            with self.assertRaises(urllib.error.HTTPError) as error:
                urllib.request.urlopen(request('/v1/echo/model',{'name':'new'},Origin='https://elsewhere.example'))
            self.assertEqual(error.exception.code,403)
            with self.assertRaises(urllib.error.HTTPError) as error:
                urllib.request.urlopen(request('/v1/chat/completions',{'messages':[]}))
            self.assertEqual(error.exception.code,503)
            mocked_align.assert_not_called()
            controller.switching.return_value = False
            controller.native_supported.return_value = False
            with urllib.request.urlopen(request('/v1/kv/op',{'op':'capabilities'})) as response:
                self.assertFalse(json.load(response)['supported'])
            with self.assertRaises(urllib.error.HTTPError) as error:
                urllib.request.urlopen(request('/v1/kv/op',{'op':'create','text':'test'}))
            self.assertEqual(error.exception.code,501)
        finally:
            align.stop()
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == '__main__':
    unittest.main()
