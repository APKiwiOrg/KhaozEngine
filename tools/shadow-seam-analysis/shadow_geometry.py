"""Floor geometry for the point-shadow seam analyzer.

Recorded-matrix floor projection, the fixed visible-floor certificate over F, pixel footprints,
convex overlap and the conservative face-crossing mask. Frozen design values live here as constants.
Geometry uses the recorded JSON matrix numbers as double-precision values and never rounds them
back to float32.

Conventions, pinned in README.md: floor polygons are (x, z) tuples on y = 0 in metres, world
coordinates with renderOrigin subtracted. Pixel (i, j) is the screen square [i, i+1) by [j, j+1),
screen x = (ndc_x + 1) * width / 2 and screen y = (1 - ndc_y) * height / 2. Matrices are
System.Numerics row-major M11 to M44 with row vectors.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any, Dict, FrozenSet, Iterable, Iterator, List, Mapping, Optional, Sequence, Tuple

Point = Tuple[float, float]
Polygon = Tuple[Point, ...]
Pixel = Tuple[int, int]

VIEWPORT_WIDTH = 192
VIEWPORT_HEIGHT = 192


@dataclass(frozen=True)
class FloorRect:
    x_min: float
    x_max: float
    z_min: float
    z_max: float


# Certified visible-floor region F on y = 0, fixed for every phase and both builds.
F_REGION = FloorRect(1.95, 5.25, 3.75, 7.15)

# Fixed non-seam pairing controls.
NON_SEAM_CONTROLS = (FloorRect(2.0, 2.4, 6.7, 7.0), FloorRect(4.8, 5.2, 6.8, 7.1))

LIGHT_POSITION = (0.0, 5.0, 0.0)

# Soft filter kernel cap, 16 * (pi/2) / 256 radians, and the conservative mask expansion factor.
MAX_ANGLE = 16.0 * (math.pi / 2.0) / 256.0
MASK_MARGIN_FACTOR = 1.0 + 1e-3

# Candidate area-band frame on the floor, (x, z) components.
ACROSS_XZ = (1.5 / math.sqrt(3.25), -1.0 / math.sqrt(3.25))
ALONG_XZ = (1.0 / math.sqrt(3.25), 1.5 / math.sqrt(3.25))
BAND_HALF_WIDTH = math.sqrt(3.25) / 40.0
BAND_HALF_LENGTH = 0.9
ENDPOINT_STRIP = (0.85, 0.9)

# Screen-space slack when enumerating pixels, so rounding never drops a touching footprint.
_ENUMERATION_SLACK_PX = 1e-7


@dataclass(frozen=True)
class DrawRecord:
    name: str
    kind: str
    minimum: Tuple[float, float, float]
    maximum: Tuple[float, float, float]


# The approved Wall callback draw list: the 40 m floor square and one box wall.
APPROVED_DRAWS = (
    DrawRecord("floor", "floor", (-20.0, 0.0, -20.0), (20.0, 0.0, 20.0)),
    DrawRecord("wall", "box", (2.0, 0.0, -3.0), (2.4, 3.0, 3.0)),
)


@dataclass(frozen=True)
class StationBand:
    on: Point
    across: Point
    along: Point
    half_width: float
    half_length: float = BAND_HALF_LENGTH


@dataclass(frozen=True)
class FloorCertificate:
    valid: bool
    reason: str
    certified_pixels: FrozenSet[Pixel]


@dataclass(frozen=True)
class CoverageResult:
    valid: bool
    reason: str
    uncovered: Tuple[Tuple[str, Any], ...]


def approved_draw_records() -> List[Dict[str, Any]]:
    """The approved draw list in the attestation's JSON shape."""
    return [
        {"name": d.name, "kind": d.kind, "min": list(d.minimum), "max": list(d.maximum)} for d in APPROVED_DRAWS
    ]


def _numbers(value: Any, length: int, label: str) -> Tuple[float, ...]:
    if not isinstance(value, (list, tuple)) or len(value) != length:
        raise ValueError(f"{label} must hold {length} numbers")
    result = []
    for item in value:
        if isinstance(item, bool) or not isinstance(item, (int, float)):
            raise ValueError(f"{label} must hold numbers")
        number = float(item)
        if not math.isfinite(number):
            raise ValueError(f"{label} must be finite")
        result.append(number)
    return tuple(result)


