using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CoRFAGen.Data
{
    /// <summary>
    /// Alibaba Cloud Model Studio (Qwen) client, OpenAI-compatible /chat/completions.
    /// Qwen does not accept PDF files, so PDF drawings are rendered locally to high-resolution PNG pages
    /// (≈9 Mpx; Qwen reads up to 16 Mpx per image, unlike DeepSeek's ~1 Mpx cap) and the text layer of
    /// the vector PDF is appended to the prompt.
    /// </summary>
    public class QwenClient : ILlmClient
    {
        private const long MaxRequestImageBytes = 40L * 1024 * 1024;
        private const int MaxImages = 12;

        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly string _endpoint;
        private readonly bool _openRouter;
        private readonly int _removedKeyChars;

        public QwenClient(string apiKey, string modelName, string baseUrl)
        {
            string raw = (apiKey ?? "").Trim();
            _apiKey = LlmHttp.SanitizeKey(raw);
            _removedKeyChars = raw.Length - _apiKey.Length;
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "qwen3.7-plus" : modelName.Trim();
            string b = string.IsNullOrWhiteSpace(baseUrl) ? CoRFAGenSettings.DefaultQwenBaseUrl : baseUrl.Trim().TrimEnd('/');
            _openRouter = b.IndexOf("openrouter.ai", StringComparison.OrdinalIgnoreCase) >= 0;
            _endpoint = b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b : b + "/chat/completions";
        }

        public string ProviderName => LlmProviders.Qwen;
        public string ModelName => _modelName;

        // raw size of the source files; PDFs are rendered to PNG before sending
        public long MaxInlineBytes => 30L * 1024 * 1024;

        public async Task<string> SendAsync(string systemPrompt, IList<InputAttachment> attachments, string userText)
        {
            PreparedInput input = await Task.Run(() => Prepare(attachments)).ConfigureAwait(false);
            string fullText = userText + input.ExtraText;

            var opt = new RequestOptions
            {
                WithImages = input.Images.Count > 0,
                JsonMode = true,
                Thinking = true,
                HighRes = true,
                MaxTokens = 32768
            };

            if (string.IsNullOrEmpty(_apiKey))
                throw new QwenException((_openRouter ? "The OpenRouter" : "The Qwen") +
                    " API key is empty. Enter it in CoRFAGen › Settings.", null);

            for (int attempt = 0; attempt < 6; attempt++)
            {
                string payload = BuildPayload(systemPrompt, fullText, opt.WithImages ? input.Images : null, opt);

                using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
                    if (_openRouter)
                    {
                        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/CoRFAGen");
                        request.Headers.TryAddWithoutValidation("X-Title", "CoRFAGen");
                    }
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    HttpResponseMessage response = await LlmHttp.Client.SendAsync(request).ConfigureAwait(false);
                    string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                        return ExtractText(responseJson);

                    string error = ExtractErrorMessage(responseJson);
                    string lower = (error ?? "").ToLowerInvariant();
                    int status = (int)response.StatusCode;

                    if (status == 400)
                    {
                        Debug.WriteLine("Qwen 400: " + error);
                        // drop optional parameters one by one if the model rejects them
                        if (opt.HighRes && lower.Contains("vl_high_resolution")) { opt.HighRes = false; continue; }
                        if (opt.Thinking && (lower.Contains("thinking") || lower.Contains("reasoning"))) { opt.Thinking = false; continue; }
                        if (lower.Contains("max_tokens") || lower.Contains("max tokens") || lower.Contains("range of"))
                        {
                            if (opt.MaxTokens > 16384) { opt.MaxTokens = 16384; continue; }
                            if (opt.MaxTokens > 8192) { opt.MaxTokens = 8192; continue; }
                        }
                        if (opt.JsonMode && (lower.Contains("response_format") || lower.Contains("json"))) { opt.JsonMode = false; continue; }
                        if (opt.WithImages && (lower.Contains("image") || lower.Contains("vision") || lower.Contains("multimodal") || lower.Contains("url")))
                        {
                            opt.WithImages = false;
                            fullText += "\n\nNOTE: the selected Qwen model cannot read images, so only the text layer of the " +
                                        "drawing and the user's description are available. Mark uncertain dimensions as estimated.";
                            continue;
                        }
                    }

                    string keyInfo = "";
                    if (status == 401 || status == 403)
                    {
                        keyInfo = $"\nKey sent: {LlmHttp.MaskKey(_apiKey)}";
                        if (_removedKeyChars > 0) keyInfo += $", {_removedKeyChars} invisible/space characters were removed from the stored key";
                        Uri finalUri = response.RequestMessage?.RequestUri;
                        if (finalUri != null && !string.Equals(finalUri.AbsoluteUri.TrimEnd('/'), _endpoint.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                            keyInfo += $"\nThe request was redirected to {finalUri} (a proxy/VPN may strip the key).";
                        if (_openRouter && !_apiKey.StartsWith("sk-or-"))
                            keyInfo += "\nOpenRouter keys start with sk-or-… — this looks like a different key.";
                    }

                    string hint = keyInfo + (_openRouter
                        ? (status == 401 ? "\nCheck the OpenRouter API key (sk-or-...) in CoRFAGen › Settings."
                           : status == 402 ? "\nOpenRouter: insufficient credits for this model."
                           : status == 429 ? "\nOpenRouter free-tier rate/daily limit reached — wait and retry, or use a paid model."
                           : status == 404 ? $"\nOpenRouter model '{_modelName}' not found or no provider accepts this request (images?)."
                           : "")
                        : status == 401 || status == 403
                        ? "\nCheck the Qwen API key in CoRFAGen › Settings. Keys are region-specific: the endpoint (Base URL) must be in the same region as the key (Singapore = dashscope-intl…)."
                        : status == 404 ? $"\nModel '{_modelName}' or endpoint not found. Check the model id and the Base URL in Settings."
                        : status == 429 ? "\nRate limit or quota reached — wait a minute and retry, or check the account balance."
                        : "");
                    throw new QwenException(
                        $"Qwen API returned {status}: {response.ReasonPhrase}\n" + LlmHttp.TruncateForMessage(error) + hint,
                        responseJson);
                }
            }

            throw new QwenException("Qwen API rejected the request after several retries.", null);
        }

        private class RequestOptions
        {
            public bool WithImages;
            public bool JsonMode;
            public bool Thinking;
            public bool HighRes;
            public int MaxTokens;
        }

        // ------------------------------------------------------------------ input preparation

        private class PreparedInput
        {
            public List<(string mime, byte[] bytes)> Images { get; } = new List<(string, byte[])>();
            public string ExtraText { get; set; } = "";
        }

        private static PreparedInput Prepare(IList<InputAttachment> attachments)
        {
            var result = new PreparedInput();
            if (attachments == null) return result;

            var notes = new StringBuilder();
            long total = 0;

            bool TryAdd(string mime, byte[] data)
            {
                if (data == null || data.Length == 0) return false;
                if (result.Images.Count >= MaxImages) return false;
                if (total + data.Length > MaxRequestImageBytes) return false;
                result.Images.Add((mime, data));
                total += data.Length;
                return true;
            }

            int fileNo = 0;
            foreach (InputAttachment att in attachments)
            {
                if (att?.Bytes == null || att.Bytes.Length == 0) continue;
                fileNo++;

                if (att.IsPdf)
                {
                    RasterizedPdf pdf = PdfRasterizer.Rasterize(att, makeTiles: false);
                    notes.AppendLine();
                    notes.AppendLine($"=== File {fileNo}: {att.FileName} (PDF drawing, {pdf.PageCount} page(s)) ===");

                    if (pdf.Error != null)
                    {
                        notes.AppendLine($"[The PDF could not be rendered to images: {pdf.Error}. Only its text layer (if any) is available.]");
                    }
                    else
                    {
                        notes.AppendLine("The PDF was rendered locally to high-resolution PNG images (the API does not accept PDF files). " +
                                         "Examine every view, section and detail on the sheet and model ALL visible parts. Images, in order:");
                        foreach (RasterizedPage page in pdf.Pages)
                        {
                            if (TryAdd("image/png", page.Png))
                                notes.AppendLine($"  • image #{result.Images.Count}: page {page.PageNumber}, full sheet ({page.WidthPx}×{page.HeightPx} px)");
                        }
                        if (pdf.PageCount > pdf.Pages.Count)
                            notes.AppendLine($"  (only the first {pdf.Pages.Count} of {pdf.PageCount} pages were sent)");
                    }

                    string text = PdfRasterizer.GetText(pdf);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        notes.AppendLine("Text layer extracted from the vector PDF (dimension values, title block, technical data; " +
                                         "word order may differ from the layout):");
                        notes.AppendLine(text);
                    }
                    else if (pdf.Error == null)
                    {
                        notes.AppendLine("(the PDF has no text layer — it is a scan; read the dimensions from the images)");
                    }
                }
                else if (att.IsImage)
                {
                    if (TryAdd(att.MimeType, att.Bytes))
                        notes.AppendLine($"\n=== File {fileNo}: {att.FileName} — image #{result.Images.Count} ===");
                    else
                        notes.AppendLine($"\n=== File {fileNo}: {att.FileName} — skipped (request size limit) ===");
                }
            }

            if (notes.Length > 0)
                result.ExtraText = "\n\n--- SOURCE FILES (prepared for Qwen) ---" + notes;
            return result;
        }

        private string BuildPayload(string systemPrompt, string userText,
            List<(string mime, byte[] bytes)> images, RequestOptions opt)
        {
            object userContent;
            if (images != null && images.Count > 0)
            {
                var parts = new List<object>();
                foreach (var img in images)
                {
                    parts.Add(new
                    {
                        type = "image_url",
                        image_url = new { url = $"data:{img.mime};base64,{Convert.ToBase64String(img.bytes)}" }
                    });
                }
                parts.Add(new { type = "text", text = userText });
                userContent = parts.ToArray();
            }
            else
            {
                userContent = userText;
            }

            var body = new Dictionary<string, object>
            {
                ["model"] = _modelName,
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt + "\n\nAnswer with one valid JSON object only." },
                    new { role = "user", content = userContent }
                },
                ["max_tokens"] = opt.MaxTokens,
                ["stream"] = false
            };
            if (opt.JsonMode) body["response_format"] = new { type = "json_object" };
            // thinking improves geometric reasoning on drawings; budget keeps time and cost bounded
            if (_openRouter)
            {
                // OpenRouter's unified reasoning switch; Alibaba-specific parameters are not sent
                body["reasoning"] = new { enabled = opt.Thinking };
            }
            else
            {
                body["enable_thinking"] = opt.Thinking;
                if (opt.Thinking) body["thinking_budget"] = 16384;
                if (opt.HighRes && images != null && images.Count > 0) body["vl_high_resolution_images"] = true;
            }

            return JsonSerializer.Serialize(body);
        }

        // ------------------------------------------------------------------ response

        private static string ExtractErrorMessage(string responseJson)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(responseJson))
                {
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("error", out JsonElement err))
                    {
                        if (err.ValueKind == JsonValueKind.String) return err.GetString();
                        if (err.TryGetProperty("message", out JsonElement msg)) return msg.GetString();
                    }
                    if (root.TryGetProperty("message", out JsonElement m2) && m2.ValueKind == JsonValueKind.String)
                        return m2.GetString();
                }
            }
            catch { }
            return responseJson;
        }

        private static string ExtractText(string responseJson)
        {
            using (JsonDocument doc = JsonDocument.Parse(responseJson))
            {
                JsonElement root = doc.RootElement;
                // OpenRouter can return HTTP 200 with an upstream error object
                if (root.TryGetProperty("error", out JsonElement upErr) && !root.TryGetProperty("choices", out _))
                {
                    string m = upErr.ValueKind == JsonValueKind.Object && upErr.TryGetProperty("message", out JsonElement um)
                        ? um.GetString() : upErr.ToString();
                    throw new QwenException("Upstream error: " + LlmHttp.TruncateForMessage(m), responseJson);
                }

                if (!root.TryGetProperty("choices", out JsonElement choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    throw new QwenException("No choices in Qwen response", responseJson);

                JsonElement choice = choices[0];
                string finish = choice.TryGetProperty("finish_reason", out JsonElement fr) &&
                                fr.ValueKind == JsonValueKind.String ? fr.GetString() : null;

                if (finish == "length")
                    throw new QwenException(
                        "Response was truncated — the family definition exceeded the token limit. Try a simpler object or reduce detail level.",
                        responseJson);
                if (finish == "content_filter")
                    throw new QwenException("The response was blocked by Qwen's content filter.", responseJson);

                string text = null;
                if (choice.TryGetProperty("message", out JsonElement message) &&
                    message.TryGetProperty("content", out JsonElement content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                        text = content.GetString();
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (JsonElement part in content.EnumerateArray())
                            if (part.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                                sb.Append(t.GetString());
                        text = sb.ToString();
                    }
                }

                // reasoning_content (thinking) is ignored on purpose
                if (string.IsNullOrWhiteSpace(text))
                    throw new QwenException(
                        "Empty answer from Qwen" + (finish != null ? $" (finish_reason: {finish})" : ""), responseJson);

                return LlmHttp.CleanJsonResponse(text);
            }
        }
    }
}
