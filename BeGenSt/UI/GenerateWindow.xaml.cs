using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using BeGenSt.Data;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BeGenSt.UI
{
    public partial class GenerateWindow : Window
    {
        public const int MaxFiles = 3;

        private readonly RFAGenSettingsManager _settingsManager;
        private readonly List<string> _selectedFiles = new List<string>();
        private bool _initializing = true;

        public string FamilyJson { get; private set; }
        public List<InputAttachment> Attachments { get; private set; } = new List<InputAttachment>();
        public string UserContext { get; private set; }
        public string FamilyName { get; private set; }
        public string Provider { get; private set; }
        public string ModelName { get; private set; }
        public string ApiKey { get; private set; }
        public string VerificationSummary { get; private set; }

        /// <summary>Fill parameters of the open family/template instead of recreating them.</summary>
        public bool ReuseFamilyParameters { get; private set; }
        /// <summary>Shared parameter file (ФОП) to use, or null.</summary>
        public string SharedParameterFile { get; private set; }

        private readonly ParameterTargetContext _familyParams;

        public byte[] SourceImageBytes => Attachments.FirstOrDefault(a => a.IsImage)?.Bytes;
        public string SourceImageMimeType => Attachments.FirstOrDefault(a => a.IsImage)?.MimeType;

        public GenerateWindow(RFAGenSettingsManager settingsManager, ParameterTargetContext familyParams = null)
        {
            InitializeComponent();
            _settingsManager = settingsManager;
            _familyParams = familyParams ?? new ParameterTargetContext();

            CmbProvider.ItemsSource = LlmProviders.All;
            CmbProvider.SelectedItem = settingsManager.GetProvider();
            LoadModels(settingsManager.GetProvider());

            RFAGenSettings s = settingsManager.GetSettings();
            ChkCheckInput.IsChecked = s.CheckInputConsistency;
            ChkVerify.IsChecked = s.VerifyBeforePreview;
            ChkUseFamilyParams.IsChecked = s.UseFamilyParameters;
            ChkUseSharedFile.IsChecked = s.UseSharedParameterFile;
            if (!string.IsNullOrWhiteSpace(s.SharedParameterFilePath)) TxtSharedFile.Text = s.SharedParameterFilePath;
            SharedFilePanel.IsEnabled = s.UseSharedParameterFile;
            TxtParamInfo.Text = $"Family / template language: {_familyParams.LanguageName}; " +
                                $"{_familyParams.FamilyParameters.Count} own parameter(s) in the family.";
            _initializing = false;
        }

        private void LoadModels(string provider)
        {
            CmbModel.ItemsSource = _settingsManager.GetAvailableModels(provider);
            CmbModel.Text = _settingsManager.GetModelName(provider);
            CmbModel.SelectedItem = _settingsManager.GetModelName(provider);
        }

        private void CmbProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            string provider = CmbProvider.SelectedItem as string ?? LlmProviders.Claude;
            _settingsManager.SetProvider(provider);
            LoadModels(provider);
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ------------------------------------------------------------------ files

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = $"Select up to {MaxFiles} files of the same object (DWG/DXF or PDF drawing, JPG photo, PNG sketch)",
                Filter = InputAttachment.OpenFileFilter,
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;

            var skipped = new List<string>();
            foreach (string f in dlg.FileNames)
            {
                if (!InputAttachment.IsSupported(f)) { skipped.Add(Path.GetFileName(f) + " (unsupported type)"); continue; }
                if (_selectedFiles.Any(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase))) continue;
                if (_selectedFiles.Count >= MaxFiles) { skipped.Add(Path.GetFileName(f) + " (limit of 3 files)"); continue; }
                _selectedFiles.Add(f);
            }

            if (skipped.Count > 0)
                System.Windows.MessageBox.Show("Not added:\n" + string.Join("\n", skipped) +
                    $"\n\nOne family can be generated from at most {MaxFiles} files: DWG, DXF, PDF, JPG/JPEG, PNG (WEBP).",
                    "RFAGen", MessageBoxButton.OK, MessageBoxImage.Information);

            RefreshFiles();
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _selectedFiles.Clear();
            RefreshFiles();
        }

        private void RefreshFiles()
        {
            BtnGenerate.IsEnabled = _selectedFiles.Count > 0;
            if (_selectedFiles.Count == 0)
            {
                TxtFilePath.Text = "No files selected";
                PreviewBorder.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            TxtFilePath.Text = _selectedFiles.Count == 1 ? _selectedFiles[0] : $"{_selectedFiles.Count} of {MaxFiles} files selected";
            LoadPreview(_selectedFiles);
        }

        private void LoadPreview(List<string> files)
        {
            ImagePreview.Source = null;
            ImagePreview.Visibility = System.Windows.Visibility.Collapsed;

            string firstImage = files.FirstOrDefault(f => !f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !CadDrawingReader.IsCadFile(f));
            if (firstImage != null)
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(firstImage);
                    bitmap.DecodePixelWidth = 400;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    ImagePreview.Source = bitmap;
                    ImagePreview.Visibility = System.Windows.Visibility.Visible;
                }
                catch { }
            }

            TxtFileList.Text = string.Join("\n", files.Select((f, i) =>
            {
                string role = CadDrawingReader.IsCadFile(f) ? "CAD drawing"
                    : f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF drawing / data"
                    : f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "sketch / image" : "photo";
                long size = new FileInfo(f).Length;
                return $"{i + 1}. {Path.GetFileName(f)}  —  {role}, {size / 1024.0:F0} KB";
            }));
            PreviewBorder.Visibility = System.Windows.Visibility.Visible;
        }

        // ------------------------------------------------------------------ parameter mapping

        private void ChkUseSharedFile_Changed(object sender, RoutedEventArgs e)
        {
            if (SharedFilePanel != null) SharedFilePanel.IsEnabled = ChkUseSharedFile.IsChecked == true;
        }

        private void BtnBrowseShared_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select a shared parameter file (ФОП)",
                Filter = "Shared parameter files (*.txt)|*.txt|All Files|*.*"
            };
            string cur = TxtSharedFile.Text;
            try { if (File.Exists(cur)) dlg.InitialDirectory = Path.GetDirectoryName(cur); } catch { }
            if (dlg.ShowDialog() != true) return;
            try
            {
                var list = ParameterTargetContext.ParseSharedParameterFile(dlg.FileName);
                if (list.Count == 0)
                {
                    System.Windows.MessageBox.Show("No parameters found in this file. Is it a Revit shared parameter file?",
                        "RFAGen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                TxtSharedFile.Text = dlg.FileName;
                TxtParamInfo.Text = $"Family / template language: {_familyParams.LanguageName}; " +
                                    $"{_familyParams.FamilyParameters.Count} own parameter(s); shared file: {list.Count} parameter(s).";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Cannot read the shared parameter file: " + ex.Message, "RFAGen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>Validates the parameter options and returns the mapping context (null = cancelled).</summary>
        private ParameterTargetContext BuildParameterContext(out bool ok)
        {
            ok = true;
            bool useFamily = ChkUseFamilyParams.IsChecked == true;
            bool useShared = ChkUseSharedFile.IsChecked == true;
            string sharedPath = TxtSharedFile.Text;
            if (useShared && !File.Exists(sharedPath))
            {
                System.Windows.MessageBox.Show("Select the shared parameter file (ФОП) or untick the option.", "RFAGen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ok = false;
                return null;
            }

            _settingsManager.SetParameterOptions(useFamily, useShared, File.Exists(sharedPath) ? sharedPath : "");
            ReuseFamilyParameters = useFamily;
            SharedParameterFile = useShared ? sharedPath : null;
            if (!useFamily && !useShared) return null;

            var ctx = new ParameterTargetContext
            {
                Language = _familyParams.Language,
                UseFamilyParameters = useFamily,
                UseSharedFile = useShared,
                SharedFilePath = useShared ? sharedPath : null
            };
            ctx.FamilyParameters.AddRange(_familyParams.FamilyParameters);
            if (useShared)
            {
                try { ctx.SharedFileParameters.AddRange(ParameterTargetContext.ParseSharedParameterFile(sharedPath)); }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("Cannot read the shared parameter file: " + ex.Message, "RFAGen",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    ok = false;
                    return null;
                }
            }
            return ctx;
        }

        // ------------------------------------------------------------------ Generate

        private async void BtnGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFiles.Count == 0 || _selectedFiles.Any(f => !File.Exists(f)))
            {
                System.Windows.MessageBox.Show("Please select valid PDF / image files.", "RFAGen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string provider = CmbProvider.SelectedItem as string ?? LlmProviders.Claude;
            string modelName = (CmbModel.Text ?? "").Trim();
            if (string.IsNullOrEmpty(modelName)) modelName = _settingsManager.GetModelName(provider);

            string apiKey = _settingsManager.GetApiKeyFor(provider, modelName);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                string who = RFAGenSettings.IsOpenRouterModel(modelName) && LlmProviders.Normalize(provider) == LlmProviders.Qwen
                    ? "OpenRouter (model " + modelName + ")" : LlmProviders.DisplayName(provider);
                System.Windows.MessageBox.Show(
                    $"No API key configured for {who}.\n\nOpen BeGenSt › Settings and enter the key, or choose another provider.",
                    "RFAGen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ParameterTargetContext paramCtx = BuildParameterContext(out bool paramsOk);
            if (!paramsOk) return;

            bool checkInput = ChkCheckInput.IsChecked == true;
            bool verify = ChkVerify.IsChecked == true;
            _settingsManager.SetProvider(provider);
            _settingsManager.SetModelName(provider, modelName);
            _settingsManager.SetPipelineOptions(checkInput, verify);

            SetGenerating(true, "Reading source files...");

            try
            {
                List<InputAttachment> attachments = _selectedFiles.Select(InputAttachment.FromFile).ToList();
                var service = new FamilyAiService(provider, apiKey, modelName);
                service.ValidateAttachments(attachments);
                service.ParameterMappingText = paramCtx?.BuildPromptText();

                string userContext = TxtContext.Text?.Trim();
                var pipeline = new GenerationPipeline(service, msg => StatusLabel.Text = msg);

                PipelineResult result = await pipeline.RunAsync(attachments, userContext, checkInput, verify, AskUser);
                if (result.Cancelled)
                {
                    SetGenerating(false);
                    StatusLabel.Text = "Cancelled.";
                    return;
                }

                string familyJson = result.FamilyJson;
                string mapNote = null;
                if (paramCtx != null)
                {
                    StatusLabel.Text = $"Mapping parameters to the family / shared parameter file ({paramCtx.LanguageName})...";
                    var mapLog = new List<string>();
                    familyJson = await ParameterMapper.MapAsync(familyJson, paramCtx, service, mapLog);
                    mapNote = MappingSummary(mapLog);
                }

                FamilyJson = familyJson;
                VerificationSummary = result.VerificationSummary + (mapNote != null ? "\n" + mapNote : "");
                Attachments = attachments;
                UserContext = userContext;
                FamilyName = TxtFamilyName.Text?.Trim();
                Provider = provider;
                ModelName = modelName;
                ApiKey = apiKey;
                DialogResult = true;
            }
            catch (LlmException ex)
            {
                Debug.WriteLine($"Response: {ex.ResponseJson}");
                System.Windows.MessageBox.Show($"AI API error: {ex.Message}", "RFAGen - Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetGenerating(false);
            }
            catch (TaskCanceledException)
            {
                System.Windows.MessageBox.Show("The request timed out. Try a faster model or a smaller PDF.",
                    "RFAGen - Error", MessageBoxButton.OK, MessageBoxImage.Error);
                SetGenerating(false);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error: {ex.Message}", "RFAGen - Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetGenerating(false);
            }
        }

        /// <summary>Shows the conflict dialog (UI thread). Returns null when the user cancels.</summary>
        private List<ConflictDecision> AskUser(SourceAnalysis analysis)
        {
            var dlg = new ConflictWindow(analysis) { Owner = this };
            return dlg.ShowDialog() == true ? dlg.Decisions : null;
        }

        private static string MappingSummary(List<string> log)
        {
            int renamed = log.Count(l => l.StartsWith("  "));
            string how = log.FirstOrDefault(l => l.StartsWith("Parameter mapping") || l.StartsWith("AI parameter mapping failed")) ?? "";
            return $"Parameters mapped / translated: {renamed}. {how}";
        }

        private async void BtnLoadJson_Click(object sender, RoutedEventArgs e)
        {
            string logsFolder = RFAGenSettingsManager.GetLogsFolderPath();
            var dlg = new OpenFileDialog
            {
                Title = "Load a family JSON",
                Filter = "JSON Files|*.json|All Files|*.*",
                InitialDirectory = Directory.Exists(logsFolder) ? logsFolder : null
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string json = File.ReadAllText(dlg.FileName);
                if (FamilyAiService.ValidateFamilyJson(json) != null)
                {
                    System.Windows.MessageBox.Show("This JSON file doesn't match the family schema (missing metadata or geometry).",
                        "RFAGen", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ParameterTargetContext paramCtx = BuildParameterContext(out bool paramsOk);
                if (!paramsOk) return;
                string provider = CmbProvider.SelectedItem as string ?? LlmProviders.Claude;
                string model = (CmbModel.Text ?? "").Trim();
                string mapNote = null;
                if (paramCtx != null)
                {
                    // the loaded JSON was never seen by the model: map / translate its parameters now
                    string key = _settingsManager.GetApiKeyFor(provider, model);
                    FamilyAiService mapper = string.IsNullOrWhiteSpace(key) ? null : new FamilyAiService(provider, key, model);
                    SetGenerating(true, mapper != null
                        ? $"Mapping parameters with {LlmProviders.DisplayName(provider)} ({paramCtx.LanguageName})..."
                        : "Mapping parameters (built-in dictionary, no API key)...");
                    var mapLog = new List<string>();
                    json = await ParameterMapper.MapAsync(json, paramCtx, mapper, mapLog);
                    mapNote = MappingSummary(mapLog);
                    SetGenerating(false);
                }
                GeometryInspector gi = GeometryInspector.Inspect(json);
                VerificationSummary = $"Loaded from file. Model size X×Y×Z: {gi.SizeX:0.#} × {gi.SizeY:0.#} × {gi.SizeZ:0.#} {gi.Units}" +
                                      (gi.Issues.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", gi.Issues) : "") +
                                      (mapNote != null ? "\n" + mapNote : "");
                FamilyJson = json;
                Provider = provider;
                ModelName = model;
                ApiKey = _settingsManager.GetApiKeyFor(provider, ModelName);
                FamilyName = TxtFamilyName.Text?.Trim();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                SetGenerating(false);
                System.Windows.MessageBox.Show($"Failed to load JSON: {ex.Message}", "RFAGen - Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetGenerating(bool generating, string status = null)
        {
            BtnGenerate.IsEnabled = !generating && _selectedFiles.Count > 0;
            BtnBrowse.IsEnabled = !generating;
            BtnClearFiles.IsEnabled = !generating;
            BtnLoadJson.IsEnabled = !generating;
            CmbProvider.IsEnabled = !generating;
            CmbModel.IsEnabled = !generating;
            TxtFamilyName.IsEnabled = !generating;
            TxtContext.IsEnabled = !generating;
            ChkCheckInput.IsEnabled = !generating;
            ChkVerify.IsEnabled = !generating;
            ChkUseFamilyParams.IsEnabled = !generating;
            ChkUseSharedFile.IsEnabled = !generating;
            SharedFilePanel.IsEnabled = !generating && ChkUseSharedFile.IsChecked == true;
            ProgressPanel.Visibility = generating ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (status != null) StatusLabel.Text = status;
        }
    }
}
