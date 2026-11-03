using System.Text.Json.Nodes;

namespace CoRFAGen.Data
{
    /// <summary>
    /// Catalog families: one parametric template + a table of types. Every distinct geometry (row values that drive
    /// geometry) is built once as a "set"; its elements are shown only for the types of that set
    /// (type yes/no parameter "BGS_Vis_NN" = formula on BGS_Set). Elements marked "toggle" (e.g. the actuator)
    /// are additionally controlled by an instance yes/no parameter ("Показать_привод").
    /// Optional: embedded lookup table (size_lookup by DN/PN) and two pipe connectors following L / DN.
    /// </summary>
    public partial class FamilyGenerator
    {
        public GenerationResult ExecuteCatalog(Document doc, Autodesk.Revit.ApplicationServices.Application app, string catalogJson,
            ParameterApplyOptions paramOptions)
        {
            var result = new GenerationResult();
            string json;
            try
            {
                json = PrepareCatalogJson(catalogJson, result);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = "Invalid catalog JSON: " + ex.Message;
                return result;
            }

            using (JsonDocument jsonDoc = JsonDocument.Parse(json))
            {
                JsonElement root = jsonDoc.RootElement;
                FamilyManager fm = doc.FamilyManager;

                foreach (var geom in root.GetProperty("geometry").EnumerateArray())
                {
                    string gt = geom.GetProperty("type").GetString();
                    if (gt != "extrusion" && gt != "sweep" && gt != "blend")
                    {
                        result.ErrorMessage = $"Geometry type '{gt}' is not supported.";
                        return result;
                    }
                }

                string units = root.GetProperty("units").GetString();
                ForgeTypeId unit = units switch
                {
                    "inches" => UnitTypeId.Inches,
                    "feet" => UnitTypeId.Feet,
                    "centimeters" => UnitTypeId.Centimeters,
                    "meters" => UnitTypeId.Meters,
                    _ => UnitTypeId.Millimeters
                };

                List<CatalogType> types = CatalogJson.ReadTypes(root);
                JsonElement cat = root.GetProperty("catalog");
                var columns = ReadColumns(root);
                int setCount = types.Select(t => t.SetIndex).Distinct().Count();
                result.ExpectedGeometryCount = 0; // counted per set after element patterns are expanded
                result.Report.Add($"CATALOG {types.Count} types, {setCount} geometry sets");

                var baseDefaults = new Dictionary<string, double>();
                foreach (var p in root.GetProperty("parameters").EnumerateArray())
                {
                    string n = p.GetProperty("name").GetString().Replace(" ", "_");
                    if (p.TryGetProperty("default_value", out JsonElement dv) && TechnicalParameters.TryGetNumber(dv, out double d))
                        baseDefaults[n] = d;
                }

                bool keepParameters = paramOptions != null && (paramOptions.ReuseExisting || !string.IsNullOrEmpty(paramOptions.SharedParameterFile));
                ClearExistingContent(doc, keepParameters);
                RemoveOldCatalogParameters(doc);
                if (paramOptions != null && !string.IsNullOrEmpty(paramOptions.SharedParameterFile))
                    paramOptions.SharedDefinitions = TechnicalParameters.LoadSharedDefinitions(app, paramOptions.SharedParameterFile, result);

                FailureLog.Reset();
                Action deferredLookup = null;
                using (Transaction trans = new Transaction(doc, "Generate catalog family"))
                {
                    FailureLog.Attach(trans, "generation");
                    trans.Start();
                    try
                    {
                        if (fm.CurrentType == null) fm.NewType(types[0].Name);
                        bool connectors = OptionEnabled(cat, "connectors", false);
                        if (connectors) SetPipeAccessoryCategory(doc, result);

                        // 1. parameters (values of the first type)
                        var familyParams = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
                        foreach (var param in root.GetProperty("parameters").EnumerateArray())
                        {
                            string pn = param.GetProperty("name").GetString();
                            try { TechnicalParameters.TryApply(doc, param, unit, familyParams, result, paramOptions); }
                            catch (Exception ex) { result.Errors.Add($"Parameter '{pn}': {ex.Message}"); }
                        }

                        // catalog columns must be plain type parameters (a reused template parameter may be an instance,
                        // reporting or formula-driven one → its values would be ignored and lookup / connector formulas fail)
                        var columnNames = new HashSet<string>(types.SelectMany(t => t.Values.Keys), StringComparer.OrdinalIgnoreCase);
                        foreach (string cn in columnNames)
                            if (familyParams.TryGetValue(cn, out FamilyParameter cfp)) NormalizeColumnParameter(fm, cfp, result);

                        FamilyParameter setParam = fm.get_Parameter(CatalogJson.SetIndexParameter) ??
                            fm.AddParameter(CatalogJson.SetIndexParameter, GroupTypeId.Visibility, SpecTypeId.Number, false);

                        var toggleParams = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
                        var toggleDefaults = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        if (cat.TryGetProperty("toggles", out JsonElement tgs) && tgs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement tg in tgs.EnumerateArray())
                            {
                                string id = tg.TryGetProperty("id", out JsonElement ide) ? ide.GetString() : null;
                                string pname = tg.TryGetProperty("parameter", out JsonElement pe) ? pe.GetString() : null;
                                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pname)) continue;
                                pname = pname.Trim().Replace(' ', '_');
                                FamilyParameter fp = fm.get_Parameter(pname) ??
                                    fm.AddParameter(pname, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, true);
                                toggleParams[id] = fp;
                                toggleDefaults[id] = !tg.TryGetProperty("default", out JsonElement de) || de.ValueKind != JsonValueKind.False;
                            }
                        }

                        // 2. subcategories
                        var subcategoryMap = new Dictionary<string, Category>();
                        if (root.TryGetProperty("subcategories", out var subcats))
                        {
                            Category parent = doc.OwnerFamily.FamilyCategory;
                            foreach (var sc in subcats.EnumerateArray())
                            {
                                string scName = sc.GetString();
                                try
                                {
                                    subcategoryMap[scName] = parent.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == scName)
                                                             ?? doc.Settings.Categories.NewSubcategory(parent, scName);
                                }
                                catch (Exception ex) { result.Report.Add($"Subcategory '{scName}': {ex.Message}"); }
                            }
                        }

