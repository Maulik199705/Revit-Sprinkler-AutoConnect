using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SprinklerAutoFitting
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
                return fi.Category?.Id.Value == (int)BuiltInCategory.OST_Sprinklers;
            return false;
        }
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Main Command
    // ─────────────────────────────────────────────────────────────────────────

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
            Document doc = uidoc.Document;

            try
            {
                // ── 1. Pick One Pipe ─────────────────────────────────────────
                Pipe pipe = PickPipe(uidoc, doc, ref message);
                if (pipe == null)
                    return Result.Cancelled;

                // ── 2. Pick Multiple Sprinklers ──────────────────────────────
                IList<FamilyInstance> sprinklers = PickSprinklers(uidoc, doc, ref message);
                if (sprinklers == null || sprinklers.Count == 0)
                    return Result.Cancelled;

                // ── 3. Process All in a Single Transaction ───────────────────
                using (var tx = new Transaction(doc, "Auto Connect Multiple Sprinklers"))
                {
                    tx.Start();

                    foreach (FamilyInstance sprinkler in sprinklers)
                    {
                        try
                        {
                            // Analyze and Decide
                            PipeAnalysisResult analysis = PipeAnalysisService.Analyse(pipe, sprinkler);
                            StrategyResult strategy = StrategyService.Decide(analysis, StrategyService.DefaultToleranceMm);

                            // Execute Connection
                            if (strategy.Strategy == ConnectionStrategy.EndConnection)
                            {
                                PipeCreationService.CreateEndConnectionFittings(doc, pipe, strategy, sprinkler);
                            }
                            else
                            {
                                PipeSplitService.CreateMidRunConnection(doc, pipe, analysis, sprinkler);
                            }
                        }
                        catch (Exception)
                        {
                            // Silently ignore individual sprinkler failures (e.g. occupied connectors)
                            // and continue processing the rest of the selection.
                            continue;
                        }
                    }

                    tx.Commit();
                }

                // Completely silent success — no TaskDialogs.
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // Only show a dialog if the entire command crashes critically
                TaskDialog.Show("Sprinkler Auto Connect — Error", ex.Message);
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
                    "Select Pipe — Esc to cancel");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }

            Pipe pipe = doc.GetElement(r) as Pipe;
            if (pipe == null) message = "Selected element is not a valid Pipe.";
            return pipe;
        }

        private static IList<FamilyInstance> PickSprinklers(
            UIDocument uidoc, Document doc, ref string message)
        {
            IList<Reference> refs;
            try
            {
                // PickObjects enables Window Selection and Ctrl+Click
                refs = uidoc.Selection.PickObjects(
                    ObjectType.Element, new SprinklerSelectionFilter(),
                    "Select Sprinklers (Window/Ctrl+Click). Click 'Finish' on the Options Bar when done.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }

            if (refs == null) return null;

            return refs
                .Select(r => doc.GetElement(r) as FamilyInstance)
                .Where(fi => fi != null)
                .ToList();
        }
    }
}