"""Validity gates G1 to G7, paired identity checks, outside-F difference reporting and the frozen
prospective outcome ordering.

Every outcome is a diagnostic classification for this scene, these four phases and the certified
floor only. Nothing here adopts a metric, clears Catalog C or attributes a shader defect. Supplied
claims are compared with pinned expectations. They are never independently verified here.
"""

from __future__ import annotations

import json
import math
import struct
from dataclasses import dataclass
from typing import Any, Dict, FrozenSet, List, Mapping, Optional, Sequence, Tuple

import shadow_geometry
import shadow_inputs
import shadow_metrics

Phase = Tuple[float, float]

T_METRES = 0.008
PHASE_BUDGET_METRES = T_METRES / 4.0
PHASES: Tuple[Phase, ...] = ((0.0, 0.0), (0.5, 0.0), (0.0, 0.5), (0.5, 0.5))
PHASE_TOLERANCE_PX = 0.02
CAMERA_RELATIVE_TOLERANCE = 1e-6

GATES = ("G1", "G2", "G3", "G4", "G5", "G6", "G7")
INVENTORY_GATE = "phase-inventory"

INVALID = "INVALID"
INCONCLUSIVE_PHASE = "INCONCLUSIVE-PHASE"
INCONCLUSIVE_UNDETECTED = "INCONCLUSIVE-UNDETECTED"
BASELINE_EXCEEDS_BOUND = "BASELINE-EXCEEDS-BOUND"
BASELINE_WITHIN_BOUND = "BASELINE-WITHIN-BOUND"
OUTCOMES = (INVALID, INCONCLUSIVE_PHASE, INCONCLUSIVE_UNDETECTED, BASELINE_EXCEEDS_BOUND, BASELINE_WITHIN_BOUND)
REPORT_ONLY = "REPORT-ONLY"

OUTSIDE_CERTIFIED_LABEL = "outside-certified-floor-region"
CLASSIFICATION_SCOPE = "diagnostic, this scene, these four phases, certified floor only"
NON_CLAIMS = (
    "no metric adoption",
    "no Catalog C waiver",
    "no shader-defect attribution",
    "no render-pass or Catalog-clear flag",
)

# G2: identical device section required in every manifest.
DEVICE_EXPECTATION = {
    "backend": "Direct3D11Native",
    "selectionSource": "EnvironmentOverride",
    "adapter": "Microsoft Basic Render Driver",
    "softwareAdapter": True,
    "deviceLossReason": None,
}

# G3: dotted paths into each manifest's `scene` block.
SCENE_EXPECTATION = {
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
}

# A collector TRX must report one executed, passed case and nothing else.
COLLECTOR_COUNTERS = {
    "total": 1, "executed": 1, "passed": 1, "failed": 0, "error": 0, "notExecuted": 0, "notRunnable": 0,
}
COLLECTOR_ZERO_IF_PRESENT = ("timeout", "aborted", "inconclusive", "passedButRunAborted", "disconnected")

# View rotation entries (upper 3 by 3) in row-major M11 to M44 order.
_VIEW_ROTATION = (0, 1, 2, 4, 5, 6, 8, 9, 10)
_MALFORMED = (KeyError, TypeError, ValueError, IndexError, AttributeError)


@dataclass(frozen=True)
class GateResult:
    gate: str
    passed: bool
    reason: str


@dataclass(frozen=True)
class Classification:
    metric: str
    outcome: str
    reason: str
    phase_range_b: Optional[float]
    phase_range_m: Optional[float]
    separation: Optional[float]
    separation_guard: Optional[float]


@dataclass(frozen=True)
class DifferenceReport:
    certified_inside_mask: Tuple[shadow_geometry.Pixel, ...]
    certified_outside_mask: Tuple[shadow_geometry.Pixel, ...]
    outside_certified: Tuple[shadow_geometry.Pixel, ...]


@dataclass(frozen=True)
class G6PhaseFacts:
    differing_certified: FrozenSet[shadow_geometry.Pixel]
    crossing_mask: FrozenSet[shadow_geometry.Pixel]
    crossing_band_pixels: FrozenSet[shadow_geometry.Pixel]
    control_outside_pixels: Tuple[FrozenSet[shadow_geometry.Pixel], FrozenSet[shadow_geometry.Pixel]]


