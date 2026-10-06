"""Bounded artifact reading and input-contract validation for the point-shadow seam analyzer.

ZIP and directory bundles open under fixed resource limits. Entry names, counts and declared sizes
are checked from metadata before any payload read, and every payload read counts the bytes it
actually produces. The existing schema-v1 evidence, the diagnostic-local expectation descriptor,
attestation shape, active-atlas sidecars and collector TRX files are validated here. Supplied claims
are checked for shape only. Comparing them with pinned expectations belongs to shadow_compare.
Nothing here renders, starts a process, accesses the network or invokes git. See README.md.
"""

from __future__ import annotations

import errno
import hashlib
import io
import json
import math
import os
import re
import stat
import struct
import zlib
from dataclasses import dataclass
from typing import Any, BinaryIO, Dict, List, Mapping, Optional, Sequence, Tuple
from xml.etree import ElementTree

# Tool resource limits. They bound reads, they are not metric parameters.
MAX_ENTRIES = 64
MAX_TOTAL_BYTES = 64 * 1024 * 1024
MAX_MANIFEST_BYTES = 2 * 1024 * 1024
READ_CHUNK_BYTES = 64 * 1024

# Existing schema-v1 evidence and capture shape.
EVIDENCE_SCHEMA = "khaozengine.point-shadow-seam-evidence"
EVIDENCE_SCHEMA_VERSION = 1
CAPTURE_WIDTH = 192
CAPTURE_HEIGHT = 192
CAPTURE_BYTES = 147_456
STATIONS = 13
PROBES_PER_STATION = 61
RECORDED_PROBES = 793

# Active atlas row shape at the frozen 256 face resolution, six face columns, R32 float.
FACE_RESOLUTION = 256
ATLAS_FACE_COLUMNS = 6
ATLAS_ROW_BYTES = 1_572_864
ATLAS_FORMAT = "R32Float little-endian IEEE754"
ATLAS_BINDING_CHECK = "BoundPointShadowTexture is PointShadowTexture"

# Diagnostic-local comparison schemas. Field names are frozen in README.md.
COMPARISON_SCHEMA_VERSION = 1
EXPECTATION_SCHEMA = "khaozengine.point-shadow-control-expectation"
ATTESTATION_SCHEMA = "khaozengine.point-shadow-source-equivalence"
ATLAS_SIDECAR_SCHEMA = "khaozengine.point-shadow-active-atlas-row"
TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"

BUILDS = ("B", "M")
MUTATION_PATH = "KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs"

DESIGN_SHA256 = "1c4ebca1d297405cb4b2f7b08f4ef585acc58946506f4a8d8569681499ea6a9d"
RETAINED_ARCHIVE_SHA256 = "ef5ce8d9e72cf5f0d2638aedb0537e48dc80094b95ac73c5313b04d58a4743b5"
RETAINED_MANIFEST_ENTRY = (
    "_temp/point-shadow-seam-37482238342-1-55a8ed392a134a15baa7fc631f47c183/point-shadow-seam.json"
)
RETAINED_TRX_ENTRY = "KhaozEngine/KhaozEngine/TestResults/seam.trx"

# The pinned retained record, transcribed from the archive pinned above. Compare mode reports the
# historical row and checks G4 against these values without rereading the archive. Retained mode
# rechecks every value against the manifest and TRX it reads.
HISTORICAL_TEST_NAME = (
    "KhaozEngine.Tests.Gpu.PointShadowFilterGpuTests.TheSoftEdgeCrossesACubeFaceBoundaryWithoutAStep"
)
RETAINED_ASSERTION_OUTCOME = "Failed"
RETAINED_SOFT_SHA256 = "ae1914c9d740bee9c730689c6451cf4262602c3def73247aedfd00cc518da405"
RETAINED_CAMERA: Mapping[str, Tuple[float, ...]] = {
    "view": (
        1.0, 0.0, 0.0, 0.0,
        0.0, 0.21900664, 0.9757233, 0.0,
        0.0, -0.9757233, 0.21900664, 0.0,
        -3.6, 5.2689047, -51.182636, 1.0,
    ),
    "projection": (
        0.5555556, 0.0, 0.0, 0.0,
        0.0, 0.5555556, 0.0, 0.0,
        0.0, 0.0, -0.0050025014, 0.0,
        0.0, 0.0, -0.00050025014, 1.0,
    ),
    "viewProjection": (
        0.5555556, 0.0, 0.0, 0.0,
        0.0, 0.121670365, -0.0048810574, 0.0,
        0.0, -0.54206854, -0.001095581, 0.0,
        -2.0, 2.9271693, 0.25554094, 1.0,
    ),
    "renderOrigin": (0.0, 0.0, 0.0),
}
RETAINED_BUMP = -0.008728723
RETAINED_WORST_ELSEWHERE = 0.008491099
RETAINED_STATION_REACHES = (
    0.9140385, 0.91098034, 0.91133153, 0.9041096, 0.896209, 0.890001, 0.90375,
    0.9024812, 0.9043846, 0.90519685, 0.9070161, 0.90815943, 0.9135727,
)

REFUSAL_CODES = frozenset(
    {
        "entry-count",
        "declared-bytes",
        "entry-bytes",
        "actual-bytes",
        "entry-integrity",
        "zip-method",
        "duplicate-name",
        "unsafe-path",
        "symlink",
        "missing-entry",
        "schema",
        "non-finite",
        "identity",
        "dimensions",
        "length",
        "sha256",
        "grid",
        "recorded-mismatch",
    }
)

