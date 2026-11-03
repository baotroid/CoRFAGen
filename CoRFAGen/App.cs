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

            PushButtonData btnPreGenerate = CmdPreGenerate.GetButtonData();
            PushButtonData btnSettings = CmdSettings.GetButtonData();
            PushButtonData btnPurge = CmdPurge.GetButtonData();

            panel.AddItem(btnPurge);
            panel.AddItem(btnPreGenerate);
            panel.AddItem(CmdTypeGen.GetButtonData());
            panel.AddItem(btnSettings);

            return Result.Succeeded;
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
