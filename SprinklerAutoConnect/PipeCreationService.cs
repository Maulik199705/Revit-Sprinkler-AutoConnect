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
        public FamilyInstance ElbowFitting          { get; }
        public FamilyInstance ReducerFitting        { get; }
        public bool           ReducerCreated        { get; }
        public XYZ            SprinklerFinalLocation { get; }

        internal EndConnectionResult(
            FamilyInstance elbow, FamilyInstance reducer, XYZ finalLoc)
        {
            ElbowFitting           = elbow;
            ReducerFitting         = reducer;
            ReducerCreated         = reducer != null;
            SprinklerFinalLocation = finalLoc;
        }
    }

    public sealed class BranchCreationResult
    {
        public Pipe      BranchPipe      { get; }
        public Connector BranchStartConn { get; }
        public Connector BranchEndConn   { get; }
        public double    MainPipeDiamFt  { get; }
        public double    SprinklerDiamFt { get; }

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

    /// <summary>
    /// EndConnection strategy — direct fitting, no stub pipes.
    ///
    /// POSITIONING RULE (the key fix):
    ///   NewElbowFitting requires connectors on different elements whose
    ///   facing directions are ANTI-PARALLEL (pointing toward each other).
    ///
    ///   Pipe end connector faces OUTWARD along pipe axis  (e.g. +X).
    ///   Sprinkler connector faces along its own axis      (e.g. -Z for upright).
    ///
    ///   We must position the sprinkler so that:
    ///     1. Its connector origin == pipe end connector origin (same point).
    ///     2. Its connector facing == OPPOSITE to what it currently is,
    ///        OR we rotate the family so the connector faces the pipe direction.
    ///
    ///   Simplest valid approach: move sprinkler connector to pipe end point.
    ///   Revit's NewElbowFitting resolves the angle between the two connector
    ///   directions and picks the right elbow angle from routing preferences.
    ///
    ///   For upright sprinkler (connector faces down, -Z):
    ///     Position sprinkler so connector is AT pipe end, then elbow turns
    ///     from horizontal pipe (+X) to vertical sprinkler (+Z). 
    ///
    ///   The OFFSET applied before fitting creation:
    ///     targetPoint = pipeEndConn.Origin
    ///                 + (pipeEndConn facing direction) * elbow_radius_offset
    ///                 + (sprinkler connector facing direction NEGATED) * offset
    ///
    ///   In practice: place sprinkler connector exactly at pipe end, let Revit
    ///   resolve the elbow geometry. Works for any angle.
    /// </summary>
    public static class PipeCreationService
    {
        private const double DiamEqualToleranceFt = 0.001;

        // ── EndConnection ─────────────────────────────────────────────────────

        public static EndConnectionResult CreateEndConnectionFittings(
            Document       doc,
            Pipe           mainPipe,
            StrategyResult strategy,
            FamilyInstance sprinkler)
        {
            if (doc       == null) throw new ArgumentNullException(nameof(doc));
            if (mainPipe  == null) throw new ArgumentNullException(nameof(mainPipe));
            if (strategy  == null) throw new ArgumentNullException(nameof(strategy));
            if (sprinkler == null) throw new ArgumentNullException(nameof(sprinkler));

            if (strategy.Strategy != ConnectionStrategy.EndConnection)
                throw new InvalidOperationException(
                    "CreateEndConnectionFittings requires EndConnection strategy.");

            double mainDiamFt = mainPipe.Diameter;
            double spkDiamFt  = GetSprinklerConnectorDiameterFt(sprinkler);
            bool   mismatch   = Math.Abs(mainDiamFt - spkDiamFt) > DiamEqualToleranceFt;

            // ── 1. Pipe end connector ─────────────────────────────────────────
            Connector pipeEndConn = ResolveMainPipeEndConnector(mainPipe, strategy);

            // ── 2. Read ACTUAL sprinkler connector facing direction ────────────
            //       CoordinateSystem.BasisZ is the connector's outward normal.
            //       For a downward-facing connector (pendant): BasisZ ≈ (0,0,-1)
            //       For an upward-facing connector  (upright): BasisZ ≈ (0,0,+1)
            Connector spkConn        = GetSprinklerPipingConnector(sprinkler);
            XYZ       spkFacing      = spkConn.CoordinateSystem.BasisZ.Normalize();
            XYZ       pipeFacing     = pipeEndConn.CoordinateSystem.BasisZ.Normalize();

            // ── 3. Compute correct sprinkler position ─────────────────────────
            //
            //   For NewElbowFitting to succeed the two connectors must be:
            //     a) On different elements             ✓ (pipe vs sprinkler)
            //     b) Both open (not connected)         ✓
            //     c) Facing directions NOT parallel    — we enforce this by placement
            //
            //   Target: sprinkler connector origin placed at pipe end.
            //   The elbow fitting resolves whatever angle exists between
            //   pipeFacing and spkFacing automatically.
            //
            //   Special case: if both connectors face the SAME direction
            //   (parallel, dot product ≈ +1), Revit can't make an elbow —
            //   we must rotate the sprinkler 180° around an axis perpendicular
            //   to the connector to flip its facing.
            //
            XYZ targetOrigin = pipeEndConn.Origin;
            MoveSprinklerConnectorTo(doc, sprinkler, spkConn, targetOrigin, pipeFacing);

            // Re-fetch after move/rotate
            spkConn = GetSprinklerPipingConnector(sprinkler);

            // ── 4. Create fitting(s) ──────────────────────────────────────────
            try
            {
                FamilyInstance elbow   = null;
                FamilyInstance reducer = null;

                if (!mismatch)
                {
                    elbow = doc.Create.NewElbowFitting(pipeEndConn, spkConn);
                    if (elbow == null)
                        throw new InvalidOperationException(
                            BuildFittingError("elbow", mainDiamFt, spkDiamFt));
                }
                else
                {
                    // Try elbow first (routing prefs may include auto-reducer)
                    try { elbow = doc.Create.NewElbowFitting(pipeEndConn, spkConn); }
                    catch { /* fall through to transition */ }

                    if (elbow == null)
                    {
                        reducer = doc.Create.NewTransitionFitting(pipeEndConn, spkConn);
                        if (reducer == null)
                            throw new InvalidOperationException(
                                BuildFittingError("reducer/transition", mainDiamFt, spkDiamFt));
                    }
                }

                Connector finalSpkConn = GetSprinklerPipingConnector(sprinkler);
                return new EndConnectionResult(elbow, reducer, finalSpkConn.Origin);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException revitEx)
            {
                throw new InvalidOperationException(
                    $"Revit fitting error: {revitEx.Message}\n\n" +
                    "Routing preferences may be missing a compatible family.\n" +
                    "Run 'Diagnose Connection' to inspect pipe type rules.", revitEx);
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Unexpected error: {ex.Message}", ex);
            }
        }

        // ── Sprinkler positioning ─────────────────────────────────────────────

        /// <summary>
        /// Moves (and if necessary rotates) the sprinkler so its piping connector
        /// origin lands exactly at <paramref name="targetOrigin"/>, and its facing
        /// direction is NOT parallel to <paramref name="pipeOutwardFacing"/>.
        ///
        /// Horizontal pipe (+X facing) + upright sprinkler (+Z facing) = 90° elbow ✓
        /// Horizontal pipe (+X facing) + pendant sprinkler (-Z facing) = 90° elbow ✓
        /// Horizontal pipe (+X facing) + horizontal sprinkler (+X facing) = 0° = INVALID
        ///   → rotate sprinkler 90° around perpendicular axis so it faces +Z or -Z
        /// </summary>
        private static void MoveSprinklerConnectorTo(
            Document       doc,
            FamilyInstance sprinkler,
            Connector      spkConn,
            XYZ            targetOrigin,
            XYZ            pipeOutwardFacing)
        {
            // ── a) Translate so connector origin == targetOrigin ──────────────
            XYZ currentOrigin = spkConn.Origin;
            XYZ moveVec       = targetOrigin - currentOrigin;

            if (moveVec.GetLength() > 1e-9)
                ElementTransformUtils.MoveElement(doc, sprinkler.Id, moveVec);

            // Re-fetch after move
            spkConn = GetSprinklerPipingConnector(sprinkler);
            XYZ spkFacing = spkConn.CoordinateSystem.BasisZ.Normalize();

            // ── b) Check for parallel facing (dot product close to ±1) ────────
            double dot = spkFacing.DotProduct(pipeOutwardFacing);

            // If connectors face same direction (+1) they can't form a valid fitting.
            // Also if anti-parallel (-1) they are co-axial: valid for transition,
            // but for an elbow we need them at an angle.
            bool parallel     = Math.Abs(dot - 1.0) < 0.01;   // same direction
            bool antiParallel = Math.Abs(dot + 1.0) < 0.01;   // directly opposing

            if (!parallel && !antiParallel)
                return; // Already at a valid angle — no rotation needed

            // ── c) Rotate sprinkler 90° so connector faces perpendicular ─────
            //       Rotate axis = vector perpendicular to both pipe facing and
            //       world Z (for horizontal pipes this gives a vertical rotation).
            XYZ rotAxis = pipeOutwardFacing.CrossProduct(XYZ.BasisZ);
            if (rotAxis.GetLength() < 1e-6)
                rotAxis = pipeOutwardFacing.CrossProduct(XYZ.BasisX); // pipe is vertical

            rotAxis = rotAxis.Normalize();

            // Rotation line passes through the connector origin (= targetOrigin after move)
            Line rotLine = Line.CreateUnbound(targetOrigin, rotAxis);

            // 90° rotation aligns the sprinkler connector to point perpendicular
            // to the pipe — correct for upright/pendant sprinklers on horizontal mains
            double angle = Math.PI / 2.0; // 90°

            // Determine sign: rotate toward world +Z for upright, -Z for pendant
            // Check which 90° rotation gives a non-parallel result
            XYZ testFacing = RotateVector(spkFacing, rotAxis, angle);
            double testDot = testFacing.DotProduct(pipeOutwardFacing);
            if (Math.Abs(testDot) > 0.9)
                angle = -Math.PI / 2.0; // other direction

            ElementTransformUtils.RotateElement(doc, sprinkler.Id, rotLine, angle);
        }

        /// <summary>Rodrigues rotation formula — rotates v around axis by angle (radians).</summary>
        private static XYZ RotateVector(XYZ v, XYZ axis, double angle)
        {
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            return v.Multiply(cos)
                .Add(axis.CrossProduct(v).Multiply(sin))
                .Add(axis.Multiply(axis.DotProduct(v) * (1 - cos)));
        }

        // ── MidRun branch pipe ────────────────────────────────────────────────

        public static BranchCreationResult CreateBranchPipe(
            Document           doc,
            Pipe               mainPipe,
            PipeAnalysisResult analysis,
            FamilyInstance     sprinkler)
        {
            if (doc       == null) throw new ArgumentNullException(nameof(doc));
            if (mainPipe  == null) throw new ArgumentNullException(nameof(mainPipe));
            if (analysis  == null) throw new ArgumentNullException(nameof(analysis));
            if (sprinkler == null) throw new ArgumentNullException(nameof(sprinkler));

            XYZ branchStart = analysis.ProjectedPoint;
            XYZ branchEnd   = analysis.SprinklerConnectorLocation;

            if (branchStart.IsAlmostEqualTo(branchEnd, 1e-6))
                throw new InvalidOperationException(
                    "Branch start and end coincident — sprinkler is on pipe centreline.");

            ElementId pipeTypeId   = mainPipe.GetTypeId();
            ElementId levelId      = mainPipe.ReferenceLevel?.Id ?? GetFallbackLevelId(doc);
            ElementId systemTypeId = GetPipeSystemTypeId(mainPipe);
            double    spkDiamFt    = GetSprinklerConnectorDiameterFt(sprinkler);

            Pipe branch = Pipe.Create(doc, systemTypeId, pipeTypeId, levelId,
                                      branchStart, branchEnd);
            if (branch == null)
                throw new InvalidOperationException("Pipe.Create returned null.");

            Parameter dp = branch.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (dp != null && !dp.IsReadOnly) dp.Set(spkDiamFt);

            GetOrderedConnectors(branch, branchStart, out Connector sc, out Connector ec);
            return new BranchCreationResult(branch, sc, ec, mainPipe.Diameter, spkDiamFt);
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
                "══ FITTING CREATED ══════════════════════\n" +
                fittingLine                                    +
                $"  Sprinkler at    : ({mx:F1}, {my:F1}, {mz:F1}) mm\n" +
                "  MEP Connection  : ✓\n";
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static string BuildFittingError(
            string fittingType, double mainDiamFt, double spkDiamFt) =>
            $"No {fittingType} family found in routing preferences for\n" +
            $"Ø{ToMm(mainDiamFt):F1}mm → Ø{ToMm(spkDiamFt):F1}mm.\n\n" +
            "Fix: open Pipe Type → Routing Preferences → add the Viking/\n" +
            "AutoSPRINK family under Elbows (and Transitions if sizes differ).\n" +
            "Then run Auto Connect again.";

        private static Connector ResolveMainPipeEndConnector(
            Pipe pipe, StrategyResult strategy)
        {
            Line line = ((pipe.Location as LocationCurve)?.Curve as Line)
                ?? throw new InvalidOperationException(
                    "Main pipe has no LocationCurve/Line.");

            XYZ target = strategy.MatchedEnd == "Start"
                ? line.GetEndPoint(0) : line.GetEndPoint(1);

            foreach (Connector c in pipe.ConnectorManager.Connectors)
                if (c.ConnectorType == ConnectorType.End &&
                    c.Origin.IsAlmostEqualTo(target, 0.01))
                    return c;

            throw new InvalidOperationException(
                $"Cannot find pipe End connector at '{strategy.MatchedEnd}'.");
        }

        private static Connector GetSprinklerPipingConnector(FamilyInstance sprinkler)
        {
            ConnectorManager mgr = sprinkler.MEPModel?.ConnectorManager
                ?? throw new InvalidOperationException(
                    $"Sprinkler {sprinkler.Id.IntegerValue} has no MEPModel.");
            foreach (Connector c in mgr.Connectors)
                if (c.Domain == Domain.DomainPiping) return c;
            throw new InvalidOperationException(
                $"Sprinkler {sprinkler.Id.IntegerValue} has no DomainPiping connector.");
        }

        private static double GetSprinklerConnectorDiameterFt(FamilyInstance sprinkler)
            => GetSprinklerPipingConnector(sprinkler).Radius * 2.0;

        private static ElementId GetPipeSystemTypeId(Pipe pipe)
        {
            Parameter p = pipe.get_Parameter(
                BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            return (p != null && p.AsElementId() != ElementId.InvalidElementId)
                ? p.AsElementId() : ElementId.InvalidElementId;
        }

        private static ElementId GetFallbackLevelId(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault()?.Id ?? ElementId.InvalidElementId;

        private static void GetOrderedConnectors(Pipe pipe, XYZ near,
            out Connector nearConn, out Connector farConn)
        {
            nearConn = null; farConn = null;
            foreach (Connector c in pipe.ConnectorManager.Connectors)
            {
                if (c.ConnectorType != ConnectorType.End) continue;
                if (nearConn == null) nearConn = c;
                else if (c.Origin.DistanceTo(near) < nearConn.Origin.DistanceTo(near))
                { farConn = nearConn; nearConn = c; }
                else farConn = c;
            }
            if (nearConn == null || farConn == null)
                throw new InvalidOperationException(
                    $"Pipe {pipe.Id.IntegerValue} does not have two End connectors.");
        }

        private static double ToMm(double ft) =>
            UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
    }
}
