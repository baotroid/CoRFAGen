using System.Data;
using System.Globalization;
using System.Text;

namespace BeGenSt.Data
{
    /// <summary>
    /// Revit-independent check of a family JSON: evaluates every expression with the parameter
    /// defaults, computes the bounding box of each geometry element and of the whole family, and
    /// reports structural problems. The report is used to verify the model against the source
    /// dimensions before it is shown in the preview.
    /// </summary>
    public class GeometryInspector
    {
        public class ElementBox
        {
            public string Name;
            public string Type;
            public bool IsVoid;
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        }

        public List<ElementBox> Boxes { get; } = new List<ElementBox>();
        public List<string> Issues { get; } = new List<string>();
        public string Units { get; private set; } = "";
        public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;

        public double SizeX => MaxX - MinX;
        public double SizeY => MaxY - MinY;
        public double SizeZ => MaxZ - MinZ;

        private readonly Dictionary<string, double> _params = new Dictionary<string, double>();
        private readonly Dictionary<string, (string dir, double off)> _planes =
            new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);

        public static GeometryInspector Inspect(string familyJson, SourceAnalysis analysis = null)
        {
            var gi = new GeometryInspector();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(familyJson))
                    gi.Run(doc.RootElement);
            }
            catch (Exception ex)
            {
                gi.Issues.Add($"JSON could not be inspected: {ex.Message}");
            }
            if (analysis != null) gi.CompareWithAnalysis(analysis);
            return gi;
        }

        private void Run(JsonElement root)
        {
            Units = root.TryGetProperty("units", out JsonElement u) ? u.GetString() : "";

            if (root.TryGetProperty("parameters", out JsonElement ps))
            {
                foreach (JsonElement p in ps.EnumerateArray())
                {
                    string type = p.TryGetProperty("type", out JsonElement t) ? t.GetString() : "";
                    if ((type == "length" || type == "number" || type == "angle" || type == "integer") &&
                        p.TryGetProperty("default_value", out JsonElement dv) &&
                        TechnicalParameters.TryGetNumber(dv, out double d))
                        _params[p.GetProperty("name").GetString().Replace(" ", "_")] = d;
                }
            }

            foreach (JsonElement rp in root.GetProperty("reference_planes").EnumerateArray())
            {
                string name = rp.GetProperty("name").GetString();
                _planes[name] = (rp.GetProperty("direction").GetString(), Eval(rp.GetProperty("offset"), $"plane '{name}'"));
            }

            foreach (JsonElement g in root.GetProperty("geometry").EnumerateArray())
            {
                string name = g.TryGetProperty("name", out JsonElement nEl) ? nEl.GetString() : "?";
                try { InspectElement(g, name); }
                catch (Exception ex) { Issues.Add($"'{name}': cannot be evaluated ({ex.Message})"); }
            }

            if (Boxes.Count == 0) Issues.Add("No geometry could be evaluated.");
        }

        private void InspectElement(JsonElement g, string name)
        {
            string type = g.GetProperty("type").GetString();
            bool isVoid = g.TryGetProperty("is_void", out JsonElement iv) && iv.ValueKind == JsonValueKind.True;
            string planeName = g.GetProperty("sketch_plane").GetString();
            if (!_planes.TryGetValue(planeName, out var plane))
            {
                if (planeName.IndexOf("Left/Right", StringComparison.OrdinalIgnoreCase) >= 0) plane = ("x", 0);
                else if (planeName.IndexOf("Front/Back", StringComparison.OrdinalIgnoreCase) >= 0) plane = ("y", 0);
                else if (planeName.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0) plane = ("z", 0);
                else { Issues.Add($"'{name}': sketch plane '{planeName}' is not defined"); return; }
            }

            double uMin, uMax, vMin, vMax, wMin, wMax;
            if (type == "extrusion")
            {
                ProfileBox(g.GetProperty("profile"), name, out uMin, out uMax, out vMin, out vMax);
                double s = Eval(g.GetProperty("extrusion_start"), name), e = Eval(g.GetProperty("extrusion_end"), name);
                if (Math.Abs(e - s) < 1e-6) Issues.Add($"'{name}': zero extrusion depth");
                wMin = plane.off + Math.Min(s, e); wMax = plane.off + Math.Max(s, e);
            }
            else if (type == "blend")
            {
                ProfileBox(g.GetProperty("bottom_profile"), name, out double bu0, out double bu1, out double bv0, out double bv1);
                ProfileBox(g.GetProperty("top_profile"), name, out double tu0, out double tu1, out double tv0, out double tv1);
                uMin = Math.Min(bu0, tu0); uMax = Math.Max(bu1, tu1); vMin = Math.Min(bv0, tv0); vMax = Math.Max(bv1, tv1);
                double b = Eval(g.GetProperty("bottom_offset"), name), t = Eval(g.GetProperty("top_offset"), name);
                if (Math.Abs(t - b) < 1e-6) Issues.Add($"'{name}': blend has zero height");
                wMin = plane.off + Math.Min(b, t); wMax = plane.off + Math.Max(b, t);
            }
            else if (type == "sweep")
            {
                ProfileBox(g.GetProperty("profile"), name, out double pu0, out double pu1, out double pv0, out double pv1);
                double margin = Math.Max(pu1 - pu0, pv1 - pv0);
                uMin = vMin = double.MaxValue; uMax = vMax = double.MinValue;
                foreach (JsonElement seg in g.GetProperty("path").GetProperty("segments").EnumerateArray())
                {
                    foreach (string key in new[] { "start", "end", "center" })
                    {
                        if (!seg.TryGetProperty(key, out JsonElement pt)) continue;
                        double pu = Eval(pt.GetProperty("u"), name), pv = Eval(pt.GetProperty("v"), name);
                        uMin = Math.Min(uMin, pu); uMax = Math.Max(uMax, pu); vMin = Math.Min(vMin, pv); vMax = Math.Max(vMax, pv);
                    }
                }
                uMin -= margin / 2; uMax += margin / 2; vMin -= margin / 2; vMax += margin / 2;
                wMin = plane.off - margin / 2; wMax = plane.off + margin / 2;
            }
            else
            {
                Issues.Add($"'{name}': unsupported geometry type '{type}'");
                return;
            }

            if (uMax - uMin < 1e-6 || vMax - vMin < 1e-6) Issues.Add($"'{name}': profile has zero size");

            var box = new ElementBox { Name = name, Type = type, IsVoid = isVoid };
            switch (plane.dir)
            {
                case "x": Set(box, wMin, wMax, uMin, uMax, vMin, vMax); break;
                case "y": Set(box, uMin, uMax, wMin, wMax, vMin, vMax); break;
                default: Set(box, uMin, uMax, vMin, vMax, wMin, wMax); break;
            }
            Boxes.Add(box);
            if (!isVoid)
            {
                MinX = Math.Min(MinX, box.MinX); MaxX = Math.Max(MaxX, box.MaxX);
                MinY = Math.Min(MinY, box.MinY); MaxY = Math.Max(MaxY, box.MaxY);
                MinZ = Math.Min(MinZ, box.MinZ); MaxZ = Math.Max(MaxZ, box.MaxZ);
            }
        }

        private static void Set(ElementBox b, double x0, double x1, double y0, double y1, double z0, double z1)
        {
            b.MinX = x0; b.MaxX = x1; b.MinY = y0; b.MaxY = y1; b.MinZ = z0; b.MaxZ = z1;
        }

        private void ProfileBox(JsonElement p, string name, out double u0, out double u1, out double v0, out double v1)
        {
            string shape = p.GetProperty("shape").GetString();
            switch (shape)
            {
                case "circle":
                {
                    double cu = Eval(p.GetProperty("center").GetProperty("u"), name);
                    double cv = Eval(p.GetProperty("center").GetProperty("v"), name);
                    double r = Eval(p.GetProperty("radius"), name);
                    if (r <= 0) Issues.Add($"'{name}': circle radius is {r}");
                    u0 = cu - r; u1 = cu + r; v0 = cv - r; v1 = cv + r;
                    return;
                }
                case "rectangle":
                {
                    double ou = Eval(p.GetProperty("origin").GetProperty("u"), name);
                    double ov = Eval(p.GetProperty("origin").GetProperty("v"), name);
                    double w = Eval(p.GetProperty("width"), name), h = Eval(p.GetProperty("height"), name);
                    if (w <= 0 || h <= 0) Issues.Add($"'{name}': rectangle width/height must be positive ({w} x {h})");
                    u0 = Math.Min(ou, ou + w); u1 = Math.Max(ou, ou + w); v0 = Math.Min(ov, ov + h); v1 = Math.Max(ov, ov + h);
                    return;
                }
                case "l_shape":
                {
                    double ou = Eval(p.GetProperty("origin").GetProperty("u"), name);
                    double ov = Eval(p.GetProperty("origin").GetProperty("v"), name);
                    double w = Eval(p.GetProperty("overall_width"), name), h = Eval(p.GetProperty("overall_height"), name);
                    u0 = ou; u1 = ou + w; v0 = ov; v1 = ov + h;
                    return;
                }
                default:
                {
                    var pts = new List<(double u, double v)>();
                    foreach (JsonElement vt in p.GetProperty("vertices").EnumerateArray())
                        pts.Add((Eval(vt.GetProperty("u"), name), Eval(vt.GetProperty("v"), name)));
                    if (pts.Count < 3) Issues.Add($"'{name}': custom profile needs at least 3 vertices");
                    if (SelfIntersects(pts)) Issues.Add($"'{name}': custom profile is self-intersecting");
                    u0 = pts.Min(q => q.u); u1 = pts.Max(q => q.u); v0 = pts.Min(q => q.v); v1 = pts.Max(q => q.v);
                    return;
                }
            }
        }

        private static bool SelfIntersects(List<(double u, double v)> p)
        {
            int n = p.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;
                    if (SegmentsCross(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) return true;
                }
            return false;
        }

        private static bool SegmentsCross((double u, double v) a, (double u, double v) b, (double u, double v) c, (double u, double v) d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) &&
                   ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
        }

        private static double Cross((double u, double v) o, (double u, double v) a, (double u, double v) b) =>
            (a.u - o.u) * (b.v - o.v) - (a.v - o.v) * (b.u - o.u);

        private double Eval(JsonElement e, string context)
        {
            if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
            string expr = e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();
            if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out double lit)) return lit;

            string evaluated = expr;
            foreach (var kv in _params.OrderByDescending(k => k.Key.Length))
                evaluated = System.Text.RegularExpressions.Regex.Replace(evaluated,
                    @"(?<![\p{L}\p{N}_.])" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + @"(?![\p{L}\p{N}_])",
                    "(" + kv.Value.ToString("R", CultureInfo.InvariantCulture) + ")");
            try
            {
                var dt = new DataTable { Locale = CultureInfo.InvariantCulture };
                return Convert.ToDouble(dt.Compute(evaluated, ""), CultureInfo.InvariantCulture);
            }
            catch
            {
                Issues.Add($"{context}: expression '{expr}' cannot be evaluated (unknown parameter?)");
                return 0;
            }
        }

        private void CompareWithAnalysis(SourceAnalysis a)
        {
            if (Boxes.Count == 0) return;
            Check("overall width (X)", SizeX, a.OverallWidthX);
            Check("overall depth (Y)", SizeY, a.OverallDepthY);
            Check("overall height (Z)", SizeZ, a.OverallHeightZ);
        }

        private void Check(string label, double actual, double? expected)
        {
            if (!expected.HasValue || expected.Value <= 0) return;
            double diff = Math.Abs(actual - expected.Value);
            if (diff > Math.Max(2.0, expected.Value * 0.01))
                Issues.Add($"{label} of the model is {actual:0.#} {Units}, but the source analysis says {expected.Value:0.#} {Units}");
        }

        public string Report()
        {
            var sb = new StringBuilder();
            if (Boxes.Count > 0)
                sb.AppendLine($"Overall solid extents ({Units}): X {MinX:0.#}..{MaxX:0.#} (= {SizeX:0.#}), " +
                              $"Y {MinY:0.#}..{MaxY:0.#} (= {SizeY:0.#}), Z {MinZ:0.#}..{MaxZ:0.#} (= {SizeZ:0.#})");
            sb.AppendLine("Element bounding boxes (min..max):");
            foreach (ElementBox b in Boxes)
                sb.AppendLine($"- {b.Name} [{b.Type}{(b.IsVoid ? ", void" : "")}]: X {b.MinX:0.#}..{b.MaxX:0.#}, " +
                              $"Y {b.MinY:0.#}..{b.MaxY:0.#}, Z {b.MinZ:0.#}..{b.MaxZ:0.#}");
            if (Issues.Count > 0)
            {
                sb.AppendLine("Automatic check found:");
                foreach (string i in Issues) sb.AppendLine("! " + i);
            }
            else sb.AppendLine("Automatic check: no structural problems found.");
            return sb.ToString();
        }
    }
}