_HEX40 = re.compile(r"[0-9a-f]{40}\Z")
_HEX64 = re.compile(r"[0-9a-f]{64}\Z")
_DRIVE_PREFIX = re.compile(r"[A-Za-z]:")
_COUNTER = re.compile(r"[0-9]+\Z")

_ZIP_LOCAL = b"PK\x03\x04"
_ZIP_CENTRAL = b"PK\x01\x02"
_ZIP_END = b"PK\x05\x06"
_LOCAL_HEADER = struct.Struct("<4sHHHHHIIIHH")
_CENTRAL_HEADER = struct.Struct("<4sHHHHHHIIIHHHHHII")
_END_RECORD = struct.Struct("<4sHHHHIIH")
_MAX_ZIP_COMMENT = 0xFFFF
_MAX_CENTRAL_RECORD = _CENTRAL_HEADER.size + 3 * 0xFFFF
_ZIP_STORED = 0
_ZIP_DEFLATED = 8
_ZIP_ENCRYPTED_FLAG = 0x0001
_ZIP_UTF8_FLAG = 0x0800
_ZIP64_MARKER = 0xFFFFFFFF


class InputRefused(ValueError):
    """An input artifact violates the contract. `code` is one of REFUSAL_CODES."""

    def __init__(self, code: str, detail: str = "") -> None:
        super().__init__(f"{code}: {detail}" if detail else code)
        self.code = code
        self.detail = detail


@dataclass(frozen=True)
class ReadLimits:
    max_entries: int = MAX_ENTRIES
    max_total_bytes: int = MAX_TOTAL_BYTES


DEFAULT_LIMITS = ReadLimits()


