using System.Windows.Interop;
using BeGenSt.Data;
using BeGenSt.UI;

namespace BeGenSt
{
    [Transaction(TransactionMode.Manual)]
    public class CmdPreGenerate : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            if (!doc.IsFamilyDocument)
            {
                TaskDialog.Show("RFAGen",
                    "RFAGen must be run inside the Family Editor.\n\nOpen or create a family document first.");
                return Result.Failed;
            }

            var settingsManager = new RFAGenSettingsManager();
            if (!settingsManager.HasAnyApiKey())
            {
                TaskDialog.Show("RFAGen",
                    "Please configure an API key first (Anthropic Claude or Google Gemini).\n\nOpen BeGenSt › PDF&Pic2RFA › Settings to enter your key.");
                return Result.Failed;
            }

            var generateWindow = new GenerateWindow(settingsManager, ParameterTargetContext.FromFamily(doc));
            new WindowInteropHelper(generateWindow).Owner = uiapp.MainWindowHandle;

            if (generateWindow.ShowDialog() != true)
                return Result.Cancelled;

            string familyJson = generateWindow.FamilyJson;
            string familyName = generateWindow.FamilyName;
            SaveJson(familyJson, "api-response", familyName);
            string sourceFilePath = SaveSourceFiles(generateWindow.Attachments, familyName);

            var previewWindow = new PreviewWindow(familyJson, generateWindow.Provider, generateWindow.ApiKey,
                generateWindow.ModelName, generateWindow.Attachments, sourceFilePath, familyName,
                generateWindow.VerificationSummary);
            new WindowInteropHelper(previewWindow).Owner = uiapp.MainWindowHandle;

            if (previewWindow.ShowDialog() == true && previewWindow.GenerateRequested)
            {
                familyName = previewWindow.FamilyName;
                string finalJson = InjectFamilyName(previewWindow.FinalFamilyJson, familyName);
                SaveJson(finalJson, "final", familyName);

                var generator = new FamilyGenerator();
                Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;
                var paramOptions = new ParameterApplyOptions
                {
                    ReuseExisting = generateWindow.ReuseFamilyParameters,
                    SharedParameterFile = generateWindow.SharedParameterFile
                };
                GenerationResult genResult = generator.Execute(doc, app, finalJson, paramOptions);
                string reportPath = SaveReport(genResult, familyName);

                if (genResult.Success)
                {
                    bool allCreated = genResult.GeometryCount == genResult.ExpectedGeometryCount;
                    string summary = (allCreated ? "Family created successfully!\n\n" :
                            $"Family created, but {genResult.ExpectedGeometryCount - genResult.GeometryCount} of " +
                            $"{genResult.ExpectedGeometryCount} geometry elements from the preview could NOT be built in Revit.\n\n") +
                        $"Parameters: {genResult.ParameterCount}\n" +
                        $"Reference Planes: {genResult.RefPlaneCount}\n" +
                        $"Geometry Elements: {genResult.GeometryCount} / {genResult.ExpectedGeometryCount}";

                    if (genResult.ErrorCount > 0)
                        summary += $"\n\nWarnings: {genResult.ErrorCount}\n" +
                            string.Join("\n", genResult.Errors);

                    if (reportPath != null)
                        summary += $"\n\nReport: {reportPath}";

                    TaskDialog.Show(allCreated ? "RFAGen - Complete" : "RFAGen - Completed with warnings", summary);
                }
                else
                {
                    string errorMsg = genResult.ErrorMessage ?? "Unknown error during generation.";
                    if (genResult.Errors.Count > 0)
                        errorMsg += "\n\nDetails:\n" + string.Join("\n", genResult.Errors);

                    TaskDialog.Show("RFAGen - Error", errorMsg);
                    return Result.Failed;
                }
            }

            return Result.Succeeded;
        }

        /// <summary>Copies all input files to the Logs folder; returns the path of the first copy.</summary>
        private static string SaveSourceFiles(List<InputAttachment> attachments, string familyName = null)
        {
            if (attachments == null || attachments.Count == 0) return null;
            string firstPath = null;
            try
            {
                string folder = RFAGenSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);

                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string slug = SanitizeFileName(familyName);
                for (int i = 0; i < attachments.Count; i++)
                {
                    InputAttachment att = attachments[i];
                    if (att?.Bytes == null) continue;
                    string ext = InputAttachment.GetExtension(att.MimeType);
                    string suffix = attachments.Count > 1 ? $"-source{i + 1}" : "-source";
                    string baseName = string.IsNullOrEmpty(slug) ? $"{timestamp}{suffix}" : $"{timestamp}-{slug}{suffix}";
                    string path = Path.Combine(folder, baseName + ext);
                    File.WriteAllBytes(path, att.Bytes);
                    if (firstPath == null) firstPath = path;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save source files: {ex.Message}");
            }
            return firstPath;
        }

        internal static string SaveReport(GenerationResult r, string familyName)
        {
            try
            {
                string folder = RFAGenSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);
                string slug = SanitizeFileName(familyName);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string path = Path.Combine(folder, string.IsNullOrEmpty(slug)
                    ? $"{stamp}-generation-report.txt" : $"{stamp}-{slug}-generation-report.txt");

                var lines = new List<string>
                {
                    $"RFAGen generation report  {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Success: {r.Success}  {r.ErrorMessage}",
                    $"Parameters: {r.ParameterCount}, reference planes: {r.RefPlaneCount}, geometry: {r.GeometryCount}/{r.ExpectedGeometryCount}",
                    ""
                };
                lines.AddRange(r.Report);
                if (r.Errors.Count > 0)
                {
                    lines.Add("");
                    lines.Add("Errors:");
                    lines.AddRange(r.Errors);
                }
                File.WriteAllLines(path, lines);
                return path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save report: {ex.Message}");
                return null;
            }
        }

        internal static void SaveJson(string json, string label, string familyName = null)
        {
            try
            {
                string folder = RFAGenSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);

                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string slug = SanitizeFileName(familyName);
                string fileName = string.IsNullOrEmpty(slug)
                    ? $"{timestamp}-{label}.json"
                    : $"{timestamp}-{slug}-{label}.json";
                File.WriteAllText(Path.Combine(folder, fileName), json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save JSON log: {ex.Message}");
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            char[] invalid = Path.GetInvalidFileNameChars();
            string sanitized = new string(name
                .Where(c => !invalid.Contains(c))
                .ToArray())
                .Trim()
                .Replace(' ', '-')
                .ToLowerInvariant();
            while (sanitized.Contains("--"))
                sanitized = sanitized.Replace("--", "-");
            return sanitized.Trim('-');
        }

        private static string InjectFamilyName(string json, string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return json;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                using var ms = new System.IO.MemoryStream();
                using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name == "metadata")
                        {
                            writer.WritePropertyName("metadata");
                            writer.WriteStartObject();
                            writer.WriteString("name", familyName);
                            foreach (var metaProp in prop.Value.EnumerateObject())
                            {
                                if (metaProp.Name != "name")
                                    metaProp.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to inject family name: {ex.Message}");
                return json;
            }
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnPreGenerate";
            string buttonTitle = "Generate";

            Helpers.ButtonDataClass myButtonData = new Helpers.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.pregen_32,
                Properties.Resources.pregen_16,
                "Generate: build a Revit family from up to 3 files (PDF drawing, JPG photo, PNG sketch) with AI (Claude, Gemini or Qwen), check it and preview it before RFAGen. Options: fill the family/template parameters or a shared parameter file (ФОП) in the family's language.");

            return myButtonData.Data;
        }
    }
}
