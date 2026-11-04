namespace CoRFAGen
{
    internal class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            // On .NET Framework (Revit 2020-2024) there are no binding redirects for add-ins,
            // so System.Text.Json and its dependencies (System.Memory, System.Buffers,
            // Microsoft.Bcl.AsyncInterfaces ...) may fail to load with a version mismatch.
            // Resolve them from the add-in folder regardless of the requested version.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAddinFolder;

            try { Loc.SetLanguage(new Data.CoRFAGenSettingsManager().GetLanguage()); }
            catch (Exception ex) { Debug.Print("Language not read: " + ex.Message); }

            string tabName = "CoRFAGen";
            try
            {
                app.CreateRibbonTab(tabName);
            }
            catch (Exception)
            {
                Debug.Print("Tab already exists.");
            }

            RibbonPanel panel = Helpers.Utils.CreateRibbonPanel(app, tabName, "Family Generator");

            _ribbonPanel = panel;
            _ribbonButtons.Clear();
            AddRibbonButton(panel, CmdPurge.GetButtonData());
            AddRibbonButton(panel, CmdPreGenerate.GetButtonData());
            AddRibbonButton(panel, CmdTypeGen.GetButtonData());
            AddRibbonButton(panel, CmdSettings.GetButtonData());
            ApplyRibbonLanguage();

            return Result.Succeeded;
        }

        private static RibbonPanel _ribbonPanel;
        private static readonly List<(RibbonItem Item, string Text, string ToolTip)> _ribbonButtons = new List<(RibbonItem, string, string)>();

        private static void AddRibbonButton(RibbonPanel panel, PushButtonData data)
        {
            string text = data.Text, tip = data.ToolTip;
            _ribbonButtons.Add((panel.AddItem(data), text, tip));
        }

        /// <summary>Panel title, button labels and tooltips in the current language.</summary>
        internal static void ApplyRibbonLanguage()
        {
            try
            {
                if (_ribbonPanel == null) return;
                _ribbonPanel.Title = Loc.T("Family Generator");
                foreach (var (item, text, tip) in _ribbonButtons)
                {
                    item.ToolTip = Loc.T(tip);
                    item.ItemText = Loc.T(text);
                }
            }
            catch (Exception ex) { Debug.Print("Ribbon language: " + ex.Message); }
        }

        public Result OnShutdown(UIControlledApplication a)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromAddinFolder;
            return Result.Succeeded;
        }

        private static Assembly ResolveFromAddinFolder(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(simpleName) || simpleName.EndsWith(".resources"))
                    return null;

                // Already loaded (any version)? Reuse it.
                Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
                if (loaded != null)
                    return loaded;

                string folder = Path.GetDirectoryName(typeof(App).Assembly.Location);
                string candidate = Path.Combine(folder ?? "", simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
