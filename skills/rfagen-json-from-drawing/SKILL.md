---
name: "rfagen-json-from-drawing"
description: "Build a BeGenSt/RFAGen Revit family JSON (or a TypeGen catalog JSON with many types) from a PDF drawing, photo or sketch: analyse, ask about conflicts, check sizes, render a preview. No API key needed."
---

# RFAGen family JSON from drawings (no API key)

Produces the JSON that the BeGenSt Revit add-in (tab **BeGenSt → PDF&Pic2RFA → Generate → Load JSON… (catalogs: TypeGen → Load catalog JSON…)**,
then **RFAGen**) turns into a Revit family. You do here what the add-in's AI pipeline does:
**analyse sources → ask about conflicts → generate JSON → check sizes & graphics → preview image → deliver.**
Talk to the user in their language (usually Russian). Never skip the conflict dialog or the check.

## 0. Inputs

Up to 3 files of ONE object: PDF (vector/scanned drawing with ≥ 2 views, title block, technical data),
JPG/JPEG (photo), PNG (hand sketch). Priority when they disagree: dimensions written on the PDF >
dimensions written on the sketch > drawn proportions > sketch > photo (photo = appearance/details).

Read every PDF page both ways:
```bash
pdftoppm -r 110 -png drawing.pdf page        # look at page-1.png ... with the Read tool
pdftotext -layout drawing.pdf - 2>/dev/null  # dimension texts, title block, specification tables
```
Zoom into crowded areas (crop with PIL) before reading small dimension text. Measure undimensioned features
by pixel ratio against a known dimension in the same view (note: drawings are often not to scale).

## 1. Source analysis

Write down (show the user a short summary):
- object, category (`Casework | Furniture | Doors | Windows | Generic Models | Specialty Equipment`), units
  (metric drawings without unit marks = millimeters);
- every view per page: titles (План, Главный вид / Вид спереди, Вид слева/справа, Разрез А-А, Вид А, Узел, Plan,
  Front, Side, Section…). Projection: first-angle (ГОСТ/ISO: top view BELOW front, left view RIGHT of front),
  third-angle (ANSI: top ABOVE front, right view RIGHT of front). Unlabelled → match views by shared dims;
- axes: plan = X/Y, front = X/Z, side = Y/Z; front view looks from −Y; Z up; for vessels the vertical axis
  is x = y = 0 and z = 0 is the lowest point;
- all dimensions (value, what it measures, axis, view, source: dimension | scaled | assumed);
- overall size X × Y × Z;
- technical data: name/designation, model, manufacturer, mass, capacity/air flow, power, voltage, current,
  frequency, pressure, temperature, material, GOST/TU…;
- assumptions (missing dimensions you had to infer).

## 2. Consistency check → dialog (mandatory)

Look for contradictions:
- chain of dimensions ≠ overall dimension over the same span (e.g. 340 + 430 + 90 = 860 vs overall 850);
- the same feature dimensioned differently in two views; diameter in one view ≠ width in another;
- dimension contradicts the drawn geometry by > ~10 %;
- idealised shape contradicts a dimension (e.g. "inlet wall tangent to the body" vs "800 from the axis");
- technical data vs geometry (DN vs drawn diameter); photo/sketch vs drawing (extra/missing parts, counts).

For every conflict use **AskUserQuestion** (≤ 4 questions per call, 2–4 options each; recommended option
first with "(Recommended)"; the user can always type another value). Also ask about genuinely unknown
dimensions that strongly affect the shape (e.g. inlet duct length along Y) — offer a sensible default.
Do not ask about things the files clearly show. Record every decision and apply it consistently:
adjust the related chain dimensions so the model stays consistent with the chosen value.

## 3. Generate the JSON

Top level:
```json
{ "metadata": {"name": "...", "schema_version": "0.1", "category": "Specialty Equipment",
               "description": "... + assumptions + user decisions", "prompt": "views found + key dimensions"},
  "units": "millimeters",
  "subcategories": ["Body", "Inlet", "Outlet", "Supports"],
  "reference_planes": [ {"name": "Base Level", "direction": "z", "offset": 0, "is_reference": "Strong"} ],
  "parameters": [ ... ], "geometry": [ ... ], "constraints": [ ... ] }
```
`units`: inches | feet | millimeters | centimeters | meters — all numbers are in these units.

