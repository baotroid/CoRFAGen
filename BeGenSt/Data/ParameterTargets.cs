using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BeGenSt.Data
{
    /// <summary>An existing parameter the AI should fill (from the family/template or from a shared parameter file).</summary>
    public class TargetParameter
    {
        public string Name { get; set; }
        /// <summary>Schema-like type: length, angle, number, integer, yes_no, text, material, mass, air_flow ... or the raw spec id.</summary>
        public string DataType { get; set; }
        public string Group { get; set; }
        public bool? IsInstance { get; set; }
        public string Guid { get; set; }
        public string Description { get; set; }
        /// <summary>"family" or "shared_file".</summary>
        public string Source { get; set; }
    }

    /// <summary>Parameters the generated family must reuse, plus the language of the family/template.</summary>
    public class ParameterTargetContext
    {
        public List<TargetParameter> FamilyParameters { get; } = new List<TargetParameter>();
        public List<TargetParameter> SharedFileParameters { get; } = new List<TargetParameter>();
        public string SharedFilePath { get; set; }
        /// <summary>"ru" or "en".</summary>
        public string Language { get; set; } = "en";
        public bool UseFamilyParameters { get; set; }
        public bool UseSharedFile { get; set; }

        public bool IsActive => (UseFamilyParameters && FamilyParameters.Count > 0) ||
                                (UseSharedFile && SharedFileParameters.Count > 0) ||
                                UseFamilyParameters || UseSharedFile;

        public string LanguageName => Language == "ru" ? "Russian" : "English";

        /// <summary>Prompt block appended to the generation / verification requests.</summary>
        public string BuildPromptText(int maxShared = 400)
        {
            if (!UseFamilyParameters && !UseSharedFile) return null;
            var sb = new StringBuilder();
            sb.AppendLine("PARAMETER MAPPING (mandatory):");
            sb.AppendLine($"- The family / template is in {LanguageName}. Name every NEW parameter and write every text value " +
                          $"(description, material, notes, units in text) in {LanguageName}. Geometric parameters used in " +
                          "expressions may keep short Latin names unless an existing parameter below fits.");

            if (UseFamilyParameters && FamilyParameters.Count > 0)
            {
                sb.AppendLine("- EXISTING FAMILY PARAMETERS. When a value from the sources (technical data OR an overall dimension) " +
                              "matches one of these by meaning, use EXACTLY this name and type, set \"target\": \"existing\" " +
                              "and do not create a duplicate:");
                foreach (TargetParameter p in FamilyParameters)
                    sb.AppendLine("  • " + Describe(p));
            }

            if (UseSharedFile && SharedFileParameters.Count > 0)
            {
                sb.AppendLine($"- SHARED PARAMETER FILE ({Path.GetFileName(SharedFilePath)}). When a value matches one of these by meaning, " +
                              "use EXACTLY this name and type and set \"target\": \"shared\" (it will be added to the family as a shared parameter):");
                foreach (TargetParameter p in SharedFileParameters.Take(maxShared))
                    sb.AppendLine("  • " + Describe(p));
                if (SharedFileParameters.Count > maxShared)
                    sb.AppendLine($"  … {SharedFileParameters.Count - maxShared} more parameters omitted.");
            }

            sb.AppendLine("- Values of matched parameters: numbers in the unit given in \"unit\" (for length: the JSON units), " +
                          "text in " + LanguageName + ". Data that matches nothing above becomes a new parameter (\"target\": \"new\").");
            return sb.ToString();
        }

        private static string Describe(TargetParameter p)
        {
            string s = $"\"{p.Name}\" [{p.DataType}";
            if (p.IsInstance.HasValue) s += p.IsInstance.Value ? ", instance" : ", type";
            s += "]";
            if (!string.IsNullOrWhiteSpace(p.Group)) s += " group: " + p.Group;
            if (!string.IsNullOrWhiteSpace(p.Description)) s += " — " + LlmHttp.TruncateForMessage(p.Description, 80);
            return s;
        }

        // ------------------------------------------------------------------ collecting

        /// <summary>Reads user parameters of the active family document and detects the template language.</summary>
        public static ParameterTargetContext FromFamily(Document doc)
        {
            var ctx = new ParameterTargetContext();
            var langSamples = new List<string>();
            try
            {
                FamilyManager fm = doc.FamilyManager;
                foreach (FamilyParameter fp in fm.Parameters)
                {
                    if (fp?.Definition == null) continue;
                    if (fp.Id.IntegerValueCompat() < 0) continue; // built-in parameters (Model, Manufacturer...) are handled separately
                    var t = new TargetParameter
                    {
                        Name = fp.Definition.Name,
                        DataType = SpecToType(fp.Definition.GetDataType()),
                        IsInstance = fp.IsInstance,
                        Source = "family"
                    };
                    try { t.Group = LabelUtils.GetLabelForGroup(fp.Definition.GetGroupTypeId()); } catch { }
                    if (fp.IsShared) { try { t.Guid = fp.GUID.ToString(); } catch { } }
                    ctx.FamilyParameters.Add(t);
                    langSamples.Add(t.Name);
                }

                // names stored in the template itself (not translated by the Revit UI language)
                foreach (ReferencePlane rp in new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                    if (!string.IsNullOrEmpty(rp.Name)) langSamples.Add(rp.Name);
                foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
                    if (!v.IsTemplate && !string.IsNullOrEmpty(v.Name)) langSamples.Add(v.Name);
                foreach (Level l in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                    langSamples.Add(l.Name);
                foreach (FamilyType ft in fm.Types)
                    if (!string.IsNullOrWhiteSpace(ft.Name)) langSamples.Add(ft.Name);
                if (!string.IsNullOrEmpty(doc.Title)) langSamples.Add(doc.Title);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ParameterTargetContext.FromFamily: " + ex.Message);
            }
            ctx.Language = DetectLanguage(langSamples);
            return ctx;
        }

        public static string DetectLanguage(IEnumerable<string> samples)
        {
            int cyr = 0, lat = 0;
            foreach (string s in samples ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(s)) continue;
                foreach (char c in s)
                {
                    if (c >= 'Ѐ' && c <= 'ӿ') cyr++;
                    else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) lat++;
                }
            }
            return cyr > 0 && cyr >= lat * 0.3 ? "ru" : "en";
        }

        // ------------------------------------------------------------------ shared parameter file (ФОП)

        /// <summary>
        /// Parses a Revit shared parameter file (tab separated *META / *GROUP / *PARAM sections; UTF-16, UTF-8 or ANSI).
        /// </summary>
        public static List<TargetParameter> ParseSharedParameterFile(string path)
        {
            var result = new List<TargetParameter>();
            string text = ReadText(path);
            var groups = new Dictionary<string, string>();
            string[] groupHeader = null, paramHeader = null;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] f = line.Split('\t');
                switch (f[0])
                {
                    case "*GROUP": groupHeader = f; break;
                    case "*PARAM": paramHeader = f; break;
                    case "GROUP":
                        if (f.Length >= 3) groups[f[Index(groupHeader, "ID", 1)]] = f[Index(groupHeader, "NAME", 2)];
                        break;
                    case "PARAM":
                    {
                        if (f.Length < 4) break;
                        string Get(string col, int def)
                        {
                            int i = Index(paramHeader, col, def);
                            return i >= 0 && i < f.Length ? f[i] : null;
                        }
                        string groupId = Get("GROUP", 5);
                        result.Add(new TargetParameter
                        {
                            Guid = Get("GUID", 1),
                            Name = Get("NAME", 2),
                            DataType = SharedTypeToType(Get("DATATYPE", 3)),
                            Group = groupId != null && groups.TryGetValue(groupId, out string g) ? g : null,
                            Description = Get("DESCRIPTION", 7),
                            Source = "shared_file"
                        });
                        break;
                    }
                }
            }
            return result.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        }

        private static int Index(string[] header, string col, int fallback)
        {
            if (header == null) return fallback;
            int i = Array.FindIndex(header, h => string.Equals(h?.Trim(), col, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : fallback;
        }

        private static string ReadText(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            // UTF-16 without BOM: every second byte of ASCII text is 0
            if (b.Length > 8 && b[1] == 0 && b[3] == 0) return Encoding.Unicode.GetString(b);
            try
            {
                return new UTF8Encoding(false, true).GetString(b);
            }
            catch (DecoderFallbackException)
            {
#if !(REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023 || REVIT2024)
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
                return Encoding.GetEncoding(1251).GetString(b);
            }
        }

        /// <summary>Shared-file DATATYPE ("LENGTH", "MASS", "autodesk.spec.aec:length-2.0.0" ...) → schema type.</summary>
        public static string SharedTypeToType(string dt)
        {
            string s = (dt ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return "text";
            var m = Regex.Match(s, @"autodesk\.spec(?:\.[a-z]+)*:([a-z0-9]+)");
            if (m.Success) s = m.Groups[1].Value;
            s = s.Replace("_", "");
            if (s.Contains("length") || s == "distance") return "length";
            if (s.Contains("angle")) return "angle";
            if (s == "text" || s == "string" || s == "url" || s.Contains("multilinetext")) return "text";
            if (s == "integer" || s == "int") return "integer";
            if (s == "yesno" || s == "boolean") return "yes_no";
            if (s == "number") return "number";
            if (s == "material") return "material";
            if (s == "mass" || s.Contains("massdensity") == false && s.Contains("mass")) return "mass";
            if (s.Contains("hvacpower") || s.Contains("coolingload") || s.Contains("heatingload")) return "hvac_power";
            if (s.Contains("electricalpower") || s.Contains("apparentpower") || s == "power") return "electrical_power";
            if (s.Contains("potential") || s.Contains("voltage")) return "voltage";
            if (s.Contains("current")) return "current";
            if (s.Contains("frequency")) return "frequency";
            if (s.Contains("airflow")) return "air_flow";
            if (s == "flow" || s.Contains("pipingflow") || s.Contains("pipeflow")) return "pipe_flow";
            if (s.Contains("pressure")) return "pressure";
            if (s.Contains("temperature")) return "temperature";
            if (s.Contains("area")) return "area";
            if (s.Contains("volume")) return "volume";
            return s;
        }

        public static string SpecToType(ForgeTypeId spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return "text";
            if (spec == SpecTypeId.Length) return "length";
            if (spec == SpecTypeId.Angle) return "angle";
            if (spec == SpecTypeId.Number) return "number";
            if (spec == SpecTypeId.Int.Integer) return "integer";
            if (spec == SpecTypeId.Boolean.YesNo) return "yes_no";
            if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.Url) return "text";
            if (spec == SpecTypeId.Reference.Material) return "material";
            if (spec == SpecTypeId.Mass) return "mass";
            if (spec == SpecTypeId.HvacPower) return "hvac_power";
            if (spec == SpecTypeId.ElectricalPower) return "electrical_power";
            if (spec == SpecTypeId.ElectricalPotential) return "voltage";
            if (spec == SpecTypeId.Current) return "current";
            if (spec == SpecTypeId.ElectricalFrequency) return "frequency";
            if (spec == SpecTypeId.AirFlow) return "air_flow";
            if (spec == SpecTypeId.Flow) return "pipe_flow";
            if (spec == SpecTypeId.HvacPressure) return "pressure";
            if (spec == SpecTypeId.HvacTemperature) return "temperature";
            if (spec == SpecTypeId.Area) return "area";
            if (spec == SpecTypeId.Volume) return "volume";
            return SharedTypeToType(spec.TypeId);
        }
    }

    internal static class ElementIdCompat
    {
        public static long IntegerValueCompat(this ElementId id)
        {
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }
    }
}
