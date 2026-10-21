namespace BeGenSt.Data
{
    public class RFAGenSettings
    {
        /// <summary>Active AI provider: "Claude", "Gemini" or "Qwen".</summary>
        public string Provider { get; set; } = LlmProviders.Claude;

        // ---- Anthropic Claude (property names kept for compatibility with existing RFAGen.json) ----
        public string ClaudeApiKey { get; set; } = "";
        public string ModelName { get; set; } = "claude-sonnet-5-5";
        public List<string> AvailableModels { get; set; } = DefaultClaudeModels();

        // ---- Google Gemini ----
        public string GeminiApiKey { get; set; } = "";
        public string GeminiModelName { get; set; } = "gemini-3.8-flash";
        public List<string> GeminiAvailableModels { get; set; } = DefaultGeminiModels();

        // ---- Qwen ----
        public string QwenApiKey { get; set; } = "";
        public string QwenModelName { get; set; } = "qwen3.7-plus";
        /// <summary>OpenAI-compatible base URL of Alibaba Cloud Model Studio (keys are region-specific).</summary>
        public string QwenBaseUrl { get; set; } = DefaultQwenBaseUrl;

        public const string DefaultQwenBaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";

        // ---- OpenRouter (used for Qwen model ids of the form "vendor/model", e.g. qwen/qwen3.8-27b:free) ----
        public string OpenRouterApiKey { get; set; } = "";
        public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

        /// <summary>Model ids with a slash are OpenRouter ids and are sent to OpenRouter.</summary>
        public static bool IsOpenRouterModel(string modelName) => (modelName ?? "").Contains("/");
        public List<string> QwenAvailableModels { get; set; } = DefaultQwenModels();

        // ---- PreGenerate pipeline ----
        /// <summary>Analyse the sources first and ask the user about conflicting dimensions.</summary>
        public bool CheckInputConsistency { get; set; } = true;
        /// <summary>Verify the generated model against the sources before the preview.</summary>
        public bool VerifyBeforePreview { get; set; } = true;

        // ---- Parameter mapping ----
        /// <summary>Fill the parameters of the open family / template (names + language) instead of creating new ones.</summary>
        public bool UseFamilyParameters { get; set; } = false;
        /// <summary>Map technical data onto definitions of a shared parameter file (ФОП).</summary>
        public bool UseSharedParameterFile { get; set; } = false;
        public string SharedParameterFilePath { get; set; } = "";

        public static List<string> DefaultClaudeModels()
        {
            return new List<string>
            {
                "claude-sonnet-5-5",
                "claude-opus-5-5",
                "claude-haiku-4-5-20251001",
                "claude-opus-4-8",
                "claude-sonnet-4-6"
            };
        }

        /// <summary>Qwen models with image input (PDF pages are rasterized locally, Qwen has no PDF input).</summary>
        public static List<string> DefaultQwenModels()
        {
            return new List<string>
            {
                "qwen3.7-plus",
                "qwen3.8-max",
                "qwen3.7-flash",
                "qwen3.6-plus",
                "qwen3-vl-plus",
                "qwen/qwen3.8-27b:free"   // OpenRouter, free tier (needs the OpenRouter key)
            };
        }

        public static List<string> DefaultGeminiModels()
        {
            return new List<string>
            {
                "gemini-3.8-flash",
                "gemini-3.7-flash",
                "gemini-3.5-flash",
                "gemini-3.1-pro-preview",
                "gemini-3-flash-preview"
            };
        }
    }
}