def _matrix(camera: Mapping[str, Any], key: str) -> Tuple[float, ...]:
    return _numbers(camera[key], 16, f"camera {key}")


def _inverse3(m: Sequence[Sequence[float]]) -> Tuple[Tuple[float, float, float], ...]:
    (a, b, c), (d, e, f), (g, h, i) = m
    co_a, co_b, co_c = e * i - f * h, f * g - d * i, d * h - e * g
    det = a * co_a + b * co_b + c * co_c
    if det == 0.0 or not math.isfinite(det):
        raise ValueError("floor projection is singular")
    return (
        (co_a / det, (c * h - b * i) / det, (b * f - c * e) / det),
        (co_b / det, (a * i - c * g) / det, (c * d - a * f) / det),
        (co_c / det, (b * g - a * h) / det, (a * e - b * d) / det),
    )


class _FloorMap:
    """The projective map between floor points (x, z) on y = 0 and screen points, for one camera."""

    def __init__(self, camera: Mapping[str, Any]) -> None:
        m = _matrix(camera, "viewProjection")
        ox, oy, oz = _numbers(camera["renderOrigin"], 3, "camera renderOrigin")
        # clip_j = x m[j] + z m[8 + j] + k_j on y = 0, after subtracting the render origin.
        k = [m[12 + j] - ox * m[j] - oy * m[4 + j] - oz * m[8 + j] for j in range(4)]
        hx, hy = VIEWPORT_WIDTH / 2.0, VIEWPORT_HEIGHT / 2.0
        self.homography = (
            (hx * (m[0] + m[3]), hx * (m[8] + m[11]), hx * (k[0] + k[3])),
            (hy * (m[3] - m[1]), hy * (m[11] - m[9]), hy * (k[3] - k[1])),
            (m[3], m[11], k[3]),
        )
        self.depth_row = (m[2], m[10], k[2])
        self.inverse_homography = _inverse3(self.homography)
        self._corners: Dict[Tuple[float, float], Point] = {}

    def forward(self, x: float, z: float) -> Point:
        (a, b, c), (d, e, f), (g, h, i) = self.homography
        w = g * x + h * z + i
        if not w > 0.0:
            raise ValueError("floor point is not in front of the camera")
        return ((a * x + b * z + c) / w, (d * x + e * z + f) / w)

    def depth(self, x: float, z: float) -> float:
        g, h, i = self.homography[2]
        a, b, c = self.depth_row
        return (a * x + b * z + c) / (g * x + h * z + i)

    def inverse(self, u: float, v: float) -> Point:
        (a, b, c), (d, e, f), (g, h, i) = self.inverse_homography
        w = g * u + h * v + i
        if w == 0.0:
            raise ValueError("screen point has no floor preimage")
        return ((a * u + b * v + c) / w, (d * u + e * v + f) / w)

    def corner(self, u: int, v: int) -> Point:
        key = (u, v)
        point = self._corners.get(key)
        if point is None:
            point = self.inverse(float(u), float(v))
            self._corners[key] = point
        return point

    def footprint(self, i: int, j: int) -> Polygon:
        polygon = (self.corner(i, j), self.corner(i + 1, j), self.corner(i + 1, j + 1), self.corner(i, j + 1))
        return polygon if _signed_area(polygon) >= 0.0 else tuple(reversed(polygon))

    def covering(self, polygon: Polygon) -> Iterator[Tuple[int, int, Polygon]]:
        screen = [self.forward(x, z) for x, z in polygon]
        if not screen:
            return
        slack = _ENUMERATION_SLACK_PX
        ys = [y for _, y in screen]
        first_row = max(0, math.floor(min(ys) - slack))
        last_row = min(VIEWPORT_HEIGHT - 1, math.floor(max(ys) + slack))
        for j in range(first_row, last_row + 1):
            strip = _clip_halfplane(screen, 0.0, 1.0, -(j - slack))
            strip = _clip_halfplane(strip, 0.0, -1.0, j + 1 + slack)
            if not strip:
                continue
            xs = [x for x, _ in strip]
            first = max(0, math.floor(min(xs) - slack))
            last = min(VIEWPORT_WIDTH - 1, math.floor(max(xs) + slack))
            for i in range(first, last + 1):
                yield i, j, self.footprint(i, j)