                        // only the template's own planes are used; other sketch planes come from the JSON offsets
                        var refPlaneMap = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);
                        foreach (ReferencePlane rp in new FilteredElementCollector(doc).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                            if (!string.IsNullOrEmpty(rp.Name)) refPlaneMap[rp.Name] = rp;

                        // 3. geometry sets
                        foreach (var set in types.GroupBy(t => t.SetIndex).OrderBy(g => g.Key))
                        {
                            CatalogType rep = set.First();
                            var defaults = new Dictionary<string, double>(baseDefaults);
                            foreach (var kv in rep.Values)
                                if (TechnicalParameters.TryGetNumber(kv.Value, out double d)) defaults[kv.Key.Replace(" ", "_")] = d;

                            result.ExpectedGeometryCount += CountExpanded(root, defaults);
                            var toggled = new Dictionary<ElementId, string>();
                            List<GenericForm> forms = BuildGeometry(doc, app, root, defaults, unit, refPlaneMap, subcategoryMap,
                                result, $"set {set.Key:00} {rep.SetLabel}", toggled);
                            doc.Regenerate();
                            ApplyVoidCuts(doc, result, forms);

                            string visName = $"BGS_Vis_{set.Key:00}";
                            FamilyParameter vis = fm.get_Parameter(visName) ??
                                fm.AddParameter(visName, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, false);
                            // the set index is a number (it may come from the lookup table) → compare with a margin, integers only
                            fm.SetFormula(vis, $"and({CatalogJson.SetIndexParameter} > {set.Key - 1}, {CatalogJson.SetIndexParameter} < {set.Key + 1})");

                            var toggleVis = new Dictionary<string, FamilyParameter>();
                            foreach (GenericForm f in forms)
                            {
                                FamilyParameter target = vis;
                                if (toggled.TryGetValue(f.Id, out string tid) && toggleParams.TryGetValue(tid, out FamilyParameter tparam))
                                {
                                    if (!toggleVis.TryGetValue(tid, out target))
                                    {
                                        string tn = $"BGS_{tid}_{set.Key:00}";
                                        target = fm.get_Parameter(tn) ?? fm.AddParameter(tn, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, true);
                                        fm.SetFormula(target, $"and({visName}, {tparam.Definition.Name})");
                                        toggleVis[tid] = target;
                                    }
                                }
                                try
                                {
                                    Parameter visible = f.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                                    if (visible != null) fm.AssociateElementParameterToFamilyParameter(visible, target);
                                }
                                catch (Exception ex) { result.Report.Add($"  visibility of '{f.Name}': {ex.Message}"); }
                            }
                        }

                        // 4. types
                        FamilyParameter connDia = connectors ? AddHelperLength(fm, "BGS_Conn_D", false) : null;
                        string diaColumn = connectors ? ConnectorOption(cat, "diameter_parameter", "DN") : null;
                        var typeNames = new HashSet<string>(types.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                        FamilyType firstType = null;
                        for (int i = 0; i < types.Count; i++)
                        {
                            CatalogType t = types[i];
                            FamilyType ft = fm.Types.Cast<FamilyType>().FirstOrDefault(x => x.Name == t.Name);
                            if (ft != null) fm.CurrentType = ft;
                            else if (i == 0 && fm.CurrentType != null && !typeNames.Contains(fm.CurrentType.Name))
                            {
                                fm.RenameCurrentType(t.Name);
                                ft = fm.CurrentType;
                            }
                            else ft = fm.NewType(t.Name);
                            fm.CurrentType = ft;
                            if (firstType == null) firstType = ft;

                            foreach (var kv in t.Values)
                            {
                                if (!familyParams.TryGetValue(kv.Key, out FamilyParameter fp)) continue;
                                columns.TryGetValue(kv.Key, out (string type, string unit) col);
                                TechnicalParameters.SetValue(doc, fp, kv.Value, col.unit, col.type ?? "text", unit, result, "type " + t.Name);
                            }
                            fm.Set(setParam, (double)t.SetIndex);
                            if (connDia != null && t.Values.TryGetValue(diaColumn, out JsonElement dnv) && TechnicalParameters.TryGetNumber(dnv, out double dn))
                                fm.Set(connDia, UnitUtils.ConvertToInternalUnits(dn, UnitTypeId.Millimeters));
                            foreach (var tp in toggleParams)
                                try { fm.Set(tp.Value, toggleDefaults[tp.Key] ? 1 : 0); } catch { }

                            try { TechnicalParameters.ComposeDescription(doc, root, result, t.Values); }
                            catch (Exception ex) { result.Report.Add("DESCRIPTION: " + ex.Message); }
                        }

                        // 5. remove types that are not in the catalog
                        foreach (FamilyType old in fm.Types.Cast<FamilyType>().ToList())
                        {
                            if (typeNames.Contains(old.Name)) continue;
                            try { fm.CurrentType = old; fm.DeleteCurrentType(); result.Report.Add($"TYPE removed '{old.Name}'"); }
                            catch (Exception ex) { result.Report.Add($"TYPE '{old.Name}' not removed: {ex.Message}"); }
                        }
                        if (firstType != null) fm.CurrentType = firstType;
                        doc.Regenerate();

                        // 5a. voids have no "Visible" parameter, so the bores of ALL sets would show in the Family Editor
                        //     (also in "Preview Visibility"). They have already cut their own set → hide them in every view.
                        HideVoidsInViews(doc, result);

                        // 6. pipe connectors on both ends (follow L, diameter = DN)
                        if (connectors)
                        {
                            try { CreatePipeConnectors(doc, cat, familyParams, connDia, result); }
                            catch (Exception ex) { result.Report.Add("CONNECTORS failed: " + ex.Message); }
                        }

                        // 7. lookup table: size_lookup(BGS_Catalog, ..., DN, PN) for the numeric columns
                        //    in its own transaction after this one is committed (the imported table is then known to formulas)
                        if (OptionEnabled(cat, "lookup", true))
                            deferredLookup = () =>
                            {
                                ApplyLookupTable(doc, cat, types, familyParams, setParam, connDia, diaColumn, unit, result);
                                if (firstType != null) fm.CurrentType = firstType;
                            };
                        if (firstType != null) fm.CurrentType = firstType;

                        doc.Regenerate();
                        trans.Commit();
                        result.Success = true;
                        result.Report.Add($"TYPES created: {types.Count}");
                    }
                    catch (Exception ex)
                    {
                        if (trans.HasStarted() && !trans.HasEnded()) trans.RollBack();
                        result.ErrorMessage = "Catalog family generation failed: " + ex.Message;
                    }
                }

