namespace BeGenSt.Data
{
    /// <summary>
    /// "Full clean": prepares an existing family for a new generation (TypeGen / RFAGen). Unlike Purge (which removes only
    /// UNUSED elements) it removes the family's own content: geometry (solids and voids), connectors, nested family
    /// instances, dimensions, model / symbolic lines, reference lines, non-template reference planes, lookup tables and
    /// the size_lookup formulas that use them. Family parameters are kept (they are reused / mapped by the generator).
    /// After that the usual purge passes remove the now unused nested families, materials, patterns ...
    /// </summary>
    public static class FamilyCleaner
    {
        public class Summary
        {
            public Dictionary<string, int> Found { get; } = new Dictionary<string, int>();
            public Dictionary<string, int> Removed { get; } = new Dictionary<string, int>();
            public List<string> Log { get; } = new List<string>();
            public int Total => Found.Values.Sum();
            public string Text(Dictionary<string, int> d) =>
                string.Join("\n", d.Where(k => k.Value > 0).Select(k => $"  • {k.Key}: {k.Value}"));
        }

        private const string KForms = "Geometry (extrusions, blends, revolutions, sweeps, voids)";
        private const string KConnectors = "Connectors";
        private const string KNested = "Nested family instances";
        private const string KDims = "Dimensions";
        private const string KLines = "Model / symbolic lines";
        private const string KRefLines = "Reference lines";
        private const string KPlanes = "Reference planes (not from the template)";
        private const string KTables = "Lookup tables";
        private const string KFormulas = "size_lookup formulas";

        /// <summary>What a full clean would remove (no changes).</summary>
        public static Summary Analyse(Document doc)
        {
            var s = new Summary();
            var c = Collect(doc);
            foreach (var kv in c.Elements) s.Found[kv.Key] = kv.Value.Count;
            s.Found[KTables] = c.Tables.Count;
            s.Found[KFormulas] = c.Formulas.Count;
            return s;
        }

        /// <summary>Removes the content in one transaction. Returns what was removed.</summary>
        public static Summary Clean(Document doc)
        {
            var s = new Summary();
            var c = Collect(doc);
            FamilyManager fm = doc.FamilyManager;
            FailureLog.Reset();
            using (var t = new Transaction(doc, "BeGenSt: full clean"))
            {
                FailureHandlingOptions o = t.GetFailureHandlingOptions();
                o.SetFailuresPreprocessor(new FailureLog("full clean"));
                t.SetFailureHandlingOptions(o);
                t.Start();

                // 1. formulas that read lookup tables (they would block deleting the tables / break new values)
                int nf = 0;
                foreach (FamilyParameter p in c.Formulas)
                {
                    try { s.Log.Add($"formula removed: {p.Definition.Name} = {p.Formula}"); fm.SetFormula(p, null); nf++; }
                    catch (Exception ex) { s.Log.Add($"formula kept: {p.Definition.Name}: {ex.Message}"); }
                }
                s.Removed[KFormulas] = nf;

                // 2. lookup tables
                int nt = 0;
                FamilySizeTableManager mgr = FamilySizeTableManager.GetFamilySizeTableManager(doc, doc.OwnerFamily.Id);
                foreach (string name in c.Tables)
                {
                    try { if (mgr.RemoveSizeTable(name)) { nt++; s.Log.Add("lookup table removed: " + name); } }
                    catch (Exception ex) { s.Log.Add($"lookup table kept: {name}: {ex.Message}"); }
                }
                s.Removed[KTables] = nt;

                // 3. elements (dimensions first, then dependent → independent)
                foreach (string kind in new[] { KDims, KConnectors, KNested, KForms, KLines, KRefLines, KPlanes })
                {
                    if (!c.Elements.TryGetValue(kind, out List<ElementId> ids)) continue;
                    int n = 0;
                    foreach (ElementId id in ids)
                    {
                        if (doc.GetElement(id) == null) { n++; continue; } // removed together with its host
                        try { doc.Delete(id); n++; }
                        catch (Exception ex) { s.Log.Add($"{kind}: {id.IntegerValueCompat()} not deleted: {ex.Message}"); }
                    }
                    s.Removed[kind] = n;
                }
                t.Commit();
            }
            s.Log.AddRange(FailureLog.Entries);
            return s;
        }

        private class Collected
        {
            public Dictionary<string, List<ElementId>> Elements = new Dictionary<string, List<ElementId>>();
            public List<string> Tables = new List<string>();
            public List<FamilyParameter> Formulas = new List<FamilyParameter>();
        }

        private static Collected Collect(Document doc)
        {
            var c = new Collected();
            List<ElementId> Ids(Type type, Func<Element, bool> filter = null) =>
                new FilteredElementCollector(doc).OfClass(type).WhereElementIsNotElementType()
                    .Where(e => filter == null || filter(e)).Select(e => e.Id).ToList();

            c.Elements[KForms] = Ids(typeof(GenericForm));
            c.Elements[KConnectors] = Ids(typeof(ConnectorElement));
            c.Elements[KNested] = Ids(typeof(FamilyInstance));
            c.Elements[KDims] = Ids(typeof(Dimension));
            c.Elements[KLines] = Ids(typeof(CurveElement), e =>
                e is CurveElement ce && ce.CurveElementType != CurveElementType.ReferenceLine && !IsSketchCurve(doc, ce));
            c.Elements[KRefLines] = Ids(typeof(CurveElement), e =>
                e is CurveElement ce && ce.CurveElementType == CurveElementType.ReferenceLine);
            c.Elements[KPlanes] = Ids(typeof(ReferencePlane), e =>
                !FamilyGenerator.BuiltInRefPlaneNames.Contains(e.Name ?? "", StringComparer.OrdinalIgnoreCase));

            try
            {
                FamilySizeTableManager mgr = FamilySizeTableManager.GetFamilySizeTableManager(doc, doc.OwnerFamily.Id);
                if (mgr != null) c.Tables.AddRange(mgr.GetAllSizeTableNames());
            }
            catch { }
            c.Formulas.AddRange(doc.FamilyManager.Parameters.Cast<FamilyParameter>()
                .Where(p => !string.IsNullOrEmpty(p.Formula) &&
                            p.Formula.IndexOf("size_lookup", StringComparison.OrdinalIgnoreCase) >= 0));
            return c;
        }

        /// <summary>Lines that belong to a sketch of a form are removed with the form.</summary>
        private static bool IsSketchCurve(Document doc, CurveElement ce)
        {
            try
            {
                SketchPlane sp = ce.SketchPlane;
                if (sp == null) return false;
                // sketch lines of forms are owned by a Sketch element; free model/symbolic lines are not
                return ce.GetType().Name.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       (ce.Category != null && ce.Category.Id.IntegerValueCompat() == (int)BuiltInCategory.OST_SketchLines);
            }
            catch { return false; }
        }
    }
}
