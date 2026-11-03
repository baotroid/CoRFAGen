using System.Threading.Tasks;

namespace CoRFAGen.Data
{
    /// <summary>
    /// Provider-independent facade: generate / refine a family JSON from images and PDF drawings.
    /// </summary>
    public class FamilyAiService
    {
        private readonly ILlmClient _client;

        public FamilyAiService(string provider, string apiKey, string modelName)
        {
            Provider = LlmProviders.Normalize(provider);
            ApiKey = apiKey;
            ModelName = modelName;
            _client = LlmClientFactory.Create(Provider, apiKey, modelName);
        }

        public string Provider { get; }
        public string ApiKey { get; }
        public string ModelName { get; }
        /// <summary>Optional "use family / shared parameters" instructions appended to every request.</summary>
        public string ParameterMappingText { get; set; }

        private string WithMapping(string userText) =>
            string.IsNullOrWhiteSpace(ParameterMappingText) ? userText : userText + "\n\n" + ParameterMappingText;

        public string DisplayName => $"{LlmProviders.DisplayName(Provider)} ({ModelName})";

        /// <summary>Throws LlmException if the attachments are too large for the selected provider.</summary>
        public void ValidateAttachments(IList<InputAttachment> attachments)
        {
            if (attachments == null) return;

            foreach (InputAttachment att in attachments)
            {
                if (att.IsPdf && !InputAttachment.LooksLikePdf(att.Bytes))
                    throw new LlmException($"File '{att.FileName}' is not a valid PDF.", null);
            }

            long total = attachments.Sum(a => (long)(a.Bytes?.Length ?? 0));
            if (total > _client.MaxInlineBytes)
            {
                throw new LlmException(
                    $"Input files are too large for {LlmProviders.DisplayName(Provider)}: " +
                    $"{total / 1024.0 / 1024.0:F1} MB (limit ≈ {_client.MaxInlineBytes / 1024 / 1024} MB). " +
                    "Reduce the PDF (remove extra pages, export with lower image resolution) or compress the images.",
                    null);
            }
        }

        public Task<string> AnalyzeAsync(IList<InputAttachment> attachments, string userContext)
        {
            string system = FamilyPromptBuilder.BuildAnalysisSystemPrompt(SkillResources.GetSkillPrompt());
            string userText = FamilyPromptBuilder.BuildAnalysisUserText(attachments, userContext);
            return _client.SendAsync(system, attachments, WithMapping(userText));
        }

        public Task<string> GenerateAsync(IList<InputAttachment> attachments, string userContext,
            string analysisJson = null, string decisionsText = null)
        {
            string system = FamilyPromptBuilder.BuildSystemPrompt(SkillResources.GetSkillPrompt(), SkillResources.GetFamilySchema());
            string userText = FamilyPromptBuilder.BuildGenerateUserText(attachments, userContext, analysisJson, decisionsText);
            return _client.SendAsync(system, attachments, WithMapping(userText));
        }

        public Task<string> VerifyAsync(IList<InputAttachment> attachments, string userContext,
            string analysisJson, string decisionsText, string familyJson, string geometryReport)
        {
            string system = FamilyPromptBuilder.BuildVerifySystemPrompt(SkillResources.GetSkillPrompt(), SkillResources.GetFamilySchema());
            string userText = FamilyPromptBuilder.BuildVerifyUserText(attachments, userContext, analysisJson, decisionsText,
                familyJson, geometryReport);
            return _client.SendAsync(system, attachments, WithMapping(userText));
        }

        public Task<string> RefineAsync(string currentJson, string instruction, IList<InputAttachment> attachments)
        {
            string system = FamilyPromptBuilder.BuildSystemPrompt(SkillResources.GetSkillPrompt(), SkillResources.GetFamilySchema());
            string userText = FamilyPromptBuilder.BuildRefineUserText(currentJson, instruction, attachments);
            return _client.SendAsync(system, attachments, WithMapping(userText));
        }

        /// <summary>TypeGen 1: read the type-size tables of a catalog sheet.</summary>
        public Task<string> ExtractCatalogAsync(IList<InputAttachment> attachments, string userContext)
        {
            return _client.SendAsync(CatalogPromptBuilder.ExtractSystemPrompt, attachments,
                CatalogPromptBuilder.BuildExtractUserText(attachments, userContext));
        }

        /// <summary>TypeGen 2: one parametric family template valid for all rows.</summary>
        public Task<string> GenerateCatalogTemplateAsync(IList<InputAttachment> attachments, string extractionJson,
            string representativeRow, string smallestRow, string largestRow, string language, string userContext)
        {
            string system = CatalogPromptBuilder.BuildTemplateSystemPrompt(SkillResources.GetSkillPrompt(), SkillResources.GetFamilySchema());
            string user = CatalogPromptBuilder.BuildTemplateUserText(attachments, extractionJson, representativeRow,
                smallestRow, largestRow, language, userContext);
            return _client.SendAsync(system, attachments, WithMapping(user));
        }

        /// <summary>Plain text request without attachments (used for parameter mapping / translation).</summary>
        public Task<string> SendTextAsync(string systemPrompt, string userText)
        {
            return _client.SendAsync(systemPrompt, new List<InputAttachment>(), userText);
        }

        /// <summary>Returns null if the JSON looks like a family definition, otherwise an error message.</summary>
        public static string ValidateFamilyJson(string json)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                        !doc.RootElement.TryGetProperty("metadata", out _) ||
                        !doc.RootElement.TryGetProperty("geometry", out _))
                        return "API returned JSON but it doesn't match the family schema (missing metadata or geometry).";
                }
                return null;
            }
            catch (JsonException ex)
            {
                return $"Invalid JSON in API response: {ex.Message}";
            }
        }
    }
}
