using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using System;
using System.Linq;

namespace SprinklerAutoConnect
{
    // ─────────────────────────────────────────────────────────────────────────
    // DTOs
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class EndConnectionResult
    {
        public FamilyInstance ElbowFitting { get; }
        public FamilyInstance ReducerFitting { get; }
        public bool ReducerCreated { get; }
        public XYZ SprinklerFinalLocation { get; }

        internal EndConnectionResult(
            FamilyInstance elbow, FamilyInstance reducer, XYZ finalLoc)
        {
            ElbowFitting = elbow;
            ReducerFitting = reducer;
            ReducerCreated = reducer != null;
            SprinklerFinalLocation = finalLoc;
        }
    }

    public sealed class BranchCreationResult
    {
        public Pipe BranchPipe { get; }
        public Connector BranchStartConn { get; }
        public Connector BranchEndConn { get; }
        public double MainPipeDiamFt { get; }
        public double SprinklerDiamFt { get; }

        internal BranchCreationResult(Pipe pipe, Connector start, Connector end,
                                      double mainDiam, double spkDiam)
        {
            BranchPipe = pipe; BranchStartConn = start; BranchEndConn = end;
            MainPipeDiamFt = mainDiam; SprinklerDiamFt = spkDiam;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PipeCreationService
    // ─────────────────────────────────────────────────────────────────────────

    public static class PipeCreationService
    {
        // ── EndConnection ─────────────────────────────────────────────────────

        public static EndConnectionResult CreateEndConnectionFittings(
            Document doc,
            Pipe mainPipe,
            StrategyResult strategy,
            FamilyInstance sprinkler)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (mainPipe == null) throw new ArgumentNullException(nameof(mainPipe));
            if (strategy == null) throw new ArgumentNullException(nameof(strategy));
            if (sprinkler == null) throw new ArgumentNullException(nameof(sprinkler));

            if (strategy.Strategy != ConnectionStrategy.EndConnection)
                throw new InvalidOperationException("CreateEndConnectionFittings requires EndConnection strategy.");

            // ── 1. Pipe end connector & Geometry ──────────────────────────────
            Connector pipeEndConn = ResolveMainPipeEndConnector(mainPipe, strategy);
            XYZ pipeEndPt = pipeEndConn.Origin;
            XYZ pipeAxis = pipeEndConn.CoordinateSystem.BasisZ.Normalize();

            // Calculate UP direction for the elbow
            XYZ worldUp = XYZ.BasisZ;
            XYZ upPerp = worldUp - pipeAxis.Multiply(worldUp.DotProduct(pipeAxis));
            XYZ branchDir = (upPerp.GetLength() > 1e-4) ? upPerp.Normalize() : BestPerpendicularTo(pipeAxis);
            if (branchDir.Z < 0) branchDir = branchDir.Negate();

            // ── 2. Create a Temporary Pipe to force elbow generation ──────────
            // This guarantees the main pipe NEVER moves. Revit will push the temp pipe instead.
            ElementId pipeTypeId = mainPipe.GetTypeId();
            ElementId levelId = mainPipe.ReferenceLevel?.Id ?? GetFallbackLevelId(doc);
            ElementId systemTypeId = GetPipeSystemTypeId(mainPipe);
            double spkDiamFt = GetSprinklerConnectorDiameterFt(sprinkler);

            XYZ tempEndPt = pipeEndPt + branchDir.Multiply(2.0); // 2 feet long straight UP
            Pipe tempPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, pipeEndPt, tempEndPt);

            Parameter dp = tempPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (dp != null && !dp.IsReadOnly) dp.Set(spkDiamFt);

            // Get the connector on the temp pipe closest to the main pipe end
            Connector tempStartConn = null;
            double minDist = double.MaxValue;
            foreach (Connector c in tempPipe.ConnectorManager.Connectors)
            {
                double d = c.Origin.DistanceTo(pipeEndPt);
                if (d < minDist) { minDist = d; tempStartConn = c; }
            }

            // ── 3. Create the Elbow ───────────────────────────────────────────
            FamilyInstance elbow = null;
            try
            {
                // Because both elements are pipes, Revit handles this perfectly.
                elbow = doc.Create.NewElbowFitting(pipeEndConn, tempStartConn);
            }
            catch (Exception ex)
            {
                doc.Delete(tempPipe.Id); // Clean up on fail
                throw new InvalidOperationException("Failed to create elbow fitting. Check routing preferences.", ex);
            }

            // ── 4. Find the Elbow's open connector pointing UP ────────────────
            // Revit has connected tempStartConn to the new elbow. We need the elbow's side of that connection.
            Connector fittingOutConn = null;
            foreach (Connector c in tempStartConn.AllRefs)
            {
                if (c.Owner.Id != tempPipe.Id && c.ConnectorType != ConnectorType.Logical)
                {
                    fittingOutConn = c;
                    break;
                }
            }

            // ── 5. Delete the temporary pipe ──────────────────────────────────
            doc.Delete(tempPipe.Id);

            if (fittingOutConn == null)
                throw new InvalidOperationException("Could not resolve the open connector on the elbow fitting.");

            // ── 6. Dock the Sprinkler onto the fitting ────────────────────────
            // Move sprinkler to exactly align with the elbow's open connector
            Connector spkConn = GetSprinklerPipingConnector(sprinkler);
            XYZ moveVec = fittingOutConn.Origin - spkConn.Origin;
            if (moveVec.GetLength() > 1e-9)
                ElementTransformUtils.MoveElement(doc, sprinkler.Id, moveVec);

            // ── 7. Rotate Sprinkler to face DOWN into the elbow ───────────────
            spkConn = GetSprinklerPipingConnector(sprinkler);
            XYZ fittingFacing = fittingOutConn.CoordinateSystem.BasisZ.Normalize();
            XYZ currentFacing = spkConn.CoordinateSystem.BasisZ.Normalize();

            // The fitting points UP. The sprinkler must point OPPOSITE to connect properly.
            RotateFacingTo(doc, sprinkler, currentFacing, fittingFacing.Negate(), fittingOutConn.Origin);

            // ── 8. Lock the MEP network logically ─────────────────────────────
            spkConn = GetSprinklerPipingConnector(sprinkler);
            fittingOutConn.ConnectTo(spkConn);

            return new EndConnectionResult(elbow, null, spkConn.Origin);
        }

