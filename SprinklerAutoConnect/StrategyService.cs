using System;

namespace SprinklerAutoConnect
{
    /// <summary>
    /// Describes HOW the branch pipe will connect to the main pipe.
    /// Drives all downstream geometry creation (Phase 6+).
    /// </summary>
    public enum ConnectionStrategy
    {
        /// <summary>
        /// Projected point is within tolerance of a pipe end connector.
        /// Branch connects directly to the existing end — no tee required.
        /// </summary>
        EndConnection,

        /// <summary>
        /// Projected point falls in the middle run of the pipe.
        /// A tee / tap fitting will be required at the projected point.
        /// </summary>
        MidRunConnection
    }

    /// <summary>
    /// Immutable decision result produced by <see cref="StrategyService"/>.
    /// </summary>
    public sealed class StrategyResult
    {
        public ConnectionStrategy Strategy         { get; }
        /// <summary>Which end was matched (Start/End), null for MidRun.</summary>
        public string             MatchedEnd        { get; }
        /// <summary>Distance from projected point to the matched end (mm). Null for MidRun.</summary>
        public double?            EndDistanceMm     { get; }
        public double             ToleranceMm       { get; }
        public string             Explanation       { get; }

        internal StrategyResult(
            ConnectionStrategy strategy,
            string matchedEnd,
            double? endDistanceMm,
            double toleranceMm,
            string explanation)
        {
            Strategy      = strategy;
            MatchedEnd    = matchedEnd;
            EndDistanceMm = endDistanceMm;
            ToleranceMm   = toleranceMm;
            Explanation   = explanation;
        }
    }

    /// <summary>
    /// Stateless service. Decides the <see cref="ConnectionStrategy"/> by comparing
    /// the sprinkler projection distances against a configurable end-tolerance.
    /// </summary>
    public static class StrategyService
    {
        /// <summary>
        /// End-tolerance: 50 mm — GOLDEN RULE.
        /// Only use EndConnection (elbow at pipe end) when the sprinkler
        /// centreline projects within 10 mm of a pipe end point.
        /// Everything else is MidRunConnection (tap out).
        /// </summary>
        public const double DefaultToleranceMm = 50.0;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluates <paramref name="analysis"/> and returns the optimal connection strategy.
        /// </summary>
        /// <param name="analysis">Result from <see cref="PipeAnalysisService.Analyse"/>.</param>
        /// <param name="toleranceMm">
        ///   Distance (mm) within which a projected point is considered "at a pipe end".
        ///   Defaults to <see cref="DefaultToleranceMm"/>.
        /// </param>
        public static StrategyResult Decide(
            PipeAnalysisResult analysis,
            double toleranceMm = DefaultToleranceMm)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (toleranceMm <= 0) throw new ArgumentOutOfRangeException(
                nameof(toleranceMm), "Tolerance must be positive.");

            bool nearStart = analysis.DistanceToStartMm <= toleranceMm;
            bool nearEnd   = analysis.DistanceToEndMm   <= toleranceMm;

            // Prefer whichever end is closer when both qualify
            if (nearStart && nearEnd)
            {
                bool startCloser = analysis.DistanceToStartMm <= analysis.DistanceToEndMm;
                return nearStart && startCloser
                    ? EndResult("Start", analysis.DistanceToStartMm, toleranceMm)
                    : EndResult("End",   analysis.DistanceToEndMm,   toleranceMm);
            }

            if (nearStart)
                return EndResult("Start", analysis.DistanceToStartMm, toleranceMm);

            if (nearEnd)
                return EndResult("End", analysis.DistanceToEndMm, toleranceMm);

            return new StrategyResult(
                strategy:      ConnectionStrategy.MidRunConnection,
                matchedEnd:    null,
                endDistanceMm: null,
                toleranceMm:   toleranceMm,
                explanation:
                    $"Projected point is {analysis.DistanceToStartMm:F1} mm from Start " +
                    $"and {analysis.DistanceToEndMm:F1} mm from End — " +
                    $"both exceed tolerance ({toleranceMm:F0} mm). " +
                    "A tee/tap fitting will be needed at the projected point."
            );
        }

        /// <summary>Formats a <see cref="StrategyResult"/> for TaskDialog display.</summary>
        public static string Format(StrategyResult r)
        {
            string strategyLabel = r.Strategy == ConnectionStrategy.EndConnection
                ? $"END CONNECTION  (at pipe {r.MatchedEnd})"
                : "MID-RUN CONNECTION  (tee/tap required)";

            string distLine = r.EndDistanceMm.HasValue
                ? $"  Dist to matched end : {r.EndDistanceMm.Value:F1} mm\n"
                : "";

            return
                "══ CONNECTION STRATEGY ══════════════════\n" +
                $"  Strategy    : {strategyLabel}\n"          +
                distLine                                       +
                $"  Tolerance   : {r.ToleranceMm:F0} mm\n"   +
                $"\n  Reason: {r.Explanation}\n";
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static StrategyResult EndResult(
            string end, double distMm, double toleranceMm) =>
            new StrategyResult(
                strategy:      ConnectionStrategy.EndConnection,
                matchedEnd:    end,
                endDistanceMm: distMm,
                toleranceMm:   toleranceMm,
                explanation:
                    $"Projected point is {distMm:F1} mm from pipe {end} — " +
                    $"within tolerance ({toleranceMm:F0} mm). " +
                    "Branch pipe connects directly to existing pipe end."
            );
    }
}
