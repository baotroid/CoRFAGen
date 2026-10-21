using System.Text;
using System.Threading.Tasks;

namespace BeGenSt.Data
{
    public class PipelineResult
    {
        public bool Cancelled { get; set; }
        public string FamilyJson { get; set; }
        public string AnalysisJson { get; set; }
        public List<ConflictDecision> Decisions { get; set; } = new List<ConflictDecision>();
        public string DecisionsText { get; set; }
        public List<string> VerificationLines { get; } = new List<string>();
        public string GeometryReport { get; set; }

        public string VerificationSummary =>
            VerificationLines.Count == 0 ? "" : string.Join("\n", VerificationLines);
    }

    /// <summary>
    /// PreGenerate pipeline: analyse sources → ask the user about conflicts → generate →
    /// check geometry (C#) and verify against sources (AI) → return JSON for the preview.
    /// </summary>
    public class GenerationPipeline
    {
        private readonly FamilyAiService _ai;
        private readonly Action<string> _progress;

        public GenerationPipeline(FamilyAiService ai, Action<string> progress)
        {
            _ai = ai;
            _progress = progress ?? (_ => { });
        }

        /// <param name="resolveConflicts">Called on the UI thread with the analysis; returns the user's
        /// decisions, or null when the user cancels.</param>
        public async Task<PipelineResult> RunAsync(List<InputAttachment> attachments, string userContext,
            bool checkInput, bool verify, Func<SourceAnalysis, List<ConflictDecision>> resolveConflicts)
        {
            var result = new PipelineResult();
            SourceAnalysis analysis = null;

            // ---- 1. analysis & consistency check
            if (checkInput)
            {
                _progress($"Step 1/3 · Checking source data with {_ai.DisplayName} (views, dimensions, technical data, conflicts)...");
                try
                {
                    string analysisJson = await Task.Run(() => _ai.AnalyzeAsync(attachments, userContext));
                    analysis = SourceAnalysis.Parse(analysisJson);
                    result.AnalysisJson = analysisJson;
                    Logs.Save(analysisJson, "source-analysis.json");
                }
                catch (LlmException) { throw; }
                catch (Exception ex)
                {
                    // analysis is an aid — do not block generation if it cannot be parsed
                    result.VerificationLines.Add($"Source analysis skipped: {ex.Message}");
                    analysis = null;
                }

                if (analysis != null && (analysis.Conflicts.Count > 0 || resolveConflicts != null))
                {
                    if (analysis.Conflicts.Count > 0)
                    {
                        _progress($"Found {analysis.Conflicts.Count} conflict(s) in the source data — waiting for your decision...");
                        List<ConflictDecision> decisions = resolveConflicts?.Invoke(analysis);
                        if (decisions == null)
                        {
                            result.Cancelled = true;
                            return result;
                        }
                        result.Decisions = decisions;
                        result.DecisionsText = SourceAnalysis.DecisionsText(decisions);
                        Logs.Save(result.DecisionsText, "user-decisions.txt");
                    }
                }
            }

            // ---- 2. generation
            _progress($"Step 2/3 · Generating the family with {_ai.DisplayName}. This may take a few minutes...");
            string familyJson = await Task.Run(() =>
                _ai.GenerateAsync(attachments, userContext, result.AnalysisJson, result.DecisionsText));
            string err = FamilyAiService.ValidateFamilyJson(familyJson);
            if (err != null) throw new LlmException(err, familyJson);
            Logs.Save(familyJson, "generated-before-check.json");

            // ---- 3. verification
            GeometryInspector gi = GeometryInspector.Inspect(familyJson, analysis);
            result.GeometryReport = gi.Report();

            if (verify)
            {
                _progress("Step 3/3 · Checking the model against the source drawing and dimensions...");
                try
                {
                    string response = await Task.Run(() => _ai.VerifyAsync(attachments, userContext, result.AnalysisJson,
                        result.DecisionsText, familyJson, result.GeometryReport));
                    Logs.Save(response, "verification.json");
                    familyJson = ApplyVerification(response, familyJson, result);
                }
                catch (LlmException ex)
                {
                    result.VerificationLines.Add($"AI verification failed: {ex.Message.Split('\n')[0]} — the unverified model is shown.");
                }
                catch (Exception ex)
                {
                    result.VerificationLines.Add($"AI verification skipped: {ex.Message}");
                }

                gi = GeometryInspector.Inspect(familyJson, analysis);
                result.GeometryReport = gi.Report();
            }

            if (gi.Boxes.Count > 0)
                result.VerificationLines.Add($"Model size X×Y×Z: {gi.SizeX:0.#} × {gi.SizeY:0.#} × {gi.SizeZ:0.#} {gi.Units}" +
                                             (analysis != null ? $"  (source: {analysis.Summary()})" : ""));
            foreach (string issue in gi.Issues)
                result.VerificationLines.Add("⚠ " + issue);
            if (gi.Issues.Count == 0 && verify)
                result.VerificationLines.Add("✓ Automatic geometry check passed.");

            result.FamilyJson = familyJson;
            Logs.Save(result.GeometryReport + "\n\n" + result.VerificationSummary, "check-report.txt");
            return result;
        }

        private static string ApplyVerification(string response, string familyJson, PipelineResult result)
        {
            using (JsonDocument doc = JsonDocument.Parse(response))
            {
                JsonElement r = doc.RootElement;

                // model returned a bare family instead of the wrapper
                if (r.TryGetProperty("metadata", out _) && r.TryGetProperty("geometry", out _))
                {
                    result.VerificationLines.Add("AI verification returned a corrected model.");
                    return response;
                }

                string status = r.TryGetProperty("status", out JsonElement st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString() : "ok";

                if (r.TryGetProperty("issues", out JsonElement issues) && issues.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement i in issues.EnumerateArray())
                    {
                        string sev = i.TryGetProperty("severity", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "";
                        string d = i.TryGetProperty("description", out JsonElement de) && de.ValueKind == JsonValueKind.String
                            ? de.GetString() : i.GetRawText();
                        result.VerificationLines.Add($"• fixed{(string.IsNullOrEmpty(sev) ? "" : " [" + sev + "]")}: {d}");
                    }
                }

                if (status == "fixed" && r.TryGetProperty("family", out JsonElement fam) && fam.ValueKind == JsonValueKind.Object)
                {
                    string fixedJson = fam.GetRawText();
                    if (FamilyAiService.ValidateFamilyJson(fixedJson) == null)
                    {
                        result.VerificationLines.Insert(0, "AI verification corrected the model:");
                        return fixedJson;
                    }
                    result.VerificationLines.Add("The corrected model was invalid and was ignored.");
                    return familyJson;
                }

                result.VerificationLines.Insert(0, "✓ AI verification: model matches the source data.");
                return familyJson;
            }
        }
    }

    internal static class Logs
    {
        public static void Save(string content, string suffix)
        {
            try
            {
                string folder = RFAGenSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}"), content ?? "",
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Log save failed: {ex.Message}");
            }
        }
    }
}