def _project_point(camera: Mapping[str, Any], point: Sequence[float]) -> Point:
    m = _matrix(camera, "viewProjection")
    origin = _numbers(camera["renderOrigin"], 3, "camera renderOrigin")
    x, y, z = (p - o for p, o in zip(_numbers(point, 3, "point"), origin))
    clip = [x * m[j] + y * m[4 + j] + z * m[8 + j] + m[12 + j] for j in range(4)]
    if not clip[3] > 0.0:
        raise ValueError("point is not in front of the camera")
    return ((clip[0] / clip[3] + 1.0) * VIEWPORT_WIDTH / 2.0, (1.0 - clip[1] / clip[3]) * VIEWPORT_HEIGHT / 2.0)


def project_floor_point(camera: Mapping[str, Any], point: Point) -> Point:
    """Screen (x, y) of a floor point under the camera's recorded viewProjection."""
    return _FloorMap(camera).forward(float(point[0]), float(point[1]))


def pixel_footprint(camera: Mapping[str, Any], pixel: Pixel) -> Polygon:
    """Exact preimage of the pixel square on y = 0, counter-clockwise in (x, z)."""
    return _FloorMap(camera).footprint(int(pixel[0]), int(pixel[1]))


def footprints_covering(camera: Mapping[str, Any], polygon: Polygon) -> Iterator[Tuple[int, int, Polygon]]:
    """Every in-viewport pixel whose footprint may intersect the polygon, with that footprint."""
    return _FloorMap(camera).covering(tuple(polygon))


def _signed_area(polygon: Sequence[Point]) -> float:
    if len(polygon) < 3:
        return 0.0
    x0, z0 = polygon[0]
    terms = []
    for k in range(1, len(polygon) - 1):
        ax, az = polygon[k][0] - x0, polygon[k][1] - z0
        bx, bz = polygon[k + 1][0] - x0, polygon[k + 1][1] - z0
        terms.append(ax * bz - az * bx)
    return 0.5 * math.fsum(terms)


def polygon_area(polygon: Polygon) -> float:
    """Unsigned area in square metres."""
    return abs(_signed_area(polygon))


def _clip_halfplane(points: Sequence[Point], a: float, b: float, c: float) -> List[Point]:
    """Sutherland-Hodgman step keeping the side where a x + b y + c >= 0."""
    if not points:
        return []
    result: List[Point] = []
    previous = points[-1]
    previous_side = a * previous[0] + b * previous[1] + c
    for current in points:
        side = a * current[0] + b * current[1] + c
        if side >= 0.0:
            if previous_side < 0.0 < side:
                t = previous_side / (previous_side - side)
                result.append((previous[0] + t * (current[0] - previous[0]),
                               previous[1] + t * (current[1] - previous[1])))
            result.append(current)
        elif previous_side > 0.0:
            t = previous_side / (previous_side - side)
            result.append((previous[0] + t * (current[0] - previous[0]),
                           previous[1] + t * (current[1] - previous[1])))
        previous, previous_side = current, side
    return result


def _counter_clockwise(polygon: Sequence[Point]) -> List[Point]:
    return list(polygon) if _signed_area(polygon) >= 0.0 else list(reversed(polygon))


def convex_overlap_area(a: Polygon, b: Polygon) -> float:
    """Area of the intersection of two convex polygons, either orientation, in double precision."""
    if len(a) < 3 or len(b) < 3 or _signed_area(a) == 0.0 or _signed_area(b) == 0.0:
        return 0.0
    # Clip in coordinates local to b, which keeps cancellation small for small footprints far
    # from the world origin.
    ox, oz = b[0]
    subject = [(x - ox, z - oz) for x, z in _counter_clockwise(a)]
    clip = [(x - ox, z - oz) for x, z in _counter_clockwise(b)]
    for k, (x0, z0) in enumerate(clip):
        x1, z1 = clip[(k + 1) % len(clip)]
        subject = _clip_halfplane(subject, -(z1 - z0), x1 - x0, (z1 - z0) * x0 - (x1 - x0) * z0)
        if not subject:
            return 0.0
    return polygon_area(tuple(subject))


