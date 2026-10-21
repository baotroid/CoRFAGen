using System.Globalization;

namespace BeGenSt.Data
{
    /// <summary>
    /// Creates family parameters from the JSON definition: geometric parameters, text parameters,
    /// technical data with units (mass, power, voltage, air flow ...) and values for Revit built-in
    /// parameters (Model, Manufacturer, Description ...).
    /// </summary>
    /// <summary>How generated parameters are mapped onto existing / shared parameters.</summary>
    public class ParameterApplyOptions
    {
        /// <summary>Keep the parameters of the family/template and fill them instead of creating duplicates.</summary>
        public bool ReuseExisting { get; set; }
        /// <summary>Shared parameter file (ФОП) whose definitions are added to the family on demand.</summary>
        public string SharedParameterFile { get; set; }

        internal Dictionary<string, ExternalDefinition> SharedDefinitions { get; set; }
    }

    internal static class TechnicalParameters
    {
        /// <summary>
        /// Loads the definitions of a shared parameter file. Revit's current shared-parameter file setting is
        /// restored afterwards, so the user's own configuration is not changed.
        /// </summary>
        public static Dictionary<string, ExternalDefinition> LoadSharedDefinitions(
            Autodesk.Revit.ApplicationServices.Application app, string path, GenerationResult result)
        {
            var defs = new Dictionary<string, ExternalDefinition>(StringComparer.OrdinalIgnoreCase);
            if (app == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return defs;
            string previous = app.SharedParametersFilename;
            try
            {
                app.SharedParametersFilename = path;
                DefinitionFile file = app.OpenSharedParameterFile();
                if (file == null)
                {
                    result.Report.Add($"SHARED FILE could not be opened: {path}");
                    return defs;
                }
                foreach (DefinitionGroup g in file.Groups)
                    foreach (Definition d in g.Definitions)
                        if (d is ExternalDefinition ed && !defs.ContainsKey(ed.Name))
                            defs[ed.Name] = ed;
                result.Report.Add($"SHARED FILE {Path.GetFileName(path)}: {defs.Count} definitions");
            }
            catch (Exception ex)
            {
                result.Report.Add($"SHARED FILE error: {ex.Message}");
            }
            finally
            {
                try { app.SharedParametersFilename = previous ?? ""; } catch { }
            }
            return defs;
        }

        public static bool TryApply(Document doc, JsonElement param, ForgeTypeId lengthUnit,
            Dictionary<string, FamilyParameter> familyParams, GenerationResult result)
            => TryApply(doc, param, lengthUnit, familyParams, result, null);

        /// <summary>Returns true when the parameter was handled (created, reused or written into a built-in).</summary>
        public static bool TryApply(Document doc, JsonElement param, ForgeTypeId lengthUnit,
            Dictionary<string, FamilyParameter> familyParams, GenerationResult result, ParameterApplyOptions options)
        {
            FamilyManager fm = doc.FamilyManager;
            string name = param.GetProperty("name").GetString();
            string type = GetString(param, "type") ?? "text";
            string group = GetString(param, "group") ?? "Other";
            string unit = GetString(param, "unit");
            bool isInstance = GetString(param, "instance_or_type") == "instance";
            param.TryGetProperty("default_value", out JsonElement dv);

            // 0a. Parameter that already exists in the family / template → fill it, never duplicate
            FamilyParameter existingParam = fm.get_Parameter(name);
            if (existingParam != null && existingParam.Id.IntegerValueCompat() > 0)
            {
                SetValue(doc, existingParam, dv, unit, type, lengthUnit, result, "existing");
                familyParams[name] = existingParam;
                result.ParameterCount++;
                return true;
            }

            // 0b. Definition from the shared parameter file (ФОП) → add as shared parameter
            if (options?.SharedDefinitions != null && options.SharedDefinitions.TryGetValue(name, out ExternalDefinition ext))
            {
                try
                {
                    FamilyParameter sp = fm.AddParameter(ext, MapGroup(group), isInstance);
                    SetValue(doc, sp, dv, unit, type, lengthUnit, result, "shared");
                    familyParams[name] = sp;
                    result.ParameterCount++;
                    return true;
                }
                catch (Exception ex)
                {
                    result.Report.Add($"PARAM shared {name}: could not add ({ex.Message}), creating a family parameter instead");
                }
            }

            // 1. Built-in Revit parameter (Model, Manufacturer, ...)
            BuiltInParameter? bip = ResolveBuiltIn(GetString(param, "builtin") ?? name);
            if (bip.HasValue)
            {
                FamilyParameter fp = fm.get_Parameter(bip.Value);
                if (fp != null)
                {
                    string text = ValueAsText(dv, unit);
                    if (fp.StorageType == StorageType.Double && TryGetNumber(dv, out double num))
                        fm.Set(fp, num);
                    else if (fp.StorageType == StorageType.String)
                        fm.Set(fp, text);
                    familyParams[name] = fp;
                    result.ParameterCount++;
                    result.Report.Add($"PARAM built-in {bip.Value} = {text}");
                    return true;
                }
            }

            // 2. Technical data with a physical unit
            if (TechSpecs.TryGetValue(type, out ForgeTypeId techSpec))
            {
                ForgeTypeId techUnit = ResolveUnit(type, unit);
                if (techUnit != null && TryGetNumber(dv, out double value))
                {
                    try
                    {
                        FamilyParameter fp = fm.AddParameter(name, MapGroup(group), techSpec, isInstance);
                        fm.Set(fp, UnitUtils.ConvertToInternalUnits(value, techUnit));
                        familyParams[name] = fp;
                        result.ParameterCount++;
                        result.Report.Add($"PARAM {type} {name} = {value} {unit}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        result.Report.Add($"PARAM {name}: typed parameter failed ({ex.Message}), stored as text");
                        // a parameter created before Set() failed must be removed before the text fallback
                        FamilyParameter existing = fm.get_Parameter(name);
                        if (existing != null) try { fm.RemoveParameter(existing); } catch { }
                    }
                }

                // unknown unit / non-numeric value: keep the information as text
                FamilyParameter tp = fm.AddParameter(name, MapGroup(group), SpecTypeId.String.Text, isInstance);
                fm.Set(tp, ValueAsText(dv, unit));
                familyParams[name] = tp;
                result.ParameterCount++;
                result.Report.Add($"PARAM text {name} = {ValueAsText(dv, unit)}");
                return true;
            }

            // 3. Regular parameters
            FamilyParameter p = fm.AddParameter(name, MapGroup(group), MapSpec(type), isInstance);
            switch (type)
            {
                case "length":
                    if (TryGetNumber(dv, out double len))
                        fm.Set(p, UnitUtils.ConvertToInternalUnits(len, lengthUnit));
                    break;
                case "angle":
                    if (TryGetNumber(dv, out double ang))
                        fm.Set(p, ang * Math.PI / 180.0);
                    break;
                case "number":
                    if (TryGetNumber(dv, out double n)) fm.Set(p, n);
                    break;
                case "integer":
                    if (TryGetNumber(dv, out double i)) fm.Set(p, (int)Math.Round(i));
                    break;
                case "yes_no":
                    fm.Set(p, IsTrue(dv) ? 1 : 0);
                    break;
                case "text":
                    fm.Set(p, ValueAsText(dv, unit));
                    break;
            }
            familyParams[name] = p;
            result.ParameterCount++;
            return true;
        }

        /// <summary>
        /// Fills the family "Description" (Описание) with the item name ("Наименование") followed by the technical
        /// characteristics found in the sources, e.g. "Циклон ЦН-15-400. Масса: 120 кг; Производительность: 2500 м3/ч".
        /// </summary>
        public static void ComposeDescription(Document doc, JsonElement root, GenerationResult result)
            => ComposeDescription(doc, root, result, null);

        /// <param name="overrides">Per-type values (catalog generation) that replace the parameters' default_value.</param>
        public static void ComposeDescription(Document doc, JsonElement root, GenerationResult result,
            Dictionary<string, JsonElement> overrides)
        {
            FamilyManager fm = doc.FamilyManager;
            if (fm.CurrentType == null || !root.TryGetProperty("parameters", out JsonElement parms)) return;

            string Norm(string x) => System.Text.RegularExpressions.Regex.Replace((x ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
            bool IsNameParam(string n)
            {
                string k = Norm(n);
                return k.Contains("наименование") || k == "name" || k == "title" || k == "product name" || k.EndsWith(" name");
            }
            bool IsDescriptionParam(string n)
            {
                string k = Norm(n);
                return k == "описание" || k == "description" || k.EndsWith(" описание") || k.EndsWith(" description");
            }

            string itemName = null;
            var specs = new List<string>();
            foreach (JsonElement p in parms.EnumerateArray())
            {
                string name = GetString(p, "name");
                string type = GetString(p, "type") ?? "text";
                string group = GetString(p, "group") ?? "";
                string unit = GetString(p, "unit");
                string builtin = GetString(p, "builtin");
                p.TryGetProperty("default_value", out JsonElement dv);
                if (overrides != null && name != null && overrides.TryGetValue(name, out JsonElement ov)) dv = ov;
                if (string.IsNullOrWhiteSpace(name) || IsDescriptionParam(name) || IsDescriptionParam(builtin)) continue;

                string text = ValueAsText(dv, unit);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (itemName == null && type == "text" && IsNameParam(name)) { itemName = text; continue; }

                bool technical = TechSpecs.ContainsKey(type) ||
                                 ((type == "number" || type == "integer") && !string.IsNullOrWhiteSpace(unit)) ||
                                 (type == "text" && (group == "Mechanical" || group == "Electrical"));
                if (technical)
                {
                    // "ADSK_Масса" → "Масса"
                    string label = System.Text.RegularExpressions.Regex.Replace(name, @"^[A-Za-z]{2,8}_", "").Replace('_', ' ');
                    specs.Add($"{label}: {text}");
                }
            }

            if (itemName == null && root.TryGetProperty("metadata", out JsonElement meta))
                itemName = GetString(meta, "name");
            if (string.IsNullOrWhiteSpace(itemName) && specs.Count == 0) return;

            string description = (itemName ?? "").Trim();
            if (specs.Count > 0)
                description = (description.Length > 0 ? description.TrimEnd('.') + ". " : "") + string.Join("; ", specs);

            int written = 0;
            FamilyParameter bip = fm.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);
            if (bip != null) { try { fm.Set(bip, description); written++; } catch { } }
            foreach (FamilyParameter fp in fm.Parameters)
            {
                if (fp.Id.IntegerValueCompat() < 0 || fp.StorageType != StorageType.String) continue;
                if (!IsDescriptionParam(fp.Definition.Name) || fp.IsReadOnly || !string.IsNullOrEmpty(fp.Formula)) continue;
                try { fm.Set(fp, description); written++; } catch { }
            }
            result.Report.Add($"DESCRIPTION ({written} parameter(s)) = {description}");
        }

        /// <summary>Writes a JSON value into an existing (or just added shared) parameter, converting units by its spec.</summary>
        internal static void SetValue(Document doc, FamilyParameter fp, JsonElement dv, string unit, string type,
            ForgeTypeId lengthUnit, GenerationResult result, string kind)
        {
            FamilyManager fm = doc.FamilyManager;
            string name = fp.Definition.Name;
            try
            {
                if (dv.ValueKind == JsonValueKind.Undefined || dv.ValueKind == JsonValueKind.Null)
                {
                    result.Report.Add($"PARAM {kind} {name}: kept (no value)");
                    return;
                }
                if (fp.IsReadOnly || !string.IsNullOrEmpty(fp.Formula))
                {
                    result.Report.Add($"PARAM {kind} {name}: kept (read-only or driven by formula)");
                    return;
                }
                if (fm.CurrentType == null) return;

                switch (fp.StorageType)
                {
                    case StorageType.String:
                        fm.Set(fp, ValueAsText(dv, unit));
                        break;
                    case StorageType.Integer:
                        if (fp.Definition.GetDataType() == SpecTypeId.Boolean.YesNo) fm.Set(fp, IsTrue(dv) ? 1 : 0);
                        else if (TryGetNumber(dv, out double iv)) fm.Set(fp, (int)Math.Round(iv));
                        break;
                    case StorageType.Double:
                        if (!TryGetNumber(dv, out double v))
                        {
                            result.Report.Add($"PARAM {kind} {name}: value '{ValueAsText(dv, unit)}' is not a number — skipped");
                            return;
                        }
                        fm.Set(fp, ToInternal(doc, fp.Definition.GetDataType(), v, unit, type, lengthUnit));
                        break;
                    default:
                        result.Report.Add($"PARAM {kind} {name}: {fp.StorageType} parameter left unchanged");
                        return;
                }
                result.Report.Add($"PARAM {kind} {name} = {ValueAsText(dv, unit)}");
            }
            catch (Exception ex)
            {
                result.Report.Add($"PARAM {kind} {name}: value not set ({ex.Message})");
            }
        }

        private static double ToInternal(Document doc, ForgeTypeId spec, double value, string unit, string type, ForgeTypeId lengthUnit)
        {
            if (spec == SpecTypeId.Length) return UnitUtils.ConvertToInternalUnits(value, lengthUnit);
            if (spec == SpecTypeId.Angle) return value * Math.PI / 180.0;
            foreach (var kv in TechSpecs)
            {
                if (kv.Value != spec) continue;
                ForgeTypeId u = ResolveUnit(kv.Key, unit);
                if (u != null) return UnitUtils.ConvertToInternalUnits(value, u);
                break;
            }
            try
            {
                if (UnitUtils.IsMeasurableSpec(spec))
                {
                    // assume the value is given in the document's display unit for this spec
                    ForgeTypeId display = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                    return UnitUtils.ConvertToInternalUnits(value, display);
                }
            }
            catch { }
            return value;
        }

        private static readonly Dictionary<string, ForgeTypeId> TechSpecs = new Dictionary<string, ForgeTypeId>
        {
            { "mass", SpecTypeId.Mass },
            { "hvac_power", SpecTypeId.HvacPower },
            { "electrical_power", SpecTypeId.ElectricalPower },
            { "voltage", SpecTypeId.ElectricalPotential },
            { "current", SpecTypeId.Current },
            { "frequency", SpecTypeId.ElectricalFrequency },
            { "air_flow", SpecTypeId.AirFlow },
            { "pipe_flow", SpecTypeId.Flow },
            { "pressure", SpecTypeId.HvacPressure },
            { "temperature", SpecTypeId.HvacTemperature },
        };

        private static ForgeTypeId ResolveUnit(string type, string unit)
        {
            string u = (unit ?? "").Trim().ToLowerInvariant()
                .Replace(" ", "").Replace("³", "3").Replace("·", "").Replace("^", "");
            switch (type)
            {
                case "mass":
                    if (u == "kg" || u == "кг" || u == "") return UnitTypeId.Kilograms;
                    if (u == "t" || u == "т" || u == "tonne" || u == "тонн") return UnitTypeId.Tonnes;
                    break;
                case "hvac_power":
                case "electrical_power":
                    if (u == "kw" || u == "квт") return UnitTypeId.Kilowatts;
                    if (u == "w" || u == "вт" || u == "") return UnitTypeId.Watts;
                    break;
                case "voltage":
                    if (u == "v" || u == "в" || u == "") return UnitTypeId.Volts;
                    if (u == "kv" || u == "кв") return UnitTypeId.Kilovolts;
                    break;
                case "current":
                    if (u == "a" || u == "а" || u == "") return UnitTypeId.Amperes;
                    break;
                case "frequency":
                    if (u == "hz" || u == "гц" || u == "") return UnitTypeId.Hertz;
                    break;
                case "air_flow":
                case "pipe_flow":
                    if (u == "m3/h" || u == "м3/ч" || u == "m3/hr" || u == "м3/час") return UnitTypeId.CubicMetersPerHour;
                    if (u == "m3/s" || u == "м3/с") return UnitTypeId.CubicMetersPerSecond;
                    if (u == "l/s" || u == "л/с") return UnitTypeId.LitersPerSecond;
                    if (u == "l/min" || u == "л/мин") return UnitTypeId.LitersPerMinute;
                    break;
                case "pressure":
                    if (u == "pa" || u == "па" || u == "") return UnitTypeId.Pascals;
                    if (u == "kpa" || u == "кпа") return UnitTypeId.Kilopascals;
                    if (u == "bar" || u == "бар") return UnitTypeId.Bars;
                    if (u == "mpa" || u == "мпа") return UnitTypeId.Megapascals;
                    break;
                case "temperature":
                    if (u == "c" || u == "°c" || u == "°с" || u == "с" || u == "") return UnitTypeId.Celsius;
                    break;
            }
            return null;
        }

        private static BuiltInParameter? ResolveBuiltIn(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "model": case "модель": return BuiltInParameter.ALL_MODEL_MODEL;
                case "manufacturer": case "изготовитель": case "производитель": return BuiltInParameter.ALL_MODEL_MANUFACTURER;
                case "description": case "описание": return BuiltInParameter.ALL_MODEL_DESCRIPTION;
                case "type comments": case "комментарии к типоразмеру": return BuiltInParameter.ALL_MODEL_TYPE_COMMENTS;
                case "url": return BuiltInParameter.ALL_MODEL_URL;
                case "cost": case "стоимость": return BuiltInParameter.ALL_MODEL_COST;
                case "keynote": case "ключевая пометка": return BuiltInParameter.KEYNOTE_PARAM;
#if !(REVIT2026 || REVIT2027)
                case "assembly code": case "код по классификатору": return BuiltInParameter.UNIFORMAT_CODE;
#endif
                default: return null;
            }
        }

        private static ForgeTypeId MapSpec(string type)
        {
            switch (type)
            {
                case "length": return SpecTypeId.Length;
                case "angle": return SpecTypeId.Angle;
                case "number": return SpecTypeId.Number;
                case "integer": return SpecTypeId.Int.Integer;
                case "yes_no": return SpecTypeId.Boolean.YesNo;
                case "material": return SpecTypeId.Reference.Material;
                default: return SpecTypeId.String.Text;
            }
        }

        private static ForgeTypeId MapGroup(string group)
        {
            switch (group)
            {
                case "Dimensions": return GroupTypeId.Geometry;
                case "Construction": return GroupTypeId.Construction;
                case "Identity Data": return GroupTypeId.IdentityData;
                case "Graphics": return GroupTypeId.Graphics;
                case "Materials and Finishes": return GroupTypeId.Materials;
                case "Mechanical": return GroupTypeId.Mechanical;
                case "Electrical": return GroupTypeId.Electrical;
                default: return GroupTypeId.General;
            }
        }

        private static string GetString(JsonElement e, string prop)
        {
            return e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        public static bool TryGetNumber(JsonElement v, out double value)
        {
            value = 0;
            if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out value);
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = (v.GetString() ?? "").Trim().Replace(',', '.');
                // take the leading number of strings like "1 200 кг" or "5.5 kW"
                var m = System.Text.RegularExpressions.Regex.Match(s.Replace(" ", ""), @"^-?\d+(\.\d+)?");
                return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
            if (v.ValueKind == JsonValueKind.True) { value = 1; return true; }
            if (v.ValueKind == JsonValueKind.False) { value = 0; return true; }
            return false;
        }

        private static bool IsTrue(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.Number) return v.TryGetDouble(out double d) && Math.Abs(d) > 0.5;
            if (v.ValueKind == JsonValueKind.String)
            {
                string s = (v.GetString() ?? "").Trim().ToLowerInvariant();
                return s == "true" || s == "yes" || s == "1" || s == "да";
            }
            return false;
        }

        private static string ValueAsText(JsonElement v, string unit)
        {
            string s;
            switch (v.ValueKind)
            {
                case JsonValueKind.String: s = v.GetString(); break;
                case JsonValueKind.Number: s = v.GetDouble().ToString("G", CultureInfo.InvariantCulture); break;
                case JsonValueKind.True: s = "Yes"; break;
                case JsonValueKind.False: s = "No"; break;
                case JsonValueKind.Undefined: case JsonValueKind.Null: s = ""; break;
                default: s = v.GetRawText(); break;
            }
            if (!string.IsNullOrWhiteSpace(unit) && !string.IsNullOrEmpty(s) && !s.EndsWith(unit))
                s += " " + unit;
            return s ?? "";
        }
    }
}
