using Autodesk.Revit.UI;
using System;
using System.Reflection;

namespace SprinklerAutoFitting
{
    public class App : IExternalApplication
    {
        private const string TabName = "Fire Protection Tools";
        private const string PanelName = "Sprinkler Tools";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                app.CreateRibbonTab(TabName);
                RibbonPanel panel = app.CreateRibbonPanel(TabName, PanelName);
                string assembly = Assembly.GetExecutingAssembly().Location;

                // ── Main connect button ───────────────────────────────────────
                PushButtonData connectBtn = new PushButtonData(
                    "SprinklerAutoConnectBtn",
                    "Sprinkler\nAuto Connect",
                    assembly,
                    "SprinklerAutoFitting.SprinklerAutoConnectCommand")
                {
                    ToolTip = "Connect a sprinkler to the nearest pipe end automatically."
                };

                // ── Diagnostic button ─────────────────────────────────────────
                PushButtonData diagBtn = new PushButtonData(
                    "SprinklerDiagnoseBtn",
                    "Diagnose\nConnection",
                    assembly,
                    "SprinklerAutoFitting.DiagnosticCommand")
                {
                    ToolTip = "Inspect pipe type, routing preferences, and fitting families. " +
                              "Run this first if Auto Connect fails."
                };

                panel.AddItem(connectBtn);
                panel.AddSeparator();
                panel.AddItem(diagBtn);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("SprinklerAutoFitting — Startup Error", ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}