class ConvexRegion:
    """A convex floor polygon with cheap disjoint and contained tests before exact clipping."""

    def __init__(self, polygon: Polygon) -> None:
        self.polygon = tuple(_counter_clockwise(polygon))
        xs = [x for x, _ in self.polygon]
        zs = [z for _, z in self.polygon]
        self.bounds = (min(xs), max(xs), min(zs), max(zs))
        self.edges = []
        for k, (x0, z0) in enumerate(self.polygon):
            x1, z1 = self.polygon[(k + 1) % len(self.polygon)]
            self.edges.append((-(z1 - z0), x1 - x0, (z1 - z0) * x0 - (x1 - x0) * z0))
        self.area = polygon_area(self.polygon)

    def overlap(self, footprint: Polygon) -> float:
        x_min, x_max, z_min, z_max = self.bounds
        if (max(x for x, _ in footprint) < x_min or min(x for x, _ in footprint) > x_max
                or max(z for _, z in footprint) < z_min or min(z for _, z in footprint) > z_max):
            return 0.0
        contained = True
        for a, b, c in self.edges:
            sides = [a * x + b * z + c for x, z in footprint]
            if all(side < 0.0 for side in sides):
                return 0.0
            if any(side < 0.0 for side in sides):
                contained = False
        if contained:
            return polygon_area(footprint)
        return convex_overlap_area(footprint, self.polygon)


def rect_polygon(rect: FloorRect) -> Polygon:
    return ((rect.x_min, rect.z_min), (rect.x_max, rect.z_min), (rect.x_max, rect.z_max), (rect.x_min, rect.z_max))


def _in_rect(rect: FloorRect, point: Point) -> bool:
    return rect.x_min <= point[0] <= rect.x_max and rect.z_min <= point[1] <= rect.z_max


def station_band(on_point: Point) -> StationBand:
    """The frozen station band centred on a recorded on-point."""
    return StationBand(
        on=(float(on_point[0]), float(on_point[1])),
        across=ACROSS_XZ,
        along=ALONG_XZ,
        half_width=BAND_HALF_WIDTH,
        half_length=BAND_HALF_LENGTH,
    )


def band_region(band: StationBand, u_min: float, u_max: float) -> Polygon:
    """The parallelogram { on + u across + v along : u in [u_min, u_max], |v| <= half_width }."""
    ox, oz = band.on
    (ax, az), (ex, ez) = band.across, band.along
    h = band.half_width
    corners = ((u_min, -h), (u_max, -h), (u_max, h), (u_min, h))
    return tuple(_counter_clockwise([(ox + u * ax + v * ex, oz + u * az + v * ez) for u, v in corners]))


def band_polygon(band: StationBand) -> Polygon:
    """The band parallelogram { on + u across + v along : |u| <= half_length, |v| <= half_width }."""
    return band_region(band, -band.half_length, band.half_length)


def overlapping_footprints(camera: Mapping[str, Any], polygon: Polygon) -> List[Tuple[int, int, Polygon, float]]:
    """In-viewport pixels whose footprint overlaps the convex polygon with positive area."""
    region = ConvexRegion(polygon)
    result = []
    for i, j, footprint in _FloorMap(camera).covering(region.polygon):
        area = region.overlap(footprint)
        if area > 0.0:
            result.append((i, j, footprint, area))
    return result


def _draw_records(draws: Any) -> Tuple[DrawRecord, ...]:
    if not isinstance(draws, (list, tuple)):
        raise ValueError("draw list must be a list")
    records = []
    for draw in draws:
        if not isinstance(draw, Mapping) or set(draw) != {"name", "kind", "min", "max"}:
            raise ValueError("draw records need exactly name, kind, min and max")
        if not isinstance(draw["name"], str) or not isinstance(draw["kind"], str):
            raise ValueError("draw name and kind must be strings")
        records.append(DrawRecord(draw["name"], draw["kind"], _numbers(draw["min"], 3, "draw min"),
                                  _numbers(draw["max"], 3, "draw max")))
    return tuple(records)