**Reference planes**: `{name, direction: x|y|z (axis normal to the plane), offset: expression, is_reference: Strong|Weak|Not a Reference}`.
One plane per level/feature line; build the body stack from shared planes (bottom flange → cone → cylinder → outlet).

**Constraints** (optional): `{description, type: alignment|lock|equality, geometry: <element name>, reference_plane: <plane name>}`.

**Parameters**: `{name, type, instance_or_type: instance|type, default_value, group, description}`
- Geometric types `length | angle | number | integer` (+ `yes_no`, `text`, `material`). Names used in
  expressions must be **Latin letters/digits/spaces only**; in expressions spaces become underscores
  (`Cyclone Diameter` → `Cyclone_Diameter`). Expressions: numbers, parameter names, `+ - * /`, parentheses only
  (no functions — compute sqrt/sin/acos yourself and write numbers or fractions of a parameter, e.g. `Cyclone_Diameter / 2 * 0.70710678`).
- Groups: `Dimensions | Construction | Identity Data | Graphics | Materials and Finishes | Mechanical | Electrical | Other`.
- **Technical data → parameters** (keep original, e.g. Russian, names; add `"source"`):
  | data | type | unit | group |
  |---|---|---|---|
  | name, marking, GOST, material | text | — | Identity Data |
  | Model / Manufacturer / Description / Type Comments / URL / Cost | text + `"builtin": "Model"` … | — | Identity Data |
  | mass | mass | kg | Construction |
  | air flow / capacity | air_flow | m3/h | Mechanical |
  | liquid flow | pipe_flow | m3/h or l/s | Mechanical |
  | pressure, resistance | pressure | Pa / kPa | Mechanical |
  | motor power | electrical_power | kW | Electrical |
  | heating/cooling capacity | hvac_power | kW | Mechanical |
  | voltage / current / frequency | voltage / current / frequency | V / A / Hz | Electrical |
  | temperature | temperature | °C | Mechanical |
  | anything else | number or text (unit in the name) | — | Other |

**Geometry** (`name` unique, `is_void`, `sketch_plane`, optional `subcategory`):
- `extrusion`: `profile` + `extrusion_start`, `extrusion_end` (along the plane normal, relative to the plane).
- `blend`: `bottom_profile`, `top_profile` (same shape type; same vertex count for custom),
  `bottom_offset`, `top_offset`. **Cones/frustums = blend of two coaxial circles** (the executor builds a true
  revolution). Avoid non-coaxial circle blends.
- `sweep`: `profile` + `path.segments` (`{segment_type: line, start, end}` / `{segment_type: arc, start, end, center}`) — only for simple trims; prefer extrusions.
- Profiles: `rectangle {origin{u,v}, width, height}` (width/height > 0), `circle {center{u,v}, radius}`,
  `l_shape {origin, overall_width, overall_height, horizontal_leg_thickness, vertical_leg_thickness}`,
  `custom {vertices:[{u,v}…]}` (closed, ≥ 3, not self-intersecting).
- (u, v) on a plane: direction x → (u=Y, v=Z); y → (u=X, v=Z); z → (u=X, v=Y). For x/y planes v is the absolute Z.
- Voids cut every solid they intersect — keep them local (e.g. bore void only through its flange).

## 4. Modelling rules (learned on real drawings)

- Use the written dimension values exactly; never round. Dimensions win over idealised shapes.
- **Zone check**: every element only where it is drawn (lugs at the bottom of a cylindrical shell go UP from
  the cylinder/cone joint, never down over the cone). Nothing on surfaces drawn clean.
- **Dimension ownership**: a dimension belongs to a feature only if its extension lines touch it; chain
  dimensions along a shell describe shell zones, not small attachments. Small undimensioned parts: measure
  by scale; lugs ≈ 150–250 mm high.
- A rectangular flange with bolt holes drawn **face-on in the front view** → the duct runs along Y; flange on −Y.
- **Cyclone / vessel pattern**: cylinder = circle extrusion; cone = coaxial circle blend; flanges 8–20 mm thick
  with a bore void; outlet pipe + flange (+ shallow bore void).
