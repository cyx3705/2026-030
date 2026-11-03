"""State-safety tests for slot persistence; never contact the real model."""
import json
import io
import urllib.error
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

import kv_disk_cache as cache


class DiskCacheTests(unittest.TestCase):
    def test_request_alignment_never_waits_for_a_busy_snapshot_lock(self):
        with patch.object(cache.fcntl,'flock',side_effect=BlockingIOError), \
                patch.object(cache,'restore') as restore:
            self.assertFalse(cache.align_request([1,2,3]))
            restore.assert_not_called()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        self.patches = [patch.object(cache, name, value) for name, value in {
            "CACHE": root, "SLOTS": root / "runtime-slots",
            "MANIFEST": root / "runtime-slots/manifest.json", "STATUS": root / "status.json",
            "runtime": self.runtime,
            "api": self.api,
            "policy": lambda: {"save_interval_seconds": 60, "poll_seconds": 2,
                               "retain_latest": 2,
                               "tiers": [{"max_age_seconds": 3600, "bucket_seconds": 300},
                                         {"max_age_seconds": 86400, "bucket_seconds": 3600},
                                         {"max_age_seconds": 604800, "bucket_seconds": 86400}]},
        }.items()]
        self.incarnation = "model-process-1"
        self.fingerprint = "same-model"
        self.capacity_fingerprint = "same-weights-rope-and-kv-types"
        self.context_capacity = 372736
        self.slot = {"is_processing": False, "id_task": 10, "n_prompt_tokens": 1200}
        self.calls = []
        self.save_error = False
        self.native_busy = False
        self.second_slot_busy = False
        for item in self.patches:
            item.start()
            self.addCleanup(item.stop)
        cache.SLOTS.mkdir()

    def runtime(self, details=False):
        if details:
            return {"fingerprint": self.fingerprint, "incarnation": self.incarnation,
                    "capacity_fingerprint": self.capacity_fingerprint,
                    "context_capacity": self.context_capacity}
        return self.fingerprint, self.incarnation

    def api(self, path, body=None, timeout=30):
        self.calls.append(path)
        if path == "/slots":
            return [dict(self.slot), {'is_processing': self.second_slot_busy}]
        if path == '/v1/kv/op':
            return {'active_sequences': int(self.native_busy), 'active_branches': 0}
        if path.endswith("action=save"):
            payload = struct.pack("<III", 0x67677371, 4, 1204) + b"\0" * (1204 * 4 + 128)
            (cache.SLOTS / body["filename"]).write_bytes(payload)
            if self.save_error:
                raise OSError("simulated incomplete write")
            self.slot["is_processing"] = False
            return {"n_saved": 1200, "n_written": len(payload)}
        if path.endswith("action=restore"):
            self.slot["n_prompt_tokens"] = 1200
            return {"n_restored": 1200, "n_read": 1500}
        self.fail("Unexpected API call: " + path)

    def test_durable_snapshot_restores_only_into_a_new_empty_process(self):
        self.assertTrue(cache.save())
        item = cache.load_manifest()["snapshots"][0]
        self.assertEqual(cache.file_digest(cache.SLOTS / item["filename"]), item["sha256"])
        self.assertFalse(cache.restore())  # a live context must never be replaced
        self.slot["n_prompt_tokens"] = 0
        self.assertFalse(cache.restore())  # same process owns the original state
        self.incarnation = "model-process-2"
        self.assertTrue(cache.restore())
        self.assertEqual(self.slot["n_prompt_tokens"], 1200)
        self.assertFalse(cache.restore())

    def test_busy_requests_are_not_interrupted_and_duplicate_saves_are_skipped(self):
        self.slot["is_processing"] = True
        self.assertFalse(cache.save())
        self.assertFalse(cache.restore())
        self.assertNotIn("/slots/0?action=save", self.calls)
        self.slot["is_processing"] = False
        self.assertTrue(cache.save())
        self.assertFalse(cache.save())

    def test_shared_a_and_busy_second_public_slot_block_snapshot_io(self):
        for field in ('native_busy', 'second_slot_busy'):
            with self.subTest(field=field):
                setattr(self, field, True)
                self.assertFalse(cache.save())
                self.assertFalse(cache.restore())
                self.assertNotIn('/slots/0?action=save', self.calls)
                setattr(self, field, False)

    def test_snapshot_queue_race_is_deferred_without_damaging_existing_copy(self):
        self.assertTrue(cache.save())
        before = cache.MANIFEST.read_bytes()
        self.slot['id_task'] += 1
        original_api = self.api
        def busy(path, body=None, timeout=30):
            if 'action=' in path:
                raise urllib.error.HTTPError(path, 400, 'busy', {},
                    io.BytesIO(b'{"error":{"message":"model_busy_for_snapshot"}}'))
            return original_api(path, body, timeout)
        with patch.object(cache, 'api', busy):
            self.assertFalse(cache.save())
            self.incarnation = 'model-process-2'
            self.slot['n_prompt_tokens'] = 0
            self.assertFalse(cache.restore())
        self.assertEqual(cache.MANIFEST.read_bytes(), before)
        self.assertEqual(json.loads(cache.STATUS.read_text())['state'], 'deferred_busy')
        self.assertFalse(list(cache.SLOTS.glob('*.partial')))

    def test_partial_failure_never_replaces_last_good_snapshot(self):
        cache.save()
        before = cache.MANIFEST.read_bytes()
        self.slot["id_task"] += 1
        self.save_error = True
        with self.assertRaises(OSError):
            cache.save()
        self.assertEqual(cache.MANIFEST.read_bytes(), before)
        self.assertTrue((cache.SLOTS / cache.load_manifest()["snapshots"][0]["filename"]).exists())

    def test_corrupt_and_incompatible_snapshots_are_not_restored(self):
        cache.save()
        item = cache.load_manifest()["snapshots"][0]
        self.incarnation = "model-process-2"
        self.slot["n_prompt_tokens"] = 0
        path = cache.SLOTS / item["filename"]
        damaged = bytearray(path.read_bytes())
        damaged[-1] ^= 1
        path.write_bytes(damaged)
        self.assertFalse(cache.restore())
        self.assertNotIn("/slots/0?action=restore", self.calls)
        manifest = cache.load_manifest()
        manifest["snapshots"][0]["fingerprint"] = "different-model"
        manifest["snapshots"][0]["capacity_fingerprint"] = "different-model"
        cache.atomic_json(cache.MANIFEST, manifest)
        cache.atomic_json(cache.SLOTS / (item["filename"] + ".json"), manifest["snapshots"][0])
        self.assertFalse(cache.restore())

    def test_quota_cannot_delete_protected_last_good_snapshot(self):
        cache.save()
        manifest = cache.load_manifest()
        item = manifest["snapshots"][0]
        with patch.object(cache, "LIMIT", 10):
            with self.assertRaises(RuntimeError):
                cache.prune(manifest, incoming=1, keep=item["filename"])
        self.assertTrue((cache.SLOTS / item["filename"]).is_file())

    def test_timed_save_waits_until_idle_and_throttles_rewrites(self):
        cache.save()
        saved = cache.load_manifest()["snapshots"][-1]["saved_at"]
        self.slot.update(id_task=11, is_processing=True)
        before = self.calls.count("/slots/0?action=save")
        with patch.object(cache.time, "time", return_value=saved + 61):
            self.assertFalse(cache.save(wait=False, scheduled=True))
        self.assertEqual(self.calls.count("/slots/0?action=save"), before)

        def release_slot(_seconds):
            self.slot["is_processing"] = False

        with patch.object(cache.time, "time", return_value=saved + 61), \
                patch.object(cache.time, "sleep", release_slot):
            self.assertTrue(cache.save(wait=True, scheduled=True))
        self.assertEqual(self.calls.count("/slots/0?action=save"), before + 1)
        self.assertFalse(self.slot["is_processing"])
        with patch.object(cache.time, "time", return_value=saved + 90):
            self.assertFalse(cache.save(wait=True, scheduled=True))
        self.assertEqual(self.calls.count("/slots/0?action=save"), before + 1)
        self.assertEqual(len(cache.load_manifest()["snapshots"]), 2)

    def test_power_loss_index_recovery_ignores_incomplete_files(self):
        cache.save()
        snapshot = cache.load_manifest()["snapshots"][0]
        cache.MANIFEST.write_text(json.dumps({"version": 1, "snapshots": []}))
        self.assertEqual(cache.load_manifest()["snapshots"][0]["filename"], snapshot["filename"])
        cache.MANIFEST.write_text("{interrupted index")
        (cache.SLOTS / "kv-incomplete.bin.partial").write_bytes(b"partial")
        recovered = cache.load_manifest()["snapshots"]
        self.assertEqual([s["filename"] for s in recovered], [snapshot["filename"]])
        self.incarnation = "model-process-2"
        self.slot["n_prompt_tokens"] = 0
        self.assertTrue(cache.restore())

    def test_retention_keeps_latest_and_distinct_time_buckets(self):
        timestamp = 1_000_000
        entries = []
        for index, age in enumerate([0, 10, 20, 400, 800, 7200, 604801, 604802]):
            name = f"kv-tier-{index}.bin"
            (cache.SLOTS / name).write_bytes(b"state")
            entries.append({"filename": name, "fingerprint": "same-model",
                            "saved_at": timestamp - age})
        manifest = {"version": 1, "snapshots": entries}
        cache.retain_tiers(manifest, cache.policy(), timestamp)
        retained = {s["filename"] for s in manifest["snapshots"]}
        self.assertIn("kv-tier-0.bin", retained)
        self.assertIn("kv-tier-1.bin", retained)
        self.assertNotIn("kv-tier-2.bin", retained)
        self.assertIn("kv-tier-3.bin", retained)
        self.assertIn("kv-tier-4.bin", retained)
        self.assertIn("kv-tier-5.bin", retained)
        self.assertNotIn("kv-tier-6.bin", retained)
        self.assertNotIn("kv-tier-7.bin", retained)

    def test_invalid_runtime_file_cannot_be_published(self):
        original = self.api
        def bad_api(path, body=None, timeout=30):
            result = original(path, body, timeout)
            if path.endswith("action=save"):
                file = cache.SLOTS / body["filename"]
                file.write_bytes(b"\0" * result["n_written"])
            return result
        with patch.object(cache, "api", bad_api):
            with self.assertRaises(ValueError):
                cache.save()
        self.assertEqual(cache.load_manifest()["snapshots"], [])
        self.assertEqual(list(cache.SLOTS.glob("*.partial")), [])

    def test_context_resize_restores_fitting_state_but_rejects_oversized_state(self):
        cache.save()
        self.incarnation = "model-process-2"
        self.fingerprint = "same-model-smaller-capacity"
        self.context_capacity = 1024
        self.slot["n_prompt_tokens"] = 0
        self.assertFalse(cache.restore())
        self.context_capacity = 262144
        self.assertTrue(cache.restore())

    def test_divergent_fork_stays_when_a_newer_branch_would_otherwise_shadow_it(self):
        timestamp = 1_000_000
        specs = [
            ("kv-old-long.bin", list(range(80)), timestamp - 700001),
            ("kv-old-short.bin", list(range(50)), timestamp - 700000),
            ("kv-other-short.bin", [9] * 5, timestamp - 800000),
            ("kv-other-tip.bin", [9] * 10, timestamp),
        ]
        entries = []
        for name, tokens, saved_at in specs:
            (cache.SLOTS / name).write_bytes(b"state")
            cache.atomic_json(cache.SLOTS / (name + ".json"), {
                "filename": name, "token_prefix": tokens, "saved_at": saved_at,
            })
            entries.append({"filename": name, "fingerprint": "same-model", "saved_at": saved_at})
        manifest = {"version": 1, "snapshots": entries}
        cache.retain_tiers(manifest, cache.policy(), timestamp)
        retained = {item["filename"] for item in manifest["snapshots"]}
        self.assertIn("kv-old-long.bin", retained)
        self.assertIn("kv-other-tip.bin", retained)
        self.assertNotIn("kv-other-short.bin", retained)

    def test_longest_prefix_wins_and_a_live_continuation_is_not_replaced(self):
        long_tokens = list(range(2000))
        short_tokens = [9] * 256
        snapshots = [
            {"filename": "long.bin", "tokens": 2000, "saved_at": 1},
            {"filename": "short.bin", "tokens": 256, "saved_at": 9},
        ]
        prefixes = {"long.bin": long_tokens, "short.bin": short_tokens}
        request = long_tokens + [7]
        chosen = cache.select_fork(snapshots, prefixes, request, short_tokens, 256)
        self.assertEqual(chosen["filename"], "long.bin")
        self.assertIsNone(cache.select_fork(snapshots, prefixes, request, long_tokens, 2500))
        self.assertEqual(
            cache.select_fork(snapshots, prefixes, None, [], 0)["filename"], "long.bin")

    def test_empty_slot_restores_the_longest_fork_not_the_newest_fragment(self):
        self.slot["n_prompt_tokens"] = 0
        self.incarnation = "model-process-2"
        names = []

        def publish(name, tokens, saved_at):
            path = cache.SLOTS / name
            path.write_bytes(b"slot-" + name.encode())
            item = {
                "filename": name, "fingerprint": self.fingerprint,
                "incarnation": "model-process-1", "tokens": tokens,
                "bytes": path.stat().st_size, "saved_at": saved_at, "hits": 0,
                "sha256": cache.file_digest(path),
                "capacity_fingerprint": self.capacity_fingerprint,
                "context_capacity": self.context_capacity,
            }
            cache.atomic_json(cache.SLOTS / (name + ".json"), item)
            names.append(item)

        publish("kv-long.bin", 53147, 10)
        publish("kv-fragment.bin", 256, 99)
        cache.commit_manifest({"version": 1, "snapshots": names})

        def api(path, body=None, timeout=30):
            self.calls.append(None if body is None else body.get("filename"))
            if path == "/slots":
                return [dict(self.slot)]
            if path.endswith("action=restore"):
                item = next(entry for entry in names if entry["filename"] == body["filename"])
                self.slot["n_prompt_tokens"] = item["tokens"]
                return {"n_restored": item["tokens"], "n_read": item["bytes"]}
            if path == "/v1/kv/op":
                return {"active_sequences": 0, "active_branches": 0}
            self.fail(path)

        with patch.object(cache, "api", api):
            self.assertTrue(cache.restore())
        self.assertEqual(self.calls[-1], "kv-long.bin")
        self.assertEqual(self.slot["n_prompt_tokens"], 53147)

    def test_align_switches_only_to_a_much_better_idle_fork(self):
        long_tokens = list(range(2000))
        short_tokens = [9] * 256
        entries = []
        for name, tokens, saved_at in (
                ("kv-long.bin", long_tokens, 10), ("kv-short.bin", short_tokens, 99)):
            path = cache.SLOTS / name
            path.write_bytes(b"slot-" + name.encode())
            item = {
                "filename": name, "fingerprint": self.fingerprint,
                "incarnation": self.incarnation, "tokens": len(tokens),
                "bytes": path.stat().st_size, "saved_at": saved_at, "hits": 0,
                "sha256": cache.file_digest(path), "token_prefix": tokens,
                "capacity_fingerprint": self.capacity_fingerprint,
                "context_capacity": self.context_capacity,
            }
            cache.atomic_json(cache.SLOTS / (name + ".json"), item)
            stored = dict(item)
            stored.pop("token_prefix")
            entries.append(stored)
        cache.commit_manifest({"version": 1, "snapshots": entries})
        cache.atomic_json(cache.STATUS, {
            "state": "restored", "resident_snapshot": "kv-short.bin", "snapshot": "kv-short.bin",
        })
        restored = []

        def api(path, body=None, timeout=30):
            if path == "/slots":
                return [dict(self.slot)]
            if path.endswith("action=restore"):
                restored.append(body["filename"])
                item = next(entry for entry in entries if entry["filename"] == body["filename"])
                self.slot["n_prompt_tokens"] = item["tokens"]
                return {"n_restored": item["tokens"], "n_read": item["bytes"]}
            if path == "/v1/kv/op":
                return {"active_sequences": 0, "active_branches": 0}
            self.fail(path)

        self.slot.update(is_processing=True, n_prompt_tokens=256)
        with patch.object(cache, "api", api):
            self.assertFalse(cache.align_request(long_tokens + [7]))
            self.assertEqual(restored, [])
            self.slot["is_processing"] = False
            self.assertTrue(cache.align_request(long_tokens + [7]))
            self.assertEqual(restored, ["kv-long.bin"])
            cache.atomic_json(cache.STATUS, {
                "state": "restored", "resident_snapshot": "kv-long.bin", "snapshot": "kv-long.bin",
                "slot_holds_resident": True,
            })
            self.slot["n_prompt_tokens"] = 2500
            restored.clear()
            self.assertFalse(cache.align_request(long_tokens + [7]))
            self.assertEqual(restored, [])
            cache.atomic_json(cache.STATUS, {
                "state": "saved", "resident_snapshot": "kv-long.bin", "snapshot": "kv-long.bin",
                "slot_holds_resident": False,
            })
            self.slot["n_prompt_tokens"] = 6000
            self.assertTrue(cache.align_request(long_tokens + [7]))
            self.assertEqual(restored, ["kv-long.bin"])

    def test_wrapped_slot_blob_compares_the_real_prompt_tokens(self):
        def publish(name, tokens):
            words = [-1, 1, len(tokens), *tokens]
            payload = struct.pack("<III", 0x67677371, 4, len(words)) + struct.pack(
                f"<{len(words)}i", *words)
            (cache.SLOTS / name).write_bytes(payload)
            cache.atomic_json(cache.SLOTS / (name + ".json"), {
                "filename": name, "token_prefix": [-1, 1, len(tokens)], "saved_at": 1,
            })
            return cache.read_token_prefix(cache.SLOTS / name)

        long_tokens = list(range(1, 2500))
        self.assertEqual(publish("kv-wrapped-long.bin", long_tokens), long_tokens)
        self.assertEqual(publish("kv-wrapped-short.bin", long_tokens[:1200])[:1200], long_tokens[:1200])
        publish("kv-wrapped-other.bin", [8] * 1300)
        manifest = {"version": 1, "snapshots": [
            {"filename": "kv-wrapped-long.bin", "saved_at": 1},
            {"filename": "kv-wrapped-short.bin", "saved_at": 2},
            {"filename": "kv-wrapped-other.bin", "saved_at": 3},
        ]}
        prefixes = {item["filename"]: cache.load_token_prefix(item) for item in manifest["snapshots"]}
        self.assertEqual(cache.common_prefix_len(
            prefixes["kv-wrapped-long.bin"], prefixes["kv-wrapped-short.bin"]), 1200)
        tips = cache.fork_tips(manifest["snapshots"], prefixes)
        self.assertIn("kv-wrapped-long.bin", tips)
        self.assertIn("kv-wrapped-other.bin", tips)
        self.assertNotIn("kv-wrapped-short.bin", tips)

    def test_short_cache_hit_means_the_slot_left_the_resident_fork(self):
        cache.atomic_json(cache.STATUS, {"state": "saved", "tokens": 5000, "slot_holds_resident": True})
        cache.note_slot_outcome(400, 5200)
        self.assertFalse(json.loads(cache.STATUS.read_text())["slot_holds_resident"])
        cache.note_slot_outcome(5000, 5300)
        self.assertTrue(json.loads(cache.STATUS.read_text())["slot_holds_resident"])

    def test_capacity_compatibility_keeps_rope_and_cache_type_in_identity(self):
        original = {"n_ctx": 372736, "argv": ["llama-server", "-c", "372736",
                    "--rope-scale", "1.421875", "-ctk", "q8_0"]}
        resized = {"n_ctx": 262144, "argv": ["llama-server", "-c", "262144",
                   "--rope-scale", "1.421875", "-ctk", "q8_0"]}
        self.assertEqual(cache.capacity_independent_identity(original), cache.capacity_independent_identity(resized))
        changed = dict(resized, argv=["llama-server", "-c", "262144", "--rope-scale", "1.0", "-ctk", "q8_0"])
        self.assertNotEqual(cache.capacity_independent_identity(original), cache.capacity_independent_identity(changed))


if __name__ == "__main__":
    unittest.main()