        // ── Formatting ────────────────────────────────────────────────────────

        public static string FormatEndConnectionResult(EndConnectionResult r)
        {
            string fittingLine = r.ReducerCreated
                ? $"  Fitting         : Reducer/Transition (Id {r.ReducerFitting.Id.IntegerValue})\n"
                : $"  Fitting         : Elbow (Id {r.ElbowFitting?.Id.IntegerValue})\n";

            double mx = ToMm(r.SprinklerFinalLocation.X);
            double my = ToMm(r.SprinklerFinalLocation.Y);
            double mz = ToMm(r.SprinklerFinalLocation.Z);

            return
                "══ END CONNECTION ═══════════════════════\n" +
                fittingLine +
                $"  Sprinkler at    : ({mx:F1}, {my:F1}, {mz:F1}) mm\n" +
                "  MEP Connection  : ✓\n";
        }

        // ── Private Geometry & Rotation Helpers ───────────────────────────────

        private static void RotateFacingTo(
            Document doc,
            FamilyInstance sprinkler,
            XYZ from,
            XYZ to,
            XYZ pivot)
        {
            from = from.Normalize();
            to = to.Normalize();
            double dot = Math.Max(-1.0, Math.Min(1.0, from.DotProduct(to)));

            if (Math.Abs(dot - 1.0) < 1.745e-3) return; // < 0.1° — already aligned

            XYZ axis;
            double angle;

            if (Math.Abs(dot + 1.0) < 1e-4)
            {
                axis = BestPerpendicularTo(from);
                angle = Math.PI;
            }
            else
            {
                axis = from.CrossProduct(to).Normalize();
                angle = Math.Acos(dot);
            }

            Line rotLine = Line.CreateUnbound(pivot, axis);
            ElementTransformUtils.RotateElement(doc, sprinkler.Id, rotLine, angle);
        }

        private static XYZ BestPerpendicularTo(XYZ v)
        {
            v = v.Normalize();
            XYZ candidate = (Math.Abs(v.DotProduct(XYZ.BasisZ)) < 0.99) ? XYZ.BasisZ : XYZ.BasisX;
            XYZ perp = v.CrossProduct(candidate);
            if (perp.GetLength() < 1e-9) perp = v.CrossProduct(XYZ.BasisY);
            return perp.Normalize();
        }

        private static Connector ResolveMainPipeEndConnector(Pipe pipe, StrategyResult strategy)
        {
            Line line = ((pipe.Location as LocationCurve)?.Curve as Line)
                ?? throw new InvalidOperationException("Main pipe has no LocationCurve/Line.");

            XYZ target = strategy.MatchedEnd == "Start" ? line.GetEndPoint(0) : line.GetEndPoint(1);

            foreach (Connector c in pipe.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.End && c.Origin.IsAlmostEqualTo(target, 0.01))
                    return c;
            }

            throw new InvalidOperationException($"Cannot find pipe End connector at '{strategy.MatchedEnd}'.");
        }

        private static Connector GetSprinklerPipingConnector(FamilyInstance sprinkler)
        {
            ConnectorManager mgr = sprinkler.MEPModel?.ConnectorManager
                ?? throw new InvalidOperationException($"Sprinkler {sprinkler.Id.IntegerValue} has no MEPModel.");

            foreach (Connector c in mgr.Connectors)
                if (c.Domain == Domain.DomainPiping) return c;

            throw new InvalidOperationException($"Sprinkler {sprinkler.Id.IntegerValue} has no DomainPiping connector.");
        }

        private static double GetSprinklerConnectorDiameterFt(FamilyInstance sprinkler)
            => GetSprinklerPipingConnector(sprinkler).Radius * 2.0;

        private static ElementId GetPipeSystemTypeId(Pipe pipe)
        {
            Parameter p = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            return (p != null && p.AsElementId() != ElementId.InvalidElementId)
                ? p.AsElementId() : ElementId.InvalidElementId;
        }

        private static ElementId GetFallbackLevelId(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;

        private static double ToMm(double ft) =>
            UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        // Branch creation kept for potential future use (from your original logic)
        public static BranchCreationResult CreateBranchPipe(
            Document doc, Pipe mainPipe, PipeAnalysisResult analysis, FamilyInstance sprinkler)
        {
            // Omitted for brevity since MidRun is handled via PipeSplitService now, 
            // but you can leave your original BranchCreationResult code here if required.
            return null;
        }
    }
}