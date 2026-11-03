import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import migrate_cache as migration


class CacheUpgradeTests(unittest.TestCase):
    def test_options_change_preserves_snapshot_and_refuses_restore(self):
        with tempfile.TemporaryDirectory() as directory:
            pending=Path(directory)/'pending.json'
            pending.write_text(json.dumps({'facts':{'arguments':['-ctk','q8_0']}}))
            with patch.object(migration,'PENDING',pending), \
                    patch.object(migration,'facts',return_value={'arguments':['-ctk','f16']}), \
                    patch.object(migration.cache,'api') as api:
                with self.assertRaisesRegex(RuntimeError,'changed'):
                    migration.apply()
                api.assert_not_called()
                self.assertTrue(pending.exists())

    def test_occupied_slot_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            pending=Path(directory)/'pending.json'
            pending.write_text(json.dumps({'facts':{}}))
            with patch.object(migration,'PENDING',pending), \
                    patch.object(migration,'facts',return_value={}), \
                    patch.object(migration.cache,'api',return_value=[{'is_processing':False,'n_prompt_tokens':10}]) as api:
                with self.assertRaisesRegex(RuntimeError,'before exposing'):
                    migration.apply()
                self.assertEqual(api.call_count,1)
                self.assertTrue(pending.exists())

    def test_checked_restore_keeps_original_and_registers_new_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            pending=root/'pending.json'
            original=root/'snapshot.bin'
            original.write_bytes(b'original')
            pending.write_text(json.dumps({'facts':{},'filename':original.name,'sha256':'verified','tokens':64}))
            def api(path, body=None, **kw):
                if path == '/slots': return [{'is_processing':False,'n_prompt_tokens':0}]
                if path == '/v1/kv/op': return {'backend':'llama-server-shared-kv'}
                return {'n_restored':64}
            with patch.object(migration,'PENDING',pending), \
                    patch.object(migration,'facts',return_value={}), \
                    patch.object(migration.cache,'ROOT',root), \
                    patch.object(migration.cache,'SLOTS',root), \
                    patch.object(migration.cache,'file_digest',return_value='verified'), \
                    patch.object(migration.cache,'api',side_effect=api), \
                    patch.object(migration.cache,'save') as save:
                migration.apply()
                save.assert_called_once()
                self.assertEqual(original.read_bytes(),b'original')
                self.assertFalse(pending.exists())
