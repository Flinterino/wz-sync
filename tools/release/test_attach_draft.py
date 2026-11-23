import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from attach_draft import Asset, Draft, GitHub, ReleaseError, attach


TAG = "2026.11.18-fixture"
COMMIT = "a" * 40
EXE = "WzSync.App.exe"
SIDECAR = EXE + ".sha256"
MANIFESTS = (
    "manifest-full.json",
    "manifest-full.json.sha256",
    "manifest-lite.json",
    "manifest-lite.json.sha256",
)
EXE_BYTES = b"synthetic executable bytes for workflow tests"
EXE_HASH = hashlib.sha256(EXE_BYTES).hexdigest()
SIDE_BYTES = f"{EXE_HASH}  {EXE}\n".encode("ascii")


class FakeGitHub:
    def __init__(self):
        self.assets = [Asset(index, name, 1) for index, name in enumerate(MANIFESTS, 1)]
        self.payloads = {asset.name: b"x" for asset in self.assets}
        self.downloaded = []
        self.uploaded = []
        self.draft = True
        self.ref = COMMIT
        self.initial_reads = 0
        self.on_snapshot = lambda: None

    def snapshot(self, tag):
        self.initial_reads += 1
        if tag != TAG or not self.draft:
            raise ReleaseError("Create an approved draft first.")
        self.on_snapshot()
        return Draft(20, tag, tuple(self.assets))

    def tag_commit(self, tag):
        return self.ref

    def download(self, entry, path):
        self.downloaded.append(entry.name)
        with path.open("xb") as file:
            file.write(self.payloads[entry.name])

    def upload(self, draft, path):
        if draft.id != 20 or draft.tag != TAG:
            raise AssertionError("Unexpected draft.")
        if path.name in self.payloads:
            raise AssertionError("An existing release asset was offered for upload.")
        self.uploaded.append(path.name)
        data = path.read_bytes()
        asset = Asset(100 + len(self.uploaded), path.name, len(data))
        self.assets.append(asset)
        self.payloads[path.name] = data
        return asset


class DraftTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="wz-sync-launcher-fixture-")
        self.addCleanup(self.tmp.cleanup)
        self.directory = Path(self.tmp.name)
        (self.directory / EXE).write_bytes(EXE_BYTES)
        (self.directory / SIDECAR).write_bytes(SIDE_BYTES)
        self.github = FakeGitHub()
        self.validated = False

    def validate(self, bundle, tag):
        self.assertEqual(tag, TAG)
        self.assertEqual(set(path.name for path in bundle.iterdir()), set(MANIFESTS))
        self.validated = True

    def run_attach(self, declared_hash=EXE_HASH):
        attach(self.github, self.directory, TAG, COMMIT, declared_hash, self.validate)

    def test_appends_both_outputs_after_validating_the_bundle(self):
        self.run_attach()
        self.assertTrue(self.validated)
        self.assertEqual(self.github.uploaded, [EXE, SIDECAR])
        self.assertGreaterEqual(self.github.initial_reads, 3)

    def test_preserves_matching_output_and_appends_only_the_missing_file(self):
        self.github.assets.append(Asset(50, EXE, len(EXE_BYTES)))
        self.github.payloads[EXE] = EXE_BYTES
        self.run_attach()
        self.assertEqual(self.github.uploaded, [SIDECAR])

    def test_repeat_uses_both_verified_outputs_without_upload(self):
        self.run_attach()
        self.github.uploaded.clear()
        self.run_attach()
        self.assertEqual(self.github.uploaded, [])

    def test_existing_conflict_is_never_overwritten(self):
        data = b"other build"
        self.github.assets.append(Asset(50, EXE, len(data)))
        self.github.payloads[EXE] = data
        with self.assertRaises(ReleaseError):
            self.run_attach()
        self.assertEqual(self.github.uploaded, [])
        self.assertEqual(self.github.payloads[EXE], data)

    def test_transport_targets_the_verified_numeric_id_and_refuses_published_state(self):
        github = GitHub()
        github.env["GH_TOKEN"] = "fixture"
        draft = Draft(20, TAG, ())
        with patch.object(github, "api", return_value={"id": 20, "tag_name": TAG, "draft": False}):
            with patch("attach_draft.http.client.HTTPSConnection") as connect:
                with self.assertRaises(ReleaseError):
                    github.upload(draft, self.directory / EXE)
                connect.assert_not_called()
        with patch.object(github, "api", return_value={"id": 20, "tag_name": TAG, "draft": True}):
            with patch("attach_draft.http.client.HTTPSConnection") as connect:
                reply = connect.return_value.getresponse.return_value.__enter__.return_value
                reply.status = 201
                reply.read.return_value = json.dumps(
                    {"id": 67, "name": EXE, "size": len(EXE_BYTES), "state": "uploaded"}
                ).encode("utf-8")
                self.assertEqual(github.upload(draft, self.directory / EXE), Asset(67, EXE, len(EXE_BYTES)))
                connect.assert_called_once_with("uploads.github.com", timeout=120)
                method, destination = connect.return_value.request.call_args.args
                self.assertEqual(method, "POST")
                self.assertEqual(destination, "/repos/Flinterino/wz-sync/releases/20/assets?name=" + EXE)

    def test_checks_asset_names_before_downloading(self):
        self.github.assets.append(Asset(51, "../other.zip", 12))
        with self.assertRaises(ReleaseError):
            self.run_attach()
        self.assertEqual(self.github.downloaded, [])

    def test_changed_draft_inputs_block_the_write(self):
        def replace_manifest():
            if self.github.initial_reads == 2:
                self.github.assets[0] = Asset(60, self.github.assets[0].name, 1)

        self.github.on_snapshot = replace_manifest
        with self.assertRaises(ReleaseError):
            self.run_attach()
        self.assertEqual(self.github.uploaded, [])

    def test_missing_draft_moved_tag_and_bad_digest_stop_early(self):
        for reason in ("published", "tag", "digest"):
            with self.subTest(reason=reason):
                self.github = FakeGitHub()
                self.github.draft = reason != "published"
                self.github.ref = "b" * 40 if reason == "tag" else COMMIT
                with self.assertRaises(ReleaseError):
                    self.run_attach("b" * 64 if reason == "digest" else EXE_HASH)
                self.assertEqual(self.github.downloaded, [])
                self.assertEqual(self.github.uploaded, [])


if __name__ == "__main__":
    unittest.main()
