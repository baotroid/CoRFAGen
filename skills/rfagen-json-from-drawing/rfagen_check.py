#!/usr/bin/env python3
"""RFAGen family JSON checker + preview renderer.

usage: python3 rfagen_check.py family.json [--expect X=900,Y=1250,Z=2480] [--drawing page.png] [--out preview.png]
       python3 rfagen_check.py catalog.json [--type N]        (catalog JSON for TypeGen: every row is checked)
Prints a report (errors / warnings / element boxes / overall size) and writes a preview PNG
(3D + plan XY + front XZ + side YZ, optional source drawing panel). Exit code 1 if errors.
Catalog JSON: each row of catalog.types is evaluated (overall X must equal L, top Z must equal H when these
columns exist, bolt holes must lie inside the flange); the preview shows row N (default 0).
"""
import argparse, json, math, re, sys

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d.art3d import Poly3DCollection

GEOM_TYPES = {"extrusion", "blend", "sweep"}
UNITS = {"inches", "feet", "millimeters", "centimeters", "meters"}
CATEGORIES = {"Casework", "Furniture", "Doors", "Windows", "Generic Models", "Specialty Equipment",
              "Pipe Accessories", "Mechanical Equipment"}
PARAM_TYPES = {"length", "angle", "number", "integer", "yes_no", "text", "material", "mass", "hvac_power",
               "electrical_power", "voltage", "current", "frequency", "air_flow", "pipe_flow", "pressure", "temperature"}
TECH_TYPES = PARAM_TYPES - {"length", "angle", "number", "integer", "yes_no", "text", "material"}
GROUPS = {"Dimensions", "Construction", "Identity Data", "Graphics", "Materials and Finishes", "Mechanical", "Electrical", "Other"}
BUILTINS = {"Model", "Manufacturer", "Description", "Type Comments", "URL", "Cost", "Keynote", "Assembly Code"}
NUMERIC = {"length", "angle", "number", "integer"}

errors, warnings = [], []


def num(v):
    if isinstance(v, bool):
        return float(v)
    if isinstance(v, (int, float)):
        return float(v)
    if isinstance(v, str):
        m = re.match(r"^\s*-?\d+(\.\d+)?", v.replace(",", "."))
        if m:
            return float(m.group(0))
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("json")
    ap.add_argument("--expect", default="", help="expected overall size, e.g. X=900,Y=1250,Z=2480")
    ap.add_argument("--drawing", default="", help="PNG of the source drawing to show next to the preview")
    ap.add_argument("--out", default="")
    ap.add_argument("--type", type=int, default=0, help="catalog: row shown in the preview")
    a = ap.parse_args()

    fam = json.load(open(a.json, encoding="utf-8"))
    cat = fam.get("catalog")
    if isinstance(cat, dict) and cat.get("types"):
        return check_catalog(fam, cat, a)
    return check_one(fam, a, None)


def check_catalog(fam, cat, a):
    rows = cat["types"]
    print(f"== catalog: {len(rows)} types ==")
    bad = 0
    for i, row in enumerate(rows):
        errors.clear(); warnings.clear()
        res = check_one(fam, a, row, quiet=True)
        o = res.get("overall")
        msg = []
        if o:
            sx, top = o[3] - o[0], o[5]
            if num(row.get("L")) is not None and abs(sx - num(row["L"])) > 0.5: errors.append(f"overall X {sx:.1f} != L {row['L']}")
            if num(row.get("H")) is not None and abs(top - num(row["H"])) > 0.5: errors.append(f"top Z {top:.1f} != H {row['H']}")
            msg.append(f"X {sx:.0f}  top {top:.0f}")
        key = " ".join(f"{k}{row[k]}" for k in ("DN", "PN") if k in row) or f"row {i}"
        print(f"  {i:3d} {key:<14} {'  '.join(msg):<22} {'OK' if not errors else 'FAIL: ' + '; '.join(errors[:4])}")
        if errors: bad += 1
    errors.clear(); warnings.clear()
    row = rows[min(max(a.type, 0), len(rows) - 1)]
    print(f"== preview: row {a.type} ==")
    check_one(fam, a, row)
    print("CATALOG RESULT:", "PASS" if bad == 0 and not errors else f"FAIL ({bad} row(s))")
    return 1 if bad or errors else 0


