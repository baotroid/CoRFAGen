namespace BeGenSt.Data
{
    /// <summary>
    /// Failure preprocessor that records Revit errors / warnings (with the names of the generated elements involved)
    /// so that they appear in the generation report. Warnings are dismissed, errors are left to Revit's default handling.
    /// </summary>
    public class FailureLog : IFailuresPreprocessor
    {
        /// <summary>Element id → readable name ("[set 05 …] Motor") of the elements created by the generator.</summary>
        public static readonly Dictionary<long, string> Names = new Dictionary<long, string>();
        /// <summary>Entries collected since the last <see cref="Reset"/>.</summary>
        public static readonly List<string> Entries = new List<string>();

        private readonly string _stage;
        public FailureLog(string stage) { _stage = stage; }

        public static void Reset() { Names.Clear(); Entries.Clear(); }

        public static void Attach(Transaction t, string stage)
        {
            FailureHandlingOptions o = t.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(new FailureLog(stage));
            t.SetFailureHandlingOptions(o);
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            Document doc = a.GetDocument();
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
            {
                try
                {
                    FailureSeverity sev = f.GetSeverity();
                    var ids = f.GetFailingElementIds().Concat(f.GetAdditionalElementIds()).Distinct().ToList();
                    string who = string.Join("; ", ids.Take(12).Select(id => Describe(doc, id)));
                    Entries.Add($"REVIT {(sev == FailureSeverity.Warning ? "warning" : "ERROR")} [{_stage}]: {f.GetDescriptionText()}" +
                                (who.Length > 0 ? " → " + who : ""));
                    if (sev == FailureSeverity.Warning) a.DeleteWarning(f);
                }
                catch { }
            }
            return FailureProcessingResult.Continue;
        }

        private static string Describe(Document doc, ElementId id)
        {
            long key = id.IntegerValueCompat();
            if (Names.TryGetValue(key, out string n)) return $"{key} {n}";
            Element e = doc.GetElement(id);
            if (e == null) return key.ToString();
            string sub = "";
            try { sub = (e as GenericForm)?.Subcategory?.Name; } catch { }
            string owner = "";
            // a sketch reports its extrusion
            if (e is Sketch) owner = " (sketch)";
            return $"{key} {e.GetType().Name} '{e.Name}'{(string.IsNullOrEmpty(sub) ? "" : " /" + sub)}{owner} — not created by RFAGen";
        }
    }
}
