using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace BeGenSt.Data
{
    /// <summary>
    /// Google Gemini API client (generateContent, v1beta).
    /// Images and PDF drawings are sent as inline_data parts; Gemini natively reads PDFs
    /// (both the embedded vector text and the rendered page image).
    /// </summary>
    public class GeminiClient : ILlmClient
    {
        private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
        private readonly string _apiKey;
        private readonly string _modelName;

        public GeminiClient(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
        }

        public string ProviderName => LlmProviders.Gemini;
        public string ModelName => _modelName;

        // Inline request data is limited to ~20 MB; base64 inflates data by ~4/3.
        public long MaxInlineBytes => 14L * 1024 * 1024;

        public async Task<string> SendAsync(string systemPrompt, IList<InputAttachment> attachments, string userText)
        {
            var parts = new List<object>();

            if (attachments != null)
            {
                foreach (InputAttachment att in attachments)
                {
                    if (att?.Bytes == null || att.Bytes.Length == 0) continue;
                    parts.Add(new
                    {
                        inline_data = new
                        {
                            mime_type = att.MimeType,
                            data = Convert.ToBase64String(att.Bytes)
                        }
                    });
                }
            }

            parts.Add(new { text = userText });

            var requestBody = new
            {
                system_instruction = new
                {
                    parts = new object[] { new { text = systemPrompt } }
                },
                contents = new object[]
                {
                    new { role = "user", parts = parts.ToArray() }
                },
                generationConfig = new
                {
                    responseMimeType = "application/json",
                    maxOutputTokens = 65536
                    // temperature left at the model default (Gemini 3 models recommend 1.0)
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);
            string model = (_modelName ?? "").Trim();
            if (model.StartsWith("models/")) model = model.Substring("models/".Length);
            string url = BaseUrl + Uri.EscapeDataString(model) + ":generateContent";

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                // Key goes in a header, never in the URL (it would end up in logs).
                request.Headers.Add("x-goog-api-key", _apiKey);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await LlmHttp.Client.SendAsync(request).ConfigureAwait(false);
                string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new GeminiException(
                        $"Gemini API returned {(int)response.StatusCode}: {response.ReasonPhrase}\n" +
                        LlmHttp.TruncateForMessage(ExtractErrorMessage(responseJson)),
                        responseJson);
                }

                return ExtractTextFromResponse(responseJson);
            }
        }

        private static string ExtractErrorMessage(string responseJson)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(responseJson))
                {
                    if (doc.RootElement.TryGetProperty("error", out JsonElement err) &&
                        err.TryGetProperty("message", out JsonElement msg))
                        return msg.GetString();
                }
            }
            catch { }
            return responseJson;
        }

        private static string ExtractTextFromResponse(string responseJson)
        {
            using (JsonDocument doc = JsonDocument.Parse(responseJson))
            {
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("promptFeedback", out JsonElement feedback) &&
                    feedback.TryGetProperty("blockReason", out JsonElement blockReason))
                {
                    throw new GeminiException($"Request was blocked by Gemini: {blockReason.GetString()}", responseJson);
                }

                if (!root.TryGetProperty("candidates", out JsonElement candidates) ||
                    candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                    throw new GeminiException("No candidates in Gemini response", responseJson);

                JsonElement candidate = candidates[0];
                string finishReason = candidate.TryGetProperty("finishReason", out JsonElement fr) &&
                                      fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

                if (finishReason == "MAX_TOKENS")
                {
                    throw new GeminiException(
                        "Response was truncated — the family definition exceeded the token limit. Try a simpler object or reduce detail level.",
                        responseJson);
                }

                var sb = new StringBuilder();
                if (candidate.TryGetProperty("content", out JsonElement content) &&
                    content.TryGetProperty("parts", out JsonElement parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement part in parts.EnumerateArray())
                    {
                        // skip "thought" parts produced by thinking models
                        if (part.TryGetProperty("thought", out JsonElement thought) &&
                            thought.ValueKind == JsonValueKind.True)
                            continue;

                        if (part.TryGetProperty("text", out JsonElement textEl))
                            sb.Append(textEl.GetString());
                    }
                }

                string text = sb.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new GeminiException(
                        "No text content in Gemini response" + (finishReason != null ? $" (finishReason: {finishReason})" : ""),
                        responseJson);
                }

                return LlmHttp.CleanJsonResponse(text);
            }
        }
    }
}