def check_one(fam, a, row, quiet=False):

    # ---------- structure
    for k in ("metadata", "units", "reference_planes", "parameters", "geometry"):
        if k not in fam:
            errors.append(f"missing top-level key '{k}'")
    if errors:
        report([], {}, a); return {"overall": None} if quiet else 1
    md = fam["metadata"]
    if md.get("schema_version") != "0.1": errors.append("metadata.schema_version must be '0.1'")
    if md.get("category") not in CATEGORIES: errors.append(f"metadata.category must be one of {sorted(CATEGORIES)}")
    if not md.get("description"): errors.append("metadata.description is required")
    if fam["units"] not in UNITS: errors.append(f"units must be one of {sorted(UNITS)}")

    params = {}
    seen = set()
    for p in fam["parameters"]:
        n = p.get("name", "")
        for req in ("name", "type", "instance_or_type", "default_value", "group"):
            if req not in p: errors.append(f"parameter '{n}': missing '{req}'")
        if n in seen: errors.append(f"duplicate parameter '{n}'")
        seen.add(n)
        t = p.get("type")
        if t not in PARAM_TYPES: errors.append(f"parameter '{n}': unknown type '{t}'")
        if p.get("group") not in GROUPS: errors.append(f"parameter '{n}': unknown group '{p.get('group')}'")
        if p.get("instance_or_type") not in ("instance", "type"): errors.append(f"parameter '{n}': instance_or_type")
        if t in TECH_TYPES and not p.get("unit"): warnings.append(f"parameter '{n}' ({t}) has no 'unit'")
        if p.get("builtin") and p["builtin"] not in BUILTINS: errors.append(f"parameter '{n}': bad builtin")
        if t in NUMERIC:
            v = num(p.get("default_value"))
            if v is None: errors.append(f"parameter '{n}': default_value must be a number")
            else: params[n.replace(" ", "_")] = v
            if not re.fullmatch(r"[A-Za-z][A-Za-z0-9 ]*", n):
                errors.append(f"geometric parameter '{n}' must use Latin letters/digits/spaces only")
    if row:  # catalog row = values of the type
        for k, v in row.items():
            if num(v) is not None and k.replace(" ", "_") in params: params[k.replace(" ", "_")] = num(v)

    def ev(x, ctx):
        if isinstance(x, (int, float)) and not isinstance(x, bool): return float(x)
        if not isinstance(x, str): errors.append(f"{ctx}: bad expression {x!r}"); return 0.0
        for ident in re.findall(r"[A-Za-z_][A-Za-z0-9_]*", x):
            if ident not in params:
                errors.append(f"{ctx}: unknown parameter '{ident}' in '{x}'"); return 0.0
        s = x
        for k in sorted(params, key=len, reverse=True):
            s = re.sub(r"\b" + re.escape(k) + r"\b", "(" + repr(params[k]) + ")", s)
        if not re.fullmatch(r"[0-9eE\.\s\+\-\*/\(\)]*", s):
            errors.append(f"{ctx}: unsupported syntax '{x}' (only + - * / and parentheses)"); return 0.0
        try: return float(eval(s))
        except Exception as e: errors.append(f"{ctx}: cannot evaluate '{x}' ({e})"); return 0.0

    planes = {}
    for rp in fam["reference_planes"]:
        if rp.get("direction") not in ("x", "y", "z"): errors.append(f"plane '{rp.get('name')}': direction x|y|z")
        if rp["name"] in planes: errors.append(f"duplicate reference plane '{rp['name']}'")
        planes[rp["name"]] = (rp["direction"], ev(rp["offset"], f"plane '{rp['name']}'"))

    subcats = set(fam.get("subcategories", []))

    # ---------- geometry
    def poly(pr, ctx, n=48):
        sh = pr.get("shape")
        if sh == "circle":
            cu, cv, r = ev(pr["center"]["u"], ctx), ev(pr["center"]["v"], ctx), ev(pr["radius"], ctx)
            if r <= 0: errors.append(f"{ctx}: radius {r} <= 0")
            return [(cu + r * math.cos(t), cv + r * math.sin(t)) for t in [2 * math.pi * i / n for i in range(n)]]
        if sh == "rectangle":
            ou, ov, w, h = ev(pr["origin"]["u"], ctx), ev(pr["origin"]["v"], ctx), ev(pr["width"], ctx), ev(pr["height"], ctx)
            if w <= 0 or h <= 0: errors.append(f"{ctx}: rectangle {w} x {h} must be positive")
            return [(ou, ov), (ou + w, ov), (ou + w, ov + h), (ou, ov + h)]
        if sh == "l_shape":
            ou, ov = ev(pr["origin"]["u"], ctx), ev(pr["origin"]["v"], ctx)
            W, H = ev(pr["overall_width"], ctx), ev(pr["overall_height"], ctx)
            HT, VT = ev(pr["horizontal_leg_thickness"], ctx), ev(pr["vertical_leg_thickness"], ctx)
            return [(ou, ov), (ou + W, ov), (ou + W, ov + HT), (ou + VT, ov + HT), (ou + VT, ov + H), (ou, ov + H)]
        if sh == "custom":
            pts = [(ev(p["u"], ctx), ev(p["v"], ctx)) for p in pr["vertices"]]
            if len(pts) < 3: errors.append(f"{ctx}: custom profile needs >= 3 vertices")
            if self_intersects(pts): errors.append(f"{ctx}: custom profile is self-intersecting")
            return pts
        errors.append(f"{ctx}: unknown profile shape '{sh}'"); return [(0, 0), (1, 0), (1, 1)]

    def to3(u, v, w, d):
        return {"x": (w, u, v), "y": (u, w, v), "z": (u, v, w)}[d]

    solids, boxes = [], {}
    names = set()
    for g in expand_patterns(fam["geometry"], ev):
        name = g.get("name", "?"); ctx = f"'{name}'"
        if name in names: errors.append(f"duplicate geometry name {ctx}")
        names.add(name)
        if g.get("type") not in GEOM_TYPES: errors.append(f"{ctx}: type must be extrusion|blend|sweep"); continue
        if g.get("sketch_plane") not in planes: errors.append(f"{ctx}: sketch plane '{g.get('sketch_plane')}' not defined"); continue
        if g.get("subcategory") and g["subcategory"] not in subcats: warnings.append(f"{ctx}: subcategory '{g['subcategory']}' not in subcategories")
        d, off = planes[g["sketch_plane"]]
        if g["type"] == "extrusion":
            pts = poly(g["profile"], ctx); s, e = ev(g["extrusion_start"], ctx), ev(g["extrusion_end"], ctx)
            if abs(e - s) < 1e-6: errors.append(f"{ctx}: zero extrusion depth")
            A = [to3(u, v, off + s, d) for u, v in pts]; B = [to3(u, v, off + e, d) for u, v in pts]
            check_holes(g, ev, ctx)
        elif g["type"] == "blend":
            bp, tp = g["bottom_profile"], g["top_profile"]
            pb, pt = poly(bp, ctx), poly(tp, ctx)
            b, t = ev(g["bottom_offset"], ctx), ev(g["top_offset"], ctx)
            if abs(t - b) < 1e-6: errors.append(f"{ctx}: blend has zero height")
            if bp["shape"] != tp["shape"]: errors.append(f"{ctx}: blend profiles must have the same shape type")
            if bp["shape"] == "circle":
                if (ev(bp["center"]["u"], ctx), ev(bp["center"]["v"], ctx)) != (ev(tp["center"]["u"], ctx), ev(tp["center"]["v"], ctx)):
                    warnings.append(f"{ctx}: non-coaxial circle blend (will be a faceted Revit blend, not a revolution)")
            elif len(pb) != len(pt): errors.append(f"{ctx}: blend profiles need the same vertex count")
            A = [to3(u, v, off + b, d) for u, v in pb]; B = [to3(u, v, off + t, d) for u, v in pt]
        else:  # sweep: approximate by the path polyline
            pts = poly(g["profile"], ctx)
            r = max(max(p[0] for p in pts) - min(p[0] for p in pts), max(p[1] for p in pts) - min(p[1] for p in pts)) / 2
            path = []
            for sg in g["path"]["segments"]:
                for k in ("start", "end"):
                    path.append((ev(sg[k]["u"], ctx), ev(sg[k]["v"], ctx)))
            A = [to3(u - r, v - r, off - r, d) for u, v in path]; B = [to3(u + r, v + r, off + r, d) for u, v in path]
        allp = A + B
        bx = [min(p[i] for p in allp) for i in range(3)] + [max(p[i] for p in allp) for i in range(3)]
        boxes[name] = (bx, g.get("is_void", False))
        solids.append((name, g.get("is_void", False), A, B))

    if quiet:
        return {"overall": overall(boxes)}
    report(solids, boxes, a)
    render(solids, boxes, fam, a)
    return 1 if errors else 0