@dataclass(frozen=True)
class G7PhaseFacts:
    min_plain_red: int
    original_endpoints: Tuple[Tuple[float, float], ...]
    candidate_endpoints: Tuple[Tuple[float, float], ...]


def _result(gate: str, problems: Sequence[str], passed_reason: str) -> GateResult:
    if problems:
        return GateResult(gate, False, "; ".join(problems))
    return GateResult(gate, True, passed_reason)


def _canonical(value: Any) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"))


def _is_number(value: Any) -> bool:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return False
    try:
        return math.isfinite(value)
    except OverflowError:
        return False


def _same_fact(actual: Any, expected: Any) -> bool:
    if isinstance(expected, bool) or expected is None:
        return actual is expected
    if isinstance(expected, str):
        return isinstance(actual, str) and actual == expected
    if isinstance(expected, tuple):
        return (isinstance(actual, (list, tuple)) and len(actual) == len(expected)
                and all(_same_fact(a, e) for a, e in zip(actual, expected)))
    return _is_number(actual) and float(actual) == float(expected)


def _f32_signature(values: Sequence[Any], length: int) -> bytes:
    if not isinstance(values, (list, tuple)) or len(values) != length:
        raise ValueError(f"expected {length} numbers")
    packed = []
    for value in values:
        if not _is_number(value):
            raise ValueError(f"non-finite or non-numeric value {value!r}")
        try:
            packed.append(struct.pack("<f", value))
        except (OverflowError, struct.error):
            raise ValueError(f"value {value!r} is outside float32 range") from None
    return b"".join(packed)


def _slot(build: str, phase: Phase) -> str:
    return f"{build} ({phase[0]:g}, {phase[1]:g})"


def frozen_parameters_record() -> Mapping[str, Any]:
    """JSON-ready record of the frozen design parameters, written into every report."""
    region = shadow_geometry.F_REGION
    return {
        "designSha256": shadow_inputs.DESIGN_SHA256,
        "thresholdMetres": T_METRES,
        "phaseBudgetMetres": PHASE_BUDGET_METRES,
        "phases": [list(phase) for phase in PHASES],
        "phaseTolerancePixels": PHASE_TOLERANCE_PX,
        "cameraRelativeTolerance": CAMERA_RELATIVE_TOLERANCE,
        "certifiedFloorRegion": {
            "y": 0.0, "xMin": region.x_min, "xMax": region.x_max, "zMin": region.z_min, "zMax": region.z_max,
        },
        "nonSeamControls": [
            {"xMin": c.x_min, "xMax": c.x_max, "zMin": c.z_min, "zMax": c.z_max}
            for c in shadow_geometry.NON_SEAM_CONTROLS
        ],
        "bandHalfWidthMetres": shadow_geometry.BAND_HALF_WIDTH,
        "bandHalfLengthMetres": shadow_geometry.BAND_HALF_LENGTH,
        "bumpStations": [3, 4, 5],
        "maxAngleRadians": shadow_geometry.MAX_ANGLE,
        "maskMarginFactor": shadow_geometry.MASK_MARGIN_FACTOR,
    }


def separation_guard(phase_range_b: float, phase_range_m: float) -> float:
    """max(T/4, 2 * max(W_B, W_M))."""
    return max(PHASE_BUDGET_METRES, 2.0 * max(phase_range_b, phase_range_m))


def attestation_mismatches(expectation: Mapping[str, Any], attestation: Any) -> List[str]:
    """Differences between a supplied attestation and the pinned expectation. Empty means they match."""
    if attestation is None:
        return ["source-equivalence attestation is missing"]
    try:
        shadow_inputs.check_attestation(attestation)
    except shadow_inputs.InputRefused as refusal:
        return [f"attestation refused ({refusal.code}): {refusal.detail}"]
    problems = []
    for key in ("baseline", "mutant", "mutation", "workflow", "host"):
        if _canonical(attestation[key]) != _canonical(expectation[key]):
            problems.append(f"attested {key} differs from the expectation")
    if attestation["host"]["sameVm"] is not True:
        problems.append("attested sameVm is not true")
    return problems