def sha256_hex(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def is_safe_name(name: Any) -> bool:
    """A POSIX relative name with no absolute, drive, backslash, empty, `.` or `..` segment."""
    if not isinstance(name, str) or not name or "\\" in name or "\x00" in name:
        return False
    if name.startswith("/") or _DRIVE_PREFIX.match(name):
        return False
    return all(part not in ("", ".", "..") for part in name.split("/"))


def _expect(condition: bool, detail: str, code: str = "schema") -> None:
    if not condition:
        raise InputRefused(code, detail)


class _CappedSink:
    """Collects produced bytes and refuses `actual-bytes` the moment they pass the cap."""

    def __init__(self, cap: int, label: str) -> None:
        self._cap = cap
        self._label = label
        self._buffer = bytearray()

    def room(self) -> int:
        return self._cap - len(self._buffer)

    def add(self, block: bytes) -> None:
        if len(block) > self.room():
            raise InputRefused("actual-bytes", f"{self._label} produced more than {self._cap} bytes")
        self._buffer += block

    def value(self) -> bytes:
        return bytes(self._buffer)


def read_capped(stream: BinaryIO, limit: int, *, chunk: int = READ_CHUNK_BYTES) -> bytes:
    """Read at most `limit` bytes, refusing `actual-bytes` as soon as the stream yields more."""
    if limit < 0 or chunk <= 0:
        raise ValueError("limit must be nonnegative and chunk positive")
    buffer = bytearray()
    while True:
        block = stream.read(min(chunk, limit + 1 - len(buffer)))
        if block is None:
            raise InputRefused("entry-integrity", "stream is not readable without blocking")
        if not block:
            return bytes(buffer)
        buffer += block
        if len(buffer) > limit:
            raise InputRefused("actual-bytes", f"stream yielded more than {limit} bytes")


def read_file(path: Any, max_bytes: int) -> bytes:
    """Read one explicitly named regular file outside a bundle, bounded by `max_bytes`."""
    try:
        handle = open(os.fspath(path), "rb", buffering=0)
    except OSError as error:
        raise InputRefused("missing-entry", f"{os.fspath(path)}: {error.strerror or error}") from None
    with handle:
        if not stat.S_ISREG(os.fstat(handle.fileno()).st_mode):
            raise InputRefused("missing-entry", f"{os.fspath(path)} is not a regular file")
        return read_capped(handle, max_bytes)


class Bundle:
    """A ZIP or directory whose entry count, names and declared sizes passed the limits at open."""

    kind: str = ""

    def __init__(self, limits: ReadLimits) -> None:
        self._limits = limits
        self._declared: Dict[str, int] = {}
        self._consumed = 0

    def names(self) -> Tuple[str, ...]:
        return tuple(sorted(self._declared))

    def read(self, name: str, max_bytes: int) -> bytes:
        """Stream one named file. Actual bytes are counted against `max_bytes` and the aggregate cap
        before the declared-size and integrity checks."""
        if not is_safe_name(name):
            raise InputRefused("unsafe-path", repr(name))
        declared = self._declared.get(name)
        if declared is None:
            raise InputRefused("missing-entry", name)
        if declared > max_bytes:
            raise InputRefused("entry-bytes", f"{name} declares {declared} bytes, limit {max_bytes}")
        cap = min(max_bytes, self._limits.max_total_bytes - self._consumed)
        data = self._read_payload(name, cap)
        self._consumed += len(data)
        if len(data) != declared:
            raise InputRefused("entry-integrity", f"{name} produced {len(data)} bytes, declared {declared}")
        self._check_payload(name, data)
        return data

    def close(self) -> None:
        pass

    def _read_payload(self, name: str, cap: int) -> bytes:
        raise NotImplementedError

    def _check_payload(self, name: str, data: bytes) -> None:
        pass

    def __enter__(self) -> "Bundle":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()


@dataclass(frozen=True)
class _ZipEntry:
    name: str
    raw_name: bytes
    flags: int
    method: int
    crc: int
    compressed_size: int
    size: int
    offset: int


class _ZipBundle(Bundle):
    kind = "zip"

    def __init__(self, handle: BinaryIO, limits: ReadLimits) -> None:
        super().__init__(limits)
        self._handle = handle
        self._entries: Dict[str, _ZipEntry] = {}
        self._central_offset = 0

    def close(self) -> None:
        self._handle.close()

    def _load(self) -> None:
        handle = self._handle
        handle.seek(0, os.SEEK_END)
        size = handle.tell()
        tail_size = min(size, _END_RECORD.size + _MAX_ZIP_COMMENT)
        handle.seek(size - tail_size)
        tail = handle.read(tail_size)
        _expect(len(tail) == tail_size, "short read of the ZIP tail", "entry-integrity")
        position = tail.rfind(_ZIP_END)
        fields = None
        while position >= 0:
            if position + _END_RECORD.size <= len(tail):
                candidate = _END_RECORD.unpack_from(tail, position)
                if position + _END_RECORD.size + candidate[7] == len(tail):
                    fields = candidate
                    break
            position = tail.rfind(_ZIP_END, 0, position)
        _expect(fields is not None, "no ZIP end-of-central-directory record", "entry-integrity")
        _, disk, central_disk, disk_entries, total, central_size, central_offset, _ = fields
        if total > self._limits.max_entries:
            raise InputRefused("entry-count", f"{total} entries, limit {self._limits.max_entries}")
        _expect(disk == 0 and central_disk == 0 and disk_entries == total, "multi-disk ZIP", "entry-integrity")
        _expect(
            central_size != _ZIP64_MARKER and central_offset != _ZIP64_MARKER,
            "ZIP64 archives are not supported",
            "entry-integrity",
        )
        end_offset = size - tail_size + position
        _expect(central_offset + central_size == end_offset, "central directory is not adjacent to its end record",
                "entry-integrity")
        _expect(central_size <= total * _MAX_CENTRAL_RECORD, "central directory larger than its entries allow",
                "entry-integrity")
        handle.seek(central_offset)
        central = handle.read(central_size)
        _expect(len(central) == central_size, "short central directory", "entry-integrity")

        entries: List[_ZipEntry] = []
        cursor = 0
        for _ in range(total):
            _expect(cursor + _CENTRAL_HEADER.size <= len(central), "truncated central record", "entry-integrity")
            record = _CENTRAL_HEADER.unpack_from(central, cursor)
            signature, flags, method, crc = record[0], record[3], record[4], record[7]
            compressed_size, uncompressed_size = record[8], record[9]
            name_length, extra_length, comment_length, offset = record[10], record[11], record[12], record[16]
            _expect(signature == _ZIP_CENTRAL, "bad central record signature", "entry-integrity")
            start = cursor + _CENTRAL_HEADER.size
            end = start + name_length
            _expect(end + extra_length + comment_length <= len(central), "truncated central record", "entry-integrity")
            _expect(_ZIP64_MARKER not in (compressed_size, uncompressed_size, offset), "ZIP64 entries are not supported",
                    "entry-integrity")
            raw_name = central[start:end]
            try:
                name = raw_name.decode("utf-8" if flags & _ZIP_UTF8_FLAG else "cp437")
            except UnicodeDecodeError:
                raise InputRefused("entry-integrity", "undecodable entry name") from None
            entries.append(_ZipEntry(name, raw_name, flags, method, crc, compressed_size, uncompressed_size, offset))
            cursor = end + extra_length + comment_length
        _expect(cursor == len(central), "central directory has trailing bytes", "entry-integrity")

        declared = sum(entry.size for entry in entries)
        if declared > self._limits.max_total_bytes:
            raise InputRefused("declared-bytes", f"{declared} declared bytes, limit {self._limits.max_total_bytes}")
        for entry in entries:
            _expect(not entry.flags & _ZIP_ENCRYPTED_FLAG, f"{entry.name!r} is encrypted", "entry-integrity")
        for entry in entries:
            if entry.method not in (_ZIP_STORED, _ZIP_DEFLATED):
                raise InputRefused("zip-method", f"{entry.name!r} uses method {entry.method}")
        seen = set()
        for entry in entries:
            if entry.name in seen:
                raise InputRefused("duplicate-name", entry.name)
            seen.add(entry.name)
        for entry in entries:
            bare = entry.name[:-1] if entry.name.endswith("/") else entry.name
            if not is_safe_name(bare):
                raise InputRefused("unsafe-path", repr(entry.name))
        for entry in entries:
            if not entry.name.endswith("/"):
                self._entries[entry.name] = entry
                self._declared[entry.name] = entry.size
        self._central_offset = central_offset

    def _read_payload(self, name: str, cap: int) -> bytes:
        entry = self._entries[name]
        handle = self._handle
        handle.seek(entry.offset)
        header = handle.read(_LOCAL_HEADER.size)
        _expect(len(header) == _LOCAL_HEADER.size, f"{name}: truncated local header", "entry-integrity")
        local = _LOCAL_HEADER.unpack(header)
        _expect(local[0] == _ZIP_LOCAL, f"{name}: bad local header signature", "entry-integrity")
        name_length, extra_length = local[9], local[10]
        _expect(handle.read(name_length) == entry.raw_name, f"{name}: local and central names differ",
                "entry-integrity")
        data_offset = entry.offset + _LOCAL_HEADER.size + name_length + extra_length
        _expect(data_offset + entry.compressed_size <= self._central_offset,
                f"{name}: compressed data overlaps the central directory", "entry-integrity")
        handle.seek(data_offset)

        sink = _CappedSink(cap, name)
        remaining = entry.compressed_size

        def next_block() -> bytes:
            nonlocal remaining
            block = handle.read(min(READ_CHUNK_BYTES, remaining))
            _expect(bool(block), f"{name}: truncated compressed data", "entry-integrity")
            remaining -= len(block)
            return block

        if entry.method == _ZIP_STORED:
            while remaining:
                sink.add(next_block())
            return sink.value()

        decoder = zlib.decompressobj(-zlib.MAX_WBITS)
        try:
            while remaining and not decoder.eof:
                pending = next_block()
                while pending and not decoder.eof:
                    sink.add(decoder.decompress(pending, sink.room() + 1))
                    pending = decoder.unconsumed_tail
        except zlib.error as error:
            raise InputRefused("entry-integrity", f"{name}: {error}") from None
        _expect(decoder.eof, f"{name}: deflate stream does not end", "entry-integrity")
        _expect(not remaining and not decoder.unused_data, f"{name}: bytes after the deflate stream",
                "entry-integrity")
        return sink.value()

    def _check_payload(self, name: str, data: bytes) -> None:
        if zlib.crc32(data) & 0xFFFFFFFF != self._entries[name].crc:
            raise InputRefused("entry-integrity", f"{name}: CRC mismatch")


class _DirectoryBundle(Bundle):
    kind = "directory"

    def __init__(self, root: str, limits: ReadLimits) -> None:
        super().__init__(limits)
        self._root = root

    def _load(self) -> None:
        count = 0
        declared = 0
        unsafe: Optional[str] = None
        link: Optional[str] = None
        pending = [("", self._root)]
        while pending:
            prefix, directory = pending.pop()
            with os.scandir(directory) as listing:
                for item in listing:
                    count += 1
                    if count > self._limits.max_entries:
                        raise InputRefused("entry-count", f"more than {self._limits.max_entries} entries")
                    name = prefix + item.name
                    if item.is_symlink():
                        link = link or name
                    elif not is_safe_name(name):
                        unsafe = unsafe or name
                    elif item.is_dir(follow_symlinks=False):
                        pending.append((name + "/", item.path))
                    elif item.is_file(follow_symlinks=False):
                        size = item.stat(follow_symlinks=False).st_size
                        declared += size
                        self._declared[name] = size
                    else:
                        unsafe = unsafe or name
        if declared > self._limits.max_total_bytes:
            raise InputRefused("declared-bytes", f"{declared} declared bytes, limit {self._limits.max_total_bytes}")
        if unsafe is not None:
            raise InputRefused("unsafe-path", f"{unsafe!r} is not a safe regular file or directory")
        if link is not None:
            raise InputRefused("symlink", link)

    def _read_payload(self, name: str, cap: int) -> bytes:
        path = os.path.join(self._root, *name.split("/"))
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
        try:
            descriptor = os.open(path, flags)
        except FileNotFoundError:
            raise InputRefused("missing-entry", name) from None
        except OSError as error:
            if error.errno == errno.ELOOP:
                raise InputRefused("symlink", name) from None
            raise InputRefused("entry-integrity", f"{name}: {error.strerror or error}") from None
        with os.fdopen(descriptor, "rb", buffering=0) as handle:
            _expect(stat.S_ISREG(os.fstat(handle.fileno()).st_mode), f"{name} is not a regular file", "unsafe-path")
            return read_capped(handle, cap)


def _open_zip(handle: BinaryIO, limits: ReadLimits) -> Bundle:
    bundle = _ZipBundle(handle, limits)
    try:
        bundle._load()
    except BaseException:
        bundle.close()
        raise
    return bundle


def open_bundle(path: Any, limits: ReadLimits = DEFAULT_LIMITS) -> Bundle:
    """Check entry count, names, sizes and supported ZIP_STORED/ZIP_DEFLATED methods.
    Directory roots and descendants may not be symlinks. Normal parent aliases are permitted."""
    root = os.fspath(path)
    try:
        info = os.lstat(root)
    except OSError as error:
        raise InputRefused("missing-entry", f"{root}: {error.strerror or error}") from None
    if stat.S_ISLNK(info.st_mode):
        try:
            target = os.stat(root)
        except OSError as error:
            raise InputRefused("missing-entry", f"{root}: {error.strerror or error}") from None
        if stat.S_ISDIR(target.st_mode):
            raise InputRefused("symlink", f"directory bundle root {root} is a symlink")
        info = target
    if stat.S_ISDIR(info.st_mode):
        bundle = _DirectoryBundle(root, limits)
        bundle._load()
        return bundle
    if stat.S_ISREG(info.st_mode):
        return _open_zip(open(root, "rb"), limits)
    raise InputRefused("unsafe-path", f"{root} is neither a directory nor a regular file")


def open_zip_bytes(data: bytes, limits: ReadLimits = DEFAULT_LIMITS) -> Bundle:
    """Open ZIP bytes that were already read and digest-checked, so the checked bytes are the ones read."""
    return _open_zip(io.BytesIO(data), limits)


def load_json(raw: bytes) -> Any:
    """Parse JSON, refusing NaN, Infinity, overflowing numbers and duplicate object keys."""
    try:
        text = bytes(raw).decode("utf-8-sig")
    except UnicodeDecodeError:
        raise InputRefused("schema", "document is not UTF-8") from None

    def constant(token: str) -> Any:
        raise InputRefused("non-finite", f"JSON token {token}")

    def number(token: str) -> float:
        value = float(token)
        if not math.isfinite(value):
            raise InputRefused("non-finite", f"JSON number {token}")
        return value

    def pairs(items: Sequence[Tuple[str, Any]]) -> Dict[str, Any]:
        document: Dict[str, Any] = {}
        for key, value in items:
            if key in document:
                raise InputRefused("schema", f"duplicate JSON key {key!r}")
            document[key] = value
        return document

    try:
        return json.loads(text, parse_constant=constant, parse_float=number, object_pairs_hook=pairs)
    except InputRefused:
        raise
    except (ValueError, RecursionError) as error:
        raise InputRefused("schema", f"invalid JSON: {error}") from None


def _is_int(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _is_number(value: Any) -> bool:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return False
    try:
        return math.isfinite(value)
    except OverflowError:
        return False


def _is_text(value: Any) -> bool:
    return isinstance(value, str) and bool(value)


def _is_hex(value: Any, length: int) -> bool:
    return isinstance(value, str) and (_HEX40 if length == 40 else _HEX64).match(value) is not None


def _expect_object(value: Any, fields: Sequence[str], label: str) -> None:
    _expect(isinstance(value, dict), f"{label} must be an object")
    missing = [field for field in fields if field not in value]
    unknown = sorted(set(value) - set(fields))
    _expect(not missing, f"{label} is missing {missing}")
    _expect(not unknown, f"{label} has unknown fields {unknown}")


def _expect_vector(value: Any, length: int, label: str, integers: bool = False) -> None:
    check = _is_int if integers else _is_number
    _expect(isinstance(value, list) and len(value) == length and all(check(v) for v in value),
            f"{label} must be {length} {'integers' if integers else 'numbers'}")


def parse_phase(value: Any) -> Tuple[float, float]:
    """A phase `[phiX, phiY]` as a tuple of floats. Membership in the frozen set is a gate concern."""
    _expect(isinstance(value, (list, tuple)) and len(value) == 2 and all(_is_number(v) for v in value),
            f"phase {value!r} must be two finite numbers")
    return (float(value[0]), float(value[1]))


def _check_identity(value: Any, label: str) -> None:
    _expect_object(value, ("sourceCommit", "sourceTree"), label)
    _expect(_is_hex(value["sourceCommit"], 40) and _is_hex(value["sourceTree"], 40),
            f"{label} commit and tree must be 40 lowercase hex")


def _check_mutation(value: Any, label: str) -> None:
    _expect_object(value, ("changedPaths", "diffSha256"), label)
    paths = value["changedPaths"]
    _expect(isinstance(paths, list) and bool(paths) and all(_is_text(p) for p in paths),
            f"{label} changedPaths must be a nonempty list of names")
    _expect(_is_hex(value["diffSha256"], 64), f"{label} diffSha256 must be 64 lowercase hex")


def _check_workflow(value: Any, label: str) -> None:
    _expect_object(value, ("repository", "workflowPath", "ref", "runId", "runAttempt", "jobId"), label)
    for key in ("repository", "workflowPath", "ref"):
        _expect(_is_text(value[key]), f"{label} {key} must be a nonempty string")
    for key in ("runId", "runAttempt", "jobId"):
        _expect(_is_int(value[key]) and value[key] >= 0, f"{label} {key} must be a nonnegative integer")


def _check_host(value: Any, label: str) -> None:
    _expect_object(value, ("computerName", "sameVm"), label)
    _expect(_is_text(value["computerName"]), f"{label} computerName must be a nonempty string")
    _expect(isinstance(value["sameVm"], bool), f"{label} sameVm must be a boolean")


_EXPECTATION_FIELDS = (
    "schema", "schemaVersion", "designSha256", "baseline", "mutant", "mutation", "workflow", "host",
    "expectedCollectorTestName", "phases", "captures", "trx", "attestation",
)
_CAPTURE_FIELDS = ("build", "phase", "manifest", "atlasRow", "atlasSidecar")
_ATTESTATION_FIELDS = ("schema", "schemaVersion", "workflow", "host", "baseline", "mutant", "mutation", "drawList")
_DRAW_FIELDS = ("name", "kind", "min", "max")


def parse_expectation(raw: bytes) -> Mapping[str, Any]:
    """Parse and schema-check an expected-proof descriptor."""
    document = load_json(raw)
    _expect_object(document, _EXPECTATION_FIELDS, "expectation")
    _expect(document["schema"] == EXPECTATION_SCHEMA and _is_int(document["schemaVersion"])
            and document["schemaVersion"] == COMPARISON_SCHEMA_VERSION, "expectation schema or version")
    _expect(_is_hex(document["designSha256"], 64), "designSha256 must be 64 lowercase hex")
    _check_identity(document["baseline"], "baseline")
    _check_identity(document["mutant"], "mutant")
    _check_mutation(document["mutation"], "mutation")
    _check_workflow(document["workflow"], "workflow")
    _check_host(document["host"], "host")
    _expect(_is_text(document["expectedCollectorTestName"]), "expectedCollectorTestName must be a nonempty string")
    _expect(isinstance(document["phases"], list), "phases must be a list")
    for phase in document["phases"]:
        parse_phase(phase)
    _expect(isinstance(document["captures"], list), "captures must be a list")
    for capture in document["captures"]:
        _expect_object(capture, _CAPTURE_FIELDS, "capture record")
        _expect(capture["build"] in BUILDS, f"capture build {capture['build']!r}")
        parse_phase(capture["phase"])
        for key in ("manifest", "atlasRow", "atlasSidecar"):
            _expect(isinstance(capture[key], str), f"capture {key} must be a string")
    _expect_object(document["trx"], BUILDS, "trx")
    for build in BUILDS:
        _expect(isinstance(document["trx"][build], str), "trx paths must be strings")
    _expect(isinstance(document["attestation"], str), "attestation must be a path string")

    paths = [capture[key] for capture in document["captures"] for key in ("manifest", "atlasRow", "atlasSidecar")]
    paths += [document["trx"][build] for build in BUILDS] + [document["attestation"]]
    for path in paths:
        if not is_safe_name(path):
            raise InputRefused("unsafe-path", repr(path))
    seen = set()
    for path in paths:
        if path in seen:
            raise InputRefused("duplicate-name", path)
        seen.add(path)
    return document


def check_attestation(document: Any) -> Mapping[str, Any]:
    """Shape of a supplied source-equivalence attestation. Its values are compared in G1."""
    _expect_object(document, _ATTESTATION_FIELDS, "attestation")
    _expect(document["schema"] == ATTESTATION_SCHEMA and _is_int(document["schemaVersion"])
            and document["schemaVersion"] == COMPARISON_SCHEMA_VERSION, "attestation schema or version")
    _check_workflow(document["workflow"], "attested workflow")
    _check_host(document["host"], "attested host")
    _check_identity(document["baseline"], "attested baseline")
    _check_identity(document["mutant"], "attested mutant")
    _check_mutation(document["mutation"], "attested mutation")
    _expect(isinstance(document["drawList"], list), "attested drawList must be a list")
    for draw in document["drawList"]:
        _expect_object(draw, _DRAW_FIELDS, "draw record")
        _expect(_is_text(draw["name"]) and _is_text(draw["kind"]), "draw name and kind must be strings")
        _expect_vector(draw["min"], 3, "draw min")
        _expect_vector(draw["max"], 3, "draw max")
    return document


@dataclass(frozen=True)
class Evidence:
    manifest: Mapping[str, Any]
    manifest_sha256: str
    plain: bytes
    soft: bytes
    station_count: int
    probe_count: int


def evidence_capture_files(manifest: Any) -> Tuple[str, ...]:
    """The capture file names a manifest names, each a bare safe name beside the manifest."""
    _expect(isinstance(manifest, dict), "evidence manifest must be an object")
    records = manifest.get("captures")
    _expect(isinstance(records, list) and len(records) == 2, "evidence captures must be two records")
    files = []
    for record in records:
        _expect(isinstance(record, dict) and isinstance(record.get("file"), str), "capture file must be a string")
        name = record["file"]
        if "/" in name or not is_safe_name(name):
            raise InputRefused("unsafe-path", repr(name))
        if name in files:
            raise InputRefused("duplicate-name", name)
        files.append(name)
    return tuple(files)


def _evidence_schema(document: Any) -> Dict[str, Mapping[str, Any]]:
    _expect(isinstance(document, dict), "evidence manifest must be an object")
    _expect(document.get("schema") == EVIDENCE_SCHEMA and _is_int(document.get("schemaVersion"))
            and document["schemaVersion"] == EVIDENCE_SCHEMA_VERSION, "evidence schema or version")
    _expect(document.get("status") == "complete", "evidence status must be complete")
    grid = document.get("grid")
    _expect(isinstance(grid, dict) and _is_int(grid.get("stations")) and _is_int(grid.get("probesPerStation")),
            "evidence grid counts")
    _expect(_is_int(document.get("recordedProbes")), "recordedProbes must be an integer")
    evidence_capture_files(document)
    records: Dict[str, Mapping[str, Any]] = {}
    for record in document["captures"]:
        _expect(record.get("name") in ("plain", "soft") and record["name"] not in records, "capture names")
        _expect(all(_is_int(record.get(key)) for key in ("width", "height", "bytes")), "capture dimensions")
        _expect(_is_hex(record.get("sha256"), 64), "capture sha256 must be 64 lowercase hex")
        records[record["name"]] = record
    camera = document.get("camera")
    _expect(isinstance(camera, dict), "camera must be an object")
    for key in ("view", "projection", "viewProjection"):
        _expect_vector(camera.get(key), 16, f"camera {key}")
    _expect_vector(camera.get("renderOrigin"), 3, "camera renderOrigin")
    for key in ("scene", "provenance", "device", "fit"):
        _expect(isinstance(document.get(key), dict), f"{key} must be an object")
    statistic = document.get("statistic")
    _expect(isinstance(statistic, dict) and _is_number(statistic.get("bump"))
            and _is_number(statistic.get("worstElsewhere")), "statistic bump and worstElsewhere")
    stations = document.get("stations")
    _expect(isinstance(stations, list), "stations must be a list")
    for station in stations:
        _expect(isinstance(station, dict) and _is_int(station.get("index")), "station index")
        for key in ("on", "from", "to"):
            _expect_vector(station.get(key), 3, f"station {key}")
        _expect(_is_number(station.get("sum")) and _is_number(station.get("reach")), "station sum and reach")
        probes = station.get("probes")
        _expect(isinstance(probes, list), "station probes must be a list")
        for probe in probes:
            _expect(isinstance(probe, dict) and _is_int(probe.get("index")), "probe index")
            _expect_vector(probe.get("world"), 3, "probe world")
            _expect_vector(probe.get("screen"), 2, "probe screen")
            _expect_vector(probe.get("pixel"), 2, "probe pixel", integers=True)
            for key in ("byteIndex", "plainRed", "shadowRed"):
                _expect(_is_int(probe.get(key)), f"probe {key} must be an integer")
            _expect(_is_number(probe.get("ratio")), "probe ratio must be a number")
    return records


def _f32_bits(value: Any) -> Optional[bytes]:
    try:
        return struct.pack("<f", value)
    except (OverflowError, struct.error):
        return None


def validate_evidence(manifest_bytes: bytes, captures: Mapping[str, bytes]) -> Evidence:
    """Validate a schema-v1 evidence manifest against its RGBA captures, keyed by manifest `file`."""
    document = load_json(manifest_bytes)
    records = _evidence_schema(document)
    for record in records.values():
        if record["file"] not in captures:
            raise InputRefused("missing-entry", record["file"])
    for record in records.values():
        if (record["width"], record["height"], record["bytes"]) != (CAPTURE_WIDTH, CAPTURE_HEIGHT, CAPTURE_BYTES):
            raise InputRefused("dimensions", f"{record['name']} declares {record['width']}x{record['height']}, "
                                             f"{record['bytes']} bytes")
    for record in records.values():
        if len(captures[record["file"]]) != record["bytes"]:
            raise InputRefused("length", f"{record['file']} has {len(captures[record['file']])} bytes")
    for record in records.values():
        if sha256_hex(bytes(captures[record["file"]])) != record["sha256"]:
            raise InputRefused("sha256", record["file"])

    grid = document["grid"]
    stations = document["stations"]
    _expect(grid["stations"] == STATIONS and grid["probesPerStation"] == PROBES_PER_STATION
            and document["recordedProbes"] == RECORDED_PROBES and len(stations) == STATIONS,
            "expected exactly 13 stations by 61 probes", "grid")
    for position, station in enumerate(stations):
        _expect(station["index"] == position and len(station["probes"]) == PROBES_PER_STATION,
                f"station {position} index or probe count", "grid")
        for probe_position, probe in enumerate(station["probes"]):
            _expect(probe["index"] == probe_position, f"station {position} probe {probe_position} index", "grid")

    plain = bytes(captures[records["plain"]["file"]])
    soft = bytes(captures[records["soft"]["file"]])
    for station in stations:
        for probe in station["probes"]:
            label = f"station {station['index']} probe {probe['index']}"
            x, y = probe["pixel"]
            _expect(x == math.trunc(probe["screen"][0]) and y == math.trunc(probe["screen"][1]),
                    f"{label} pixel is not the truncated screen position", "recorded-mismatch")
            _expect(0 <= x < CAPTURE_WIDTH and 0 <= y < CAPTURE_HEIGHT, f"{label} pixel outside the capture",
                    "recorded-mismatch")
            index = (y * CAPTURE_WIDTH + x) * 4
            _expect(probe["byteIndex"] == index, f"{label} byteIndex", "recorded-mismatch")
            plain_red, soft_red = plain[index], soft[index]
            _expect(probe["plainRed"] == plain_red and probe["shadowRed"] == soft_red,
                    f"{label} recorded red bytes differ from the captures", "recorded-mismatch")
            _expect(plain_red != 0, f"{label} plain red is zero", "recorded-mismatch")
            recorded = _f32_bits(probe["ratio"])
            _expect(recorded is not None and recorded == _f32_bits(soft_red / plain_red),
                    f"{label} float32 ratio bits differ from the capture bytes", "recorded-mismatch")
    return Evidence(document, sha256_hex(bytes(manifest_bytes)), plain, soft, STATIONS, RECORDED_PROBES)


@dataclass(frozen=True)
class AtlasRow:
    build: str
    phase: Tuple[float, float]
    source_commit: str
    active_row: int
    atlas_rows: int
    face_resolution: int
    sha256: str
    data: bytes
    binding_verified: bool


_ATLAS_FIELDS = (
    "schema", "schemaVersion", "build", "phase", "sourceCommit", "activeRow", "atlasRows", "atlasWidth",
    "atlasHeight", "faceResolution", "rowWidth", "rowHeight", "format", "bytes", "sha256", "binding",
)
_ATLAS_INTEGERS = ("activeRow", "atlasRows", "atlasWidth", "atlasHeight", "faceResolution", "rowWidth", "rowHeight",
                   "bytes")


def validate_atlas_row(
    sidecar: bytes,
    row: bytes,
    *,
    build: str,
    phase: Tuple[float, float],
    source_commit: str,
) -> AtlasRow:
    """Validate an active-atlas row and its sidecar against the expected build, phase and commit."""
    document = load_json(sidecar)
    _expect_object(document, _ATLAS_FIELDS, "atlas sidecar")
    _expect(document["schema"] == ATLAS_SIDECAR_SCHEMA and _is_int(document["schemaVersion"])
            and document["schemaVersion"] == COMPARISON_SCHEMA_VERSION, "atlas sidecar schema or version")
    _expect(document["build"] in BUILDS, f"atlas build {document['build']!r}")
    sidecar_phase = parse_phase(document["phase"])
    _expect(_is_hex(document["sourceCommit"], 40), "atlas sourceCommit must be 40 lowercase hex")
    for key in _ATLAS_INTEGERS:
        _expect(_is_int(document[key]), f"atlas {key} must be an integer")
    _expect(document["format"] == ATLAS_FORMAT, f"atlas format must be {ATLAS_FORMAT!r}")
    _expect(_is_hex(document["sha256"], 64), "atlas sha256 must be 64 lowercase hex")
    binding = document["binding"]
    _expect_object(binding, ("boundTextureIsAtlas", "check"), "atlas binding")
    _expect(isinstance(binding["boundTextureIsAtlas"], bool), "binding boundTextureIsAtlas must be a boolean")
    _expect(binding["check"] == ATLAS_BINDING_CHECK, f"binding check must be {ATLAS_BINDING_CHECK!r}")

    if (document["build"], document["sourceCommit"], sidecar_phase) != (build, source_commit, parse_phase(phase)):
        raise InputRefused("identity", f"sidecar names {document['build']} {sidecar_phase} "
                                       f"{document['sourceCommit']}, expected {build} {tuple(phase)} {source_commit}")
    face = document["faceResolution"]
    rows = document["atlasRows"]
    shape_ok = (
        face == FACE_RESOLUTION
        and rows >= 1
        and 0 <= document["activeRow"] < rows
        and document["atlasWidth"] == ATLAS_FACE_COLUMNS * face
        and document["atlasHeight"] == rows * face
        and document["rowWidth"] == document["atlasWidth"]
        and document["rowHeight"] == face
        and document["bytes"] == document["rowWidth"] * document["rowHeight"] * 4 == ATLAS_ROW_BYTES
    )
    _expect(shape_ok, "atlas width, height, rows, active row or byte count", "dimensions")
    _expect(len(row) == document["bytes"], f"atlas row has {len(row)} bytes, declared {document['bytes']}", "length")
    _expect(sha256_hex(bytes(row)) == document["sha256"], "atlas row digest", "sha256")
    return AtlasRow(
        build=document["build"],
        phase=sidecar_phase,
        source_commit=document["sourceCommit"],
        active_row=document["activeRow"],
        atlas_rows=rows,
        face_resolution=face,
        sha256=document["sha256"],
        data=bytes(row),
        binding_verified=binding["boundTextureIsAtlas"] is True,
    )


@dataclass(frozen=True)
class TrxResult:
    test_name: Optional[str]
    computer_name: Optional[str]
    outcome: Optional[str]


@dataclass(frozen=True)
class TrxRecord:
    results: Tuple[TrxResult, ...]
    counters: Mapping[str, int]
    outcome: Optional[str]


def parse_trx(raw: bytes) -> TrxRecord:
    """Parse a TRX document in the normal TeamTest namespace. No document type or entity is accepted."""
    _expect(b"<!DOCTYPE" not in raw and b"<!ENTITY" not in raw, "TRX may not declare a document type or entity")
    try:
        root = ElementTree.fromstring(bytes(raw))
    except ElementTree.ParseError as error:
        raise InputRefused("schema", f"malformed TRX: {error}") from None
    namespace = "{" + TRX_NAMESPACE + "}"
    _expect(root.tag == namespace + "TestRun", "TRX root must be a TeamTest TestRun")
    results = tuple(
        TrxResult(element.get("testName"), element.get("computerName"), element.get("outcome"))
        for element in root.iter(namespace + "UnitTestResult")
    )
    summary = root.find(namespace + "ResultSummary")
    counters_element = summary.find(namespace + "Counters") if summary is not None else None
    _expect(counters_element is not None, "TRX has no ResultSummary counters")
    counters: Dict[str, int] = {}
    for key, value in counters_element.attrib.items():
        _expect(_COUNTER.match(value) is not None, f"TRX counter {key}={value!r}")
        counters[key] = int(value)
    return TrxRecord(results, counters, summary.get("outcome"))


__all__: Sequence[str] = (
    "AtlasRow",
    "Bundle",
    "DEFAULT_LIMITS",
    "Evidence",
    "InputRefused",
    "ReadLimits",
    "TrxRecord",
    "TrxResult",
    "check_attestation",
    "evidence_capture_files",
    "is_safe_name",
    "load_json",
    "open_bundle",
    "open_zip_bytes",
    "parse_expectation",
    "parse_phase",
    "parse_trx",
    "read_capped",
    "read_file",
    "sha256_hex",
    "validate_atlas_row",
    "validate_evidence",
)
