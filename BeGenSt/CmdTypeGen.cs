using System.Windows.Interop;
using BeGenSt.Data;
using BeGenSt.UI;

namespace BeGenSt
{
    /// <summary>TypeGen: family with several types from a catalog table (one row = one type).</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdTypeGen : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsFamilyDocument)
            {
                TaskDialog.Show("TypeGen", "TypeGen must be run inside the Family Editor.\n\nOpen or create a family document first.");
                return Result.Cancelled;
            }

            var settings = new RFAGenSettingsManager();
            ParameterTargetContext ctx = ParameterTargetContext.FromFamily(doc);
            RFAGenSettings s = settings.GetSettings();
            ctx.UseFamilyParameters = s.UseFamilyParameters;
            if (s.UseSharedParameterFile && File.Exists(s.SharedParameterFilePath))
            {
                ctx.UseSharedFile = true;
                ctx.SharedFilePath = s.SharedParameterFilePath;
                try { ctx.SharedFileParameters.AddRange(ParameterTargetContext.ParseSharedParameterFile(s.SharedParameterFilePath)); } catch { }
            }

            var window = new TypeGenWindow(settings, ctx);
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            if (window.ShowDialog() != true || string.IsNullOrEmpty(window.CatalogJsonResult))
                return Result.Cancelled;

            string json = window.CatalogJsonResult;
            if (!ConfirmForeignContent(doc)) return Result.Cancelled;
            CmdPreGenerate.SaveJson(json, "catalog-final", doc.Title);

            var options = new ParameterApplyOptions
            {
                ReuseExisting = ctx.UseFamilyParameters,
                SharedParameterFile = ctx.UseSharedFile ? ctx.SharedFilePath : null
            };
            GenerationResult r = new FamilyGenerator().Execute(doc, uiapp.Application, json, options);
            if (!r.Success)
            {
                CmdPreGenerate.SaveReport(r, doc.Title + "-types");
                TaskDialog.Show("TypeGen - Error", (r.ErrorMessage ?? "Generation failed.") +
                    (r.Errors.Count > 0 ? "\n\n" + string.Join("\n", r.Errors.Take(15)) : ""));
                return Result.Failed;
            }

            // automatic clean-up of the template "tails" (agreed: purge after creation)
            string purgeText = "";
            try
            {
                FamilyPurger.PurgeResult p = FamilyPurger.Purge(doc);
                if (p.Deleted > 0) purgeText = $"\nPurge: {p.Deleted} unused element(s) removed.";
            }
            catch (Exception ex) { purgeText = "\nPurge skipped: " + ex.Message; }
            r.Report.Add("PURGE:" + purgeText.Replace("\n", " "));
            string report = CmdPreGenerate.SaveReport(r, doc.Title + "-types");

            int types = doc.FamilyManager.Types.Size;
            string summary = $"Family types created: {types}\n" +
                             $"Geometry elements: {r.GeometryCount} / {r.ExpectedGeometryCount}\n" +
                             $"Parameters: {r.ParameterCount}" + purgeText +
                             (r.Errors.Count > 0 ? $"\n\nWarnings ({r.Errors.Count}):\n" + string.Join("\n", r.Errors.Take(10)) : "") +
                             (report != null ? $"\n\nReport: {report}" : "");

            string path = doc.PathName;
            CmdPurge.OfferCompactSave(doc, path, CmdPurge.SizeOf(path), summary);
            return Result.Succeeded;
        }

        /// <summary>
        /// The catalog generator replaces the geometry, but other content of an existing family (nested families,
        /// own lookup tables, parameters driven by size_lookup, dimensioned reference planes) stays and may break
        /// the new types. Recommend a clean family.
        /// </summary>
        private static bool ConfirmForeignContent(Document doc)
        {
            var found = new List<string>();
            try
            {
                int nested = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).GetElementCount();
                if (nested > 0) found.Add($"nested family instances: {nested}");
                FamilySizeTableManager mgr = FamilySizeTableManager.GetFamilySizeTableManager(doc, doc.OwnerFamily.Id);
                var tables = mgr?.GetAllSizeTableNames().Where(n => n != CatalogJson.LookupTableName).ToList() ?? new List<string>();
                if (tables.Count > 0) found.Add("lookup tables: " + string.Join(", ", tables));
                int lookupParams = doc.FamilyManager.Parameters.Cast<FamilyParameter>()
                    .Count(p => !string.IsNullOrEmpty(p.Formula) && p.Formula.Contains("size_lookup") && !p.Formula.Contains(CatalogJson.LookupTableName));
                if (lookupParams > 0) found.Add($"parameters driven by size_lookup: {lookupParams}");
                int dims = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).GetElementCount();
                if (dims > 0) found.Add($"dimensions: {dims}");
            }
            catch { }
            if (found.Count == 0) return true;

            var td = new TaskDialog("TypeGen")
            {
                MainInstruction = "This family already contains its own content",
                MainContent = string.Join("\n", found.Select(f => "• " + f)) +
                              "\n\nTypeGen replaces the geometry, but this content would stay and can break the new types " +
                              "(errors \"Cannot make extrusion\" when switching types, parts of another actuator).",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Full clean, then generate (recommended)",
                "Removes geometry, nested families, dimensions, own reference planes, lookup tables and size_lookup formulas. Parameters are kept.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Generate into this family as it is");
            TaskDialogResult res = td.Show();
            if (res == TaskDialogResult.CommandLink2) return true;
            if (res != TaskDialogResult.CommandLink1) return false;
            try
            {
                FamilyCleaner.Summary cleaned = FamilyCleaner.Clean(doc);
                try { FamilyPurger.Purge(doc); } catch { }
                var cr = new GenerationResult { Success = true };
                cr.Report.Add("FULL CLEAN before TypeGen");
                cr.Report.AddRange(cleaned.Removed.Select(k => $"{k.Key}: {k.Value}"));
                cr.Report.AddRange(cleaned.Log);
                CmdPreGenerate.SaveReport(cr, doc.Title + "-clean");
                return true;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("TypeGen", "Full clean failed: " + ex.Message);
                return false;
            }
        }

        internal static PushButtonData GetButtonData()
        {
            var data = new Helpers.ButtonDataClass(
                "btnBeGenStTypeGen",
                "TypeGen",
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.typegen_32,
                Properties.Resources.typegen_16,
                "TypeGen: family with several types from a catalog sheet — reads the type-size table (DN, PN, D1, L, H, mass…), " +
                "builds one parametric model and creates one Revit type per row (e.g. «КЗРр 25с947нж DN50 PN16»). " +
                "The actuator can be hidden with an instance check box.");
            return data.Data;
        }
    }
}
