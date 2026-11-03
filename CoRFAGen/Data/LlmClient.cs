using System.Threading.Tasks;

namespace CoRFAGen.Data
{
    /// <summary>Supported AI providers. Stored in settings as plain strings.</summary>
    public static class LlmProviders
    {
        public const string Claude = "Claude";
        public const string Gemini = "Gemini";
        public const string Qwen = "Qwen";

        public static readonly List<string> All = new List<string> { Claude, Gemini, Qwen };

        public static string Normalize(string provider)
        {
            if (string.Equals(provider, Gemini, StringComparison.OrdinalIgnoreCase)) return Gemini;
            if (string.Equals(provider, Qwen, StringComparison.OrdinalIgnoreCase)) return Qwen;
            // DeepSeek was replaced by Qwen in v1.2 (weak image reading on drawings)
            if (string.Equals(provider, "DeepSeek", StringComparison.OrdinalIgnoreCase)) return Qwen;
            return Claude;
        }

        public static string DisplayName(string provider)
        {
            switch (Normalize(provider))
            {
                case Gemini: return "Google Gemini";
                case Qwen: return "Alibaba Qwen";
                default: return "Anthropic Claude";
            }
        }
    }

    /// <summary>
    /// A single-turn multimodal request: system prompt + attachments (images / PDFs) + user text,
    /// returning the raw text of the model answer.
    /// </summary>
    public interface ILlmClient
    {
        string ProviderName { get; }
        string ModelName { get; }

        /// <summary>Maximum total size (bytes) of attachments the provider accepts inline.</summary>
        long MaxInlineBytes { get; }

        Task<string> SendAsync(string systemPrompt, IList<InputAttachment> attachments, string userText);
    }

    public static class LlmClientFactory
    {
        public static ILlmClient Create(string provider, string apiKey, string modelName)
        {
            switch (LlmProviders.Normalize(provider))
            {
                case LlmProviders.Gemini: return new GeminiClient(apiKey, modelName);
                case LlmProviders.Qwen: return new QwenClient(apiKey, modelName,
                    CoRFAGenSettings.IsOpenRouterModel(modelName) ? CoRFAGenSettings.OpenRouterBaseUrl : new CoRFAGenSettingsManager().GetQwenBaseUrl());
                default: return new ClaudeClient(apiKey, modelName);
            }
        }
    }

    public class LlmException : Exception
    {
        public string ResponseJson { get; }

        public LlmException(string message, string responseJson)
            : base(message)
        {
            ResponseJson = responseJson;
        }
    }

    /// <summary>Kept for backward compatibility with the original code base.</summary>
    public class ClaudeException : LlmException
    {
        public ClaudeException(string message, string responseJson) : base(message, responseJson) { }
    }

    public class GeminiException : LlmException
    {
        public GeminiException(string message, string responseJson) : base(message, responseJson) { }
    }

    public class QwenException : LlmException
    {
        public QwenException(string message, string responseJson) : base(message, responseJson) { }
    }

    internal static class LlmHttp
    {
        // One shared HttpClient for the whole add-in. PDF + reasoning models can be slow.
        public static readonly System.Net.Http.HttpClient Client = CreateClient();

        private static System.Net.Http.HttpClient CreateClient()
        {
#if REVIT2020 || REVIT2021 || REVIT2022 || REVIT2023 || REVIT2024
            // .NET Framework: make sure TLS 1.2 is enabled for api.anthropic.com / googleapis.com
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
            return new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(600) };
        }

        /// <summary>Removes markdown fences / chatter and returns the outermost JSON object.</summary>
        public static string CleanJsonResponse(string text)
        {
            if (text == null) return null;
            text = text.Trim();

            if (text.StartsWith("```json"))
                text = text.Substring(7);
            else if (text.StartsWith("```"))
                text = text.Substring(3);

            if (text.EndsWith("```"))
                text = text.Substring(0, text.Length - 3);

            text = text.Trim();

            int firstBrace = text.IndexOf('{');
            int lastBrace = text.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                text = text.Substring(firstBrace, lastBrace - firstBrace + 1);

            return text;
        }

        /// <summary>
        /// Cleans an API key pasted from a web page / messenger: removes spaces, line breaks, quotes,
        /// zero-width and other non-ASCII characters and a pasted "Bearer " prefix. Such characters make
        /// .NET drop the Authorization header ("Missing Authentication header").
        /// </summary>
        public static string SanitizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string k = key.Trim();
            if (k.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) k = k.Substring(7);
            var sb = new System.Text.StringBuilder(k.Length);
            foreach (char c in k)
                if (c > 0x20 && c < 0x7F && c != '"' && c != '\'' && c != '`') sb.Append(c);
            return sb.ToString();
        }

        /// <summary>"sk-or-v1-ab…9f (73 chars)" — safe to show in error messages.</summary>
        public static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "(empty)";
            if (key.Length <= 12) return new string('*', key.Length) + $" ({key.Length} chars)";
            return key.Substring(0, 9) + "…" + key.Substring(key.Length - 3) + $" ({key.Length} chars)";
        }

        public static string TruncateForMessage(string s, int max = 600)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