def _visibility_problem(camera: Mapping[str, Any], draws: Any) -> Optional[str]:
    records = _draw_records(draws)
    if records != APPROVED_DRAWS:
        return "draw list differs from the approved Wall callback (floor and one box wall)"
    projection = _matrix(camera, "projection")
    vp = _matrix(camera, "viewProjection")
    for label, matrix in (("projection", projection), ("viewProjection", vp)):
        if (matrix[3], matrix[7], matrix[11], matrix[15]) != (0.0, 0.0, 0.0, 1.0):
            return f"camera {label} is not orthographic"
    # The orthographic ray is the null direction of the screen x and y columns. Toward the camera
    # the depth decreases.
    c0, c1, c2 = (vp[0], vp[4], vp[8]), (vp[1], vp[5], vp[9]), (vp[2], vp[6], vp[10])
    ray = (c0[1] * c1[2] - c0[2] * c1[1], c0[2] * c1[0] - c0[0] * c1[2], c0[0] * c1[1] - c0[1] * c1[0])
    depth_slope = sum(r * c for r, c in zip(ray, c2))
    if depth_slope == 0.0:
        return "camera ray direction is degenerate"
    if depth_slope > 0.0:
        ray = (-ray[0], -ray[1], -ray[2])
    if not (ray[1] > 0.0 and ray[2] >= 0.0):
        return "toward-camera ray must have positive Y and nonnegative Z"
    for record in records:
        if record.kind == "floor":
            covers = (record.minimum[1] == 0.0 == record.maximum[1]
                      and record.minimum[0] <= F_REGION.x_min and record.maximum[0] >= F_REGION.x_max
                      and record.minimum[2] <= F_REGION.z_min and record.maximum[2] >= F_REGION.z_max)
            if not covers:
                return f"floor draw {record.name} does not cover F"
        elif not record.maximum[2] < F_REGION.z_min:
            # Rays from F toward the camera keep z >= F.z_min, so only geometry behind that plane
            # is proved unable to occlude F.
            return f"draw {record.name} reaches z >= {F_REGION.z_min} and may occlude F"
    floor = _FloorMap(camera)
    for corner in rect_polygon(F_REGION):
        sx, sy = floor.forward(*corner)
        depth = floor.depth(*corner)
        if not (0.0 <= sx <= VIEWPORT_WIDTH and 0.0 <= sy <= VIEWPORT_HEIGHT and 0.0 <= depth <= 1.0):
            return f"F corner {corner} projects outside the viewport or depth range"
    return None


def certify_visible_floor(camera: Mapping[str, Any], draws: Sequence[Mapping[str, Any]]) -> FloorCertificate:
    """Source/geometry visibility proof over F. Never classifies receivers from pixel colour."""
    try:
        problem = _visibility_problem(camera, draws)
        if problem is None:
            floor = _FloorMap(camera)
            screen = [floor.forward(*corner) for corner in rect_polygon(F_REGION)]
            first_i = max(0, math.floor(min(x for x, _ in screen)))
            last_i = min(VIEWPORT_WIDTH - 1, math.floor(max(x for x, _ in screen)))
            first_j = max(0, math.floor(min(y for _, y in screen)))
            last_j = min(VIEWPORT_HEIGHT - 1, math.floor(max(y for _, y in screen)))
            # A footprint is convex, so it lies in F exactly when its four corners do.
            certified = frozenset(
                (i, j)
                for j in range(first_j, last_j + 1)
                for i in range(first_i, last_i + 1)
                if all(_in_rect(F_REGION, floor.corner(u, v))
                       for u, v in ((i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)))
            )
    except (KeyError, TypeError, ValueError) as error:
        problem = f"camera or draw records unavailable: {error}"
    if problem is not None:
        return FloorCertificate(False, problem, frozenset())
    return FloorCertificate(
        True,
        "orthographic camera, toward-camera ray with positive Y and nonnegative Z, the given draw list equals "
        "the approved Wall list (not independently verified), F corners inside the viewport and depth range",
        certified,
    )


