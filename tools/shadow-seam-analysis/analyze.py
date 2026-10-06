"""Offline point-shadow seam analyzer with two explicit modes and no implicit discovery.

    python3 analyze.py retained --artifact <zip> --output <json>
    python3 analyze.py compare --expectation <descriptor.json> --bundle <zip-or-dir> --output <json>

Exit 0 means the requested analysis completed, not that a shader passed. Invalid input or a failed
gate returns 2 with an INVALID report when one can safely be formed. Unhandled internal errors fail.
It never renders, starts a process, accesses the network, invokes git or changes an input.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import posixpath
import stat
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Dict, FrozenSet, List, Mapping, Optional, Sequence, Tuple

import shadow_compare
import shadow_geometry
import shadow_inputs
import shadow_metrics
from shadow_compare import GATES, INVENTORY_GATE, PHASES, GateResult
from shadow_inputs import BUILDS, InputRefused
from shadow_metrics import MetricInvalid

EXIT_COMPLETED = 0
EXIT_INVALID = 2
TOOL = "shadow-seam-analysis"

# G4 measures each achieved phase at this station's recorded on-point, against the build's (0, 0) camera.
PHASE_REFERENCE_STATION = 4

# The output guard enumerates at most this many bundle entries. A larger directory cannot be a
# valid bundle, and no report is written rather than risk writing over an unlisted input.
GUARD_MAX_ENTRIES = 4096

COMPLETED = "COMPLETED"
INVALID = "INVALID"
DIAGNOSTIC = "DIAGNOSTIC"
HISTORICAL = "HISTORICAL"
NOT_AVAILABLE = {
    "mutant": "the retained artifact holds no clamped-face mutant capture",
    "phases": "the retained artifact holds phase (0, 0) only",
    "atlas": "the retained artifact holds no active-atlas row or binding record",
}

Pixel = shadow_geometry.Pixel
Overlaps = List[Tuple[int, int, shadow_geometry.Polygon, float]]
OverlapSource = Callable[[Mapping[str, Any], shadow_geometry.StationBand], Overlaps]


def _json_ready(value: Any) -> Any:
    """Plain JSON values only: tuples become lists and non-finite numbers become null."""
    if isinstance(value, Mapping):
        return {str(key): _json_ready(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_ready(item) for item in value]
    if isinstance(value, (set, frozenset)):
        return [_json_ready(item) for item in sorted(value)]
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def _slot_label(build: str, phase: Tuple[float, float]) -> str:
    return f"{build} ({phase[0]:g}, {phase[1]:g})"


def _camera_key(camera: Mapping[str, Any]) -> Tuple[Tuple[float, ...], Tuple[float, ...]]:
    return (tuple(float(v) for v in camera["viewProjection"]), tuple(float(v) for v in camera["renderOrigin"]))


def _probe_pixels(evidence: shadow_inputs.Evidence) -> List[Pixel]:
    return [(p["pixel"][0], p["pixel"][1]) for s in evidence.manifest["stations"] for p in s["probes"]]


def _bands(evidence: shadow_inputs.Evidence) -> List[shadow_geometry.StationBand]:
    return [shadow_geometry.station_band((s["on"][0], s["on"][2])) for s in evidence.manifest["stations"]]


def _band_overlaps(camera: Mapping[str, Any], band: shadow_geometry.StationBand) -> Overlaps:
    return shadow_geometry.overlapping_footprints(camera, shadow_geometry.band_polygon(band))


def _read_captures(read: Callable[[str, int], bytes], manifest_name: str, manifest_raw: bytes) -> Dict[str, bytes]:
    """Read the captures a manifest names, beside the manifest, before full evidence validation."""
    files = shadow_inputs.evidence_capture_files(shadow_inputs.load_json(manifest_raw))
    directory = posixpath.dirname(manifest_name)
    return {name: read(posixpath.join(directory, name) if directory else name, shadow_inputs.CAPTURE_BYTES)
            for name in files}


def _original_measurement(evidence: shadow_inputs.Evidence) -> Dict[str, Any]:
    reaches = shadow_metrics.original_station_reaches(evidence)
    statistic = shadow_metrics.detrend_statistic([r.reach for r in reaches], single_precision=True)
    stations = evidence.manifest["stations"]
    return {
        "metric": "original",
        "reach": [r.reach for r in reaches],
        "residuals": list(statistic.residuals),
        "bump": statistic.bump,
        "bumpMillimetres": statistic.bump * 1000.0,
        "worstElsewhere": statistic.worst_elsewhere,
        "minPlainRed": min(p["plainRed"] for s in stations for p in s["probes"]),
        "endpoints": [[float(s["probes"][0]["ratio"]), float(s["probes"][-1]["ratio"])] for s in stations],
    }


@dataclass(frozen=True)
class _Candidate:
    row: Dict[str, Any]
    band_pixels: Tuple[FrozenSet[Pixel], ...]


def _candidate_measurement(evidence: shadow_inputs.Evidence, overlaps_for: OverlapSource) -> _Candidate:
    camera = evidence.manifest["camera"]
    width = shadow_geometry.VIEWPORT_WIDTH
    reaches: List[float] = []
    endpoints: List[List[float]] = []
    band_pixels: List[FrozenSet[Pixel]] = []
    for band in _bands(evidence):
        overlaps = overlaps_for(camera, band)
        samples = []
        for i, j, footprint, _ in overlaps:
            index = (j * width + i) * 4
            samples.append((footprint, evidence.plain[index], evidence.soft[index]))
        result = shadow_metrics.area_band_reach(band, samples)
        reaches.append(result.reach)
        endpoints.append([result.lit_endpoint_mean, result.shadow_endpoint_mean])
        band_pixels.append(frozenset((i, j) for i, j, _, _ in overlaps))
    statistic = shadow_metrics.detrend_statistic(reaches, single_precision=False)
    read_set = frozenset().union(*band_pixels)
    row = {
        "metric": "candidate",
        "reach": reaches,
        "residuals": list(statistic.residuals),
        "bump": statistic.bump,
        "bumpMillimetres": statistic.bump * 1000.0,
        "worstElsewhere": statistic.worst_elsewhere,
        "minPlainRed": min(evidence.plain[(j * width + i) * 4] for i, j in read_set),
        "endpoints": endpoints,
    }
    return _Candidate(row, tuple(band_pixels))


def _achieved_phase(manifests: Mapping[Tuple[str, Tuple[float, float]], Mapping[str, Any]], build: str,
                    phase: Tuple[float, float]) -> Optional[List[float]]:
    """The achieved [x, y] shift in pixels exactly as G4 computes it, or None when it cannot be formed."""
    zero, current = manifests.get((build, PHASES[0])), manifests.get((build, phase))
    if zero is None or current is None:
        return None
    try:
        achieved = shadow_geometry.achieved_phase(
            zero["camera"], current["camera"], zero["stations"][PHASE_REFERENCE_STATION]["on"])
    except (KeyError, TypeError, ValueError, IndexError):
        return None
    return [achieved[0], achieved[1]]


def _certificate_record(certificate: shadow_geometry.FloorCertificate) -> Dict[str, Any]:
    return {"valid": certificate.valid, "reason": certificate.reason,
            "certifiedPixelCount": len(certificate.certified_pixels)}


def _coverage_record(coverage: shadow_geometry.CoverageResult) -> Dict[str, Any]:
    return {"valid": coverage.valid, "reason": coverage.reason,
            "uncovered": [[kind, value] for kind, value in coverage.uncovered]}


def _historical_assertion() -> Dict[str, Any]:
    return {
        "outcome": shadow_inputs.RETAINED_ASSERTION_OUTCOME,
        "testName": shadow_inputs.HISTORICAL_TEST_NAME,
        "boundMetres": shadow_compare.T_METRES,
    }


def _historical_row(bump: float, worst: float, reaches: Sequence[float]) -> Dict[str, Any]:
    """The recorded historical statistic, never recomputed."""
    return {
        "label": "retained-historical",
        "metric": "original",
        "bump": bump,
        "bumpMillimetres": bump * 1000.0,
        "worstElsewhere": worst,
        "reach": list(reaches),
        "status": HISTORICAL,
    }


def _pinned_historical_row() -> Dict[str, Any]:
    return _historical_row(shadow_inputs.RETAINED_BUMP, shadow_inputs.RETAINED_WORST_ELSEWHERE,
                           shadow_inputs.RETAINED_STATION_REACHES)


def _check_pinned_retained(evidence: shadow_inputs.Evidence, historical: shadow_inputs.TrxResult) -> None:
    """The pinned retained constants used by compare mode must equal the archive retained mode read."""
    manifest = evidence.manifest
    mismatches = []
    for key, pinned in shadow_inputs.RETAINED_CAMERA.items():
        if [float(v) for v in manifest["camera"][key]] != [float(v) for v in pinned]:
            mismatches.append(f"camera {key}")
    statistic = manifest["statistic"]
    if (float(statistic["bump"]), float(statistic["worstElsewhere"])) != (
            shadow_inputs.RETAINED_BUMP, shadow_inputs.RETAINED_WORST_ELSEWHERE):
        mismatches.append("statistic")
    if [float(s["reach"]) for s in manifest["stations"]] != list(shadow_inputs.RETAINED_STATION_REACHES):
        mismatches.append("station reaches")
    soft = [c["sha256"] for c in manifest["captures"] if c["name"] == "soft"]
    if soft != [shadow_inputs.RETAINED_SOFT_SHA256]:
        mismatches.append("soft capture digest")
    if (historical.test_name, historical.outcome) != (
            shadow_inputs.HISTORICAL_TEST_NAME, shadow_inputs.RETAINED_ASSERTION_OUTCOME):
        mismatches.append("historical assertion")
    if mismatches:
        raise RuntimeError(f"pinned retained constants disagree with the retained archive: {mismatches}")


def run_retained(
    artifact: Path,
    *,
    expected_sha256: str = shadow_inputs.RETAINED_ARCHIVE_SHA256,
    limits: shadow_inputs.ReadLimits = shadow_inputs.DEFAULT_LIMITS,
) -> Mapping[str, Any]:
    """REPORT-ONLY analysis of the single retained artifact. Fabricates no paired-control data."""
    archive = shadow_inputs.read_file(artifact, limits.max_total_bytes)
    artifact_sha = shadow_inputs.sha256_hex(archive)
    if artifact_sha != expected_sha256:
        raise InputRefused("sha256", f"artifact digest {artifact_sha} is not the pinned {expected_sha256}")
    with shadow_inputs.open_zip_bytes(archive, limits) as source:
        manifest_raw = source.read(shadow_inputs.RETAINED_MANIFEST_ENTRY, shadow_inputs.MAX_MANIFEST_BYTES)
        captures = _read_captures(source.read, shadow_inputs.RETAINED_MANIFEST_ENTRY, manifest_raw)
        trx_raw = source.read(shadow_inputs.RETAINED_TRX_ENTRY, shadow_inputs.MAX_MANIFEST_BYTES)
    evidence = shadow_inputs.validate_evidence(manifest_raw, captures)
    trx = shadow_inputs.parse_trx(trx_raw)
    if len(trx.results) != 1:
        raise InputRefused("schema", f"retained TRX holds {len(trx.results)} results, expected 1")
    historical = trx.results[0]
    _check_pinned_retained(evidence, historical)

    camera = evidence.manifest["camera"]
    certificate = shadow_geometry.certify_visible_floor(camera, shadow_geometry.approved_draw_records())
    coverage = shadow_geometry.check_measurement_coverage(
        camera, certificate, _probe_pixels(evidence), _bands(evidence), shadow_geometry.NON_SEAM_CONTROLS
    )
    if not coverage.valid:
        raise MetricInvalid("coverage", coverage.reason)
    original = _original_measurement(evidence)
    candidate = _candidate_measurement(evidence, _band_overlaps).row
    statistic = evidence.manifest["statistic"]
    rows = [
        _historical_row(statistic["bump"], statistic["worstElsewhere"],
                        [s["reach"] for s in evidence.manifest["stations"]]),
        {"label": "retained-replay-original", **original, "status": shadow_compare.REPORT_ONLY},
        {"label": "retained-candidate-area-band", **candidate, "status": shadow_compare.REPORT_ONLY},
    ]
    report = {
        "tool": TOOL,
        "mode": "retained",
        "status": shadow_compare.REPORT_ONLY,
        "designSha256": shadow_inputs.DESIGN_SHA256,
        "units": shadow_metrics.UNITS,
        "parameters": shadow_compare.frozen_parameters_record(),
        "inputs": {
            "artifactSha256": artifact_sha,
            "manifestSha256": evidence.manifest_sha256,
            "trxSha256": shadow_inputs.sha256_hex(trx_raw),
            "captureSha256": {c["name"]: shadow_inputs.sha256_hex(captures[c["file"]])
                              for c in evidence.manifest["captures"]},
        },
        "coverage": {"certificate": _certificate_record(certificate), "measurements": _coverage_record(coverage),
                     "drawList": "approved Wall callback source draw list, not a supplied attestation"},
        "rows": rows,
        "historicalAssertion": {
            "outcome": historical.outcome,
            "testName": historical.test_name,
            "boundMetres": shadow_compare.T_METRES,
        },
        "notAvailable": dict(NOT_AVAILABLE),
    }
    return _json_ready(report)


def _with_notes(result: GateResult, notes: Sequence[str]) -> GateResult:
    if not notes:
        return result
    reasons = ([] if result.passed else [result.reason]) + list(notes)
    return GateResult(result.gate, False, "; ".join(reasons))


def _gate_records(gates: Sequence[GateResult]) -> List[Dict[str, Any]]:
    return [{"gate": g.gate, "passed": g.passed, "reason": g.reason} for g in gates]


def _invalid_compare_report(
    expectation_sha: Optional[str], gates: Sequence[GateResult], files: Mapping[str, str]
) -> Mapping[str, Any]:
    """An INVALID compare report when the experiment could not be evaluated at all."""
    classifications = {
        metric: shadow_compare.classification_record(shadow_compare.classify_metric(metric, gates, {}, {}), {}, {})
        for metric in ("original", "candidate")
    }
    report = {
        "tool": TOOL,
        "mode": "compare",
        "status": INVALID,
        "designSha256": shadow_inputs.DESIGN_SHA256,
        "units": shadow_metrics.UNITS,
        "parameters": shadow_compare.frozen_parameters_record(),
        "inputs": {"expectationSha256": expectation_sha, "files": dict(files), "trx": {}},
        "claims": {
            "sourceEquivalence": {"kind": "supplied-claim", "matchesExpectation": False},
            "workflow": {"kind": "supplied-claim", "value": None},
            "host": {"kind": "supplied-claim", "value": None, "trxComputerNamesAgree": False},
            "independentlyVerified": False,
        },
        "rows": [_pinned_historical_row()],
        "historicalAssertion": _historical_assertion(),
        "gates": _gate_records(gates),
        "coverage": [],
        "differences": [],
        "classifications": classifications,
        "reportedOnly": {"baselineZeroSoftEqualsRetained": None},
    }
    return _json_ready(report)


@dataclass(frozen=True)
class _Trx:
    sha256: Optional[str]
    record: Optional[shadow_inputs.TrxRecord]
    problem: Optional[str]


@dataclass(frozen=True)
class _SlotMeasure:
    certificate: shadow_geometry.FloorCertificate
    coverage: shadow_geometry.CoverageResult
    controls: List[Dict[str, Any]]
    control_outside: Tuple[FrozenSet[Pixel], ...]
    original: Optional[Dict[str, Any]]
    candidate: Optional[_Candidate]


class _CompareAnalysis:
    """One paired B/M analysis over an opened bundle. Failures are assigned to their gates."""

    def __init__(self, expected: Mapping[str, Any], expectation_sha: str, source: shadow_inputs.Bundle) -> None:
        self.expected = expected
        self.expectation_sha = expectation_sha
        self.source = source
        self.files: Dict[str, str] = {}
        self.notes: Dict[str, List[str]] = {gate: [] for gate in GATES + (INVENTORY_GATE,)}
        self._overlap_cache: Dict[Any, Overlaps] = {}
        self._certificate_cache: Dict[Any, shadow_geometry.FloorCertificate] = {}

    def read(self, name: str, limit: int) -> bytes:
        data = self.source.read(name, limit)
        self.files[name] = shadow_inputs.sha256_hex(data)
        return data

    def overlaps(self, camera: Mapping[str, Any], band: shadow_geometry.StationBand) -> Overlaps:
        key = (_camera_key(camera), band.on)
        if key not in self._overlap_cache:
            self._overlap_cache[key] = _band_overlaps(camera, band)
        return self._overlap_cache[key]

    def certificate(self, camera: Mapping[str, Any], draws: Any) -> shadow_geometry.FloorCertificate:
        if draws is None:
            return shadow_geometry.FloorCertificate(
                False, "approved draw list unavailable: attestation missing or malformed", frozenset())
        key = (_camera_key(camera), json.dumps(draws, sort_keys=True))
        if key not in self._certificate_cache:
            self._certificate_cache[key] = shadow_geometry.certify_visible_floor(camera, draws)
        return self._certificate_cache[key]

    def _attestation(self) -> Any:
        try:
            return shadow_inputs.load_json(self.read(self.expected["attestation"], shadow_inputs.MAX_MANIFEST_BYTES))
        except InputRefused as refusal:
            self.notes["G1"].append(f"attestation unavailable ({refusal.code}): {refusal.detail}")
            return None

    def _trx(self, build: str) -> _Trx:
        name = self.expected["trx"][build]
        try:
            raw = self.read(name, shadow_inputs.MAX_MANIFEST_BYTES)
        except InputRefused as refusal:
            return _Trx(None, None, f"collector TRX {build} unavailable ({refusal.code}): {refusal.detail}")
        try:
            return _Trx(self.files[name], shadow_inputs.parse_trx(raw), None)
        except InputRefused as refusal:
            return _Trx(self.files[name], None, f"collector TRX {build} refused ({refusal.code}): {refusal.detail}")

    def _evidence(self, label: str, record: Mapping[str, Any]) -> Optional[shadow_inputs.Evidence]:
        try:
            manifest_raw = self.read(record["manifest"], shadow_inputs.MAX_MANIFEST_BYTES)
            captures = _read_captures(self.read, record["manifest"], manifest_raw)
            return shadow_inputs.validate_evidence(manifest_raw, captures)
        except InputRefused as refusal:
            self.notes[INVENTORY_GATE].append(f"{label} evidence refused ({refusal.code}): {refusal.detail}")
            return None

    def _atlas(self, label: str, build: str, phase: Tuple[float, float], record: Mapping[str, Any]
               ) -> Optional[shadow_inputs.AtlasRow]:
        commit = self.expected["baseline" if build == "B" else "mutant"]["sourceCommit"]
        try:
            sidecar = self.read(record["atlasSidecar"], shadow_inputs.MAX_MANIFEST_BYTES)
            row = self.read(record["atlasRow"], shadow_inputs.ATLAS_ROW_BYTES)
            return shadow_inputs.validate_atlas_row(sidecar, row, build=build, phase=phase, source_commit=commit)
        except InputRefused as refusal:
            self.notes["G5"].append(f"{label} active atlas row refused ({refusal.code}): {refusal.detail}")
            return None

    def _controls(self, camera: Mapping[str, Any], certificate: shadow_geometry.FloorCertificate
                  ) -> Tuple[List[Dict[str, Any]], Tuple[FrozenSet[Pixel], ...]]:
        certified = certificate.certified_pixels if certificate.valid else frozenset()
        records, outside_sets = [], []
        for index, rect in enumerate(shadow_geometry.NON_SEAM_CONTROLS):
            overlaps = [(i, j, area) for i, j, _, area
                        in shadow_geometry.overlapping_footprints(camera, shadow_geometry.rect_polygon(rect))
                        if (i, j) in certified]
            mask = shadow_geometry.crossing_mask(camera, shadow_geometry.LIGHT_POSITION,
                                                 [(i, j) for i, j, _ in overlaps])
            outside = [(i, j, area) for i, j, area in overlaps if (i, j) not in mask]
            records.append({"index": index, "outsideMaskArea": math.fsum(area for _, _, area in outside),
                            "outsideMaskPixelCount": len(outside)})
            outside_sets.append(frozenset((i, j) for i, j, _ in outside))
        return records, tuple(outside_sets)

    def _measure(self, label: str, evidence: shadow_inputs.Evidence, draws: Any) -> _SlotMeasure:
        camera = evidence.manifest["camera"]
        certificate = self.certificate(camera, draws)
        coverage = shadow_geometry.check_measurement_coverage(
            camera, certificate, _probe_pixels(evidence), _bands(evidence), shadow_geometry.NON_SEAM_CONTROLS
        )
        if not certificate.valid:
            self.notes["G6"].append(f"{label} visible-floor certificate invalid: {certificate.reason}")
        elif not coverage.valid:
            self.notes["G6"].append(f"{label} required footprint coverage invalid: {coverage.reason}")
        try:
            controls, control_outside = self._controls(camera, certificate)
        except ValueError as error:
            # A camera that cannot project the controls, such as one placing them behind it.
            self.notes["G6"].append(f"{label} control geometry unavailable: {error}")
            controls, control_outside = [], (frozenset(), frozenset())
        original = None
        try:
            original = _original_measurement(evidence)
        except MetricInvalid as invalid:
            self.notes[INVENTORY_GATE].append(f"{label} original replay refused ({invalid.code}): {invalid.detail}")
        candidate = None
        try:
            candidate = _candidate_measurement(evidence, self.overlaps)
        except MetricInvalid as invalid:
            gate = "G6" if invalid.code == "coverage" else "G7"
            self.notes[gate].append(f"{label} candidate metric refused ({invalid.code}): {invalid.detail}")
        except ValueError as error:
            self.notes["G6"].append(f"{label} candidate band geometry unavailable: {error}")
        return _SlotMeasure(certificate, coverage, controls, control_outside, original, candidate)

    def report(self) -> Mapping[str, Any]:
        expected = self.expected
        inventory = shadow_compare.gate_phase_inventory(
            expected["phases"], [(c["build"], c["phase"]) for c in expected["captures"]]
        )
        if not inventory.passed:
            gates = [GateResult(g, False, "not evaluated: the phase inventory failed") for g in GATES] + [inventory]
            return _invalid_compare_report(self.expectation_sha, gates, self.files)
        slots = {(c["build"], shadow_inputs.parse_phase(c["phase"])): c for c in expected["captures"]}

        attestation = self._attestation()
        trx = {build: self._trx(build) for build in BUILDS}
        evidence: Dict[Tuple[str, Tuple[float, float]], shadow_inputs.Evidence] = {}
        atlas: Dict[Tuple[str, Tuple[float, float]], shadow_inputs.AtlasRow] = {}
        for build in BUILDS:
            for phase in PHASES:
                label = _slot_label(build, phase)
                found = self._evidence(label, slots[(build, phase)])
                if found is not None:
                    evidence[(build, phase)] = found
                row = self._atlas(label, build, phase, slots[(build, phase)])
                if row is not None:
                    atlas[(build, phase)] = row
        manifests = {slot: found.manifest for slot, found in evidence.items()}
        draws = attestation.get("drawList") if isinstance(attestation, dict) else None
        measured = {slot: self._measure(_slot_label(*slot), found, draws) for slot, found in evidence.items()}

        # G1: supplied claims, manifest provenance and collector TRX validity.
        collector_problems = []
        for build in BUILDS:
            entry = trx[build]
            if entry.record is None:
                collector_problems.append(entry.problem)
            else:
                collector_problems += [f"collector TRX {build}: {problem}" for problem in
                                       shadow_compare.collector_trx_problems(
                                           entry.record, expected["expectedCollectorTestName"])]
        g1 = _with_notes(shadow_compare.gate_g1_provenance(expected, attestation, manifests),
                         self.notes["G1"] + collector_problems)

        # G2: device sections, corroborated by both TRX host names.
        attested_host = None
        if isinstance(attestation, dict) and isinstance(attestation.get("host"), dict):
            attested_host = attestation["host"].get("computerName")
        host_problems = []
        for build in BUILDS:
            name = shadow_compare.trx_computer_name(trx[build].record)
            if name is None:
                host_problems.append(f"collector TRX {build} has no single-result computerName")
            elif name != expected["host"]["computerName"]:
                host_problems.append(f"collector TRX {build} computerName {name!r} differs from the expected host")
            elif name != attested_host:
                host_problems.append(f"collector TRX {build} computerName {name!r} differs from the attested host")
        g2 = _with_notes(shadow_compare.gate_g2_device(manifests), host_problems)

        g3 = shadow_compare.gate_g3_scene(manifests)

        # G4: each build's cameras against the retained camera, and identical B/M cameras per phase.
        camera_problems = []
        for build in BUILDS:
            zero = manifests.get((build, PHASES[0]))
            if zero is None:
                camera_problems.append(f"{_slot_label(build, PHASES[0])} camera unavailable")
                continue
            cameras = {phase: manifests[(build, phase)]["camera"] for phase in PHASES if (build, phase) in manifests}
            result = shadow_compare.gate_g4_camera(cameras, shadow_inputs.RETAINED_CAMERA,
                                                   zero["stations"][PHASE_REFERENCE_STATION]["on"])
            if not result.passed:
                camera_problems.append(f"{build}: {result.reason}")
        for phase in PHASES:
            pair = (manifests.get(("B", phase)), manifests.get(("M", phase)))
            if None not in pair and _camera_key(pair[0]["camera"]) != _camera_key(pair[1]["camera"]):
                camera_problems.append(f"phase ({phase[0]:g}, {phase[1]:g}) B and M cameras differ")
        g4 = (GateResult("G4", False, "; ".join(camera_problems)) if camera_problems else
              GateResult("G4", True, "both builds match the retained projection and view rotation, phase (0, 0) "
                                     "viewProjection is bit-identical, achieved phases within 0.02 px, B and M "
                                     "cameras identical per phase"))

        g5 = _with_notes(shadow_compare.gate_g5_matched_data(
            {p: (evidence[("B", p)].plain, evidence[("M", p)].plain)
             for p in PHASES if ("B", p) in evidence and ("M", p) in evidence},
            {p: (atlas[("B", p)], atlas[("M", p)]) for p in PHASES if ("B", p) in atlas and ("M", p) in atlas},
        ), self.notes["G5"])

        # G6 and the difference records, in frozen phase order.
        g6_facts = {}
        differences = []
        for phase in PHASES:
            measure_b, measure_m = measured.get(("B", phase)), measured.get(("M", phase))
            if measure_b is None or measure_m is None:
                differences.append({"phase": list(phase), "certifiedInsideMask": None, "certifiedOutsideMask": None,
                                    shadow_compare.OUTSIDE_CERTIFIED_LABEL: None})
                continue
            evidence_b, evidence_m = evidence[("B", phase)], evidence[("M", phase)]
            camera = evidence_b.manifest["camera"]
            certified = ((measure_b.certificate.certified_pixels if measure_b.certificate.valid else frozenset())
                         & (measure_m.certificate.certified_pixels if measure_m.certificate.valid else frozenset()))
            changed = [p for p in shadow_compare.differing_pixels(evidence_b.soft, evidence_m.soft) if p in certified]
            bands = _bands(evidence_b)
            try:
                mask = shadow_geometry.crossing_mask(camera, shadow_geometry.LIGHT_POSITION, changed)
                crossing_band = frozenset(
                    (i, j) for station in shadow_metrics.BUMP_STATIONS
                    for i, j, _, _ in self.overlaps(camera, bands[station])
                ) & certified
            except ValueError as error:
                self.notes["G6"].append(f"phase ({phase[0]:g}, {phase[1]:g}) mask geometry unavailable: {error}")
                mask, crossing_band = frozenset(), frozenset()
            split = shadow_compare.classify_pixel_differences(evidence_b.soft, evidence_m.soft, certified, mask)
            differences.append({"phase": list(phase), **shadow_compare.difference_record(split)})
            g6_facts[phase] = shadow_compare.G6PhaseFacts(
                frozenset(split.certified_inside_mask + split.certified_outside_mask),
                mask,
                crossing_band,
                measure_b.control_outside,
            )
        g6 = _with_notes(shadow_compare.gate_g6_exercised_and_confined(g6_facts), self.notes["G6"])

        g7_facts = {}
        for slot, measure in measured.items():
            if measure.original is None or measure.candidate is None:
                continue
            g7_facts[slot] = shadow_compare.G7PhaseFacts(
                min(measure.original["minPlainRed"], measure.candidate.row["minPlainRed"]),
                tuple((pair[0], pair[1]) for pair in measure.original["endpoints"]),
                tuple((pair[0], pair[1]) for pair in measure.candidate.row["endpoints"]),
            )
        g7 = _with_notes(shadow_compare.gate_g7_preconditions(g7_facts), self.notes["G7"])
        inventory = _with_notes(inventory, self.notes[INVENTORY_GATE])
        gates = [g1, g2, g3, g4, g5, g6, g7, inventory]

        rows = [_pinned_historical_row()]
        bumps: Dict[str, Dict[str, Dict[Tuple[float, float], float]]] = {
            "original": {"B": {}, "M": {}}, "candidate": {"B": {}, "M": {}},
        }
        for build in BUILDS:
            for phase in PHASES:
                measure = measured.get((build, phase))
                if measure is None:
                    continue
                for label, data in (("paired-original", measure.original),
                                    ("paired-candidate-area-band",
                                     measure.candidate.row if measure.candidate is not None else None)):
                    if data is None:
                        continue
                    rows.append({"label": label, "build": build, "phase": list(phase), **data, "status": DIAGNOSTIC})
                    bumps[data["metric"]][build][phase] = data["bump"]
        classifications = {}
        for metric in ("original", "candidate"):
            outcome = shadow_compare.classify_metric(metric, gates, bumps[metric]["B"], bumps[metric]["M"])
            classifications[metric] = shadow_compare.classification_record(
                outcome, bumps[metric]["B"], bumps[metric]["M"])

        coverage = []
        for build in BUILDS:
            for phase in PHASES:
                measure = measured.get((build, phase))
                phase_record = {"build": build, "phase": list(phase), "nominalPhase": list(phase),
                                "achievedPhase": _achieved_phase(manifests, build, phase)}
                if measure is None:
                    unavailable = {"valid": False, "reason": "evidence unavailable"}
                    coverage.append({**phase_record, "bandCentres": None,
                                     "certificate": {**unavailable, "certifiedPixelCount": 0},
                                     "measurements": {**unavailable, "uncovered": []}, "controls": []})
                    continue
                coverage.append({**phase_record,
                                 "bandCentres": [list(band.on) for band in _bands(evidence[(build, phase)])],
                                 "certificate": _certificate_record(measure.certificate),
                                 "measurements": _coverage_record(measure.coverage),
                                 "controls": measure.controls})

        trx_inputs = {}
        for build in BUILDS:
            entry = trx[build]
            counters = entry.record.counters if entry.record is not None else {}
            single = entry.record.results[0] if entry.record is not None and len(entry.record.results) == 1 else None
            trx_inputs[build] = {
                "sha256": entry.sha256,
                "testName": single.test_name if single is not None else None,
                "computerName": single.computer_name if single is not None else None,
                "executed": counters.get("executed"),
                "passed": counters.get("passed"),
                "skipped": counters.get("notExecuted"),
                "errors": counters.get("error"),
                "assertsBound": False,
            }
        attested = attestation if isinstance(attestation, dict) else None
        zero_b = evidence.get(("B", PHASES[0]))
        report = {
            "tool": TOOL,
            "mode": "compare",
            "status": COMPLETED if all(g.passed for g in gates) else INVALID,
            "designSha256": shadow_inputs.DESIGN_SHA256,
            "units": shadow_metrics.UNITS,
            "parameters": shadow_compare.frozen_parameters_record(),
            "inputs": {"expectationSha256": self.expectation_sha, "files": dict(self.files), "trx": trx_inputs},
            "claims": {
                "sourceEquivalence": {
                    "kind": "supplied-claim",
                    "matchesExpectation": not shadow_compare.attestation_mismatches(expected, attestation),
                },
                "workflow": {"kind": "supplied-claim", "value": attested.get("workflow") if attested else None},
                "host": {"kind": "supplied-claim", "value": attested.get("host") if attested else None,
                         "trxComputerNamesAgree": not host_problems},
                "independentlyVerified": False,
            },
            "rows": rows,
            "historicalAssertion": _historical_assertion(),
            "gates": _gate_records(gates),
            "coverage": coverage,
            "differences": differences,
            "classifications": classifications,
            "reportedOnly": {
                "baselineZeroSoftEqualsRetained": (
                    shadow_inputs.sha256_hex(zero_b.soft) == shadow_inputs.RETAINED_SOFT_SHA256
                    if zero_b is not None else None
                ),
            },
        }
        return _json_ready(report)


def run_compare(
    expectation: Path,
    bundle: Path,
    *,
    limits: shadow_inputs.ReadLimits = shadow_inputs.DEFAULT_LIMITS,
) -> Mapping[str, Any]:
    """Paired B/M analysis against an explicit expected-proof descriptor.
    A refused descriptor or bundle yields an INVALID report in which no gate was evaluated."""
    expectation_sha = None
    try:
        raw = shadow_inputs.read_file(expectation, shadow_inputs.MAX_MANIFEST_BYTES)
        expectation_sha = shadow_inputs.sha256_hex(raw)
        expected = shadow_inputs.parse_expectation(raw)
        source = shadow_inputs.open_bundle(bundle, limits)
    except InputRefused as refusal:
        reason = f"not evaluated: input refused ({refusal.code}): {refusal.detail}"
        gates = [GateResult(g, False, reason) for g in GATES + (INVENTORY_GATE,)]
        return _invalid_compare_report(expectation_sha, gates, {})
    with source:
        return _CompareAnalysis(expected, expectation_sha, source).report()


def _invalid_retained_report(refusal: ValueError) -> Mapping[str, Any]:
    return _json_ready({
        "tool": TOOL,
        "mode": "retained",
        "status": INVALID,
        "designSha256": shadow_inputs.DESIGN_SHA256,
        "units": shadow_metrics.UNITS,
        "parameters": shadow_compare.frozen_parameters_record(),
        "refusal": {"code": getattr(refusal, "code", ""), "detail": getattr(refusal, "detail", str(refusal))},
    })


def _nearest_existing_directory(path: str) -> Optional[os.stat_result]:
    """The identity of the closest existing ancestor directory of a path that may not exist yet."""
    current = os.path.dirname(path)
    while True:
        try:
            info = os.stat(current)
        except FileNotFoundError:
            parent = os.path.dirname(current)
            if parent == current:
                return None
            current = parent
            continue
        except OSError:
            return None
        return info if stat.S_ISDIR(info.st_mode) else None


def _output_refusal(output: Path, inputs: Sequence[Path], bundle: Optional[Path]) -> Optional[str]:
    """Why the output may not be written, or None. Lexical aliases, symlinks and hard links to the
    artifact, descriptor, bundle or any existing bundle file are refused before any analysis. For a
    directory bundle, any output resolved beneath it is refused too, including a file that does not
    exist yet, so a report can never become a bundle entry."""
    protected_paths = set()
    protected_files = set()
    bundle_root = None
    bundle_directories = set()

    def protect(path: Any) -> None:
        protected_paths.add(os.path.realpath(path))
        try:
            info = os.stat(path)
        except OSError:
            return
        protected_files.add((info.st_dev, info.st_ino))

    for path in inputs:
        protect(path)
    if bundle is not None:
        protect(bundle)
        if os.path.isdir(bundle):
            bundle_root = os.path.realpath(bundle)
            count = 0
            for directory, subdirectories, files in os.walk(bundle):
                try:
                    info = os.stat(directory)
                except OSError as error:
                    return f"the bundle directory cannot be inspected: {error}"
                bundle_directories.add((info.st_dev, info.st_ino))
                for name in subdirectories + files:
                    count += 1
                    if count > GUARD_MAX_ENTRIES:
                        return f"the bundle has more than {GUARD_MAX_ENTRIES} entries to protect"
                    protect(os.path.join(directory, name))
    resolved = os.path.realpath(output)
    if resolved in protected_paths:
        return "the output path resolves to an input"
    if bundle_root is not None:
        # Lexical containment after symlink resolution, then directory identity, which also catches
        # aliases such as a case variant on a case-insensitive file system.
        try:
            contained = os.path.commonpath([bundle_root, resolved]) == bundle_root
        except ValueError:
            contained = False  # different drives, so not beneath the bundle
        if contained:
            return "the output path resolves under the directory bundle"
        parent = _nearest_existing_directory(resolved)
        if parent is not None and (parent.st_dev, parent.st_ino) in bundle_directories:
            return "the output path resolves under the directory bundle"
    try:
        info = os.stat(output)
    except FileNotFoundError:
        return None
    except OSError as error:
        return f"the output path cannot be inspected: {error}"
    if stat.S_ISDIR(info.st_mode):
        return "the output path is a directory"
    if (info.st_dev, info.st_ino) in protected_files:
        return "the output path is a link to an input"
    return None


def _write_report(output: Path, report: Mapping[str, Any]) -> None:
    text = json.dumps(report, indent=2, allow_nan=False) + "\n"
    with open(output, "w", encoding="utf-8") as handle:
        handle.write(text)


def main(argv: Optional[Sequence[str]] = None) -> int:
    """CLI entry point. Writes the JSON report to --output and returns the exit code."""
    parser = argparse.ArgumentParser(
        prog="analyze.py",
        description="Offline point-shadow seam analyzer. Exit 0 means the analysis completed, not that a shader "
                    "passed.",
    )
    modes = parser.add_subparsers(dest="mode", required=True)
    retained = modes.add_parser("retained", help="REPORT-ONLY analysis of the retained artifact")
    retained.add_argument("--artifact", required=True, type=Path)
    retained.add_argument("--output", required=True, type=Path)
    compare = modes.add_parser("compare", help="paired B/M analysis against an explicit descriptor")
    compare.add_argument("--expectation", required=True, type=Path)
    compare.add_argument("--bundle", required=True, type=Path)
    compare.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)

    if args.mode == "retained":
        refusal = _output_refusal(args.output, [args.artifact], None)
    else:
        refusal = _output_refusal(args.output, [args.expectation], args.bundle)
    if refusal is not None:
        print(f"analyze.py: no report written to {args.output}: {refusal}", file=sys.stderr)
        return EXIT_INVALID

    if args.mode == "retained":
        try:
            report = run_retained(args.artifact)
        except (InputRefused, MetricInvalid) as refused:
            report = _invalid_retained_report(refused)
    else:
        report = run_compare(args.expectation, args.bundle)
    _write_report(args.output, report)
    return EXIT_COMPLETED if report["status"] in (shadow_compare.REPORT_ONLY, COMPLETED) else EXIT_INVALID


if __name__ == "__main__":
    sys.exit(main())