def gate_g1_provenance(
    expectation: Mapping[str, Any],
    attestation: Optional[Mapping[str, Any]],
    manifests: Mapping[Tuple[str, Phase], Mapping[str, Any]],
) -> GateResult:
    """Supplied source/workflow/host claims versus pinned expectations. No independent git/VM proof.
    Compare orchestration also assigns collector TRX execution/name validity to this gate."""
    problems: List[str] = []
    try:
        if expectation["designSha256"] != shadow_inputs.DESIGN_SHA256:
            problems.append("expected design digest differs from the frozen design")
        if list(expectation["mutation"]["changedPaths"]) != [shadow_inputs.MUTATION_PATH]:
            problems.append(f"expected mutation must change exactly {shadow_inputs.MUTATION_PATH}")
        baseline, mutant = expectation["baseline"], expectation["mutant"]
        if baseline["sourceCommit"] == mutant["sourceCommit"] or baseline["sourceTree"] == mutant["sourceTree"]:
            problems.append("baseline and mutant source identities must differ")
        if expectation["host"]["sameVm"] is not True:
            problems.append("expected host must claim one VM")
        problems.extend(attestation_mismatches(expectation, attestation))
        for build, identity in (("B", baseline), ("M", mutant)):
            commit = identity["sourceCommit"]
            for phase in PHASES:
                manifest = manifests.get((build, phase))
                if manifest is None:
                    problems.append(f"{_slot(build, phase)} manifest missing")
                    continue
                provenance = manifest["provenance"]
                if provenance.get("sourceCommit") != commit or provenance.get("sourceTree") != identity["sourceTree"]:
                    problems.append(f"{_slot(build, phase)} manifest source identity differs from the expected {build}")
                if provenance.get("revisionAgreement") != "agree":
                    problems.append(f"{_slot(build, phase)} assembly revisions do not agree")
                assemblies = provenance.get("assemblies")
                if not isinstance(assemblies, list) or not assemblies:
                    problems.append(f"{_slot(build, phase)} has no assembly provenance")
                    continue
                for assembly in assemblies:
                    version = assembly.get("informationalVersion")
                    if assembly.get("sourceRevision") != commit or (
                            version is not None and not str(version).endswith("+" + commit)):
                        problems.append(f"{_slot(build, phase)} assembly {assembly.get('name')!r} revision differs")
    except _MALFORMED as error:
        problems.append(f"provenance records malformed: {error!r}")
    return _result("G1", problems, "supplied source, mutation, workflow and host claims match the pinned "
                                   "expectation (supplied claims, not independently verified)")


def collector_trx_problems(record: shadow_inputs.TrxRecord, expected_test_name: str) -> List[str]:
    """Collector TRX validity owned by G1: one executed Passed case with the explicit name, clean counters."""
    problems = []
    if len(record.results) != 1:
        problems.append(f"collector TRX holds {len(record.results)} UnitTestResult records, expected 1")
    for result in record.results:
        if result.test_name != expected_test_name:
            problems.append(f"collector testName {result.test_name!r} is not expectedCollectorTestName")
        if result.outcome != "Passed":
            problems.append(f"collector outcome {result.outcome!r} is not Passed")
    for key, expected in COLLECTOR_COUNTERS.items():
        if record.counters.get(key) != expected:
            problems.append(f"collector counter {key}={record.counters.get(key)!r}, expected {expected}")
    for key in COLLECTOR_ZERO_IF_PRESENT:
        if record.counters.get(key, 0) != 0:
            problems.append(f"collector counter {key}={record.counters[key]} must be 0")
    return problems


def trx_computer_name(record: Optional[shadow_inputs.TrxRecord]) -> Optional[str]:
    if record is None or len(record.results) != 1:
        return None
    return record.results[0].computer_name


