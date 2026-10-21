namespace BeGenSt.Data
{
    public class RFAGenSettingsManager
    {
        private static readonly string SettingsFileName = "RFAGen.json";
        private readonly string _filePath;
        private RFAGenSettings _settings;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public RFAGenSettingsManager()
        {
            _filePath = GetSettingsFilePath();
            _settings = LoadSettings();
        }

        public static string GetSettingsFilePath()
        {
            string folderPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BeGenSt", "RFAGen");
            return Path.Combine(folderPath, SettingsFileName);
        }

        private RFAGenSettings LoadSettings()
        {
            if (!File.Exists(_filePath))
            {
                // Migrate API keys/models from the old ArchSmarter FamFab add-in, if present
                try
                {
                    string oldPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ArchSmarter", "FamFab", "FamFab.json");
                    if (File.Exists(oldPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_filePath));
                        File.Copy(oldPath, _filePath, false);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Settings migration skipped: {ex.Message}");
                }
            }

            if (!File.Exists(_filePath))
            {
                CreateDefaultSettingsFile();
            }

            RFAGenSettings settings;
            try
            {
                string json = File.ReadAllText(_filePath);
                settings = JsonSerializer.Deserialize<RFAGenSettings>(json) ?? new RFAGenSettings();
            }
            catch (Exception)
            {
                settings = new RFAGenSettings();
            }

            // Upgrade older settings files: add new default models / fields without losing user entries.
            settings.Provider = LlmProviders.Normalize(settings.Provider);
            settings.AvailableModels = MergeModels(settings.AvailableModels, RFAGenSettings.DefaultClaudeModels());
            settings.GeminiAvailableModels = MergeModels(settings.GeminiAvailableModels, RFAGenSettings.DefaultGeminiModels());
            settings.QwenAvailableModels = MergeModels(settings.QwenAvailableModels, RFAGenSettings.DefaultQwenModels());
            if (string.IsNullOrWhiteSpace(settings.ModelName)) settings.ModelName = new RFAGenSettings().ModelName;
            if (string.IsNullOrWhiteSpace(settings.GeminiModelName)) settings.GeminiModelName = new RFAGenSettings().GeminiModelName;
            if (string.IsNullOrWhiteSpace(settings.QwenModelName)) settings.QwenModelName = new RFAGenSettings().QwenModelName;
            settings.ClaudeApiKey = settings.ClaudeApiKey ?? "";
            settings.GeminiApiKey = settings.GeminiApiKey ?? "";
            settings.QwenApiKey = settings.QwenApiKey ?? "";
            settings.OpenRouterApiKey = settings.OpenRouterApiKey ?? "";
            if (string.IsNullOrWhiteSpace(settings.QwenBaseUrl)) settings.QwenBaseUrl = RFAGenSettings.DefaultQwenBaseUrl;
            return settings;
        }

        private static List<string> MergeModels(List<string> saved, List<string> defaults)
        {
            var result = new List<string>();
            foreach (string m in defaults.Concat(saved ?? new List<string>()))
            {
                if (!string.IsNullOrWhiteSpace(m) && !result.Contains(m.Trim()))
                    result.Add(m.Trim());
            }
            return result;
        }

        private void CreateDefaultSettingsFile()
        {
            string folderPath = Path.GetDirectoryName(_filePath);
            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            if (!File.Exists(_filePath))
            {
                string json = JsonSerializer.Serialize(new RFAGenSettings(), JsonOptions);
                File.WriteAllText(_filePath, json);
            }
        }

        public void SaveSettings()
        {
            try
            {
                string folderPath = Path.GetDirectoryName(_filePath);
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);

                string json = JsonSerializer.Serialize(_settings, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public RFAGenSettings GetSettings()
        {
            return _settings;
        }

        // ---------------- Provider ----------------

        public string GetProvider()
        {
            return LlmProviders.Normalize(_settings.Provider);
        }

        public void SetProvider(string provider)
        {
            _settings.Provider = LlmProviders.Normalize(provider);
            SaveSettings();
        }

        // ---------------- API keys ----------------

        public string GetApiKey(string provider)
        {
            switch (LlmProviders.Normalize(provider))
            {
                case LlmProviders.Gemini: return _settings.GeminiApiKey ?? "";
                case LlmProviders.Qwen: return _settings.QwenApiKey ?? "";
                default: return _settings.ClaudeApiKey ?? "";
            }
        }

        public void SetApiKey(string provider, string apiKey)
        {
            apiKey = LlmHttp.SanitizeKey(apiKey);
            switch (LlmProviders.Normalize(provider))
            {
                case LlmProviders.Gemini: _settings.GeminiApiKey = apiKey; break;
                case LlmProviders.Qwen: _settings.QwenApiKey = apiKey; break;
                default: _settings.ClaudeApiKey = apiKey; break;
            }
            SaveSettings();
        }

        /// <summary>Key for a concrete model: OpenRouter model ids ("vendor/model") use the OpenRouter key.</summary>
        public string GetApiKeyFor(string provider, string modelName)
        {
            if (LlmProviders.Normalize(provider) == LlmProviders.Qwen && RFAGenSettings.IsOpenRouterModel(modelName))
            {
                string or = LlmHttp.SanitizeKey(_settings.OpenRouterApiKey);
                if (or.Length > 0) return or;
                string q = LlmHttp.SanitizeKey(_settings.QwenApiKey);
                return q.StartsWith("sk-or-") ? q : "";
            }
            return GetApiKey(provider);
        }

        public string GetOpenRouterApiKey() => _settings.OpenRouterApiKey ?? "";

        public void SetOpenRouterApiKey(string key)
        {
            _settings.OpenRouterApiKey = LlmHttp.SanitizeKey(key);
            SaveSettings();
        }

        public bool HasAnyApiKey()
        {
            return !string.IsNullOrWhiteSpace(_settings.ClaudeApiKey) ||
                   !string.IsNullOrWhiteSpace(_settings.GeminiApiKey) ||
                   !string.IsNullOrWhiteSpace(_settings.QwenApiKey) ||
                   !string.IsNullOrWhiteSpace(_settings.OpenRouterApiKey);
        }

        // Backward-compatible Claude helpers
        public string GetClaudeApiKey() => GetApiKey(LlmProviders.Claude);
        public void SetClaudeApiKey(string apiKey) => SetApiKey(LlmProviders.Claude, apiKey);

        // ---------------- Models ----------------

        public string GetModelName(string provider)
        {
            var defaults = new RFAGenSettings();
            switch (LlmProviders.Normalize(provider))
            {
                case LlmProviders.Gemini:
                    return string.IsNullOrWhiteSpace(_settings.GeminiModelName) ? defaults.GeminiModelName : _settings.GeminiModelName;
                case LlmProviders.Qwen:
                    return string.IsNullOrWhiteSpace(_settings.QwenModelName) ? defaults.QwenModelName : _settings.QwenModelName;
                default:
                    return string.IsNullOrWhiteSpace(_settings.ModelName) ? defaults.ModelName : _settings.ModelName;
            }
        }

        public void SetModelName(string provider, string modelName)
        {
            modelName = modelName?.Trim();
            if (string.IsNullOrWhiteSpace(modelName)) return;

            List<string> models;
            switch (LlmProviders.Normalize(provider))
            {
                case LlmProviders.Gemini:
                    _settings.GeminiModelName = modelName;
                    models = _settings.GeminiAvailableModels;
                    break;
                case LlmProviders.Qwen:
                    _settings.QwenModelName = modelName;
                    models = _settings.QwenAvailableModels;
                    break;
                default:
                    _settings.ModelName = modelName;
                    models = _settings.AvailableModels;
                    break;
            }

            // remember custom model ids typed by the user
            if (models != null && !models.Contains(modelName))
                models.Add(modelName);

            SaveSettings();
        }

        public List<string> GetAvailableModels(string provider)
        {
            List<string> models;
            List<string> defaults;
            switch (LlmProviders.Normalize(provider))
            {
                case LlmProviders.Gemini:
                    models = _settings.GeminiAvailableModels; defaults = RFAGenSettings.DefaultGeminiModels(); break;
                case LlmProviders.Qwen:
                    models = _settings.QwenAvailableModels; defaults = RFAGenSettings.DefaultQwenModels(); break;
                default:
                    models = _settings.AvailableModels; defaults = RFAGenSettings.DefaultClaudeModels(); break;
            }
            return models == null || models.Count == 0 ? defaults : models;
        }

        // Backward-compatible Claude helpers
        public string GetModelName() => GetModelName(LlmProviders.Claude);
        public void SetModelName(string modelName) => SetModelName(LlmProviders.Claude, modelName);
        public List<string> GetAvailableModels() => GetAvailableModels(LlmProviders.Claude);

        public string GetQwenBaseUrl()
        {
            string url = (_settings.QwenBaseUrl ?? "").Trim();
            return string.IsNullOrEmpty(url) ? RFAGenSettings.DefaultQwenBaseUrl : url;
        }

        public void SetQwenBaseUrl(string url)
        {
            url = (url ?? "").Trim();
            _settings.QwenBaseUrl = string.IsNullOrEmpty(url) ? RFAGenSettings.DefaultQwenBaseUrl : url;
            SaveSettings();
        }

        public void SetParameterOptions(bool useFamilyParameters, bool useSharedFile, string sharedFilePath)
        {
            _settings.UseFamilyParameters = useFamilyParameters;
            _settings.UseSharedParameterFile = useSharedFile;
            _settings.SharedParameterFilePath = sharedFilePath ?? "";
            SaveSettings();
        }

        public void SetPipelineOptions(bool checkInput, bool verify)
        {
            _settings.CheckInputConsistency = checkInput;
            _settings.VerifyBeforePreview = verify;
            SaveSettings();
        }

        public static string GetLogsFolderPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BeGenSt", "RFAGen", "Logs");
        }
    }
}
