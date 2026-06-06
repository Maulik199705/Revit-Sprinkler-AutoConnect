using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Text;

namespace SprinklerAutoConnect
{
    /// <summary>
    /// Immutable snapshot of a single Revit Connector's data.
    /// Decoupled from the live API object — safe to hold after transaction.
    /// </summary>
    public sealed class ConnectorData
    {
        public int    Index         { get; }
        public XYZ    Location      { get; }
        public double DiameterMm    { get; }
        public string ConnectorType { get; }   // e.g. "End", "Curve", "Logical"
        public string Domain        { get; }   // e.g. "Piping", "Undefined"

        internal ConnectorData(int index, XYZ location, double diameterMm,
                               string connectorType, string domain)
        {
            Index         = index;
            Location      = location;
            DiameterMm    = diameterMm;
            ConnectorType = connectorType;
            Domain        = domain;
        }
    }

    /// <summary>
    /// Reusable service for extracting connector data from any MEP element.
    /// Stateless — all methods are static.
    /// </summary>
    public static class ConnectorService
    {
        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns connector data for every connector on <paramref name="element"/>.
        /// Skips connectors that throw (e.g. logical connectors with no geometry).
        /// </summary>
        /// <exception cref="ArgumentNullException"/>
        /// <exception cref="InvalidOperationException">Element has no ConnectorManager.</exception>
        public static IReadOnlyList<ConnectorData> GetConnectors(Element element)
        {
            if (element == null) throw new ArgumentNullException(nameof(element));

            ConnectorManager mgr = GetConnectorManager(element);
            var result = new List<ConnectorData>();
            int index  = 0;

            foreach (Connector c in mgr.Connectors)
            {
                try
                {
                    double diamMm = 0;
                    if (c.Shape == ConnectorProfileType.Round)
                    {
                        diamMm = UnitUtils.ConvertFromInternalUnits(
                            c.Radius * 2.0, UnitTypeId.Millimeters);
                    }

                    result.Add(new ConnectorData(
                        index:         index++,
                        location:      c.Origin,
                        diameterMm:    diamMm,
                        connectorType: c.ConnectorType.ToString(),
                        domain:        c.Domain.ToString()
                    ));
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    // Logical connectors may have no valid geometry — skip silently.
                    index++;
                }
            }

            return result;
        }

        /// <summary>
        /// Formats a connector list as a human-readable block for TaskDialog display.
        /// </summary>
        public static string FormatConnectors(
            string header,
            IReadOnlyList<ConnectorData> connectors)
        {
            var sb = new StringBuilder();
            sb.AppendLine(header);

            if (connectors.Count == 0)
            {
                sb.AppendLine("  (no connectors found)");
                return sb.ToString();
            }

            foreach (ConnectorData c in connectors)
            {
                sb.AppendLine($"  [{c.Index}] Type     : {c.ConnectorType}  |  Domain: {c.Domain}");
                sb.AppendLine($"       Location : ({MmStr(c.Location.X)}, {MmStr(c.Location.Y)}, {MmStr(c.Location.Z)}) mm");
                sb.AppendLine($"       Diameter : {c.DiameterMm:F1} mm");
            }

            return sb.ToString();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Resolves ConnectorManager for both MEPCurve (pipes, ducts, …)
        /// and FamilyInstance (sprinklers, fittings, …).
        /// </summary>
        private static ConnectorManager GetConnectorManager(Element element)
        {
            if (element is MEPCurve curve)
                return curve.ConnectorManager;

            if (element is FamilyInstance fi)
                return fi.MEPModel?.ConnectorManager
                    ?? throw new InvalidOperationException(
                        $"FamilyInstance '{element.Id.IntegerValue}' has no MEPModel/ConnectorManager.");

            throw new InvalidOperationException(
                $"Element type '{element.GetType().Name}' (Id {element.Id.IntegerValue}) " +
                "does not expose a ConnectorManager.");
        }

        /// <summary>Converts Revit internal feet → mm, returns formatted string.</summary>
        private static string MmStr(double internalValue)
        {
            double mm = UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters);
            return mm.ToString("F1");
        }
    }
}
