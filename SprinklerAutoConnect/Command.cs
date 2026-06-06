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
    // Selection Filters
    // ─────────────────────────────────────────────────────────────────────────

    public class PipeSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is Pipe;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    public class SprinklerSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem is FamilyInstance fi)
                return fi.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Sprinklers;
            return false;
        }
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main Command
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full pipeline (Phases 1–7):
    ///   1. Pick Pipe + Sprinkler
    ///   2. Analyse geometry
    ///   3. Decide strategy (End / MidRun)
    ///   4. EndConnection  → Elbow + [Reducer] + move sprinkler to fit
    ///      MidRunConnection → branch pipe (tee deferred Phase 8)
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class SprinklerAutoConnectCommand : IExternalCommand
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
                // ── 1. Pick Pipe ─────────────────────────────────────────────
                Pipe pipe = PickPipe(uidoc, doc, ref message);
                if (pipe == null)
                    return message == null ? Result.Cancelled : Result.Failed;

                // ── 2. Pick Sprinkler ────────────────────────────────────────
                FamilyInstance sprinkler = PickSprinkler(uidoc, doc, ref message);
                if (sprinkler == null)
                    return message == null ? Result.Cancelled : Result.Failed;

                // ── 3. Geometry analysis + strategy ──────────────────────────
                PipeAnalysisResult analysis = PipeAnalysisService.Analyse(pipe, sprinkler);
                StrategyResult     strategy = StrategyService.Decide(
                                                analysis, StrategyService.DefaultToleranceMm);

                // ── 4. Build report header ────────────────────────────────────
                var sb = new StringBuilder();
                AppendSelectionSummary(sb, pipe, sprinkler);
                sb.Append(PipeAnalysisService.Format(analysis));
                sb.AppendLine();
                sb.Append(StrategyService.Format(strategy));
                sb.AppendLine();

                // ── 5. Transaction ────────────────────────────────────────────
                using (var tx = new Transaction(doc, "Sprinkler Auto Connect"))
                {
                    tx.Start();
                    try
                    {
                        if (strategy.Strategy == ConnectionStrategy.EndConnection)
                        {
                            // Phase 7 — Elbow + [Reducer] + move sprinkler
                            // NO intermediate branch pipe.
                            EndConnectionResult result =
                                PipeCreationService.CreateEndConnectionFittings(
                                    doc, pipe, strategy, sprinkler);

                            sb.Append(PipeCreationService.FormatEndConnectionResult(result));
                        }
                        else
                        {
                            // MidRunConnection — branch pipe only (tee → Phase 8)
                            BranchCreationResult branchResult =
                                PipeCreationService.CreateBranchPipe(
                                    doc, pipe, analysis, sprinkler);

                            sb.AppendLine("══ BRANCH PIPE ══════════════════════════");
                            sb.AppendLine($"  Id       : {branchResult.BranchPipe.Id.IntegerValue}");
                            sb.AppendLine($"  Diameter : " +
                                $"{UnitUtils.ConvertFromInternalUnits(branchResult.BranchPipe.Diameter, UnitTypeId.Millimeters):F1} mm");
                            sb.AppendLine("  Note     : Tee fitting — Phase 8.");
                        }

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        TaskDialog.Show(
                            "Sprinkler Auto Connect — Rolled Back",
                            ex.Message + "\n\nNo changes were made to the model.");
                        return Result.Failed;
                    }
                }

                // ── 6. Display ────────────────────────────────────────────────
                TaskDialog dlg = new TaskDialog("Sprinkler Auto Connect — Complete");
                dlg.MainInstruction = "Connection Created Successfully";
                dlg.MainContent     = sb.ToString();
                dlg.CommonButtons   = TaskDialogCommonButtons.Ok;
                dlg.Show();

                return Result.Succeeded;
            }
            catch (InvalidOperationException ex)
            {
                TaskDialog.Show("Sprinkler Auto Connect — Error", ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Sprinkler Auto Connect — Unexpected Error", ex.Message);
                return Result.Failed;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Pipe PickPipe(UIDocument uidoc, Document doc, ref string message)
        {
            Reference r;
            try
            {
                r = uidoc.Selection.PickObject(
                    ObjectType.Element, new PipeSelectionFilter(),
                    "Select main Pipe — Esc to cancel");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }

            Pipe pipe = doc.GetElement(r) as Pipe;
            if (pipe == null) message = "Selected element is not a valid Pipe.";
            return pipe;
        }

        private static FamilyInstance PickSprinkler(
            UIDocument uidoc, Document doc, ref string message)
        {
            Reference r;
            try
            {
                r = uidoc.Selection.PickObject(
                    ObjectType.Element, new SprinklerSelectionFilter(),
                    "Select Sprinkler — Esc to cancel");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }

            FamilyInstance fi = doc.GetElement(r) as FamilyInstance;
            if (fi == null) message = "Selected element is not a valid Sprinkler.";
            return fi;
        }

        private static void AppendSelectionSummary(
            StringBuilder sb, Pipe pipe, FamilyInstance sprinkler)
        {
            double pipeDiamMm = UnitUtils.ConvertFromInternalUnits(
                pipe.Diameter, UnitTypeId.Millimeters);
            sb.AppendLine("══ SELECTION ════════════════════════════");
            sb.AppendLine($"  Pipe Id        : {pipe.Id.IntegerValue}");
            sb.AppendLine($"  Pipe Diameter  : {pipeDiamMm:F1} mm");
            sb.AppendLine($"  Sprinkler Id   : {sprinkler.Id.IntegerValue}");
            sb.AppendLine($"  Family Name    : {sprinkler.Symbol.FamilyName}");
            sb.AppendLine();
        }
    }
}
