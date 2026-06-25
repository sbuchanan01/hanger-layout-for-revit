using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace HangerLayout.Revit
{
    /// <summary>
    /// Maps each connected fabrication part to the WORLD-POSITION origin of
    /// the connector that's closer to a user-chosen "start" element. Built
    /// once per Apply via a BFS over the fab network. Lets the placer decide
    /// which side of each fitting is "before" (far from start) vs "after"
    /// (near to start).
    ///
    /// Stores XYZ instead of an index because Revit's ConnectorManager.Connectors
    /// enumeration order is unstable across calls — a near-connector index
    /// captured during BFS may refer to a different connector when the placer
    /// re-enumerates the part. Origin position is stable.
    /// </summary>
    internal class HangerFlowMap
    {
        // ElementId.Value → world position of the connector that's CLOSER to start
        private readonly Dictionary<long, XYZ> _nearEndOrigin = new();

        // ~1.2" tolerance — well below typical pipe/duct lengths but generous
        // enough to absorb floating-point noise on connector origins.
        // NOTE: We use explicit DistanceTo, NOT XYZ.IsAlmostEqualTo(tol). The
        // latter has a non-obvious tolerance semantic (NOT plain Euclidean
        // distance) — empirically it returns true for connectors ~5 ft apart
        // when tol=0.1, which is wildly wrong for our use case. DistanceTo is
        // unambiguous.
        private const double OriginTolFt = 0.1;

        public bool IsKnown(ElementId id) =>
            _nearEndOrigin.ContainsKey(id.Value);

        public int Count => _nearEndOrigin.Count;

        /// <summary>Returns every (partId, nearConnectorOrigin) entry in the map.</summary>
        public IEnumerable<KeyValuePair<long, XYZ>> Entries => _nearEndOrigin;

        public bool IsNearEnd(ElementId id, Connector c)
        {
            if (!_nearEndOrigin.TryGetValue(id.Value, out var near)) return false;
            return c.Origin.DistanceTo(near) <= OriginTolFt;
        }

        public bool IsFarEnd(ElementId id, Connector c)
        {
            if (!_nearEndOrigin.TryGetValue(id.Value, out var near)) return false;
            return c.Origin.DistanceTo(near) > OriginTolFt;
        }

        /// <summary>
        /// BFS the fabrication network from <paramref name="startId"/>. Each
        /// visited part records the origin of the connector through which the
        /// BFS first reached it — i.e. the "near" end relative to the start.
        /// Takes the start connector's ORIGIN (not an index) to avoid the
        /// unstable-enumeration-order trap.
        /// </summary>
        public static HangerFlowMap Build(Document doc, ElementId startId, XYZ startConnectorOrigin)
        {
            var map = new HangerFlowMap();
            if (startId == null || startId == ElementId.InvalidElementId) return map;
            if (doc.GetElement(startId) is not FabricationPart startPart) return map;
            if (startConnectorOrigin == null) return map;

            var startConns = ConnectorHelper.GetPhysicalConnectors(startPart);
            if (startConns.Count == 0) return map;

            // Find the start connector by ORIGIN match (this enumeration may
            // differ from earlier ones — that's exactly why we don't trust
            // indices).
            int startNear = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < startConns.Count; i++)
            {
                double d = startConns[i].Origin.DistanceTo(startConnectorOrigin);
                if (d < bestD) { bestD = d; startNear = i; }
            }
            if (startNear < 0 || bestD > OriginTolFt) startNear = 0;
            // bestD here is already DistanceTo (computed above) — fine.
            map._nearEndOrigin[startPart.Id.Value] = startConns[startNear].Origin;

            var queue = new Queue<FabricationPart>();
            queue.Enqueue(startPart);

            while (queue.Count > 0)
            {
                var part = queue.Dequeue();
                var conns = ConnectorHelper.GetPhysicalConnectors(part);

                for (int i = 0; i < conns.Count; i++)
                {
                    var c = conns[i];
                    bool connected = false;
                    try { connected = c.IsConnected; } catch { }
                    if (!connected) continue;

                    foreach (Connector other in c.AllRefs)
                    {
                        if (other == null) continue;
                        if (other.Owner is not FabricationPart neighbor) continue;
                        if (neighbor.Id == part.Id) continue;
                        if (map._nearEndOrigin.ContainsKey(neighbor.Id.Value)) continue;
                        // Skip hangers — they're branches off the run, not part
                        // of the flow path between pipes/fittings, and they
                        // would otherwise eat into the "covered" count and
                        // possibly route the BFS into a dead-end.
                        bool isHanger = false;
                        try { isHanger = neighbor.IsAHanger(); } catch { }
                        if (isHanger) continue;

                        // The neighbor's near connector is the one whose origin
                        // matches the connection point. Store the ORIGIN, not
                        // an index — index lookups suffer from unstable
                        // ConnectorManager enumeration order.
                        map._nearEndOrigin[neighbor.Id.Value] = other.Origin;
                        queue.Enqueue(neighbor);
                    }
                }
            }

            return map;
        }

        // ────────────────────────────────────────────────────────────────────
        // Mechanical-Equipment-as-start finder
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// BFS from <paramref name="startFromConn"/> through the connector
        /// graph, counting hops to the nearest Mechanical Equipment
        /// FamilyInstance. Elements in <paramref name="initialVisited"/> are
        /// treated as already-seen (used by the caller to mask the chain's
        /// own parts so we don't recurse back into ourselves).
        ///
        /// Returns the hop count when found, or -1 if no Mechanical Equipment
        /// was reachable within <paramref name="hopLimit"/> hops.
        ///
        /// Used by HangerPlacer to decide which end of a multi-segment chain
        /// is the "source" side when no explicit Start Node was picked and
        /// the Use-Mechanical-Equipment-as-Start setting is on.
        /// </summary>
        public static int HopsToMechanicalEquipment(
            Connector startFromConn,
            HashSet<long> initialVisited,
            int hopLimit = 30)
        {
            if (startFromConn == null) return -1;
            var queue = new Queue<(Element elem, int hops)>();
            var visited = new HashSet<long>(initialVisited);

            foreach (Connector other in startFromConn.AllRefs)
            {
                var ownerElem = other.Owner;
                if (ownerElem == null) continue;
                long ownerId = ownerElem.Id.Value;
                if (visited.Contains(ownerId)) continue;
                visited.Add(ownerId);
                queue.Enqueue((ownerElem, 1));
            }

            while (queue.Count > 0)
            {
                var (elem, hops) = queue.Dequeue();
                if (hops > hopLimit) return -1;

                if (IsMechanicalEquipment(elem))
                    return hops;

                var connMgr = GetConnectorManager(elem);
                if (connMgr == null) continue;

                foreach (Connector c in connMgr.Connectors)
                {
                    foreach (Connector nextRef in c.AllRefs)
                    {
                        var nextOwner = nextRef.Owner;
                        if (nextOwner == null) continue;
                        long nextId = nextOwner.Id.Value;
                        if (visited.Contains(nextId)) continue;
                        visited.Add(nextId);
                        queue.Enqueue((nextOwner, hops + 1));
                    }
                }
            }
            return -1;
        }

        private static bool IsMechanicalEquipment(Element elem)
        {
            if (elem?.Category == null) return false;
            return elem.Category.Id.Value == (long)BuiltInCategory.OST_MechanicalEquipment;
        }

        private static ConnectorManager? GetConnectorManager(Element elem)
        {
            return elem switch
            {
                FamilyInstance fi => fi.MEPModel?.ConnectorManager,
                FabricationPart fp => fp.ConnectorManager,
                MEPCurve mc => mc.ConnectorManager,
                _ => null,
            };
        }
    }
}
