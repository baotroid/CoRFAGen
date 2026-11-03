using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB.Plumbing;

namespace CoRFAGen.Data
{
    /// <summary>
    /// Catalog families, MEP part: pipe connectors on both ends (they follow L and DN), Pipe Accessories category,
    /// and an embedded lookup table (size_lookup by the key columns, e.g. DN + PN).
    /// </summary>
    public partial class FamilyGenerator
    {
        // ------------------------------------------------------------------ options

        /// <summary>catalog.&lt;name&gt;.enabled; when the section is missing → <paramref name="whenMissing"/>.</summary>
        private static bool OptionEnabled(JsonElement cat, string name, bool whenMissing)
        {
            if (!cat.TryGetProperty(name, out JsonElement o) || o.ValueKind != JsonValueKind.Object) return whenMissing;
            return !o.TryGetProperty("enabled", out JsonElement e) || e.ValueKind != JsonValueKind.False;
        }

        private static string ConnectorOption(JsonElement cat, string key, string fallback)
        {
            if (cat.TryGetProperty("connectors", out JsonElement c) && c.ValueKind == JsonValueKind.Object &&
                c.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString().Trim();
            return fallback;
        }

        private static List<string> LookupKeys(JsonElement cat)
        {
            var keys = new List<string>();
            if (cat.TryGetProperty("lookup", out JsonElement l) && l.ValueKind == JsonValueKind.Object &&
                l.TryGetProperty("keys", out JsonElement k) && k.ValueKind == JsonValueKind.Array)
                foreach (JsonElement x in k.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())) keys.Add(x.GetString().Trim());
            if (keys.Count == 0) keys.AddRange(new[] { "DN", "PN" });
            return keys;
        }

        private static List<string> LookupKeys(JsonObject cat)
        {
            using (JsonDocument d = JsonDocument.Parse(cat.ToJsonString()))
                return LookupKeys(d.RootElement);
        }

        private static FamilyParameter AddHelperLength(FamilyManager fm, string name, bool instance)
        {
            return fm.get_Parameter(name) ?? fm.AddParameter(name, GroupTypeId.Constraints, SpecTypeId.Length, instance);
        }

        // ------------------------------------------------------------------ category

        /// <summary>Generic Model → Pipe Accessories (connectors of a valve need an MEP category).</summary>
        private static void SetPipeAccessoryCategory(Document doc, GenerationResult result)
        {
            try
            {
                Family fam = doc.OwnerFamily;
                Category current = fam.FamilyCategory;
                if (current != null && current.Id.IntegerValueCompat() == (int)BuiltInCategory.OST_PipeAccessory) return;
                if (current != null && current.Id.IntegerValueCompat() != (int)BuiltInCategory.OST_GenericModel)
                {
                    result.Report.Add($"CATEGORY kept '{current.Name}' (only Generic Model is switched to Pipe Accessories)");
                    return;
                }
                Category pa = Category.GetCategory(doc, BuiltInCategory.OST_PipeAccessory);
                if (pa == null) return;
                fam.FamilyCategory = pa;
                result.Report.Add("CATEGORY Generic Model → " + pa.Name);
            }
            catch (Exception ex) { result.Report.Add("CATEGORY not changed: " + ex.Message); }
        }

        private static void SetValvePartType(Document doc, GenerationResult result)
        {
            try
            {
                Parameter pt = doc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                if (pt == null || pt.IsReadOnly) return;
                if (Enum.TryParse("ValveBreaksInto", out PartType v))
                {
                    pt.Set((int)v);
                    result.Report.Add("PART TYPE = Valve - Breaks Into");
                }
            }
            catch (Exception ex) { result.Report.Add("PART TYPE not set: " + ex.Message); }
        }

        // ------------------------------------------------------------------ connectors

