using System.Globalization;
using System.Text;

namespace CoRFAGen.Data
{
    public class ConflictOption
    {
        public string Label { get; set; }
        public string Value { get; set; }
    }

    public class SourceConflict
    {
        public string Id { get; set; }
        public string Question { get; set; }
        public string Affects { get; set; }
        public List<ConflictOption> Options { get; set; } = new List<ConflictOption>();
        public int Recommended { get; set; }
    }

    public class ConflictDecision
    {
        public string Id { get; set; }
        public string Question { get; set; }
        public string Answer { get; set; }
        public bool IsCustom { get; set; }
    }

    /// <summary>
    /// Result of the first AI pass: what the model found in the source files before generating
    /// anything (views, dimensions, technical data, conflicts between dimensions).
    /// </summary>
    public class SourceAnalysis
    {
        public string RawJson { get; private set; }
        public string ObjectName { get; private set; }
        public string Units { get; private set; }
        public int ViewCount { get; private set; }
        public int DimensionCount { get; private set; }
        public int TechnicalDataCount { get; private set; }
        public double? OverallWidthX { get; private set; }
        public double? OverallDepthY { get; private set; }
        public double? OverallHeightZ { get; private set; }
        public List<SourceConflict> Conflicts { get; } = new List<SourceConflict>();
        public List<string> TechnicalDataLines { get; } = new List<string>();

        public static SourceAnalysis Parse(string json)
        {
            var a = new SourceAnalysis { RawJson = json };
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement r = doc.RootElement;
                if (r.TryGetProperty("object", out JsonElement obj) && obj.TryGetProperty("name", out JsonElement n) &&
                    n.ValueKind == JsonValueKind.String)
                    a.ObjectName = n.GetString();
                if (r.TryGetProperty("units", out JsonElement u) && u.ValueKind == JsonValueKind.String)
                    a.Units = u.GetString();
                a.ViewCount = Count(r, "views");
                a.DimensionCount = Count(r, "dimensions");
                a.TechnicalDataCount = Count(r, "technical_data");

                if (r.TryGetProperty("overall", out JsonElement ov) && ov.ValueKind == JsonValueKind.Object)
                {
                    a.OverallWidthX = Num(ov, "width_x");
                    a.OverallDepthY = Num(ov, "depth_y");
                    a.OverallHeightZ = Num(ov, "height_z");
                }

                if (r.TryGetProperty("technical_data", out JsonElement td) && td.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement t in td.EnumerateArray())
                    {
                        string name = Str(t, "name");
                        string value = t.TryGetProperty("value", out JsonElement v)
                            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : "";
                        string unit = Str(t, "unit");
                        a.TechnicalDataLines.Add($"{name}: {value} {unit}".Trim());
                    }
                }

                if (r.TryGetProperty("conflicts", out JsonElement cs) && cs.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (JsonElement c in cs.EnumerateArray())
                    {
                        i++;
                        var conflict = new SourceConflict
                        {
                            Id = Str(c, "id") ?? $"C{i}",
                            Question = Str(c, "question") ?? "Which value should be used?",
                            Affects = Str(c, "affects"),
                            Recommended = (int)(Num(c, "recommended") ?? 0)
                        };
                        if (c.TryGetProperty("options", out JsonElement opts) && opts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement o in opts.EnumerateArray())
                            {
                                string value = o.TryGetProperty("value", out JsonElement ovv)
                                    ? (ovv.ValueKind == JsonValueKind.String ? ovv.GetString() : ovv.GetRawText()) : null;
                                conflict.Options.Add(new ConflictOption { Label = Str(o, "label") ?? value, Value = value ?? Str(o, "label") });
                            }
                        }
                        if (conflict.Options.Count > 0)
                        {
                            if (conflict.Recommended < 0 || conflict.Recommended >= conflict.Options.Count) conflict.Recommended = 0;
                            a.Conflicts.Add(conflict);
                        }
                    }
                }
            }
            return a;
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"Views: {ViewCount}, dimensions: {DimensionCount}, technical data: {TechnicalDataCount}");
            if (OverallWidthX.HasValue || OverallHeightZ.HasValue)
                sb.Append($"; overall X×Y×Z: {F(OverallWidthX)} × {F(OverallDepthY)} × {F(OverallHeightZ)} {Units}");
            return sb.ToString();
        }

        public static string DecisionsText(IList<ConflictDecision> decisions)
        {
            if (decisions == null || decisions.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (ConflictDecision d in decisions)
                sb.AppendLine($"- {d.Id}: {d.Question} -> USE: {d.Answer}{(d.IsCustom ? " (value entered by the user)" : "")}");
            return sb.ToString();
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "?";

        private static int Count(JsonElement r, string prop) =>
            r.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;

        private static string Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Num(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out JsonElement v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }
    }
}
