"""Seam metrics in metres: the original float32 replay, the candidate area-band reach and the frozen
detrend statistic.

The original replay ports the arithmetic of the immutable 2026-10-07-point-shadow-seam-offline.py
operation for operation, so its sums, reaches, residuals and statistic reproduce the recorded
float32 bit patterns. The candidate stays in double precision.
"""

from __future__ import annotations

import math
import struct
from dataclasses import dataclass
from typing import Any, Callable, Iterable, List, Sequence, Tuple

import shadow_geometry

STATION_COUNT = 13
BUMP_STATIONS = (3, 4, 5)
ORIGINAL_PROBES = 61
ORIGINAL_WINDOW_METRES = 0.9
LIT_ENDPOINT_MIN = 0.9
SHADOW_ENDPOINT_MAX = 0.1
COMPLETENESS_RELATIVE_TOLERANCE = 1e-9
UNITS = "m"

METRIC_CODES = frozenset({"coverage", "non-finite", "stations", "replay-mismatch"})


class MetricInvalid(ValueError):
    """A metric cannot be formed from its inputs. `code` is one of METRIC_CODES."""

    def __init__(self, code: str, detail: str = "") -> None:
        super().__init__(f"{code}: {detail}" if detail else code)
        self.code = code
        self.detail = detail


@dataclass(frozen=True)
class StationReach:
    index: int
    sum: float
    reach: float


@dataclass(frozen=True)
class Statistic:
    bump: float
    worst_elsewhere: float
    residuals: Tuple[float, ...]
    slope: float
    mean_y: float


@dataclass(frozen=True)
class AreaReach:
    reach: float
    band_area: float
    covered_area: float
    lit_endpoint_mean: float
    shadow_endpoint_mean: float
    units: str = UNITS


# One integration sample: the pixel's floor footprint, its plain red byte and its soft red byte.
Sample = Tuple[shadow_geometry.Polygon, int, int]


def _f32(value: float) -> float:
    return struct.unpack("<f", struct.pack("<f", value))[0]


def _same_f32(value: float, recorded: Any) -> bool:
    try:
        return struct.pack("<f", value) == struct.pack("<f", recorded)
    except (OverflowError, struct.error, TypeError):
        return False


def original_station_reaches(evidence: Any) -> Tuple[StationReach, ...]:
    """Replay the original float32 per-station sums and reaches, refusing `replay-mismatch` on any bit difference."""
    stations = evidence.manifest["stations"]
    if len(stations) != STATION_COUNT:
        raise MetricInvalid("stations", f"{len(stations)} stations")
    window = _f32(ORIGINAL_WINDOW_METRES)
    result = []
    for station in stations:
        probes = station["probes"]
        if len(probes) != ORIGINAL_PROBES:
            raise MetricInvalid("stations", f"station {station['index']} has {len(probes)} probes")
        total = 0.0
        for probe in probes:
            index = probe["byteIndex"]
            plain_red, soft_red = evidence.plain[index], evidence.soft[index]
            if plain_red == 0:
                raise MetricInvalid("non-finite", f"station {station['index']} probe {probe['index']} plain red is zero")
            visibility = _f32(soft_red / plain_red)
            total = _f32(total + _f32(1 - visibility))
        if not _same_f32(total, station["sum"]):
            raise MetricInvalid("replay-mismatch", f"station {station['index']} sum {total!r} vs {station['sum']!r}")
        reach = _f32(_f32(_f32(total * 2) * window) / (ORIGINAL_PROBES - 1))
        if not _same_f32(reach, station["reach"]):
            raise MetricInvalid("replay-mismatch", f"station {station['index']} reach {reach!r} vs {station['reach']!r}")
        result.append(StationReach(station["index"], total, reach))
    return tuple(result)