def _region_certified(floor: _FloorMap, certified: FrozenSet[Pixel], polygon: Polygon) -> bool:
    if not all(_in_rect(F_REGION, point) for point in polygon):
        return False
    region = ConvexRegion(polygon)
    for i, j, footprint in floor.covering(region.polygon):
        if (i, j) not in certified and region.overlap(footprint) > 0.0:
            return False
    return True


def check_measurement_coverage(
    camera: Mapping[str, Any],
    certificate: FloorCertificate,
    probe_pixels: Iterable[Pixel],
    bands: Sequence[StationBand],
    controls: Sequence[FloorRect],
) -> CoverageResult:
    """Every probe pixel and every footprint touching a band or control must be certified."""
    certified = certificate.certified_pixels if certificate.valid else frozenset()
    uncovered: List[Tuple[str, Any]] = []
    seen = set()
    for pixel in probe_pixels:
        key = (int(pixel[0]), int(pixel[1]))
        if key not in certified and key not in seen:
            seen.add(key)
            uncovered.append(("probe", key))
    try:
        floor = _FloorMap(camera)
        for index, band in enumerate(bands):
            if not _region_certified(floor, certified, band_polygon(band)):
                uncovered.append(("band", index))
        for index, control in enumerate(controls):
            if not _region_certified(floor, certified, rect_polygon(control)):
                uncovered.append(("control", index))
    except (KeyError, TypeError, ValueError) as error:
        return CoverageResult(False, f"camera unavailable: {error}", tuple(uncovered))
    if not certificate.valid:
        return CoverageResult(False, f"visible-floor certificate invalid: {certificate.reason}", tuple(uncovered))
    if uncovered:
        return CoverageResult(False, f"{len(uncovered)} required items are not inside certified F, "
                                     f"first {uncovered[0]}", tuple(uncovered))
    return CoverageResult(True, "every probe, band and control footprint is certified", ())


def _unit(vector: Sequence[float]) -> Tuple[float, float, float]:
    length = math.sqrt(sum(c * c for c in vector))
    if length == 0.0:
        raise ValueError("zero direction")
    return (vector[0] / length, vector[1] / length, vector[2] / length)


def _face_boundary_distance(direction: Tuple[float, float, float]) -> float:
    """Angle from a unit direction to the nearest of the four planes bounding its dominant cube face."""
    axis = max(range(3), key=lambda k: abs(direction[k]))
    sign = 1.0 if direction[axis] >= 0.0 else -1.0
    distances = []
    for other in range(3):
        if other == axis:
            continue
        for other_sign in (1.0, -1.0):
            # Plane between face (axis, sign) and face (other, other_sign): sign d_a = other_sign d_b.
            dot = (sign * direction[axis] - other_sign * direction[other]) / math.sqrt(2.0)
            distances.append(math.asin(min(1.0, abs(dot))))
    return min(distances)


def crossing_mask(
    camera: Mapping[str, Any], light: Tuple[float, float, float], pixels: Iterable[Pixel]
) -> FrozenSet[Pixel]:
    """Pixels that may cross a cube-face boundary under either kernel, conservatively expanded."""
    floor = _FloorMap(camera)
    lx, ly, lz = (float(c) for c in light)
    kernel = MAX_ANGLE * MASK_MARGIN_FACTOR
    result = set()
    for pixel in pixels:
        i, j = int(pixel[0]), int(pixel[1])
        cx, cz = floor.inverse(i + 0.5, j + 0.5)
        centre = _unit((cx - lx, -ly, cz - lz))
        radius = 0.0
        for x, z in floor.footprint(i, j):
            ray = _unit((x - lx, -ly, z - lz))
            dot = max(-1.0, min(1.0, sum(a * b for a, b in zip(centre, ray))))
            radius = max(radius, math.acos(dot))
        if _face_boundary_distance(centre) <= radius + kernel:
            result.add((i, j))
    return frozenset(result)


def achieved_phase(
    camera_zero: Mapping[str, Any], camera_phase: Mapping[str, Any], on_point: Sequence[float]
) -> Point:
    """Absolute screen shift of the station-4 on-point between the (0, 0) camera and a phase camera."""
    zero = _project_point(camera_zero, on_point)
    shifted = _project_point(camera_phase, on_point)
    return (abs(shifted[0] - zero[0]), abs(shifted[1] - zero[1]))
