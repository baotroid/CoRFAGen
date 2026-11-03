using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ACadSharp;
using ACadSharp.IO;
using Cad = ACadSharp.Entities;

namespace CoRFAGen.Data
{
    /// <summary>What a DWG/DXF drawing becomes for the AI model: one sheet image plus exact data as text.</summary>
    public class CadSheet
    {
        public string Summary { get; set; }
        public byte[] Png { get; set; }
        public int WidthPx { get; set; }
        public int HeightPx { get; set; }
        public int DimensionCount { get; set; }
        public int TextCount { get; set; }
        public int RegionCount { get; set; }
        public int OutsideDimensions { get; set; }
        public int MismatchCount { get; set; }
        public int UnreadObjects { get; set; }
        public bool SheetFrameFound { get; set; }
        /// <summary>All lines on the sheet (kind,layer,x1,y1,x2,y2), curves as short segments. For tools; not sent to the model.</summary>
        public string GeometryCsv { get; set; }
    }

    /// <summary>
    /// Reads a DWG or DXF drawing with ACadSharp and prepares it for the model: finds the sheet (stray objects
    /// outside it are ignored), expands blocks, decodes texts, takes each dimension's displayed label together with
    /// its measured length, splits the sheet into regions (views) and renders the sheet to PNG.
    /// The sheet image goes to every provider as a normal image; Summary is added to the prompt.
    /// </summary>
    public static class CadDrawingReader
    {
        public const int SheetLongSidePx = 3600;
        public const int MaxSummaryChars = 45000;
        private const int MaxBlockDepth = 8;
        private const double ScaleTolerance = 0.02;
        private const double LabelToleranceMm = 1.5;

        public static readonly string[] Extensions = { ".dwg", ".dxf" };

        public static bool IsCadFile(string path) =>
            Extensions.Contains(Path.GetExtension(path)?.ToLowerInvariant());

        // ------------------------------------------------------------------ flattened drawing

