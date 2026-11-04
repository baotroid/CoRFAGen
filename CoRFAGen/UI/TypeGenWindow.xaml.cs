using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CoRFAGen.Data;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace CoRFAGen.UI
{
    /// <summary>
    /// TypeGen: catalog sheet → editable type table → one parametric template → family with many types.
    /// </summary>
    public partial class TypeGenWindow : Window
    {
        private const string UseColumn = "Use";
        private readonly CoRFAGenSettingsManager _settings;
        private readonly ParameterTargetContext _familyCtx;
        private readonly List<string> _files = new List<string>();
        private List<InputAttachment> _attachments = new List<InputAttachment>();
        private bool _initializing = true;

        private DataTable _table;
        private readonly List<(string name, string type, string unit, string description)> _columns =
            new List<(string, string, string, string)>();
        private readonly List<JsonObject> _figures = new List<JsonObject>();
        private JsonObject _extraction;      // last table extraction (for the template prompt)
        private string _templateJson;        // family JSON without "catalog"
        private string _source = "";

        /// <summary>Result: complete catalog JSON (template + types).</summary>
        public string CatalogJsonResult { get; private set; }
        public string ProviderUsed { get; private set; }

        public TypeGenWindow(CoRFAGenSettingsManager settings, ParameterTargetContext familyCtx)
        {
            InitializeComponent();
            Loc.Apply(this);
            _settings = settings;
            _familyCtx = familyCtx ?? new ParameterTargetContext();
            CmbProvider.ItemsSource = LlmProviders.All;
            CmbProvider.SelectedItem = settings.GetProvider();
            LoadModels(settings.GetProvider());
            _initializing = false;
            UpdateButtons();
        }

        // ------------------------------------------------------------------ chrome / provider

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void LoadModels(string provider)
        {
            CmbModel.ItemsSource = _settings.GetAvailableModels(provider);
            CmbModel.Text = _settings.GetModelName(provider);
        }

        private void CmbProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            string p = CmbProvider.SelectedItem as string ?? LlmProviders.Claude;
            _settings.SetProvider(p);
            LoadModels(p);
        }

        private FamilyAiService CreateService(out string error)
        {
            error = null;
            string provider = CmbProvider.SelectedItem as string ?? LlmProviders.Claude;
            string model = (CmbModel.Text ?? "").Trim();
            if (string.IsNullOrEmpty(model)) model = _settings.GetModelName(provider);
            string key = _settings.GetApiKeyFor(provider, model);
            if (string.IsNullOrWhiteSpace(key))
            {
                error = $"No API key configured for {LlmProviders.DisplayName(provider)}. Open CoRFAGen › Settings, " +
                        "or use Load catalog JSON… (prepared without an API key).";
                return null;
            }
            _settings.SetModelName(provider, model);
            ProviderUsed = provider;
            var svc = new FamilyAiService(provider, key, model);
            return svc;
        }

        // ------------------------------------------------------------------ files

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = Loc.T(InputAttachment.OpenFileFilter), Multiselect = true, Title = Loc.T("Catalog sheet(s)") };
            if (dlg.ShowDialog() != true) return;
            foreach (string f in dlg.FileNames)
                if (InputAttachment.IsSupported(f) && _files.Count < 3 && !_files.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _files.Add(f);
            TxtFiles.Text = Loc.T(_files.Count == 0 ? "No files selected" : string.Join("  ·  ", _files.Select(Path.GetFileName)));
            UpdateButtons();
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            TxtFiles.Text = Loc.T("No files selected");
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            BtnExtract.IsEnabled = _files.Count > 0;
            BtnTemplate.IsEnabled = _files.Count > 0 && _table != null && _table.Rows.Count > 0;
            bool ready = _templateJson != null && _table != null && SelectedRows().Count > 0;
            BtnCreate.IsEnabled = ready;
            BtnSave.IsEnabled = ready;
            BtnPreview.IsEnabled = ready;
        }

        private void SetBusy(bool busy, string status = null)
        {
            ProgressPanel.Visibility = busy ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (status != null) StatusLabel.Text = Loc.T(status);
            foreach (System.Windows.Controls.Control c in new System.Windows.Controls.Control[]
                     { BtnExtract, BtnTemplate, BtnCreate, BtnLoad, BtnSave, BtnBrowse, BtnClearFiles, BtnPreview })
                c.IsEnabled = !busy;
            if (!busy) UpdateButtons();
        }

        // ------------------------------------------------------------------ step 1: table

        private async void BtnExtract_Click(object sender, RoutedEventArgs e)
        {
            FamilyAiService svc = CreateService(out string err);
            if (svc == null) { Warn(err); return; }
            try
            {
                SetBusy(true, $"Reading the type table with {LlmProviders.DisplayName(svc.Provider)}...");
                _attachments = _files.Select(InputAttachment.FromFile).ToList();
                svc.ValidateAttachments(_attachments);
                string response = await svc.ExtractCatalogAsync(_attachments, TxtContext.Text);
                SaveLog(response, "catalog-extraction");
                _extraction = JsonNode.Parse(LlmHttp.CleanJsonResponse(response)) as JsonObject
                              ?? throw new InvalidOperationException("The model did not return a JSON object.");
                FillFromExtraction(_extraction);
                _templateJson = null;
                TxtCheck.Text = Loc.T("Table read: " + (_table?.Rows.Count ?? 0) + " rows." + WarningsText(_extraction) +
                                "\nCheck the values, choose the figure and press «2 · Generate model».");
            }
            catch (Exception ex)
            {
                Warn("Reading the table failed: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        private static string WarningsText(JsonObject x)
        {
            var sb = new StringBuilder();
            foreach (string key in new[] { "warnings", "notes" })
                if (x?[key] is JsonArray arr && arr.Count > 0)
                    sb.Append($"\n{(key == "warnings" ? "⚠" : "•")} " + string.Join($"\n{(key == "warnings" ? "⚠" : "•")} ",
                        arr.Select(a => a?.ToString())));
            return sb.ToString();
        }

        private void FillFromExtraction(JsonObject x)
        {
            _columns.Clear();
            if (x["columns"] is JsonArray cols)
                foreach (JsonNode c in cols)
                    if (c?["name"] != null)
                        _columns.Add((Str(c["name"]), Str(c["type"]) ?? "text", Str(c["unit"]), Str(c["description"])));
            var rows = (x["types"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            foreach (JsonObject r in rows)
                foreach (var kv in r)
                    if (!_columns.Any(c => string.Equals(c.name, kv.Key, StringComparison.OrdinalIgnoreCase)))
                        _columns.Add((kv.Key, kv.Value is JsonValue v && v.TryGetValue(out double _) ? "number" : "text", null, null));
            BuildTable(rows);

            _figures.Clear();
            if (x["figures"] is JsonArray figs) _figures.AddRange(figs.OfType<JsonObject>());
            CmbFigure.ItemsSource = _figures.Select(f => Str(f["figure"])).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (_figures.Count > 0) CmbFigure.SelectedIndex = 0;
            _source = string.Join(", ", _files.Select(Path.GetFileName));
            UpdateButtons();
        }

        private void BuildTable(List<JsonObject> rows)
        {
            var t = new DataTable();
            t.Columns.Add(UseColumn, typeof(bool));
            for (int i = 0; i < _columns.Count; i++) t.Columns.Add(ColKey(i), typeof(string));
            foreach (JsonObject r in rows)
            {
                DataRow dr = t.NewRow();
                dr[UseColumn] = true;
                for (int i = 0; i < _columns.Count; i++)
                    if (r.TryGetPropertyValue(_columns[i].name, out JsonNode v) && v != null) dr[ColKey(i)] = NodeText(v);
                t.Rows.Add(dr);
            }
            _table = t;
            GridTypes.ItemsSource = _table.DefaultView;
        }

        // DataTable columns are c0, c1 ... (real names may contain commas / dots that break WPF bindings)
        private static string ColKey(int i) => "c" + i.ToString(CultureInfo.InvariantCulture);

        private int ColIndex(string name) =>
            _columns.FindIndex(c => string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase));

        private void GridTypes_AutoGeneratingColumn(object sender, System.Windows.Controls.DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.PropertyName == UseColumn) { e.Column.Header = "✓"; return; }
            if (e.PropertyName.StartsWith("c") && int.TryParse(e.PropertyName.Substring(1), out int i) && i < _columns.Count)
            {
                var c = _columns[i];
                e.Column.Header = string.IsNullOrEmpty(c.unit) ? c.name : $"{c.name}, {c.unit}";
            }
        }

        private void GridTypes_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateButtons));
        }

        private void BtnAllRows_Click(object sender, RoutedEventArgs e) => SetAllRows(true);
        private void BtnNoRows_Click(object sender, RoutedEventArgs e) => SetAllRows(false);

        private void SetAllRows(bool on)
        {
            if (_table == null) return;
            foreach (DataRow r in _table.Rows) r[UseColumn] = on;
            UpdateButtons();
        }

        // ------------------------------------------------------------------ figure / naming

        private void CmbFigure_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_initializing) return;
            string fig = CmbFigure.SelectedItem as string;
            JsonObject f = _figures.FirstOrDefault(x => Str(x["figure"]) == fig);
            if (f == null) return;
            string prefix = Str(f["prefix"]) ?? "";
            if (prefix.Contains("/")) prefix = prefix.Split('/')[0].Trim();
            TxtPrefix.Text = prefix;

            // rows with a PN the figure does not allow are unticked
            int pnCol = ColIndex("PN");
            if (f["pn"] is JsonArray pns && pns.Count > 0 && _table != null && pnCol >= 0)
            {
                var allowed = new HashSet<double>(pns.Select(p => ParseNum(NodeText(p))).Where(d => !double.IsNaN(d)));
                foreach (DataRow r in _table.Rows)
                {
                    double pn = ParseNum(r[ColKey(pnCol)] as string);
                    if (!double.IsNaN(pn)) r[UseColumn] = allowed.Contains(pn);
                }
            }
            InvalidateCatalog();
        }

        private void Naming_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing) return;
            InvalidateCatalog();
        }

        private void InvalidateCatalog()
        {
            if (_templateJson != null) RunChecks();
            UpdateButtons();
        }

        // ------------------------------------------------------------------ step 2: template

        private async void BtnTemplate_Click(object sender, RoutedEventArgs e)
        {
            FamilyAiService svc = CreateService(out string err);
            if (svc == null) { Warn(err); return; }
            List<JsonObject> rows = SelectedRowObjects();
            if (rows.Count == 0) { Warn("Tick at least one row."); return; }
            try
            {
                SetBusy(true, $"Generating the parametric model with {LlmProviders.DisplayName(svc.Provider)} (one request for all types)...");
                if (_attachments.Count == 0) _attachments = _files.Select(InputAttachment.FromFile).ToList();
                var ordered = rows.OrderBy(r => SortKey(r)).ToList();
                string rep = ordered[ordered.Count / 2].ToJsonString(Relaxed);
                string min = ordered.First().ToJsonString(Relaxed), max = ordered.Last().ToJsonString(Relaxed);

                var ext = new JsonObject
                {
                    ["product_name"] = _extraction?["product_name"]?.DeepClone(),
                    ["manufacturer"] = _extraction?["manufacturer"]?.DeepClone(),
                    ["url"] = _extraction?["url"]?.DeepClone(),
                    ["columns"] = ColumnsNode(),
                    ["figure"] = CmbFigure.Text,
                    ["prefix"] = TxtPrefix.Text,
                    ["constants"] = _extraction?["constants"]?.DeepClone(),
                    ["notes"] = _extraction?["notes"]?.DeepClone(),
                    ["types"] = new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray())
                };
                if (_familyCtx.UseFamilyParameters || _familyCtx.UseSharedFile) svc.ParameterMappingText = _familyCtx.BuildPromptText();

                string response = await svc.GenerateCatalogTemplateAsync(_attachments, ext.ToJsonString(Relaxed), rep, min, max,
                    _familyCtx.LanguageName, TxtContext.Text);
                SaveLog(response, "catalog-template");
                string json = LlmHttp.CleanJsonResponse(response);
                string invalid = FamilyAiService.ValidateFamilyJson(json);
                if (invalid != null) throw new InvalidOperationException("The model returned no valid family JSON: " + invalid);
                JsonObject t = (JsonObject)JsonNode.Parse(json);
                t.Remove("catalog");
                _templateJson = t.ToJsonString(Relaxed);
                RunChecks();
            }
            catch (Exception ex)
            {
                Warn("Model generation failed: " + ex.Message);
            }
            finally { SetBusy(false); }
        }

        // ------------------------------------------------------------------ catalog JSON

        private static readonly JsonSerializerOptions Relaxed = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private JsonArray ColumnsNode()
        {
            var arr = new JsonArray();
            foreach (var c in _columns)
            {
                var o = new JsonObject { ["name"] = c.name, ["type"] = c.type ?? "text" };
                if (!string.IsNullOrEmpty(c.unit)) o["unit"] = c.unit;
                if (!string.IsNullOrEmpty(c.description)) o["description"] = c.description;
                arr.Add(o);
            }
            return arr;
        }

        private List<DataRow> SelectedRows() =>
            _table == null ? new List<DataRow>() : _table.Rows.Cast<DataRow>().Where(r => r[UseColumn] is bool b && b).ToList();

        private List<JsonObject> SelectedRowObjects()
        {
            var list = new List<JsonObject>();
            foreach (DataRow r in SelectedRows())
            {
                var o = new JsonObject();
                for (int i = 0; i < _columns.Count; i++)
                {
                    var c = _columns[i];
                    string s = r[ColKey(i)] as string;
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    double d = ParseNum(s);
                    bool numeric = c.type != "text" && !double.IsNaN(d);
                    if (numeric)
                    {
                        if (c.type == "integer") o[c.name] = (int)Math.Round(d);
                        else o[c.name] = d;
                    }
                    else o[c.name] = s.Trim();
                }
                list.Add(o);
            }
            return list;
        }

        /// <summary>Template + selected rows + naming + toggles → catalog JSON.</summary>
        private string BuildCatalogJson()
        {
            if (_templateJson == null) return null;
            JsonObject root = (JsonObject)JsonNode.Parse(_templateJson);
            string figure = (CmbFigure.Text ?? "").Trim();
            string prefix = (TxtPrefix.Text ?? "").Trim();

            // designation / model follow the chosen figure
            if (root["parameters"] is JsonArray parms)
            {
                foreach (JsonObject p in parms.OfType<JsonObject>())
                {
                    string n = Str(p["name"]) ?? "";
                    string b = Str(p["builtin"]) ?? "";
                    if (!string.IsNullOrEmpty(figure) &&
                        (n.Equals("Обозначение", StringComparison.OrdinalIgnoreCase) || b == "Model" || n == "Model" || n == "Модель"))
                        p["default_value"] = (prefix + " " + figure).Trim();
                }
            }

            var toggles = new JsonArray();
            if (ChkActuator.IsChecked == true)
                toggles.Add(new JsonObject { ["id"] = "actuator", ["parameter"] = _familyCtx.Language == "ru" ? "Показать_привод" : "Show_Actuator", ["default"] = true });
            else if (root["geometry"] is JsonArray geom)
                foreach (JsonObject g in geom.OfType<JsonObject>()) g.Remove("toggle");

            root["catalog"] = new JsonObject
            {
                ["source"] = _source,
                ["type_name_pattern"] = TxtPattern.Text,
                ["constants"] = new JsonObject { ["Префикс"] = prefix, ["Обозначение"] = figure, ["Prefix"] = prefix, ["Figure"] = figure },
                ["columns"] = ColumnsNode(),
                ["figures"] = new JsonArray(_figures.Select(f => (JsonNode)f.DeepClone()).ToArray()),
                ["toggles"] = toggles,
                ["lookup"] = new JsonObject { ["enabled"] = ChkLookup.IsChecked == true, ["keys"] = new JsonArray("DN", "PN") },
                ["connectors"] = new JsonObject
                {
                    ["enabled"] = ChkConnectors.IsChecked == true, ["type"] = "pipe", ["system"] = "Global",
                    ["length_parameter"] = "L", ["diameter_parameter"] = "DN", ["axis"] = "x"
                },
                ["types"] = new JsonArray(SelectedRowObjects().Select(r => (JsonNode)r).ToArray())
            };
            return root.ToJsonString(Relaxed);
        }

        /// <summary>Instantiates every type and compares the model with L / H of the table.</summary>
        private void RunChecks()
        {
            string json = BuildCatalogJson();
            if (json == null) return;
            var lines = new List<string>();
            int ok = 0, bad = 0;
            var names = new List<string>();
            try
            {
                List<CatalogType> types;
                using (JsonDocument d = JsonDocument.Parse(json)) types = CatalogJson.ReadTypes(d.RootElement);
                int sets = types.Select(t => t.SetIndex).Distinct().Count();
                for (int i = 0; i < types.Count; i++)
                {
                    names.Add(types[i].Name);
                    GeometryInspector gi = GeometryInspector.Inspect(CatalogJson.Instantiate(json, i));
                    var issues = new List<string>();
                    if (TryValue(types[i], "L", out double L) && Math.Abs(gi.SizeX - L) > Math.Max(1, L * 0.01))
                        issues.Add($"length X {gi.SizeX:0} ≠ L {L:0}");
                    if (TryValue(types[i], "H", out double H) && Math.Abs(gi.MaxZ - H) > Math.Max(1, H * 0.01) &&
                        Math.Abs(gi.SizeZ - H) > Math.Max(1, H * 0.01))
                        issues.Add($"height {gi.MaxZ:0} ≠ H {H:0}");
                    issues.AddRange(gi.Issues.Take(2));
                    if (issues.Count == 0) ok++;
                    else { bad++; lines.Add($"⚠ {types[i].Name}: {string.Join("; ", issues)}"); }
                }
                TxtCheck.Text = Loc.T($"Model check: {types.Count} types, {sets} geometry sets — OK {ok}, deviations {bad}." +
                                (lines.Count > 0 ? "\n" + string.Join("\n", lines.Take(30)) : "") +
                                $"\nFirst type: {(names.Count > 0 ? names[0] : "—")}");
            }
            catch (Exception ex)
            {
                TxtCheck.Text = Loc.T("Model check failed: " + ex.Message);
            }
            int sel = CmbPreviewType.SelectedIndex;
            CmbPreviewType.ItemsSource = names;
            CmbPreviewType.SelectedIndex = names.Count == 0 ? -1 : Math.Max(0, Math.Min(sel, names.Count - 1));
            UpdateButtons();
        }

        private static bool TryValue(CatalogType t, string key, out double v)
        {
            v = 0;
            return t.Values.TryGetValue(key, out JsonElement e) && TechnicalParameters.TryGetNumber(e, out v) && v > 0;
        }

        // ------------------------------------------------------------------ preview / load / save / create

        private void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            string json = BuildCatalogJson();
            if (json == null || CmbPreviewType.SelectedIndex < 0) return;
            try
            {
                string inst = CatalogJson.Instantiate(json, CmbPreviewType.SelectedIndex);
                var pw = new PreviewWindow(inst, ProviderUsed ?? _settings.GetProvider(), null, null, _attachments,
                    null, CmbPreviewType.SelectedItem as string, TxtCheck.Text) { Owner = this };
                pw.SetPreviewOnly();
                pw.ShowDialog();
            }
            catch (Exception ex) { Warn("Preview failed: " + ex.Message); }
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            string logs = CoRFAGenSettingsManager.GetLogsFolderPath();
            var dlg = new OpenFileDialog
            {
                Filter = Loc.T("Catalog JSON|*.json|All files|*.*"),
                Title = Loc.T("Load catalog JSON (template + types)"),
                InitialDirectory = Directory.Exists(logs) ? logs : null
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                JsonObject root = JsonNode.Parse(File.ReadAllText(dlg.FileName)) as JsonObject
                                  ?? throw new InvalidOperationException("not a JSON object");
                if (!(root["catalog"] is JsonObject cat) || !(cat["types"] is JsonArray types) || types.Count == 0)
                    throw new InvalidOperationException("the file has no \"catalog\" section with types");

                _columns.Clear();
                if (cat["columns"] is JsonArray cols)
                    foreach (JsonNode c in cols)
                        if (c?["name"] != null) _columns.Add((Str(c["name"]), Str(c["type"]) ?? "text", Str(c["unit"]), Str(c["description"])));
                foreach (JsonObject r in types.OfType<JsonObject>())
                    foreach (var kv in r)
                        if (!_columns.Any(c => string.Equals(c.name, kv.Key, StringComparison.OrdinalIgnoreCase)))
                            _columns.Add((kv.Key, kv.Value is JsonValue v && v.TryGetValue(out double _) ? "number" : "text", null, null));
                BuildTable(types.OfType<JsonObject>().ToList());

                _initializing = true;
                _figures.Clear();
                if (cat["figures"] is JsonArray figs) _figures.AddRange(figs.OfType<JsonObject>().Select(f => (JsonObject)f.DeepClone()));
                CmbFigure.ItemsSource = _figures.Select(f => Str(f["figure"])).Where(s => !string.IsNullOrEmpty(s)).ToList();
                string fig = Str(cat["constants"]?["Обозначение"]) ?? Str(cat["constants"]?["Figure"]) ?? "";
                CmbFigure.Text = fig;
                if (_figures.Any(f => Str(f["figure"]) == fig)) CmbFigure.SelectedItem = fig;
                TxtPrefix.Text = Str(cat["constants"]?["Префикс"]) ?? Str(cat["constants"]?["Prefix"]) ?? "";
                if (Str(cat["type_name_pattern"]) is string pat) TxtPattern.Text = pat;
                ChkActuator.IsChecked = cat["toggles"] is JsonArray tg && tg.Count > 0;
                ChkLookup.IsChecked = !(cat["lookup"] is JsonObject lk) || lk["enabled"]?.GetValueKind() != JsonValueKind.False;
                ChkConnectors.IsChecked = !(cat["connectors"] is JsonObject cn) || cn["enabled"]?.GetValueKind() != JsonValueKind.False;
                _initializing = false;

                _source = Str(cat["source"]) ?? Path.GetFileName(dlg.FileName);
                root.Remove("catalog");
                _templateJson = root.ToJsonString(Relaxed);
                _extraction = null;
                RunChecks();
            }
            catch (Exception ex)
            {
                _initializing = false;
                Warn("Cannot load the catalog JSON: " + ex.Message);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string json = BuildCatalogJson();
            if (json == null) return;
            var dlg = new SaveFileDialog
            {
                Filter = Loc.T("Catalog JSON|*.json"),
                FileName = MakeFileName((TxtPrefix.Text + " " + CmbFigure.Text).Trim()) + "_catalog.json"
            };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            string json = BuildCatalogJson();
            if (json == null) return;
            int n = SelectedRows().Count;
            if (n > 120 && Loc.Show($"{n} types will be created. Continue?", "TypeGen",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            CatalogJsonResult = json;
            DialogResult = true;
        }

        // ------------------------------------------------------------------ helpers

        private void Warn(string msg) =>
            Loc.Show(msg, "CoRFAGen · TypeGen", MessageBoxButton.OK, MessageBoxImage.Warning);

        private static string Str(JsonNode n)
        {
            if (n == null) return null;
            try { return n.GetValue<string>(); } catch { return n.ToJsonString(); }
        }

        private static string NodeText(JsonNode v)
        {
            if (v is JsonValue jv)
            {
                if (jv.TryGetValue(out string s)) return s;
                if (jv.TryGetValue(out double d)) return d.ToString("0.###", CultureInfo.InvariantCulture);
            }
            return v?.ToJsonString() ?? "";
        }

        private static double ParseNum(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return double.NaN;
            return double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
        }

        private double SortKey(JsonObject r)
        {
            foreach (string k in new[] { "DN", "Ду", "Size" })
                if (r.TryGetPropertyValue(k, out JsonNode v) && v is JsonValue jv && jv.TryGetValue(out double d)) return d;
            var first = r.FirstOrDefault(kv => kv.Value is JsonValue jv2 && jv2.TryGetValue(out double _));
            return first.Value is JsonValue j && j.TryGetValue(out double x) ? x : 0;
        }

        private static string MakeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "family" : s.Replace(' ', '_');
        }

        private static void SaveLog(string text, string kind)
        {
            try
            {
                string folder = CoRFAGenSettingsManager.GetLogsFolderPath();
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.json"), text ?? "", new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