def gate_g2_device(manifests: Mapping[Tuple[str, Phase], Mapping[str, Any]]) -> GateResult:
    """Eight identical device sections, each matching the frozen software D3D11 facts.
    Compare orchestration also corroborates the supplied host claim against both TRX host names."""
    problems: List[str] = []
    sections = set()
    for build in shadow_inputs.BUILDS:
        for phase in PHASES:
            manifest = manifests.get((build, phase))
            if manifest is None:
                problems.append(f"{_slot(build, phase)} manifest missing")
                continue
            device = manifest.get("device") if isinstance(manifest, Mapping) else None
            if not isinstance(device, Mapping):
                problems.append(f"{_slot(build, phase)} has no device section")
                continue
            for key, expected in DEVICE_EXPECTATION.items():
                if key not in device or not _same_fact(device[key], expected):
                    problems.append(f"{_slot(build, phase)} device {key} is {device.get(key)!r}, expected {expected!r}")
            sections.add(_canonical(device))
    if len(sections) > 1:
        problems.append("device sections are not identical across the eight manifests")
    return _result("G2", problems, "eight identical software Direct3D11Native device sections")


def _dotted(document: Mapping[str, Any], path: str) -> Any:
    value: Any = document
    for part in path.split("."):
        value = value[part]
    return value


def _geometry_signature(manifest: Mapping[str, Any]) -> bytes:
    parts = []
    for station in manifest["stations"]:
        for key in ("on", "from", "to"):
            parts.append(_f32_signature(station[key], 3))
        for probe in station["probes"]:
            parts.append(_f32_signature(probe["world"], 3))
    scene = manifest["scene"]
    parts.append(_f32_signature(scene["across"], 3))
    parts.append(_f32_signature([scene["window"], scene["frameHalfExtent"]], 2))
    return b"".join(parts)


def gate_g3_scene(manifests: Mapping[Tuple[str, Phase], Mapping[str, Any]]) -> GateResult:
    """Frozen scene facts and f32 bit-identical world/on/from/to/across geometry across B/M/phases.
    frameCentre may vary with phase but must match within each paired phase."""
    problems: List[str] = []
    signatures: Dict[Tuple[str, Phase], bytes] = {}
    centres: Dict[Tuple[str, Phase], bytes] = {}
    for build in shadow_inputs.BUILDS:
        for phase in PHASES:
            manifest = manifests.get((build, phase))
            if manifest is None:
                problems.append(f"{_slot(build, phase)} manifest missing")
                continue
            try:
                scene = manifest["scene"]
                for path, expected in SCENE_EXPECTATION.items():
                    try:
                        actual = _dotted(scene, path)
                    except (KeyError, TypeError):
                        problems.append(f"{_slot(build, phase)} scene {path} missing")
                        continue
                    if not _same_fact(actual, expected):
                        problems.append(f"{_slot(build, phase)} scene {path} is {actual!r}, expected {expected!r}")
                signatures[(build, phase)] = _geometry_signature(manifest)
                centres[(build, phase)] = _f32_signature(scene["frameCentre"], 3)
            except _MALFORMED as error:
                problems.append(f"{_slot(build, phase)} scene geometry malformed: {error}")
    if signatures:
        reference_slot = next(iter(signatures))
        for slot, signature in signatures.items():
            if signature != signatures[reference_slot]:
                problems.append(f"{_slot(*slot)} station, probe or scene geometry is not float32 bit-identical "
                                f"to {_slot(*reference_slot)}")
    for phase in PHASES:
        pair = (centres.get(("B", phase)), centres.get(("M", phase)))
        if None not in pair and pair[0] != pair[1]:
            problems.append(f"frameCentre differs between B and M at phase ({phase[0]:g}, {phase[1]:g})")
    return _result("G3", problems, "frozen scene facts hold and recorded geometry is float32 bit-identical")


