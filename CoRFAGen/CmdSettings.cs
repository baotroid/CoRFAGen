using System.Windows.Interop;
using CoRFAGen.UI;

namespace CoRFAGen
{
    [Transaction(TransactionMode.Manual)]
    public class CmdSettings : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;

            var window = new SettingsWindow();
            new WindowInteropHelper(window).Owner = uiapp.MainWindowHandle;
            window.ShowDialog();

            return Result.Succeeded;
        }

        internal static PushButtonData GetButtonData()
        {
            string buttonInternalName = "btnCoRFAGenSettings";
            string buttonTitle = "Settings";   // translated with the ribbon language, see App

            Helpers.ButtonDataClass myButtonData = new Helpers.ButtonDataClass(
                buttonInternalName,
                buttonTitle,
                MethodBase.GetCurrentMethod().DeclaringType?.FullName,
                Properties.Resources.settings_sliders_32,
                Properties.Resources.settings_sliders_16,
                "Configure CoRFAGen settings and API key");

            return myButtonData.Data;
        }
    }
}