def expand_patterns(geom, ev):
    """Element-level polar array ("pattern" on an element) → copies with moved profile centres / origins."""
    out = []
    for g in geom:
        p = g.get("pattern")
        if not isinstance(p, dict):
            out.append(g); continue
        ctx = f"'{g.get('name')}' pattern"
        n = int(round(ev(p.get("count", 1), ctx))); r = ev(p.get("radius", 0), ctx); a0 = ev(p.get("start_angle", 0), ctx)
        cu = ev(p["center"]["u"], ctx) if "center" in p else 0.0
        cv = ev(p["center"]["v"], ctx) if "center" in p else 0.0
        if not 1 <= n <= 64: errors.append(f"{ctx}: count {n} must be 1..64"); n = max(1, min(n, 64))
        for k in range(n):
            t = math.radians(a0 + 360.0 * k / n); du, dv = cu + r * math.cos(t), cv + r * math.sin(t)
            c = json.loads(json.dumps(g)); c.pop("pattern"); c["name"] = f"{g.get('name')} #{k + 1}"
            for key in ("profile", "bottom_profile", "top_profile"):
                pr = c.get(key)
                if not isinstance(pr, dict): continue
                pk = "center" if "center" in pr else "origin" if "origin" in pr else None
                if pk is None: continue
                pr[pk]["u"] = f"({pr[pk]['u']}) + ({du:.6f})"; pr[pk]["v"] = f"({pr[pk]['v']}) + ({dv:.6f})"
            out.append(c)
    return out


