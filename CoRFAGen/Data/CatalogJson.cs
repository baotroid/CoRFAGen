using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CoRFAGen.Data
{
    /// <summary>One row of a catalog (one Revit family type).</summary>
    public class CatalogType
    {
        public string Name { get; set; }
        public Dictionary<string, JsonElement> Values { get; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        public int SetIndex { get; set; }
        public string SetLabel { get; set; }
    }

    /// <summary>
    /// Family JSON with a "catalog" section: one parametric template (geometry as expressions of the table columns)
    /// + a table of types (one row = one Revit type).
    /// <code>
    /// "catalog": {
    ///   "type_name_pattern": "{Префикс} {Обозначение} DN{DN} PN{PN}",
    ///   "constants": { "Префикс": "КЗРр", "Обозначение": "25с947нж" },
    ///   "columns": [ { "name": "DN", "type": "integer" }, { "name": "D1", "type": "length" }, { "name": "Масса", "type": "mass", "unit": "кг" } ],
    ///   "types": [ { "DN": 15, "PN": 16, "D1": 46, ... } ],
    ///   "toggles": [ { "id": "actuator", "parameter": "Показать_привод", "default": true } ]
    /// }
    /// </code>
    /// Geometry elements may carry "toggle": "actuator" and extrusions may carry "holes" (bore, polar bolt pattern).
    /// </summary>
    public static class CatalogJson
    {
        public const string SetIndexParameter = "BGS_Set";
        public const string LookupTableName = "BGS_Catalog";

        public static bool IsCatalog(string json)
        {
            try
            {
                using (JsonDocument d = JsonDocument.Parse(json))
                    return IsCatalog(d.RootElement);
            }
            catch { return false; }
        }

        public static bool IsCatalog(JsonElement root) =>
            root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("catalog", out JsonElement c) && c.ValueKind == JsonValueKind.Object &&
            c.TryGetProperty("types", out JsonElement t) && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0;

        // ------------------------------------------------------------------ types

        public static List<CatalogType> ReadTypes(JsonElement root)
        {
            var list = new List<CatalogType>();
            JsonElement cat = root.GetProperty("catalog");
            string pattern = cat.TryGetProperty("type_name_pattern", out JsonElement p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : "DN{DN} PN{PN}";
            var constants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (cat.TryGetProperty("constants", out JsonElement cs) && cs.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty kv in cs.EnumerateObject()) constants[kv.Name] = ValueText(kv.Value);

            var geomIds = GeometryIdentifiers(root);
            var setByKey = new Dictionary<string, int>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement row in cat.GetProperty("types").EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                var t = new CatalogType();
                foreach (JsonProperty kv in row.EnumerateObject()) t.Values[kv.Name] = kv.Value.Clone();

                string name = FormatName(pattern, t.Values, constants);
                string unique = name; int k = 2;
                while (!usedNames.Add(unique)) unique = $"{name} ({k++})";
                t.Name = unique;

                // rows with identical geometric values share one geometry set
                string key = string.Join(";", t.Values
                    .Where(v => geomIds.Contains(v.Key.Replace(" ", "_")))
                    .OrderBy(v => v.Key, StringComparer.Ordinal)
                    .Select(v => v.Key + "=" + ValueText(v.Value)));
                if (!setByKey.TryGetValue(key, out int set))
                {
                    set = setByKey.Count + 1;
                    setByKey[key] = set;
                }
                t.SetIndex = set;
                list.Add(t);
            }
            foreach (var g in list.GroupBy(x => x.SetIndex))
            {
                string first = g.First().Name;
                foreach (CatalogType t in g) t.SetLabel = g.Count() > 1 ? $"{first} (+{g.Count() - 1})" : first;
            }
            return list;
        }

        public static string FormatName(string pattern, Dictionary<string, JsonElement> values, Dictionary<string, string> constants)
        {
            string s = Regex.Replace(pattern ?? "", @"\{([^{}]+)\}", m =>
            {
                string key = m.Groups[1].Value.Trim();
                if (values != null && values.TryGetValue(key, out JsonElement v)) return ValueText(v);
                if (constants != null && constants.TryGetValue(key, out string c)) return c;
                return "";
            });
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        public static string ValueText(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    return v.GetDouble().ToString("0.###", CultureInfo.InvariantCulture);
                case JsonValueKind.String: return v.GetString() ?? "";
                case JsonValueKind.True: return "Yes";
                case JsonValueKind.False: return "No";
                case JsonValueKind.Null:
                case JsonValueKind.Undefined: return "";
                default: return v.GetRawText();
            }
        }

        /// <summary>Identifiers (parameter names with underscores) used by reference planes and geometry.</summary>
        public static HashSet<string> GeometryIdentifiers(JsonElement root)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            void Walk(JsonElement e, string key)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (JsonProperty p in e.EnumerateObject())
                            if (!NonExpressionKeys.Contains(p.Name)) Walk(p.Value, p.Name);
                        break;
                    case JsonValueKind.Array:
                        foreach (JsonElement x in e.EnumerateArray()) Walk(x, key);
                        break;
                    case JsonValueKind.String:
                        foreach (Match m in Regex.Matches(e.GetString() ?? "", @"[\p{L}_][\p{L}\p{N}_]*")) ids.Add(m.Value);
                        break;
                }
            }
            if (root.TryGetProperty("reference_planes", out JsonElement rp)) Walk(rp, "reference_planes");
            if (root.TryGetProperty("geometry", out JsonElement g)) Walk(g, "geometry");
            return ids;
        }

        private static readonly HashSet<string> NonExpressionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "type", "shape", "sketch_plane", "subcategory", "direction", "is_reference", "toggle", "is_void",
            "description", "material", "plane", "profile_type", "path_plane"
        };

        // ------------------------------------------------------------------ single-type instance (preview / checks)

        /// <summary>
        /// Numeric single-type family JSON for the 3D preview and the geometry check: parameter defaults = values of the type,
        /// every expression evaluated, holes converted to void extrusions, catalog section removed.
        /// </summary>
        public static string Instantiate(string catalogJson, int typeIndex)
        {
            JsonNode root = JsonNode.Parse(catalogJson);
            JsonArray types = root["catalog"]?["types"] as JsonArray;
            JsonObject row = types != null && typeIndex >= 0 && typeIndex < types.Count ? types[typeIndex] as JsonObject : null;

            var values = new Dictionary<string, double>();
            if (root["parameters"] is JsonArray parms)
            {
                foreach (JsonNode p in parms)
                {
                    string name = p?["name"]?.GetValue<string>();
                    if (name == null) continue;
                    if (row != null && row.TryGetPropertyValue(name, out JsonNode rv) && rv != null)
                        p["default_value"] = rv.DeepClone();
                    if (TryNumber(p["default_value"], out double d)) values[name.Replace(" ", "_")] = d;
                }
            }
            if (row != null)
                foreach (var kv in row)
                    if (TryNumber(kv.Value, out double d)) values[kv.Key.Replace(" ", "_")] = d;

            if (root["reference_planes"] is JsonNode rps) EvaluateAll(rps, values);
            if (root["geometry"] is JsonArray geom)
            {
                EvaluateAll(geom, values);
                ExpandPatterns(geom);
                var extra = new List<JsonNode>();
                foreach (JsonNode g in geom)
                {
                    if (g is JsonObject go && go["holes"] is JsonArray holes)
                    {
                        extra.AddRange(HolesAsVoids(go, holes));
                        go.Remove("holes");
                    }
                    (g as JsonObject)?.Remove("toggle");
                }
                foreach (JsonNode x in extra) geom.Add(x);
            }
            (root as JsonObject)?.Remove("catalog");
            if (row != null && root["metadata"] is JsonObject meta)
            {
                using (JsonDocument d = JsonDocument.Parse(catalogJson))
                {
                    List<CatalogType> t = ReadTypes(d.RootElement);
                    if (typeIndex < t.Count) meta["type_name"] = t[typeIndex].Name;
                }
            }
            return root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        /// <summary>Element-level polar arrays ("pattern" on a geometry element) of an evaluated (numeric) JSON → copies.</summary>
        private static void ExpandPatterns(JsonArray geom)
        {
            for (int i = 0; i < geom.Count; i++)
            {
                if (!(geom[i] is JsonObject g) || !(g["pattern"] is JsonObject pat)) continue;
                int count = Math.Max(1, Math.Min(64, (int)Math.Round(Num(pat["count"]))));
                double r = Num(pat["radius"]), a0 = Num(pat["start_angle"]);
                double pu = Num(pat["center"]?["u"]), pv = Num(pat["center"]?["v"]);
                g.Remove("pattern");
                var copies = new List<JsonNode>();
                for (int k = 0; k < count; k++)
                {
                    double a = (a0 + 360.0 * k / count) * Math.PI / 180.0;
                    double du = pu + r * Math.Cos(a), dv = pv + r * Math.Sin(a);
                    var c = (JsonObject)g.DeepClone();
                    c["name"] = (g["name"]?.GetValue<string>() ?? "element") + " #" + (k + 1);
                    foreach (string key in new[] { "profile", "bottom_profile", "top_profile" })
                    {
                        if (!(c[key] is JsonObject prof)) continue;
                        JsonObject pt = (prof["center"] ?? prof["origin"]) as JsonObject;
                        if (pt == null) continue;
                        pt["u"] = Math.Round(Num(pt["u"]) + du, 4);
                        pt["v"] = Math.Round(Num(pt["v"]) + dv, 4);
                    }
                    copies.Add(c);
                }
                geom.RemoveAt(i);
                for (int k = 0; k < copies.Count; k++) geom.Insert(i + k, copies[k]);
                i += copies.Count - 1;
            }
        }

        private static IEnumerable<JsonNode> HolesAsVoids(JsonObject ext, JsonArray holes)
        {
            int i = 0;
            foreach (JsonNode h in holes)
            {
                double r = Num(h?["radius"]);
                if (r <= 0) continue;
                double hu = Num(h["center"]?["u"]), hv = Num(h["center"]?["v"]);
                var centers = new List<(double u, double v)>();
                if (h["pattern"] is JsonObject pat)
                {
                    int count = (int)Math.Round(Num(pat["count"]));
                    double pr = Num(pat["radius"]), a0 = Num(pat["start_angle"]);
                    double pu = pat["center"] != null ? Num(pat["center"]["u"]) : hu;
                    double pv = pat["center"] != null ? Num(pat["center"]["v"]) : hv;
                    for (int k = 0; k < Math.Max(0, Math.Min(count, 64)); k++)
                    {
                        double a = (a0 + 360.0 * k / count) * Math.PI / 180.0;
                        centers.Add((pu + pr * Math.Cos(a), pv + pr * Math.Sin(a)));
                    }
                }
                else centers.Add((hu, hv));

                foreach (var c in centers)
                {
                    yield return new JsonObject
                    {
                        ["name"] = $"{ext["name"]?.GetValue<string>()} hole {++i}",
                        ["type"] = "extrusion",
                        ["is_void"] = true,
                        ["sketch_plane"] = ext["sketch_plane"]?.DeepClone(),
                        ["profile"] = new JsonObject
                        {
                            ["shape"] = "circle",
                            ["center"] = new JsonObject { ["u"] = Math.Round(c.u, 4), ["v"] = Math.Round(c.v, 4) },
                            ["radius"] = r
                        },
                        ["extrusion_start"] = ext["extrusion_start"]?.DeepClone(),
                        ["extrusion_end"] = ext["extrusion_end"]?.DeepClone()
                    };
                }
            }
        }

        private static void EvaluateAll(JsonNode node, Dictionary<string, double> values)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var kv in o.ToList())
                    {
                        if (NonExpressionKeys.Contains(kv.Key)) continue;
                        if (kv.Value is JsonValue v && v.TryGetValue(out string s))
                        {
                            if (TryEvaluate(s, values, out double d)) o[kv.Key] = Math.Round(d, 6);
                        }
                        else if (kv.Value != null) EvaluateAll(kv.Value, values);
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonValue v && v.TryGetValue(out string s))
                        {
                            if (TryEvaluate(s, values, out double d)) a[i] = Math.Round(d, 6);
                        }
                        else if (a[i] != null) EvaluateAll(a[i], values);
                    }
                    break;
            }
        }

        public static bool TryEvaluate(string expr, Dictionary<string, double> values, out double result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(expr)) return false;
            if (double.TryParse(expr.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return true;
            string e = expr;
            foreach (var kv in values.OrderByDescending(k => k.Key.Length))
                e = Regex.Replace(e, @"(?<![\p{L}\p{N}_.])" + Regex.Escape(kv.Key) + @"(?![\p{L}\p{N}_])",
                    "(" + kv.Value.ToString("R", CultureInfo.InvariantCulture) + ")");
            if (Regex.IsMatch(e, @"[\p{L}_]")) return false; // unknown identifier left
            try
            {
                var dt = new DataTable { Locale = CultureInfo.InvariantCulture };
                result = Convert.ToDouble(dt.Compute(e, ""), CultureInfo.InvariantCulture);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch { return false; }
        }

        private static bool TryNumber(JsonNode n, out double d)
        {
            d = 0;
            if (n is JsonValue v)
            {
                if (v.TryGetValue(out double dd)) { d = dd; return true; }
                if (v.TryGetValue(out int ii)) { d = ii; return true; }
                if (v.TryGetValue(out long ll)) { d = ll; return true; }
                if (v.TryGetValue(out string s))
                    return double.TryParse((s ?? "").Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            }
            return false;
        }

        private static double Num(JsonNode n) => TryNumber(n, out double d) ? d : 0;
    }
}