def gate_phase_inventory(
    expected_phases: Sequence[Sequence[float]], captures: Sequence[Tuple[str, Sequence[float]]]
) -> GateResult:
    """Exactly the four frozen phases, each captured once per build."""
    problems: List[str] = []
    try:
        expected = [shadow_inputs.parse_phase(phase) for phase in expected_phases]
        if len(expected) != len(PHASES) or set(expected) != set(PHASES):
            problems.append(f"expected phases {expected} are not exactly the frozen four")
        seen = set()
        for build, phase in captures:
            slot = (build, shadow_inputs.parse_phase(phase))
            if build not in shadow_inputs.BUILDS:
                problems.append(f"unknown build {build!r}")
            elif slot[1] not in PHASES:
                problems.append(f"{build} phase {slot[1]} is not a frozen phase")
            elif slot in seen:
                problems.append(f"{_slot(*slot)} is captured more than once")
            seen.add(slot)
        for build in shadow_inputs.BUILDS:
            for phase in PHASES:
                if (build, phase) not in seen:
                    problems.append(f"{_slot(build, phase)} capture missing")
    except shadow_inputs.InputRefused as refusal:
        problems.append(f"phase record refused: {refusal.detail}")
    except _MALFORMED as error:
        problems.append(f"capture records malformed: {error!r}")
    return _result(INVENTORY_GATE, problems, "four frozen phases, each captured once per build")


def _close(actual: float, expected: float) -> bool:
    return abs(actual - expected) <= CAMERA_RELATIVE_TOLERANCE * max(1.0, abs(actual), abs(expected))


def _matrix(camera: Mapping[str, Any], key: str) -> List[float]:
    values = camera[key]
    if not isinstance(values, (list, tuple)) or len(values) != 16 or not all(_is_number(v) for v in values):
        raise ValueError(f"camera {key} must be 16 finite numbers")
    return [float(v) for v in values]


def gate_g4_camera(
    cameras: Mapping[Phase, Mapping[str, Any]],
    retained_camera: Mapping[str, Any],
    on_point: Sequence[float],
) -> GateResult:
    """Projection and view rotation match, (0, 0) viewProjection bitwise, achieved phase within 0.02 px."""
    problems: List[str] = []
    try:
        if set(cameras) != set(PHASES):
            problems.append("cameras must cover exactly the four frozen phases")
        projection_ref = _matrix(retained_camera, "projection")
        view_ref = _matrix(retained_camera, "view")
        zero = cameras.get(PHASES[0])
        for phase in PHASES:
            camera = cameras.get(phase)
            if camera is None:
                continue
            label = f"phase ({phase[0]:g}, {phase[1]:g})"
            origin = camera["renderOrigin"]
            if not (isinstance(origin, (list, tuple)) and len(origin) == 3
                    and all(_is_number(v) and float(v) == 0.0 for v in origin)):
                problems.append(f"{label} render origin is not (0, 0, 0)")
            if not all(_close(a, b) for a, b in zip(_matrix(camera, "projection"), projection_ref)):
                problems.append(f"{label} projection differs from the retained projection")
            view = _matrix(camera, "view")
            if not all(_close(view[k], view_ref[k]) for k in _VIEW_ROTATION):
                problems.append(f"{label} view rotation differs from the retained view")
            if phase == PHASES[0] and (_f32_signature(camera["viewProjection"], 16)
                                       != _f32_signature(retained_camera["viewProjection"], 16)):
                problems.append("phase (0, 0) viewProjection is not bit-identical to the retained one")
            if zero is not None:
                achieved = shadow_geometry.achieved_phase(zero, camera, on_point)
                if (abs(achieved[0] - phase[0]) > PHASE_TOLERANCE_PX
                        or abs(achieved[1] - phase[1]) > PHASE_TOLERANCE_PX):
                    problems.append(f"{label} achieved phase ({achieved[0]:.6f}, {achieved[1]:.6f}) px is more than "
                                    f"{PHASE_TOLERANCE_PX} px from nominal")
    except _MALFORMED as error:
        problems.append(f"camera records malformed: {error}")
    return _result("G4", problems, "projection, view rotation, (0, 0) viewProjection and achieved phases match")


