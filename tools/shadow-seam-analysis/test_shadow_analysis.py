"""Stage 0 finite unittest inventory for the offline point-shadow seam analyzer.

Exactly 24 methods, one per row of the accepted Stage 0 brief. Fixtures are bounded and shared:
the committed retained artifact is read once into immutable bytes, synthetic captures and atlas
rows are module constants and every written file lives in a TemporaryDirectory. Synthetic claim
records are labelled synthetic and never pretend a render or a git inspection occurred.
Expected values are recorded data, design closed forms or hand-derived fixture facts, never
outputs of the code under test.
"""

from __future__ import annotations

import functools
import hashlib
import inspect
import io
import json
import math
import os
import struct
import tempfile
import unittest
import warnings
import zipfile
import zlib
from pathlib import Path

import analyze
import shadow_compare
import shadow_geometry
import shadow_inputs
import shadow_metrics
from shadow_compare import G6PhaseFacts, G7PhaseFacts, GateResult
from shadow_geometry import FloorRect, StationBand
from shadow_inputs import AtlasRow, InputRefused
from shadow_metrics import MetricInvalid

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
RETAINED_ZIP = REPO / "docs/superpowers/plans/proofs/2026-10-07-point-shadow-seam-artifact.zip"
RETAINED_SHA = "ef5ce8d9e72cf5f0d2638aedb0537e48dc80094b95ac73c5313b04d58a4743b5"
RETAINED_DIR = "_temp/point-shadow-seam-37482238342-1-55a8ed392a134a15baa7fc631f47c183/"
RETAINED_MANIFEST_SHA = "3033ce431b3d0cd9fc6c21071b3a895c0f50b7e0c0663189c93d4d7152acd4b3"
PLAIN_NAME = "point-shadow-seam.plain.rgba"
SOFT_NAME = "point-shadow-seam.soft.rgba"
DESIGN_SHA = "1c4ebca1d297405cb4b2f7b08f4ef585acc58946506f4a8d8569681499ea6a9d"
MUTATION_PATH = "KhaozEngine.Render3D/Internal/ShaderSources.Lighting.cs"
HISTORICAL_TEST = "KhaozEngine.Tests.Gpu.PointShadowFilterGpuTests.TheSoftEdgeCrossesACubeFaceBoundaryWithoutAStep"
COLLECTOR_TEST = "Synthetic.PointShadowControlCollector.RecordsFourFrozenPhasesWithoutAssertingTheBound"
SYNTHETIC_HOST = {"computerName": "synthetic-fixture-vm", "sameVm": True}
MUTATION_PIXEL = (95, 74)

PHASE_LITERAL = ((0.0, 0.0), (0.5, 0.0), (0.0, 0.5), (0.5, 0.5))
GATE_LITERAL = ("G1", "G2", "G3", "G4", "G5", "G6", "G7")
OUTCOME_LITERAL = (
    "INVALID",
    "INCONCLUSIVE-PHASE",
    "INCONCLUSIVE-UNDETECTED",
    "BASELINE-EXCEEDS-BOUND",
    "BASELINE-WITHIN-BOUND",
)
SCOPE_LITERAL = "diagnostic, this scene, these four phases, certified floor only"
NON_CLAIMS_LITERAL = [
    "no metric adoption",
    "no Catalog C waiver",
    "no shader-defect attribution",
    "no render-pass or Catalog-clear flag",
]
CLASSIFICATION_KEYS = {
    "metric",
    "outcome",
    "reason",
    "phaseRange",
    "separation",
    "separationGuard",
    "bumps",
    "scope",
    "nonClaims",
}
COMPARE_KEYS = {
    "tool", "mode", "status", "designSha256", "units", "parameters", "inputs", "claims", "rows",
    "historicalAssertion", "gates", "coverage", "differences", "classifications", "reportedOnly",
}

BAND_H = math.sqrt(3.25) / 40.0
ACROSS_LITERAL = (1.5 / math.sqrt(3.25), -1.0 / math.sqrt(3.25))
ALONG_LITERAL = (1.0 / math.sqrt(3.25), 1.5 / math.sqrt(3.25))

# Recorded output of the immutable 2026-10-07-point-shadow-seam-offline.py replay (offline.json).
OFFLINE_REACH = [
    0.9140384793281555,
    0.9109803438186646,
    0.9113315343856812,
    0.9041095972061157,
    0.8962090015411377,
    0.8900009989738464,
    0.9037500023841858,
    0.902481198310852,
    0.9043846130371094,
    0.9051968455314636,
    0.9070160984992981,
    0.9081594347953796,
    0.9135727286338806,
]
OFFLINE_RESIDUALS = [
    0.008491098880767822,
    0.005444347858428955,
    0.005806922912597656,
    -0.0014036893844604492,
    -0.009292900562286377,
    -0.015489578247070312,
    -0.0017291903495788574,
    -0.002986609935760498,
    -0.0010718703269958496,
    -0.00024825334548950195,
    0.001582324504852295,
    0.0027370452880859375,
    0.008161723613739014,
]
OFFLINE_BUMP = -0.008728723041713238
OFFLINE_WORST = 0.008491098880767822

# Shared immutable synthetic bytes. A 192 by 192 RGBA capture and one 256-resolution atlas row.
PLAIN_FIXTURE = bytes([155, 155, 155, 255]) * (192 * 192)
ATLAS_ROW = bytes(range(256)) * 6144
ATLAS_SHA = hashlib.sha256(ATLAS_ROW).hexdigest()

# Synthetic claim records. Identities are placeholder hex and the workflow names a fixture.
SYNTHETIC_BASE = {"sourceCommit": "b" * 40, "sourceTree": "c" * 40}
SYNTHETIC_MUTANT = {"sourceCommit": "d" * 40, "sourceTree": "e" * 40}
SYNTHETIC_MUTATION = {"changedPaths": [MUTATION_PATH], "diffSha256": "f" * 64}
SYNTHETIC_WORKFLOW = {
    "repository": "synthetic/fixture-not-a-real-run",
    "workflowPath": ".github/workflows/synthetic-fixture.yml",
    "ref": "refs/heads/synthetic-proof-fixture",
    "runId": 1,
    "runAttempt": 1,
    "jobId": 1,
}
APPROVED_DRAW_JSON = [
    {"name": "floor", "kind": "floor", "min": [-20.0, 0.0, -20.0], "max": [20.0, 0.0, 20.0]},
    {"name": "wall", "kind": "box", "min": [2.0, 0.0, -3.0], "max": [2.4, 3.0, 3.0]},
]


def _phase_dir(phase):
    return f"p{phase[0]:g}-{phase[1]:g}"


SYNTHETIC_EXPECTATION = {
    "schema": "khaozengine.point-shadow-control-expectation",
    "schemaVersion": 1,
    "designSha256": DESIGN_SHA,
    "baseline": SYNTHETIC_BASE,
    "mutant": SYNTHETIC_MUTANT,
    "mutation": SYNTHETIC_MUTATION,
    "workflow": SYNTHETIC_WORKFLOW,
    "host": SYNTHETIC_HOST,
    "expectedCollectorTestName": COLLECTOR_TEST,
    "phases": [list(p) for p in PHASE_LITERAL],
    "captures": [
        {
            "build": build,
            "phase": list(phase),
            "manifest": f"{build}/{_phase_dir(phase)}/point-shadow-seam.json",
            "atlasRow": f"{build}/{_phase_dir(phase)}/active-atlas-row.r32",
            "atlasSidecar": f"{build}/{_phase_dir(phase)}/active-atlas-row.json",
        }
        for build in ("B", "M")
        for phase in PHASE_LITERAL
    ],
    "trx": {"B": "B/seam.trx", "M": "M/seam.trx"},
    "attestation": "source-equivalence.json",
}
SYNTHETIC_ATTESTATION = {
    "schema": "khaozengine.point-shadow-source-equivalence",
    "schemaVersion": 1,
    "workflow": SYNTHETIC_WORKFLOW,
    "host": SYNTHETIC_HOST,
    "baseline": SYNTHETIC_BASE,
    "mutant": SYNTHETIC_MUTANT,
    "mutation": SYNTHETIC_MUTATION,
    "drawList": APPROVED_DRAW_JSON,
}

ALL_GATES_PASS = tuple(GateResult(g, True, "synthetic pass") for g in GATE_LITERAL)


# ---------------------------------------------------------------------------------------------
# Fixture helpers. None of them computes an expected analyzer result.


def _fresh(value):
    return json.loads(json.dumps(value))


@functools.lru_cache(maxsize=None)
def _retained():
    """Manifest, plain and soft bytes from the committed retained artifact, read once."""
    with zipfile.ZipFile(RETAINED_ZIP) as archive:
        manifest = archive.read(RETAINED_DIR + "point-shadow-seam.json")
        plain = archive.read(RETAINED_DIR + PLAIN_NAME)
        soft = archive.read(RETAINED_DIR + SOFT_NAME)
    return manifest, plain, soft


def _manifest():
    return json.loads(_retained()[0])


def _captures(plain=None, soft=None):
    _, retained_plain, retained_soft = _retained()
    return {
        PLAIN_NAME: retained_plain if plain is None else plain,
        SOFT_NAME: retained_soft if soft is None else soft,
    }


def _mutated_manifest(mutate):
    manifest = _manifest()
    mutate(manifest)
    return json.dumps(manifest).encode("utf-8")


def _f32_bits(value):
    return struct.pack("<f", value)


def _next_f32(value):
    bits = struct.unpack("<I", struct.pack("<f", value))[0]
    return struct.unpack("<f", struct.pack("<I", bits + 1))[0]


def _previous_f32(value):
    bits = struct.unpack("<I", struct.pack("<f", value))[0]
    return struct.unpack("<f", struct.pack("<I", bits - 1))[0]


def _adjacent_f64(value, step):
    """One double-precision neighbour of a positive fixture value, without a version-specific API."""
    bits = struct.unpack("<Q", struct.pack("<d", value))[0]
    return struct.unpack("<d", struct.pack("<Q", bits + step))[0]


def _flip(data, index, mask=0x01):
    changed = bytearray(data)
    changed[index] ^= mask
    return bytes(changed)


def _zip_bytes(entries, compression=zipfile.ZIP_STORED):
    buffer = io.BytesIO()
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        with zipfile.ZipFile(buffer, "w", compression) as archive:
            for name, data in entries:
                info = zipfile.ZipInfo(name)
                info.compress_type = compression
                archive.writestr(info, data)
    return buffer.getvalue()


def _patch_declared_size(data, size, prefix_crc=None):
    """Rewrite one entry's uncompressed size and optionally its CRC in both ZIP headers.
    Compressed size and the full actual payload stay untouched, including for DEFLATED entries."""
    patched = bytearray(data)
    local = patched.find(b"PK\x03\x04")
    central = patched.find(b"PK\x01\x02")
    if local < 0 or central <= local:
        raise AssertionError("fixture ZIP headers not found")
    struct.pack_into("<I", patched, local + 22, size)
    struct.pack_into("<I", patched, central + 24, size)
    if prefix_crc is not None:
        struct.pack_into("<I", patched, local + 14, prefix_crc)
        struct.pack_into("<I", patched, central + 16, prefix_crc)
    return bytes(patched)


def _unsupported_zip_method(data):
    patched = bytearray(data)
    struct.pack_into("<H", patched, patched.find(b"PK\x03\x04") + 8, 12)
    struct.pack_into("<H", patched, patched.find(b"PK\x01\x02") + 10, 12)
    return bytes(patched)


def _write_files(root, files):
    for name, data in files.items():
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)


class _CountingZeroStream(io.RawIOBase):
    """Finite zero stream that counts every byte it hands out, without a large allocation."""

    def __init__(self, total):
        super().__init__()
        self.total = total
        self.served = 0

    def readable(self):
        return True

    def readinto(self, buffer):
        count = min(len(buffer), self.total - self.served)
        if count <= 0:
            return 0
        buffer[:count] = bytes(count)
        self.served += count
        return count


def _shift_camera(camera, pixels):
    """Synthetic phase camera: the recorded orthographic camera translated on the floor by the
    frozen metres per pixel. Only the translation rows of view and viewProjection change."""
    dx = pixels[0] * 0.0187500
    dz = pixels[1] * 0.0192165
    shifted = _fresh(camera)
    for key in ("view", "viewProjection"):
        m = [float(v) for v in camera[key]]
        for column in range(4):
            m[12 + column] = m[12 + column] - (dx * m[column] + dz * m[8 + column])
        shifted[key] = m
    return shifted


def _series(values):
    return dict(zip(PHASE_LITERAL, values))


def _tiles(x0, x1, z0, z1, size):
    """Axis-aligned square tiles whose shared edges come from the same index arithmetic."""
    columns = int(round((x1 - x0) / size))
    rows = int(round((z1 - z0) / size))
    tiles = {}
    for kx in range(columns):
        for kz in range(rows):
            a, b = x0 + size * kx, x0 + size * (kx + 1)
            c, d = z0 + size * kz, z0 + size * (kz + 1)
            tiles[(kx, kz)] = ((a, c), (b, c), (b, d), (a, d))
    return tiles


def _synthetic_provenance_manifests():
    manifests = {}
    for build, identity in (("B", SYNTHETIC_BASE), ("M", SYNTHETIC_MUTANT)):
        for phase in PHASE_LITERAL:
            manifests[(build, phase)] = {
                "provenance": {
                    "sourceCommit": identity["sourceCommit"],
                    "sourceTree": identity["sourceTree"],
                    "revisionAgreement": "agree",
                    "assemblies": [{"name": "KhaozEngine.Render3D", "sourceRevision": identity["sourceCommit"]}],
                }
            }
    return manifests


def _atlas_sidecar(build="B", phase=(0.0, 0.0), commit=None, drop=(), **overrides):
    doc = {
        "schema": "khaozengine.point-shadow-active-atlas-row",
        "schemaVersion": 1,
        "build": build,
        "phase": list(phase),
        "sourceCommit": commit or (SYNTHETIC_BASE if build == "B" else SYNTHETIC_MUTANT)["sourceCommit"],
        "activeRow": 0,
        "atlasRows": 8,
        "atlasWidth": 1536,
        "atlasHeight": 2048,
        "faceResolution": 256,
        "rowWidth": 1536,
        "rowHeight": 256,
        "format": "R32Float little-endian IEEE754",
        "bytes": 1572864,
        "sha256": ATLAS_SHA,
        "binding": {"boundTextureIsAtlas": True, "check": "BoundPointShadowTexture is PointShadowTexture"},
    }
    doc.update(overrides)
    for key in drop:
        doc.pop(key)
    return json.dumps(doc).encode("utf-8")


