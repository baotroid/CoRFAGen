using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CoRFAGen.Data
{
    public class ParameterMapping
    {
        public string From { get; set; }
        public string To { get; set; }
        /// <summary>existing | shared | new</summary>
        public string Target { get; set; }
        /// <summary>Translated text value (text parameters only), or null.</summary>
        public string Value { get; set; }
    }

    /// <summary>
    /// Maps the parameters of a family JSON onto the parameters of the open family/template and/or a shared
    /// parameter file (ФОП), and translates new parameter names and text values into the family language.
    /// Runs after AI generation AND for JSON files loaded from disk (which were never seen by the model).
    /// Uses the selected AI model when an API key is available, otherwise a built-in dictionary.
    /// </summary>
    public static class ParameterMapper
    {
        private static readonly HashSet<string> GeometricTypes = new HashSet<string> { "length", "angle" };

        public static async Task<string> MapAsync(string json, ParameterTargetContext ctx, FamilyAiService ai, List<string> log)
        {
            if (ctx == null || (!ctx.UseFamilyParameters && !ctx.UseSharedFile)) return json;
            JsonNode root = JsonNode.Parse(json);
            JsonArray parms = root?["parameters"] as JsonArray;
            if (parms == null || parms.Count == 0) return json;

            List<ParameterMapping> maps = null;
            if (ai != null)
            {
                try
                {
                    maps = await AiMapAsync(parms, ctx, ai).ConfigureAwait(false);
                    log?.Add($"Parameter mapping by AI: {maps.Count} item(s)");
                }
                catch (Exception ex)
                {
                    log?.Add("AI parameter mapping failed, using the built-in dictionary: " + ex.Message);
                    maps = null;
                }
            }
            if (maps == null || maps.Count == 0)
            {
                maps = LocalMap(parms, ctx);
                log?.Add($"Parameter mapping by dictionary: {maps.Count} item(s)");
            }

            Apply(root, maps, ctx, log);
            return root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        // ------------------------------------------------------------------ AI mapping

        private static async Task<List<ParameterMapping>> AiMapAsync(JsonArray parms, ParameterTargetContext ctx, FamilyAiService ai)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Family language: {ctx.LanguageName}.");
            sb.AppendLine("PARAMETERS OF THE GENERATED FAMILY (name | type | unit | group | value):");
            foreach (JsonNode p in parms)
            {
                if (p == null) continue;
                sb.AppendLine($"- {Str(p, "name")} | {Str(p, "type")} | {Str(p, "unit")} | {Str(p, "group")} | " +
                              LlmHttp.TruncateForMessage(p["default_value"]?.ToJsonString() ?? "", 80) +
                              (string.IsNullOrEmpty(Str(p, "builtin")) ? "" : " | builtin " + Str(p, "builtin")));
            }
            if (ctx.UseFamilyParameters && ctx.FamilyParameters.Count > 0)
            {
                sb.AppendLine("TARGETS — EXISTING FAMILY PARAMETERS (target \"existing\"):");
                foreach (TargetParameter t in ctx.FamilyParameters) sb.AppendLine($"- {t.Name} [{t.DataType}]");
            }
            if (ctx.UseSharedFile && ctx.SharedFileParameters.Count > 0)
            {
                sb.AppendLine("TARGETS — SHARED PARAMETER FILE (target \"shared\"):");
                foreach (TargetParameter t in ctx.SharedFileParameters.Take(600))
                    sb.AppendLine($"- {t.Name} [{t.DataType}]" + (string.IsNullOrEmpty(t.Group) ? "" : " group " + t.Group));
            }
            sb.AppendLine($@"
RULES:
1. For EVERY parameter above return one item {{""from"", ""to"", ""target"", ""value""}}.
2. If a target parameter has the same meaning and a compatible type (e.g. Weight/Mass → ADSK_Масса, Manufacturer → ADSK_Завод-изготовитель,
   Name/Title → ADSK_Наименование, overall Length/Width/Height → ADSK_Размер_Длина/Ширина/Высота), set ""to"" = its EXACT name and
   ""target"" = ""existing"" or ""shared"". Never map two parameters to the same target.
3. Otherwise ""target"" = ""new"" and ""to"" = the parameter name in {ctx.LanguageName} (short standard engineering term). Geometric
   parameters that are only used to build geometry (radii, offsets, thicknesses) may keep their name.
4. Parameters with ""builtin"" (Model, Manufacturer, Description...) keep their name unless a target fits better.
5. ""value"": for text parameters give the value translated into {ctx.LanguageName} if it is in another language, else null.
Return ONLY: {{""mapping"": [ ... ]}}");

            const string system = "You map Revit family parameters onto existing / shared parameters and translate names. Reply with one JSON object only.";
            string response = await ai.SendTextAsync(system, sb.ToString()).ConfigureAwait(false);

            var result = new List<ParameterMapping>();
            using (JsonDocument doc = JsonDocument.Parse(LlmHttp.CleanJsonResponse(response)))
            {
                if (!doc.RootElement.TryGetProperty("mapping", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                    return result;
                foreach (JsonElement m in arr.EnumerateArray())
                {
                    string from = Get(m, "from"), to = Get(m, "to");
                    if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) continue;
                    result.Add(new ParameterMapping { From = from, To = to.Trim(), Target = Get(m, "target") ?? "new", Value = Get(m, "value") });
                }
            }
            return result;
        }

        private static string Get(JsonElement e, string p) =>
            e.TryGetProperty(p, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string Str(JsonNode n, string p)
        {
            try { return n?[p]?.GetValue<string>() ?? ""; } catch { return n?[p]?.ToJsonString() ?? ""; }
        }

        // ------------------------------------------------------------------ dictionary mapping (no API key)

        /// <summary>English term → Russian term (also used to recognise targets such as ADSK_Масса).</summary>
        private static readonly (string[] en, string ru)[] Terms =
        {
            (new[] { "name", "title", "product name", "item name" }, "Наименование"),
            (new[] { "mass", "weight", "dry weight", "net weight" }, "Масса"),
            (new[] { "operating weight", "operating mass" }, "Масса в рабочем состоянии"),
            (new[] { "power", "rated power", "motor power", "electrical power", "power consumption" }, "Мощность"),
            (new[] { "heating capacity", "heat output" }, "Тепловая мощность"),
            (new[] { "cooling capacity" }, "Холодопроизводительность"),
            (new[] { "voltage", "rated voltage" }, "Напряжение"),
            (new[] { "current", "rated current" }, "Ток"),
            (new[] { "frequency" }, "Частота"),
            (new[] { "air flow", "airflow", "air volume" }, "Расход воздуха"),
            (new[] { "flow", "flow rate", "capacity", "throughput", "productivity", "performance" }, "Производительность"),
            (new[] { "pressure", "working pressure", "operating pressure" }, "Давление"),
            (new[] { "pressure drop", "pressure loss", "resistance" }, "Потери давления"),
            (new[] { "temperature", "operating temperature" }, "Температура"),
            (new[] { "manufacturer", "maker", "vendor" }, "Завод-изготовитель"),
            (new[] { "model", "type designation" }, "Модель"),
            (new[] { "mark", "designation", "code", "article", "part number" }, "Обозначение"),
            (new[] { "material" }, "Материал"),
            (new[] { "standard", "gost", "specification" }, "Стандарт"),
            (new[] { "description" }, "Описание"),
            (new[] { "note", "notes", "comment", "comments" }, "Примечание"),
            (new[] { "length", "overall length" }, "Длина"),
            (new[] { "width", "overall width" }, "Ширина"),
            (new[] { "height", "overall height" }, "Высота"),
            (new[] { "diameter" }, "Диаметр"),
            (new[] { "speed", "rotation speed", "rpm" }, "Частота вращения"),
            (new[] { "volume" }, "Объём"),
            (new[] { "efficiency" }, "КПД"),
            (new[] { "noise level", "sound power level", "sound pressure level" }, "Уровень шума"),
            (new[] { "ingress protection", "ip rating", "protection class" }, "Степень защиты"),
            (new[] { "explosion protection" }, "Взрывозащита"),
            (new[] { "quantity", "count" }, "Количество"),
            (new[] { "unit", "units of measure" }, "Единица измерения"),
        };

        private static string Norm(string s) =>
            Regex.Replace((s ?? "").ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+", " ").Trim();

        private static List<ParameterMapping> LocalMap(JsonArray parms, ParameterTargetContext ctx)
        {
            var targets = new List<(TargetParameter t, string kind)>();
            if (ctx.UseFamilyParameters) targets.AddRange(ctx.FamilyParameters.Select(t => (t, "existing")));
            if (ctx.UseSharedFile) targets.AddRange(ctx.SharedFileParameters.Select(t => (t, "shared")));
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ParameterMapping>();
            bool ru = ctx.Language == "ru";

            foreach (JsonNode p in parms)
            {
                string name = Str(p, "name");
                if (string.IsNullOrEmpty(name)) continue;
                string type = Str(p, "type");
                string n = Norm(name.Replace('_', ' '));

                // dictionary entry for this parameter (by English or Russian term)
                var entry = Terms.FirstOrDefault(d => d.en.Any(e => e == n) || Norm(d.ru) == n);
                string ruTerm = entry.ru;

                // 1) exact name match, 2) target whose name contains the Russian / English term
                (TargetParameter t, string kind) hit = targets.FirstOrDefault(x => !used.Contains(x.t.Name) && Norm(x.t.Name) == n);
                if (hit.t == null && ruTerm != null)
                {
                    string r = Norm(ruTerm);
                    hit = targets.Where(x => !used.Contains(x.t.Name) && Compatible(type, x.t.DataType))
                                 .Where(x => (" " + Norm(x.t.Name.Replace('_', ' ')) + " ").Contains(" " + r + " ") ||
                                             entry.en.Any(e => (" " + Norm(x.t.Name.Replace('_', ' ')) + " ").Contains(" " + e + " ")))
                                 .OrderBy(x => x.t.Name.Length)
                                 .FirstOrDefault();
                }

                if (hit.t != null)
                {
                    used.Add(hit.t.Name);
                    result.Add(new ParameterMapping { From = name, To = hit.t.Name, Target = hit.kind });
                }
                else if (ru && ruTerm != null && !GeometricTypes.Contains(type))
                {
                    result.Add(new ParameterMapping { From = name, To = ruTerm, Target = "new" });
                }
            }
            return result;
        }

        private static bool Compatible(string jsonType, string targetType)
        {
            if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(jsonType)) return true;
            if (targetType == "text") return true; // anything can be written as text
            if (jsonType == targetType) return true;
            bool num(string t) => t != "text" && t != "material" && t != "yes_no";
            return num(jsonType) && num(targetType) && !GeometricTypes.Contains(targetType) && !GeometricTypes.Contains(jsonType);
        }

        // ------------------------------------------------------------------ applying

        private static readonly HashSet<string> NonExpressionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name", "description", "source", "group", "type", "units", "unit", "builtin", "target", "instance_or_type",
            "default_value", "schema_version", "category", "subcategory", "material", "sketch_plane", "direction", "detail_level"
        };

        private static void Apply(JsonNode root, List<ParameterMapping> maps, ParameterTargetContext ctx, List<string> log)
        {
            JsonArray parms = (JsonArray)root["parameters"];
            var existingNames = new HashSet<string>(parms.Select(p => Str(p, "name")), StringComparer.OrdinalIgnoreCase);
            var renames = new Dictionary<string, string>();
            var takenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ParameterMapping m in maps)
            {
                JsonNode p = parms.FirstOrDefault(x => string.Equals(Str(x, "name"), m.From, StringComparison.OrdinalIgnoreCase));
                if (p == null) continue;
                string to = m.To.Trim();
                bool isTarget = m.Target == "existing" || m.Target == "shared";
                if (isTarget && !takenTargets.Add(to)) continue; // one source per target

                if (!string.Equals(to, m.From, StringComparison.Ordinal))
                {
                    // never collide with another JSON parameter
                    if (existingNames.Contains(to) && !string.Equals(to, m.From, StringComparison.OrdinalIgnoreCase)) continue;
                    p["name"] = to;
                    existingNames.Add(to);
                    renames[m.From] = to;
                    log?.Add($"  {m.From} → {to} ({m.Target})");
                }
                p["target"] = isTarget ? m.Target : "new";
                if (!string.IsNullOrWhiteSpace(m.Value) && Str(p, "type") == "text")
                    p["default_value"] = m.Value;
                if (isTarget) p.AsObject().Remove("builtin"); // the mapped parameter wins over a built-in
            }

            if (renames.Count > 0) RenameInExpressions(root, renames, null);

            // catalog families: rename the table columns too
            if (renames.Count > 0 && root["catalog"] is JsonObject cat)
            {
                if (cat["types"] is JsonArray rows)
                    foreach (JsonObject r in rows.OfType<JsonObject>())
                        foreach (var rn in renames)
                            if (r.TryGetPropertyValue(rn.Key, out JsonNode v)) { r.Remove(rn.Key); r[rn.Value] = v; }
                if (cat["columns"] is JsonArray cols)
                    foreach (JsonObject c in cols.OfType<JsonObject>())
                        if (Str(c, "name") is string cn && renames.TryGetValue(cn, out string nn)) c["name"] = nn;
                if (cat["type_name_pattern"] is JsonValue pv && pv.TryGetValue(out string pattern))
                {
                    foreach (var rn in renames) pattern = pattern.Replace("{" + rn.Key + "}", "{" + rn.Value + "}");
                    cat["type_name_pattern"] = pattern;
                }
                // lookup keys and connector parameters refer to columns by name
                if (cat["lookup"]?["keys"] is JsonArray keys)
                    for (int i = 0; i < keys.Count; i++)
                        if (keys[i] is JsonValue kv && kv.TryGetValue(out string k) && renames.TryGetValue(k, out string nk)) keys[i] = nk;
                if (cat["connectors"] is JsonObject con)
                    foreach (string ck in new[] { "length_parameter", "diameter_parameter" })
                        if (con[ck] is JsonValue cv && cv.TryGetValue(out string cn2) && renames.TryGetValue(cn2, out string nn2)) con[ck] = nn2;
            }
        }

        /// <summary>Replaces renamed parameter identifiers (spaces → underscores) inside expression strings.</summary>
        private static void RenameInExpressions(JsonNode node, Dictionary<string, string> renames, string key)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var kv in o.ToList())
                    {
                        if (kv.Value is JsonValue v && v.TryGetValue(out string s))
                        {
                            if (NonExpressionKeys.Contains(kv.Key)) continue;
                            string r = ReplaceIdentifiers(s, renames);
                            if (r != s) o[kv.Key] = r;
                        }
                        else if (kv.Value != null && !string.Equals(kv.Key, "parameters", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(kv.Key, "catalog", StringComparison.OrdinalIgnoreCase))
                            RenameInExpressions(kv.Value, renames, kv.Key);
                    }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonValue v && v.TryGetValue(out string s))
                        {
                            string r = ReplaceIdentifiers(s, renames);
                            if (r != s) a[i] = r;
                        }
                        else if (a[i] != null) RenameInExpressions(a[i], renames, key);
                    }
                    break;
            }
        }

        private static string ReplaceIdentifiers(string expr, Dictionary<string, string> renames)
        {
            foreach (var kv in renames.OrderByDescending(k => k.Key.Length))
            {
                string from = Regex.Escape(kv.Key.Replace(" ", "_"));
                string to = kv.Value.Replace(" ", "_");
                expr = Regex.Replace(expr, @"(?<![\p{L}\p{N}_])" + from + @"(?![\p{L}\p{N}_])", to.Replace("$", "$$"));
            }
            return expr;
        }
    }
}
