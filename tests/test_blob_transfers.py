"""Worker-side transfer regressions: no Blender is needed for framing/storage validation."""

import base64
import hashlib
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "worker"))
import blobs  # noqa: E402


class TransferTests(unittest.TestCase):
    def setUp(self):
        self.kept = []
        self.transfers = blobs.Transfers(self.kept)

    def tearDown(self):
        self.transfers.close()
        for path in self.kept:
            pathlib.Path(path).unlink(missing_ok=True)

    def begin(self, data, sha=None):
        return self.transfers.put_begin({"bytes": len(data), "sha256": sha or hashlib.sha256(data).hexdigest()})["transfer_id"]

    def test_large_upload_download_and_cleanup(self):
        chunk = bytes(range(256)) * (blobs.CHUNK_BYTES // 256)
        count = 72
        digest = hashlib.sha256()
        for _ in range(count):
            digest.update(chunk)
        key = self.transfers.put_begin({"bytes": len(chunk) * count, "sha256": digest.hexdigest()})["transfer_id"]
        for i in range(count):
            self.transfers.put_chunk({"transfer_id": key, "offset": i * len(chunk), "data": base64.b64encode(chunk).decode()})
        loaded = []
        result = self.transfers.put_commit({"transfer_id": key, "keep": True}, loaded.append)
        path = result["path"]
        self.assertEqual([path], loaded)
        descriptor = self.transfers.describe(path)
        received = hashlib.sha256()
        for i in range(count):
            part = self.transfers.get_chunk({"transfer_id": descriptor["transfer_id"], "offset": i * len(chunk)})
            received.update(base64.b64decode(part["data"]))
        self.assertEqual(digest.hexdigest(), received.hexdigest())
        self.transfers.get_finish(descriptor)
        self.assertFalse(pathlib.Path(path).exists())

    def test_bad_offsets_bounds_and_hash_do_not_load_checkpoint(self):
        key = self.begin(b"abc", "0" * 64)
        with self.assertRaises(blobs.BlobError):
            self.transfers.put_chunk({"transfer_id": key, "offset": 1, "data": "YWJj"})
        with self.assertRaises(blobs.BlobError):
            self.transfers.put_chunk({"transfer_id": key, "offset": 0, "data": "A" * (blobs.MAX_ENCODED_CHUNK + 1)})
        self.transfers.put_chunk({"transfer_id": key, "offset": 0, "data": "YWJj"})
        path = self.transfers.uploads[key]["path"]
        loaded = []
        with self.assertRaises(blobs.BlobError):
            self.transfers.put_commit({"transfer_id": key}, loaded.append)
        self.assertEqual([], loaded)
        self.assertFalse(pathlib.Path(path).exists())

    def test_disconnect_reaps_partial_upload(self):
        key = self.begin(b"abc")
        path = self.transfers.uploads[key]["path"]
        self.transfers.close()
        self.assertFalse(pathlib.Path(path).exists())


if __name__ == "__main__":
    unittest.main()
