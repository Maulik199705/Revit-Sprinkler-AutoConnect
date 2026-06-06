using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Text;

namespace SprinklerAutoConnect
{
    // ─────────────────────────────────────────────────────────────────────────
    // Selection Filters  (unchanged from Phase 2)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Restricts pick to Pipe elements only.</summary>
    public class PipeSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Pipe;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    /// <summary>Restricts pick to sprinkler FamilyInstance elements only.</summary>
    public class SprinklerSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is FamilyInstance fi)
                return fi.Category?.Id.IntegerValue ==
                       (int)BuiltInCategory.OST_Sprinklers;
            return false;
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main Command — Phase 4 + 5
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 4+5 — After selection:
    ///   • PipeAnalysisService computes full geometric relationship.
    ///   • StrategyService decides EndConnection vs MidRunConnection.
    ///   • Both results displayed in a single TaskDialog.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SprinklerAutoConnectCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document   doc   = uidoc.Document;

            try
            {
                // ── Step 1: Pick Pipe ────────────────────────────────────────
                Reference pipeRef;
                try
                {
                    pipeRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new PipeSelectionFilter(),
                        "Select a Pipe — press Esc to cancel");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                Pipe pipe = doc.GetElement(pipeRef) as Pipe;
                if (pipe == null)
                {
                    message = "Selected element is not a valid Pipe.";
                    return Result.Failed;
                }

                // ── Step 2: Pick Sprinkler ───────────────────────────────────
                Reference sprinklerRef;
                try
                {
                    sprinklerRef = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new SprinklerSelectionFilter(),
                        "Select a Sprinkler — press Esc to cancel");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                FamilyInstance sprinkler = doc.GetElement(sprinklerRef) as FamilyInstance;
                if (sprinkler == null)
                {
                    message = "Selected element is not a valid Sprinkler.";
                    return Result.Failed;
                }

                // ── Step 3: Pipe + sprinkler geometry (Phase 4) ──────────────
                PipeAnalysisResult analysis = PipeAnalysisService.Analyse(pipe, sprinkler);

                // ── Step 4: Connection strategy (Phase 5) ────────────────────
                StrategyResult strategy = StrategyService.Decide(
                    analysis,
                    toleranceMm: StrategyService.DefaultToleranceMm);

                // ── Step 5: Element summary header ───────────────────────────
                double pipeDiamMm = UnitUtils.ConvertFromInternalUnits(
                    pipe.Diameter, UnitTypeId.Millimeters);

                var sb = new StringBuilder();
                sb.AppendLine("══ SELECTION ════════════════════════════");
                sb.AppendLine($"  Pipe Id         : {pipe.Id.IntegerValue}");
                sb.AppendLine($"  Pipe Diameter   : {pipeDiamMm:F1} mm");
                sb.AppendLine($"  Sprinkler Id    : {sprinkler.Id.IntegerValue}");
                sb.AppendLine($"  Sprinkler Family: {sprinkler.Symbol.FamilyName}");
                sb.AppendLine();
                sb.Append(PipeAnalysisService.Format(analysis));
                sb.AppendLine();
                sb.Append(StrategyService.Format(strategy));

                // ── Step 6: Display ──────────────────────────────────────────
                TaskDialog dlg = new TaskDialog("Sprinkler Auto Connect — Analysis");
                dlg.MainInstruction = "Geometry Analysis & Connection Strategy";
                dlg.MainContent     = sb.ToString();
                dlg.CommonButtons   = TaskDialogCommonButtons.Ok;
                dlg.Show();

                return Result.Succeeded;
            }
            catch (InvalidOperationException ex)
            {
                TaskDialog.Show("Sprinkler Auto Connect — Analysis Error", ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Sprinkler Auto Connect — Error", ex.Message);
                return Result.Failed;
            }
        }
    }
}
