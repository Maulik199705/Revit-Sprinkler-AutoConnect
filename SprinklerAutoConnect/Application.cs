using Autodesk.Revit.UI;
using System;
using System.Reflection;

namespace SprinklerAutoConnect
{
    /// <summary>
    /// IExternalApplication — runs on Revit startup.
    /// Adds the "Fire Protection Tools" ribbon tab and "Sprinkler Auto Connect" button.
    /// </summary>
    public class App : IExternalApplication
    {
        private const string TabName    = "Fire Protection Tools";
        private const string PanelName  = "Sprinkler Tools";
        private const string ButtonName = "Sprinkler Auto Connect";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                // Create ribbon tab
                app.CreateRibbonTab(TabName);

                // Create panel inside that tab
                RibbonPanel panel = app.CreateRibbonPanel(TabName, PanelName);

                // Push-button data
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                PushButtonData buttonData = new PushButtonData(
                    name:       "SprinklerAutoConnectBtn",
                    text:       ButtonName,
                    assemblyName: assemblyPath,
                    className:  "SprinklerAutoConnect.SprinklerAutoConnectCommand"
                );
                buttonData.ToolTip = "Automatically connects sprinkler heads to the nearest pipe.";

                panel.AddItem(buttonData);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("SprinklerAutoConnect — Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app)
        {
            return Result.Succeeded;
        }
    }
}