def gate_g5_matched_data(
    plain: Mapping[Phase, Tuple[bytes, bytes]],
    atlas: Mapping[Phase, Tuple[shadow_inputs.AtlasRow, shadow_inputs.AtlasRow]],
) -> GateResult:
    """Byte-identical paired plain captures and active atlas rows, agreeing row metadata, and a supplied
    binding claim that is true for both builds. The binding is never independently verified here."""
    problems: List[str] = []
    if set(plain) != set(PHASES):
        problems.append("paired plain captures must cover exactly the four frozen phases")
    if set(atlas) != set(PHASES):
        problems.append("paired atlas rows must cover exactly the four frozen phases")
    for phase in PHASES:
        label = f"phase ({phase[0]:g}, {phase[1]:g})"
        pair = plain.get(phase)
        if pair is not None:
            plain_b, plain_m = pair
            if len(plain_b) != shadow_inputs.CAPTURE_BYTES or len(plain_m) != shadow_inputs.CAPTURE_BYTES:
                problems.append(f"{label} plain capture length differs from {shadow_inputs.CAPTURE_BYTES}")
            elif bytes(plain_b) != bytes(plain_m):
                problems.append(f"{label} plain(B) and plain(M) are not byte identical")
        rows = atlas.get(phase)
        if rows is None:
            continue
        row_b, row_m = rows
        if (row_b.build, row_m.build) != ("B", "M"):
            problems.append(f"{label} atlas rows are not a B/M pair")
        if row_b.phase != phase or row_m.phase != phase:
            problems.append(f"{label} atlas row phase labels are {row_b.phase} and {row_m.phase}")
        if (row_b.active_row, row_b.atlas_rows, row_b.face_resolution) != (
                row_m.active_row, row_m.atlas_rows, row_m.face_resolution):
            problems.append(f"{label} atlas row metadata differs between B and M")
        if not (row_b.binding_verified and row_m.binding_verified):
            problems.append(f"{label} receiver binding does not reference the atlas")
        if (len(row_b.data) != shadow_inputs.ATLAS_ROW_BYTES or bytes(row_b.data) != bytes(row_m.data)
                or row_b.sha256 != row_m.sha256):
            problems.append(f"{label} active atlas rows are not byte identical")
    return _result("G5", problems, "paired plain captures and active atlas rows are byte identical, and the supplied "
                                   "binding claim is true for both builds (not independently verified)")


def differing_pixels(soft_b: bytes, soft_m: bytes) -> Tuple[shadow_geometry.Pixel, ...]:
    """Pixels where any of the four RGBA bytes differ, sorted by (x, y)."""
    if len(soft_b) != shadow_inputs.CAPTURE_BYTES or len(soft_m) != shadow_inputs.CAPTURE_BYTES:
        raise ValueError("paired captures must both be 192 by 192 RGBA")
    if soft_b == soft_m:
        return ()
    width = shadow_geometry.VIEWPORT_WIDTH
    row_bytes = width * 4
    found = []
    for y in range(shadow_geometry.VIEWPORT_HEIGHT):
        start = y * row_bytes
        if soft_b[start:start + row_bytes] == soft_m[start:start + row_bytes]:
            continue
        for x in range(width):
            offset = start + x * 4
            if soft_b[offset:offset + 4] != soft_m[offset:offset + 4]:
                found.append((x, y))
    return tuple(sorted(found))


def classify_pixel_differences(
    soft_b: bytes,
    soft_m: bytes,
    certified: FrozenSet[shadow_geometry.Pixel],
    mask: FrozenSet[shadow_geometry.Pixel],
) -> DifferenceReport:
    """Split every differing pixel into certified inside mask, certified outside mask and outside F."""
    inside, outside_mask, outside_certified = [], [], []
    for pixel in differing_pixels(soft_b, soft_m):
        if pixel not in certified:
            outside_certified.append(pixel)
        elif pixel in mask:
            inside.append(pixel)
        else:
            outside_mask.append(pixel)
    return DifferenceReport(tuple(inside), tuple(outside_mask), tuple(outside_certified))


