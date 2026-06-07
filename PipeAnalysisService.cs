using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using System;

namespace SprinklerAutoFitting
{
    /// <summary>
    /// Immutable result of pipe + sprinkler geometric analysis.
    /// All XYZ values are in Revit internal units (feet).
    /// All scalar distances/lengths are in mm for display.
    /// </summary>
    public sealed class PipeAnalysisResult
    {
        // ── Pipe geometry ─────────────────────────────────────────────────────
        public XYZ    PipeStart        { get; }   // internal feet
        public XYZ    PipeEnd          { get; }   // internal feet
        public double PipeLengthMm     { get; }
        public XYZ    PipeDirection    { get; }   // unit vector

        // ── Sprinkler ─────────────────────────────────────────────────────────
        public XYZ    SprinklerConnectorLocation { get; }  // internal feet

        // ── Projection ────────────────────────────────────────────────────────
        /// <summary>Closest point ON the pipe centerline to the sprinkler connector.</summary>
        public XYZ    ProjectedPoint         { get; }  // internal feet
        public double DistanceSprinklerToLineMm { get; }  // perpendicular distance
        public double DistanceToStartMm      { get; }  // projected pt → pipe start
        public double DistanceToEndMm        { get; }  // projected pt → pipe end

        /// <summary>Normalised parameter [0,1] along pipe. 0=start, 1=end.</summary>
        public double ProjectionParameter    { get; }

        internal PipeAnalysisResult(
            XYZ pipeStart, XYZ pipeEnd, double pipeLengthMm, XYZ pipeDirection,
            XYZ sprinklerConnLoc,
            XYZ projectedPoint, double distToLineMm,
            double distToStartMm, double distToEndMm,
            double projectionParameter)
        {
            PipeStart                   = pipeStart;
            PipeEnd                     = pipeEnd;
            PipeLengthMm                = pipeLengthMm;
            PipeDirection               = pipeDirection;
            SprinklerConnectorLocation  = sprinklerConnLoc;
            ProjectedPoint              = projectedPoint;
            DistanceSprinklerToLineMm   = distToLineMm;
            DistanceToStartMm           = distToStartMm;
            DistanceToEndMm             = distToEndMm;
            ProjectionParameter         = projectionParameter;
        }
    }

    /// <summary>
    /// Stateless service. Performs all geometric calculations between a Pipe
    /// and its target Sprinkler using LocationCurve and XYZ vector math.
    /// </summary>
    public static class PipeAnalysisService
    {
        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Analyses the geometric relationship between <paramref name="pipe"/> and
        /// the first piping connector of <paramref name="sprinkler"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"/>
        /// <exception cref="InvalidOperationException">
        ///   Pipe has no LocationCurve, or sprinkler has no piping connector.
        /// </exception>
        public static PipeAnalysisResult Analyse(Pipe pipe, FamilyInstance sprinkler)
        {
            if (pipe      == null) throw new ArgumentNullException(nameof(pipe));
            if (sprinkler == null) throw new ArgumentNullException(nameof(sprinkler));

            // ── 1. Pipe geometry from LocationCurve ───────────────────────────
            LocationCurve lc = pipe.Location as LocationCurve
                ?? throw new InvalidOperationException(
                    $"Pipe {pipe.Id.Value} has no LocationCurve.");

            Line pipeLine = lc.Curve as Line
                ?? throw new InvalidOperationException(
                    $"Pipe {pipe.Id.Value} LocationCurve is not a Line.");

            XYZ pipeStart = pipeLine.GetEndPoint(0);
            XYZ pipeEnd   = pipeLine.GetEndPoint(1);
            XYZ pipeDir   = (pipeEnd - pipeStart).Normalize();
            double pipeLenFt = pipeStart.DistanceTo(pipeEnd);

            // ── 2. Sprinkler connector location ───────────────────────────────
            XYZ sprinklerLoc = GetSprinklerPipingConnectorOrigin(sprinkler);

            // ── 3. Project sprinkler onto pipe centreline ─────────────────────
            //  param t = dot(S - P0, dir) — signed distance along pipe from start
            double t = (sprinklerLoc - pipeStart).DotProduct(pipeDir);

            // Clamp to [0, pipeLen] so projected point is always on the segment
            double tClamped = Math.Max(0.0, Math.Min(pipeLenFt, t));

            XYZ projectedPoint = pipeStart + pipeDir.Multiply(tClamped);

            // ── 4. Distances ──────────────────────────────────────────────────
            double distToLineFt   = sprinklerLoc.DistanceTo(projectedPoint);
            double distToStartFt  = projectedPoint.DistanceTo(pipeStart);
            double distToEndFt    = projectedPoint.DistanceTo(pipeEnd);
            double projParam      = pipeLenFt > 1e-9 ? tClamped / pipeLenFt : 0.0;

            return new PipeAnalysisResult(
                pipeStart:           pipeStart,
                pipeEnd:             pipeEnd,
                pipeLengthMm:        ToMm(pipeLenFt),
                pipeDirection:       pipeDir,
                sprinklerConnLoc:    sprinklerLoc,
                projectedPoint:      projectedPoint,
                distToLineMm:        ToMm(distToLineFt),
                distToStartMm:       ToMm(distToStartFt),
                distToEndMm:         ToMm(distToEndFt),
                projectionParameter: projParam
            );
        }

        /// <summary>Formats a <see cref="PipeAnalysisResult"/> for TaskDialog display.</summary>
        public static string Format(PipeAnalysisResult r)
        {
            return
                "══ PIPE GEOMETRY ════════════════════════\n"  +
                $"  Start      : {FmtXyz(r.PipeStart)}\n"     +
                $"  End        : {FmtXyz(r.PipeEnd)}\n"       +
                $"  Length     : {r.PipeLengthMm:F1} mm\n"    +
                $"  Direction  : {FmtVec(r.PipeDirection)}\n" +
                "\n"                                           +
                "══ SPRINKLER ════════════════════════════\n"  +
                $"  Connector  : {FmtXyz(r.SprinklerConnectorLocation)}\n" +
                "\n"                                           +
                "══ PROJECTION ═══════════════════════════\n"  +
                $"  Proj Point : {FmtXyz(r.ProjectedPoint)}\n"              +
                $"  Param [0,1]: {r.ProjectionParameter:F4}\n"              +
                $"  Dist → Pipe: {r.DistanceSprinklerToLineMm:F1} mm\n"    +
                $"  Dist → Start:{r.DistanceToStartMm:F1} mm\n"            +
                $"  Dist → End  :{r.DistanceToEndMm:F1} mm\n";
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static XYZ GetSprinklerPipingConnectorOrigin(FamilyInstance sprinkler)
        {
            ConnectorManager mgr = sprinkler.MEPModel?.ConnectorManager
                ?? throw new InvalidOperationException(
                    $"Sprinkler {sprinkler.Id.Value} has no MEPModel.");

            foreach (Connector c in mgr.Connectors)
            {
                if (c.Domain == Domain.DomainPiping)
                    return c.Origin;
            }

            throw new InvalidOperationException(
                $"Sprinkler {sprinkler.Id.Value} has no Piping connector.");
        }

        private static double ToMm(double feet) =>
            UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

        private static string FmtXyz(XYZ p) =>
            $"({ToMm(p.X):F1}, {ToMm(p.Y):F1}, {ToMm(p.Z):F1}) mm";

        private static string FmtVec(XYZ v) =>
            $"({v.X:F4}, {v.Y:F4}, {v.Z:F4})";
    }
}
