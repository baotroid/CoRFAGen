using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CoRFAGen.Data
{
    /// <summary>
    /// Anthropic Messages API client. Images are sent as "image" blocks,
    /// PDF drawings as "document" blocks (Claude reads both the text layer and the rendered page).
    /// </summary>
    public class ClaudeClient : ILlmClient
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private readonly string _apiKey;
        private readonly string _modelName;

        public ClaudeClient(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            _modelName = modelName;
        }

        public string ProviderName => LlmProviders.Claude;
        public string ModelName => _modelName;

        // Anthropic request limit is 32 MB; base64 inflates data by ~4/3.
        public long MaxInlineBytes => 22L * 1024 * 1024;

        public async Task<string> SendAsync(string systemPrompt, IList<InputAttachment> attachments, string userText)
        {
            var content = new List<object>();

            if (attachments != null)
            {
                foreach (InputAttachment att in attachments)
                {
                    if (att?.Bytes == null || att.Bytes.Length == 0) continue;
                    string data = Convert.ToBase64String(att.Bytes);

                    if (att.IsPdf)
                    {
                        content.Add(new
                        {
                            type = "document",
                            title = att.FileName,
                            source = new { type = "base64", media_type = "application/pdf", data = data }
                        });
                    }
                    else
                    {
                        content.Add(new
                        {
                            type = "image",
                            source = new { type = "base64", media_type = att.MimeType, data = data }
                        });
                    }
                }
            }

            content.Add(new { type = "text", text = userText });

            var requestBody = new
            {
                model = _modelName,
                max_tokens = 32768,
                system = systemPrompt,
                messages = new object[]
                {
                    new { role = "user", content = content.ToArray() }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);

            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await LlmHttp.Client.SendAsync(request).ConfigureAwait(false);
                string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    throw new ClaudeException(
                        $"Claude API returned {(int)response.StatusCode}: {response.ReasonPhrase}\n" +
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

                if (root.TryGetProperty("stop_reason", out JsonElement stopReason) &&
                    stopReason.ValueKind == JsonValueKind.String &&
                    stopReason.GetString() == "max_tokens")
                {
                    throw new ClaudeException(
                        "Response was truncated — the family definition exceeded the token limit. Try a simpler object or reduce detail level.",
                        responseJson);
                }

                if (!root.TryGetProperty("content", out JsonElement content) ||
                    content.ValueKind != JsonValueKind.Array || content.GetArrayLength() == 0)
                    throw new ClaudeException("No content in response", responseJson);

                var sb = new StringBuilder();
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out JsonElement typeEl) &&
                        typeEl.GetString() == "text" &&
                        block.TryGetProperty("text", out JsonElement textEl))
                    {
                        sb.Append(textEl.GetString());
                    }
                }

                string text = sb.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    throw new ClaudeException("No text content in response", responseJson);

                return LlmHttp.CleanJsonResponse(text);
            }
        }
    }
}