def difference_record(report: DifferenceReport) -> Mapping[str, Any]:
    """JSON-ready difference record. Outside-F pixels carry no floor-confinement conclusion."""

    def group(pixels: Sequence[shadow_geometry.Pixel]) -> Mapping[str, Any]:
        return {"count": len(pixels), "pixels": [[x, y] for x, y in pixels]}

    return {
        "certifiedInsideMask": group(report.certified_inside_mask),
        "certifiedOutsideMask": group(report.certified_outside_mask),
        OUTSIDE_CERTIFIED_LABEL: group(report.outside_certified),
    }


def gate_g6_exercised_and_confined(facts: Mapping[Phase, G6PhaseFacts]) -> GateResult:
    """Per phase: a crossing-band difference, no certified difference outside the mask, nonempty controls.
    Compare orchestration assigns failed floor certification or required-footprint coverage to G6."""
    problems: List[str] = []
    if set(facts) != set(PHASES):
        problems.append("G6 facts must cover exactly the four frozen phases")
    for phase in PHASES:
        fact = facts.get(phase)
        if fact is None:
            continue
        label = f"phase ({phase[0]:g}, {phase[1]:g})"
        if not fact.differing_certified & fact.crossing_band_pixels:
            problems.append(f"{label} soft(M) does not differ from soft(B) in a certified station 3, 4 or 5 band pixel")
        escaped = sorted(fact.differing_certified - fact.crossing_mask)
        if escaped:
            problems.append(f"{label} {len(escaped)} certified differences lie outside the crossing mask, "
                            f"first {escaped[0]}")
        if len(fact.control_outside_pixels) != len(shadow_geometry.NON_SEAM_CONTROLS):
            problems.append(f"{label} needs one outside-mask set per non-seam control")
            continue
        for index, pixels in enumerate(fact.control_outside_pixels):
            if not pixels:
                problems.append(f"{label} control {index} has no certified footprint wholly outside the mask")
            elif pixels & fact.differing_certified:
                problems.append(f"{label} control {index} soft captures differ outside the mask")
    return _result("G6", problems, "the mutant changes certified crossing-band pixels only inside the crossing mask, "
                                   "and both controls are covered outside it")


def _f32_value(value: Any) -> Optional[float]:
    if not _is_number(value):
        return None
    try:
        return struct.unpack("<f", struct.pack("<f", value))[0]
    except (OverflowError, struct.error):
        return None


# The frozen endpoint bounds live in shadow_metrics. The original metric compares them as C# float32.
_LIT_F32 = struct.unpack("<f", struct.pack("<f", shadow_metrics.LIT_ENDPOINT_MIN))[0]
_SHADOW_F32 = struct.unpack("<f", struct.pack("<f", shadow_metrics.SHADOW_ENDPOINT_MAX))[0]


def gate_g7_preconditions(facts: Mapping[Tuple[str, Phase], G7PhaseFacts]) -> GateResult:
    """All eight slots, plain red > 24 and 13 finite endpoint pairs per metric.
    Original values and 0.9f/0.1f boundaries are f32. Candidate values/boundaries remain double.
    Both comparisons are strict and do not change the frozen thresholds."""
    problems: List[str] = []
    for build in shadow_inputs.BUILDS:
        for phase in PHASES:
            fact = facts.get((build, phase))
            label = _slot(build, phase)
            if fact is None:
                problems.append(f"{label} precondition facts missing")
                continue
            if not (_is_number(fact.min_plain_red) and fact.min_plain_red > 24):
                problems.append(f"{label} minimum plain red {fact.min_plain_red!r} is not above 24")
            for metric, pairs in (("original", fact.original_endpoints), ("candidate", fact.candidate_endpoints)):
                if len(pairs) != shadow_inputs.STATIONS:
                    problems.append(f"{label} {metric} has {len(pairs)} endpoint pairs, expected 13")
                    continue
                for station, pair in enumerate(pairs):
                    lit, shadow = pair
                    if metric == "original":
                        lit_value, shadow_value = _f32_value(lit), _f32_value(shadow)
                        lit_bound, shadow_bound = _LIT_F32, _SHADOW_F32
                    else:
                        lit_value = float(lit) if _is_number(lit) else None
                        shadow_value = float(shadow) if _is_number(shadow) else None
                        lit_bound, shadow_bound = shadow_metrics.LIT_ENDPOINT_MIN, shadow_metrics.SHADOW_ENDPOINT_MAX
                    if lit_value is None or shadow_value is None:
                        problems.append(f"{label} {metric} station {station} endpoint is not finite")
                    elif not (lit_value > lit_bound and shadow_value < shadow_bound):
                        problems.append(f"{label} {metric} station {station} endpoints ({lit!r}, {shadow!r}) are not "
                                        f"above {shadow_metrics.LIT_ENDPOINT_MIN:g} lit and below "
                                        f"{shadow_metrics.SHADOW_ENDPOINT_MAX:g} shadowed")
    return _result("G7", problems, "plain red above 24 and every endpoint pair strictly inside its bound")


