using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using System;
using System.Linq;

namespace SprinklerAutoConnect
{
    public sealed class MidRunConnectionResult
    {
        public Pipe HostPipe { get; }
        public FamilyInstance TakeoffFitting { get; }
        public XYZ SprinklerFinalLocation { get; }

        internal MidRunConnectionResult(Pipe host, FamilyInstance takeoff, XYZ finalLoc)
        {
            HostPipe = host; TakeoffFitting = takeoff; SprinklerFinalLocation = finalLoc;
        }
    }

    public static class PipeSplitService
    {
        public static MidRunConnectionResult CreateMidRunConnection(
            Document doc,
            Pipe mainPipe,
            PipeAnalysisResult analysis,
            FamilyInstance sprinkler)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (mainPipe == null) throw new ArgumentNullException(nameof(mainPipe));
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (sprinkler == null) throw new ArgumentNullException(nameof(sprinkler));

            try
            {
                XYZ tapPoint = analysis.ProjectedPoint;

                // ── 1. Gather attributes for a temporary branch pipe ───────────
                ElementId pipeTypeId = mainPipe.GetTypeId();
                ElementId levelId = mainPipe.ReferenceLevel?.Id ?? GetFallbackLevelId(doc);
                ElementId systemTypeId = GetPipeSystemTypeId(mainPipe);
                double spkDiamFt = GetSprinklerConnectorDiameterFt(sprinkler);

                // Create a temporary 2-foot branch pipe pointing purely UP
                XYZ branchEnd = tapPoint + XYZ.BasisZ.Multiply(2.0);
                Pipe tempPipe = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId, tapPoint, branchEnd);

                // Set branch pipe to sprinkler diameter so the tap sizes itself correctly
                Parameter dp = tempPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dp != null && !dp.IsReadOnly) dp.Set(spkDiamFt);

                // Retrieve the starting connector of the temporary pipe 
                Connector tempStartConn = null;
                foreach (Connector c in tempPipe.ConnectorManager.Connectors)
                {
                    if (c.Origin.IsAlmostEqualTo(tapPoint, 0.01))
                    {
                        tempStartConn = c;
                        break;
                    }
                }

                if (tempStartConn == null)
                    throw new InvalidOperationException("Could not locate start connector on the temporary branch pipe.");

                // ── 2. Create the Tap (Takeoff) directly on the main pipe ──────
                // This builds a Tap on the surface without breaking/splitting mainPipe.
                FamilyInstance tap = doc.Create.NewTakeoffFitting(tempStartConn, mainPipe);

                if (tap == null)
                    throw new InvalidOperationException(
                        "NewTakeoffFitting failed. Ensure routing preferences support Tap/Spud fittings for Junctions.");

                // ── 3. Delete the temporary pipe ───────────────────────────────
                // Removing it leaves the Tap perfectly intact with an open branch connector.
                doc.Delete(tempPipe.Id);

                // ── 4. Locate the Tap's open branch connector ──────────────────
                Connector branchConn = GetTapBranchConnector(tap, analysis.PipeDirection)
                    ?? throw new InvalidOperationException("Cannot find open branch connector on tap fitting after cleanup.");

                // ── 5. Move sprinkler UP to dock exactly at the Tap ────────────
                Connector spkConn = GetSprinklerPipingConnector(sprinkler);
                XYZ moveVec = branchConn.Origin - spkConn.Origin;
                if (moveVec.GetLength() > 1e-9)
                    ElementTransformUtils.MoveElement(doc, sprinkler.Id, moveVec);

                // ── 6. Rotate sprinkler to point DOWN into the Tap ─────────────
                spkConn = GetSprinklerPipingConnector(sprinkler);
                XYZ branchFacing = branchConn.CoordinateSystem.BasisZ.Normalize();
                XYZ currentFacing = spkConn.CoordinateSystem.BasisZ.Normalize();

                // Because branch is facing UP, the Sprinkler must face DOWN 
                RotateFacingTo(doc, sprinkler, currentFacing, branchFacing.Negate(), branchConn.Origin);