- **Tangential (volute) inlet** of a cyclone, body radius R, inlet width W, flange at y = −L, inlet on +x,
  flange outer edge at distance D from the axis (if dimensioned): custom profile in PLAN on the z-plane at the
  inlet bottom, extruded by the inlet height (top flush with the cylinder top unless drawn otherwise).
  x_o = D − (FlangeWidth − W)/2, x_i = x_o − W (without D: x_i = R).
  Inner wall: from (x_i, −L) straight to the tangent point on the body circle, angle α = atan2(−L, x_i) + acos(R/√(x_i²+L²)) (α = 0 when x_i = R).
  Outer wall: x = x_o from the flange to y₁ = R·√2 − x_o, then 45° along x + y = R·√2 to the tangent point (R/√2, R/√2).
  Close along the body circle with intermediate points at 30° and 15° (only those greater than α), ending at α. Vertices:
  (x_i,−L) → (x_o,−L) → (x_o,y₁) → (0.7071R,0.7071R) → (0.8660R,0.5R) → (0.9659R,0.2588R) → (R·cos α, R·sin α).
  Flange: rectangle on the y-plane at −L, outer edge at D, extrusion −thickness..0, plus a shallow opening void.
  Mirror signs for an inlet on −x or the opposite swirl. If L is not on the drawing, ask (default L ≈ R).
- **Support lugs (опоры-лапы)**: 4 at 0/90/180/270°: base plate (10–16 mm, top at the cylinder/cone joint,
  projecting 80–120 mm) + two triangular gussets (8–10 mm, ~200 mm high, 100–150 mm apart) as custom triangles on
  vertical planes through each gusset: vertical edge on the shell (use R − 5 so it touches the curved shell),
  horizontal edge along the plate, sloped edge between them.
- Geometric parameter defaults must reproduce the drawing exactly; reuse one parameter for repeated sizes.

## 5. Check & preview (mandatory, iterate until PASS)

Write the script below to `rfagen_check.py` (once per session) and run:
```bash
pdftoppm -r 40 -png drawing.pdf src          # small source image for side-by-side
python3 rfagen_check.py family.json --expect X=…,Y=…,Z=… --drawing src-1.png --out family-preview.png
```
It validates structure/enums/expressions, evaluates every element's bounding box, compares the overall size
with `--expect` (use the analysed/decided overall dimensions) and renders 3D + plan + front + side + source.
Then **look at the preview** (Read the PNG) and compare view by view with the drawing:
silhouette and proportions per view, positions/heights of each feature, counts (lugs, holes), orientation of
the inlet, nothing floating/overlapping, chain dimensions reproduced (read element Z ranges from the report).
Fix the JSON and rerun until RESULT: PASS and the views match. Optionally also validate against the add-in's
schema (`C:\Portable\AI\BeGenSt\BeGenSt\EmbeddedContent\family-schema.json`) with `jsonschema` if available
(`metadata.name` and `verification` are allowed by the BeGenSt schema).

## 6. Deliver

