"""Connection-local, disk-backed blob transfers. No whole-file base64 allocation.

IDs never name caller-selected paths. Chunks are ordered, bounded and hashed; a checkpoint
is loaded only after full length/hash verification. All partial files are connection-owned.
"""

import base64
import hashlib
import os
import tempfile
import uuid

CHUNK_BYTES = 1024 * 1024
MAX_ENCODED_CHUNK = ((CHUNK_BYTES + 2) // 3) * 4


class BlobError(ValueError):
    abl_kind = "bad_request"


class Transfers:
    def __init__(self, kept):
        self.uploads = {}
        self.downloads = {}
        self.kept = kept

    def put_begin(self, req):
        size, sha = req.get("bytes"), req.get("sha256")
        if type(size) is not int or size < 0:
            raise BlobError("blob bytes must be a nonnegative integer")
        if not isinstance(sha, str) or len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
            raise BlobError("blob sha256 must be a lowercase SHA-256")
        if len(self.uploads) >= 8:
            raise BlobError("too many unfinished uploads")
        fd, path = tempfile.mkstemp(prefix="abl-upload-", suffix=".blend")
        key = uuid.uuid4().hex
        self.uploads[key] = {
            "file": os.fdopen(fd, "wb"), "path": path, "bytes": size, "sha256": sha,
            "offset": 0, "hash": hashlib.sha256(),
        }
        return {"transfer_id": key, "chunk_bytes": CHUNK_BYTES}

    def put_chunk(self, req):
        item = self.uploads.get(req.get("transfer_id"))
        if item is None:
            raise BlobError("unknown upload")
        if type(req.get("offset")) is not int or req["offset"] != item["offset"]:
            raise BlobError("upload offset is not the next ordered offset")
        encoded = req.get("data")
        if not isinstance(encoded, str) or len(encoded) > MAX_ENCODED_CHUNK:
            raise BlobError("upload chunk exceeds the 1 MiB bound")
        try:
            data = base64.b64decode(encoded, validate=True)
        except (ValueError, UnicodeError) as exc:
            raise BlobError("upload chunk is not base64") from exc
        if not data or len(data) > CHUNK_BYTES or item["offset"] + len(data) > item["bytes"]:
            raise BlobError("upload chunk exceeds the declared length or chunk bound")
        item["file"].write(data)
        item["hash"].update(data)
        item["offset"] += len(data)
        return {"offset": item["offset"]}

    def put_commit(self, req, load):
        key = req.get("transfer_id")
        item = self.uploads.pop(key, None)
        if item is None:
            raise BlobError("unknown upload")
        keep = False
        try:
            item["file"].close()
            if item["offset"] != item["bytes"] or item["hash"].hexdigest() != item["sha256"]:
                raise BlobError("upload length/SHA-256 verification failed")
            load(item["path"])
            keep = bool(req.get("keep"))
            if keep:
                self.kept.append(item["path"])
                return {"path": item["path"]}
            return {}
        finally:
            if not keep:
                self._remove(item["path"])

    def describe(self, path, keep=False):
        digest = hashlib.sha256()
        size = 0
        with open(path, "rb") as f:
            while data := f.read(CHUNK_BYTES):
                digest.update(data)
                size += len(data)
        key = uuid.uuid4().hex
        self.downloads[key] = {"path": path, "bytes": size, "offset": 0, "keep": keep}
        if keep and path not in self.kept:
            self.kept.append(path)
        return {"transfer_id": key, "bytes": size, "sha256": digest.hexdigest()}

    def get_chunk(self, req):
        item = self.downloads.get(req.get("transfer_id"))
        if item is None:
            raise BlobError("unknown download")
        if type(req.get("offset")) is not int or req["offset"] != item["offset"]:
            raise BlobError("download offset is not the next ordered offset")
        with open(item["path"], "rb") as f:
            f.seek(item["offset"])
            data = f.read(min(CHUNK_BYTES, item["bytes"] - item["offset"]))
        offset = item["offset"]
        item["offset"] += len(data)
        return {"offset": offset, "data": base64.b64encode(data).decode("ascii")}

    def get_finish(self, req):
        key = req.get("transfer_id")
        item = self.downloads.get(key)
        if item is None:
            raise BlobError("unknown download")
        if item["offset"] != item["bytes"]:
            raise BlobError("download is incomplete")
        self.abort(req)
        return {}

    def abort(self, req):
        key = req.get("transfer_id")
        upload = self.uploads.pop(key, None)
        if upload is not None:
            upload["file"].close()
            self._remove(upload["path"])
        download = self.downloads.pop(key, None)
        if download is not None and not download["keep"]:
            self._remove(download["path"])
        return {}

    def close(self):
        for key in list(self.uploads) + list(self.downloads):
            self.abort({"transfer_id": key})

    @staticmethod
    def _remove(path):
        try:
            os.remove(path)
        except OSError:
            pass