def check_holes(g, ev, ctx):
    """"holes": inner circles (optionally a polar pattern) must lie inside a circular profile."""
    pr = g.get("profile", {})
    if not g.get("holes"): return
    if pr.get("shape") != "circle": return
    R = ev(pr["radius"], ctx); pcu, pcv = ev(pr["center"]["u"], ctx), ev(pr["center"]["v"], ctx)
    for h in g["holes"]:
        hr = ev(h["radius"], ctx)
        if hr <= 0: errors.append(f"{ctx}: hole radius {hr} <= 0")
        if "pattern" in h:
            p = h["pattern"]; pr_ = ev(p["radius"], ctx); n = ev(p["count"], ctx)
            if pr_ + hr > R + 1e-6: errors.append(f"{ctx}: bolt circle {pr_:.1f} + hole {hr:.1f} outside the profile R {R:.1f}")
            if n < 1: errors.append(f"{ctx}: hole count {n}")
        else:
            hu, hv = ev(h["center"]["u"], ctx), ev(h["center"]["v"], ctx)
            if math.hypot(hu - pcu, hv - pcv) + hr > R + 1e-6: errors.append(f"{ctx}: hole outside the profile")


def self_intersects(p):
    n = len(p)
    def cr(o, a, b): return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    for i in range(n):
        for j in range(i + 2, n):
            if i == 0 and j == n - 1: continue
            a, b, c, d = p[i], p[(i + 1) % n], p[j], p[(j + 1) % n]
            d1, d2, d3, d4 = cr(c, d, a), cr(c, d, b), cr(a, b, c), cr(a, b, d)
            if d1 * d2 < -1e-9 and d3 * d4 < -1e-9: return True
    return False


def overall(boxes):
    s = [b for b, v in boxes.values() if not v]
    if not s: return None
    return [min(b[i] for b in s) for i in range(3)] + [max(b[i + 3] for b in s) for i in range(3)]