def _atlas(build, phase, data=ATLAS_ROW, active_row=0, atlas_rows=8, binding=True):
    commit = (SYNTHETIC_BASE if build == "B" else SYNTHETIC_MUTANT)["sourceCommit"]
    sha = ATLAS_SHA if data is ATLAS_ROW else hashlib.sha256(data).hexdigest()
    return AtlasRow(build, phase, commit, active_row, atlas_rows, 256, sha, data, binding)


@functools.lru_cache(maxsize=4)
def _compare_phase_inputs(phase):
    """Prepare synthetic inputs, not expected analyzer outputs.

    The recorded floor mapping is x_screen = 53.3333376 X - 96 and
    y_screen = 52.03857984 Z - 185.0082528. Translate the recorded probe screens by
    the known affine camera displacement, then relocate their original four RGBA bytes.
    Every recorded world/on/from/to/across value, ratio, sum, reach and fit stays intact.
    The other pixels sample the analytic half-plane 1.5 X - Z = 0 at their centres.
    """
    manifest = _manifest()
    _, retained_plain, retained_soft = _retained()
    dx, dz = phase[0] * 0.0187500, phase[1] * 0.0192165
    screen_dx, screen_dy = dx * 53.3333376, dz * 52.03857984
    manifest["camera"] = _shift_camera(manifest["camera"], phase)
    manifest["camera"]["eye"][0] += dx
    manifest["camera"]["eye"][2] += dz
    manifest["scene"]["frameCentre"][0] += dx
    manifest["scene"]["frameCentre"][2] += dz

    plain = bytearray(bytes([200, 200, 200, 255]) * (192 * 192))
    soft = bytearray(plain)
    for j in range(192):
        z = (j + 0.5 + 185.0082528) / 52.03857984 + dz
        for i in range(192):
            x = (i + 0.5 + 96.0) / 53.3333376 + dx
            if 1.5 * x - z > 0.0:
                offset = (j * 192 + i) * 4
                soft[offset:offset + 4] = bytes([0, 0, 0, 255])

    occupied = set()
    for station in manifest["stations"]:
        for probe in station["probes"]:
            original_index = probe["byteIndex"]
            probe["screen"] = [probe["screen"][0] - screen_dx, probe["screen"][1] - screen_dy]
            probe["pixel"] = [int(probe["screen"][0]), int(probe["screen"][1])]
            pixel = tuple(probe["pixel"])
            if pixel in occupied or not (0 <= pixel[0] < 192 and 0 <= pixel[1] < 192):
                raise AssertionError("synthetic translated probe positions must be unique and in viewport")
            occupied.add(pixel)
            new_index = (pixel[1] * 192 + pixel[0]) * 4
            probe["byteIndex"] = new_index
            plain[new_index:new_index + 4] = retained_plain[original_index:original_index + 4]
            soft[new_index:new_index + 4] = retained_soft[original_index:original_index + 4]
    if len(occupied) != 793 or MUTATION_PIXEL in occupied:
        raise AssertionError("fixture mutation pixel must touch no translated probe")

    mutation_index = (MUTATION_PIXEL[1] * 192 + MUTATION_PIXEL[0]) * 4
    if soft[mutation_index] != 0 or plain[mutation_index] != 200:
        raise AssertionError("fixed mutation pixel must be analytic shadow with plain red 200")
    mutant = bytearray(soft)
    mutant[mutation_index:mutation_index + 4] = bytes([200, 200, 200, 255])
    return manifest, bytes(plain), bytes(soft), bytes(mutant)


def _synthetic_scene_manifests():
    return {(build, phase): _fresh(_compare_phase_inputs(phase)[0]) for build in ("B", "M") for phase in PHASE_LITERAL}


def _collector_trx(test_name=COLLECTOR_TEST, host="synthetic-fixture-vm", outcome="Passed", count=1):
    results = "".join(
        f'<UnitTestResult testName="{test_name}" computerName="{host}" outcome="{outcome}" '
        f'executionId="00000000-0000-0000-0000-{index + 1:012d}" '
        'testId="00000000-0000-0000-0000-000000000001" />'
        for index in range(count)
    )
    passed = count if outcome == "Passed" else 0
    skipped = count if outcome == "NotExecuted" else 0
    errors = count if outcome == "Error" else 0
    return (
        '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        f'<Results>{results}</Results><ResultSummary outcome="{outcome}">'
        f'<Counters total="{count}" executed="{count - skipped}" passed="{passed}" '
        f'failed="0" error="{errors}" notExecuted="{skipped}" notRunnable="0" />'
        '</ResultSummary></TestRun>'
    ).encode("utf-8")


def _compare_bundle(root):
    """An explicit 43-file, ten-directory bundle. Atlas payloads share one inode.
    All file preparation stays under the caller's TemporaryDirectory and mocks no analyzer behavior."""
    bundle = root / "bundle"
    bundle.mkdir()
    descriptor = root / "expectation.json"
    descriptor.write_bytes(json.dumps(SYNTHETIC_EXPECTATION).encode("utf-8"))
    (bundle / "source-equivalence.json").write_bytes(json.dumps(SYNTHETIC_ATTESTATION).encode("utf-8"))
    first_atlas = None
    for build, identity in (("B", SYNTHETIC_BASE), ("M", SYNTHETIC_MUTANT)):
        for phase in PHASE_LITERAL:
            template, plain, soft_b, soft_m = _compare_phase_inputs(phase)
            manifest = _fresh(template)
            manifest["provenance"].update(identity)
            manifest["provenance"]["revisionAgreement"] = "agree"
            for assembly in manifest["provenance"]["assemblies"]:
                assembly["sourceRevision"] = identity["sourceCommit"]
                version = assembly["informationalVersion"].split("+", 1)[0]
                assembly["informationalVersion"] = version + "+" + identity["sourceCommit"]
            soft = soft_b if build == "B" else soft_m
            for capture in manifest["captures"]:
                payload = plain if capture["name"] == "plain" else soft
                capture["sha256"] = hashlib.sha256(payload).hexdigest()
            directory = bundle / build / _phase_dir(phase)
            directory.mkdir(parents=True)
            _write_files(directory, {
                "point-shadow-seam.json": json.dumps(manifest).encode("utf-8"),
                PLAIN_NAME: plain,
                SOFT_NAME: soft,
                "active-atlas-row.json": _atlas_sidecar(build, phase),
            })
            row = directory / "active-atlas-row.r32"
            if first_atlas is None:
                row.write_bytes(ATLAS_ROW)
                first_atlas = row
            else:
                os.link(first_atlas, row)
        (bundle / build / "seam.trx").write_bytes(_collector_trx())
    return descriptor, bundle


def _replace_compare_capture(bundle, build, phase, name, data):
    """Keep a deliberately changed input's declaration/hash coherent so the target gate is reached."""
    directory = bundle / build / _phase_dir(phase)
    path = directory / name
    path.write_bytes(data)
    manifest_path = directory / "point-shadow-seam.json"
    manifest = json.loads(manifest_path.read_bytes())
    for capture in manifest["captures"]:
        if capture["file"] == name:
            capture["sha256"] = hashlib.sha256(data).hexdigest()
    manifest_path.write_bytes(json.dumps(manifest).encode("utf-8"))


