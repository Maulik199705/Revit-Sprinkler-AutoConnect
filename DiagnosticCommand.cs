using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SprinklerAutoFitting
{
    /// <summary>
    /// Diagnostic command — select one Pipe and one Sprinkler,
    /// dumps full model state to a .txt file and shows a TaskDialog summary.
    /// Run this BEFORE the main connect command to understand root causes.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class DiagnosticCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string          message,
            ElementSet          elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uidoc.Document;

            try
            {
                // ── Pick pipe ────────────────────────────────────────────────
                Reference pipeRef;
                try
                {
                    pipeRef = uidoc.Selection.PickObject(
                        ObjectType.Element, new PipeSelectionFilter(),
                        "DIAGNOSTIC: Select Pipe");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                { return Result.Cancelled; }

                Pipe pipe = doc.GetElement(pipeRef) as Pipe;

                // ── Pick sprinkler ───────────────────────────────────────────
                Reference spkRef;
                try
                {
                    spkRef = uidoc.Selection.PickObject(
                        ObjectType.Element, new SprinklerSelectionFilter(),
                        "DIAGNOSTIC: Select Sprinkler");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                { return Result.Cancelled; }

                FamilyInstance sprinkler = doc.GetElement(spkRef) as FamilyInstance;

                // ── Build full diagnostic report ─────────────────────────────
                var sb = new StringBuilder();
                sb.AppendLine("═══════════════════════════════════════════════════");
                sb.AppendLine("  SPRINKLER AUTO CONNECT — DIAGNOSTIC REPORT");
                sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("═══════════════════════════════════════════════════");

                DiagnosePipe(sb, pipe, doc);
                DiagnoseSprinkler(sb, sprinkler);
                DiagnoseRoutingPreferences(sb, pipe, doc);
                DiagnoseLoadedFittingFamilies(sb, doc);
                DiagnoseConnectorCompatibility(sb, pipe, sprinkler);

                string report = sb.ToString();

                // ── Write to desktop ─────────────────────────────────────────
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "SprinklerDiagnostic.txt");
                File.WriteAllText(path, report);

                // ── Show summary TaskDialog ──────────────────────────────────
                TaskDialog dlg = new TaskDialog("Sprinkler Diagnostic");
                dlg.MainInstruction = "Diagnostic Complete";
                dlg.MainContent     = BuildSummary(pipe, sprinkler, doc) +
                                      $"\n\nFull report saved to:\n{path}";
                dlg.CommonButtons   = TaskDialogCommonButtons.Ok;
                dlg.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Diagnostic Error", ex.Message);
                return Result.Failed;
            }
        }

        // ── Pipe ─────────────────────────────────────────────────────────────

        private static void DiagnosePipe(StringBuilder sb, Pipe pipe, Document doc)
        {
            sb.AppendLine();
            sb.AppendLine("── PIPE ──────────────────────────────────────────────");

            if (pipe == null) { sb.AppendLine("  ERROR: pipe is null"); return; }

            double diamMm = ToMm(pipe.Diameter);
            sb.AppendLine($"  Element Id    : {pipe.Id.Value}");
            sb.AppendLine($"  Diameter      : {diamMm:F1} mm  ({pipe.Diameter:F6} ft)");

            // Pipe type
            PipeType pt = doc.GetElement(pipe.GetTypeId()) as PipeType;
            sb.AppendLine($"  Pipe Type Id  : {pipe.GetTypeId().Value}");
            sb.AppendLine($"  Pipe Type Name: {pt?.Name ?? "NOT FOUND"}");

            // System type
            Parameter sysParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            ElementId sysId    = sysParam?.AsElementId();
            Element   sysElem  = (sysId != null && sysId != ElementId.InvalidElementId)
                                 ? doc.GetElement(sysId) : null;
            sb.AppendLine($"  System Type Id: {sysId?.Value ?? -1}");
            sb.AppendLine($"  System Type   : {sysElem?.Name ?? "NOT FOUND / Invalid"}");

            // Level
            sb.AppendLine($"  Reference Level: {pipe.ReferenceLevel?.Name ?? "null"}");

            // Location
            LocationCurve lc   = pipe.Location as LocationCurve;
            Line          line = lc?.Curve as Line;
            if (line != null)
            {
                sb.AppendLine($"  Start         : {FmtXyz(line.GetEndPoint(0))}");
                sb.AppendLine($"  End           : {FmtXyz(line.GetEndPoint(1))}");
                sb.AppendLine($"  Length        : {ToMm(line.Length):F1} mm");
            }
            else sb.AppendLine("  WARNING: No LocationCurve/Line found");

            // Connectors
            sb.AppendLine($"  Connectors ({pipe.ConnectorManager.Connectors.Size}):");
            foreach (Connector c in pipe.ConnectorManager.Connectors)
            {
                sb.AppendLine($"    [{c.ConnectorType}] Origin={FmtXyz(c.Origin)}  " +
                              $"Diam={ToMm(c.Radius * 2):F1}mm  " +
                              $"Domain={c.Domain}  " +
                              $"IsConnected={c.IsConnected}");
            }
        }

        // ── Sprinkler ─────────────────────────────────────────────────────────

        private static void DiagnoseSprinkler(StringBuilder sb, FamilyInstance sprinkler)
        {
            sb.AppendLine();
            sb.AppendLine("── SPRINKLER ─────────────────────────────────────────");

            if (sprinkler == null) { sb.AppendLine("  ERROR: sprinkler is null"); return; }

            sb.AppendLine($"  Element Id    : {sprinkler.Id.Value}");
            sb.AppendLine($"  Family Name   : {sprinkler.Symbol.FamilyName}");
            sb.AppendLine($"  Type Name     : {sprinkler.Symbol.Name}");
            sb.AppendLine($"  Category      : {sprinkler.Category?.Name}");

            LocationPoint lp = sprinkler.Location as LocationPoint;
            if (lp != null)
                sb.AppendLine($"  Location      : {FmtXyz(lp.Point)}");

            ConnectorManager mgr = sprinkler.MEPModel?.ConnectorManager;
            if (mgr == null)
            {
                sb.AppendLine("  ERROR: No MEPModel/ConnectorManager");
                return;
            }

            sb.AppendLine($"  Connectors ({mgr.Connectors.Size}):");
            foreach (Connector c in mgr.Connectors)
            {
                sb.AppendLine($"    [{c.ConnectorType}] Origin={FmtXyz(c.Origin)}  " +
                              $"Diam={ToMm(c.Radius * 2):F1}mm  " +
                              $"Domain={c.Domain}  " +
                              $"IsConnected={c.IsConnected}  " +
                              $"Shape={c.Shape}");
            }
        }

        // ── Routing Preferences ───────────────────────────────────────────────

        private static void DiagnoseRoutingPreferences(StringBuilder sb, Pipe pipe, Document doc)
        {
            sb.AppendLine();
            sb.AppendLine("── ROUTING PREFERENCES ───────────────────────────────");

            PipeType pt = doc.GetElement(pipe.GetTypeId()) as PipeType;
            if (pt == null) { sb.AppendLine("  ERROR: PipeType not found"); return; }

            RoutingPreferenceManager rpm = pt.RoutingPreferenceManager;
            if (rpm == null) { sb.AppendLine("  ERROR: No RoutingPreferenceManager"); return; }

            RoutingPreferenceRuleGroupType[] groups = new[]
            {
                RoutingPreferenceRuleGroupType.Elbows,
                RoutingPreferenceRuleGroupType.Transitions,
                RoutingPreferenceRuleGroupType.Crosses,
                RoutingPreferenceRuleGroupType.Junctions,
                RoutingPreferenceRuleGroupType.Unions,
                RoutingPreferenceRuleGroupType.Caps
            };

            foreach (var group in groups)
            {
                int count = rpm.GetNumberOfRules(group);
                sb.AppendLine($"  {group,-15}: {count} rule(s)");
                for (int i = 0; i < count; i++)
                {
                    RoutingPreferenceRule rule = rpm.GetRule(group, i);
                    Element familySymbol = doc.GetElement(rule.MEPPartId);
                    sb.AppendLine($"    [{i}] FamilySymbolId={rule.MEPPartId.Value}  " +
                                  $"Name={familySymbol?.Name ?? "NOT FOUND"}");
                }
            }
        }

        // ── Loaded Fitting Families ───────────────────────────────────────────

        private static void DiagnoseLoadedFittingFamilies(StringBuilder sb, Document doc)
        {
            sb.AppendLine();
            sb.AppendLine("── LOADED PIPE FITTING FAMILIES ──────────────────────");

            var fittings = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .OrderBy(f => f.FamilyName)
                .ToList();

            if (!fittings.Any())
            {
                sb.AppendLine("  WARNING: No pipe fitting families loaded!");
                return;
            }

            string lastFamily = null;
            foreach (FamilySymbol fs in fittings)
            {
                if (fs.FamilyName != lastFamily)
                {
                    sb.AppendLine($"  Family: {fs.FamilyName}");
                    lastFamily = fs.FamilyName;
                }
                sb.AppendLine($"    Type: {fs.Name}  (Id={fs.Id.Value})  Active={fs.IsActive}");
            }
        }

        // ── Connector Compatibility ───────────────────────────────────────────

        private static void DiagnoseConnectorCompatibility(
            StringBuilder sb, Pipe pipe, FamilyInstance sprinkler)
        {
            sb.AppendLine();
            sb.AppendLine("── CONNECTOR COMPATIBILITY CHECK ─────────────────────");

            double pipeDiam   = pipe.Diameter;
            double pipeDiamMm = ToMm(pipeDiam);

            Connector spkConn = null;
            ConnectorManager spkMgr = sprinkler.MEPModel?.ConnectorManager;
            if (spkMgr != null)
                foreach (Connector c in spkMgr.Connectors)
                    if (c.Domain == Domain.DomainPiping) { spkConn = c; break; }

            if (spkConn == null)
            {
                sb.AppendLine("  ERROR: Sprinkler has no DomainPiping connector");
                return;
            }

            double spkDiam   = spkConn.Radius * 2.0;
            double spkDiamMm = ToMm(spkDiam);
            bool   mismatch  = Math.Abs(pipeDiam - spkDiam) > 0.001;

            sb.AppendLine($"  Pipe diameter      : {pipeDiamMm:F1} mm");
            sb.AppendLine($"  Sprinkler diameter : {spkDiamMm:F1} mm");
            sb.AppendLine($"  Diameter mismatch  : {(mismatch ? $"YES — reducer required ({pipeDiamMm:F1}→{spkDiamMm:F1}mm)" : "No — direct connect")}");
            sb.AppendLine($"  Sprinkler connected: {spkConn.IsConnected}");
            sb.AppendLine($"  Sprinkler shape    : {spkConn.Shape}");

            // Distance between pipe end and sprinkler connector
            LocationCurve lc   = pipe.Location as LocationCurve;
            Line          line = lc?.Curve as Line;
            if (line != null)
            {
                double dStart = ToMm(spkConn.Origin.DistanceTo(line.GetEndPoint(0)));
                double dEnd   = ToMm(spkConn.Origin.DistanceTo(line.GetEndPoint(1)));
                sb.AppendLine($"  Dist spk→pipe start: {dStart:F1} mm");
                sb.AppendLine($"  Dist spk→pipe end  : {dEnd:F1} mm");
                sb.AppendLine($"  Nearest end        : {(dStart < dEnd ? "Start" : "End")} ({Math.Min(dStart, dEnd):F1} mm)");
            }

            // Projection
            if (line != null)
            {
                XYZ  dir  = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
                double t  = (spkConn.Origin - line.GetEndPoint(0)).DotProduct(dir);
                double perpDistMm = ToMm(spkConn.Origin.DistanceTo(
                    line.GetEndPoint(0) + dir.Multiply(
                        Math.Max(0, Math.Min(line.Length, t)))));
                sb.AppendLine($"  Perp dist to centreline: {perpDistMm:F1} mm");
                sb.AppendLine($"  ON centreline          : {(perpDistMm < 1.0 ? "YES ← likely cause of zero-length branch" : "No")}");
            }
        }

        // ── Summary (for TaskDialog) ──────────────────────────────────────────

        private static string BuildSummary(Pipe pipe, FamilyInstance sprinkler, Document doc)
        {
            var sb = new StringBuilder();

            PipeType pt  = doc.GetElement(pipe.GetTypeId()) as PipeType;
            RoutingPreferenceManager rpm = pt?.RoutingPreferenceManager;

            int elbowRules      = rpm?.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows)      ?? 0;
            int transitionRules = rpm?.GetNumberOfRules(RoutingPreferenceRuleGroupType.Transitions) ?? 0;
            int fittingCount    = new FilteredElementCollector(doc)
                                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                                    .OfClass(typeof(FamilySymbol))
                                    .GetElementCount();

            double spkDiam = 0;
            ConnectorManager sumMgr = sprinkler.MEPModel?.ConnectorManager;
            if (sumMgr != null)
                foreach (Connector c in sumMgr.Connectors)
                    if (c.Domain == Domain.DomainPiping) { spkDiam = c.Radius * 2; break; }

            sb.AppendLine($"Pipe:       {pipe.Id.Value}  Ø{ToMm(pipe.Diameter):F1}mm  [{pt?.Name ?? "?"}]");
            sb.AppendLine($"Sprinkler:  {sprinkler.Id.Value}  Ø{ToMm(spkDiam):F1}mm  [{sprinkler.Symbol.FamilyName}]");
            sb.AppendLine($"Elbow rules in routing prefs : {elbowRules}  {(elbowRules == 0 ? "← PROBLEM" : "OK")}");
            sb.AppendLine($"Transition rules             : {transitionRules}  {(transitionRules == 0 ? "← PROBLEM" : "OK")}");
            sb.AppendLine($"Pipe fitting families loaded : {fittingCount}  {(fittingCount == 0 ? "← PROBLEM" : "OK")}");

            return sb.ToString();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static double ToMm(double ft) =>
            UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        private static string FmtXyz(XYZ p) =>
            $"({ToMm(p.X):F1}, {ToMm(p.Y):F1}, {ToMm(p.Z):F1})mm";
    }
}