                // ── 7. Lock the MEP network logically ──────────────────────────
                spkConn = GetSprinklerPipingConnector(sprinkler);
                branchConn.ConnectTo(spkConn);

                return new MidRunConnectionResult(mainPipe, tap, spkConn.Origin);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException revitEx)
            {
                throw new InvalidOperationException(
                    $"Revit API error during tap-out:\n{revitEx.Message}\n\n" +
                    "Run 'Diagnose Connection' to check routing preferences.", revitEx);
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Unexpected tap-out error: {ex.Message}", ex);
            }
        }

        public static string FormatMidRunResult(MidRunConnectionResult r)
        {
            double mx = ToMm(r.SprinklerFinalLocation.X);
            double my = ToMm(r.SprinklerFinalLocation.Y);
            double mz = ToMm(r.SprinklerFinalLocation.Z);
            return
                "══ TAP-OUT CONNECTION ═══════════════════\n" +
                $"  Host Pipe Id   : {r.HostPipe.Id.IntegerValue}\n" +
                $"  Takeoff Id     : {r.TakeoffFitting.Id.IntegerValue}\n" +
                $"  Sprinkler at   : ({mx:F1}, {my:F1}, {mz:F1}) mm\n" +
                "  MEP Connection : ✓\n";
        }

        // ── Shared connector & system helpers ─────────────────────────────────

        private static Connector GetTapBranchConnector(FamilyInstance tap, XYZ pipeAxis)
        {
            Connector best = null; double bestPerp = -1;
            ConnectorManager mgr = tap.MEPModel?.ConnectorManager;
            if (mgr == null) return null;

            foreach (Connector c in mgr.Connectors)
            {
                if (c.Domain != Domain.DomainPiping) continue;
                if (c.IsConnected) continue;

                double perp = 1.0 - Math.Abs(
                    c.CoordinateSystem.BasisZ.Normalize().DotProduct(pipeAxis.Normalize()));
                if (perp > bestPerp) { bestPerp = perp; best = c; }
            }
            return best;
        }

        private static Connector GetSprinklerPipingConnector(FamilyInstance fi)
        {
            ConnectorManager mgr = fi.MEPModel?.ConnectorManager
                ?? throw new InvalidOperationException(
                    $"Sprinkler {fi.Id.IntegerValue} has no MEPModel.");
            foreach (Connector c in mgr.Connectors)
                if (c.Domain == Domain.DomainPiping) return c;
            throw new InvalidOperationException(
                $"Sprinkler {fi.Id.IntegerValue} has no DomainPiping connector.");
        }

        private static double GetSprinklerConnectorDiameterFt(FamilyInstance fi)
        {
            return GetSprinklerPipingConnector(fi).Radius * 2.0;
        }

        private static ElementId GetPipeSystemTypeId(Pipe pipe)
        {
            Parameter p = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            return (p != null && p.AsElementId() != ElementId.InvalidElementId)
                ? p.AsElementId() : ElementId.InvalidElementId;
        }

        private static ElementId GetFallbackLevelId(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;
        }

        // ── Rotation helper ───────────────────────────────────────────────────

        private static void RotateFacingTo(
            Document doc, FamilyInstance fi, XYZ from, XYZ to, XYZ pivot)
        {
            from = from.Normalize(); to = to.Normalize();
            double dot = Math.Max(-1.0, Math.Min(1.0, from.DotProduct(to)));
            if (Math.Abs(dot - 1.0) < 1.745e-3) return;

            XYZ axis; double angle;
            if (Math.Abs(dot + 1.0) < 1e-4)
            {
                XYZ c = Math.Abs(from.DotProduct(XYZ.BasisZ)) < 0.99
                    ? XYZ.BasisZ : XYZ.BasisX;
                axis = from.CrossProduct(c).Normalize();
                angle = Math.PI;
            }
            else
            {
                axis = from.CrossProduct(to).Normalize();
                angle = Math.Acos(dot);
            }
            ElementTransformUtils.RotateElement(doc, fi.Id,
                Line.CreateUnbound(pivot, axis), angle);
        }

        private static double ToMm(double ft) =>
            UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    }
}