def classify_metric(
    metric: str,
    gates: Sequence[GateResult],
    bumps_b: Mapping[Phase, float],
    bumps_m: Mapping[Phase, float],
) -> Classification:
    """Section 9 outcome for one metric, gates first, in the frozen order."""

    def invalid(reason: str) -> Classification:
        return Classification(metric, INVALID, reason, None, None, None, None)

    present = set()
    for gate in gates:
        if not gate.passed:
            return invalid(f"{gate.gate} failed: {gate.reason}")
        present.add(gate.gate)
    missing = [gate for gate in GATES if gate not in present]
    if missing:
        return invalid(f"{missing[0]} result missing")
    if set(bumps_b) != set(PHASES) or set(bumps_m) != set(PHASES):
        return invalid("bump series must cover exactly the four frozen phases")
    values_b = [bumps_b[phase] for phase in PHASES]
    values_m = [bumps_m[phase] for phase in PHASES]
    if not all(_is_number(v) for v in values_b + values_m):
        return invalid("a bump is not finite")

    range_b = max(values_b) - min(values_b)
    range_m = max(values_m) - min(values_m)
    if range_b > PHASE_BUDGET_METRES or range_m > PHASE_BUDGET_METRES:
        return Classification(metric, INCONCLUSIVE_PHASE,
                              f"phase range W_B {range_b!r} m or W_M {range_m!r} m exceeds T/4",
                              range_b, range_m, None, None)
    separation = min(abs(v) for v in values_m) - max(abs(v) for v in values_b)
    guard = separation_guard(range_b, range_m)
    if any(abs(v) <= T_METRES for v in values_m):
        return Classification(metric, INCONCLUSIVE_UNDETECTED, "the mutant bump is at or below T at some phase",
                              range_b, range_m, separation, guard)
    if separation <= guard:
        return Classification(metric, INCONCLUSIVE_UNDETECTED,
                              f"separation {separation!r} m is not above the guard {guard!r} m",
                              range_b, range_m, separation, guard)
    if any(abs(v) > T_METRES for v in values_b):
        return Classification(metric, BASELINE_EXCEEDS_BOUND,
                              "control detected and the baseline bump exceeds T at some phase",
                              range_b, range_m, separation, guard)
    return Classification(metric, BASELINE_WITHIN_BOUND,
                          "control detected and the baseline bump is within T at every phase",
                          range_b, range_m, separation, guard)


def _json_number(value: Any) -> Optional[float]:
    return float(value) if _is_number(value) else None


def classification_record(
    classification: Classification,
    bumps_b: Mapping[Phase, float],
    bumps_m: Mapping[Phase, float],
) -> Mapping[str, Any]:
    """JSON-ready classification record with scope and non-claims."""

    def series(bumps: Mapping[Phase, float]) -> List[List[Any]]:
        return [[phase[0], phase[1], _json_number(bumps[phase])] for phase in PHASES if phase in bumps]

    return {
        "metric": classification.metric,
        "outcome": classification.outcome,
        "reason": classification.reason,
        "phaseRange": {"B": classification.phase_range_b, "M": classification.phase_range_m},
        "separation": classification.separation,
        "separationGuard": classification.separation_guard,
        "bumps": {"B": series(bumps_b), "M": series(bumps_m)},
        "scope": CLASSIFICATION_SCOPE,
        "nonClaims": list(NON_CLAIMS),
    }
