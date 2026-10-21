using System.Data;
using System.Globalization;

namespace BeGenSt.Data
{
    public class GenerationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int ParameterCount { get; set; }
        public int RefPlaneCount { get; set; }
        public int GeometryCount { get; set; }
        public int ErrorCount { get; set; }
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>Per-element log (OK / FAIL) written to the Logs folder after generation.</summary>
        public List<string> Report { get; set; } = new List<string>();
        public int ExpectedGeometryCount { get; set; }
    }

    public partial class FamilyGenerator
    {
        private static readonly HashSet<string> KnownBuiltInParams = new HashSet<string>
        {
            "Family Name", "Keynote", "Assembly Code", "Type Comments", "Model",
            "Description", "Type IFC Predefined Type", "Type Image",
            "Export Type to IFC As", "Default Elevation", "Cost", "URL", "Manufacturer"
        };

        internal static readonly string[] BuiltInRefPlaneNames =
        {
            "Center Left/Right",
            "Center (Left/Right)",
            "Center Front/Back",
            "Center (Front/Back)",
            "Ref. Level",
            "Ref Level",
            "Reference Plane",
            "Ceiling",
            "Exterior",
            "Interior",
            "Sill",
            "Wall Closure",
            // Russian templates
            "Центр (слева/справа)",
            "Центр (спереди/сзади)",
            "Центр (Слева/Справа)",
            "Центр (Спереди/Сзади)",
            "Опорный уровень",
            "Опорная плоскость"
        };

        /// <summary>English template plane names used by the JSON → names of the same planes in Russian templates.</summary>
        private static readonly Dictionary<string, string[]> PlaneAliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Center (Left/Right)", new[] { "Центр (слева/справа)", "Центр (Слева/Справа)" } },
            { "Center Left/Right", new[] { "Центр (слева/справа)", "Центр (Слева/Справа)" } },
            { "Center (Front/Back)", new[] { "Центр (спереди/сзади)", "Центр (Спереди/Сзади)" } },
            { "Center Front/Back", new[] { "Центр (спереди/сзади)", "Центр (Спереди/Сзади)" } },
            { "Ref. Level", new[] { "Опорный уровень" } },
            { "Ref Level", new[] { "Опорный уровень" } },
        };

        public GenerationResult Execute(Document doc, Autodesk.Revit.ApplicationServices.Application app, string familyJson)
            => Execute(doc, app, familyJson, null);

        public GenerationResult Execute(Document doc, Autodesk.Revit.ApplicationServices.Application app, string familyJson,
            ParameterApplyOptions paramOptions)
        {
            // family with a table of types (TypeGen)
            if (CatalogJson.IsCatalog(familyJson))
                return ExecuteCatalog(doc, app, familyJson, paramOptions);

            var result = new GenerationResult();

            JsonDocument jsonDoc;
            try
            {
                jsonDoc = JsonDocument.Parse(familyJson);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Failed to parse JSON: {ex.Message}";
                return result;
            }

            var root = jsonDoc.RootElement;

            string schemaVersion = root.GetProperty("metadata").GetProperty("schema_version").GetString();
            if (schemaVersion != "0.1")
            {
                result.ErrorMessage = $"Unsupported schema version: {schemaVersion}. Expected 0.1.";
                return result;
            }

            string units = root.GetProperty("units").GetString();

            foreach (var geom in root.GetProperty("geometry").EnumerateArray())
            {
                result.ExpectedGeometryCount++;
                string geomType = geom.GetProperty("type").GetString();
                if (geomType != "extrusion" && geomType != "sweep" && geomType != "blend")
                {
                    result.ErrorMessage = $"Geometry type '{geomType}' is not supported. Only extrusions, sweeps, and blends are supported.";
                    return result;
                }
            }

            ForgeTypeId sourceUnitType = units switch
            {
                "inches" => UnitTypeId.Inches,
                "feet" => UnitTypeId.Feet,
                "millimeters" => UnitTypeId.Millimeters,
                "centimeters" => UnitTypeId.Centimeters,
                "meters" => UnitTypeId.Meters,
                _ => UnitTypeId.Inches
            };

            var paramDefaults = new Dictionary<string, double>();
            foreach (var param in root.GetProperty("parameters").EnumerateArray())
            {
                string paramName = param.GetProperty("name").GetString().Replace(" ", "_");
                string paramType = param.GetProperty("type").GetString();

                if ((paramType == "length" || paramType == "number" || paramType == "angle" || paramType == "integer") &&
                    param.TryGetProperty("default_value", out JsonElement dvEl) &&
                    TechnicalParameters.TryGetNumber(dvEl, out double defaultVal))
                {
                    paramDefaults[paramName] = defaultVal;
                }
            }

            bool keepParameters = paramOptions != null && (paramOptions.ReuseExisting || !string.IsNullOrEmpty(paramOptions.SharedParameterFile));
            ClearExistingContent(doc, keepParameters);
            if (paramOptions != null && !string.IsNullOrEmpty(paramOptions.SharedParameterFile))
                paramOptions.SharedDefinitions = TechnicalParameters.LoadSharedDefinitions(app, paramOptions.SharedParameterFile, result);

            using (Transaction trans = new Transaction(doc, "Generate Family from JSON"))
            {
                trans.Start();

                try
                {
                    // Step 1: Parameters
                    if (doc.FamilyManager.CurrentType == null)
                    {
                        string typeName = "Default";
                        if (root.TryGetProperty("metadata", out var meta) &&
                            meta.TryGetProperty("name", out var nameEl))
                        {
                            string n = nameEl.GetString()?.Trim();
                            if (!string.IsNullOrEmpty(n)) typeName = n;
                        }
                        doc.FamilyManager.NewType(typeName);
                    }

                    // The family type is named after the family name entered in the add-in
                    if (root.TryGetProperty("metadata", out var metaT) && metaT.TryGetProperty("name", out var famNameEl))
                    {
                        string famName = famNameEl.GetString()?.Trim();
                        FamilyType cur = doc.FamilyManager.CurrentType;
                        if (!string.IsNullOrEmpty(famName) && cur != null && cur.Name != famName)
                        {
                            try
                            {
                                doc.FamilyManager.RenameCurrentType(famName);
                                result.Report.Add($"TYPE renamed to '{famName}'");
                            }
                            catch (Exception ex)
                            {
                                result.Report.Add($"TYPE could not be renamed to '{famName}': {ex.Message}");
                            }
                        }
                    }

                    var familyParams = new Dictionary<string, FamilyParameter>();
                    foreach (var param in root.GetProperty("parameters").EnumerateArray())
                    {
                        string paramName = param.GetProperty("name").GetString();
                        try
                        {
                            if (TechnicalParameters.TryApply(doc, param, sourceUnitType, familyParams, result, paramOptions))
                                continue;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error creating parameter '{paramName}': {ex.Message}");
                            result.Errors.Add($"Parameter '{paramName}': {ex.Message}");
                        }
                    }

                    // Description = name + technical characteristics from the sources
                    try { TechnicalParameters.ComposeDescription(doc, root, result); }
                    catch (Exception ex) { result.Report.Add("DESCRIPTION not composed: " + ex.Message); }

                    doc.Regenerate();

                    // Step 2: Reference Planes
                    View activeView = doc.ActiveView;
                    var refPlaneMap = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);

                    foreach (ReferencePlane rp in new FilteredElementCollector(doc)
                        .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                    {
                        if (!string.IsNullOrEmpty(rp.Name))
                            refPlaneMap[rp.Name] = rp;
                    }
                    // Russian template: let the English names used in the JSON find the template's own planes
                    foreach (var alias in PlaneAliases)
                    {
                        if (refPlaneMap.ContainsKey(alias.Key)) continue;
                        foreach (string ru in alias.Value)
                            if (refPlaneMap.TryGetValue(ru, out ReferencePlane rup)) { refPlaneMap[alias.Key] = rup; break; }
                    }

                    foreach (var plane in root.GetProperty("reference_planes").EnumerateArray())
                    {
                        string planeName = plane.GetProperty("name").GetString();
                        string direction = plane.GetProperty("direction").GetString();
                        string offsetExpr = GetExpressionString(plane.GetProperty("offset"));

                        double offsetValue = EvaluateExpression(offsetExpr, paramDefaults);
                        double offsetFeet = UnitUtils.ConvertToInternalUnits(offsetValue, sourceUnitType);

                        XYZ bubbleEnd, freeEnd, cutVector;
                        switch (direction)
                        {
                            case "x":
                                bubbleEnd = new XYZ(offsetFeet, 0, 0);
                                freeEnd = new XYZ(offsetFeet, 0, 4);
                                cutVector = new XYZ(0, 1, 0);
                                break;
                            case "y":
                                bubbleEnd = new XYZ(0, offsetFeet, 0);
                                freeEnd = new XYZ(0, offsetFeet, 4);
                                cutVector = new XYZ(1, 0, 0);
                                break;
                            case "z":
                                bubbleEnd = new XYZ(0, 0, offsetFeet);
                                freeEnd = new XYZ(4, 0, offsetFeet);
                                cutVector = new XYZ(0, 1, 0);
                                break;
                            default:
                                continue;
                        }

                        if (refPlaneMap.ContainsKey(planeName))
                        {
                            // Reuse the existing template plane — just count it
                            result.RefPlaneCount++;
                        }
                        else
                        {
                            try
                            {
                                ReferencePlane newPlane = doc.FamilyCreate.NewReferencePlane(
                                    bubbleEnd, freeEnd, cutVector, activeView);
                                newPlane.Name = planeName;
                                refPlaneMap[planeName] = newPlane;
                                result.RefPlaneCount++;
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error creating ref plane '{planeName}': {ex.Message}");
                                result.Errors.Add($"Ref plane '{planeName}': {ex.Message}");
                            }
                        }
                    }

                    doc.Regenerate();

                    // Step 3: Subcategories
                    var subcategoryMap = new Dictionary<string, Category>();
                    if (root.TryGetProperty("subcategories", out var subcatsElement))
                    {
                        Category parentCategory = doc.OwnerFamily.FamilyCategory;
                        foreach (var subcat in subcatsElement.EnumerateArray())
                        {
                            string subcatName = subcat.GetString();
                            try
                            {
                                Category existing = parentCategory.SubCategories
                                    .Cast<Category>()
                                    .FirstOrDefault(c => c.Name == subcatName);

                                subcategoryMap[subcatName] = existing ??
                                    doc.Settings.Categories.NewSubcategory(parentCategory, subcatName);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Subcategory '{subcatName}': {ex.Message}");
                            }
                        }
                    }

                    // Step 4: Geometry
                    BuildGeometry(doc, app, root, paramDefaults, sourceUnitType, refPlaneMap, subcategoryMap, result, null);

                    doc.Regenerate();

                    // Step 5: make every void cut the solids it intersects
                    int cuts = ApplyVoidCuts(doc, result);
                    if (cuts > 0) result.Report.Add($"Void cuts applied: {cuts}");
                    trans.Commit();
                    result.Success = true;
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    result.ErrorMessage = $"Family generation failed: {ex.Message}";
                }
            }

            result.ErrorCount = result.Errors.Count;
            return result;
        }


        /// <summary>
        /// Builds every geometry element of the JSON with the given parameter values.
        /// Returns the created forms (used by the catalog builder to group them into visibility sets).
        /// <paramref name="toggleFilter"/>: null = all elements; otherwise called per element to decide.
        /// </summary>
        private static List<GenericForm> BuildGeometry(Document doc, Autodesk.Revit.ApplicationServices.Application app,
            JsonElement root, Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, ReferencePlane> refPlaneMap, Dictionary<string, Category> subcategoryMap,
            GenerationResult result, string setLabel, Dictionary<ElementId, string> toggles = null)
        {
            var created = new List<GenericForm>();
            string prefix = setLabel == null ? "" : "[" + setLabel + "] ";
            foreach (var geomSrc in root.GetProperty("geometry").EnumerateArray())
            foreach (var geom in ExpandPattern(geomSrc, paramDefaults))
            {
                string geomName = geom.GetProperty("name").GetString();
                string geomType = geom.GetProperty("type").GetString();
                string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

                refPlaneMap.TryGetValue(sketchPlaneName, out ReferencePlane sketchRefPlane);
                if (sketchRefPlane == null && !PlaneDefinedInJson(sketchPlaneName, root))
                {
                    result.Errors.Add($"'{geomName}': sketch plane '{sketchPlaneName}' not found");
                    result.Report.Add(prefix + $"FAIL  {geomName}: sketch plane '{sketchPlaneName}' not found");
                    continue;
                }
                // If Revit could not create the reference plane, the geometry is still built
                // from the plane definition in the JSON (direction + offset).
                string planeDirection = GetPlaneDirection(sketchPlaneName, root);

                bool ok = false;
                string error = null;
                var before = new HashSet<ElementId>(new FilteredElementCollector(doc).OfClass(typeof(GenericForm)).ToElementIds());

                if (geomType == "extrusion")
                {
                    ok = CreateExtrusion(doc, geom, sketchRefPlane, planeDirection,
                        root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                }
                else if (geomType == "sweep")
                {
                    ok = CreateSweep(doc, app, geom, sketchRefPlane, planeDirection,
                        root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                }
                else if (geomType == "blend")
                {
                    ok = CreateBlend(doc, app, geom, sketchRefPlane, planeDirection,
                        root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                }

                bool isVoidGeom = geom.TryGetProperty("is_void", out var iv) && iv.ValueKind == JsonValueKind.True;
                var newForms = new FilteredElementCollector(doc).OfClass(typeof(GenericForm)).Cast<GenericForm>()
                    .Where(f => !before.Contains(f.Id)).ToList();
                created.AddRange(newForms);
                foreach (GenericForm f in newForms)
                {
                    FailureLog.Names[f.Id.IntegerValueCompat()] = prefix + geomName;
                    try { if (f is Extrusion ex && ex.Sketch != null) FailureLog.Names[ex.Sketch.Id.IntegerValueCompat()] = prefix + geomName + " (sketch)"; } catch { }
                }
                if (toggles != null && geom.TryGetProperty("toggle", out var tg) && tg.ValueKind == JsonValueKind.String)
                    foreach (GenericForm f in newForms) toggles[f.Id] = tg.GetString();
                if (ok)
                {
                    result.GeometryCount++;
                    result.Report.Add(prefix + $"OK    {geomType,-9} {(isVoidGeom ? "void " : "solid")} {geomName}" +
                              (error != null ? $"  ({error})" : ""));
                    error = null;
                }
                else
                {
                    string msg = error ?? "not created (unknown reason)";
                    result.Errors.Add($"'{geomName}': {msg}");
                    result.Report.Add(prefix + $"FAIL  {geomType,-9} {(isVoidGeom ? "void " : "solid")} {geomName}: {msg}");
                }
            }

            return created;
        }

        /// <summary>
        /// Element-level polar array: "pattern": { "count": "4", "radius": "0.8*R", "start_angle": "45", "center": {u,v}? }
        /// copies the element (circle / rectangle profiles; blends: both profiles) around the pattern centre.
        /// </summary>
        private static IEnumerable<JsonElement> ExpandPattern(JsonElement geom, Dictionary<string, double> defaults)
        {
            if (!geom.TryGetProperty("pattern", out JsonElement pat) || pat.ValueKind != JsonValueKind.Object)
            {
                yield return geom;
                yield break;
            }
            int count = (int)Math.Round(EvaluateExpression(GetExpressionString(pat.GetProperty("count")), defaults));
            double r = EvaluateExpression(GetExpressionString(pat.GetProperty("radius")), defaults);
            double a0 = pat.TryGetProperty("start_angle", out JsonElement sa) ? EvaluateExpression(GetExpressionString(sa), defaults) : 0;
            double pu = 0, pv = 0;
            if (pat.TryGetProperty("center", out JsonElement pc))
            {
                pu = EvaluateExpression(GetExpressionString(pc.GetProperty("u")), defaults);
                pv = EvaluateExpression(GetExpressionString(pc.GetProperty("v")), defaults);
            }
            count = Math.Max(1, Math.Min(count, 64));
            for (int k = 0; k < count; k++)
            {
                double ang = (a0 + 360.0 * k / count) * Math.PI / 180.0;
                double du = pu + r * Math.Cos(ang), dv = pv + r * Math.Sin(ang);
                var node = System.Text.Json.Nodes.JsonNode.Parse(geom.GetRawText()).AsObject();
                node.Remove("pattern");
                node["name"] = (node["name"]?.GetValue<string>() ?? "element") + " #" + (k + 1);
                foreach (string key in new[] { "profile", "bottom_profile", "top_profile" })
                {
                    if (!(node[key] is System.Text.Json.Nodes.JsonObject prof)) continue;
                    string pointKey = prof["center"] != null ? "center" : prof["origin"] != null ? "origin" : null;
                    if (pointKey == null) continue;
                    var pt = (System.Text.Json.Nodes.JsonObject)prof[pointKey];
                    pt["u"] = "(" + GetNodeExpr(pt["u"]) + ") + (" + Math.Round(du, 6).ToString("0.######", CultureInfo.InvariantCulture) + ")";
                    pt["v"] = "(" + GetNodeExpr(pt["v"]) + ") + (" + Math.Round(dv, 6).ToString("0.######", CultureInfo.InvariantCulture) + ")";
                }
                using (JsonDocument d = JsonDocument.Parse(node.ToJsonString()))
                    yield return d.RootElement.Clone();
            }
        }

        private static string GetNodeExpr(System.Text.Json.Nodes.JsonNode n)
        {
            if (n == null) return "0";
            if (n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string s)) return s;
            return n.ToJsonString();
        }

        private void ClearExistingContent(Document doc, bool keepParameters = false)
        {
            var existingExtrusions = new FilteredElementCollector(doc)
                .OfClass(typeof(Extrusion)).ToElements();
            var existingSweeps = new FilteredElementCollector(doc)
                .OfClass(typeof(Sweep)).ToElements();
            var existingBlends = new FilteredElementCollector(doc)
                .OfClass(typeof(Blend)).ToElements();
            var existingRevolutions = new FilteredElementCollector(doc)
                .OfClass(typeof(Revolution)).ToElements();
            var existingRefPlanes = new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                .Where(rp => !BuiltInRefPlaneNames.Contains(rp.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            bool needsClear = existingExtrusions.Count > 0 || existingSweeps.Count > 0
                || existingBlends.Count > 0 || existingRevolutions.Count > 0 || existingRefPlanes.Count > 0;

            if (!needsClear) return;

            using (Transaction t = new Transaction(doc, "Clear existing family content"))
            {
                t.Start();

                foreach (var sw in existingSweeps)
                    try { doc.Delete(sw.Id); } catch { }

                foreach (var bl in existingBlends)
                    try { doc.Delete(bl.Id); } catch { }

                foreach (var rv in existingRevolutions)
                    try { doc.Delete(rv.Id); } catch { }

                foreach (var ext in existingExtrusions)
                    try { doc.Delete(ext.Id); } catch { }

                foreach (var rp in existingRefPlanes)
                    try { doc.Delete(rp.Id); } catch { }

                var paramsToDelete = new List<FamilyParameter>();
                // with "use family / shared parameters" the template's parameters are kept and refilled
                if (!keepParameters)
                foreach (FamilyParameter fp in doc.FamilyManager.Parameters)
                {
                    if (fp.IsReadOnly || KnownBuiltInParams.Contains(fp.Definition.Name))
                        continue;
                    paramsToDelete.Add(fp);
                }
                foreach (var fp in paramsToDelete)
                    try { doc.FamilyManager.RemoveParameter(fp); } catch { }

                doc.Regenerate();
                t.Commit();
            }
        }

        // ==================== GEOMETRY CREATORS ====================

        private static bool CreateExtrusion(
            Document doc, JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            string geomName = geom.GetProperty("name").GetString();
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

            SketchPlane sketchPlane;
            try
            {
                XYZ planeOrigin = PlaneOriginFeet(planeDirection,
                    GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType));
                XYZ expectedNormal = planeDirection switch
                {
                    "x" => XYZ.BasisX, "y" => XYZ.BasisY, "z" => XYZ.BasisZ, _ => XYZ.BasisZ
                };
                Plane plane = Plane.CreateByNormalAndOrigin(expectedNormal, planeOrigin);
                sketchPlane = SketchPlane.Create(doc, plane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane from '{sketchPlaneName}': {ex.Message}";
                return false;
            }

            var profile = geom.GetProperty("profile");
            CurveArrArray curveArrArray = new CurveArrArray();
            CurveArray curveArray = new CurveArray();

            string shape = profile.GetProperty("shape").GetString();
            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);

            try
            {
                switch (shape)
                {
                    case "rectangle":
                        BuildRectangleProfile(profile, planeDirection, paramDefaults, sourceUnitType, curveArray, planeOffsetFeet);
                        break;
                    case "circle":
                        BuildCircleProfile(profile, planeDirection, paramDefaults, sourceUnitType, curveArray, planeOffsetFeet);
                        break;
                    case "custom":
                        BuildCustomProfile(profile, planeDirection, paramDefaults, sourceUnitType, curveArray, planeOffsetFeet);
                        break;
                    default:
                        error = $"Profile shape '{shape}' not supported.";
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Profile error: {ex.Message}";
                return false;
            }

            curveArrArray.Append(curveArray);

            // Inner loops: holes (bore, bolt holes as a polar pattern) cut in the same sketch
            if (geom.TryGetProperty("holes", out JsonElement holes) && holes.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    foreach (CurveArray loop in BuildHoleLoops(holes, planeDirection, paramDefaults, sourceUnitType, planeOffsetFeet))
                        curveArrArray.Append(loop);
                }
                catch (Exception ex)
                {
                    error = "holes skipped: " + ex.Message;
                }
            }

            string startExpr = GetExpressionString(geom.GetProperty("extrusion_start"));
            string endExpr = GetExpressionString(geom.GetProperty("extrusion_end"));
            double startValue = EvaluateExpression(startExpr, paramDefaults);
            double endValue = EvaluateExpression(endExpr, paramDefaults);

            double startFeet = UnitUtils.ConvertToInternalUnits(startValue, sourceUnitType);
            double endFeet = UnitUtils.ConvertToInternalUnits(endValue, sourceUnitType);

            double creationEnd = Math.Abs(endFeet - startFeet);
            if (creationEnd < 0.001) creationEnd = 0.01;

            try
            {
                Extrusion extrusion = doc.FamilyCreate.NewExtrusion(
                    !isVoid, curveArrArray, sketchPlane, creationEnd);
                extrusion.StartOffset = startFeet;
                extrusion.EndOffset = endFeet;

                if (geom.TryGetProperty("subcategory", out var subcatProp))
                {
                    string subcatName = subcatProp.GetString();
                    if (subcategoryMap.ContainsKey(subcatName))
                        extrusion.Subcategory = subcategoryMap[subcatName];
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Extrusion failed: {ex.Message}";
                return false;
            }
        }

        private static bool CreateSweep(
            Document doc, Autodesk.Revit.ApplicationServices.Application app,
            JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            string geomName = geom.GetProperty("name").GetString();
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

            SketchPlane pathSketchPlane;
            try
            {
                XYZ planeOrigin = PlaneOriginFeet(planeDirection,
                    GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType));
                XYZ expectedNormal = planeDirection switch
                {
                    "x" => XYZ.BasisX, "y" => XYZ.BasisY, "z" => XYZ.BasisZ, _ => XYZ.BasisZ
                };
                Plane plane = Plane.CreateByNormalAndOrigin(expectedNormal, planeOrigin);
                pathSketchPlane = SketchPlane.Create(doc, plane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane for sweep path: {ex.Message}";
                return false;
            }

            var pathProp = geom.GetProperty("path");
            var segments = pathProp.GetProperty("segments");
            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);

            var pathCurves3D = new List<Curve>();
            Curve previousCurve = null;

            foreach (var seg in segments.EnumerateArray())
            {
                string segType = seg.GetProperty("segment_type").GetString();
                try
                {
                    if (segType == "line")
                    {
                        var startPt = seg.GetProperty("start");
                        var endPt = seg.GetProperty("end");
                        double su = EvaluateExpression(GetExpressionString(startPt.GetProperty("u")), paramDefaults);
                        double sv = EvaluateExpression(GetExpressionString(startPt.GetProperty("v")), paramDefaults);
                        double eu = EvaluateExpression(GetExpressionString(endPt.GetProperty("u")), paramDefaults);
                        double ev = EvaluateExpression(GetExpressionString(endPt.GetProperty("v")), paramDefaults);

                        XYZ start3D = UVToXYZ(su, sv, planeDirection, sourceUnitType, planeOffsetFeet);
                        XYZ end3D = UVToXYZ(eu, ev, planeDirection, sourceUnitType, planeOffsetFeet);

                        Line line = Line.CreateBound(start3D, end3D);
                        pathCurves3D.Add(line);
                        previousCurve = line;
                    }
                    else if (segType == "arc")
                    {
                        var startPt = seg.GetProperty("start");
                        var endPt = seg.GetProperty("end");
                        var centerPt = seg.GetProperty("center");

                        double su = EvaluateExpression(GetExpressionString(startPt.GetProperty("u")), paramDefaults);
                        double sv = EvaluateExpression(GetExpressionString(startPt.GetProperty("v")), paramDefaults);
                        double eu = EvaluateExpression(GetExpressionString(endPt.GetProperty("u")), paramDefaults);
                        double ev = EvaluateExpression(GetExpressionString(endPt.GetProperty("v")), paramDefaults);
                        double cu = EvaluateExpression(GetExpressionString(centerPt.GetProperty("u")), paramDefaults);
                        double cv = EvaluateExpression(GetExpressionString(centerPt.GetProperty("v")), paramDefaults);

                        double inTanU = 0, inTanV = 0;
                        if (previousCurve != null)
                        {
                            XYZ tangent3D = previousCurve.ComputeDerivatives(1.0, true).BasisX.Normalize();
                            ExtractUV(tangent3D, planeDirection, out inTanU, out inTanV);
                        }
                        else
                        {
                            double dsu = su - cu, dsv = sv - cv;
                            inTanU = -dsv;
                            inTanV = dsu;
                        }

                        XYZ passThroughPoint = ComputeArcPassThrough(
                            su, sv, eu, ev, cu, cv, inTanU, inTanV,
                            planeDirection, sourceUnitType, planeOffsetFeet);

                        XYZ start3D = UVToXYZ(su, sv, planeDirection, sourceUnitType, planeOffsetFeet);
                        XYZ end3D = UVToXYZ(eu, ev, planeDirection, sourceUnitType, planeOffsetFeet);

                        Arc arc = Arc.Create(start3D, end3D, passThroughPoint);
                        pathCurves3D.Add(arc);
                        previousCurve = arc;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error building path segment: {ex.Message}");
                }
            }

            if (pathCurves3D.Count == 0)
            {
                error = "No valid path curves for sweep.";
                return false;
            }

            var modelCurves = new List<ModelCurve>();
            try
            {
                foreach (var curve in pathCurves3D)
                    modelCurves.Add(doc.FamilyCreate.NewModelCurve(curve, pathSketchPlane));
                doc.Regenerate();
            }
            catch (Exception ex)
            {
                error = $"Error creating model curves: {ex.Message}";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }

            ReferenceArray pathRefArray = new ReferenceArray();
            bool refsOk = true;
            foreach (var mc in modelCurves)
            {
                try
                {
                    Reference curveRef = mc.GeometryCurve.Reference;
                    if (curveRef != null)
                    {
                        pathRefArray.Append(curveRef);
                    }
                    else
                    {
                        Options opts = new Options { ComputeReferences = true };
                        GeometryElement geomElem = mc.get_Geometry(opts);
                        bool found = false;
                        foreach (GeometryObject geomObj in geomElem)
                        {
                            if (geomObj is Curve c && c.Reference != null)
                            {
                                pathRefArray.Append(c.Reference);
                                found = true;
                                break;
                            }
                        }
                        if (!found) refsOk = false;
                    }
                }
                catch { refsOk = false; }
            }

            if (!refsOk || pathRefArray.Size == 0)
            {
                error = "Could not collect path references for sweep.";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }

            var profileProp = geom.GetProperty("profile");
            string profileShape = profileProp.GetProperty("shape").GetString();

            CurveArrArray sweepProfileArray = new CurveArrArray();
            CurveArray sweepProfileCurves = new CurveArray();

            try
            {
                Curve firstPathCurve = pathCurves3D[0];
                XYZ pathStartPoint = firstPathCurve.GetEndPoint(0);
                XYZ pathTangent = firstPathCurve.ComputeDerivatives(0, true).BasisX.Normalize();

                XYZ sketchNormal = planeDirection switch
                {
                    "x" => XYZ.BasisX, "y" => XYZ.BasisY, "z" => XYZ.BasisZ, _ => XYZ.BasisZ
                };

                XYZ profileU = sketchNormal;
                XYZ profileV = pathTangent.CrossProduct(profileU).Normalize();

                if (profileV.GetLength() < 0.001)
                {
                    profileV = XYZ.BasisZ;
                    if (Math.Abs(profileV.DotProduct(pathTangent)) > 0.99)
                        profileV = XYZ.BasisY;
                    profileV = pathTangent.CrossProduct(profileU);
                    if (profileV.GetLength() < 0.001)
                        profileV = XYZ.BasisZ;
                    else
                        profileV = profileV.Normalize();
                }

                switch (profileShape)
                {
                    case "rectangle":
                        BuildSweepRectangleProfile(profileProp, paramDefaults, sourceUnitType,
                            pathStartPoint, profileU, profileV, sweepProfileCurves);
                        break;
                    case "circle":
                        BuildSweepCircleProfile(profileProp, paramDefaults, sourceUnitType,
                            pathStartPoint, profileU, profileV, sweepProfileCurves);
                        break;
                    default:
                        error = $"Sweep profile shape '{profileShape}' not supported.";
                        foreach (var mc in modelCurves)
                            try { doc.Delete(mc.Id); } catch { }
                        return false;
                }
            }
            catch (Exception ex)
            {
                error = $"Error building sweep profile: {ex.Message}";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }

            sweepProfileArray.Append(sweepProfileCurves);

            try
            {
                SweepProfile sweepProfile = app.Create.NewCurveLoopsProfile(sweepProfileArray);
                Sweep sweep = doc.FamilyCreate.NewSweep(
                    !isVoid, pathRefArray, sweepProfile, 0, ProfilePlaneLocation.Start);

                if (geom.TryGetProperty("subcategory", out var subcatProp))
                {
                    string subcatName = subcatProp.GetString();
                    if (subcategoryMap.ContainsKey(subcatName))
                        sweep.Subcategory = subcategoryMap[subcatName];
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Sweep failed: {ex.Message}";
                foreach (var mc in modelCurves)
                    try { doc.Delete(mc.Id); } catch { }
                return false;
            }
        }

        private static bool CreateBlend(
            Document doc, Autodesk.Revit.ApplicationServices.Application app,
            JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();

            var bottomProfile = geom.GetProperty("bottom_profile");
            var topProfile = geom.GetProperty("top_profile");
            string bottomShape = bottomProfile.GetProperty("shape").GetString();
            string topShape = topProfile.GetProperty("shape").GetString();

            double bottomOffsetValue = EvaluateExpression(GetExpressionString(geom.GetProperty("bottom_offset")), paramDefaults);
            double topOffsetValue = EvaluateExpression(GetExpressionString(geom.GetProperty("top_offset")), paramDefaults);
            double bottomOffsetFeet = UnitUtils.ConvertToInternalUnits(bottomOffsetValue, sourceUnitType);
            double topOffsetFeet = UnitUtils.ConvertToInternalUnits(topOffsetValue, sourceUnitType);

            if (Math.Abs(topOffsetFeet - bottomOffsetFeet) < 0.001)
            {
                error = "Blend has zero height (top_offset == bottom_offset).";
                return false;
            }

            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);
            string revolveError = null;

            // ---- Circle -> circle: build a true cone / cylinder --------------------------------
            if (bottomShape == "circle" && topShape == "circle")
            {
                double bcu = EvaluateExpression(GetExpressionString(bottomProfile.GetProperty("center").GetProperty("u")), paramDefaults);
                double bcv = EvaluateExpression(GetExpressionString(bottomProfile.GetProperty("center").GetProperty("v")), paramDefaults);
                double tcu = EvaluateExpression(GetExpressionString(topProfile.GetProperty("center").GetProperty("u")), paramDefaults);
                double tcv = EvaluateExpression(GetExpressionString(topProfile.GetProperty("center").GetProperty("v")), paramDefaults);
                double bottomRadius = EvaluateExpression(GetExpressionString(bottomProfile.GetProperty("radius")), paramDefaults);
                double topRadius = EvaluateExpression(GetExpressionString(topProfile.GetProperty("radius")), paramDefaults);

                bool coaxial = Math.Abs(bcu - tcu) < 0.001 && Math.Abs(bcv - tcv) < 0.001;

                if (coaxial)
                {
                    // Revolution gives a smooth cone (a blend of two polygons would be faceted and
                    // Revit often refuses blends between circles).
                    if (CreateConeAsRevolution(doc, geom, isVoid, planeDirection, bcu, bcv,
                            bottomRadius, topRadius, planeOffsetFeet, bottomOffsetFeet, topOffsetFeet,
                            sourceUnitType, subcategoryMap, out revolveError))
                        return true;
                }

                if (Math.Abs(bottomRadius - topRadius) < 0.001)
                {
                    return CreateBlendAsSweep(doc, geom, sketchRefPlane, planeDirection,
                        root, paramDefaults, sourceUnitType, subcategoryMap, out error);
                }
            }

            // ---- General blend: base profile on the lower plane, top profile on a parallel plane ----
            XYZ normal = PlaneNormal(planeDirection);
            SketchPlane sketchPlane;
            try
            {
                Plane plane = Plane.CreateByNormalAndOrigin(normal,
                    PlaneOriginFeet(planeDirection, planeOffsetFeet + bottomOffsetFeet));
                sketchPlane = SketchPlane.Create(doc, plane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane from '{sketchPlaneName}': {ex.Message}";
                return false;
            }

            CurveArray bottomCurves = new CurveArray();
            CurveArray topCurves = new CurveArray();

            try
            {
                BuildBlendSection(bottomProfile, bottomShape, planeDirection, paramDefaults, sourceUnitType,
                    bottomCurves, planeOffsetFeet + bottomOffsetFeet);
                BuildBlendSection(topProfile, topShape, planeDirection, paramDefaults, sourceUnitType,
                    topCurves, planeOffsetFeet + topOffsetFeet);
            }
            catch (Exception ex)
            {
                error = $"Profile error: {ex.Message}";
                return false;
            }

            if (bottomCurves.Size != topCurves.Size)
            {
                error = $"Blend profiles must have the same number of curve segments. Bottom has {bottomCurves.Size}, top has {topCurves.Size}.";
                return false;
            }

            Blend blend;
            try
            {
                // profile1 = base (lies on sketchPlane), profile2 = top (parallel plane, already at its height)
                blend = doc.FamilyCreate.NewBlend(!isVoid, bottomCurves, topCurves, sketchPlane);
            }
            catch (Exception ex)
            {
                error = $"NewBlend failed ({ex.GetType().Name}): {ex.Message}" +
                        (revolveError != null ? $" | revolve fallback also failed: {revolveError}" : "");
                return false;
            }

            if (geom.TryGetProperty("subcategory", out var subcatProp))
            {
                string subcatName = subcatProp.GetString();
                if (subcatName != null && subcategoryMap.ContainsKey(subcatName))
                    blend.Subcategory = subcategoryMap[subcatName];
            }

            return true;
        }

        private static void BuildBlendSection(JsonElement profile, string shape, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType, CurveArray curves, double planeOffsetFeet)
        {
            switch (shape)
            {
                case "rectangle":
                    BuildRectangleProfile(profile, planeDirection, defaults, unitType, curves, planeOffsetFeet);
                    break;
                case "circle":
                    BuildCircleAsPolygonProfile(profile, planeDirection, defaults, unitType, curves, planeOffsetFeet);
                    break;
                case "custom":
                    BuildCustomProfile(profile, planeDirection, defaults, unitType, curves, planeOffsetFeet);
                    break;
                default:
                    throw new InvalidOperationException($"Profile shape '{shape}' not supported for blends.");
            }
        }

        /// <summary>
        /// Coaxial circle-to-circle blend (cone, frustum, cylinder) built as a Revolution around
        /// the axis normal to the sketch plane.
        /// </summary>
        private static bool CreateConeAsRevolution(
            Document doc, JsonElement geom, bool isVoid, string planeDirection,
            double cu, double cv, double bottomRadius, double topRadius,
            double planeOffsetFeet, double bottomOffsetFeet, double topOffsetFeet,
            ForgeTypeId unitType, Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            try
            {
                XYZ n = PlaneNormal(planeDirection);
                XYZ axisBase = UVToXYZ(cu, cv, planeDirection, unitType, planeOffsetFeet);
                XYZ uDir = (UVToXYZ(cu + 1, cv, planeDirection, unitType, planeOffsetFeet) - axisBase).Normalize();

                double rb = UnitUtils.ConvertToInternalUnits(Math.Abs(bottomRadius), unitType);
                double rt = UnitUtils.ConvertToInternalUnits(Math.Abs(topRadius), unitType);
                if (rb < 0.003 && rt < 0.003)
                {
                    error = "Both radii are zero.";
                    return false;
                }

                XYZ p0 = axisBase + n * bottomOffsetFeet;
                XYZ p3 = axisBase + n * topOffsetFeet;
                XYZ p1 = p0 + uDir * rb;
                XYZ p2 = p3 + uDir * rt;

                var pts = new List<XYZ> { p0 };
                if (rb >= 0.003) pts.Add(p1);
                if (rt >= 0.003) pts.Add(p2);
                pts.Add(p3);

                CurveArray loop = new CurveArray();
                for (int i = 0; i < pts.Count; i++)
                    loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));

                CurveArrArray profile = new CurveArrArray();
                profile.Append(loop);

                XYZ sketchNormal = n.CrossProduct(uDir).Normalize();
                SketchPlane sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(sketchNormal, p0));
                Line axis = Line.CreateBound(p0, p3);

                Revolution rev = doc.FamilyCreate.NewRevolution(!isVoid, profile, sp, axis, 0, 2 * Math.PI);

                if (geom.TryGetProperty("subcategory", out var subcatProp))
                {
                    string subcatName = subcatProp.GetString();
                    if (subcatName != null && subcategoryMap.ContainsKey(subcatName))
                        rev.Subcategory = subcategoryMap[subcatName];
                }
                return true;
            }
            catch (Exception ex)
            {
                error = $"Revolution failed ({ex.GetType().Name}): {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Makes every void form cut every solid form it intersects. API-created voids in a family
        /// do not always cut automatically.
        /// </summary>
        private static int ApplyVoidCuts(Document doc, GenerationResult result, List<GenericForm> only = null)
        {
            int count = 0;
            try
            {
                List<GenericForm> forms = only ?? new FilteredElementCollector(doc)
                    .OfClass(typeof(GenericForm)).Cast<GenericForm>().ToList();
                List<GenericForm> voids = forms.Where(f => !f.IsSolid).ToList();
                List<GenericForm> solids = forms.Where(f => f.IsSolid).ToList();

                foreach (GenericForm v in voids)
                {
                    BoundingBoxXYZ vb = v.get_BoundingBox(null);
                    foreach (GenericForm s in solids)
                    {
                        try
                        {
                            BoundingBoxXYZ sb = s.get_BoundingBox(null);
                            if (vb != null && sb != null && !BoxesIntersect(vb, sb)) continue;
                            if (SolidSolidCutUtils.CutExistsBetweenElements(s, v, out _)) continue;
                            if (!SolidSolidCutUtils.CanElementCutElement(v, s, out _)) continue;
                            SolidSolidCutUtils.AddCutBetweenSolids(doc, s, v);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Void cut skipped: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Report.Add($"Void cutting skipped: {ex.Message}");
            }
            return count;
        }

        private static bool BoxesIntersect(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            const double tol = 1e-6;
            return a.Min.X <= b.Max.X + tol && a.Max.X >= b.Min.X - tol &&
                   a.Min.Y <= b.Max.Y + tol && a.Max.Y >= b.Min.Y - tol &&
                   a.Min.Z <= b.Max.Z + tol && a.Max.Z >= b.Min.Z - tol;
        }

        private static bool CreateBlendAsSweep(
            Document doc, JsonElement geom, ReferencePlane sketchRefPlane,
            string planeDirection, JsonElement root,
            Dictionary<string, double> paramDefaults, ForgeTypeId sourceUnitType,
            Dictionary<string, Category> subcategoryMap, out string error)
        {
            error = null;
            bool isVoid = geom.GetProperty("is_void").GetBoolean();
            string sketchPlaneName = geom.GetProperty("sketch_plane").GetString();
            double planeOffsetFeet = GetPlaneOffsetFeet(sketchPlaneName, root, paramDefaults, sourceUnitType);

            var bottomProfile = geom.GetProperty("bottom_profile");
            var topProfile = geom.GetProperty("top_profile");

            var bottomCenter = bottomProfile.GetProperty("center");
            double bcu = EvaluateExpression(GetExpressionString(bottomCenter.GetProperty("u")), paramDefaults);
            double bcv = EvaluateExpression(GetExpressionString(bottomCenter.GetProperty("v")), paramDefaults);

            var topCenter = topProfile.GetProperty("center");
            double tcu = EvaluateExpression(GetExpressionString(topCenter.GetProperty("u")), paramDefaults);
            double tcv = EvaluateExpression(GetExpressionString(topCenter.GetProperty("v")), paramDefaults);

            double radius = EvaluateExpression(
                GetExpressionString(bottomProfile.GetProperty("radius")), paramDefaults);

            string bottomOffsetExpr = GetExpressionString(geom.GetProperty("bottom_offset"));
            string topOffsetExpr = GetExpressionString(geom.GetProperty("top_offset"));
            double bottomOffsetValue = EvaluateExpression(bottomOffsetExpr, paramDefaults);
            double topOffsetValue = EvaluateExpression(topOffsetExpr, paramDefaults);
            double bottomOffsetFeet = UnitUtils.ConvertToInternalUnits(bottomOffsetValue, sourceUnitType);
            double topOffsetFeet = UnitUtils.ConvertToInternalUnits(topOffsetValue, sourceUnitType);

            XYZ bottomPoint = UVToXYZ(bcu, bcv, planeDirection, sourceUnitType, planeOffsetFeet + bottomOffsetFeet);
            XYZ topPoint = UVToXYZ(tcu, tcv, planeDirection, sourceUnitType, planeOffsetFeet + topOffsetFeet);

            double cableLength = bottomPoint.DistanceTo(topPoint);
            if (cableLength < 0.01)
            {
                error = "Cable too short (bottom and top points nearly coincide).";
                return false;
            }

            XYZ cableDir = (topPoint - bottomPoint).Normalize();

            Plane extPlane = Plane.CreateByNormalAndOrigin(cableDir, bottomPoint);
            SketchPlane extSketchPlane;
            try
            {
                extSketchPlane = SketchPlane.Create(doc, extPlane);
            }
            catch (Exception ex)
            {
                error = $"SketchPlane for extrusion failed: {ex.Message}";
                return false;
            }

            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, sourceUnitType);
            XYZ basisU = extPlane.XVec;
            XYZ basisV = extPlane.YVec;

            CurveArrArray profileLoops = new CurveArrArray();
            CurveArray profileCurves = new CurveArray();

            try
            {
                XYZ circleTop = bottomPoint.Add(basisV.Multiply(radiusFeet));
                XYZ circleBottom = bottomPoint.Add(basisV.Multiply(-radiusFeet));
                XYZ circleRight = bottomPoint.Add(basisU.Multiply(radiusFeet));
                XYZ circleLeft = bottomPoint.Add(basisU.Multiply(-radiusFeet));

                profileCurves.Append(Arc.Create(circleTop, circleBottom, circleRight));
                profileCurves.Append(Arc.Create(circleBottom, circleTop, circleLeft));
            }
            catch (Exception ex)
            {
                error = $"Circle profile failed: {ex.Message}";
                return false;
            }

            profileLoops.Append(profileCurves);

            try
            {
                Extrusion extrusion = doc.FamilyCreate.NewExtrusion(
                    !isVoid, profileLoops, extSketchPlane, cableLength);

                if (geom.TryGetProperty("subcategory", out var subcatProp))
                {
                    string subcatName = subcatProp.GetString();
                    if (subcategoryMap.ContainsKey(subcatName))
                        extrusion.Subcategory = subcategoryMap[subcatName];
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Extrusion failed ({ex.GetType().Name}): {ex.Message}";
                if (ex.InnerException != null)
                    error += $" | Inner: {ex.InnerException.Message}";
                return false;
            }
        }

        // ==================== EXPRESSION HANDLING ====================

        private static string GetExpressionString(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.GetDouble().ToString("R", CultureInfo.InvariantCulture);
            return element.GetString();
        }

        private static double EvaluateExpression(string expr, Dictionary<string, double> defaults)
        {
            if (string.IsNullOrWhiteSpace(expr)) return 0;
            if (double.TryParse(expr.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double literal)) return literal;

            // Parameter values are substituted with invariant formatting ("12.5", never "12,5"),
            // otherwise expressions silently evaluate to 0 on Windows with a comma decimal separator.
            string evaluated = expr;
            // whole-identifier replacement: short names such as "n", "d", "L", "H" must not hit other identifiers
            foreach (var kvp in defaults.OrderByDescending(k => k.Key.Length))
                evaluated = System.Text.RegularExpressions.Regex.Replace(evaluated,
                    @"(?<![\p{L}\p{N}_.])" + System.Text.RegularExpressions.Regex.Escape(kvp.Key) + @"(?![\p{L}\p{N}_])",
                    "(" + kvp.Value.ToString("R", CultureInfo.InvariantCulture) + ")");

            try
            {
                var dt = new DataTable { Locale = CultureInfo.InvariantCulture };
                object result = dt.Compute(evaluated, "");
                return Convert.ToDouble(result);
            }
            catch
            {
                return 0;
            }
        }

        // ==================== REFERENCE PLANE HELPERS ====================

        private static string GetPlaneDirection(string planeName, JsonElement root)
        {
            foreach (var plane in root.GetProperty("reference_planes").EnumerateArray())
            {
                if (string.Equals(plane.GetProperty("name").GetString(), planeName, StringComparison.OrdinalIgnoreCase))
                    return plane.GetProperty("direction").GetString();
            }

            if (planeName.Contains("Left/Right", StringComparison.OrdinalIgnoreCase)) return "x";
            if (planeName.Contains("Front/Back", StringComparison.OrdinalIgnoreCase)) return "y";
            if (planeName.Contains("Ref. Level", StringComparison.OrdinalIgnoreCase) ||
                planeName.Contains("Ref Level", StringComparison.OrdinalIgnoreCase)) return "z";
            return "z";
        }

        private static bool PlaneDefinedInJson(string planeName, JsonElement root)
        {
            foreach (var plane in root.GetProperty("reference_planes").EnumerateArray())
            {
                if (string.Equals(plane.GetProperty("name").GetString(), planeName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Origin of a sketch plane given its normal direction and offset (feet).</summary>
        private static XYZ PlaneOriginFeet(string planeDirection, double offsetFeet)
        {
            switch (planeDirection)
            {
                case "x": return new XYZ(offsetFeet, 0, 0);
                case "y": return new XYZ(0, offsetFeet, 0);
                default: return new XYZ(0, 0, offsetFeet);
            }
        }

        private static XYZ PlaneNormal(string planeDirection)
        {
            switch (planeDirection)
            {
                case "x": return XYZ.BasisX;
                case "y": return XYZ.BasisY;
                default: return XYZ.BasisZ;
            }
        }

        private static double GetPlaneOffsetFeet(
            string planeName, JsonElement root,
            Dictionary<string, double> defaults, ForgeTypeId sourceUnitType)
        {
            foreach (var plane in root.GetProperty("reference_planes").EnumerateArray())
            {
                if (string.Equals(plane.GetProperty("name").GetString(), planeName, StringComparison.OrdinalIgnoreCase))
                {
                    string offsetExpr = GetExpressionString(plane.GetProperty("offset"));
                    double offsetInJsonUnits = EvaluateExpression(offsetExpr, defaults);
                    return UnitUtils.ConvertToInternalUnits(offsetInJsonUnits, sourceUnitType);
                }
            }
            return 0;
        }

        // ==================== PARAMETER TYPE MAPPING ====================

        private static ForgeTypeId MapParamTypeToSpec(string paramType)
        {
            return paramType switch
            {
                "length" => SpecTypeId.Length,
                "angle" => SpecTypeId.Angle,
                "number" => SpecTypeId.Number,
                "integer" => SpecTypeId.Int.Integer,
                "yes_no" => SpecTypeId.Boolean.YesNo,
                "text" => SpecTypeId.String.Text,
                "material" => SpecTypeId.Reference.Material,
                _ => SpecTypeId.String.Text
            };
        }

        private static ForgeTypeId MapParamGroupToGroupType(string group)
        {
            return group switch
            {
                "Dimensions" => GroupTypeId.Geometry,
                "Construction" => GroupTypeId.Construction,
                "Identity Data" => GroupTypeId.IdentityData,
                "Graphics" => GroupTypeId.Graphics,
                "Materials and Finishes" => GroupTypeId.Materials,
                _ => GroupTypeId.General
            };
        }

        // ==================== COORDINATE HELPERS ====================

        private static XYZ UVToXYZ(double u, double v, string planeDirection, ForgeTypeId unitType, double planeOffsetFeet)
        {
            double uFeet = UnitUtils.ConvertToInternalUnits(u, unitType);
            double vFeet = UnitUtils.ConvertToInternalUnits(v, unitType);

            return planeDirection switch
            {
                "x" => new XYZ(planeOffsetFeet, uFeet, vFeet),
                "y" => new XYZ(uFeet, planeOffsetFeet, vFeet),
                "z" => new XYZ(uFeet, vFeet, planeOffsetFeet),
                _ => new XYZ(uFeet, vFeet, planeOffsetFeet)
            };
        }

        private static void ExtractUV(XYZ vec, string planeDirection, out double u, out double v)
        {
            switch (planeDirection)
            {
                case "x": u = vec.Y; v = vec.Z; break;
                case "y": u = vec.X; v = vec.Z; break;
                case "z": u = vec.X; v = vec.Y; break;
                default: u = vec.X; v = vec.Y; break;
            }
        }

        // ==================== ARC CALCULATION ====================

        private static XYZ ComputeArcPassThrough(
            double su, double sv, double eu, double ev,
            double cu, double cv, double inTanU, double inTanV,
            string planeDirection, ForgeTypeId unitType, double planeOffsetFeet)
        {
            double radius = Math.Sqrt((su - cu) * (su - cu) + (sv - cv) * (sv - cv));
            double mu = (su + eu) / 2.0;
            double mv = (sv + ev) / 2.0;
            double dmu = mu - cu;
            double dmv = mv - cv;
            double dmLen = Math.Sqrt(dmu * dmu + dmv * dmv);

            double mid1u, mid1v, mid2u, mid2v;

            if (dmLen < 0.001)
            {
                double chu = eu - su, chv = ev - sv;
                double chLen = Math.Sqrt(chu * chu + chv * chv);
                if (chLen < 0.001)
                    return UVToXYZ(cu, cv, planeDirection, unitType, planeOffsetFeet);

                double perpU = -chv / chLen, perpV = chu / chLen;
                mid1u = cu + perpU * radius;
                mid1v = cv + perpV * radius;
                mid2u = cu - perpU * radius;
                mid2v = cv - perpV * radius;
            }
            else
            {
                double dirU = dmu / dmLen, dirV = dmv / dmLen;
                mid1u = cu + dirU * radius;
                mid1v = cv + dirV * radius;
                mid2u = cu - dirU * radius;
                mid2v = cv - dirV * radius;
            }

            double csu = su - cu, csv = sv - cv;
            double perpAu = -csv, perpAv = csu;
            double dotToMid1 = perpAu * (mid1u - su) + perpAv * (mid1v - sv);
            double tangentForMid1U = dotToMid1 >= 0 ? perpAu : -perpAu;
            double tangentForMid1V = dotToMid1 >= 0 ? perpAv : -perpAv;
            double alignment = tangentForMid1U * inTanU + tangentForMid1V * inTanV;

            double chosenU = alignment >= 0 ? mid1u : mid2u;
            double chosenV = alignment >= 0 ? mid1v : mid2v;

            return UVToXYZ(chosenU, chosenV, planeDirection, unitType, planeOffsetFeet);
        }

        // ==================== EXTRUSION PROFILE BUILDERS ====================

        /// <summary>
        /// "holes": [ { "radius": "d/2", "center": {u,v}?, "pattern": { "count": "n", "radius": "D2/2",
        /// "start_angle": "180/n", "center": {u,v}? } } ] → circular inner loops of an extrusion profile.
        /// </summary>
        private static List<CurveArray> BuildHoleLoops(JsonElement holes, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType, double planeOffsetFeet)
        {
            var loops = new List<CurveArray>();
            foreach (JsonElement h in holes.EnumerateArray())
            {
                double r = EvaluateExpression(GetExpressionString(h.GetProperty("radius")), defaults);
                if (r <= 0) continue;
                double hu = 0, hv = 0;
                if (h.TryGetProperty("center", out JsonElement hc))
                {
                    hu = EvaluateExpression(GetExpressionString(hc.GetProperty("u")), defaults);
                    hv = EvaluateExpression(GetExpressionString(hc.GetProperty("v")), defaults);
                }

                var centers = new List<(double u, double v)>();
                if (h.TryGetProperty("pattern", out JsonElement pat))
                {
                    int count = (int)Math.Round(EvaluateExpression(GetExpressionString(pat.GetProperty("count")), defaults));
                    double pr = EvaluateExpression(GetExpressionString(pat.GetProperty("radius")), defaults);
                    double a0 = pat.TryGetProperty("start_angle", out JsonElement sa)
                        ? EvaluateExpression(GetExpressionString(sa), defaults) : 0;
                    double pu = hu, pv = hv;
                    if (pat.TryGetProperty("center", out JsonElement pc))
                    {
                        pu = EvaluateExpression(GetExpressionString(pc.GetProperty("u")), defaults);
                        pv = EvaluateExpression(GetExpressionString(pc.GetProperty("v")), defaults);
                    }
                    for (int k = 0; k < Math.Max(0, Math.Min(count, 64)); k++)
                    {
                        double a = (a0 + 360.0 * k / count) * Math.PI / 180.0;
                        centers.Add((pu + pr * Math.Cos(a), pv + pr * Math.Sin(a)));
                    }
                }
                else centers.Add((hu, hv));

                double rf = UnitUtils.ConvertToInternalUnits(r, unitType);
                XYZ uDir, vDir;
                switch (planeDirection)
                {
                    case "x": uDir = XYZ.BasisY; vDir = XYZ.BasisZ; break;
                    case "y": uDir = XYZ.BasisX; vDir = XYZ.BasisZ; break;
                    default: uDir = XYZ.BasisX; vDir = XYZ.BasisY; break;
                }
                foreach (var c in centers)
                {
                    XYZ c3 = UVToXYZ(c.u, c.v, planeDirection, unitType, planeOffsetFeet);
                    var loop = new CurveArray();
                    loop.Append(Arc.Create(c3.Add(vDir.Multiply(rf)), c3.Add(vDir.Multiply(-rf)), c3.Add(uDir.Multiply(rf))));
                    loop.Append(Arc.Create(c3.Add(vDir.Multiply(-rf)), c3.Add(vDir.Multiply(rf)), c3.Add(uDir.Multiply(-rf))));
                    loops.Add(loop);
                }
            }
            return loops;
        }

        private static void BuildRectangleProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var origin = profile.GetProperty("origin");
            double ou = EvaluateExpression(GetExpressionString(origin.GetProperty("u")), defaults);
            double ov = EvaluateExpression(GetExpressionString(origin.GetProperty("v")), defaults);
            double w = EvaluateExpression(GetExpressionString(profile.GetProperty("width")), defaults);
            double h = EvaluateExpression(GetExpressionString(profile.GetProperty("height")), defaults);

            XYZ p0 = UVToXYZ(ou, ov, planeDirection, unitType, planeOffsetFeet);
            XYZ p1 = UVToXYZ(ou + w, ov, planeDirection, unitType, planeOffsetFeet);
            XYZ p2 = UVToXYZ(ou + w, ov + h, planeDirection, unitType, planeOffsetFeet);
            XYZ p3 = UVToXYZ(ou, ov + h, planeDirection, unitType, planeOffsetFeet);

            curves.Append(Line.CreateBound(p0, p1));
            curves.Append(Line.CreateBound(p1, p2));
            curves.Append(Line.CreateBound(p2, p3));
            curves.Append(Line.CreateBound(p3, p0));
        }

        private static void BuildCircleProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var center = profile.GetProperty("center");
            double cu = EvaluateExpression(GetExpressionString(center.GetProperty("u")), defaults);
            double cv = EvaluateExpression(GetExpressionString(center.GetProperty("v")), defaults);
            double radius = EvaluateExpression(GetExpressionString(profile.GetProperty("radius")), defaults);
            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, unitType);

            XYZ center3D = UVToXYZ(cu, cv, planeDirection, unitType, planeOffsetFeet);
            XYZ uDir, vDir;
            switch (planeDirection)
            {
                case "x": uDir = XYZ.BasisY; vDir = XYZ.BasisZ; break;
                case "y": uDir = XYZ.BasisX; vDir = XYZ.BasisZ; break;
                default: uDir = XYZ.BasisX; vDir = XYZ.BasisY; break;
            }

            XYZ top = center3D.Add(vDir.Multiply(radiusFeet));
            XYZ bottom = center3D.Add(vDir.Multiply(-radiusFeet));
            XYZ right = center3D.Add(uDir.Multiply(radiusFeet));
            XYZ left = center3D.Add(uDir.Multiply(-radiusFeet));

            curves.Append(Arc.Create(top, bottom, right));
            curves.Append(Arc.Create(bottom, top, left));
        }

        private static void BuildCircleAsPolygonProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var center = profile.GetProperty("center");
            double cu = EvaluateExpression(GetExpressionString(center.GetProperty("u")), defaults);
            double cv = EvaluateExpression(GetExpressionString(center.GetProperty("v")), defaults);
            double radius = EvaluateExpression(GetExpressionString(profile.GetProperty("radius")), defaults);

            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, unitType);
            const double minEdgeFeet = 0.01;
            int segments = 8;
            if (radiusFeet > 0)
            {
                double edgeLength = 2.0 * radiusFeet * Math.Sin(Math.PI / segments);
                while (edgeLength < minEdgeFeet && segments > 3)
                {
                    segments--;
                    edgeLength = 2.0 * radiusFeet * Math.Sin(Math.PI / segments);
                }
            }

            var points = new List<XYZ>();
            for (int i = 0; i < segments; i++)
            {
                double angle = 2.0 * Math.PI * i / segments;
                double pu = cu + radius * Math.Cos(angle);
                double pv = cv + radius * Math.Sin(angle);
                points.Add(UVToXYZ(pu, pv, planeDirection, unitType, planeOffsetFeet));
            }

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                curves.Append(Line.CreateBound(points[i], points[next]));
            }
        }

        private static void BuildCustomProfile(
            JsonElement profile, string planeDirection,
            Dictionary<string, double> defaults, ForgeTypeId unitType,
            CurveArray curves, double planeOffsetFeet)
        {
            var vertices = profile.GetProperty("vertices");
            var points = new List<XYZ>();

            foreach (var vertex in vertices.EnumerateArray())
            {
                double u = EvaluateExpression(GetExpressionString(vertex.GetProperty("u")), defaults);
                double v = EvaluateExpression(GetExpressionString(vertex.GetProperty("v")), defaults);
                points.Add(UVToXYZ(u, v, planeDirection, unitType, planeOffsetFeet));
            }

            if (points.Count < 3) return;

            for (int i = 0; i < points.Count; i++)
            {
                int next = (i + 1) % points.Count;
                curves.Append(Line.CreateBound(points[i], points[next]));
            }
        }

        // ==================== SWEEP PROFILE BUILDERS ====================

        private static void BuildSweepRectangleProfile(
            JsonElement profile, Dictionary<string, double> defaults,
            ForgeTypeId unitType, XYZ pathStart,
            XYZ profileUAxis, XYZ profileVAxis, CurveArray curves)
        {
            var origin = profile.GetProperty("origin");
            double ou = EvaluateExpression(GetExpressionString(origin.GetProperty("u")), defaults);
            double ov = EvaluateExpression(GetExpressionString(origin.GetProperty("v")), defaults);
            double w = EvaluateExpression(GetExpressionString(profile.GetProperty("width")), defaults);
            double h = EvaluateExpression(GetExpressionString(profile.GetProperty("height")), defaults);

            double ouFeet = UnitUtils.ConvertToInternalUnits(ou, unitType);
            double ovFeet = UnitUtils.ConvertToInternalUnits(ov, unitType);
            double wFeet = UnitUtils.ConvertToInternalUnits(w, unitType);
            double hFeet = UnitUtils.ConvertToInternalUnits(h, unitType);

            XYZ p0 = pathStart.Add(profileUAxis.Multiply(ouFeet)).Add(profileVAxis.Multiply(ovFeet));
            XYZ p1 = p0.Add(profileUAxis.Multiply(wFeet));
            XYZ p2 = p1.Add(profileVAxis.Multiply(hFeet));
            XYZ p3 = p0.Add(profileVAxis.Multiply(hFeet));

            curves.Append(Line.CreateBound(p0, p1));
            curves.Append(Line.CreateBound(p1, p2));
            curves.Append(Line.CreateBound(p2, p3));
            curves.Append(Line.CreateBound(p3, p0));
        }

        private static void BuildSweepCircleProfile(
            JsonElement profile, Dictionary<string, double> defaults,
            ForgeTypeId unitType, XYZ pathStart,
            XYZ profileUAxis, XYZ profileVAxis, CurveArray curves)
        {
            var center = profile.GetProperty("center");
            double cu = EvaluateExpression(GetExpressionString(center.GetProperty("u")), defaults);
            double cv = EvaluateExpression(GetExpressionString(center.GetProperty("v")), defaults);
            double radius = EvaluateExpression(GetExpressionString(profile.GetProperty("radius")), defaults);

            double cuFeet = UnitUtils.ConvertToInternalUnits(cu, unitType);
            double cvFeet = UnitUtils.ConvertToInternalUnits(cv, unitType);
            double radiusFeet = UnitUtils.ConvertToInternalUnits(radius, unitType);

            XYZ center3D = pathStart.Add(profileUAxis.Multiply(cuFeet)).Add(profileVAxis.Multiply(cvFeet));
            XYZ top = center3D.Add(profileVAxis.Multiply(radiusFeet));
            XYZ bottom = center3D.Add(profileVAxis.Multiply(-radiusFeet));
            XYZ right = center3D.Add(profileUAxis.Multiply(radiusFeet));
            XYZ left = center3D.Add(profileUAxis.Multiply(-radiusFeet));

            curves.Append(Arc.Create(top, bottom, right));
            curves.Append(Arc.Create(bottom, top, left));
        }
    }
}