def _bundle_hashes(bundle):
    return {str(path.relative_to(bundle)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in bundle.rglob("*") if path.is_file()}


def _change_json(path, mutate):
    document = json.loads(path.read_bytes())
    mutate(document)
    path.write_bytes(json.dumps(document).encode("utf-8"))


def _keys(value):
    if isinstance(value, dict):
        for key, item in value.items():
            yield key
            yield from _keys(item)
    elif isinstance(value, list):
        for item in value:
            yield from _keys(item)


class ShadowSeamAnalysisTests(unittest.TestCase):
    maxDiff = 4000

    def _refused(self, code, call, *args, **kwargs):
        with self.assertRaises(InputRefused) as caught:
            call(*args, **kwargs)
        self.assertEqual(caught.exception.code, code)

    # 1 -----------------------------------------------------------------------------------------
    def test_bundle_schema_and_resource_limits(self):
        self.assertEqual(shadow_inputs.MAX_ENTRIES, 64)
        self.assertEqual(shadow_inputs.MAX_TOTAL_BYTES, 64 * 1024 * 1024)
        self.assertEqual(shadow_inputs.MAX_MANIFEST_BYTES, 2 * 1024 * 1024)
        self.assertEqual(shadow_inputs.DEFAULT_LIMITS, shadow_inputs.ReadLimits(64, 64 * 1024 * 1024))
        small = shadow_inputs.ReadLimits(max_entries=3, max_total_bytes=1000)
        default = shadow_inputs.DEFAULT_LIMITS

        with tempfile.TemporaryDirectory() as tmp_name:
            tmp = Path(tmp_name)

            zip_cases = [
                ("zip-default-entry-count", _zip_bytes([(f"e{i:02d}.bin", b"x") for i in range(65)]), default, "entry-count"),
                ("zip-injected-entry-count", _zip_bytes([(f"e{i}.bin", b"x") for i in range(4)]), small, "entry-count"),
                (
                    "zip-declared-huge",
                    _patch_declared_size(_zip_bytes([("big.bin", b"x" * 16)]), 0x7FFF_FFF0),
                    default,
                    "declared-bytes",
                ),
                (
                    "zip-injected-declared",
                    _zip_bytes([("a.bin", b"a" * 600), ("b.bin", b"b" * 600)]),
                    small,
                    "declared-bytes",
                ),
                ("zip-duplicate", _zip_bytes([("a.json", b"1"), ("a.json", b"2")]), default, "duplicate-name"),
                ("zip-unsupported-method", _unsupported_zip_method(_zip_bytes([("s.bin", b"s" * 16)])), default, "zip-method"),
            ]
            for index, unsafe in enumerate(("../evil.json", "/abs.json", "a\\..\\b.json", "C:/drive.json")):
                zip_cases.append(
                    (f"zip-unsafe-{index}", _zip_bytes([("ok.json", b"{}"), (unsafe, b"{}")]), default, "unsafe-path")
                )
            for label, data, limits, code in zip_cases:
                path = tmp / f"{label}.zip"
                path.write_bytes(data)
                with self.subTest(label):
                    # Refusal happens at open, from metadata, before any payload read.
                    self._refused(code, shadow_inputs.open_bundle, path, limits)

            good_zip = tmp / "good.zip"
            good_zip.write_bytes(_zip_bytes([("m.json", b'{"a": 1}'), ("big.bin", b"z" * 600)]))
            with shadow_inputs.open_bundle(good_zip) as bundle:
                self.assertEqual(sorted(bundle.names()), ["big.bin", "m.json"])
                self.assertEqual(bundle.read("m.json", shadow_inputs.MAX_MANIFEST_BYTES), b'{"a": 1}')
                for name, code in (("big.bin", "entry-bytes"), ("../m.json", "unsafe-path"), ("absent.json", "missing-entry")):
                    with self.subTest(zip_read=name):
                        self._refused(code, bundle.read, name, 100)

            # Declared metadata says 32 bytes, the actual stream yields 16.
            short_zip = tmp / "short.zip"
            short_zip.write_bytes(_patch_declared_size(_zip_bytes([("s.bin", b"s" * 16)]), 32))
            with self.subTest("zip-actual-bytes-differ-from-declared"):
                with self.assertRaises(InputRefused) as caught:
                    with shadow_inputs.open_bundle(short_zip) as bundle:
                        bundle.read("s.bin", 1000)
                self.assertEqual(caught.exception.code, "entry-integrity")

            # Understated output must be detected even if a naive ZipExtFile would truncate
            # at 16 bytes and accept a CRC deliberately changed to match that prefix.
            payload = b"o" * 600
            prefix_crc = zlib.crc32(payload[:16]) & 0xFFFF_FFFF
            for compression in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                for label, crc in (("full-crc", None), ("prefix-crc", prefix_crc)):
                    path = tmp / f"overrun-{compression}-{label}.zip"
                    data = _patch_declared_size(_zip_bytes([("o.bin", payload)], compression), 16, crc)
                    path.write_bytes(data)
                    with self.subTest(compression=compression, understated=label):
                        with self.assertRaises(InputRefused) as caught:
                            with shadow_inputs.open_bundle(path) as bundle:
                                bundle.read("o.bin", 1000)
                        self.assertEqual(caught.exception.code, "entry-integrity")
                    if crc is not None:
                        for cap, limits, read_limit in (
                            ("read", default, 100),
                            ("aggregate", shadow_inputs.ReadLimits(3, 100), 1000),
                        ):
                            with self.subTest(compression=compression, actual_cap=cap):
                                with self.assertRaises(InputRefused) as caught:
                                    with shadow_inputs.open_bundle(path, limits) as bundle:
                                        bundle.read("o.bin", read_limit)
                                self.assertEqual(caught.exception.code, "actual-bytes")

            # The streaming cap stops reading as soon as actual bytes exceed the limit.
            stream = _CountingZeroStream(4 * 1024 * 1024)
            self._refused("actual-bytes", shadow_inputs.read_capped, stream, 1000)
            self.assertLessEqual(stream.served, 1000 + shadow_inputs.READ_CHUNK_BYTES)
            self.assertEqual(shadow_inputs.read_capped(_CountingZeroStream(500), 1000), bytes(500))

            outside = tmp / "outside.json"
            outside.write_bytes(b"{}")
            outside_dir = tmp / "outside-dir"
            outside_dir.mkdir()
            (outside_dir / "x.json").write_bytes(b"{}")
            root_link = tmp / "root-link"
            os.symlink(outside_dir, root_link)
            with self.subTest("directory-root-itself-is-symlink"):
                self._refused("symlink", shadow_inputs.open_bundle, root_link)

            def file_link(root):
                _write_files(root, {"ok.json": b"{}"})
                os.symlink(outside, root / "link.json")

            def dir_link(root):
                _write_files(root, {"ok.json": b"{}"})
                os.symlink(outside_dir, root / "sub")

            dir_cases = [
                ("dir-default-entry-count", lambda r: _write_files(r, {f"e{i:02d}.bin": b"" for i in range(65)}), default, "entry-count"),
                ("dir-injected-entry-count", lambda r: _write_files(r, {f"e{i}.bin": b"x" for i in range(4)}), small, "entry-count"),
                ("dir-injected-declared", lambda r: _write_files(r, {"a.bin": b"a" * 600, "b.bin": b"b" * 600}), small, "declared-bytes"),
                ("dir-file-symlink", file_link, default, "symlink"),
                ("dir-directory-symlink", dir_link, default, "symlink"),
            ]
            for label, build, limits, code in dir_cases:
                root = tmp / label
                root.mkdir()
                build(root)
                with self.subTest(label):
                    self._refused(code, shadow_inputs.open_bundle, root, limits)

            good_dir = tmp / "good-dir"
            good_dir.mkdir()
            _write_files(good_dir, {"B/m.json": b'{"a": 1}', "big.bin": b"z" * 600})
            with shadow_inputs.open_bundle(good_dir) as bundle:
                self.assertEqual(sorted(bundle.names()), ["B/m.json", "big.bin"])
                self.assertEqual(bundle.read("B/m.json", shadow_inputs.MAX_MANIFEST_BYTES), b'{"a": 1}')
                for name, code in (("big.bin", "entry-bytes"), ("../outside.json", "unsafe-path"), ("absent.json", "missing-entry")):
                    with self.subTest(dir_read=name):
                        self._refused(code, bundle.read, name, 100)

        # Required schema for the expected-proof descriptor and the evidence manifest.
        good = json.dumps(SYNTHETIC_EXPECTATION).encode("utf-8")
        parsed = shadow_inputs.parse_expectation(good)
        self.assertEqual(parsed["designSha256"], DESIGN_SHA)
        self.assertEqual(len(parsed["captures"]), 8)

        def captures_alias(doc):
            doc["captures"][1]["manifest"] = doc["captures"][0]["manifest"]

        schema_cases = [
            ("wrong-schema", lambda d: d.__setitem__("schema", "khaozengine.other"), "schema"),
            ("wrong-version", lambda d: d.__setitem__("schemaVersion", 2), "schema"),
            ("collector-name-missing", lambda d: d.pop("expectedCollectorTestName"), "schema"),
            ("workflow-ref-missing", lambda d: d["workflow"].pop("ref"), "schema"),
            ("host-missing", lambda d: d.pop("host"), "schema"),
            ("missing-captures", lambda d: d.pop("captures"), "schema"),
            ("unknown-field", lambda d: d.__setitem__("extra", 1), "schema"),
            ("duplicate-capture-path", captures_alias, "duplicate-name"),
            ("unsafe-capture-path", lambda d: d["captures"][0].__setitem__("atlasRow", "../escape.r32"), "unsafe-path"),
        ]
        for label, mutate, code in schema_cases:
            doc = _fresh(SYNTHETIC_EXPECTATION)
            mutate(doc)
            with self.subTest(label):
                self._refused(code, shadow_inputs.parse_expectation, json.dumps(doc).encode("utf-8"))
        with self.subTest("non-finite-token"):
            raw = good.replace(b'"runAttempt": 1', b'"runAttempt": NaN')
            self.assertNotEqual(raw, good)
            self._refused("non-finite", shadow_inputs.parse_expectation, raw)
        with self.subTest("evidence-schema-version"):
            manifest = _mutated_manifest(lambda m: m.__setitem__("schemaVersion", 2))
            self._refused("schema", shadow_inputs.validate_evidence, manifest, _captures())

    # 2 -----------------------------------------------------------------------------------------
    def test_capture_dimensions_lengths_and_hashes(self):
        manifest, plain, soft = _retained()
        self.assertEqual(len(plain), 147_456)
        self.assertEqual(len(soft), 147_456)

        def capture_field(name, field, value):
            def mutate(m):
                for capture in m["captures"]:
                    if capture["name"] == name:
                        capture[field] = value

            return mutate

        missing_soft = _captures()
        missing_soft.pop(SOFT_NAME)
        cases = [
            ("soft-truncated", manifest, _captures(soft=soft[:-4]), "length"),
            ("width-191", _mutated_manifest(capture_field("soft", "width", 191)), _captures(), "dimensions"),
            ("declared-bytes-wrong", _mutated_manifest(capture_field("plain", "bytes", 147_452)), _captures(), "dimensions"),
            ("soft-byte-changed", manifest, _captures(soft=_flip(soft, 0)), "sha256"),
            ("soft-missing", manifest, missing_soft, "missing-entry"),
        ]
        for label, manifest_bytes, captures, code in cases:
            with self.subTest(label):
                self._refused(code, shadow_inputs.validate_evidence, manifest_bytes, captures)

        valid = shadow_inputs.validate_atlas_row(
            _atlas_sidecar(), ATLAS_ROW, build="B", phase=(0.0, 0.0), source_commit=SYNTHETIC_BASE["sourceCommit"]
        )
        self.assertEqual(valid.sha256, ATLAS_SHA)
        self.assertEqual(len(valid.data), 1_572_864)
        atlas_cases = [
            ("atlas-truncated", _atlas_sidecar(), ATLAS_ROW[:-4], "length"),
            ("atlas-row-height", _atlas_sidecar(rowHeight=255), ATLAS_ROW, "dimensions"),
            ("atlas-row-width", _atlas_sidecar(rowWidth=1280), ATLAS_ROW, "dimensions"),
            ("atlas-byte-changed", _atlas_sidecar(), _flip(ATLAS_ROW, 100), "sha256"),
        ]
        for label, sidecar, row, code in atlas_cases:
            with self.subTest(label):
                self._refused(
                    code,
                    shadow_inputs.validate_atlas_row,
                    sidecar,
                    row,
                    build="B",
                    phase=(0.0, 0.0),
                    source_commit=SYNTHETIC_BASE["sourceCommit"],
                )

    # 3 -----------------------------------------------------------------------------------------
    def test_grid_and_recorded_byte_integrity(self):
        manifest, plain, soft = _retained()
        evidence = shadow_inputs.validate_evidence(manifest, _captures())
        self.assertEqual(evidence.station_count, 13)
        self.assertEqual(evidence.probe_count, 793)
        self.assertEqual(evidence.plain, plain)
        self.assertEqual(evidence.soft, soft)
        self.assertEqual(evidence.manifest_sha256, RETAINED_MANIFEST_SHA)

        def probe(m, station=2, index=10):
            return m["stations"][station]["probes"][index]

        def swap_probe_indices(m):
            probes = m["stations"][0]["probes"]
            probes[1]["index"], probes[2]["index"] = probes[2]["index"], probes[1]["index"]

        def bump_byte(field):
            def mutate(m):
                probe(m)[field] = (probe(m)[field] + 1) % 256

            return mutate

        def shift_pixel(m):
            probe(m)["pixel"][0] += 1

        cases = [
            ("probe-removed", lambda m: m["stations"][5]["probes"].pop(30), "grid"),
            ("station-removed", lambda m: m["stations"].pop(), "grid"),
            ("station-index", lambda m: m["stations"][7].__setitem__("index", 8), "grid"),
            ("probe-index", swap_probe_indices, "grid"),
            ("grid-stations", lambda m: m["grid"].__setitem__("stations", 14), "grid"),
            ("recorded-probes", lambda m: m.__setitem__("recordedProbes", 792), "grid"),
            ("plain-red", bump_byte("plainRed"), "recorded-mismatch"),
            ("shadow-red", bump_byte("shadowRed"), "recorded-mismatch"),
            ("pixel-not-truncated-screen", shift_pixel, "recorded-mismatch"),
            ("byte-index", lambda m: probe(m).__setitem__("byteIndex", probe(m)["byteIndex"] + 4), "recorded-mismatch"),
            ("ratio-bits", lambda m: probe(m).__setitem__("ratio", _next_f32(probe(m)["ratio"])), "recorded-mismatch"),
            ("ratio-nan", lambda m: probe(m).__setitem__("ratio", float("nan")), "non-finite"),
            ("screen-infinite", lambda m: probe(m)["screen"].__setitem__(0, float("inf")), "non-finite"),
        ]
        for label, mutate, code in cases:
            with self.subTest(label):
                self._refused(code, shadow_inputs.validate_evidence, _mutated_manifest(mutate), _captures())

    # 4 -----------------------------------------------------------------------------------------
    def test_original_float32_replay(self):
        manifest_bytes, _, _ = _retained()
        recorded = json.loads(manifest_bytes)
        evidence = shadow_inputs.validate_evidence(manifest_bytes, _captures())
        reaches = shadow_metrics.original_station_reaches(evidence)
        self.assertEqual(len(reaches), 13)
        for station, reach in zip(recorded["stations"], reaches):
            with self.subTest(station=station["index"]):
                self.assertEqual(reach.index, station["index"])
                self.assertEqual(_f32_bits(reach.sum), _f32_bits(station["sum"]))
                self.assertEqual(_f32_bits(reach.reach), _f32_bits(station["reach"]))
        self.assertEqual([r.reach for r in reaches], OFFLINE_REACH)

        statistic = shadow_metrics.detrend_statistic([r.reach for r in reaches], single_precision=True)
        self.assertEqual(statistic.bump, OFFLINE_BUMP)
        self.assertEqual(statistic.worst_elsewhere, OFFLINE_WORST)
        self.assertEqual(list(statistic.residuals), OFFLINE_RESIDUALS)
        self.assertEqual(_f32_bits(statistic.bump), _f32_bits(recorded["statistic"]["bump"]))
        self.assertEqual(_f32_bits(statistic.worst_elsewhere), _f32_bits(recorded["statistic"]["worstElsewhere"]))
        self.assertEqual(
            [_f32_bits(v) for v in statistic.residuals], [_f32_bits(v) for v in recorded["fit"]["residuals"]]
        )

        # A recorded station sum one float32 step away no longer replays.
        tampered = _mutated_manifest(
            lambda m: m["stations"][6].__setitem__("sum", _next_f32(m["stations"][6]["sum"]))
        )
        tampered_evidence = shadow_inputs.validate_evidence(tampered, _captures())
        with self.assertRaises(MetricInvalid) as caught:
            shadow_metrics.original_station_reaches(tampered_evidence)
        self.assertEqual(caught.exception.code, "replay-mismatch")

    # 5 -----------------------------------------------------------------------------------------
    def test_provenance_and_mutation_attestation(self):
        accepted = shadow_compare.gate_g1_provenance(
            _fresh(SYNTHETIC_EXPECTATION), _fresh(SYNTHETIC_ATTESTATION), _synthetic_provenance_manifests()
        )
        self.assertEqual((accepted.gate, accepted.passed), ("G1", True))

        def case(expect=None, attest=None, manifests=None, no_attestation=False):
            expectation = _fresh(SYNTHETIC_EXPECTATION)
            attestation = _fresh(SYNTHETIC_ATTESTATION)
            records = _synthetic_provenance_manifests()
            if expect:
                expect(expectation)
            if attest:
                attest(attestation)
            if manifests:
                manifests(records)
            return expectation, None if no_attestation else attestation, records

        def identical_trees(expectation):
            expectation["mutant"] = dict(expectation["baseline"])

        def identical_trees_attested(attestation):
            attestation["mutant"] = dict(attestation["baseline"])

        def m_manifests_as_b(records):
            for phase in PHASE_LITERAL:
                records[("M", phase)] = records[("B", phase)]

        cases = {
            "attestation-missing": case(no_attestation=True),
            "attestation-without-mutation": case(attest=lambda a: a.pop("mutation")),
            "attested-mutant-commit": case(attest=lambda a: a["mutant"].__setitem__("sourceCommit", "9" * 40)),
            "attested-extra-changed-path": case(
                attest=lambda a: a["mutation"]["changedPaths"].append("KhaozEngine.Render3D/ModelRenderer.cs")
            ),
            "attested-diff-hash": case(attest=lambda a: a["mutation"].__setitem__("diffSha256", "0" * 64)),
            "attested-workflow-run": case(attest=lambda a: a["workflow"].__setitem__("runId", 2)),
            "attested-workflow-ref": case(attest=lambda a: a["workflow"].__setitem__("ref", "refs/heads/other")),
            "attested-workflow-ref-missing": case(attest=lambda a: a["workflow"].pop("ref")),
            "attested-workflow-job": case(attest=lambda a: a["workflow"].__setitem__("jobId", 2)),
            "attested-workflow-attempt": case(attest=lambda a: a["workflow"].__setitem__("runAttempt", 2)),
            "attested-workflow-path": case(attest=lambda a: a["workflow"].__setitem__("workflowPath", "other.yml")),
            "attested-workflow-repository": case(attest=lambda a: a["workflow"].__setitem__("repository", "synthetic/other")),
            "attested-host-missing": case(attest=lambda a: a.pop("host")),
            "attested-host-name": case(attest=lambda a: a["host"].__setitem__("computerName", "other-host")),
            "same-vm-claim-false": case(attest=lambda a: a["host"].__setitem__("sameVm", False)),
            "expected-path-not-frozen": case(
                expect=lambda e: e["mutation"].__setitem__("changedPaths", ["KhaozEngine.Render3D/Other.cs"]),
                attest=lambda a: a["mutation"].__setitem__("changedPaths", ["KhaozEngine.Render3D/Other.cs"]),
            ),
            "design-digest": case(expect=lambda e: e.__setitem__("designSha256", "0" * 64)),
            "identical-trees": case(expect=identical_trees, attest=identical_trees_attested, manifests=m_manifests_as_b),
            "manifest-build-swapped": case(
                manifests=lambda r: r[("M", (0.5, 0.0))]["provenance"].__setitem__("sourceCommit", "b" * 40)
            ),
            "assembly-revision": case(
                manifests=lambda r: r[("B", (0.0, 0.5))]["provenance"]["assemblies"][0].__setitem__(
                    "sourceRevision", "a" * 40
                )
            ),
            "revision-disagree": case(
                manifests=lambda r: r[("B", (0.0, 0.0))]["provenance"].__setitem__("revisionAgreement", "disagree")
            ),
            "manifest-missing": case(manifests=lambda r: r.pop(("M", (0.5, 0.5)))),
        }
        for label, (expectation, attestation, manifests) in cases.items():
            with self.subTest(label):
                result = shadow_compare.gate_g1_provenance(expectation, attestation, manifests)
                self.assertEqual(result.gate, "G1")
                self.assertFalse(result.passed)
                self.assertTrue(result.reason)

    # 6 -----------------------------------------------------------------------------------------
    def test_exact_phase_inventory_and_achieved_phase(self):
        phases = [list(p) for p in PHASE_LITERAL]
        captures = [(build, list(p)) for build in ("B", "M") for p in PHASE_LITERAL]
        self.assertTrue(shadow_compare.gate_phase_inventory(phases, captures).passed)
        substituted = [list(p) for p in PHASE_LITERAL[:3]] + [[0.25, 0.25]]
        inventory_cases = {
            "duplicate-capture": (phases, captures + [("B", [0.0, 0.0])]),
            "missing-capture": (phases, [c for c in captures if c != ("M", [0.5, 0.5])]),
            "extra-phase-capture": (phases, captures + [("B", [0.25, 0.0]), ("M", [0.25, 0.0])]),
            "unknown-build": (phases, captures + [("X", [0.0, 0.0])]),
            "expected-phase-substituted": (
                substituted,
                [(build, list(p)) for build in ("B", "M") for p in substituted],
            ),
            "expected-phase-duplicated": (phases + [[0.0, 0.0]], captures),
        }
        for label, (expected, captured) in inventory_cases.items():
            with self.subTest(label):
                self.assertFalse(shadow_compare.gate_phase_inventory(expected, captured).passed)

        retained = _manifest()["camera"]
        on_point = _manifest()["stations"][4]["on"]
        for phase in PHASE_LITERAL:
            with self.subTest(achieved=phase):
                achieved = shadow_geometry.achieved_phase(retained, _shift_camera(retained, phase), on_point)
                self.assertAlmostEqual(achieved[0], phase[0], delta=1e-4)
                self.assertAlmostEqual(achieved[1], phase[1], delta=1e-4)

        def cameras(**overrides):
            result = {p: _shift_camera(retained, p) for p in PHASE_LITERAL}
            result[(0.0, 0.0)] = _fresh(retained)
            result.update(overrides)
            return result

        accepted = shadow_compare.gate_g4_camera(cameras(), retained, on_point)
        self.assertEqual((accepted.gate, accepted.passed), ("G4", True))
        # The sign of the shift is irrelevant to the frozen set.
        mirrored_cameras = {
            **cameras(),
            (0.5, 0.0): _shift_camera(retained, (-0.5, 0.0)),
            (0.0, 0.5): _shift_camera(retained, (0.0, -0.5)),
        }
        self.assertTrue(shadow_compare.gate_g4_camera(mirrored_cameras, retained, on_point).passed)

        def nudged_vp():
            camera = _fresh(retained)
            camera["viewProjection"][0] = _next_f32(camera["viewProjection"][0])
            return camera

        def scaled(key, index, phase, factor):
            camera = _shift_camera(retained, phase)
            camera[key][index] = camera[key][index] * factor
            return camera

        def moved_origin():
            camera = _shift_camera(retained, (0.5, 0.5))
            camera["renderOrigin"] = [0.1, 0.0, 0.0]
            return camera

        g4_cases = {
            "achieved-phase-off-by-0.03px": {**cameras(), (0.5, 0.0): _shift_camera(retained, (0.53, 0.0))},
            "zero-phase-vp-one-ulp": {**cameras(), (0.0, 0.0): nudged_vp()},
            "projection-relative-1e-5": {**cameras(), (0.0, 0.5): scaled("projection", 0, (0.0, 0.5), 1.0 + 1e-5)},
            "view-rotation-relative-1e-5": {**cameras(), (0.5, 0.5): scaled("view", 5, (0.5, 0.5), 1.0 + 1e-5)},
            "render-origin": {**cameras(), (0.5, 0.5): moved_origin()},
            "missing-phase": {p: c for p, c in cameras().items() if p != (0.5, 0.5)},
        }
        for label, phase_cameras in g4_cases.items():
            with self.subTest(label):
                result = shadow_compare.gate_g4_camera(phase_cameras, retained, on_point)
                self.assertEqual(result.gate, "G4")
                self.assertFalse(result.passed)

    # 7 -----------------------------------------------------------------------------------------
    def test_active_atlas_identity(self):
        commit_b = SYNTHETIC_BASE["sourceCommit"]
        row_b = shadow_inputs.validate_atlas_row(
            _atlas_sidecar(), ATLAS_ROW, build="B", phase=(0.0, 0.0), source_commit=commit_b
        )
        self.assertEqual(
            (row_b.build, row_b.phase, row_b.source_commit, row_b.active_row, row_b.atlas_rows, row_b.face_resolution),
            ("B", (0.0, 0.0), commit_b, 0, 8, 256),
        )
        self.assertTrue(row_b.binding_verified)
        self.assertEqual(row_b.data, ATLAS_ROW)

        sidecar_cases = [
            ("big-endian", _atlas_sidecar(format="R32Float big-endian IEEE754"), "schema"),
            ("binding-missing", _atlas_sidecar(drop=("binding",)), "schema"),
            ("wrong-build", _atlas_sidecar(build="M", commit=commit_b), "identity"),
            ("wrong-commit", _atlas_sidecar(commit="9" * 40), "identity"),
            ("wrong-phase", _atlas_sidecar(phase=(0.5, 0.0)), "identity"),
            ("active-row-out-of-range", _atlas_sidecar(activeRow=8), "dimensions"),
            ("atlas-width-not-six-faces", _atlas_sidecar(atlasWidth=1280), "dimensions"),
            ("atlas-height-not-declared-rows", _atlas_sidecar(atlasHeight=2304), "dimensions"),
        ]
        for label, sidecar, code in sidecar_cases:
            with self.subTest(label):
                self._refused(
                    code,
                    shadow_inputs.validate_atlas_row,
                    sidecar,
                    ATLAS_ROW,
                    build="B",
                    phase=(0.0, 0.0),
                    source_commit=commit_b,
                )

        unbound = shadow_inputs.validate_atlas_row(
            _atlas_sidecar(
                build="M",
                phase=(0.0, 0.5),
                binding={"boundTextureIsAtlas": False, "check": "BoundPointShadowTexture is PointShadowTexture"},
            ),
            ATLAS_ROW,
            build="M",
            phase=(0.0, 0.5),
            source_commit=SYNTHETIC_MUTANT["sourceCommit"],
        )
        self.assertFalse(unbound.binding_verified)

        plain = {p: (PLAIN_FIXTURE, PLAIN_FIXTURE) for p in PHASE_LITERAL}

        def atlas():
            return {p: (_atlas("B", p), _atlas("M", p)) for p in PHASE_LITERAL}

        accepted = shadow_compare.gate_g5_matched_data(plain, atlas())
        self.assertEqual((accepted.gate, accepted.passed), ("G5", True))
        g5_cases = {
            "row-bytes-differ": {
                **atlas(),
                (0.5, 0.5): (_atlas("B", (0.5, 0.5)), _atlas("M", (0.5, 0.5), data=_flip(ATLAS_ROW, 4096))),
            },
            "active-row-differs": {**atlas(), (0.0, 0.0): (_atlas("B", (0.0, 0.0)), _atlas("M", (0.0, 0.0), active_row=1))},
            "row-count-differs": {**atlas(), (0.5, 0.0): (_atlas("B", (0.5, 0.0)), _atlas("M", (0.5, 0.0), atlas_rows=9))},
            "binding-unverified": {**atlas(), (0.0, 0.5): (_atlas("B", (0.0, 0.5)), unbound)},
            "phase-mislabelled": {**atlas(), (0.0, 0.0): (_atlas("B", (0.0, 0.0)), _atlas("M", (0.5, 0.0)))},
            "missing-phase": {p: v for p, v in atlas().items() if p != (0.5, 0.0)},
        }
        for label, pairs in g5_cases.items():
            with self.subTest(label):
                result = shadow_compare.gate_g5_matched_data(plain, pairs)
                self.assertEqual(result.gate, "G5")
                self.assertFalse(result.passed)

    # 8 -----------------------------------------------------------------------------------------
    def test_plain_capture_identity(self):
        atlas = {p: (_atlas("B", p), _atlas("M", p)) for p in PHASE_LITERAL}
        same = {p: (PLAIN_FIXTURE, PLAIN_FIXTURE) for p in PHASE_LITERAL}
        accepted = shadow_compare.gate_g5_matched_data(same, atlas)
        self.assertEqual((accepted.gate, accepted.passed), ("G5", True))
        pixel = (100 * 192 + 100) * 4
        cases = {
            "red-byte": {**same, (0.0, 0.5): (PLAIN_FIXTURE, _flip(PLAIN_FIXTURE, pixel))},
            "alpha-byte-only": {**same, (0.5, 0.5): (_flip(PLAIN_FIXTURE, pixel + 3), PLAIN_FIXTURE)},
            "length": {**same, (0.5, 0.0): (PLAIN_FIXTURE, PLAIN_FIXTURE[:-4])},
            "missing-phase": {p: v for p, v in same.items() if p != (0.0, 0.0)},
        }
        for label, plain in cases.items():
            with self.subTest(label):
                result = shadow_compare.gate_g5_matched_data(plain, atlas)
                self.assertEqual(result.gate, "G5")
                self.assertFalse(result.passed)

    # 9 -----------------------------------------------------------------------------------------
    def test_visible_floor_geometry_certificate(self):
        # The certificate never sees pixel colour.
        self.assertEqual(list(inspect.signature(shadow_geometry.certify_visible_floor).parameters), ["camera", "draws"])
        camera = _manifest()["camera"]
        certificate = shadow_geometry.certify_visible_floor(camera, _fresh(APPROVED_DRAW_JSON))
        self.assertTrue(certificate.valid, certificate.reason)
        # Hand-derived from the recorded matrix: screen x = 53.333336 X - 96, screen y = 52.03858 Z - 185.00825.
        for pixel in ((9, 11), (9, 100), (95, 74), (182, 185)):
            self.assertIn(pixel, certificate.certified_pixels)
        for pixel in ((0, 0), (7, 100), (95, 5), (100, 191), (191, 100)):
            self.assertNotIn(pixel, certificate.certified_pixels)
        self.assertTrue(all(8 <= i <= 184 and 10 <= j <= 187 for i, j in certificate.certified_pixels))

        def perspective():
            c = _fresh(camera)
            c["projection"][11] = -1.0
            c["projection"][15] = 0.0
            return c

        def mirrored_z():
            c = _fresh(camera)
            for key in ("view", "viewProjection"):
                for index in range(8, 12):
                    c[key][index] = -float(c[key][index])
            return c

        def off_viewport():
            c = _fresh(camera)
            c["viewProjection"][12] = float(c["viewProjection"][12]) + 1.0
            return c

        draws = _fresh(APPROVED_DRAW_JSON)
        crate = draws + [{"name": "crate", "kind": "box", "min": [3.0, 0.0, 5.0], "max": [3.5, 0.5, 5.5]}]
        moved_wall = _fresh(APPROVED_DRAW_JSON)
        moved_wall[1]["max"] = [2.4, 3.0, 4.0]
        cases = {
            "not-orthographic": (perspective(), draws),
            "ray-direction-z-negative": (mirrored_z(), draws),
            "corner-outside-viewport": (off_viewport(), draws),
            "extra-occluder": (_fresh(camera), crate),
            "wall-transform-changed": (_fresh(camera), moved_wall),
            "floor-draw-missing": (_fresh(camera), draws[1:]),
        }
        for label, (case_camera, case_draws) in cases.items():
            with self.subTest(label):
                result = shadow_geometry.certify_visible_floor(case_camera, case_draws)
                self.assertFalse(result.valid)
                self.assertTrue(result.reason)

    # 10 ----------------------------------------------------------------------------------------
    def test_every_measurement_footprint_is_certified(self):
        manifest = _manifest()
        camera = manifest["camera"]
        certificate = shadow_geometry.certify_visible_floor(camera, _fresh(APPROVED_DRAW_JSON))
        probes = [tuple(p["pixel"]) for s in manifest["stations"] for p in s["probes"]]
        self.assertEqual(len(probes), 793)
        bands = [shadow_geometry.station_band((s["on"][0], s["on"][2])) for s in manifest["stations"]]
        controls = [FloorRect(2.0, 2.4, 6.7, 7.0), FloorRect(4.8, 5.2, 6.8, 7.1)]
        accepted = shadow_geometry.check_measurement_coverage(camera, certificate, probes, bands, controls)
        self.assertTrue(accepted.valid, accepted.reason)
        self.assertEqual(accepted.uncovered, ())

        moved_band = shadow_geometry.station_band((2.0, 4.35))
        cases = {
            "probe-outside-F": (certificate, probes + [(0, 0)], bands, controls, ("probe", (0, 0))),
            "band-partly-outside-F": (certificate, probes, [moved_band] + bands[1:], controls, ("band", 0)),
            "control-partly-outside-F": (
                certificate,
                probes,
                bands,
                [FloorRect(1.9, 2.4, 6.7, 7.0), controls[1]],
                ("control", 0),
            ),
            "control-edge-beyond-F": (
                certificate,
                probes,
                bands,
                [controls[0], FloorRect(4.8, 5.3, 6.8, 7.1)],
                ("control", 1),
            ),
        }
        for label, (cert, case_probes, case_bands, case_controls, expected) in cases.items():
            with self.subTest(label):
                result = shadow_geometry.check_measurement_coverage(camera, cert, case_probes, case_bands, case_controls)
                self.assertFalse(result.valid)
                self.assertIn(expected, result.uncovered)

        perspective = _fresh(camera)
        perspective["projection"][11] = -1.0
        perspective["projection"][15] = 0.0
        uncertified = shadow_geometry.certify_visible_floor(perspective, _fresh(APPROVED_DRAW_JSON))
        with self.subTest("uncertified-floor"):
            self.assertFalse(
                shadow_geometry.check_measurement_coverage(camera, uncertified, probes, bands, controls).valid
            )

    # 11 ----------------------------------------------------------------------------------------
    def test_noncertified_differences_are_reported(self):
        changed = bytearray(PLAIN_FIXTURE)
        for (i, j), channel in (((100, 100), 0), ((9, 100), 0), ((0, 0), 0), ((191, 191), 3)):
            changed[(j * 192 + i) * 4 + channel] ^= 0x01
        soft_m = bytes(changed)
        certified = frozenset({(100, 100), (9, 100), (50, 50)})
        mask = frozenset({(100, 100), (0, 0)})

        report = shadow_compare.classify_pixel_differences(PLAIN_FIXTURE, soft_m, certified, mask)
        self.assertEqual(report.certified_inside_mask, ((100, 100),))
        self.assertEqual(report.certified_outside_mask, ((9, 100),))
        self.assertEqual(report.outside_certified, ((0, 0), (191, 191)))
        self.assertEqual(
            _fresh(shadow_compare.difference_record(report)),
            {
                "certifiedInsideMask": {"count": 1, "pixels": [[100, 100]]},
                "certifiedOutsideMask": {"count": 1, "pixels": [[9, 100]]},
                "outside-certified-floor-region": {"count": 2, "pixels": [[0, 0], [191, 191]]},
            },
        )
        unchanged = shadow_compare.classify_pixel_differences(PLAIN_FIXTURE, PLAIN_FIXTURE, certified, mask)
        self.assertEqual((unchanged.certified_inside_mask, unchanged.certified_outside_mask, unchanged.outside_certified), ((), (), ()))

    # 12 ----------------------------------------------------------------------------------------
    def test_crossing_mask_exercise_and_confinement(self):
        camera = _manifest()["camera"]
        # Hand-derived: (95, 74) sits on the -Y/+Z boundary near Z = 5, (170, 12) on the -Y/+X
        # boundary near X = 5, and (95, 12) is about 0.118 rad from every boundary of its -Y face,
        # beyond maxAngle * 1.001 plus a footprint radius under 0.002 rad.
        mask = shadow_geometry.crossing_mask(camera, (0.0, 5.0, 0.0), [(95, 74), (170, 12), (95, 12)])
        self.assertEqual(mask, frozenset({(95, 74), (170, 12)}))

        band_pixels = frozenset({(100, 100), (101, 100)})
        crossing = frozenset({(100, 100), (101, 100), (102, 100)})
        controls = (frozenset({(9, 140)}), frozenset({(170, 150)}))

        def facts(differing, control_sets=controls):
            return G6PhaseFacts(frozenset(differing), crossing, band_pixels, control_sets)

        good = {p: facts({(100, 100)}) for p in PHASE_LITERAL}
        accepted = shadow_compare.gate_g6_exercised_and_confined(good)
        self.assertEqual((accepted.gate, accepted.passed), ("G6", True))
        cases = {
            "not-exercised-in-bands": {**good, (0.5, 0.5): facts({(102, 100)})},
            "no-difference": {**good, (0.0, 0.0): facts(set())},
            "difference-outside-mask": {**good, (0.0, 0.5): facts({(100, 100), (9, 100)})},
            "empty-control-outside-set": {**good, (0.5, 0.0): facts({(100, 100)}, (frozenset(), controls[1]))},
            "missing-phase": {p: f for p, f in good.items() if p != (0.5, 0.5)},
            "extra-phase": {**good, (0.25, 0.0): facts({(100, 100)})},
        }
        for label, phase_facts in cases.items():
            with self.subTest(label):
                result = shadow_compare.gate_g6_exercised_and_confined(phase_facts)
                self.assertEqual(result.gate, "G6")
                self.assertFalse(result.passed)

    # 13 ----------------------------------------------------------------------------------------
    def test_polygon_overlap_closed_forms(self):
        unit = ((0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0))
        far = ((2.0, 2.0), (3.0, 2.0), (3.0, 3.0), (2.0, 3.0))
        big = ((-1.0, -1.0), (2.0, -1.0), (2.0, 2.0), (-1.0, 2.0))
        diamond = ((0.5, -0.25), (1.25, 0.5), (0.5, 1.25), (-0.25, 0.5))
        clockwise_unit = tuple(reversed(unit))
        cases = [
            ("disjoint", unit, far, 0.0),
            ("contained", unit, big, 1.0),
            ("diamond", unit, diamond, 7.0 / 8.0),
            ("diamond-clockwise", clockwise_unit, diamond, 7.0 / 8.0),
            ("diamond-swapped", diamond, unit, 7.0 / 8.0),
        ]
        for label, a, b, expected in cases:
            with self.subTest(label):
                self.assertLessEqual(abs(shadow_geometry.convex_overlap_area(a, b) - expected), 1e-12)
        self.assertLessEqual(abs(shadow_geometry.polygon_area(diamond) - 1.125), 1e-12)
        self.assertLessEqual(abs(shadow_geometry.polygon_area(clockwise_unit) - 1.0), 1e-12)

    # 14 ----------------------------------------------------------------------------------------
    def test_constant_visibility_physical_reach(self):
        band = StationBand(on=(0.0, 0.0), across=(1.0, 0.0), along=(0.0, 1.0), half_width=BAND_H, half_length=0.9)
        tiles = list(_tiles(-1.0, 1.0, -0.1, 0.1, 0.05).values())
        band_area = 1.8 * 2.0 * BAND_H
        for plain, soft, expected in ((200, 0, 1.8), (200, 100, 0.9), (200, 200, 0.0)):
            with self.subTest(visibility=soft / plain):
                result = shadow_metrics.area_band_reach(band, [(tile, plain, soft) for tile in tiles])
                self.assertLessEqual(abs(result.reach - expected), 1e-9)
                self.assertEqual(result.units, "m")
                self.assertLessEqual(abs(result.band_area - band_area), 1e-12)
                self.assertLessEqual(abs(result.covered_area - band_area), 1e-12)
                self.assertLessEqual(abs(result.lit_endpoint_mean - soft / plain), 1e-12)
                self.assertLessEqual(abs(result.shadow_endpoint_mean - soft / plain), 1e-12)

    # 15 ----------------------------------------------------------------------------------------
    def test_halfplane_pixel_edge_at_four_phases(self):
        retained = _manifest()["camera"]
        on = [float(v) for v in _manifest()["stations"][4]["on"]]
        recorded_centres = []
        for phase in PHASE_LITERAL:
            camera = _fresh(retained) if phase == (0.0, 0.0) else _shift_camera(retained, phase)
            vp = [float(v) for v in camera["viewProjection"]]
            for axis in ("x", "y"):
                if axis == "x":
                    # On y = 0, screen x = (vp[0] X + vp[8] Z + vp[12] + 1) * 96. vp[8] is zero here.
                    edge = round((vp[0] * on[0] + vp[8] * on[2] + vp[12] + 1.0) * 96.0)
                    centre = ((edge / 96.0 - 1.0 - vp[12] - vp[8] * on[2]) / vp[0], on[2])

                    def shadowed(i, j, edge=edge):
                        return i >= edge

                else:
                    # On y = 0, screen y = (1 - (vp[1] X + vp[9] Z + vp[13])) * 96. vp[1] is zero here.
                    edge = round((1.0 - (vp[1] * on[0] + vp[9] * on[2] + vp[13])) * 96.0)
                    centre = (on[0], ((1.0 - edge / 96.0) - vp[13] - vp[1] * on[0]) / vp[9])

                    def shadowed(i, j, edge=edge):
                        return j >= edge

                recorded_centres.append((phase, axis, edge, centre))
                with self.subTest(phase=phase, axis=axis, edge=edge, centre=centre):
                    band = StationBand(
                        on=centre, across=ACROSS_LITERAL, along=ALONG_LITERAL, half_width=BAND_H, half_length=0.9
                    )
                    polygon = shadow_geometry.band_polygon(band)
                    samples = [
                        (footprint, 200, 0 if shadowed(i, j) else 200)
                        for i, j, footprint in shadow_geometry.footprints_covering(camera, polygon)
                    ]
                    result = shadow_metrics.area_band_reach(band, samples)
                    self.assertLessEqual(abs(result.reach - 0.9), 1e-9)
        self.assertEqual(len(recorded_centres), 8)

    # 16 ----------------------------------------------------------------------------------------
    def test_detrend_constant_and_known_step(self):
        flat = shadow_metrics.detrend_statistic([0.9] * 13)
        self.assertLessEqual(abs(flat.bump), 1e-12)
        self.assertEqual(len(flat.residuals), 13)
        step = [0.9 + (0.04 if i in (3, 4, 5) else 0.0) for i in range(13)]
        expected = 0.04 * (10.0 / 13.0 - 12.0 / 182.0)
        self.assertLessEqual(abs(expected - 0.028131868131868), 1e-14)
        self.assertLessEqual(abs(shadow_metrics.detrend_statistic(step).bump - expected), 1e-12)
        self.assertLessEqual(abs(shadow_metrics.detrend_statistic(step, single_precision=False).bump - expected), 1e-12)
        for label, series, code in (
            ("twelve-stations", [0.9] * 12, "stations"),
            ("non-finite", [0.9] * 12 + [float("nan")], "non-finite"),
        ):
            with self.subTest(label):
                with self.assertRaises(MetricInvalid) as caught:
                    shadow_metrics.detrend_statistic(series)
                self.assertEqual(caught.exception.code, code)

    # 17 ----------------------------------------------------------------------------------------
    def test_area_coverage_and_physical_units(self):
        band = StationBand(on=(0.0, 0.0), across=(1.0, 0.0), along=(0.0, 1.0), half_width=BAND_H, half_length=0.9)
        tiles = _tiles(-1.0, 1.0, -0.1, 0.1, 0.05)
        full = [(tile, 200, 100) for tile in tiles.values()]
        # Tile (19, 1) spans X [-0.05, 0], Z [-0.05, 0] and overlaps the band.
        missing = [(tile, 200, 100) for key, tile in tiles.items() if key != (19, 1)]
        duplicated = full + [(tiles[(19, 1)], 200, 100)]
        centimetre_band = StationBand(
            on=(0.0, 0.0), across=(1.0, 0.0), along=(0.0, 1.0), half_width=BAND_H * 100.0, half_length=90.0
        )
        metre_band_far = StationBand(
            on=(3.0, 4.5), across=(1.0, 0.0), along=(0.0, 1.0), half_width=BAND_H, half_length=0.9
        )
        pixel_unit_tiles = [
            (((float(i), float(j)), (i + 1.0, float(j)), (i + 1.0, j + 1.0), (float(i), j + 1.0)), 200, 100)
            for i in range(90, 100)
            for j in range(35, 45)
        ]
        cases = [
            ("missing-tile", band, missing, "coverage"),
            ("double-counted-tile", band, duplicated, "coverage"),
            ("band-in-centimetres", centimetre_band, full, "coverage"),
            ("footprints-in-pixels", metre_band_far, pixel_unit_tiles, "coverage"),
            ("plain-zero", band, [(tile, 0, 0) for tile in tiles.values()], "non-finite"),
        ]
        for label, case_band, samples, code in cases:
            with self.subTest(label):
                with self.assertRaises(MetricInvalid) as caught:
                    shadow_metrics.area_band_reach(case_band, samples)
                self.assertEqual(caught.exception.code, code)

        # The same physical half-plane at two pixel sizes gives the same reach in metres.
        reaches = []
        for size, edge_index in ((0.05, 20), (0.02, 50)):
            grid = _tiles(-1.0, 1.0, -0.1, 0.1, size)
            samples = [(tile, 200, 0 if kx >= edge_index else 200) for (kx, _), tile in grid.items()]
            reaches.append(shadow_metrics.area_band_reach(band, samples).reach)
        for reach in reaches:
            self.assertLessEqual(abs(reach - 0.9), 1e-9)
        self.assertLessEqual(abs(reaches[0] - reaches[1]), 1e-9)

    # 18 ----------------------------------------------------------------------------------------
    def test_frozen_parameters(self):
        self.assertEqual(shadow_compare.T_METRES, 0.008)
        self.assertEqual(shadow_compare.PHASE_BUDGET_METRES, 0.002)
        self.assertEqual(shadow_compare.PHASES, PHASE_LITERAL)
        self.assertEqual(shadow_compare.PHASE_TOLERANCE_PX, 0.02)
        self.assertEqual(shadow_compare.CAMERA_RELATIVE_TOLERANCE, 1e-6)
        self.assertEqual(shadow_compare.GATES, GATE_LITERAL)
        self.assertEqual(shadow_compare.OUTCOMES, OUTCOME_LITERAL)
        self.assertEqual(shadow_geometry.F_REGION, FloorRect(1.95, 5.25, 3.75, 7.15))
        self.assertEqual(
            shadow_geometry.NON_SEAM_CONTROLS, (FloorRect(2.0, 2.4, 6.7, 7.0), FloorRect(4.8, 5.2, 6.8, 7.1))
        )
        self.assertEqual(shadow_geometry.MAX_ANGLE, 16.0 * (math.pi / 2.0) / 256.0)
        self.assertLessEqual(abs(shadow_geometry.MAX_ANGLE - 0.0981748), 1e-7)
        self.assertEqual(shadow_geometry.MASK_MARGIN_FACTOR, 1.0 + 1e-3)
        self.assertEqual(shadow_geometry.BAND_HALF_WIDTH, math.sqrt(3.25) / 40.0)
        self.assertLessEqual(abs(shadow_geometry.BAND_HALF_WIDTH - 0.0450694), 1e-7)
        self.assertEqual(shadow_geometry.BAND_HALF_LENGTH, 0.9)
        self.assertEqual(shadow_geometry.ENDPOINT_STRIP, (0.85, 0.9))
        for actual, expected in zip(shadow_geometry.ACROSS_XZ + shadow_geometry.ALONG_XZ, (0.8320503, -0.5547002, 0.5547002, 0.8320503)):
            self.assertLessEqual(abs(actual - expected), 1e-7)
        self.assertEqual(shadow_geometry.LIGHT_POSITION, (0.0, 5.0, 0.0))
        self.assertEqual(
            shadow_geometry.APPROVED_DRAWS,
            (
                shadow_geometry.DrawRecord("floor", "floor", (-20.0, 0.0, -20.0), (20.0, 0.0, 20.0)),
                shadow_geometry.DrawRecord("wall", "box", (2.0, 0.0, -3.0), (2.4, 3.0, 3.0)),
            ),
        )
        self.assertEqual(shadow_metrics.BUMP_STATIONS, (3, 4, 5))
        self.assertEqual(shadow_metrics.STATION_COUNT, 13)
        self.assertEqual((shadow_metrics.LIT_ENDPOINT_MIN, shadow_metrics.SHADOW_ENDPOINT_MAX), (0.9, 0.1))
        self.assertEqual(shadow_metrics.COMPLETENESS_RELATIVE_TOLERANCE, 1e-9)
        # Each frozen value has one home. Unused duplicate constants are absent.
        self.assertFalse(hasattr(shadow_compare, "PHASE_METRES_PER_PIXEL"))
        self.assertFalse(hasattr(analyze, "REPORT_SCHEMA_VERSION"))
        self.assertEqual(
            shadow_compare.SCENE_EXPECTATION,
            {
                "light": (0.0, 5.0, 0.0),
                "radius": 30.0,
                "intensity": 1.0,
                "shadowId": 307,
                "settings.faceResolution": 256,
                "settings.bias": 0.01,
                "settings.slopeBias": 0.02,
                "settings.filter": "Soft",
                "settings.lightSizeMetres": 0.5,
                "settings.maxPenumbraTexels": 16,
                "settings.maxShadowedLights": 8,
                "resolved.degraded": False,
                "resolved.transientAtlasRows": 0,
                "softShadowedLights": 1,
                "plainShadowedLights": 0,
            },
        )
        self.assertEqual(
            shadow_compare.DEVICE_EXPECTATION,
            {
                "backend": "Direct3D11Native",
                "selectionSource": "EnvironmentOverride",
                "adapter": "Microsoft Basic Render Driver",
                "softwareAdapter": True,
                "deviceLossReason": None,
            },
        )

        # Behavior that consumes the frozen values.
        manifests = _synthetic_scene_manifests()
        for gate, call in (("G2", shadow_compare.gate_g2_device), ("G3", shadow_compare.gate_g3_scene)):
            accepted = call(manifests)
            self.assertEqual((accepted.gate, accepted.passed), (gate, True))
            self.assertTrue(accepted.reason)
            with self.subTest(gate=gate, missing="M-half-half"):
                missing = dict(manifests)
                missing.pop(("M", (0.5, 0.5)))
                result = call(missing)
                self.assertEqual(result.gate, gate)
                self.assertFalse(result.passed)
                self.assertTrue(result.reason)

        device_cases = {
            "backend": lambda d: d.__setitem__("backend", "Other"),
            "selection-source": lambda d: d.__setitem__("selectionSource", "Default"),
            "adapter": lambda d: d.__setitem__("adapter", "Other"),
            "hardware": lambda d: d.__setitem__("softwareAdapter", False),
            "device-loss": lambda d: d.__setitem__("deviceLossReason", "synthetic loss"),
            "device-section-only-difference": lambda d: d.__setitem__("deviceType", "Synthetic.OtherDevice"),
            "missing-software-fact": lambda d: d.pop("softwareAdapter"),
        }
        for label, mutate in device_cases.items():
            with self.subTest(device=label):
                changed = _fresh(manifests[("M", (0.5, 0.0))])
                mutate(changed["device"])
                result = shadow_compare.gate_g2_device({**manifests, ("M", (0.5, 0.0)): changed})
                self.assertEqual(result.gate, "G2")
                self.assertFalse(result.passed)
                self.assertTrue(result.reason)

        scene_cases = {
            "radius": lambda m: m["scene"].__setitem__("radius", 29.0),
            "hard-filter": lambda m: m["scene"]["settings"].__setitem__("filter", "Hard"),
            "degraded": lambda m: m["scene"]["resolved"].__setitem__("degraded", True),
            "transient-row": lambda m: m["scene"]["resolved"].__setitem__("transientAtlasRows", 1),
            "extra-soft-light": lambda m: m["scene"].__setitem__("softShadowedLights", 2),
            "plain-shadowed-light": lambda m: m["scene"].__setitem__("plainShadowedLights", 1),
            "non-finite-radius": lambda m: m["scene"].__setitem__("radius", float("nan")),
            "station-on-one-f32-step": lambda m: m["stations"][5]["on"].__setitem__(0, _next_f32(m["stations"][5]["on"][0])),
            "station-from-one-f32-step": lambda m: m["stations"][5]["from"].__setitem__(0, _next_f32(m["stations"][5]["from"][0])),
            "station-to-one-f32-step": lambda m: m["stations"][5]["to"].__setitem__(0, _next_f32(m["stations"][5]["to"][0])),
            "probe-world-one-f32-step": lambda m: m["stations"][5]["probes"][30]["world"].__setitem__(
                2, _next_f32(m["stations"][5]["probes"][30]["world"][2])
            ),
            "across-one-f32-step": lambda m: m["scene"]["across"].__setitem__(0, _next_f32(m["scene"]["across"][0])),
            "across-signed-zero": lambda m: m["scene"]["across"].__setitem__(1, -0.0),
            "window-one-f32-step": lambda m: m["scene"].__setitem__("window", _next_f32(m["scene"]["window"])),
            "frame-extent-one-f32-step": lambda m: m["scene"].__setitem__("frameHalfExtent", _next_f32(m["scene"]["frameHalfExtent"])),
            "frame-centre-pair-mismatch": lambda m: m["scene"]["frameCentre"].__setitem__(0, _next_f32(m["scene"]["frameCentre"][0])),
        }
        for label, mutate in scene_cases.items():
            with self.subTest(scene=label):
                changed = _fresh(manifests[("M", (0.5, 0.0))])
                mutate(changed)
                result = shadow_compare.gate_g3_scene({**manifests, ("M", (0.5, 0.0)): changed})
                self.assertEqual(result.gate, "G3")
                self.assertFalse(result.passed)
                self.assertTrue(result.reason)
        # Cross-phase identity is enforced even when B and M still match within that phase.
        changed_b = _fresh(manifests[("B", (0.0, 0.5))])
        changed_b["stations"][5]["on"][0] = _next_f32(changed_b["stations"][5]["on"][0])
        self.assertFalse(shadow_compare.gate_g3_scene({
            **manifests, ("B", (0.0, 0.5)): changed_b, ("M", (0.0, 0.5)): _fresh(changed_b),
        }).passed)
        self.assertNotEqual(manifests[("B", (0.0, 0.0))]["scene"]["frameCentre"],
                            manifests[("B", (0.5, 0.5))]["scene"]["frameCentre"])

        self.assertEqual(shadow_compare.separation_guard(0.0, 0.0), 0.002)
        self.assertEqual(shadow_compare.separation_guard(0.0015, 0.0), 0.003)
        self.assertEqual(shadow_compare.separation_guard(0.0, 0.0012), 0.0024)
        self.assertEqual(
            _fresh(shadow_compare.frozen_parameters_record()),
            {
                "designSha256": DESIGN_SHA,
                "thresholdMetres": 0.008,
                "phaseBudgetMetres": 0.002,
                "phases": [[0.0, 0.0], [0.5, 0.0], [0.0, 0.5], [0.5, 0.5]],
                "phaseTolerancePixels": 0.02,
                "cameraRelativeTolerance": 1e-6,
                "certifiedFloorRegion": {"y": 0.0, "xMin": 1.95, "xMax": 5.25, "zMin": 3.75, "zMax": 7.15},
                "nonSeamControls": [
                    {"xMin": 2.0, "xMax": 2.4, "zMin": 6.7, "zMax": 7.0},
                    {"xMin": 4.8, "xMax": 5.2, "zMin": 6.8, "zMax": 7.1},
                ],
                "bandHalfWidthMetres": math.sqrt(3.25) / 40.0,
                "bandHalfLengthMetres": 0.9,
                "bumpStations": [3, 4, 5],
                "maxAngleRadians": 16.0 * (math.pi / 2.0) / 256.0,
                "maskMarginFactor": 1.0 + 1e-3,
            },
        )
        band = shadow_geometry.station_band((3.3, 4.95))
        self.assertEqual(band.on, (3.3, 4.95))
        self.assertEqual((band.across, band.along), (shadow_geometry.ACROSS_XZ, shadow_geometry.ALONG_XZ))
        self.assertEqual((band.half_width, band.half_length), (math.sqrt(3.25) / 40.0, 0.9))
        self.assertLessEqual(
            abs(shadow_geometry.polygon_area(shadow_geometry.band_polygon(band)) - 1.8 * 2.0 * BAND_H), 1e-12
        )

    # 19 ----------------------------------------------------------------------------------------
    def test_invalid_has_priority(self):
        recorded = _manifest()
        endpoints = tuple((s["probes"][0]["ratio"], s["probes"][-1]["ratio"]) for s in recorded["stations"])
        minimum = min(p["plainRed"] for s in recorded["stations"] for p in s["probes"])
        self.assertEqual((minimum, endpoints), (118, ((1.0, 0.0),) * 13))
        accepted_facts = {(build, phase): G7PhaseFacts(minimum, endpoints, endpoints)
                          for build in ("B", "M") for phase in PHASE_LITERAL}
        accepted = shadow_compare.gate_g7_preconditions(accepted_facts)
        self.assertEqual((accepted.gate, accepted.passed), ("G7", True))

        def endpoint_facts(metric, side, value):
            original, candidate = list(endpoints), list(endpoints)
            target = original if metric == "original" else candidate
            pair = list(target[5])
            pair[side] = value
            target[5] = tuple(pair)
            return G7PhaseFacts(118, tuple(original), tuple(candidate))

        lit_f32 = struct.unpack("<f", _f32_bits(0.9))[0]
        shadow_f32 = struct.unpack("<f", _f32_bits(0.1))[0]
        endpoint_cases = [
            ("plain-red-24", G7PhaseFacts(24, endpoints, endpoints), False),
            ("plain-red-25", G7PhaseFacts(25, endpoints, endpoints), True),
            ("original-lit-equality-f32", endpoint_facts("original", 0, lit_f32), False),
            ("original-lit-next-f32-inside", endpoint_facts("original", 0, _next_f32(0.9)), True),
            ("original-lit-next-f32-outside", endpoint_facts("original", 0, _previous_f32(0.9)), False),
            ("original-shadow-equality-f32", endpoint_facts("original", 1, shadow_f32), False),
            ("original-shadow-next-f32-inside", endpoint_facts("original", 1, _previous_f32(0.1)), True),
            ("original-shadow-next-f32-outside", endpoint_facts("original", 1, _next_f32(0.1)), False),
            # A double comparison here would accept these. C# rounds each to its f32 boundary.
            ("original-lit-double-still-rounds-to-boundary", endpoint_facts("original", 0, _adjacent_f64(0.9, 1)), False),
            ("original-shadow-double-still-rounds-to-boundary", endpoint_facts("original", 1, _adjacent_f64(0.1, -1)), False),
            ("candidate-lit-equality-double", endpoint_facts("candidate", 0, 0.9), False),
            ("candidate-lit-next-double-inside", endpoint_facts("candidate", 0, _adjacent_f64(0.9, 1)), True),
            ("candidate-lit-next-double-outside", endpoint_facts("candidate", 0, _adjacent_f64(0.9, -1)), False),
            ("candidate-shadow-equality-double", endpoint_facts("candidate", 1, 0.1), False),
            ("candidate-shadow-next-double-inside", endpoint_facts("candidate", 1, _adjacent_f64(0.1, -1)), True),
            ("candidate-shadow-next-double-outside", endpoint_facts("candidate", 1, _adjacent_f64(0.1, 1)), False),
            ("original-twelve-stations", G7PhaseFacts(118, endpoints[:12], endpoints), False),
            ("candidate-twelve-stations", G7PhaseFacts(118, endpoints, endpoints[:12]), False),
            ("original-endpoint-nan", endpoint_facts("original", 0, float("nan")), False),
            ("candidate-endpoint-infinite", endpoint_facts("candidate", 0, float("inf")), False),
        ]
        for label, facts, passed in endpoint_cases:
            with self.subTest(precondition=label):
                result = shadow_compare.gate_g7_preconditions({**accepted_facts, ("M", (0.5, 0.5)): facts})
                self.assertEqual((result.gate, result.passed), ("G7", passed))
                self.assertTrue(result.reason)
        with self.subTest("G7-missing-phase"):
            missing = dict(accepted_facts)
            missing.pop(("M", (0.5, 0.5)))
            self.assertFalse(shadow_compare.gate_g7_preconditions(missing).passed)

        within_b = _series([0.001, 0.0015, 0.0005, 0.001])
        within_m = _series([0.02, 0.021, 0.0205, 0.0195])
        failed_g5 = tuple(GateResult(g.gate, g.gate != "G5", "synthetic") for g in ALL_GATES_PASS)
        result = shadow_compare.classify_metric("candidate", failed_g5, within_b, within_m)
        self.assertEqual(result.outcome, "INVALID")
        self.assertIn("G5", result.reason)
        self.assertEqual(
            (result.phase_range_b, result.phase_range_m, result.separation, result.separation_guard),
            (None, None, None, None),
        )

        failed_g2 = tuple(GateResult(g.gate, g.gate != "G2", "synthetic") for g in ALL_GATES_PASS)
        cases = {
            "gate-missing": (tuple(g for g in ALL_GATES_PASS if g.gate != "G3"), within_b, within_m),
            "inventory-failed": (ALL_GATES_PASS + (GateResult("phase-inventory", False, "synthetic"),), within_b, within_m),
            "phase-missing": (ALL_GATES_PASS, within_b, {p: v for p, v in within_m.items() if p != (0.5, 0.5)}),
            "phase-extra": (ALL_GATES_PASS, {**within_b, (0.25, 0.0): 0.001}, within_m),
            "bump-nan": (ALL_GATES_PASS, {**within_b, (0.0, 0.5): float("nan")}, within_m),
            "bump-infinite": (ALL_GATES_PASS, within_b, {**within_m, (0.5, 0.0): float("inf")}),
            "exceeding-but-invalid": (failed_g2, _series([0.02] * 4), _series([0.05] * 4)),
        }
        for label, (gates, bumps_b, bumps_m) in cases.items():
            with self.subTest(label):
                outcome = shadow_compare.classify_metric("original", gates, bumps_b, bumps_m)
                self.assertEqual(outcome.outcome, "INVALID")
                self.assertIsNone(outcome.separation)

        # Real compare orchestration and CLI refusal paths, with one bounded directory fixture.
        with tempfile.TemporaryDirectory() as tmp_name:
            tmp = Path(tmp_name)
            descriptor, bundle = _compare_bundle(tmp)
            attestation = bundle / "source-equivalence.json"
            trx_m = bundle / "M/seam.trx"
            manifest_m = bundle / "M/p0-0/point-shadow-seam.json"

            def no_soft_difference():
                for phase in PHASE_LITERAL:
                    _replace_compare_capture(bundle, "M", phase, SOFT_NAME,
                                             (bundle / "B" / _phase_dir(phase) / SOFT_NAME).read_bytes())

            def plain_red_24():
                offset = (74 * 192 + 95) * 4
                for build in ("B", "M"):
                    data = bytearray((bundle / build / "p0-0" / PLAIN_NAME).read_bytes())
                    data[offset] = 24
                    _replace_compare_capture(bundle, build, (0.0, 0.0), PLAIN_NAME, bytes(data))

            no_diff_paths = [bundle / "M" / _phase_dir(p) / name
                             for p in PHASE_LITERAL for name in (SOFT_NAME, "point-shadow-seam.json")]
            red_paths = [bundle / b / "p0-0" / name for b in ("B", "M")
                         for name in (PLAIN_NAME, "point-shadow-seam.json")]
            cases = [
                ("unexercised-control", "G6", no_soft_difference, no_diff_paths),
                ("attestation-missing", "G1", lambda: attestation.unlink(), [attestation]),
                ("hardware-device", "G2", lambda: _change_json(manifest_m, lambda d: d["device"].__setitem__("softwareAdapter", False)), [manifest_m]),
                ("candidate-read-plain-red-24", "G7", plain_red_24, red_paths),
                ("wrong-workflow-ref", "G1", lambda: _change_json(attestation, lambda d: d["workflow"].__setitem__("ref", "refs/heads/other")), [attestation]),
                ("wrong-workflow-job", "G1", lambda: _change_json(attestation, lambda d: d["workflow"].__setitem__("jobId", 2)), [attestation]),
                ("wrong-host-claim", "G1", lambda: _change_json(attestation, lambda d: d["host"].__setitem__("computerName", "other-host")), [attestation]),
                ("collector-name-is-explicit", "G1", lambda: trx_m.write_bytes(_collector_trx(test_name=HISTORICAL_TEST)), [trx_m]),
                ("collector-not-executed", "G1", lambda: trx_m.write_bytes(_collector_trx(outcome="NotExecuted")), [trx_m]),
                ("collector-error", "G1", lambda: trx_m.write_bytes(_collector_trx(outcome="Error")), [trx_m]),
                ("collector-error-counter", "G1", lambda: trx_m.write_bytes(_collector_trx().replace(b'error="0"', b'error="1"')), [trx_m]),
                ("collector-skip-counter", "G1", lambda: trx_m.write_bytes(_collector_trx().replace(b'notExecuted="0"', b'notExecuted="1"')), [trx_m]),
                ("collector-malformed-xml", "G1", lambda: trx_m.write_bytes(_collector_trx()[:-10]), [trx_m]),
                ("collector-missing", "G1", lambda: trx_m.unlink(), [trx_m]),
                ("collector-zero-cases", "G1", lambda: trx_m.write_bytes(_collector_trx(count=0)), [trx_m]),
                ("collector-two-cases", "G1", lambda: trx_m.write_bytes(_collector_trx(count=2)), [trx_m]),
                ("collector-wrong-host", "G2", lambda: trx_m.write_bytes(_collector_trx(host="other-host")), [trx_m]),
                ("collector-host-missing", "G2", lambda: trx_m.write_bytes(_collector_trx().replace(b' computerName="synthetic-fixture-vm"', b'')), [trx_m]),
                ("floor-certificate-unavailable", "G6", lambda: _change_json(attestation, lambda d: d["drawList"].append(
                    {"name": "occluder", "kind": "box", "min": [3.0, 1.0, 5.0], "max": [4.0, 2.0, 6.0]}
                )), [attestation]),
            ]
            for label, gate, mutate, paths in cases:
                with self.subTest(compare_invalid=label):
                    saved = {path: path.read_bytes() for path in paths}
                    try:
                        mutate()
                        if label == "unexercised-control":
                            direct = analyze.run_compare(descriptor, bundle)
                            self.assertEqual(direct["status"], "INVALID")
                        before = _bundle_hashes(bundle)
                        descriptor_hash = hashlib.sha256(descriptor.read_bytes()).hexdigest()
                        output = tmp / (label + ".json")
                        code = analyze.main(["compare", "--expectation", str(descriptor), "--bundle", str(bundle),
                                             "--output", str(output)])
                        self.assertEqual(code, 2)
                        invalid = json.loads(output.read_bytes())
                        self.assertEqual((invalid["mode"], invalid["status"]), ("compare", "INVALID"))
                        gates = {g["gate"]: g for g in invalid["gates"]}
                        self.assertTrue(set(GATE_LITERAL) <= set(gates))
                        self.assertFalse(gates[gate]["passed"])
                        self.assertTrue(gates[gate]["reason"])
                        for metric in ("original", "candidate"):
                            classification = invalid["classifications"][metric]
                            self.assertEqual(classification["outcome"], "INVALID")
                            self.assertIsNone(classification["separation"])
                        if label == "unexercised-control":
                            self.assertEqual(invalid, _fresh(direct))
                            for passed_gate in ("G1", "G2", "G3", "G4", "G5", "G7"):
                                self.assertTrue(gates[passed_gate]["passed"])
                        if label == "candidate-read-plain-red-24":
                            self.assertTrue(gates["G5"]["passed"])
                        self.assertEqual(_bundle_hashes(bundle), before)
                        self.assertEqual(hashlib.sha256(descriptor.read_bytes()).hexdigest(), descriptor_hash)
                    finally:
                        for path, data in saved.items():
                            path.write_bytes(data)

    # 20 ----------------------------------------------------------------------------------------
    def test_phase_inconclusive(self):
        steady_m = _series([0.02] * 4)
        cases = [
            ("baseline-range-over-budget", _series([0.0, 0.0021, 0.001, 0.001]), steady_m, "INCONCLUSIVE-PHASE"),
            ("mutant-range-over-budget", _series([0.001] * 4), _series([0.02, 0.0225, 0.02, 0.02]), "INCONCLUSIVE-PHASE"),
            ("phase-before-undetected", _series([0.0, 0.003, 0.0, 0.0]), _series([0.0] * 4), "INCONCLUSIVE-PHASE"),
            # W_B equals T/4 exactly. The rule is strict, so classification continues.
            ("range-at-budget-is-not-phase", _series([0.0, 0.002, 0.001, 0.0015]), steady_m, "BASELINE-WITHIN-BOUND"),
        ]
        for label, bumps_b, bumps_m, expected in cases:
            with self.subTest(label):
                result = shadow_compare.classify_metric("candidate", ALL_GATES_PASS, bumps_b, bumps_m)
                self.assertEqual(result.outcome, expected)
        over = shadow_compare.classify_metric("candidate", ALL_GATES_PASS, cases[0][1], steady_m)
        self.assertLessEqual(abs(over.phase_range_b - 0.0021), 1e-15)

    # 21 ----------------------------------------------------------------------------------------
    def test_control_undetected_and_separation_boundary(self):
        # Dyadic fixtures make the strict separation boundary exact in binary floating point.
        boundary_b = _series([0.0078125, 0.005859375, 0.0068359375, 0.0068359375])
        cases = [
            ("mutant-at-bound", _series([0.001] * 4), _series([0.008, 0.009, 0.0095, 0.009]), "INCONCLUSIVE-UNDETECTED"),
            ("negative-mutant-within-bound", _series([0.001] * 4), _series([-0.0079, -0.0085, -0.0085, -0.0085]), "INCONCLUSIVE-UNDETECTED"),
            ("insufficient-separation", _series([0.007] * 4), _series([0.0085] * 4), "INCONCLUSIVE-UNDETECTED"),
            ("zero-range-equal-effects", _series([0.012] * 4), _series([0.012] * 4), "INCONCLUSIVE-UNDETECTED"),
            ("separation-equal-to-guard", boundary_b, _series([0.01171875] * 4), "INCONCLUSIVE-UNDETECTED"),
            ("separation-just-above-guard", boundary_b, _series([0.01171875 + 2.0**-20] * 4), "BASELINE-WITHIN-BOUND"),
        ]
        for label, bumps_b, bumps_m, expected in cases:
            with self.subTest(label):
                result = shadow_compare.classify_metric("original", ALL_GATES_PASS, bumps_b, bumps_m)
                self.assertEqual(result.outcome, expected)
        equal = shadow_compare.classify_metric("original", ALL_GATES_PASS, boundary_b, _series([0.01171875] * 4))
        self.assertEqual(equal.separation, 0.00390625)
        self.assertEqual(equal.separation_guard, 0.00390625)

        # A complete synthetic B/M experiment reads the real bundle format and mocks nothing.
        with tempfile.TemporaryDirectory() as tmp_name:
            tmp = Path(tmp_name)
            descriptor, bundle = _compare_bundle(tmp)
            before = _bundle_hashes(bundle)
            descriptor_before = hashlib.sha256(descriptor.read_bytes()).hexdigest()
            self.assertEqual(len(before), 43)
            self.assertEqual(sum(1 for p in bundle.rglob("*") if p.is_dir()), 10)
            self.assertLess(sum(p.stat().st_size for p in bundle.rglob("*") if p.is_file()), 64 * 1024 * 1024)
            retained = _manifest()
            for phase in PHASE_LITERAL:
                manifest, plain, soft_b, soft_m = _compare_phase_inputs(phase)
                probes = [p for s in manifest["stations"] for p in s["probes"]]
                self.assertEqual(len({tuple(p["pixel"]) for p in probes}), 793)
                self.assertNotIn(MUTATION_PIXEL, {tuple(p["pixel"]) for p in probes})
                for station, source in zip(manifest["stations"], retained["stations"]):
                    for key in ("on", "from", "to", "sum", "reach"):
                        self.assertEqual(station[key], source[key])
                    for probe, source_probe in zip(station["probes"], source["probes"]):
                        self.assertEqual((probe["world"], probe["ratio"]), (source_probe["world"], source_probe["ratio"]))
                        offset = probe["byteIndex"]
                        self.assertEqual((plain[offset], soft_b[offset], soft_m[offset]),
                                         (probe["plainRed"], probe["shadowRed"], probe["shadowRed"]))
                self.assertEqual((manifest["fit"], manifest["statistic"]), (retained["fit"], retained["statistic"]))

            report = analyze.run_compare(descriptor, bundle)
            self.assertEqual(set(report), COMPARE_KEYS)
            self.assertEqual((report["tool"], report["mode"], report["status"], report["units"]),
                             ("shadow-seam-analysis", "compare", "COMPLETED", "m"))
            self.assertEqual(report["designSha256"], DESIGN_SHA)
            self.assertEqual(set(report["inputs"]), {"expectationSha256", "files", "trx"})
            self.assertEqual(report["inputs"]["expectationSha256"], descriptor_before)
            self.assertEqual(report["inputs"]["files"], before)
            for build in ("B", "M"):
                trx = report["inputs"]["trx"][build]
                self.assertEqual(trx["sha256"], before[build + "/seam.trx"])
                self.assertEqual((trx["testName"], trx["computerName"]), (COLLECTOR_TEST, SYNTHETIC_HOST["computerName"]))
                self.assertEqual((trx["executed"], trx["passed"], trx["skipped"], trx["errors"]), (1, 1, 0, 0))
                self.assertFalse(trx["assertsBound"])
            self.assertEqual(report["claims"], {
                "sourceEquivalence": {"kind": "supplied-claim", "matchesExpectation": True},
                "workflow": {"kind": "supplied-claim", "value": SYNTHETIC_WORKFLOW},
                "host": {"kind": "supplied-claim", "value": SYNTHETIC_HOST, "trxComputerNamesAgree": True},
                "independentlyVerified": False,
            })
            gates = {g["gate"]: g for g in report["gates"]}
            self.assertEqual(set(gates), set(GATE_LITERAL) | {"phase-inventory"})
            for gate in gates.values():
                self.assertEqual(set(gate), {"gate", "passed", "reason"})
                self.assertTrue(gate["passed"])
                self.assertTrue(gate["reason"])
                # Supplied binding and draw-list claims are never described as independently verified.
                self.assertNotIn("verified", gate["reason"].replace("not independently verified", ""))
            self.assertIn("supplied binding claim", gates["G5"]["reason"])

            coverage = {(r["build"], tuple(r["phase"])): r for r in report["coverage"]}
            self.assertEqual(set(coverage), {(b, p) for b in ("B", "M") for p in PHASE_LITERAL})
            # Nominal and achieved phases and the recorded band centres, per build then frozen phase.
            self.assertEqual([(r["build"], tuple(r["phase"])) for r in report["coverage"]],
                             [(b, p) for b in ("B", "M") for p in PHASE_LITERAL])
            band_centres = [[s["on"][0], s["on"][2]] for s in retained["stations"]]
            self.assertEqual(len(band_centres), 13)
            for (build, phase), record in coverage.items():
                with self.subTest(phase_record=(build, phase)):
                    self.assertEqual(record["nominalPhase"], list(phase))
                    achieved = record["achievedPhase"]
                    self.assertEqual(len(achieved), 2)
                    for value, nominal in zip(achieved, phase):
                        self.assertTrue(math.isfinite(value))
                        self.assertLessEqual(abs(value - nominal), 1e-4)
                    if phase == (0.0, 0.0):
                        self.assertEqual(achieved, [0.0, 0.0])
                    self.assertEqual(record["bandCentres"], band_centres)
                    self.assertNotIn("verified",
                                     record["certificate"]["reason"].replace("not independently verified", ""))
                    self.assertIn("approved Wall list", record["certificate"]["reason"])
            for record in coverage.values():
                self.assertTrue(record["certificate"]["valid"])
                self.assertGreater(record["certificate"]["certifiedPixelCount"], 793)
                self.assertTrue(record["measurements"]["valid"])
                self.assertEqual(record["measurements"]["uncovered"], [])
                self.assertEqual([c["index"] for c in record["controls"]], [0, 1])
                for control in record["controls"]:
                    self.assertGreater(control["outsideMaskArea"], 0.0)
                    self.assertGreater(control["outsideMaskPixelCount"], 0)
            self.assertEqual([tuple(d["phase"]) for d in report["differences"]], list(PHASE_LITERAL))
            for difference in report["differences"]:
                self.assertEqual(set(difference), {"phase", "certifiedInsideMask", "certifiedOutsideMask",
                                                  "outside-certified-floor-region"})
                self.assertEqual(difference["certifiedInsideMask"], {"count": 1, "pixels": [[95, 74]]})
                self.assertEqual(difference["certifiedOutsideMask"], {"count": 0, "pixels": []})
                self.assertEqual(difference["outside-certified-floor-region"], {"count": 0, "pixels": []})

            self.assertEqual(report["rows"][0]["label"], "retained-historical")
            self.assertEqual(_f32_bits(report["rows"][0]["bump"]), _f32_bits(-0.008728723))
            self.assertEqual(report["historicalAssertion"], {
                "outcome": "Failed", "testName": HISTORICAL_TEST, "boundMetres": 0.008,
            })
            rows = {(r["build"], tuple(r["phase"]), r["metric"]): r for r in report["rows"][1:]}
            self.assertEqual(len(report["rows"]), 17)
            self.assertEqual([(r["build"], tuple(r["phase"]), r["metric"]) for r in report["rows"][1:]],
                             [(b, p, m) for b in ("B", "M") for p in PHASE_LITERAL for m in ("original", "candidate")])
            self.assertEqual(set(rows), {(b, p, m) for b in ("B", "M") for p in PHASE_LITERAL
                                         for m in ("original", "candidate")})
            # The one fully contained changed footprint contributes its closed-form area / 2h.
            delta_reach = (1.0 / 53.3333376) * (1.0 / 52.03857984) / (2.0 * BAND_H)
            delta_bump_bound = delta_reach * (1.0 / 3.0 - 1.0 / 13.0 - 2.0 / 182.0)
            self.assertLess(delta_bump_bound, 0.000982)
            for phase in PHASE_LITERAL:
                for build in ("B", "M"):
                    original = rows[(build, phase, "original")]
                    self.assertEqual((original["reach"], original["residuals"]), (OFFLINE_REACH, OFFLINE_RESIDUALS))
                    self.assertEqual((original["bump"], original["worstElsewhere"]), (OFFLINE_BUMP, OFFLINE_WORST))
                    self.assertEqual(original["endpoints"], [[1.0, 0.0]] * 13)
                    self.assertEqual(original["minPlainRed"], 118)
                    candidate = rows[(build, phase, "candidate")]
                    self.assertEqual(candidate["endpoints"], [[1.0, 0.0]] * 13)
                    self.assertEqual((len(candidate["reach"]), len(candidate["residuals"])), (13, 13))
                    self.assertEqual((original["label"], candidate["label"]), ("paired-original", "paired-candidate-area-band"))
                    self.assertEqual((original["status"], candidate["status"]), ("DIAGNOSTIC", "DIAGNOSTIC"))
                    for value in candidate["reach"] + candidate["residuals"] + [candidate["bump"], candidate["worstElsewhere"]]:
                        self.assertTrue(math.isfinite(value))
                candidate_b, candidate_m = rows[("B", phase, "candidate")], rows[("M", phase, "candidate")]
                for station in range(13):
                    if station != 5:
                        self.assertEqual(struct.pack("<d", candidate_b["reach"][station]),
                                         struct.pack("<d", candidate_m["reach"][station]))
                self.assertLessEqual(abs(candidate_b["reach"][5] - candidate_m["reach"][5] - delta_reach), 1e-12)
                self.assertLessEqual(abs(candidate_b["bump"] - candidate_m["bump"] - delta_bump_bound), 1e-12)
            original = report["classifications"]["original"]
            self.assertEqual(set(original), CLASSIFICATION_KEYS)
            self.assertEqual((original["outcome"], original["separation"]), ("INCONCLUSIVE-UNDETECTED", 0.0))
            self.assertEqual(original["phaseRange"], {"B": 0.0, "M": 0.0})
            candidate = report["classifications"]["candidate"]
            self.assertEqual(set(candidate), CLASSIFICATION_KEYS)
            for classification in (original, candidate):
                self.assertEqual((classification["scope"], classification["nonClaims"]), (SCOPE_LITERAL, NON_CLAIMS_LITERAL))
            self.assertIn(candidate["outcome"], ("INCONCLUSIVE-PHASE", "INCONCLUSIVE-UNDETECTED"))
            if candidate["separation"] is not None:
                self.assertLessEqual(candidate["separation"], delta_bump_bound + 1e-12)
                self.assertGreaterEqual(candidate["separationGuard"], 0.002)
            self.assertEqual(report["reportedOnly"], {"baselineZeroSoftEqualsRetained": False})

            output = tmp / "compare.json"
            code = analyze.main(["compare", "--expectation", str(descriptor), "--bundle", str(bundle), "--output", str(output)])
            self.assertEqual(code, 0)
            self.assertEqual(json.loads(output.read_bytes()), _fresh(report))
            self.assertEqual(_bundle_hashes(bundle), before)
            self.assertEqual(hashlib.sha256(descriptor.read_bytes()).hexdigest(), descriptor_before)
            # A sibling whose name extends the bundle name is outside the bundle and stays writable.
            sibling = tmp / (bundle.name + "-reports")
            sibling.mkdir()
            sibling_output = sibling / "compare.json"
            code = analyze.main(["compare", "--expectation", str(descriptor), "--bundle", str(bundle),
                                 "--output", str(sibling_output)])
            self.assertEqual(code, 0)
            self.assertEqual(json.loads(sibling_output.read_bytes()), _fresh(report))
            self.assertEqual(_bundle_hashes(bundle), before)

            # Resolve lexical aliases, symlinks and hard links before any report write.
            descriptor_symlink = tmp / "descriptor-symlink.json"
            os.symlink(descriptor, descriptor_symlink)
            descriptor_hardlink = tmp / "descriptor-hardlink.json"
            os.link(descriptor, descriptor_hardlink)
            capture = bundle / "B/p0-0" / SOFT_NAME
            capture_symlink = tmp / "capture-symlink.rgba"
            os.symlink(capture, capture_symlink)
            capture_hardlink = tmp / "capture-hardlink.rgba"
            os.link(capture, capture_hardlink)
            alias_dir = tmp / "alias-dir"
            alias_dir.mkdir()
            extra_input = bundle / "unused-but-protected.bin"
            extra_input.write_bytes(b"named bundle root still owns this existing file")
            protected = [descriptor, alias_dir / ".." / descriptor.name, descriptor_symlink, descriptor_hardlink,
                         capture, bundle / "B/p0-0/.." / "p0-0" / SOFT_NAME, capture_symlink, capture_hardlink,
                         extra_input]
            guarded_before = _bundle_hashes(bundle)
            for target in protected:
                with self.subTest(protected_compare_output=str(target)):
                    code = analyze.main(["compare", "--expectation", str(descriptor), "--bundle", str(bundle),
                                         "--output", str(target)])
                    self.assertEqual(code, 2)
                    self.assertEqual(_bundle_hashes(bundle), guarded_before)
                    self.assertEqual(hashlib.sha256(descriptor.read_bytes()).hexdigest(), descriptor_before)

            # A directory bundle owns every path resolved beneath it, including files that do not exist yet.
            bundle_link = tmp / "bundle-link"
            os.symlink(bundle, bundle_link, target_is_directory=True)
            phase_link = tmp / "phase-link"
            os.symlink(bundle / "B" / "p0-0", phase_link, target_is_directory=True)
            new_targets = [bundle / "new-report.json", bundle / "B" / "p0-0" / "new-report.json",
                           alias_dir / ".." / bundle.name / "new-report.json", bundle / "new-dir" / "new-report.json",
                           bundle_link / "new-report.json", phase_link / "new-report.json"]
            for target in new_targets:
                with self.subTest(new_output_under_bundle=str(target)):
                    self.assertFalse(os.path.lexists(target))
                    code = analyze.main(["compare", "--expectation", str(descriptor), "--bundle", str(bundle),
                                         "--output", str(target)])
                    self.assertEqual(code, 2)
                    self.assertFalse(os.path.lexists(target))
                    self.assertFalse((bundle / "new-dir").exists())
                    self.assertEqual(_bundle_hashes(bundle), guarded_before)
                    self.assertEqual(hashlib.sha256(descriptor.read_bytes()).hexdigest(), descriptor_before)

    # 22 ----------------------------------------------------------------------------------------
    def test_baseline_exceeds_bound(self):
        cases = [
            ("negative-baseline-over-bound", _series([-0.0087, -0.0080, -0.0081, -0.0082]), _series([0.03] * 4)),
            ("one-phase-over-bound", _series([0.0081, 0.007, 0.007, 0.007]), _series([-0.03] * 4)),
        ]
        for label, bumps_b, bumps_m in cases:
            with self.subTest(label):
                result = shadow_compare.classify_metric("candidate", ALL_GATES_PASS, bumps_b, bumps_m)
                self.assertEqual(result.outcome, "BASELINE-EXCEEDS-BOUND")
                record = _fresh(shadow_compare.classification_record(result, bumps_b, bumps_m))
                self.assertEqual(set(record), CLASSIFICATION_KEYS)
                self.assertEqual(record["outcome"], "BASELINE-EXCEEDS-BOUND")
                self.assertEqual(record["scope"], SCOPE_LITERAL)
                self.assertEqual(record["nonClaims"], NON_CLAIMS_LITERAL)
                for key in _keys(record):
                    lowered = key.lower()
                    for forbidden in ("pass", "clear", "catalog", "adopt", "waiver", "defect", "cause"):
                        self.assertNotIn(forbidden, lowered)

    # 23 ----------------------------------------------------------------------------------------
    def test_baseline_within_bound(self):
        bumps_b = _series([0.001, 0.0015, 0.0005, 0.001])
        bumps_m = _series([0.02, 0.021, 0.0205, 0.0195])
        result = shadow_compare.classify_metric("candidate", ALL_GATES_PASS, bumps_b, bumps_m)
        self.assertEqual(result.outcome, "BASELINE-WITHIN-BOUND")
        self.assertEqual(result.metric, "candidate")
        self.assertLessEqual(abs(result.phase_range_b - 0.001), 1e-15)
        self.assertLessEqual(abs(result.phase_range_m - 0.0015), 1e-15)
        self.assertLessEqual(abs(result.separation - 0.018), 1e-15)
        self.assertLessEqual(abs(result.separation_guard - 0.003), 1e-15)
        record = _fresh(shadow_compare.classification_record(result, bumps_b, bumps_m))
        self.assertEqual(set(record), CLASSIFICATION_KEYS)
        self.assertEqual(record["metric"], "candidate")
        self.assertEqual(record["scope"], SCOPE_LITERAL)
        self.assertEqual(record["nonClaims"], NON_CLAIMS_LITERAL)
        self.assertEqual(
            record["bumps"],
            {
                "B": [[0.0, 0.0, 0.001], [0.5, 0.0, 0.0015], [0.0, 0.5, 0.0005], [0.5, 0.5, 0.001]],
                "M": [[0.0, 0.0, 0.02], [0.5, 0.0, 0.021], [0.0, 0.5, 0.0205], [0.5, 0.5, 0.0195]],
            },
        )
        original = shadow_compare.classify_metric("original", ALL_GATES_PASS, bumps_b, bumps_m)
        self.assertEqual(original.metric, "original")

    # 24 ----------------------------------------------------------------------------------------
    def test_retained_mode_is_report_only(self):
        self.assertEqual(hashlib.sha256(RETAINED_ZIP.read_bytes()).hexdigest(), RETAINED_SHA)
        report = analyze.run_retained(RETAINED_ZIP)
        self.assertEqual(report["mode"], "retained")
        self.assertEqual(report["status"], "REPORT-ONLY")
        self.assertEqual(report["designSha256"], DESIGN_SHA)
        self.assertEqual(report["units"], "m")
        self.assertEqual(report["inputs"]["artifactSha256"], RETAINED_SHA)
        self.assertEqual(report["inputs"]["manifestSha256"], RETAINED_MANIFEST_SHA)

        rows = report["rows"]
        self.assertEqual(rows[0]["label"], "retained-historical")
        self.assertEqual(rows[0]["metric"], "original")
        self.assertEqual(_f32_bits(rows[0]["bump"]), _f32_bits(-0.008728723))
        by_label = {row["label"]: row for row in rows}
        replay = by_label["retained-replay-original"]
        self.assertEqual((replay["bump"], replay["worstElsewhere"]), (OFFLINE_BUMP, OFFLINE_WORST))
        candidate = by_label["retained-candidate-area-band"]
        self.assertEqual(candidate["metric"], "candidate")
        self.assertEqual(candidate["status"], "REPORT-ONLY")
        self.assertEqual(len(candidate["reach"]), 13)
        for value in candidate["reach"] + [candidate["bump"], candidate["worstElsewhere"]]:
            self.assertTrue(math.isfinite(value))

        self.assertEqual(report["historicalAssertion"]["outcome"], "Failed")
        self.assertEqual(report["historicalAssertion"]["testName"], HISTORICAL_TEST)
        self.assertEqual(report["historicalAssertion"]["boundMetres"], 0.008)
        self.assertTrue({"mutant", "phases", "atlas"} <= set(report["notAvailable"]))
        certificate_reason = report["coverage"]["certificate"]["reason"]
        self.assertIn("approved Wall list", certificate_reason)
        self.assertNotIn("verified", certificate_reason.replace("not independently verified", ""))
        text = json.dumps(report)
        for outcome in OUTCOME_LITERAL:
            self.assertNotIn(f'"{outcome}"', text)
        self.assertFalse({"B", "M"} & set(_keys(_fresh(report))))

        with tempfile.TemporaryDirectory() as tmp_name:
            tmp = Path(tmp_name)
            output = tmp / "retained.json"
            code = analyze.main(["retained", "--artifact", str(RETAINED_ZIP), "--output", str(output)])
            self.assertEqual(code, 0)
            self.assertEqual(json.loads(output.read_text(encoding="utf-8")), _fresh(report))

            tampered = tmp / "tampered.zip"
            tampered.write_bytes(RETAINED_ZIP.read_bytes() + b"\0")
            self._refused("sha256", analyze.run_retained, tampered)
            invalid_output = tmp / "tampered.json"
            code = analyze.main(["retained", "--artifact", str(tampered), "--output", str(invalid_output)])
            self.assertEqual(code, 2)
            invalid = json.loads(invalid_output.read_text(encoding="utf-8"))
            self.assertEqual((invalid["mode"], invalid["status"]), ("retained", "INVALID"))

            # Protect a temporary copy, so even a broken guard cannot mutate committed evidence.
            protected_artifact = tmp / "protected.zip"
            protected_artifact.write_bytes(RETAINED_ZIP.read_bytes())
            artifact_symlink = tmp / "artifact-symlink.zip"
            os.symlink(protected_artifact, artifact_symlink)
            artifact_hardlink = tmp / "artifact-hardlink.zip"
            os.link(protected_artifact, artifact_hardlink)
            alias_dir = tmp / "alias-dir"
            alias_dir.mkdir()
            for target in (protected_artifact, alias_dir / ".." / protected_artifact.name,
                           artifact_symlink, artifact_hardlink):
                with self.subTest(protected_retained_output=str(target)):
                    code = analyze.main(["retained", "--artifact", str(protected_artifact), "--output", str(target)])
                    self.assertEqual(code, 2)
                    self.assertEqual(hashlib.sha256(protected_artifact.read_bytes()).hexdigest(), RETAINED_SHA)

        self.assertEqual(hashlib.sha256(RETAINED_ZIP.read_bytes()).hexdigest(), RETAINED_SHA)


if __name__ == "__main__":
    unittest.main()
