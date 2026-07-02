"""Fit collision-proxy geometry to a building render mesh, and audit a merged spec for capsule pin traps.

Two modes:

  FIT   - measure the mesh and emit a DRAFT spec (body boxes + roof wedges/slabs) to merge with the
          hand-authored capsule pieces (entrance steps, rails, furniture - see README capsule rules):
          blender -b --python fit_proxy.py -- fit <building.glb> <heightMeters> <placementScale> <out_draft.json>

  AUDIT - headroom-check a FINAL merged spec: every standable top a player can reach (walk/step/jump)
          must have capsule clearance to any piece above it, or the player pins between them
          (the anvil-under-porch-roof bug):
          blender -b --python fit_proxy.py -- audit <building.glb> <heightMeters> <placementScale> <spec.json>

Coordinates are the building's raw Blender frame (Z up), same as build_proxy.py specs. heightMeters /
placementScale convert capsule limits (world metres) into raw units per building: ws = heightMeters /
rawHeight * placementScale.

What FIT automates (the parts that were previously eyeballed and drifted):
  - BODY: z-sliced trimmed wall footprints, split into stories where the footprint jumps (a jetty).
    Each story becomes one box at the median footprint of its slice run.
  - ROOFS: sloped up-facing faces are clustered by facing direction + spatial connectivity, each cluster
    gets a least-squares plane fit, and the emitted wedge's hypotenuse IS the fitted plane (no guessed
    ridges). Near-flat clusters (< FLAT_DEG) become flat slab boxes instead of wedges.
Entrances/steps/furniture stay hand-authored: they are capsule-rule geometry, not mesh-fit geometry.

Capsule constants mirror the engine defaults (MoveTuning): capsule height 1.8, radius 0.4, StepHeight 0.4,
jump apex ~1.92. Keep in sync with KhaozEngine.Locomotion if those change.
"""
import bpy, sys, os, json, math
from mathutils import Vector

# ---- capsule (world metres, engine MoveTuning defaults) ----
CAPSULE_HEIGHT = 1.8
JUMP_APEX_FEET = 1.92          # feet rise of a full jump (Ruinborne tuning: default * sqrt(1.5))
HEADROOM = CAPSULE_HEIGHT + 0.15   # min clear space above a standable top
MAX_SLOPE_DEG = 40.0           # engine walkable-slope gate
FLAT_DEG = 10.0                # a roof plane flatter than this becomes a slab box
MIN_DROP_DIM = 0.18            # world: pieces thinner than this in 2 dims would be "thin trap" - warn only

argv = sys.argv[sys.argv.index("--") + 1:]
mode, src, height_m, place_scale, spec_path = argv[0], argv[1], float(argv[2]), float(argv[3]), argv[4]
roof_floor_override = float(argv[5]) if len(argv) > 5 else None   # fit mode: explicit wall-top z (raw) for
# buildings whose intersecting roofs defeat the taper detector (see README)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
deps = bpy.context.evaluated_depsgraph_get()

wco, polys = [], []   # world verts; (normal, [verts], centroid, area)
for o in [x for x in bpy.context.scene.objects if x.type == 'MESH']:
    eo = o.evaluated_get(deps); me = eo.to_mesh(); mw = eo.matrix_world; rot = mw.to_3x3()
    base = len(wco)
    wco.extend([mw @ v.co for v in me.vertices])
    for p in me.polygons:
        vs = [wco[base + vi] for vi in p.vertices]
        c = sum(vs, Vector()) / len(vs)
        polys.append(((rot @ p.normal).normalized(), vs, c, p.area))
    eo.to_mesh_clear()

mn = Vector((min(w[i] for w in wco) for i in range(3)))
mx = Vector((max(w[i] for w in wco) for i in range(3)))
raw_h = mx.z - mn.z
ws = height_m / raw_h * place_scale          # world metres per raw unit
def w2r(m): return m / ws                    # world -> raw

# =====================================================================================
# FIT
# =====================================================================================
def trimmed(vals, frac=0.02):
    vals = sorted(vals)
    k = max(0, int(len(vals) * frac))
    return vals[k], vals[-1 - k]