        private class Seg { public double X1, Y1, X2, Y2; public bool IsDim; public string Layer; public double Len => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)); }
        private class Txt { public string Value; public double X, Y, Height, Rotation; public bool IsDim; }
        private class Dim { public string Label, Kind, Direction = "points"; public double Measured, X, Y, P1X, P1Y, P2X, P2Y; public bool HasPoints; public double? Number; public int Region = -1; }
        private class Round { public double X, Y, R; public bool IsCircle; }
        private class Rect2 { public double X0, Y0, X1, Y1; public double W => X1 - X0; public double H => Y1 - Y0; public bool Has(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1; }

        private class Drawing
        {
            public readonly List<Seg> Segs = new List<Seg>();
            public readonly List<Txt> Texts = new List<Txt>();
            public readonly List<Dim> Dims = new List<Dim>();
            public readonly List<Round> Rounds = new List<Round>();
            public readonly List<Rect2> Rects = new List<Rect2>();
        }

        /// <summary>2D affine transform: x' = A·x + C·y + E, y' = B·x + D·y + F.</summary>
        private struct Xf
        {
            public double A, B, C, D, E, F;
            public static readonly Xf Identity = new Xf { A = 1, D = 1 };
            public void Apply(double x, double y, out double ox, out double oy) { ox = A * x + C * y + E; oy = B * x + D * y + F; }
            public Xf Then(Xf n) => new Xf
            {
                A = n.A * A + n.C * B, B = n.B * A + n.D * B,
                C = n.A * C + n.C * D, D = n.B * C + n.D * D,
                E = n.A * E + n.C * F + n.E, F = n.B * E + n.D * F + n.F
            };
            public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
            public double Angle => Math.Atan2(B, A);
            public bool Uniform => Math.Abs(A * A + B * B - (C * C + D * D)) < 1e-6 * (A * A + B * B + 1e-12);
        }

        // ------------------------------------------------------------------ entry point

        public static CadSheet Read(string path)
        {
            string name = Path.GetFileName(path);
            var unread = new Dictionary<string, int>();
            CadDocument doc;
            NotificationEventHandler onNote = (s, e) =>
            {
                Match m = Regex.Match(e.Message ?? "", @"DXF name (\S+) has been read as an UnknownEntity");
                if (!m.Success) return;
                unread.TryGetValue(m.Groups[1].Value, out int n);
                unread[m.Groups[1].Value] = n + 1;
            };
            try
            {
                if (string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase))
                    using (var r = new DxfReader(path, onNote)) doc = r.Read();
                else
                    using (var r = new DwgReader(path, onNote)) doc = r.Read();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not read the drawing '{name}': {ex.Message}\n" +
                    "Save it as an AutoCAD 2000 or later DWG/DXF, or export it to PDF.", ex);
            }

            Drawing all = FlattenBestSpace(doc);
            if (all.Segs.Count(s => !s.IsDim) == 0 && all.Texts.Count == 0 && all.Dims.Count == 0)
                throw new InvalidOperationException($"The drawing '{name}' has no readable 2D content (only 3D solids or external references?). Export it to PDF.");

            Rect2 sheet = FindSheet(all, out bool frameFound);
            Drawing d = CropToSheet(all, sheet, out int outDims, out int outOther);
            List<Rect2> regions = BuildRegions(d, sheet);
            AssignDimensions(d, regions);
            int total = unread.Values.Sum();

            var result = new CadSheet
            {
                DimensionCount = d.Dims.Count,
                TextCount = d.Texts.Count,
                RegionCount = regions.Count,
                OutsideDimensions = outDims,
                UnreadObjects = total,
                SheetFrameFound = frameFound
            };
            result.Summary = BuildSummary(name, doc, d, sheet, frameFound, regions, outDims, outOther, unread, result);
            result.GeometryCsv = BuildGeometryCsv(d);
            Render(d, sheet, result);
            return result;
        }

        // ------------------------------------------------------------------ flatten

        private static Drawing FlattenBestSpace(CadDocument doc)
        {
            var model = new Drawing();
            FlattenAll(doc.ModelSpace.Entities, Xf.Identity, model, 0, false);
            if (model.Segs.Count(s => !s.IsDim) >= 20) return model;

            // Almost nothing in the model: the drawing may live on a sheet layout.
            Drawing best = model;
            foreach (ACadSharp.Objects.Layout layout in doc.Layouts)
            {
                var block = layout.AssociatedBlock;
                if (block == null || block.Name == doc.ModelSpace.Name) continue;
                var candidate = new Drawing();
                FlattenAll(block.Entities, Xf.Identity, candidate, 0, false);
                if (candidate.Segs.Count > best.Segs.Count) best = candidate;
            }
            return best;
        }

        private static void FlattenAll(IEnumerable<Cad.Entity> entities, Xf xf, Drawing d, int depth, bool isDim)
        {
            foreach (Cad.Entity e in entities)
            {
                try { Flatten(e, xf, d, depth, isDim); }
                catch { /* one odd entity must not lose the whole drawing */ }
            }
        }

        private static void Flatten(Cad.Entity e, Xf xf, Drawing d, int depth, bool isDim)
        {
            string layer = e.Layer?.Name;
            switch (e)
            {
                case Cad.Line l:
                    AddSeg(d, xf, l.StartPoint.X, l.StartPoint.Y, l.EndPoint.X, l.EndPoint.Y, isDim, layer);
                    break;
                case Cad.Arc a:
                    AddCurve(d, xf, a.PolygonalVertexes(CurveSteps(a.EndAngle - a.StartAngle)).Select(p => (Ocs(p.X, a.Normal.Z), p.Y)).ToList(), false, isDim, layer);
                    if (!isDim) AddRound(d, xf, Ocs(a.Center.X, a.Normal.Z), a.Center.Y, a.Radius, false);
                    break;
                case Cad.Circle c:
                    AddCurve(d, xf, c.PolygonalVertexes(48).Select(p => (Ocs(p.X, c.Normal.Z), p.Y)).ToList(), true, isDim, layer);
                    if (!isDim) AddRound(d, xf, Ocs(c.Center.X, c.Normal.Z), c.Center.Y, c.Radius, true);
                    break;
                case Cad.Ellipse el:
                    AddCurve(d, xf, el.PolygonalVertexes(48).Select(p => (Ocs(p.X, el.Normal.Z), p.Y)).ToList(), false, isDim, layer);
                    break;
                case Cad.Spline sp:
                    AddCurve(d, xf, SplinePoints(sp), sp.IsClosed, isDim, layer);
                    break;
                case Cad.LwPolyline lw:
                    AddPolyline(d, xf, lw.Vertices.Select(v => (Ocs(v.Location.X, lw.Normal.Z), v.Location.Y, lw.Normal.Z < 0 ? -v.Bulge : v.Bulge)).ToList(), lw.IsClosed, isDim, layer);
                    break;
                case Cad.Polyline2D p2:
                    AddPolyline(d, xf, p2.Vertices.Select(v => (Ocs(v.Location.X, p2.Normal.Z), v.Location.Y, p2.Normal.Z < 0 ? -v.Bulge : v.Bulge)).ToList(), p2.IsClosed, isDim, layer);
                    break;
                case Cad.Polyline3D p3:
                    AddPolyline(d, xf, p3.Vertices.Select(v => (v.Location.X, v.Location.Y, 0.0)).ToList(), p3.IsClosed, isDim, layer);
                    break;
                case Cad.Solid so:
                    if (isDim) break;
                    AddCurve(d, xf, new List<(double, double)> { (so.FirstCorner.X, so.FirstCorner.Y), (so.SecondCorner.X, so.SecondCorner.Y), (so.FourthCorner.X, so.FourthCorner.Y), (so.ThirdCorner.X, so.ThirdCorner.Y) }, true, false, layer);
                    break;
                case Cad.AttributeEntity _:
                    break;
                case Cad.TextEntity t:
                    AddText(d, xf, t.Value, t.InsertPoint.X, t.InsertPoint.Y, t.Height, t.Rotation, isDim);
                    break;
                case Cad.MText m:
                    AddText(d, xf, m.Value, m.InsertPoint.X, m.InsertPoint.Y, m.Height,
                        m.AlignmentPoint.X == 0 && m.AlignmentPoint.Y == 0 ? m.Rotation : Math.Atan2(m.AlignmentPoint.Y, m.AlignmentPoint.X), isDim);
                    break;
                case Cad.Dimension dim:
                    AddDimension(dim, xf, d, depth);
                    break;
                case Cad.Insert ins:
                    if (depth >= MaxBlockDepth || ins.Block == null) break;
                    FlattenAll(ins.Block.Entities, InsertTransform(ins).Then(xf), d, depth + 1, isDim);
                    if (ins.Attributes != null)
                        foreach (Cad.AttributeEntity at in ins.Attributes)
                            AddText(d, xf, at.Value, at.InsertPoint.X, at.InsertPoint.Y, at.Height, at.Rotation, isDim);
                    break;
            }
        }

        private static double Ocs(double x, double normalZ) => normalZ < 0 ? -x : x;

        private static int CurveSteps(double sweep)
        {
            if (sweep < 0) sweep += 2 * Math.PI;
            return Math.Max(4, (int)Math.Ceiling(sweep / (Math.PI / 24)));
        }

        private static Xf InsertTransform(Cad.Insert ins)
        {
            var bp = ins.Block.BlockEntity.BasePoint;
            double cos = Math.Cos(ins.Rotation), sin = Math.Sin(ins.Rotation);
            var m = new Xf { A = ins.XScale * cos, B = ins.XScale * sin, C = -ins.YScale * sin, D = ins.YScale * cos, E = ins.InsertPoint.X, F = ins.InsertPoint.Y };
            m.E -= m.A * bp.X + m.C * bp.Y;
            m.F -= m.B * bp.X + m.D * bp.Y;
            if (ins.Normal.Z < 0) { m.A = -m.A; m.C = -m.C; m.E = -m.E; }   // mirrored insert: OCS x points the other way
            return m;
        }

        private static List<(double, double)> SplinePoints(Cad.Spline sp)
        {
            try
            {
                List<CSMath.XYZ> pts = sp.PolygonalVertexes(Math.Max(16, sp.ControlPoints.Count * 8));
                if (pts != null && pts.Count > 1) return pts.Select(p => (p.X, p.Y)).ToList();
            }
            catch { }
            var src = sp.FitPoints != null && sp.FitPoints.Count > 1 ? sp.FitPoints : sp.ControlPoints;
            return src.Select(p => (p.X, p.Y)).ToList();
        }

        private static void AddSeg(Drawing d, Xf xf, double x1, double y1, double x2, double y2, bool isDim, string layer)
        {
            xf.Apply(x1, y1, out double ax, out double ay);
            xf.Apply(x2, y2, out double bx, out double by);
            if (!Finite(ax) || !Finite(ay) || !Finite(bx) || !Finite(by)) return;
            d.Segs.Add(new Seg { X1 = ax, Y1 = ay, X2 = bx, Y2 = by, IsDim = isDim, Layer = layer });
        }

        private static void AddCurve(Drawing d, Xf xf, List<(double, double)> pts, bool closed, bool isDim, string layer)
        {
            for (int i = 0; i + 1 < pts.Count; i++)
                AddSeg(d, xf, pts[i].Item1, pts[i].Item2, pts[i + 1].Item1, pts[i + 1].Item2, isDim, layer);
            if (closed && pts.Count > 2)
                AddSeg(d, xf, pts[pts.Count - 1].Item1, pts[pts.Count - 1].Item2, pts[0].Item1, pts[0].Item2, isDim, layer);
        }

        private static void AddPolyline(Drawing d, Xf xf, List<(double X, double Y, double Bulge)> v, bool closed, bool isDim, string layer)
        {
            if (v.Count < 2) return;
            if (closed && !isDim && v.Count >= 4 && v.Count <= 5 && v.All(p => Math.Abs(p.Bulge) < 1e-9)) AddRect(d, xf, v);
            int n = closed ? v.Count : v.Count - 1;
            for (int i = 0; i < n; i++)
            {
                var a = v[i]; var b = v[(i + 1) % v.Count];
                if (Math.Abs(a.Bulge) < 1e-9) { AddSeg(d, xf, a.X, a.Y, b.X, b.Y, isDim, layer); continue; }
                AddCurve(d, xf, BulgePoints(a.X, a.Y, b.X, b.Y, a.Bulge), false, isDim, layer);
            }
        }

        private static List<(double, double)> BulgePoints(double x1, double y1, double x2, double y2, double bulge)
        {
            double sweep = 4 * Math.Atan(bulge), dx = x2 - x1, dy = y2 - y1, chord = Math.Sqrt(dx * dx + dy * dy);
            var pts = new List<(double, double)> { (x1, y1) };
            if (chord < 1e-9) { pts.Add((x2, y2)); return pts; }
            double r = chord / (2 * Math.Sin(Math.Abs(sweep) / 2));
            double h = r * Math.Cos(sweep / 2) * Math.Sign(bulge);
            double cx = (x1 + x2) / 2 - h * dy / chord, cy = (y1 + y2) / 2 + h * dx / chord;
            double a0 = Math.Atan2(y1 - cy, x1 - cx);
            int steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 24)));
            for (int i = 1; i < steps; i++)
            {
                double t = a0 + sweep * i / steps;
                pts.Add((cx + r * Math.Cos(t), cy + r * Math.Sin(t)));
            }
            pts.Add((x2, y2));
            return pts;
        }

        private static void AddRect(Drawing d, Xf xf, List<(double X, double Y, double Bulge)> v)
        {
            var pts = v.Select(p => { xf.Apply(p.X, p.Y, out double x, out double y); return (x, y); }).ToList();
            double x0 = pts.Min(p => p.x), x1 = pts.Max(p => p.x), y0 = pts.Min(p => p.y), y1 = pts.Max(p => p.y);
            double tol = 1e-3 * Math.Max(x1 - x0, y1 - y0);
            bool onBox = pts.All(p => (Math.Abs(p.x - x0) < tol || Math.Abs(p.x - x1) < tol) && (Math.Abs(p.y - y0) < tol || Math.Abs(p.y - y1) < tol));
            if (onBox && x1 > x0 && y1 > y0) d.Rects.Add(new Rect2 { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 });
        }

        private static void AddRound(Drawing d, Xf xf, double x, double y, double r, bool isCircle)
        {
            xf.Apply(x, y, out double ox, out double oy);
            if (xf.Uniform) d.Rounds.Add(new Round { X = ox, Y = oy, R = r * xf.Scale, IsCircle = isCircle });
        }

        private static void AddText(Drawing d, Xf xf, string raw, double x, double y, double height, double rot, bool isDim)
        {
            string v = CleanText(raw);
            if (string.IsNullOrWhiteSpace(v)) return;
            xf.Apply(x, y, out double ox, out double oy);
            if (!Finite(ox) || !Finite(oy)) return;
            d.Texts.Add(new Txt { Value = v, X = ox, Y = oy, Height = height * xf.Scale, Rotation = rot + xf.Angle, IsDim = isDim });
        }

        private static void AddDimension(Cad.Dimension dim, Xf xf, Drawing d, int depth)
        {
            string shown = null;
            double mx = dim.TextMiddlePoint.X, my = dim.TextMiddlePoint.Y, sx = mx, sy = my;
            if (dim.Block != null)
                foreach (Cad.Entity e in dim.Block.Entities)
                {
                    if (e is Cad.MText m) { shown = m.Value; sx = m.InsertPoint.X; sy = m.InsertPoint.Y; break; }
                    if (e is Cad.TextEntity t) { shown = t.Value; sx = t.InsertPoint.X; sy = t.InsertPoint.Y; break; }
                }
            if (mx == 0 && my == 0) { mx = sx; my = sy; }
            double tx = mx, ty = my;
            if (dim.Block != null && depth < MaxBlockDepth)
            {
                // The drawn block of a dimension nested in another block has its own offset: align it by the label.
                Xf drawn = depth > 0 ? new Xf { A = 1, D = 1, E = mx - sx, F = my - sy }.Then(xf) : xf;
                FlattenAll(dim.Block.Entities, drawn, d, depth + 1, true);
            }

            double measured = dim.Measurement * xf.Scale;
            bool angular = dim is Cad.DimensionAngular2Line || dim is Cad.DimensionAngular3Pt;
            string label = CleanText(shown);
            if (string.IsNullOrWhiteSpace(label)) label = CleanText(dim.Text);
            if (string.IsNullOrWhiteSpace(label) || label.Contains("<>"))
            {
                string value = angular ? Fmt(measured * 180 / Math.PI) + "°" : Fmt(measured);
                label = string.IsNullOrWhiteSpace(label) ? value : label.Replace("<>", value);
            }

            var rec = new Dim { Label = label, Measured = angular ? measured * 180 / Math.PI : measured };
            xf.Apply(tx, ty, out rec.X, out rec.Y);
            rec.Kind = angular ? "angle" : dim is Cad.DimensionRadius ? "radius" : dim is Cad.DimensionDiameter ? "diameter" : "length";
            if (dim is Cad.DimensionAligned al)
            {
                xf.Apply(al.FirstPoint.X, al.FirstPoint.Y, out rec.P1X, out rec.P1Y);
                xf.Apply(al.SecondPoint.X, al.SecondPoint.Y, out rec.P2X, out rec.P2Y);
                rec.HasPoints = true;
                rec.Direction = DirectionOf(dim is Cad.DimensionLinear lin ? lin.Rotation + xf.Angle
                    : Math.Atan2(rec.P2Y - rec.P1Y, rec.P2X - rec.P1X));
            }
            if (!angular) rec.Number = PureNumber(label);
            if (Finite(rec.X) && Finite(rec.Y)) d.Dims.Add(rec);
        }

        /// <summary>The value of a label like "1250", "Ø495", "R20", "~370"; null for "24-Ø14", "M20", "2x45°".</summary>
        private static double? PureNumber(string label)
        {
            Match m = Regex.Match(label.Trim(), @"^[ØφΦ⌀R～~≈]?\s*(\d+(?:[.,]\d+)?)$");
            if (!m.Success) return null;
            return double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }

        /// <summary>Decodes CAD text: \U+XXXX, \M+nXXXX, MText formatting codes, stacked fractions, %%c / %%d / %%p.</summary>
        public static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"\\[Uu]\+([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            s = Regex.Replace(s, @"\\[Mm]\+\d([0-9A-Fa-f]{4})", DecodeMif);
            s = Regex.Replace(s, @"\\[Ss]([^;]*);", m => m.Groups[1].Value.Replace('^', '/').Replace('#', '/'));
            s = Regex.Replace(s, @"\\[fFHhQqWwTtAaCcp][^;\\]*;", "");
            s = Regex.Replace(s, @"\\[LlOoKk]", "");
            s = s.Replace("\\P", " / ").Replace("\\p", " / ").Replace("\\~", " ").Replace("\\X", " / ");
            s = s.Replace("\\{", "\u0001").Replace("\\}", "\u0002").Replace("{", "").Replace("}", "").Replace("\u0001", "{").Replace("\u0002", "}");
            s = Regex.Replace(s, @"%%[cC]", "Ø");
            s = Regex.Replace(s, @"%%[dD]", "°");
            s = Regex.Replace(s, @"%%[pP]", "±");
            s = Regex.Replace(s, @"%%[uUoO]", "");
            s = Regex.Replace(s, @"[ \t\r\n]+", " ");
            return s.Trim();
        }

        private static string DecodeMif(Match m)
        {
            try
            {
                string h = m.Groups[1].Value;
                byte[] b = { Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16) };
                return Encoding.GetEncoding(936).GetString(b);
            }
            catch { return ""; }
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static string Fmt(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ sheet

        /// <summary>The sheet: the largest rectangle holding a good part of the geometry, else the densest cluster.</summary>
        private static Rect2 FindSheet(Drawing d, out bool frameFound)
        {
            List<Seg> geo = d.Segs.Where(s => !s.IsDim).ToList();
            foreach (Rect2 r in d.Rects.OrderByDescending(r => r.W * r.H))
            {
                double aspect = Math.Max(r.W, r.H) / Math.Min(r.W, r.H);
                if (aspect > 5) continue;
                int inside = geo.Count(s => r.Has((s.X1 + s.X2) / 2, (s.Y1 + s.Y2) / 2));
                if (geo.Count > 0 && inside >= 0.3 * geo.Count) { frameFound = true; return r; }
            }
            // A frame drawn with separate lines: the outer box of the geometry has its four sides covered by lines.
            if (geo.Count > 0)
            {
                var box = new Rect2 { X0 = geo.Min(g => Math.Min(g.X1, g.X2)), X1 = geo.Max(g => Math.Max(g.X1, g.X2)), Y0 = geo.Min(g => Math.Min(g.Y1, g.Y2)), Y1 = geo.Max(g => Math.Max(g.Y1, g.Y2)) };
                if (box.W > 0 && box.H > 0 && BordersCovered(geo, box)) { frameFound = true; return box; }
            }
            frameFound = false;

            var pts = geo.Select(s => ((s.X1 + s.X2) / 2, (s.Y1 + s.Y2) / 2)).ToList();
            pts.AddRange(d.Texts.Select(t => (t.X, t.Y)));
            if (pts.Count == 0) pts.AddRange(d.Dims.Select(t => (t.X, t.Y)));
            double x0 = pts.Min(p => p.Item1), x1 = pts.Max(p => p.Item1), y0 = pts.Min(p => p.Item2), y1 = pts.Max(p => p.Item2);
            double cell = Math.Max(Math.Max(x1 - x0, y1 - y0) / 30, 1e-6);
            var groups = new Dictionary<(int, int), List<(double, double)>>();
            foreach (var p in pts)
            {
                var k = ((int)Math.Floor(p.Item1 / cell), (int)Math.Floor(p.Item2 / cell));
                if (!groups.TryGetValue(k, out var list)) groups[k] = list = new List<(double, double)>();
                list.Add(p);
            }
            // Keep every substantial cluster: small far-away leftovers are dropped, separate views are not.
            var seen = new HashSet<(int, int)>();
            var kept = new List<(double, double)>();
            foreach (var start in groups.Keys)
            {
                if (!seen.Add(start)) continue;
                var cluster = new List<(double, double)>();
                var stack = new Stack<(int, int)>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    cluster.AddRange(groups[c]);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var n = (c.Item1 + dx, c.Item2 + dy);
                            if (groups.ContainsKey(n) && seen.Add(n)) stack.Push(n);
                        }
                }
                if (cluster.Count >= 0.05 * pts.Count) kept.AddRange(cluster);
            }
            if (kept.Count == 0) kept = pts;
            double m = cell / 2;
            return new Rect2 { X0 = kept.Min(p => p.Item1) - m, X1 = kept.Max(p => p.Item1) + m, Y0 = kept.Min(p => p.Item2) - m, Y1 = kept.Max(p => p.Item2) + m };
        }

        private static bool BordersCovered(List<Seg> geo, Rect2 box)
        {
            double tol = 1e-3 * Math.Max(box.W, box.H);
            double Covered(Func<Seg, bool> onEdge, Func<Seg, double> len) => geo.Where(onEdge).Sum(len);
            double top = Covered(g => Math.Abs(g.Y1 - box.Y1) < tol && Math.Abs(g.Y2 - box.Y1) < tol, g => Math.Abs(g.X2 - g.X1));
            double bottom = Covered(g => Math.Abs(g.Y1 - box.Y0) < tol && Math.Abs(g.Y2 - box.Y0) < tol, g => Math.Abs(g.X2 - g.X1));
            double left = Covered(g => Math.Abs(g.X1 - box.X0) < tol && Math.Abs(g.X2 - box.X0) < tol, g => Math.Abs(g.Y2 - g.Y1));
            double right = Covered(g => Math.Abs(g.X1 - box.X1) < tol && Math.Abs(g.X2 - box.X1) < tol, g => Math.Abs(g.Y2 - g.Y1));
            return top >= 0.9 * box.W && bottom >= 0.9 * box.W && left >= 0.9 * box.H && right >= 0.9 * box.H;
        }

        private static Drawing CropToSheet(Drawing all, Rect2 s, out int outDims, out int outOther)
        {
            var d = new Drawing();
            d.Segs.AddRange(all.Segs.Where(x => s.Has((x.X1 + x.X2) / 2, (x.Y1 + x.Y2) / 2)));
            d.Texts.AddRange(all.Texts.Where(x => s.Has(x.X, x.Y)));
            d.Dims.AddRange(all.Dims.Where(x => s.Has(x.X, x.Y)));
            d.Rounds.AddRange(all.Rounds.Where(x => s.Has(x.X, x.Y)));
            outDims = all.Dims.Count - d.Dims.Count;
            outOther = all.Segs.Count(x => !x.IsDim) - d.Segs.Count(x => !x.IsDim) + (all.Texts.Count(x => !x.IsDim) - d.Texts.Count(x => !x.IsDim));
            return d;
        }

        // ------------------------------------------------------------------ regions (views)

        /// <summary>Connected groups of drawn geometry on a coarse grid; border lines and dimension lines do not connect them.</summary>
        private static List<Rect2> BuildRegions(Drawing d, Rect2 sheet)
        {
            double longSide = Math.Max(sheet.W, sheet.H), cell = longSide / 150;
            var cells = new Dictionary<(int, int), Rect2>();
            foreach (Seg s in d.Segs)
            {
                if (s.IsDim || s.Len > 0.6 * Math.Min(sheet.W, sheet.H) || IsAnnotationLayer(s.Layer)) continue;
                int steps = Math.Max(1, (int)Math.Ceiling(s.Len / cell));
                for (int i = 0; i <= steps; i++)
                {
                    double x = s.X1 + (s.X2 - s.X1) * i / steps, y = s.Y1 + (s.Y2 - s.Y1) * i / steps;
                    var k = ((int)Math.Floor(x / cell), (int)Math.Floor(y / cell));
                    if (!cells.TryGetValue(k, out Rect2 r)) cells[k] = r = new Rect2 { X0 = x, X1 = x, Y0 = y, Y1 = y };
                    r.X0 = Math.Min(r.X0, x); r.X1 = Math.Max(r.X1, x); r.Y0 = Math.Min(r.Y0, y); r.Y1 = Math.Max(r.Y1, y);
                }
            }

            var regions = new List<Rect2>();
            var seen = new HashSet<(int, int)>();
            foreach (var start in cells.Keys)
            {
                if (!seen.Add(start)) continue;
                var box = new Rect2 { X0 = double.MaxValue, Y0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue };
                var stack = new Stack<(int, int)>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    Rect2 r = cells[c];
                    box.X0 = Math.Min(box.X0, r.X0); box.X1 = Math.Max(box.X1, r.X1); box.Y0 = Math.Min(box.Y0, r.Y0); box.Y1 = Math.Max(box.Y1, r.Y1);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            var n = (c.Item1 + dx, c.Item2 + dy);
                            if (cells.ContainsKey(n) && seen.Add(n)) stack.Push(n);
                        }
                }
                if (Math.Max(box.W, box.H) > 0.02 * longSide) regions.Add(box);
            }
            return regions.OrderByDescending(r => r.W * r.H).ToList();
        }

        private static bool IsAnnotationLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            string l = layer.ToLowerInvariant();
            return l.Contains("dim") || l.Contains("标注") || l.Contains("border") || l.Contains("text") || l.Contains("文字");
        }

        private static void AssignDimensions(Drawing d, List<Rect2> regions)
        {
            foreach (Dim dim in d.Dims)
            {
                double x = dim.HasPoints ? dim.P1X : dim.X, y = dim.HasPoints ? dim.P1Y : dim.Y;
                dim.Region = NearestRegion(regions, x, y);
            }
        }

        private static int NearestRegion(List<Rect2> regions, double x, double y)
        {
            int best = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < regions.Count; i++)
            {
                Rect2 r = regions[i];
                double dx = Math.Max(Math.Max(r.X0 - x, 0), x - r.X1), dy = Math.Max(Math.Max(r.Y0 - y, 0), y - r.Y1);
                double dist = dx * dx + dy * dy;
                if (dist < bestDist - 1e-9) { bestDist = dist; best = i; }   // ties: the larger region comes first
            }
            return best;
        }

        // ------------------------------------------------------------------ summary for the model

        private static string BuildSummary(string name, CadDocument doc, Drawing d, Rect2 sheet, bool frameFound, List<Rect2> regions,
            int outDims, int outOther, Dictionary<string, int> unread, CadSheet result)
        {
            var sb = new StringBuilder();
            string units = doc.Header.InsUnits.ToString();
            sb.AppendLine($"--- EXACT DATA READ FROM THE CAD FILE \"{name}\" (use these numbers, not values read from the image) ---");
            sb.AppendLine($"The attached image of this file is a render of the sheet. Drawing units: {units}" +
                          (units == "Millimeters" ? "" : " (if unitless, a metric drawing is in millimeters)") + ". All coordinates below are drawing coordinates.");
            sb.AppendLine($"Sheet {(frameFound ? "frame" : "extents (no frame found)")}: {Box(sheet)}.");
            if (outDims + outOther > 0)
                sb.AppendLine($"Ignored as not on the sheet: {outDims} dimensions, {outOther} other objects.");
            if (unread.Count > 0)
                sb.AppendLine("Custom objects that could not be read (some dimensions or weld/surface symbols may be missing here and on the image, " +
                              "so do not treat a missing value as absent from the real drawing): " +
                              string.Join(", ", unread.OrderByDescending(k => k.Value).Select(k => $"{k.Key} × {k.Value}")) + ".");
            sb.AppendLine("Rules: the displayed label is the design value. A view can be drawn at its own scale, or be schematic: a dimension with a different " +
                          "\"drawn\" length is not a conflict when its whole region has the same ratio. Report as a conflict only the labels listed under " +
                          "\"label differs from the drawn length\", and only if they matter for the shape.");

            var dimLines = new StringBuilder();
            var groups = d.Dims.GroupBy(x => x.Region).OrderBy(g => g.Key);
            int mismatches = 0;
            foreach (var g in groups)
            {
                List<double> ratios = g.Where(x => x.Number.HasValue && x.Measured > 1e-6).Select(x => x.Number.Value / x.Measured).OrderBy(x => x).ToList();
                double scale = ratios.Count > 0 ? ratios[ratios.Count / 2] : 1;
                string title = g.Key >= 0 ? $"Region R{g.Key + 1} {Box(regions[g.Key])}" : "Outside the regions";
                dimLines.AppendLine();
                dimLines.AppendLine(title + (Math.Abs(scale - 1) > ScaleTolerance
                    ? $": drawn at its own scale, label ≈ {scale.ToString("0.##", CultureInfo.InvariantCulture)} × drawn length" : ""));
                var odd = new List<string>();
                foreach (Dim x in g.OrderByDescending(x => x.Measured))
                {
                    dimLines.Append($"  {x.Label} [{x.Kind}");
                    if (x.HasPoints) dimLines.Append($" {x.Direction} {Fmt(x.P1X)},{Fmt(x.P1Y)} to {Fmt(x.P2X)},{Fmt(x.P2Y)}");
                    else dimLines.Append($" at {Fmt(x.X)},{Fmt(x.Y)}");
                    dimLines.AppendLine($" drawn {Fmt(x.Measured)}]");
                    if (x.Number.HasValue && Math.Abs(x.Number.Value - x.Measured * scale) > Math.Max(LabelToleranceMm, ScaleTolerance * x.Number.Value))
                        odd.Add($"{x.Label} (drawn {Fmt(x.Measured)})");
                }
                if (odd.Count > 0)
                {
                    mismatches += odd.Count;
                    dimLines.AppendLine("  label differs from the drawn length: " + string.Join("; ", odd));
                }
            }
            result.MismatchCount = mismatches;

            sb.AppendLine();
            sb.AppendLine($"DIMENSIONS ({d.Dims.Count}): label [kind, direction, measured points, drawn length], grouped by region (a region is usually one view or table).");
            sb.Append(dimLines);

            sb.AppendLine();
            sb.AppendLine("TEXTS on the sheet, top to bottom, left to right (table rows stay together):");
            double rowStep = Math.Max(sheet.W, sheet.H) / 400;
            foreach (var row in d.Texts.Where(t => !t.IsDim).GroupBy(t => Math.Round(t.Y / rowStep)).OrderByDescending(g => g.Key))
                sb.AppendLine("  " + string.Join(" | ", row.OrderBy(t => t.X).Select(t => t.Value)));

            sb.AppendLine();
            sb.AppendLine("CIRCLES by region: diameter × count (centres of up to 12).");
            foreach (var g in d.Rounds.Where(r => r.IsCircle).GroupBy(r => NearestRegion(regions, r.X, r.Y)).OrderBy(g => g.Key))
            {
                sb.AppendLine(g.Key >= 0 ? $"Region R{g.Key + 1}:" : "Other:");
                foreach (var c in g.GroupBy(r => Math.Round(r.R * 2, 1)).OrderByDescending(c => c.Key))
                    sb.AppendLine($"  Ø{Fmt(c.Key)} × {c.Count()}: " + string.Join(" ", c.Take(12).Select(r => $"({Fmt(r.X)},{Fmt(r.Y)})")));
            }

            sb.AppendLine();
            sb.AppendLine("LONGEST GEOMETRY LINES by region (x1,y1 to x2,y2):");
            for (int i = 0; i < regions.Count; i++)
            {
                Rect2 r = regions[i];
                double min = 0.04 * Math.Max(r.W, r.H);
                List<Seg> lines = d.Segs.Where(s => !s.IsDim && s.Len >= min && !IsAnnotationLayer(s.Layer) && r.Has((s.X1 + s.X2) / 2, (s.Y1 + s.Y2) / 2))
                    .OrderByDescending(s => s.Len).Take(40).ToList();
                if (lines.Count == 0 || d.Dims.All(x => x.Region != i)) continue;   // tables and title blocks: the texts are enough
                sb.AppendLine($"Region R{i + 1}:");
                foreach (Seg s in lines)
                    sb.AppendLine($"  {Fmt(s.X1)},{Fmt(s.Y1)} to {Fmt(s.X2)},{Fmt(s.Y2)}");
            }

            string text = sb.ToString();
            return text.Length <= MaxSummaryChars ? text
                : text.Substring(0, MaxSummaryChars) + "\n[... the rest of the data was cut to keep the request small; use the image for it]";
        }

        private static string DirectionOf(double angle)
        {
            double sin = Math.Abs(Math.Sin(angle)), cos = Math.Abs(Math.Cos(angle));
            return sin < 1e-3 ? "horizontal" : cos < 1e-3 ? "vertical" : "aligned";
        }

        private static string Box(Rect2 r) => $"x {Fmt(r.X0)}..{Fmt(r.X1)}, y {Fmt(r.Y0)}..{Fmt(r.Y1)} ({Fmt(r.W)} × {Fmt(r.H)})";

        private static string BuildGeometryCsv(Drawing d)
        {
            var sb = new StringBuilder("kind,layer,x1,y1,x2,y2\n");
            foreach (Seg x in d.Segs)
                sb.Append(x.IsDim ? "dim," : "geo,").Append((x.Layer ?? "").Replace(',', ' ')).Append(',')
                  .Append(Fmt(x.X1)).Append(',').Append(Fmt(x.Y1)).Append(',').Append(Fmt(x.X2)).Append(',').Append(Fmt(x.Y2)).Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ sheet image

        private static void Render(Drawing d, Rect2 sheet, CadSheet result)
        {
            Exception error = null;
            // WPF drawing needs an STA thread with a dispatcher of its own; callers may be anywhere
            var thread = new Thread(() =>
            {
                try { RenderOnStaThread(d, sheet, result); }
                catch (Exception ex) { error = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (error != null) throw new InvalidOperationException("Could not render the drawing: " + error.Message, error);
        }

        private static void RenderOnStaThread(Drawing d, Rect2 sheet, CadSheet result)
        {
            double k = SheetLongSidePx / Math.Max(sheet.W, sheet.H);
            int width = Math.Max(1, (int)Math.Ceiling(sheet.W * k)), height = Math.Max(1, (int)Math.Ceiling(sheet.H * k));
            System.Windows.Point P(double x, double y) => new System.Windows.Point((x - sheet.X0) * k, (sheet.Y1 - y) * k);

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, width, height));
                DrawSegs(dc, d.Segs.Where(s => !s.IsDim), System.Windows.Media.Brushes.Black, P);
                DrawSegs(dc, d.Segs.Where(s => s.IsDim), System.Windows.Media.Brushes.Red, P);
                var face = new Typeface("Arial");
                foreach (Txt t in d.Texts)
                {
                    double em = Math.Max(7, t.Height * k * 1.25);
                    var ft = new System.Windows.Media.FormattedText(t.Value, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                        face, em, t.IsDim ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.DarkBlue, 1.0);
                    System.Windows.Point p = P(t.X, t.Y);
                    dc.PushTransform(new RotateTransform(-t.Rotation * 180 / Math.PI, p.X, p.Y));
                    dc.DrawText(ft, new System.Windows.Point(p.X, p.Y - (t.IsDim ? em / 2 : em)));
                    dc.Pop();
                }
            }

            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                result.Png = ms.ToArray();
            }
            result.WidthPx = width;
            result.HeightPx = height;
        }

        private static void DrawSegs(DrawingContext dc, IEnumerable<Seg> segs, System.Windows.Media.Brush brush, Func<double, double, System.Windows.Point> P)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
                foreach (Seg s in segs)
                {
                    g.BeginFigure(P(s.X1, s.Y1), false, false);
                    g.LineTo(P(s.X2, s.Y2), true, false);
                }
            geometry.Freeze();
            var pen = new System.Windows.Media.Pen(brush, 1);
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }
}