        /// <summary>
        /// Two pipe connectors on the end faces of the valve (pipe axis = X through the origin).
        /// Each sits on a hidden thin disc whose start/end are driven by L (formulas L/2 - L/100 and L/2),
        /// the connector diameter is associated to BGS_Conn_D (= DN in mm per type, or from the lookup table).
        /// </summary>
        private static void CreatePipeConnectors(Document doc, JsonElement cat, Dictionary<string, FamilyParameter> familyParams,
            FamilyParameter connDia, GenerationResult result)
        {
            FamilyManager fm = doc.FamilyManager;
            string lenName = ConnectorOption(cat, "length_parameter", "L");
            if (!familyParams.TryGetValue(lenName, out FamilyParameter lp) && (lp = fm.get_Parameter(lenName)) == null)
            {
                result.Report.Add($"CONNECTORS skipped: length parameter '{lenName}' not found");
                return;
            }
            string L = lp.Definition.Name;
            double? lval = fm.CurrentType?.AsDouble(lp);
            if (lval == null || lval.Value <= 0)
            {
                result.Report.Add($"CONNECTORS skipped: {L} = 0 in the current type");
                return;
            }

            FamilyParameter pStart = AddHelperLength(fm, "BGS_Conn_Start", false);
            FamilyParameter pEnd = AddHelperLength(fm, "BGS_Conn_End", false);
            try
            {
                fm.SetFormula(pStart, $"{L} / 2 - {L} / 100");
                fm.SetFormula(pEnd, $"{L} / 2");
            }
            catch (Exception ex)
            {
                // e.g. L is an instance / reporting parameter of the template → plain values per type
                result.Report.Add($"  connector offsets: formula on '{L}' rejected ({ex.Message}) → values per type" +
                                  $" [{L}: {(lp.IsInstance ? "instance" : "type")}{(lp.IsReporting ? ", reporting" : "")}" +
                                  $"{(string.IsNullOrEmpty(lp.Formula) ? "" : ", formula '" + lp.Formula + "'")}]");
                try { fm.SetFormula(pStart, null); fm.SetFormula(pEnd, null); } catch { }
                FamilyType keep = fm.CurrentType;
                foreach (FamilyType ft in fm.Types)
                {
                    double? l = ft.AsDouble(lp);
                    if (l == null || l.Value <= 0) continue;
                    fm.CurrentType = ft;
                    fm.Set(pStart, l.Value / 2 - l.Value / 100);
                    fm.Set(pEnd, l.Value / 2);
                }
                fm.CurrentType = keep;
            }
            doc.Regenerate();

            PipeSystemType system = Enum.TryParse(ConnectorOption(cat, "system", "Global"), true, out PipeSystemType st) ? st : PipeSystemType.Fitting;
            double rFeet = Math.Max(UnitUtils.ConvertToInternalUnits(5, UnitTypeId.Millimeters),
                                    (connDia != null ? fm.CurrentType.AsDouble(connDia) ?? 0 : 0) / 2);

            var made = new List<ConnectorElement>();
            foreach (int side in new[] { -1, 1 })
            {
                XYZ n = XYZ.BasisX * side;
                SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(n, XYZ.Zero));
                var loop = new CurveArray();
                loop.Append(Arc.Create(XYZ.Zero, rFeet, 0, Math.PI, XYZ.BasisY, XYZ.BasisZ));
                loop.Append(Arc.Create(XYZ.Zero, rFeet, Math.PI, 2 * Math.PI, XYZ.BasisY, XYZ.BasisZ));
                var profile = new CurveArrArray();
                profile.Append(loop);
                Extrusion disc = doc.FamilyCreate.NewExtrusion(true, profile, sp, lval.Value / 2);
                disc.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM)?.Set(lval.Value / 2 - lval.Value / 100);
                fm.AssociateElementParameterToFamilyParameter(disc.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), pEnd);
                fm.AssociateElementParameterToFamilyParameter(disc.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM), pStart);
                doc.Regenerate();

                PlanarFace face = FindFace(disc, n);
                if (face?.Reference == null)
                {
                    result.Report.Add($"CONNECTOR {(side < 0 ? "left" : "right")}: end face not found");
                    continue;
                }
                ConnectorElement ce = ConnectorElement.CreatePipeConnector(doc, system, face.Reference);
                Parameter dia = ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER);
                if (dia != null && connDia != null)
                {
                    try { fm.AssociateElementParameterToFamilyParameter(dia, connDia); }
                    catch (Exception ex) { result.Report.Add("  connector diameter not associated: " + ex.Message); }
                }
                try { disc.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)?.Set(0); } catch { }
                FailureLog.Names[disc.Id.IntegerValueCompat()] = $"connector disc {(side < 0 ? "left" : "right")}";
                try { if (disc.Sketch != null) FailureLog.Names[disc.Sketch.Id.IntegerValueCompat()] = $"connector disc {(side < 0 ? "left" : "right")} (sketch)"; } catch { }
                FailureLog.Names[ce.Id.IntegerValueCompat()] = $"pipe connector {(side < 0 ? "left" : "right")}";
                made.Add(ce);
                result.Report.Add($"CONNECTOR {(side < 0 ? "left" : "right")}: pipe, {system}, at ±{L}/2, Ø = {connDia?.Definition.Name}");
            }

            if (made.Count == 2)
            {
                try
                {
                    typeof(ConnectorElement).GetMethod("SetLinkedConnectorElement")?.Invoke(made[0], new object[] { made[1] });
                    typeof(ConnectorElement).GetMethod("SetLinkedConnectorElement")?.Invoke(made[1], new object[] { made[0] });
                }
                catch (Exception ex) { result.Report.Add("  connectors not linked: " + (ex.InnerException ?? ex).Message); }
                SetValvePartType(doc, result);
            }
        }

        private static PlanarFace FindFace(Element e, XYZ normal)
        {
            var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
            GeometryElement ge = e.get_Geometry(opt);
            if (ge == null) return null;
            foreach (GeometryObject go in ge)
                if (go is Solid s && s.Faces.Size > 0)
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.IsAlmostEqualTo(normal)) return pf;
            return null;
        }

        // ------------------------------------------------------------------ lookup table

        private class LookupColumn
        {
            public string Header;          // ASCII column id in the CSV (K1, V3, S, CD)
            public FamilyParameter Param;
            public bool IsLength;
            public Func<CatalogType, double?> Value; // value in the CSV units (mm for lengths)
        }

        /// <summary>
        /// Writes BGS_Catalog.csv (keys + numeric columns), imports it into the family and drives the numeric type
        /// parameters with size_lookup(BGS_Catalog, "Vi", default, keys...). Every formula is checked against all types;
        /// a parameter whose lookup does not reproduce the table falls back to plain per-type values.
        /// </summary>
        private static void ApplyLookupTable(Document doc, JsonElement cat, List<CatalogType> types,
            Dictionary<string, FamilyParameter> familyParams, FamilyParameter setParam, FamilyParameter connDia, string diaColumn,
            ForgeTypeId unit, GenerationResult result)
        {
            FamilyManager fm = doc.FamilyManager;

            // keys
            var keys = new List<LookupColumn>();
            foreach (string k in LookupKeys(cat))
            {
                if (!familyParams.TryGetValue(k, out FamilyParameter fp)) continue;
                ForgeTypeId spec = fp.Definition.GetDataType();
                bool isLen = spec == SpecTypeId.Length;
                if (!isLen && spec != SpecTypeId.Number && spec != SpecTypeId.Int.Integer)
                {
                    result.Report.Add($"LOOKUP key '{k}' skipped (type {spec?.TypeId})");
                    continue;
                }
                string key = k;
                keys.Add(new LookupColumn { Header = "K" + (keys.Count + 1), Param = fp, IsLength = isLen, Value = t => Number(t, key, isLen, unit) });
            }
            if (keys.Count == 0) { result.Report.Add("LOOKUP skipped: no key columns (DN/PN)"); return; }
            if (types.Any(t => keys.Any(k => k.Value(t) == null))) { result.Report.Add("LOOKUP skipped: a type has no key value"); return; }
            var tuples = types.Select(t => string.Join("|", keys.Select(k => k.Value(t).Value.ToString("R", CultureInfo.InvariantCulture)))).ToList();
            if (tuples.Distinct().Count() != tuples.Count) { result.Report.Add("LOOKUP skipped: key values are not unique per type"); return; }

            // value columns
            var cols = new List<LookupColumn>();
            var keyParams = new HashSet<FamilyParameter>(keys.Select(k => k.Param));
            foreach (var kv in familyParams)
            {
                FamilyParameter fp = kv.Value;
                if (keyParams.Contains(fp) || fp.IsInstance || fp.StorageType != StorageType.Double || fp.IsDeterminedByFormula) continue;
                ForgeTypeId spec = fp.Definition.GetDataType();
                bool isLen = spec == SpecTypeId.Length;
                if (!isLen && spec != SpecTypeId.Number) continue;
                string col = kv.Key;
                if (!types.All(t => t.Values.ContainsKey(col))) continue;
                if (types.Any(t => Number(t, col, isLen, unit) == null)) continue;
                if (cols.Any(c => c.Param.Id == fp.Id)) continue;
                cols.Add(new LookupColumn { Header = "V" + (cols.Count + 1), Param = fp, IsLength = isLen, Value = t => Number(t, col, isLen, unit) });
            }
            cols.Add(new LookupColumn { Header = "S", Param = setParam, IsLength = false, Value = t => t.SetIndex });
            if (connDia != null && !string.IsNullOrEmpty(diaColumn))
                cols.Add(new LookupColumn { Header = "CD", Param = connDia, IsLength = true, Value = t => Number(t, diaColumn, false, unit) });

            // CSV (ASCII headers, invariant numbers)
            string folder = Path.Combine(Path.GetTempPath(), "CoRFAGen");
            Directory.CreateDirectory(folder);
            string csv = Path.Combine(folder, CatalogJson.LookupTableName + ".csv");
            var sb = new StringBuilder();
            sb.Append(string.Join(",", new[] { "" }.Concat(keys.Concat(cols).Select(Header))));
            sb.Append("\r\n");
            for (int i = 0; i < types.Count; i++)
            {
                CatalogType t = types[i];
                sb.Append("R" + (i + 1));
                foreach (LookupColumn c in keys.Concat(cols))
                    sb.Append(",").Append((c.Value(t) ?? 0).ToString("0.######", CultureInfo.InvariantCulture));
                sb.Append("\r\n");
            }
            File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));

            FamilySizeTableManager.CreateFamilySizeTableManager(doc, doc.OwnerFamily.Id);
            FamilySizeTableManager mgr = FamilySizeTableManager.GetFamilySizeTableManager(doc, doc.OwnerFamily.Id);
            if (mgr == null) { result.Report.Add("LOOKUP skipped: size table manager not available"); return; }
            if (mgr.HasSizeTable(CatalogJson.LookupTableName)) mgr.RemoveSizeTable(CatalogJson.LookupTableName);
            var err = new FamilySizeTableErrorInfo();
            if (!mgr.ImportSizeTable(doc, csv, err))
            {
                // property names differ between Revit versions → report whatever the error info carries
                string details = string.Join(", ", err.GetType().GetProperties()
                    .Select(pi => { try { return pi.Name + "=" + pi.GetValue(err); } catch { return null; } })
                    .Where(x => x != null));
                result.Report.Add($"LOOKUP import failed: {details}");
                return;
            }
            result.Report.Add($"LOOKUP table '{CatalogJson.LookupTableName}' imported: {types.Count} rows, keys {string.Join(", ", keys.Select(k => k.Param.Definition.Name))}" +
                              $" (tables in the family: {string.Join(", ", mgr.GetAllSizeTableNames())})");
            foreach (LookupColumn k in keys)
                result.Report.Add($"  key '{k.Param.Definition.Name}': {k.Param.Definition.GetDataType()?.TypeId}, {(k.Param.IsInstance ? "instance" : "type")}");

            FamilyParameter zero = AddHelperLength(fm, "BGS_Zero", false);
            string args = string.Join(", ", keys.Select(k => k.Param.Definition.Name));
            var applied = new List<LookupColumn>();
            foreach (LookupColumn c in cols)
            {
                // variants: the first one is the intended formula; the others narrow down why Revit refuses it
                string tn = CatalogJson.LookupTableName;
                var variants = new List<string>
                {
                    $"size_lookup({tn}, \"{c.Header}\", {(c.IsLength ? zero.Definition.Name : "0")}, {args})",
                    $"size_lookup({tn}, \"{c.Header}\", 0, {args})",
                    $"size_lookup({tn},\"{c.Header}\",{(c.IsLength ? zero.Definition.Name : "0")},{args.Replace(" ", "")})",
                };
                bool ok = false;
                var errors = new List<string>();
                foreach (string f in variants.Distinct())
                {
                    try
                    {
                        fm.SetFormula(c.Param, f);
                        applied.Add(c);
                        ok = true;
                        if (f != variants[0]) result.Report.Add($"  lookup '{c.Param.Definition.Name}': accepted variant {f}");
                        break;
                    }
                    catch (Exception ex) { errors.Add($"{f} → {ex.Message.Split('\n')[0].Trim()}"); }
                }
                if (!ok)
                    result.Report.Add($"  lookup formula for '{c.Param.Definition.Name}' rejected: " + string.Join(" | ", errors));
            }

            // check every type; parameters whose lookup does not match fall back to values
            var bad = new HashSet<LookupColumn>();
            var byName = fm.Types.Cast<FamilyType>().ToDictionary(x => x.Name, x => x);
            foreach (CatalogType t in types)
            {
                if (!byName.TryGetValue(t.Name, out FamilyType ft)) continue;
                fm.CurrentType = ft;
                doc.Regenerate();
                foreach (LookupColumn c in applied)
                {
                    double expected = c.IsLength ? UnitUtils.ConvertToInternalUnits(c.Value(t) ?? 0, UnitTypeId.Millimeters) : (c.Value(t) ?? 0);
                    double actual = ft.AsDouble(c.Param) ?? double.NaN;
                    if (double.IsNaN(actual) || Math.Abs(actual - expected) > 1e-6 * Math.Max(1, Math.Abs(expected))) bad.Add(c);
                }
            }
            foreach (LookupColumn c in bad)
            {
                try
                {
                    fm.SetFormula(c.Param, null);
                    foreach (CatalogType t in types)
                    {
                        if (!byName.TryGetValue(t.Name, out FamilyType ft)) continue;
                        fm.CurrentType = ft;
                        double v = c.IsLength ? UnitUtils.ConvertToInternalUnits(c.Value(t) ?? 0, UnitTypeId.Millimeters) : (c.Value(t) ?? 0);
                        fm.Set(c.Param, v);
                    }
                    result.Report.Add($"  lookup for '{c.Param.Definition.Name}' did not match the table → plain type values");
                }
                catch (Exception ex) { result.Report.Add($"  could not restore '{c.Param.Definition.Name}': {ex.Message}"); }
            }
            result.Report.Add($"LOOKUP formulas: {applied.Count - bad.Count} of {cols.Count} parameters driven by size_lookup({args})");
        }

        private static string Header(LookupColumn c) =>
            c.Header + (c.IsLength ? "##length##millimeters" : "##number##general");

        /// <summary>Numeric value of a column for a type; lengths converted to millimetres.</summary>
        private static double? Number(CatalogType t, string column, bool isLength, ForgeTypeId unit)
        {
            if (!t.Values.TryGetValue(column, out JsonElement v) || !TechnicalParameters.TryGetNumber(v, out double d)) return null;
            if (!isLength) return d;
            return UnitUtils.ConvertFromInternalUnits(UnitUtils.ConvertToInternalUnits(d, unit), UnitTypeId.Millimeters);
        }
    }
}
