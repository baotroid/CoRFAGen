using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CoRFAGen.Data;

namespace CoRFAGen.UI
{
    public class SettingsWindowViewModel : INotifyPropertyChanged
    {
        private readonly CoRFAGenSettingsManager _settingsManager;

        public SettingsWindowViewModel()
        {
            _settingsManager = new CoRFAGenSettingsManager();

            _claudeApiKey = _settingsManager.GetApiKey(LlmProviders.Claude);
            _geminiApiKey = _settingsManager.GetApiKey(LlmProviders.Gemini);
            _qwenApiKey = _settingsManager.GetApiKey(LlmProviders.Qwen);
            _qwenBaseUrl = _settingsManager.GetQwenBaseUrl();
            _openRouterApiKey = _settingsManager.GetOpenRouterApiKey();
            _selectedProvider = _settingsManager.GetProvider();
            AvailableModels = new ObservableCollection<string>(_settingsManager.GetAvailableModels(_selectedProvider));
            _selectedModel = _settingsManager.GetModelName(_selectedProvider);
            _selectedLanguage = _settingsManager.GetLanguage() == Loc.Russian ? LanguageRussian : LanguageEnglish;

            UpdateStatus();
        }

        public List<string> Providers { get; } = LlmProviders.All;

        private const string LanguageEnglish = "English";
        private const string LanguageRussian = "Русский";
        public List<string> Languages { get; } = new List<string> { LanguageEnglish, LanguageRussian };

        /// <summary>Raised after the interface language changes, so the open window can translate itself.</summary>
        public event Action LanguageChanged;

        private string _selectedLanguage;
        public string SelectedLanguage
        {
            get => _selectedLanguage;
            set
            {
                if (value == null || value == _selectedLanguage) return;
                _selectedLanguage = value;
                string language = value == LanguageRussian ? Loc.Russian : Loc.English;
                _settingsManager.SetLanguage(language);
                Loc.SetLanguage(language);
                App.ApplyRibbonLanguage();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModelLabel));
                UpdateStatus();
                LanguageChanged?.Invoke();
            }
        }

        private string _selectedProvider;
        public string SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                if (value == null || value == _selectedProvider) return;
                _selectedProvider = LlmProviders.Normalize(value);
                _settingsManager.SetProvider(_selectedProvider);

                AvailableModels.Clear();
                foreach (string m in _settingsManager.GetAvailableModels(_selectedProvider))
                    AvailableModels.Add(m);
                _selectedModel = _settingsManager.GetModelName(_selectedProvider);

                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(ModelLabel));
                UpdateStatus();
            }
        }

        public string ModelLabel => Loc.T($"Model ({LlmProviders.DisplayName(_selectedProvider)})");

        private string _claudeApiKey;
        public string ClaudeApiKey
        {
            get => _claudeApiKey;
            set
            {
                _claudeApiKey = value;
                _settingsManager.SetApiKey(LlmProviders.Claude, value);
                UpdateStatus();
                OnPropertyChanged();
            }
        }

        private string _geminiApiKey;
        public string GeminiApiKey
        {
            get => _geminiApiKey;
            set
            {
                _geminiApiKey = value;
                _settingsManager.SetApiKey(LlmProviders.Gemini, value);
                UpdateStatus();
                OnPropertyChanged();
            }
        }

        private string _qwenApiKey;
        public string QwenApiKey
        {
            get => _qwenApiKey;
            set
            {
                _qwenApiKey = value;
                _settingsManager.SetApiKey(LlmProviders.Qwen, value);
                UpdateStatus();
                OnPropertyChanged();
            }
        }

        private string _openRouterApiKey;
        public string OpenRouterApiKey
        {
            get => _openRouterApiKey;
            set
            {
                _openRouterApiKey = value;
                _settingsManager.SetOpenRouterApiKey(value);
                UpdateStatus();
                OnPropertyChanged();
            }
        }

        private string _qwenBaseUrl;
        public string QwenBaseUrl
        {
            get => _qwenBaseUrl;
            set
            {
                _settingsManager.SetQwenBaseUrl(value);
                _qwenBaseUrl = _settingsManager.GetQwenBaseUrl();
                OnPropertyChanged();
            }
        }

        private string _selectedModel;
        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                _selectedModel = value.Trim();
                _settingsManager.SetModelName(_selectedProvider, _selectedModel);
                if (!AvailableModels.Contains(_selectedModel))
                    AvailableModels.Add(_selectedModel);
                OnPropertyChanged();
            }
        }

        public ObservableCollection<string> AvailableModels { get; }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private void UpdateStatus()
        {
            string claude = string.IsNullOrWhiteSpace(_claudeApiKey) ? "not set"
                : (_claudeApiKey.Trim().StartsWith("sk-ant-") ? "configured" : "set (verify it starts with sk-ant-)");
            string gemini = string.IsNullOrWhiteSpace(_geminiApiKey) ? "not set"
                : (_geminiApiKey.Trim().StartsWith("AIza") ? "configured" : "set (Gemini keys usually start with AIza)");

            string qwen = string.IsNullOrWhiteSpace(_qwenApiKey) ? "not set"
                : (_qwenApiKey.Trim().StartsWith("sk-") ? "configured" : "set (Qwen keys usually start with sk-)");

            string active = string.IsNullOrWhiteSpace(_settingsManager.GetApiKeyFor(_selectedProvider, _selectedModel))
                ? $"  ⚠ Active provider {LlmProviders.DisplayName(_selectedProvider)} has no key."
                : "";

            string openRouter = string.IsNullOrWhiteSpace(_openRouterApiKey) ? "not set" : "set";
            StatusText = Loc.T($"Claude key: {claude}.  Gemini key: {gemini}.  Qwen key: {qwen}.  OpenRouter key: {openRouter}.{active}\n" +
                         $"Keys are stored locally in {CoRFAGenSettingsManager.GetSettingsFilePath()}");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