- Save `<Object>.json` (UTF-8, indent 2) and `<Object>-preview.png`; if a project folder is connected, write
  them there (e.g. `C:\Portable\AI\BeGenSt\TestDrawings\` or next to the PDF) and also show the preview.
- Report briefly: views found, overall size, user decisions, assumptions, technical-data parameters, check result.
- Tell the user: Revit → family template (e.g. Metric Generic Model) → BeGenSt → Generate → **Load JSON…** →
  check the 3D preview → **RFAGen**. The add-in writes a generation report to `%AppData%\BeGenSt\RFAGen\Logs`;
  if an element is missing in Revit, ask for that report and fix the JSON.

## 7. Catalog families with several types (TypeGen, plugin ≥ 1.4.4)

When the source is a catalog sheet with a **table of type sizes** (DN × PN, D1, D2, D3, L, n, d, H, mass …) build ONE
catalog JSON — loaded in Revit via **TypeGen → Load catalog JSON… → RFAGen**. One parametric template + the table;
the add-in builds one geometry set per distinct row (visibility by type), one Revit type per row.

**Template**
* Every size column is a parameter (`instance_or_type: "type"`, exact table names, Latin), ALL geometry dimensions
  are expressions of them (only `+ - * /`, no functions). Proportions not given by the table are expressions too
  (`0.36*D3`, `0.55*H`) — check them on the smallest AND the largest row: no zero/negative lengths, no collisions
  (body top < yoke < actuator bottom). Integer columns (DN, n) may stay `integer` — with the lookup table the add-in
  makes them numbers.
* Axes for pipe accessories: pipe axis = X through the origin, raised-face end faces exactly at ±L/2, Z up to the
  actuator top = H. Planes: `Left Face` (x, −L/2), `Right Face` (x, L/2), `Pipe Axis` (z, 0), `Motor Plane` (y, 0).
* Flange bolt holes: `"holes": [{"radius": "d/2", "pattern": {"count": "n", "radius": "D2/2", "start_angle": "180/n"}}]`
  on the flange extrusion (D2/2 + d/2 < D3/2); a ring = extrusion with one central hole (`"center": {u, v}`);
  bore = ONE void extrusion `DN/2` through `L` (voids have no Visible parameter; the add-in hides them in the views).
* Detail (25–35 elements, e.g. a globe / control valve): raised faces + flanges; conical necks (`blend` circle→circle,
  bottom_offset < top_offset, same centre); globe body = two cones on `Pipe Axis` (lower to 0, upper from 0); body and
  bonnet flanges; conical bonnet; studs; gland; yoke = two pillars + top plate; stem; coupling.
  Repeated parts = ONE element with an element-level polar array
  `"pattern": {"count": 8, "radius": "0.265*D3", "start_angle": 22.5}` (copies it around the plane origin / `center`).
* Actuator (AUMA-like): flange, gearbox box, motor cylinder sideways + fan cover, handwheel ring + shaft on the other
  side, control box, position indicator — all with `"toggle": "actuator"`, the top exactly at `H`.
  Subcategories `Body`, `Actuator`. Do NOT model pipe connectors.
* Non-geometric data (mass, actuator model, ΔP, notes, materials, name, manufacturer) = parameters (text in the
  catalog language; `builtin` for Model / Manufacturer / URL).

**`"catalog"` section**
* `type_name_pattern` (`{Префикс} {Обозначение} DN{DN} PN{PN}`), `constants` (`Префикс`, `Обозначение`), `columns`
  (name/type/unit), `types` (one object per row; merged PN cells expanded into separate rows; ΔP / actuator /
  notes joined by DN), `figures`, `source`.
* `toggles`: `[{"id": "actuator", "parameter": "Показать_привод", "default": true}]` (instance yes/no).
* `lookup`: `{"enabled": true, "keys": ["DN", "PN"]}` — embedded lookup table `BGS_Catalog`, numeric type
  parameters by `size_lookup` (kept as plain type values when Revit refuses a formula; the report says why).
* `connectors`: `{"enabled": true, "type": "pipe", "system": "Global", "length_parameter": "L",
  "diameter_parameter": "DN", "axis": "x"}` — two pipe connectors at ±L/2, Ø = DN, category Generic Model →
  Pipe Accessories, part type Valve – Breaks Into.

**Check** — `python3 rfagen_check.py catalog.json --type N` checks every row (overall X = L, top Z = H, holes inside
the flange, pattern counts) and renders row N; iterate until `CATALOG RESULT: PASS`.

**Tell the user (Revit side)**
* Best: a NEW family (Metric Generic Model). Generating into an existing family: first **Purge → Full clean before
  generation** (or accept TypeGen's offer) — it removes the old geometry, nested families (e.g. an actuator),
  dimensions, own reference planes, lookup tables and size_lookup formulas; parameters are kept and reused
  (instance / reporting / formula-driven columns are turned into plain type parameters).
* Generation of ~40 types × 35 elements takes long (tens of minutes). The report
  `%AppData%\BeGenSt\RFAGen\Logs\…-types-generation-report.txt` lists every element, lookup/connector results and
  Revit errors with element names — ask for it when something is wrong.
* Example: `TestDrawings/Avangard_KZRr_25s947nzh_catalog.json` (КЗРр 25с947нж, 40 types, 23 geometry sets, 27 elements).

## rfagen_check.py

```python
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
```