def fit_body():
    """z-sliced trimmed wall footprints -> story boxes. A story break = a footprint half-extent jumping
    by more than JUMP_RAW between adjacent slice runs (a jetty / tower setback)."""
    JUMP_RAW = w2r(0.38)   # real jetty/setback scale; wall relief and sills stay under this
    n_slices = 28
    lo_z = mn.z + raw_h * 0.03                 # skip the ground plinth band
    hi_z = mn.z + raw_h * 0.995
    step = (hi_z - lo_z) / n_slices
    slices = []
    for i in range(n_slices):
        z0, z1 = lo_z + i * step, lo_z + (i + 1) * step
        pts = [w for w in wco if z0 <= w.z < z1]
        if len(pts) < 8:
            slices.append(None); continue
        tx = trimmed([p.x for p in pts]); ty = trimmed([p.y for p in pts])
        slices.append((z0, z1, tx[0], tx[1], ty[0], ty[1]))
    # walk slices, break stories on PERSISTENT footprint jumps (a single deviating slice is sill/relief
    # noise and is skipped; two consecutive deviations = a real story break, e.g. a jetty)
    stories, run = [], []
    def med_of(r, idx): return sorted(s[idx] for s in r)[len(r) // 2]
    def flush():
        if len(run) < 2: return
        stories.append({"z0": run[0][0], "z1": run[-1][1],
                        "x0": med_of(run, 2), "x1": med_of(run, 3),
                        "y0": med_of(run, 4), "y1": med_of(run, 5)})
    real = [s for s in slices if s is not None]
    # ROOF TAPER CUT: walls have constant footprints; a roof shrinks steadily slice over slice. Find the
    # earliest slice where the min horizontal width becomes non-increasing for K consecutive slices with a
    # substantial total shrink, and cut the body fit there - everything above is roof (fit_roofs' domain).
    K = 4
    width = [min(s[3] - s[2], s[5] - s[4]) for s in real]
    taper_at = None
    for t in range(len(real) - K):
        ok = all(width[t + k + 1] <= width[t + k] + w2r(0.03) for k in range(K))
        if not (ok and (width[t] - width[t + K]) > w2r(0.40)):
            continue
        # a TRUE roof taper never re-widens above itself (porch furniture below walls does; a jetty does)
        if any(wd > width[t] + w2r(0.15) for wd in width[t + 1:]):
            continue
        taper_at = t
        break
    roof_floor_hint = real[taper_at][0] if taper_at is not None else None
    if taper_at is not None:
        real = real[:taper_at]
    i = 0
    while i < len(real):
        s = real[i]
        if not run:
            run.append(s); i += 1; continue
        dev = any(abs(s[j] - med_of(run, j)) > JUMP_RAW for j in (2, 3, 4, 5))
        if not dev:
            run.append(s); i += 1; continue
        nxt = real[i + 1] if i + 1 < len(real) else None
        nxt_dev = nxt is not None and any(abs(nxt[j] - med_of(run, j)) > JUMP_RAW for j in (2, 3, 4, 5))
        if nxt_dev:
            flush(); run = [s]                    # persistent -> story break
        # else: lone outlier slice (sill band) - skip it
        i += 1
    flush()
    # roofs also show up as "stories" whose footprint shrinks slice by slice; drop trailing stories whose
    # height band is inside the detected roof zone (handled by fit_roofs) - heuristic: a story is a WALL story
    # if its slice run is at least 3 slices AND its footprint is stable; the roof taper breaks into 1-2 slice
    # runs that we discard.
    stories = [st for st in stories if (st["z1"] - st["z0"]) > 2.5 * step]
    # merge adjacent stories whose footprints agree (the break was sill noise, not a jetty)
    merged = []
    for st in stories:
        if merged and all(abs(st[k] - merged[-1][k]) <= JUMP_RAW for k in ("x0", "x1", "y0", "y1")):
            merged[-1]["z1"] = st["z1"]
            for k in ("x0", "y0"): merged[-1][k] = min(merged[-1][k], st[k])
            for k in ("x1", "y1"): merged[-1][k] = max(merged[-1][k], st[k])
        else:
            merged.append(dict(st))
    # drop narrow protrusion/ridge bands caught as stories: a wall story has a substantial footprint
    if merged:
        biggest = max((st["x1"] - st["x0"]) * (st["y1"] - st["y0"]) for st in merged)
        merged = [st for st in merged
                  if (st["x1"] - st["x0"]) * (st["y1"] - st["y0"]) > 0.30 * biggest]
    # drop the roof-eave band: a short story whose footprint IS the full mesh bbox (the overhang)
    merged = [st for st in merged
              if not ((st["x1"] - st["x0"]) > 0.96 * (mx.x - mn.x)
                      and (st["y1"] - st["y0"]) > 0.96 * (mx.y - mn.y)
                      and (st["z1"] - st["z0"]) < 0.18 * raw_h)]
    return merged, roof_floor_hint

def fit_roofs(roof_floor):
    """Cluster up-facing faces ABOVE the fitted wall top by facing + connectivity; least-squares plane per
    cluster; emit a wedge whose hypotenuse is the fitted plane, or a slab box when near-flat."""
    roof_faces = []
    for n, vs, c, a in polys:
        if c.z <= roof_floor or a <= 1e-5: continue
        if n.z >= 0.985 or 0.20 < n.z < 0.985:
            roof_faces.append((n, vs, c, a))
    bins = {}
    for f in roof_faces:
        n = f[0]
        if n.z >= 0.985: key = "flat"
        elif abs(n.x) > abs(n.y): key = "x+" if n.x > 0 else "x-"
        else: key = "y+" if n.y > 0 else "y-"
        bins.setdefault(key, []).append(f)
    def clusters(faces, reach):
        parent = list(range(len(faces)))
        def find(i):
            while parent[i] != i: parent[i] = parent[parent[i]]; i = parent[i]
            return i
        for i in range(len(faces)):
            for j in range(i + 1, len(faces)):
                if (faces[i][2] - faces[j][2]).length < reach:
                    pi, pj = find(i), find(j)
                    if pi != pj: parent[pi] = pj
        out = {}
        for i, f in enumerate(faces): out.setdefault(find(i), []).append(f)
        return [v for v in out.values() if sum(f[3] for f in v) > (raw_h * 0.10) ** 2]

    pieces, seen = [], set()
    def emit(p):
        if any(p["min"][i] >= p["max"][i] for i in range(3)): return          # degenerate
        key = (p["kind"], p.get("axis"), p.get("dir"),
               tuple(round(v, 2) for v in p["min"]), tuple(round(v, 2) for v in p["max"]))
        if key in seen: return                                                 # tile-strata clone
        seen.add(key); pieces.append(p)

    for key, faces in bins.items():
        for cl in clusters(faces, reach=max(w2r(0.9), raw_h * 0.06)):
            xs = [v.x for f in cl for v in f[1]]; ys = [v.y for f in cl for v in f[1]]
            zs = [v.z for f in cl for v in f[1]]
            x0, x1 = min(xs), max(xs); y0, y1 = min(ys), max(ys); z0, z1 = min(zs), max(zs)
            angle = 0.0
            if key != "flat":
                axis = "x" if key[0] == "x" else "y"
                su = sw = suu = suz = sz = 0.0
                for n, vs, c, a in cl:
                    u = c.x if axis == "x" else c.y
                    sw += a; su += a * u; suu += a * u * u; sz += a * c.z; suz += a * u * c.z
                den = sw * suu - su * su
                if abs(den) < 1e-9: continue
                slope_a = (sw * suz - su * sz) / den
                b = (sz - slope_a * su) / sw
                u0, u1 = (x0, x1) if axis == "x" else (y0, y1)
                z_lo_end, z_hi_end = sorted((slope_a * u0 + b, slope_a * u1 + b))
                angle = math.degrees(math.atan(abs(slope_a)))
                if angle > 55.0: continue                     # wall relief / trim, not a roof
            if key == "flat" or angle < FLAT_DEG:
                if min(x1 - x0, y1 - y0) < raw_h * 0.18: continue   # chimney caps etc.
                emit({"kind": "box", "name": "roof_flat",
                      "min": [x0, y0, max(z0, z1 - w2r(0.30))], "max": [x1, y1, z1]})
                continue
            dirn = 1 if (slope_a > 0) else -1
            # clamp the slope-axis extent to where the FITTED PLANE meets the cluster's eave and ridge
            # heights - the raw bbox overshoots past the ridge (cross-facing faces, dormers), which made
            # opposing slabs cross in an X above the roof.
            z_eave = max(z0, roof_floor - w2r(0.05))
            z_ridge = z1
            u_a = (z_eave - b) / slope_a
            u_b = (z_ridge - b) / slope_a
            u_lo2 = max(u0, min(u_a, u_b)); u_hi2 = min(u1, max(u_a, u_b))
            if u_hi2 - u_lo2 < w2r(0.15): continue
            z_at_lo = slope_a * u_lo2 + b; z_at_hi = slope_a * u_hi2 + b
            mn_p = [x0, y0, min(z_at_lo, z_at_hi)]; mx_p = [x1, y1, max(z_at_lo, z_at_hi)]
            if axis == "x": mn_p[0], mx_p[0] = u_lo2, u_hi2
            else: mn_p[1], mx_p[1] = u_lo2, u_hi2
            emit({"kind": "slab", "name": f"roof_{key}", "axis": axis, "dir": dirn,
                  "min": mn_p, "max": mx_p,
                  "thickness": w2r(0.35), "angleDeg": round(angle, 1)})
    # merge pieces lying on the SAME plane (tile strata / hip facets cluster apart but fit one plane):
    # same kind+axis+dir, angle within 3 deg, z ends within ~0.1 m -> union the bounds.
    merged = []
    for p_ in pieces:
        hit = None
        for m in merged:
            if (m["kind"], m.get("axis"), m.get("dir")) != (p_["kind"], p_.get("axis"), p_.get("dir")):
                continue
            if abs(m.get("angleDeg", 0) - p_.get("angleDeg", 0)) > 3.0: continue
            if abs(m["min"][2] - p_["min"][2]) > w2r(0.12) or abs(m["max"][2] - p_["max"][2]) > w2r(0.12):
                continue
            hit = m; break
        if hit is None:
            merged.append(dict(p_))
        else:
            for i in range(3):
                hit["min"][i] = min(hit["min"][i], p_["min"][i])
                hit["max"][i] = max(hit["max"][i], p_["max"][i])
    # drop dormer/trim slivers: a roof piece whose 2D footprint is tiny next to the main roof reads as
    # decoration (and crossed slivers over the roof look broken in the overlay + snag sliding capsules)
    slabs = [m for m in merged if m["kind"] == "slab"]
    if slabs:
        areas = {id(m): (m["max"][0] - m["min"][0]) * (m["max"][1] - m["min"][1]) for m in slabs}
        biggest = max(areas.values())
        merged = [m for m in merged if m["kind"] != "slab" or areas[id(m)] > 0.15 * biggest]
    # RIDGE TRIMMING: where two opposing slabs on the same axis OVERLAP on the slope axis (each was
    # clamped to its own cluster's ridge, so where the fits differ they poke past each other - X tips at
    # peaks, a full X where a dormer pair crosses), cut both at the overlap midpoint so they meet there.
    # Bounded by construction: only shrinks within a real overlap, each slab keeps its own plane, and
    # non-overlapping pairs (hip skirts across a flat cap) are untouched.
    slabs = [m for m in merged if m["kind"] == "slab"]
    for i in range(len(slabs)):
        for j in range(i + 1, len(slabs)):
            si, sj = slabs[i], slabs[j]
            if si["axis"] != sj["axis"] or si["dir"] == sj["dir"]: continue
            ax = 0 if si["axis"] == "x" else 1
            cx = 1 - ax
            if min(si["max"][cx], sj["max"][cx]) - max(si["min"][cx], sj["min"][cx]) <= 0: continue
            if not (si["min"][2] < sj["max"][2] and sj["min"][2] < si["max"][2]): continue
            o0 = max(si["min"][ax], sj["min"][ax]); o1 = min(si["max"][ax], sj["max"][ax])
            if o1 <= o0: continue
            u_m = (o0 + o1) / 2
            for s in (si, sj):
                u0, u1 = s["min"][ax], s["max"][ax]
                if u1 - u0 < 1e-6: continue
                # z of this slab's own plane at u_m (dir>0: z rises min->max with u; dir<0: falls)
                f = (u_m - u0) / (u1 - u0)
                z_at = (s["min"][2] + (s["max"][2] - s["min"][2]) * f) if s["dir"] > 0                        else (s["max"][2] - (s["max"][2] - s["min"][2]) * f)
                if s["dir"] > 0 and u1 > u_m:        # ridge end is the high-u side
                    s["max"][ax] = round(u_m, 3); s["max"][2] = round(z_at, 3)
                elif s["dir"] < 0 and u0 < u_m:      # ridge end is the low-u side
                    s["min"][ax] = round(u_m, 3); s["max"][2] = round(z_at, 3)
    merged = [m for m in merged if m["kind"] != "slab"
              or (m["max"][0] - m["min"][0] > 1e-3 and m["max"][1] - m["min"][1] > 1e-3)]
    return merged

# =====================================================================================
# AUDIT - standable tops vs ceilings above them
# =====================================================================================
def spec_pieces(spec):
    out = []
    for b in spec.get("boxes", []):
        out.append({"kind": "box", "name": b["name"], "min": b["min"], "max": b["max"]})
    for w in spec.get("wedges", []):
        out.append({"kind": "wedge", "name": w["name"], "min": w["min"], "max": w["max"],
                    "axis": w.get("axis", "x"), "dir": w.get("dir", 1)})
    for c in spec.get("cylinders", []):
        r = c["radius"]
        out.append({"kind": "box", "name": c["name"],
                    "min": [c["center"][0] - r, c["center"][1] - r, c["z"][0]],
                    "max": [c["center"][0] + r, c["center"][1] + r, c["z"][1]]})
    for s in spec.get("slabs", []):
        out.append({"kind": "slab", "name": s["name"], "min": s["min"], "max": s["max"],
                    "axis": s.get("axis", "x"), "dir": s.get("dir", 1),
                    "thickness": s.get("thickness", 0.1)})
    return out

def slab_plane_z(s, x, y):
    """Top-plane z of a slab piece at (x, y) (clamped into its footprint)."""
    ax = 0 if s.get("axis", "x") == "x" else 1
    u = max(s["min"][ax], min(s["max"][ax], x if ax == 0 else y))
    u0, u1 = s["min"][ax], s["max"][ax]
    zA, zB = (s["min"][2], s["max"][2]) if s.get("dir", 1) > 0 else (s["max"][2], s["min"][2])
    f = 0.0 if u1 - u0 < 1e-9 else (u - u0) / (u1 - u0)
    return zA + (zB - zA) * f

def overlap2d(a, b, shrink=0.0):
    ox = min(a["max"][0], b["max"][0]) - max(a["min"][0], b["min"][0])
    oy = min(a["max"][1], b["max"][1]) - max(a["min"][1], b["min"][1])
    return ox > shrink and oy > shrink

def audit(spec):
    pieces = spec_pieces(spec)
    reach = w2r(JUMP_APEX_FEET + 0.4)     # a top below this is jump-mountable from the ground
    head = w2r(HEADROOM)
    warns = []
    for a in pieces:
        top = a["max"][2]
        if a["kind"] == "wedge":
            if math.degrees(math.atan((a["max"][2] - a["min"][2]) /
                    max(1e-6, a["max"][0 if a["axis"] == "x" else 1] - a["min"][0 if a["axis"] == "x" else 1]))) > MAX_SLOPE_DEG:
                continue                   # steeper than the gate: not standable
        if top - mn.z > reach:
            continue                        # unreachable top (no ladder mechanics)
        for b in pieces:
            if b is a: continue
            if not overlap2d(a, b, shrink=w2r(0.15)): continue
            if b["kind"] == "slab":
                cx = (max(a["min"][0], b["min"][0]) + min(a["max"][0], b["max"][0])) / 2
                cy = (max(a["min"][1], b["min"][1]) + min(a["max"][1], b["max"][1])) / 2
                bot = slab_plane_z(b, cx, cy) - b.get("thickness", 0.1)
            else:
                bot = b["min"][2]
            if bot <= top: continue
            gap = (bot - top) * ws
            if gap < HEADROOM:
                warns.append(f"PIN TRAP: standing on '{a['name']}' (top {top:.3f} raw, {top*ws:.2f} m world reachable) "
                             f"under '{b['name']}' (underside {bot:.3f} raw) leaves {gap:.2f} m headroom < {HEADROOM:.2f}")
    return warns

# =====================================================================================
if mode == "fit":
    body, taper_z = fit_body()
    if roof_floor_override is not None:
        body = [st for st in body if st["z0"] < roof_floor_override]
        for st in body: st["z1"] = min(st["z1"], roof_floor_override)
        roof_floor = roof_floor_override
    else:
        wall_top = max((st["z1"] for st in body), default=mn.z + raw_h * 0.55)
        roof_floor = (taper_z if taper_z is not None else wall_top) - w2r(0.05)
    roofs = fit_roofs(roof_floor)
    print(f"ROOF FLOOR: {roof_floor:.3f} raw ({'override' if roof_floor_override is not None else 'auto'})")
    draft = {"_comment": f"DRAFT fitted by fit_proxy.py from {os.path.basename(src)} "
                         f"(ws={ws:.4f} world m per raw unit). Merge with hand-authored capsule pieces "
                         f"(entrance steps, rails, furniture) per the README capsule rules, then audit.",
             "boxes": [], "wedges": []}
    for i, st in enumerate(body):
        name = "body" if i == 0 else f"story_{i}"
        z0 = -0.3 if i == 0 else st["z0"]
        draft["boxes"].append({"name": name, "min": [round(st["x0"], 3), round(st["y0"], 3), round(z0, 3)],
                               "max": [round(st["x1"], 3), round(st["y1"], 3), round(st["z1"], 3)]})
    n = {}
    for p in roofs:
        nm = p["name"]; n[nm] = n.get(nm, 0) + 1
        nm2 = nm if n[nm] == 1 else f"{nm}{n[nm]}"
        entry = {"name": nm2, "min": [round(v, 3) for v in p["min"]], "max": [round(v, 3) for v in p["max"]]}
        if p["kind"] == "slab":
            entry["axis"] = p["axis"]; entry["dir"] = p["dir"]; entry["thickness"] = round(p["thickness"], 3)
            print(f"ROOF {nm2}: slab axis {p['axis']} dir {p['dir']} angle {p['angleDeg']} deg "
                  f"z {p['min'][2]:.3f}..{p['max'][2]:.3f}")
            draft.setdefault("slabs", []).append(entry)
        elif p["kind"] == "wedge":
            entry["axis"] = p["axis"]; entry["dir"] = p["dir"]
            print(f"ROOF {nm2}: axis {p['axis']} dir {p['dir']} angle {p['angleDeg']} deg "
                  f"z {p['min'][2]:.3f}..{p['max'][2]:.3f}")
            draft["wedges"].append(entry)
        else:
            print(f"ROOF {nm2}: flat slab z {p['min'][2]:.3f}..{p['max'][2]:.3f}")
            draft["boxes"].append(entry)
    for st in body:
        print(f"BODY story: x {st['x0']:.3f}..{st['x1']:.3f}  y {st['y0']:.3f}..{st['y1']:.3f}  "
              f"z {st['z0']:.3f}..{st['z1']:.3f}")
    json.dump(draft, open(spec_path, "w"), indent=2)
    print("WROTE", spec_path)
elif mode == "audit":
    spec = json.load(open(spec_path))
    warns = audit(spec)
    for w in warns: print(w)
    print(f"AUDIT {'CLEAN' if not warns else f'{len(warns)} WARNING(S)'}")
    sys.exit(0 if not warns else 2)
else:
    raise SystemExit(f"unknown mode {mode}")