def detrend_statistic(series: Sequence[float], *, single_precision: bool = False) -> Statistic:
    """Least-squares detrend over 13 stations, bump = mean residual of stations 3 to 5."""
    values = list(series)
    if len(values) != STATION_COUNT:
        raise MetricInvalid("stations", f"{len(values)} station values")
    for value in values:
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise MetricInvalid("non-finite", f"station value {value!r}")
    r: Callable[[float], float] = _f32 if single_precision else (lambda x: x)
    n = len(values)
    mean_x = r((n - 1) / 2)
    mean_y: float = 0
    for value in values:
        mean_y = r(mean_y + value)
    mean_y = r(mean_y / n)
    sxx: float = 0
    sxy: float = 0
    for i, value in enumerate(values):
        dx = r(i - mean_x)
        sxx = r(sxx + r(dx * dx))
        sxy = r(sxy + r(dx * r(value - mean_y)))
    slope = r(sxy / sxx)
    residuals = tuple(r(value - r(mean_y + r(slope * r(i - mean_x)))) for i, value in enumerate(values))
    first, second, third = BUMP_STATIONS
    bump = r(r(r(residuals[first] + residuals[second]) + residuals[third]) / 3)
    worst = max(abs(value) for i, value in enumerate(residuals) if i not in BUMP_STATIONS)
    return Statistic(bump, worst, residuals, slope, mean_y)


def _visibility(plain: Any, soft: Any) -> float:
    if plain == 0:
        raise MetricInvalid("non-finite", "zero plain byte inside a band")
    visibility = soft / plain
    if not math.isfinite(visibility):
        raise MetricInvalid("non-finite", f"visibility {soft!r}/{plain!r}")
    return visibility


def area_band_reach(band: shadow_geometry.StationBand, samples: Iterable[Sample]) -> AreaReach:
    """Candidate reach (1 / 2h) * sum area(F_ij intersect R_s) * (1 - V_ij) with the completeness check."""
    strip_inner, strip_outer = shadow_geometry.ENDPOINT_STRIP
    whole = shadow_geometry.ConvexRegion(shadow_geometry.band_polygon(band))
    lit = shadow_geometry.ConvexRegion(shadow_geometry.band_region(band, -strip_outer, -strip_inner))
    shadow = shadow_geometry.ConvexRegion(shadow_geometry.band_region(band, strip_inner, strip_outer))
    areas: List[float] = []
    deficits: List[float] = []
    lit_areas: List[float] = []
    lit_weighted: List[float] = []
    shadow_areas: List[float] = []
    shadow_weighted: List[float] = []
    for footprint, plain, soft in samples:
        area = whole.overlap(footprint)
        if area <= 0.0:
            continue
        visibility = _visibility(plain, soft)
        areas.append(area)
        deficits.append(area * (1.0 - visibility))
        lit_area = lit.overlap(footprint)
        if lit_area > 0.0:
            lit_areas.append(lit_area)
            lit_weighted.append(lit_area * visibility)
        shadow_area = shadow.overlap(footprint)
        if shadow_area > 0.0:
            shadow_areas.append(shadow_area)
            shadow_weighted.append(shadow_area * visibility)

    band_area = whole.area
    covered = math.fsum(areas)
    if not abs(covered - band_area) <= COMPLETENESS_RELATIVE_TOLERANCE * band_area:
        raise MetricInvalid("coverage", f"footprint overlaps sum to {covered!r} m^2, band area {band_area!r} m^2")
    lit_total = math.fsum(lit_areas)
    shadow_total = math.fsum(shadow_areas)
    if not (lit_total > 0.0 and shadow_total > 0.0):
        raise MetricInvalid("coverage", "an endpoint strip has no covered area")
    reach = math.fsum(deficits) / (2.0 * band.half_width)
    lit_mean = math.fsum(lit_weighted) / lit_total
    shadow_mean = math.fsum(shadow_weighted) / shadow_total
    for label, value in (("reach", reach), ("lit endpoint", lit_mean), ("shadow endpoint", shadow_mean)):
        if not math.isfinite(value):
            raise MetricInvalid("non-finite", f"{label} {value!r}")
    return AreaReach(reach, band_area, covered, lit_mean, shadow_mean)