def report(solids, boxes, a):
    print("== RFAGen JSON check ==")
    for n, (b, v) in boxes.items():
        print(f"  {n:<34}{'void ' if v else '     '} X {b[0]:8.1f}..{b[3]:8.1f}  Y {b[1]:8.1f}..{b[4]:8.1f}  Z {b[2]:8.1f}..{b[5]:8.1f}")
    o = overall(boxes)
    if o:
        size = (o[3] - o[0], o[4] - o[1], o[5] - o[2])
        print(f"OVERALL solid size X x Y x Z = {size[0]:.1f} x {size[1]:.1f} x {size[2]:.1f}  "
              f"(X {o[0]:.1f}..{o[3]:.1f}, Y {o[1]:.1f}..{o[4]:.1f}, Z {o[2]:.1f}..{o[5]:.1f})")
        for part in filter(None, a.expect.split(",")):
            ax, val = part.split("="); val = float(val); got = size["XYZ".index(ax.strip().upper())]
            if abs(got - val) > max(1.0, 0.005 * val):
                errors.append(f"overall {ax} = {got:.1f}, expected {val:.1f}")
            else:
                print(f"  overall {ax} = {got:.1f} OK")
    for w in warnings: print("WARNING:", w)
    for e in errors: print("ERROR:", e)
    print("RESULT:", "FAIL" if errors else "PASS")


def render(solids, boxes, fam, a):
    out = a.out or a.json.rsplit(".", 1)[0] + "-preview.png"
    ncol = 5 if a.drawing else 4
    fig = plt.figure(figsize=(5 * ncol, 6.5))
    ax3 = fig.add_subplot(1, ncol, 1, projection="3d")
    views = [(fig.add_subplot(1, ncol, 2), 0, 1, "PLAN (X→, Y↑; front = −Y)"),
             (fig.add_subplot(1, ncol, 3), 0, 2, "FRONT (X→, Z↑)"),
             (fig.add_subplot(1, ncol, 4), 1, 2, "LEFT SIDE (Y→, Z↑)")]
    for name, void, A, B in solids:
        col = "#d97757" if void else "#9fb3c8"; lc = "r" if void else "k"
        n = len(A)
        faces = [A, B] + [[A[i], A[(i + 1) % n], B[(i + 1) % n], B[i]] for i in range(n)]
        ax3.add_collection3d(Poly3DCollection(faces, facecolor=col, edgecolor="#444", linewidths=0.1, alpha=0.3 if void else 0.9))
        for axv, i, j, _ in views:
            for Q in (A, B):
                axv.plot([p[i] for p in Q] + [Q[0][i]], [p[j] for p in Q] + [Q[0][j]], color=lc, lw=0.6)
            for k in range(n):
                axv.plot([A[k][i], B[k][i]], [A[k][j], B[k][j]], color=lc, lw=0.25)
    o = overall(boxes)
    if o:
        c = [(o[i] + o[i + 3]) / 2 for i in range(3)]; r = max(o[3] - o[0], o[4] - o[1], o[5] - o[2]) / 2
        ax3.set_xlim(c[0] - r, c[0] + r); ax3.set_ylim(c[1] - r, c[1] + r); ax3.set_zlim(c[2] - r, c[2] + r)
        ax3.set_box_aspect((1, 1, 1))
    ax3.view_init(22, -60); ax3.set_title(fam["metadata"].get("name", "family") + " — 3D")
    for axv, i, j, t in views:
        axv.set_aspect("equal"); axv.set_title(t); axv.grid(alpha=0.3)
    if a.drawing:
        axd = fig.add_subplot(1, ncol, 5); axd.imshow(plt.imread(a.drawing)); axd.axis("off"); axd.set_title("SOURCE")
    fig.suptitle(f"{fam['metadata'].get('name', '')}  ·  units: {fam['units']}  ·  "
                 + ("PASS" if not errors else f"{len(errors)} ERROR(S)"), color="green" if not errors else "red")
    plt.tight_layout(); plt.savefig(out, dpi=90)
    print("preview:", out)


if __name__ == "__main__":
    sys.exit(main())
