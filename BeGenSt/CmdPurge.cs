using BeGenSt.Data;

namespace BeGenSt
{
    /// <summary>Purge: removes unused "tails" from the family and saves it compacted.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdPurge : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;
            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show("BeGenSt Purge", "Purge works in the Family Editor. Open the family (.rfa) first.");
                return Result.Cancelled;
            }

            string path = doc.PathName;
            long sizeBefore = SizeOf(path);

            // mode: purge unused only, or full clean of the family content before a new generation
            FamilyCleaner.Summary content = null;
            try { content = FamilyCleaner.Analyse(doc); } catch { }
            if (content != null && content.Total > 0)
            {
                var mode = new TaskDialog("BeGenSt Purge")
                {
                    MainInstruction = "What should be removed?",
                    MainContent = "The family contains its own content:\n" + content.Text(content.Found),
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                mode.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Purge unused elements",
                    "Like Revit Purge Unused (several passes) + compact save. Geometry and parameters stay.");
                mode.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Full clean before generation (TypeGen / RFAGen)",
                    "Removes geometry, connectors, nested families, dimensions, lines, own reference planes, lookup tables and " +
                    "size_lookup formulas, then purges unused elements. Family parameters are kept.");
                TaskDialogResult m = mode.Show();
                if (m == TaskDialogResult.Cancel || m == TaskDialogResult.Close) return Result.Cancelled;
                if (m == TaskDialogResult.CommandLink2)
                {
                    FamilyCleaner.Summary cleaned;
                    try { cleaned = FamilyCleaner.Clean(doc); }
                    catch (Exception ex)
                    {
                        TaskDialog.Show("BeGenSt Purge", "Full clean failed: " + ex.Message);
                        return Result.Failed;
                    }
                    FamilyPurger.PurgeResult pr = null;
                    try { pr = FamilyPurger.Purge(doc); } catch { }
                    var log = new List<string> { "FULL CLEAN" };
                    log.AddRange(cleaned.Removed.Select(k => $"{k.Key}: {k.Value}"));
                    log.AddRange(cleaned.Log);
                    if (pr != null) { log.Add($"PURGE: {pr.Deleted} deleted in {pr.Passes} pass(es)"); log.AddRange(pr.Log); }
                    WriteLog(doc, log);
                    OfferCompactSave(doc, path, sizeBefore, "Full clean done:\n" + cleaned.Text(cleaned.Removed) +
                        (pr != null && pr.Deleted > 0 ? $"\n  • Unused elements purged: {pr.Deleted}" : ""));
                    return Result.Succeeded;
                }
            }

            List<FamilyPurger.Candidate> unused;
            try { unused = FamilyPurger.FindUnused(doc); }
            catch (Exception ex)
            {
                TaskDialog.Show("BeGenSt Purge", "Could not analyse the family: " + ex.Message);
                return Result.Failed;
            }

            if (unused.Count == 0)
            {
                OfferCompactSave(doc, path, sizeBefore, "Nothing to purge — the family has no unused elements.");
                return Result.Succeeded;
            }

            string summary = string.Join("\n", unused.GroupBy(c => c.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => $"  • {g.Key}: {g.Count()}"));

            var confirm = new TaskDialog("BeGenSt Purge")
            {
                MainInstruction = $"Remove {unused.Count} unused element(s) from the family?",
                MainContent = summary + "\n\nFurther passes may remove elements that were used only by these." +
                              (sizeBefore > 0 ? $"\n\nCurrent file size: {FamilyPurger.FormatSize(sizeBefore)}" : ""),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;

            FamilyPurger.PurgeResult r;
            try { r = FamilyPurger.Purge(doc); }
            catch (Exception ex)
            {
                TaskDialog.Show("BeGenSt Purge", "Purge failed: " + ex.Message);
                return Result.Failed;
            }

            string done = $"Removed {r.Deleted} element(s) in {r.Passes} pass(es)" +
                          (r.Failed > 0 ? $", {r.Failed} could not be deleted" : "") + ":\n" +
                          string.Join("\n", r.ByKind.OrderByDescending(k => k.Value).Select(k => $"  • {k.Key}: {k.Value}"));
            WriteLog(doc, r.Log);
            OfferCompactSave(doc, path, sizeBefore, done);
            return Result.Succeeded;
        }

        /// <summary>Revit only shrinks the file when it is saved with compaction (or via Save As).</summary>
        internal static void OfferCompactSave(Document doc, string path, long sizeBefore, string text)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                TaskDialog.Show("BeGenSt Purge", text + "\n\nThe family has not been saved yet — use Save As to write a compact file.");
                return;
            }

            var ask = new TaskDialog("BeGenSt Purge")
            {
                MainInstruction = "Save the family compacted now?",
                MainContent = text + "\n\nRevit keeps deleted data inside the file until it is saved with compaction.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            if (ask.Show() != TaskDialogResult.Yes) return;

            try
            {
                doc.Save(new SaveOptions { Compact = true });
                long after = SizeOf(path);
                TaskDialog.Show("BeGenSt Purge",
                    $"Saved compacted: {FamilyPurger.FormatSize(sizeBefore)} → {FamilyPurger.FormatSize(after)}\n{path}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("BeGenSt Purge", "Could not save: " + ex.Message + "\nUse Save As to write a compact copy.");
            }
        }

        internal static long SizeOf(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        private static void WriteLog(Document doc, IEnumerable<string> lines)
        {
            try
            {
                string folder = RFAGenSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-purge.txt");
                File.WriteAllLines(file, new[] { "Family: " + doc.Title }.Concat(lines));
            }
            catch { }
        }

        internal static PushButtonData GetButtonData()
        {
            Helpers.ButtonDataClass myButtonData = new Helpers.ButtonDataClass(
                "btnBeGenStPurge",
                "Purge",
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.purge_32,
                Properties.Resources.purge_16,
                "Purge: remove unused elements left in the family, or a full clean of the family content before a new generation (CAD import types and line styles after a deleted DWG, " +
                "unused materials, nested families, patterns, images ...) and save the family compacted to reduce its file size.");
            return myButtonData.Data;
        }
    }
}