                if (result.Success && deferredLookup != null)
                {
                    using (Transaction lt = new Transaction(doc, "Catalog lookup table"))
                    {
                        FailureLog.Attach(lt, "lookup table");
                        lt.Start();
                        try
                        {
                            deferredLookup();
                            doc.Regenerate();
                            lt.Commit();
                        }
                        catch (Exception ex)
                        {
                            if (lt.HasStarted() && !lt.HasEnded()) lt.RollBack();
                            result.Report.Add("LOOKUP failed: " + ex.Message);
                        }
                    }
                }
                result.Report.AddRange(FailureLog.Entries);
            }
            result.ErrorCount = result.Errors.Count;
            return result;
        }

        /// <summary>Type parameters = catalog columns: forced to "type", defaults = first row, missing ones added.</summary>
        private static string PrepareCatalogJson(string json, GenerationResult result)
        {
            JsonNode root = JsonNode.Parse(json);
            JsonObject cat = root["catalog"] as JsonObject ?? throw new InvalidOperationException("no \"catalog\" section");
            JsonArray rows = cat["types"] as JsonArray;
            if (rows == null || rows.Count == 0) throw new InvalidOperationException("catalog has no types");
            JsonArray parms = root["parameters"] as JsonArray ?? new JsonArray();
            root["parameters"] = parms;

            var colDefs = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            if (cat["columns"] is JsonArray cols)
                foreach (JsonNode c in cols)
                    if (c is JsonObject co && co["name"] != null) colDefs[co["name"].GetValue<string>()] = co;

            var columnNames = new List<string>(colDefs.Keys);
            foreach (JsonNode r in rows)
                if (r is JsonObject ro)
                    foreach (var kv in ro) if (!columnNames.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) columnNames.Add(kv.Key);

            JsonObject first = (JsonObject)rows[0];
            bool lookup = !(cat["lookup"] is JsonObject lo) || lo["enabled"] == null || lo["enabled"].GetValueKind() != JsonValueKind.False;
            foreach (string col in columnNames)
            {
                JsonObject p = parms.OfType<JsonObject>().FirstOrDefault(x =>
                    string.Equals(x["name"]?.GetValue<string>(), col, StringComparison.OrdinalIgnoreCase));
                if (p == null)
                {
                    colDefs.TryGetValue(col, out JsonObject def);
                    string type = def?["type"]?.GetValue<string>() ?? GuessType(first[col]);
                    p = new JsonObject
                    {
                        ["name"] = col,
                        ["type"] = type,
                        ["group"] = def?["group"]?.GetValue<string>() ?? (type == "length" ? "Dimensions" : type == "text" ? "Identity Data" : "Mechanical")
                    };
                    if (def?["unit"] != null) p["unit"] = def["unit"].DeepClone();
                    parms.Add(p);
                }
                p["instance_or_type"] = "type";
                if (lookup && p["type"]?.GetValue<string>() == "integer" && string.IsNullOrEmpty(p["builtin"]?.GetValue<string>()))
                    p["type"] = "number"; // size_lookup returns numbers
                if (first.TryGetPropertyValue(col, out JsonNode fv) && fv != null) p["default_value"] = fv.DeepClone();
                else if (p["default_value"] == null) p["default_value"] = "";
            }
            return root.ToJsonString();
        }

        /// <summary>Number of geometry elements after element-level patterns are expanded (studs ×8, pillars ×2 …).</summary>
        private static int CountExpanded(JsonElement root, Dictionary<string, double> defaults)
        {
            int n = 0;
            foreach (JsonElement g in root.GetProperty("geometry").EnumerateArray())
                n += ExpandPattern(g, defaults).Count();
            return n;
        }

        private static void NormalizeColumnParameter(FamilyManager fm, FamilyParameter fp, GenerationResult result)
        {
            string n = fp.Definition.Name;
            try
            {
                if (!string.IsNullOrEmpty(fp.Formula))
                {
                    result.Report.Add($"PARAM {n}: formula '{fp.Formula}' removed — values come from the catalog table");
                    fm.SetFormula(fp, null);
                }
            }
            catch (Exception ex) { result.Report.Add($"PARAM {n}: formula not removed: {ex.Message}"); }
            try { if (fp.IsReporting) { fm.MakeNonReporting(fp); result.Report.Add($"PARAM {n}: reporting → normal"); } }
            catch (Exception ex) { result.Report.Add($"PARAM {n}: still reporting: {ex.Message}"); }
            try { if (fp.IsInstance) { fm.MakeType(fp); result.Report.Add($"PARAM {n}: instance → type"); } }
            catch (Exception ex) { result.Report.Add($"PARAM {n}: still instance: {ex.Message}"); }
            if (fp.IsReadOnly) result.Report.Add($"PARAM {n}: read-only in this family — catalog values cannot be written");
        }

        private static string GuessType(JsonNode v)
        {
            if (v is JsonValue jv && (jv.TryGetValue(out double _) || jv.TryGetValue(out int _) || jv.TryGetValue(out long _))) return "number";
            return "text";
        }

        private static Dictionary<string, (string type, string unit)> ReadColumns(JsonElement root)
        {
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in root.GetProperty("parameters").EnumerateArray())
            {
                string n = p.GetProperty("name").GetString();
                string t = p.TryGetProperty("type", out JsonElement te) ? te.GetString() : "text";
                string u = p.TryGetProperty("unit", out JsonElement ue) && ue.ValueKind == JsonValueKind.String ? ue.GetString() : null;
                map[n] = (t, u);
            }
            return map;
        }

        /// <summary>Hides every void form in all model views of the family (cuts stay; "Reveal Hidden Elements" shows them).</summary>
        private static void HideVoidsInViews(Document doc, GenerationResult result)
        {
            var voids = new FilteredElementCollector(doc).OfClass(typeof(GenericForm)).Cast<GenericForm>()
                .Where(f => !f.IsSolid).Select(f => f.Id).ToList();
            if (voids.Count == 0) return;
            int views = 0;
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v.IsTemplate) continue;
                if (v.ViewType != ViewType.ThreeD && v.ViewType != ViewType.FloorPlan && v.ViewType != ViewType.CeilingPlan &&
                    v.ViewType != ViewType.Elevation && v.ViewType != ViewType.Section) continue;
                try
                {
                    var ids = voids.Where(id => doc.GetElement(id)?.CanBeHidden(v) == true).ToList();
                    if (ids.Count == 0) continue;
                    v.HideElements(ids);
                    views++;
                }
                catch (Exception ex) { result.Report.Add($"  voids not hidden in '{v.Name}': {ex.Message}"); }
            }
            result.Report.Add($"VOIDS hidden: {voids.Count} in {views} view(s)");
        }

        /// <summary>Visibility helper parameters of a previous catalog generation (formulas reference deleted elements).</summary>
        private static void RemoveOldCatalogParameters(Document doc)
        {
            FamilyManager fm = doc.FamilyManager;
            var old = fm.Parameters.Cast<FamilyParameter>()
                .Where(p => p.Definition != null && p.Definition.Name.StartsWith("BGS_", StringComparison.Ordinal)).ToList();
            // table columns driven by size_lookup(BGS_Catalog, ...) of a previous generation
            var lookupDriven = fm.Parameters.Cast<FamilyParameter>()
                .Where(p => !string.IsNullOrEmpty(p.Formula) && p.Formula.IndexOf("BGS_", StringComparison.Ordinal) >= 0).ToList();
            if (old.Count == 0 && lookupDriven.Count == 0) return;
            using (var t = new Transaction(doc, "Remove old catalog visibility parameters"))
            {
                t.Start();
                // formulas first, then the parameters (dependent ones before the set index)
                foreach (FamilyParameter p in lookupDriven.Concat(old)) { try { if (!string.IsNullOrEmpty(p.Formula)) fm.SetFormula(p, null); } catch { } }
                foreach (FamilyParameter p in old.OrderBy(p => p.Definition.Name == CatalogJson.SetIndexParameter ? 1 : 0))
                    try { fm.RemoveParameter(p); } catch { }
                t.Commit();
            }
        }
    }
}
