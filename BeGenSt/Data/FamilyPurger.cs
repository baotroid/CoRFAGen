namespace BeGenSt.Data
{
    /// <summary>
    /// Removes unused elements ("tails") from a family: CAD import types and their line styles left after a deleted DWG,
    /// unused materials with appearance assets, nested families, line/fill patterns, raster images, view templates ...
    /// Equivalent to Revit's Purge Unused run several times (each pass can free elements used only by the previous ones).
    /// </summary>
    public static class FamilyPurger
    {
        public class Candidate
        {
            public ElementId Id;
            public string Kind;
            public string Name;
        }

        public class PurgeResult
        {
            public int Deleted;
            public int Passes;
            public int Failed;
            public Dictionary<string, int> ByKind { get; } = new Dictionary<string, int>();
            public List<string> Log { get; } = new List<string>();
        }

        /// <summary>Elements Revit considers unused (one purge pass).</summary>
        public static List<Candidate> FindUnused(Document doc)
        {
            ICollection<ElementId> ids;
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
            ids = FindUnusedViaAdviser(doc);
#else
            ids = doc.GetUnusedElements(new HashSet<ElementId>());
#endif
            var list = new List<Candidate>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                list.Add(new Candidate { Id = id, Kind = KindOf(e), Name = SafeName(e) });
            }
            return list;
        }

#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023
        // "Project contains unused families and types" — the rule behind Purge Unused in older Revit versions
        private static readonly Guid PurgeRuleGuid = new Guid("e8c63650-70b7-435a-9010-ec97660c1bda");

        private static ICollection<ElementId> FindUnusedViaAdviser(Document doc)
        {
            var result = new List<ElementId>();
            PerformanceAdviser adviser = PerformanceAdviser.GetPerformanceAdviser();
            PerformanceAdviserRuleId rule = adviser.GetAllRuleIds().FirstOrDefault(r => r.Guid == PurgeRuleGuid);
            if (rule == null) return result;
            foreach (FailureMessage m in adviser.ExecuteRules(doc, new List<PerformanceAdviserRuleId> { rule }))
                result.AddRange(m.GetFailingElements());
            return result;
        }
#endif

        /// <summary>Runs purge passes until nothing more can be removed (max 6 passes).</summary>
        public static PurgeResult Purge(Document doc)
        {
            var result = new PurgeResult();
            var failedIds = new HashSet<long>();

            for (int pass = 1; pass <= 6; pass++)
            {
                List<Candidate> unused = FindUnused(doc).Where(c => !failedIds.Contains(c.Id.IntegerValueCompat())).ToList();
                if (unused.Count == 0) break;
                result.Passes = pass;

                int before = result.Deleted;
                DeleteAll(doc, unused, result, failedIds);
                result.Log.Add($"Pass {pass}: {unused.Count} unused, {result.Deleted - before} deleted");
                if (result.Deleted == before) break;
            }
            return result;
        }

        private static void DeleteAll(Document doc, List<Candidate> items, PurgeResult result, HashSet<long> failedIds)
        {
            // fast path: one transaction for the whole batch
            using (var t = new Transaction(doc, "BeGenSt: purge unused"))
            {
                SetSilent(t);
                t.Start();
                bool ok = true;
                try
                {
                    doc.Delete(items.Select(i => i.Id).ToList());
                }
                catch
                {
                    ok = false;
                }
                if (ok && t.Commit() == TransactionStatus.Committed)
                {
                    foreach (Candidate c in items) Count(result, c);
                    return;
                }
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            }

            // slow path: element by element, skipping the ones Revit refuses to delete
            foreach (Candidate c in items)
            {
                if (doc.GetElement(c.Id) == null) continue; // already removed with a previous element
                using (var t = new Transaction(doc, "BeGenSt: purge element"))
                {
                    SetSilent(t);
                    t.Start();
                    try
                    {
                        doc.Delete(c.Id);
                        if (t.Commit() == TransactionStatus.Committed) { Count(result, c); continue; }
                    }
                    catch
                    {
                        if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    }
                    result.Failed++;
                    failedIds.Add(c.Id.IntegerValueCompat());
                    result.Log.Add($"  not deleted: {c.Kind} '{c.Name}'");
                }
            }
        }

        private static void Count(PurgeResult r, Candidate c)
        {
            r.Deleted++;
            r.ByKind[c.Kind] = r.ByKind.TryGetValue(c.Kind, out int n) ? n + 1 : 1;
        }

        private static void SetSilent(Transaction t)
        {
            FailureHandlingOptions o = t.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(new SwallowWarnings());
            o.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(o);
        }

        private class SwallowWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (FailureMessageAccessor f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        public static string KindOf(Element e)
        {
            switch (e)
            {
                case CADLinkType _: return "CAD import (DWG/DXF)";
                case ImportInstance _: return "CAD import (DWG/DXF)";
                case ImageType _: return "Raster image";
                case Material _: return "Material";
                case AppearanceAssetElement _: return "Appearance asset";
                case FamilySymbol _: return "Nested family type";
                case Family _: return "Nested family";
                case LinePatternElement _: return "Line pattern";
                case FillPatternElement _: return "Fill pattern";
                case View v when v.IsTemplate: return "View template";
                case GraphicsStyle _: return "Line / object style";
                case ElementType _: return "Type";
                default:
                    return e.Category?.Name ?? e.GetType().Name;
            }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name; } catch { return e.Id.ToString(); }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "—";
            return bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024.0:F0} KB";
        }
    }
}
