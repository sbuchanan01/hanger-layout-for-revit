using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using HangerLayout.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HangerLayout.Revit
{
    /// <summary>
    /// Places fabrication hangers on straight FabricationPart pipes/ducts according
    /// to a SupportSpec's size-banded spacing rules. Caller wraps in a Transaction.
    /// </summary>
    internal static class HangerPlacer
    {
        // ── Hanger shape-compatibility filter ──────────────────────────────
        // Used by ResolveHangerButton to make sure a continuous mixed run
        // (round + rectangular ducts) picks the right hanger type per part
        // when no HangerOverride is set.
        //
        // Three-step precedence per button:
        //   1. Name contains an "explicit Round" keyword (e.g. ROUND, PIPE)
        //      → Round-compatible. This wins even if a rect-form-factor word
        //      is also present, e.g. "Trapeze Hanger Round" — the explicit
        //      ROUND signal trumps the form-factor.
        //   2. Otherwise, name contains a rect-only keyword (BEARER, TRAPEZE,
        //      RECTANGULAR) → Rect-only.
        //   3. Otherwise (default) → Round-compatible. This catches Clevis /
        //      Ring / Strap / Half Strap / J-Hook / Loop / Gripple / generic
        //      "Hanger" / vendor SKUs without enumerating every variant.
        //
        // Matching is case-insensitive substring against btn.Name. To handle
        // non-standard vendor naming, either add a keyword here, or set an
        // explicit HangerOverride on the SupportSpec — overrides bypass this
        // filter.
        private static readonly string[] RoundExplicitHangerKeywords =
            { "ROUND", "PIPE" };
        private static readonly string[] RectOnlyHangerKeywords =
            { "RECTANGULAR", "BEARER", "TRAPEZE" };

        public class Outcome
        {
            public int Placed         { get; set; }
            public int SkippedShort   { get; set; }
            public int SkippedNoSpec  { get; set; }
            public int SkippedNoButton{ get; set; }
            public int SkippedTooClose{ get; set; }
            public int OversizeBand   { get; set; }
            public int CreateFailed   { get; set; }
            // Per-chain orientation tally (multi-segment chains only;
            // single-segment "chains" don't contribute).
            public int ChainsOrientedByStartNode { get; set; }
            public int ChainsOrientedByMechEq    { get; set; }
            public int ChainsOrientedAuto        { get; set; }
            public bool DumpedDiagnostics { get; set; }
            public List<ElementId> CreatedIds { get; } = new();
            public List<string> Notes { get; } = new();
        }

        /// <summary>
        /// Top-level apply. parts is the user-collected set; spec is chosen for that domain.
        /// flowMap (optional) lets Before/After modes decide which side of each fitting gets the
        /// hanger based on a user-picked start element.
        /// </summary>
        public static void Place(
            Document doc,
            IEnumerable<FabricationPart> parts,
            SupportSpec spec,
            Outcome outcome,
            HangerFlowMap? flowMap = null,
            bool attachToStructure = false,
            double minSpacingFt = 0.0,
            bool useMechEqAsStart = false)
        {
            if (spec == null || spec.Rows == null || spec.Rows.Count == 0)
            {
                outcome.SkippedNoSpec += parts.Count();
                return;
            }

            var sortedRows = spec.Rows
                .Where(r => r.MaxSizeInches > 0)
                .OrderBy(r => r.MaxSizeInches)
                .ToList();
            if (sortedRows.Count == 0)
            {
                outcome.SkippedNoSpec += parts.Count();
                return;
            }

            // Group user-selected parts into chains (consecutive straights
            // joined by joint-only interfaces). Only valid when
            // StraightJoints=NotAtJoint — otherwise each joint contributes its
            // own anchored hanger and per-segment processing is correct.
            //
            // For each chain: only the leftmost-in-chain selected segment
            // ("lead") drives placement; the others are silently coalesced.
            // Single-segment chains fall through to the original per-segment
            // path so behaviour is unchanged for non-chained content.
            var partList = parts.ToList();
            var selectedIds = new HashSet<long>(partList.Select(p => p.Id.Value));
            bool spanChains = spec.StraightJoints == StraightJointMode.NotAtJoint;
            var visited = new HashSet<long>();

            foreach (var part in partList)
            {
                if (visited.Contains(part.Id.Value)) continue;
                try
                {
                    ChainInfo chain = spanChains
                        ? BuildChainInfo(doc, part, selectedIds, flowMap, useMechEqAsStart, outcome)
                        : new ChainInfo();  // empty chain → fall through to per-segment
                    if (chain.Segments.Count > 1)
                    {
                        // Mark every segment in the chain as visited so we don't
                        // re-process them when the outer loop reaches them.
                        foreach (var seg in chain.Segments)
                            visited.Add(seg.Part.Id.Value);
                        PlaceForChain(doc, chain, spec, sortedRows, outcome, flowMap, attachToStructure, minSpacingFt);
                    }
                    else
                    {
                        PlaceForPart(doc, part, spec, sortedRows, outcome, flowMap, attachToStructure, minSpacingFt);
                    }
                }
                catch (Exception ex)
                {
                    outcome.Notes.Add($"[skip {part.Id.Value}] {ex.Message}");
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // Per-part placement
        // ────────────────────────────────────────────────────────────────────────

        private static void PlaceForPart(
            Document doc,
            FabricationPart part,
            SupportSpec spec,
            List<SupportSpecRow> sortedRows,
            Outcome outcome,
            HangerFlowMap? flowMap,
            bool attachToStructure,
            double minSpacingFt = 0.0)
        {
            // Pick the two End connectors (a tee-tapped pipe also has Curve connectors,
            // which we ignore for the hanger axis).
            var allConns = ConnectorHelper.GetPhysicalConnectors(part);
            var endConns = new List<Connector>();
            foreach (var c in allConns) if (c.ConnectorType == ConnectorType.End) endConns.Add(c);
            var conns = endConns.Count == 2 ? endConns : allConns;
            if (conns.Count != 2)
            {
                outcome.SkippedShort++;
                outcome.Notes.Add($"[skip {part.Id.Value}] not 2 end connectors (have {conns.Count})");
                return;
            }

            var c0 = conns[0];
            var c1 = conns[1];

            XYZ p0 = c0.Origin;
            XYZ p1 = c1.Origin;
            XYZ axisVec = p1 - p0;
            double length = axisVec.GetLength();
            if (length < 1e-6)
            {
                outcome.SkippedShort++;
                outcome.Notes.Add($"[skip {part.Id.Value}] zero-length axis");
                return;
            }
            XYZ axis = axisVec.Normalize();

            double sizeInches = ResolveSizeInches(c0, c1);

            // Pick band: smallest row whose MaxSize >= partSize; else largest
            SupportSpecRow row = sortedRows[sortedRows.Count - 1];
            bool oversize = true;
            foreach (var r in sortedRows)
            {
                if (sizeInches <= r.MaxSizeInches)
                {
                    row = r;
                    oversize = false;
                    break;
                }
            }
            if (oversize) outcome.OversizeBand++;

            // For "Before"/"After" mode resolution: where does each end sit relative
            // to the user-picked start? The "near" end of the part (closer to start
            // in the BFS) is the AFTER side of any fitting on that end; the FAR end
            // is the BEFORE side. Query flowMap by Connector (it matches on world
            // origin internally) since ConnectorManager enumeration order is unstable.
            bool partKnownToFlow = flowMap != null && flowMap.IsKnown(part.Id);
            bool? aIsFar = partKnownToFlow
                ? flowMap!.IsFarEnd(part.Id, c0)
                : (bool?)null;
            bool? bIsFar = partKnownToFlow
                ? flowMap!.IsFarEnd(part.Id, c1)
                : (bool?)null;

            // When both ends classify identically (both near or both far) we
            // have a flow-map bug — surface the raw connector origins + the
            // stored near origin so we can see what's confusing the matcher.
            if (partKnownToFlow && aIsFar == bIsFar)
            {
                XYZ? nearOrigin = null;
                foreach (var kv in flowMap!.Entries)
                {
                    if (kv.Key == part.Id.Value) { nearOrigin = kv.Value; break; }
                }
                string n = nearOrigin == null
                    ? "(none)"
                    : $"({nearOrigin.X:F3},{nearOrigin.Y:F3},{nearOrigin.Z:F3})";
                outcome.Notes.Add(
                    $"[flow-bug {part.Id.Value}] both ends classify '{(aIsFar == true ? "far" : "near")}' " +
                    $"c0=({c0.Origin.X:F3},{c0.Origin.Y:F3},{c0.Origin.Z:F3}) " +
                    $"c1=({c1.Origin.X:F3},{c1.Origin.Y:F3},{c1.Origin.Z:F3}) " +
                    $"stored-near={n} c0Type={c0.ConnectorType} c1Type={c1.ConnectorType} " +
                    $"allConns={ConnectorHelper.GetPhysicalConnectors(part).Count}");
            }
            // Surface unmapped pipes — they fall back to symmetric anchoring
            // (= "Before and After" behavior), which is usually NOT what the
            // user wants when they picked a Starting Node. Note them in the
            // diagnostic so we can see which connections the BFS missed.
            if (flowMap != null && !partKnownToFlow)
            {
                outcome.Notes.Add(
                    $"[unmapped {part.Id.Value}] not reached by flow BFS — " +
                    $"falling back to symmetric anchor (both ends).");
            }

            var endA = ClassifyEnd(doc, part, c0, row, spec, aIsFar);
            var endB = ClassifyEnd(doc, part, c1, row, spec, bIsFar);

            // Per-part diagnostic for every host pipe/duct. Includes flow side
            // (near/far) so we can see which end the placer treated as BEFORE
            // vs AFTER relative to the user-picked start.
            string sideA = aIsFar == null ? "?" : (aIsFar.Value ? "far"  : "near");
            string sideB = bIsFar == null ? "?" : (bIsFar.Value ? "far"  : "near");
            outcome.Notes.Add(
                $"[anchor {part.Id.Value}] len={length * 12:F1}\" " +
                $"endA={DescribeEnd(doc, part, c0)}/{sideA}/{(endA.Anchored ? endA.SetbackInches.ToString("0.#") : "no")} " +
                $"endB={DescribeEnd(doc, part, c1)}/{sideB}/{(endB.Anchored ? endB.SetbackInches.ToString("0.#") : "no")}");

            double freeStartFt = endA.Anchored ? InchesToFeet(endA.SetbackInches) : 0;
            double freeEndFt   = length - (endB.Anchored ? InchesToFeet(endB.SetbackInches) : 0);
            double freeLenFt   = freeEndFt - freeStartFt;

            if (row.StraightSpacingInches <= 0)
            {
                outcome.SkippedShort++;
                outcome.Notes.Add($"[skip {part.Id.Value}] spec straight-spacing is 0");
                return;
            }
            if (freeLenFt <= 1e-6)
            {
                outcome.SkippedShort++;
                outcome.Notes.Add(
                    $"[skip {part.Id.Value}] length {length * 12:F1}\" ≤ end-setbacks " +
                    $"({endA.SetbackInches:F1}\"+{endB.SetbackInches:F1}\")");
                return;
            }

            double spacingFt = InchesToFeet(row.StraightSpacingInches);

            // Compute hanger positions along the host axis.
            //   - Anchored end → place a hanger AT the setback (4" from the fitting/joint).
            //   - Open end (mode says "skip this side") → no hanger at the end; spacing
            //     is measured from the open end's connector (= the elbow/joint face).
            //   - Between, step every StraightSpacing.
            //   - Special case: if one end is open and exactly ONE intermediate
            //     hanger fits between the open end and the anchored hanger, centre
            //     that intermediate between them (per user-specified rule).
            const double epsFt = 1e-3;
            var positionsFt = BuildPositions(
                length, freeStartFt, freeEndFt, spacingFt,
                endA.Anchored, endB.Anchored, epsFt);
            positionsFt = ApplyMinSpacing(positionsFt, minSpacingFt, outcome);

            if (positionsFt.Count == 0)
            {
                outcome.SkippedShort++;
                outcome.Notes.Add($"[skip {part.Id.Value}] no positions computed");
                return;
            }

            // Resolve hanger button + condition once for this part's service.
            // Pass the host's connector profile so the default-path fallback
            // picks a shape-compatible hanger when no HangerOverride is set —
            // matters for mixed round + rectangular duct runs.
            string serviceName = part.ServiceName ?? string.Empty;
            ConnectorProfileType hostShape = ConnectorProfileType.Round;
            try { hostShape = c0.Shape; } catch { }
            var (button, condition, hangerNote) =
                ResolveHangerButton(doc, serviceName, spec.HangerOverride, sizeInches, hostShape);
            if (button == null)
            {
                outcome.SkippedNoButton++;
                outcome.Notes.Add($"[no hanger {part.Id.Value}] service '{serviceName}': {hangerNote}");
                return;
            }

            // One-shot API diagnostic: dump all CreateHanger overloads + button conditions
            // the first time we attempt placement, so we can see what Revit actually exposes.
            if (!outcome.DumpedDiagnostics)
            {
                outcome.DumpedDiagnostics = true;
                DumpApiDiagnostics(button, outcome);
            }

            // Distances measured from c0 (origin = p0) along the host's axis.
            // The hosted CreateHanger overload places the hanger at the given
            // distance from the supplied host connector and auto-orients/sizes.
            foreach (var offsetFt in positionsFt)
            {
                FabricationPart? hanger = null;
                string? createErr = null;
                try
                {
                    hanger = CreateHangerOnHost(doc, button, condition, part, c0, offsetFt, attachToStructure);
                }
                catch (Exception ex) { createErr = ex.Message; }

                if (hanger == null)
                {
                    outcome.CreateFailed++;
                    outcome.Notes.Add(
                        $"[create-failed {part.Id.Value}] btn='{button.Name}' cond={condition} " +
                        $"dist={offsetFt:F2}ft: {createErr ?? "Create returned null"}");
                    continue;
                }

                if (outcome.Placed == 0 && outcome.Notes.Count(n => n.StartsWith("[diag")) < 3)
                {
                    try { doc.Regenerate(); } catch { }
                    DumpHangerDiagnostics(hanger, outcome);
                }

                outcome.Placed++;
                outcome.CreatedIds.Add(hanger.Id);
            }
        }

        /// <summary>Sliding tolerance: include the endpoint when within 0.001ft.</summary>
        private static double freeEnd_AtTolerance(double freeEndFt) => freeEndFt + 1e-3;

        // ────────────────────────────────────────────────────────────────────────
        // Diagnostic dump (one-shot per Apply)
        // ────────────────────────────────────────────────────────────────────────

        private static string ResolveDumpPath()
        {
            // Try Documents first (always exists, always writable, easy for user to find).
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrWhiteSpace(docs) && System.IO.Directory.Exists(docs))
                    return System.IO.Path.Combine(docs, "hanger_diag.txt");
            }
            catch { }
            try
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hanger_diag.txt");
            }
            catch { }
            return @"C:\hanger_diag.txt";
        }

        private static void DumpApiDiagnostics(FabricationServiceButton btn, Outcome outcome)
        {
            string path = ResolveDumpPath();
            try
            {
                using var sw = new System.IO.StreamWriter(path, append: true);
                sw.WriteLine($"=== Hanger API diagnostics @ {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                sw.WriteLine($"Button: '{btn.Name}'  IsAHanger={btn.IsAHanger}  ConditionCount={btn.ConditionCount}");
                for (int ci = 0; ci < btn.ConditionCount; ci++)
                {
                    string cn = "?"; try { cn = btn.GetConditionName(ci) ?? "?"; } catch { }
                    sw.WriteLine($"  cond[{ci}] = '{cn}'");
                }
                sw.WriteLine();
                sw.WriteLine("FabricationPart static factory methods:");
                foreach (var m in typeof(FabricationPart).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (!(m.Name.StartsWith("Create") || m.Name.StartsWith("Place"))) continue;
                    string sig = string.Join(", ", m.GetParameters()
                        .Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sw.WriteLine($"  {m.Name}({sig})");
                }
                long size = 0;
                try { size = new System.IO.FileInfo(path).Length; } catch { }
                outcome.Notes.Add($"[diag] dump @ {path} ({size} bytes)");
            }
            catch (Exception ex)
            {
                outcome.Notes.Add($"[diag-fail] tried '{path}': {ex.Message}");
            }
        }

        private static void DumpHangerDiagnostics(FabricationPart hanger, Outcome outcome)
        {
            string path = ResolveDumpPath();
            try
            {
                using var sw = new System.IO.StreamWriter(path, append: true);
                sw.WriteLine();
                sw.WriteLine("=== First placed hanger ===");
                sw.WriteLine($"Id={hanger.Id.Value}  Category={hanger.Category?.Name}");

                // Connectors
                try
                {
                    var mgr = hanger.ConnectorManager;
                    if (mgr != null)
                    {
                        int idx = 0;
                        foreach (Connector c in mgr.Connectors)
                        {
                            string bz = "?";
                            try { var z = c.CoordinateSystem.BasisZ; bz = $"({z.X:F3},{z.Y:F3},{z.Z:F3})"; }
                            catch { }
                            string origin = "?";
                            try { var o = c.Origin; origin = $"({o.X:F2},{o.Y:F2},{o.Z:F2})"; }
                            catch { }
                            string radius = "?";
                            try { radius = c.Radius.ToString("F3"); } catch { }
                            string connected = "?";
                            try { connected = c.IsConnected.ToString(); } catch { }
                            sw.WriteLine(
                                $"  conn[{idx}] type={c.ConnectorType} domain={c.Domain} " +
                                $"shape={c.Shape} radius={radius} connected={connected} " +
                                $"origin={origin} basisZ={bz}");
                            idx++;
                        }
                    }
                }
                catch (Exception ex) { sw.WriteLine($"  conn-enum failed: {ex.Message}"); }

                // Parameters that look size-related
                sw.WriteLine("Size-related parameters:");
                foreach (Parameter p in hanger.Parameters)
                {
                    string n = p.Definition?.Name ?? "";
                    if (n.IndexOf("size", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("diam", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("rod",  StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("host", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string v = "";
                    try
                    {
                        v = p.StorageType switch
                        {
                            StorageType.Double      => p.AsDouble().ToString("F4"),
                            StorageType.Integer     => p.AsInteger().ToString(),
                            StorageType.String      => p.AsString() ?? "",
                            StorageType.ElementId   => p.AsElementId().Value.ToString(),
                            _                       => "?"
                        };
                    }
                    catch { v = "(read fail)"; }
                    sw.WriteLine($"  {n} [{p.StorageType}, ro={p.IsReadOnly}] = {v}");
                }

                // GetHostedInfo if available
                try
                {
                    var m = typeof(FabricationPart).GetMethod("GetHostedInfo", Type.EmptyTypes);
                    if (m != null)
                    {
                        var info = m.Invoke(hanger, null);
                        if (info != null)
                        {
                            sw.WriteLine($"GetHostedInfo() returned {info.GetType().Name}");
                            foreach (var pi in info.GetType().GetProperties())
                            {
                                try
                                {
                                    var v = pi.GetValue(info);
                                    sw.WriteLine($"  {pi.Name} = {v}");
                                }
                                catch { }
                            }
                        }
                        else sw.WriteLine("GetHostedInfo() returned null");
                    }
                }
                catch (Exception ex) { sw.WriteLine($"GetHostedInfo failed: {ex.Message}"); }
            }
            catch (Exception ex) { outcome.Notes.Add($"[diag-hanger-fail] {ex.Message}"); }
        }

        // ────────────────────────────────────────────────────────────────────────
        // End classification: anchored?, setback, reason
        // ────────────────────────────────────────────────────────────────────────

        private struct EndAnchor
        {
            public bool   Anchored;
            public double SetbackInches;
        }

        private static EndAnchor ClassifyEnd(
            Document doc, FabricationPart part, Connector conn,
            SupportSpecRow row, SupportSpec spec, bool? endIsFarFromStart)
        {
            FabricationPart? neighbor = FindNeighbor(doc, part, conn);
            if (neighbor == null)
            {
                // Open end (nothing connected) — treat as a terminator that
                // always wants a hanger at the fitting setback.
                return new EndAnchor
                {
                    Anchored      = true,
                    SetbackInches = row.FittingDistanceInches
                };
            }

            // In fab content, pipes/ducts connect to fittings THROUGH a short
            // in-line joint piece:
            //    Pipe → Weld → Elbow → Weld → Pipe       (pipe side)
            //    Duct → TDC  → Elbow → TDC  → Duct        (duct side — drive-cleat / slip / etc)
            // Pipe-side joints (weld/flange/coupling) are PCF-classifiable by
            // name via IsJointPart. Duct-side joints are NOT — they have no
            // canonical PCF type. So we add a STRUCTURAL test: a short (<12")
            // in-line 2-connector neighbor is treated as a joint regardless of
            // name. This catches duct TDC / slip / drive-cleat joints and any
            // unusual pipe-side joint families that aren't in the name list.
            bool immediateIsJointByName = IsJointPart(neighbor);

            // "In-line" = a straight pipe OR a CID-confirmed straight duct.
            // PCF.IsStraightPipe works on pipe geometry; for ducts we use the
            // explicit StraightDuctCids set (the PCF domain doesn't cover ducts).
            bool neighborIsInline = false;
            try
            {
                neighborIsInline =
                    PartTypeClassifier.IsStraightPipe(neighbor) ||
                    PartTypeClassifier.IsStraightDuctByCid(neighbor);
            }
            catch { }

            double neighborLenIn = 0;
            try
            {
                var nc = ConnectorHelper.GetPhysicalConnectors(neighbor);
                if (nc.Count == 2)
                    neighborLenIn = nc[0].Origin.DistanceTo(nc[1].Origin) * 12.0;
            }
            catch { }
            // Two flavors of "joint":
            //   (1) A short in-line connector piece (weld ~0", flange ~1",
            //       coupling ~2"). These EXIST as a separate FabricationPart
            //       and must be WALKED PAST to find the real fitting beyond.
            //   (2) A direct end-to-end interface between two straight sections
            //       with no separate joint piece (common for fab ducts — two
            //       duct sections meet face-to-face). No walking; the interface
            //       itself IS the joint, and the neighbor is just the next
            //       independent straight segment.
            bool neighborIsShort = neighborLenIn > 0 && neighborLenIn < 12.0;

            bool immediateIsJoint =
                immediateIsJointByName ||  // PCF-classified pipe joint
                neighborIsInline;          // any in-line straight = at-a-joint interface

            // Only walk past SHORT joint pieces. Long straight neighbors are
            // their own pipe segments, not joint connectors — don't skip them.
            FabricationPart? beyond =
                (immediateIsJoint && (immediateIsJointByName || neighborIsShort))
                    ? FindNeighborAcross(doc, part, neighbor)
                    : null;
            FabricationPart? effective = beyond ?? neighbor;

            bool effectiveIsJoint = IsJointPart(effective);
            bool effectiveIsStraight = false;
            try { effectiveIsStraight = PartTypeClassifier.IsStraightPipe(effective); }
            catch { }

            // True "straight joint" = joint between two straight pipes/ducts.
            // Apply the StraightJoints mode only in that case.
            bool isStraightJoint = immediateIsJoint &&
                (effectiveIsStraight || (effectiveIsJoint && effective.Id == neighbor.Id));

            if (isStraightJoint)
            {
                bool anchored = ShouldAnchor(spec.StraightJoints, endIsFarFromStart);
                return new EndAnchor
                {
                    Anchored = anchored,
                    SetbackInches = anchored ? row.DistanceFromJointInches : 0
                };
            }

            // Otherwise the effective neighbor is a direction-change fitting
            // (elbow / tee / valve / cross / reducer / cap / olet) — apply the
            // SupportPositions mode and place the hanger 4" inside this pipe
            // from the joint/fitting boundary.
            bool anchoredF = ShouldAnchor(spec.SupportPositions, endIsFarFromStart);
            return new EndAnchor
            {
                Anchored = anchoredF,
                SetbackInches = anchoredF ? row.FittingDistanceInches : 0
            };
        }

        private static string DescribeEnd(Document doc, FabricationPart part, Connector conn)
        {
            var n = FindNeighbor(doc, part, conn);
            if (n == null) return "open";

            // Mirror ClassifyEnd's joint detection: name-based OR any in-line
            // neighbor (which covers both short joint pieces AND direct
            // straight-to-straight interfaces).
            bool isJointByName = IsJointPart(n);
            bool nIsInline = false;
            try
            {
                nIsInline =
                    PartTypeClassifier.IsStraightPipe(n) ||
                    PartTypeClassifier.IsStraightDuctByCid(n);
            }
            catch { }
            bool isJoint = isJointByName || nIsInline;

            if (!isJoint) return "fitting";

            double nLenIn = 0;
            try
            {
                var nc = ConnectorHelper.GetPhysicalConnectors(n);
                if (nc.Count == 2)
                    nLenIn = nc[0].Origin.DistanceTo(nc[1].Origin) * 12.0;
            }
            catch { }
            bool nIsShort = nLenIn > 0 && nLenIn < 12.0;
            if (!isJointByName && !nIsShort) return "joint→straight";  // direct duct-to-duct

            var beyond = FindNeighborAcross(doc, part, n);
            if (beyond == null) return "joint→open";
            bool beyondStraight = false;
            try { beyondStraight = PartTypeClassifier.IsStraightPipe(beyond); } catch { }
            if (beyondStraight) return "joint→straight";
            return "joint→fitting";
        }

        /// <summary>
        /// Walks across <paramref name="through"/> (typically a weld/coupling/
        /// flange) to find the neighbor on its OTHER side, given that
        /// <paramref name="source"/> is on one side. Used to peek past welds
        /// so a pipe's "real" neighbor is the fitting beyond the weld.
        /// </summary>
        private static FabricationPart? FindNeighborAcross(
            Document doc, FabricationPart source, FabricationPart through)
        {
            try
            {
                var conns = ConnectorHelper.GetPhysicalConnectors(through);
                foreach (var c in conns)
                {
                    bool connected = false;
                    try { connected = c.IsConnected; } catch { }
                    if (!connected) continue;
                    foreach (Connector other in c.AllRefs)
                    {
                        if (other?.Owner is FabricationPart fp &&
                            fp.Id != source.Id && fp.Id != through.Id)
                            return fp;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Whether this end of the pipe gets a setback hanger when its
        /// (effective) neighbor is a direction change. Modes:
        ///   • NotAtChange         — never anchor.
        ///   • BeforeAndAfterChange — always anchor.
        ///   • BeforeChange         — anchor only on the FAR end relative to
        ///                            the user-picked Starting Node (this is
        ///                            the side approaching the next fitting).
        ///   • AfterChange          — anchor only on the NEAR end (the side
        ///                            leaving the previous fitting).
        /// Without a Starting Node, endIsFar is null and Before/After fall
        /// back to anchoring both sides (symmetric).
        /// </summary>
        private static bool ShouldAnchor(SupportPositionMode mode, bool? endIsFar)
        {
            if (mode == SupportPositionMode.NotAtChange) return false;
            if (mode == SupportPositionMode.BeforeAndAfterChange) return true;
            if (endIsFar == null) return true; // no flow direction → symmetric
            return mode switch
            {
                SupportPositionMode.BeforeChange => endIsFar.Value,
                SupportPositionMode.AfterChange  => !endIsFar.Value,
                _ => true
            };
        }

        private static bool ShouldAnchor(StraightJointMode mode, bool? endIsFar)
        {
            if (mode == StraightJointMode.NotAtJoint) return false;
            if (mode == StraightJointMode.BeforeAndAfterJoint) return true;
            if (endIsFar == null) return true;
            return mode switch
            {
                StraightJointMode.BeforeJoint => endIsFar.Value,
                StraightJointMode.AfterJoint  => !endIsFar.Value,
                _ => true
            };
        }

        // ────────────────────────────────────────────────────────────────────────
        // Position list construction
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Walk start-to-end and drop any candidate position whose distance
        /// from the previous *kept* position is less than
        /// <paramref name="minSpacingFt"/>. The earlier (start-side) position
        /// wins, which matches the user-facing rule: the leftmost hanger is
        /// already placed correctly per spec; the later candidate (typically
        /// a fitting / joint setback that crowds the rhythm) gets skipped.
        /// </summary>
        private static List<double> ApplyMinSpacing(
            List<double> positionsFt, double minSpacingFt, Outcome outcome)
        {
            if (positionsFt == null || positionsFt.Count <= 1 || minSpacingFt <= 0)
                return positionsFt ?? new List<double>();
            var sorted = positionsFt.OrderBy(p => p).ToList();
            var kept = new List<double>(sorted.Count) { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] - kept[kept.Count - 1] >= minSpacingFt - 1e-6)
                    kept.Add(sorted[i]);
                else
                    outcome.SkippedTooClose++;
            }
            return kept;
        }

        private static List<double> BuildPositions(
            double length, double freeStartFt, double freeEndFt, double spacingFt,
            bool leftAnchored, bool rightAnchored, double epsFt)
        {
            var positions = new List<double>();
            double leftBound  = leftAnchored  ? freeStartFt : 0;
            double rightBound = rightAnchored ? freeEndFt   : length;

            if (rightBound - leftBound < epsFt) return positions;
            if (spacingFt <= 0) return positions;

            // Spacing intervals are measured HANGER-TO-HANGER, not from a
            // fitting face. Each consecutive pair of hangers sits exactly
            // spacingFt apart; the anchored end-position takes a setback from
            // the adjacent fitting (FittingDistance / DistanceFromJoint),
            // and that's the only place where setback enters the math.
            //
            // Half-spacing exclusion zones around UN-anchored ends prevent a
            // hanger from landing right at the side where the mode says
            // "no setback".
            double halfSpacing = spacingFt * 0.5;

            if (leftAnchored && rightAnchored)
            {
                // Both ends anchored. First hanger at the left setback, step
                // INWARD by spacingFt, and add the right setback if the gap
                // from the last stepped position is non-trivial. The first
                // stepped hanger sits at leftBound + spacingFt — i.e. the
                // gap between hangers 0 and 1 equals spacingFt exactly.
                positions.Add(leftBound);
                double pos = leftBound + spacingFt;
                while (pos < rightBound - epsFt)
                {
                    positions.Add(pos);
                    pos += spacingFt;
                }
                if (rightBound - positions[^1] > epsFt)
                    positions.Add(rightBound);
                return positions;
            }

            if (!leftAnchored && rightAnchored)
            {
                // c0 = NEAR/after-side (un-anchored), c1 = FAR (anchored).
                // Anchor sits at rightBound; step BACKWARD by spacingFt.
                // Skip steps that fall within halfSpacing of the un-anchored
                // end (c0 = position 0).
                positions.Add(rightBound);
                double pos = rightBound - spacingFt;
                while (pos >= halfSpacing - epsFt)
                {
                    positions.Add(pos);
                    pos -= spacingFt;
                }
                positions.Sort();
                return positions;
            }

            if (leftAnchored && !rightAnchored)
            {
                // c0 = FAR (anchored), c1 = NEAR/after-side (un-anchored).
                // Anchor sits at leftBound; step FORWARD by spacingFt. Skip
                // steps that fall within halfSpacing of the un-anchored end
                // (c1 = position length).
                positions.Add(leftBound);
                double pos = leftBound + spacingFt;
                while (pos <= length - halfSpacing + epsFt)
                {
                    positions.Add(pos);
                    pos += spacingFt;
                }
                return positions;
            }

            // Neither anchored — spacingFt intervals across the full pipe.
            // No reference fitting on either side, so we start a half-step
            // in from c0 and step forward; this gives roughly symmetric
            // distribution for typical lengths.
            {
                double pos = spacingFt;
                while (pos < length - epsFt)
                {
                    positions.Add(pos);
                    pos += spacingFt;
                }
                return positions;
            }
        }

        private static FabricationPart? FindNeighbor(Document doc, FabricationPart self, Connector conn)
        {
            try
            {
                if (!conn.IsConnected) return null;
                foreach (Connector other in conn.AllRefs)
                {
                    if (other == null) continue;
                    if (other.Owner == null) continue;
                    if (other.Owner.Id == self.Id) continue;
                    if (other.Owner is FabricationPart fp) return fp;
                }
            }
            catch { }
            return null;
        }

        // PIPE-SIDE joint detection only. PCF is a pipe-domain spec and has no
        // notion of duct joints (TDC / slip / drive-cleat / Pittsburgh / S-cleat),
        // so this returns false for every duct part. Duct joints are caught by
        // the structural test in ClassifyEnd (in-line + <12" length).
        private static bool IsJointPart(FabricationPart part)
        {
            try
            {
                string pcfType = PartTypeClassifier.GetPcfType(part);
                if (pcfType == "FLANGE" || pcfType == "COUPLING" || pcfType == "WELD")
                    return true;

                string text = ((part.Alias ?? "") + " " +
                               (part.LookupParameter("Long Description")?.AsString() ?? "")
                              ).ToUpperInvariant();
                if (text.Contains("UNION")) return true;
            }
            catch { }
            return false;
        }

        // ────────────────────────────────────────────────────────────────────────
        // Size lookup
        // ────────────────────────────────────────────────────────────────────────

        private static double ResolveSizeInches(Connector c0, Connector c1)
        {
            double s0 = ConnectorSizeInches(c0);
            double s1 = ConnectorSizeInches(c1);
            return Math.Max(s0, s1);
        }

        private static double ConnectorSizeInches(Connector c)
        {
            try
            {
                if (c.Shape == ConnectorProfileType.Round)
                    return c.Radius * 2.0 * 12.0;
                // Rectangular / Oval — use larger of width/height
                double w = c.Width;
                double h = c.Height;
                return Math.Max(w, h) * 12.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private static double InchesToFeet(double inches) => inches / 12.0;

        // ────────────────────────────────────────────────────────────────────────
        // Hanger button resolution
        // ────────────────────────────────────────────────────────────────────────

        private static (FabricationServiceButton? button, int condition, string note) ResolveHangerButton(
            Document doc, string serviceName, string? overrideKey, double sizeInches,
            ConnectorProfileType hostShape)
        {
            try
            {
                var config = FabricationConfiguration.GetFabricationConfiguration(doc);
                if (config == null) return (null, 0, "no fabrication configuration");

                FabricationService? service = ResolveServiceByName(config, serviceName);
                if (service == null) return (null, 0, "service not found in loaded services");

                // Override path: match "GroupName|ButtonName" exactly. The user's
                // explicit choice bypasses the shape filter — if they want a
                // specific button, they get it regardless of profile.
                if (!string.IsNullOrWhiteSpace(overrideKey))
                {
                    var (ovBtn, ovCond) = FindOverrideButton(service, overrideKey!, sizeInches);
                    if (ovBtn != null) return (ovBtn, ovCond, "override");
                }

                // Default path: first non-excluded hanger compatible with the
                // host's connector shape.
                // The "X" marks in the Revit fab UI come from per-service exclusion
                // (FabricationService.IsButtonExcluded), not from the global
                // FabricationServiceButton.IsExcluded. Check both.
                int hangersSeen = 0, hangersExcluded = 0, hangersWrongShape = 0;
                int palettesExcluded = 0, palettesSeen = 0;
                for (int pi = 0; pi < service.PaletteCount; pi++)
                {
                    palettesSeen++;
                    bool excl = false;
                    try { excl = service.IsPaletteExcluded(pi); } catch { }
                    if (excl) { palettesExcluded++; continue; }

                    for (int bi = 0; bi < service.GetButtonCount(pi); bi++)
                    {
                        var btn = service.GetButton(pi, bi);
                        if (btn == null) continue;

                        bool isHanger = false;
                        try { isHanger = btn.IsAHanger; } catch { }
                        if (!isHanger) continue;
                        hangersSeen++;

                        if (IsButtonExcludedForService(service, pi, bi, btn))
                        { hangersExcluded++; continue; }

                        if (!IsButtonShapeCompatible(btn, hostShape))
                        { hangersWrongShape++; continue; }

                        int cond = FindHangerConditionForSize(btn, sizeInches);
                        return (btn, Math.Max(0, cond), "default");
                    }
                }

                string reason;
                if (hangersSeen == 0)
                {
                    reason = $"no hanger buttons in service ({palettesExcluded}/{palettesSeen} palettes excluded)";
                }
                else if (hangersWrongShape > 0 && hangersWrongShape + hangersExcluded == hangersSeen)
                {
                    reason = $"all {hangersSeen} hanger buttons rejected " +
                             $"({hangersExcluded} excluded, {hangersWrongShape} wrong shape for {hostShape})";
                }
                else
                {
                    reason = $"all {hangersSeen} hanger buttons in service are excluded";
                }
                return (null, 0, reason);
            }
            catch (Exception ex) { return (null, 0, $"resolve threw: {ex.Message}"); }
        }

        /// <summary>
        /// Returns true if <paramref name="btn"/> is compatible with a host
        /// part of the given connector profile, based on a name-keyword test.
        /// See <see cref="RectOnlyHangerKeywords"/> for the rule and how to
        /// extend it.
        /// </summary>
        private static bool IsButtonShapeCompatible(
            FabricationServiceButton btn, ConnectorProfileType hostShape)
        {
            // Only Round and Rectangular hosts get filtered. Oval / Invalid /
            // anything else falls through so we don't accidentally block a
            // valid placement on uncommon content.
            if (hostShape != ConnectorProfileType.Round &&
                hostShape != ConnectorProfileType.Rectangular)
                return true;

            string name = string.Empty;
            try { name = (btn.Name ?? string.Empty).ToUpperInvariant(); } catch { }
            if (string.IsNullOrWhiteSpace(name)) return true;  // unnameable → don't filter

            // Step 1: explicit-round override beats rect form-factor words.
            // e.g. "Trapeze Hanger Round" should match Round hosts even
            // though TRAPEZE is in the rect-only list.
            bool isExplicitRound = false;
            foreach (var kw in RoundExplicitHangerKeywords)
            {
                if (name.Contains(kw)) { isExplicitRound = true; break; }
            }
            if (isExplicitRound)
                return hostShape == ConnectorProfileType.Round;

            // Step 2 + 3: rect-only keyword check, with default-to-round.
            bool hasRectKeyword = false;
            foreach (var kw in RectOnlyHangerKeywords)
            {
                if (name.Contains(kw)) { hasRectKeyword = true; break; }
            }

            return hostShape == ConnectorProfileType.Rectangular
                ? hasRectKeyword            // rect host → need rect-specific
                : !hasRectKeyword;          // round host (+ pipes) → reject rect-only
        }

        /// <summary>
        /// Determines whether a hanger button should be skipped because the user
        /// has marked it "excluded" in the fab palette (the X overlay in the UI).
        /// The exact API call differs across Revit versions so we probe several
        /// likely names via reflection on both FabricationService (taking palette
        /// + button indices) and FabricationServiceButton (no args), and treat
        /// any positive result as "exclude this button".
        /// </summary>
        private static bool IsButtonExcludedForService(
            FabricationService service, int pi, int bi, FabricationServiceButton btn)
        {
            // service-level (int, int) probes
            string[] svcMethodNames = {
                "IsButtonExcluded", "IsButtonHidden", "IsButtonOmitted",
                "IsButtonDisabled", "GetButtonExcluded"
            };
            foreach (var name in svcMethodNames)
            {
                try
                {
                    var m = typeof(FabricationService).GetMethod(
                        name, new[] { typeof(int), typeof(int) });
                    if (m != null)
                    {
                        var result = m.Invoke(service, new object[] { pi, bi });
                        if (result is bool b && b) return true;
                    }
                }
                catch { }
            }

            // button-level no-arg probes
            string[] btnMethodNames = {
                "IsExcluded", "IsHidden", "IsOmitted", "IsDisabled"
            };
            foreach (var name in btnMethodNames)
            {
                try
                {
                    var m = typeof(FabricationServiceButton).GetMethod(name, Type.EmptyTypes);
                    if (m != null)
                    {
                        var result = m.Invoke(btn, null);
                        if (result is bool b && b) return true;
                    }
                }
                catch { }
            }

            // button-level no-arg properties
            string[] btnPropNames = { "IsExcluded", "IsHidden", "IsOmitted", "IsDisabled" };
            foreach (var name in btnPropNames)
            {
                try
                {
                    var p = typeof(FabricationServiceButton).GetProperty(name);
                    if (p != null && p.PropertyType == typeof(bool))
                    {
                        var result = p.GetValue(btn);
                        if (result is bool b && b) return true;
                    }
                }
                catch { }
            }

            return false;
        }

        /// <summary>
        /// Match a FabricationService by name. FabricationPart.ServiceName sometimes
        /// stores a SHORT label ("Process Chilled Water Supply") while
        /// FabricationService.Name in the loaded config is the FULL label
        /// ("ADSK - Hydronic: Process Chilled Water Supply"). Try exact, then either
        /// containment direction.
        /// </summary>
        private static FabricationService? ResolveServiceByName(
            FabricationConfiguration config, string partServiceName)
        {
            if (string.IsNullOrWhiteSpace(partServiceName)) return null;
            var loaded = config.GetAllLoadedServices();
            foreach (var s in loaded)
                if (string.Equals(s.Name, partServiceName, StringComparison.OrdinalIgnoreCase))
                    return s;
            foreach (var s in loaded)
                if (s.Name != null &&
                    s.Name.IndexOf(partServiceName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s;
            foreach (var s in loaded)
                if (s.Name != null &&
                    partServiceName.IndexOf(s.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s;
            return null;
        }

        private static (FabricationServiceButton? button, int cond) FindOverrideButton(
            FabricationService service, string key, double sizeInches)
        {
            int sep = key.IndexOf('|');
            string targetGroup  = sep > 0 ? key.Substring(0, sep) : string.Empty;
            string targetButton = sep > 0 ? key.Substring(sep + 1) : key;

            for (int pi = 0; pi < service.PaletteCount; pi++)
            {
                string groupName = SafeGetPaletteName(service, pi);
                if (!string.IsNullOrEmpty(targetGroup) &&
                    !string.Equals(groupName, targetGroup, StringComparison.OrdinalIgnoreCase))
                    continue;

                for (int bi = 0; bi < service.GetButtonCount(pi); bi++)
                {
                    var btn = service.GetButton(pi, bi);
                    if (btn == null) continue;
                    if (!string.Equals(btn.Name, targetButton, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int cond = FindHangerConditionForSize(btn, sizeInches);
                    return (btn, Math.Max(0, cond));
                }
            }
            return (null, 0);
        }

        private static string SafeGetPaletteName(FabricationService service, int pi)
        {
            try { return service.GetPaletteName(pi) ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Picks a condition index whose name encodes the host's size, falling back to 0.
        /// Loose match: condition name contains the size in inches (e.g. "2", "2 in", "2\"").
        /// </summary>
        private static int FindHangerConditionForSize(FabricationServiceButton btn, double sizeInches)
        {
            try
            {
                int count = btn.ConditionCount;
                if (count <= 0) return 0;

                string[] tokens =
                {
                    sizeInches.ToString("0.##"),
                    sizeInches.ToString("0.##") + "\"",
                    sizeInches.ToString("0.##") + " in",
                    ((int)Math.Round(sizeInches)).ToString()
                };

                for (int ci = 0; ci < count; ci++)
                {
                    string name = btn.GetConditionName(ci) ?? string.Empty;
                    foreach (var t in tokens)
                        if (name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                            return ci;
                }
            }
            catch { }
            return 0;
        }

        // ────────────────────────────────────────────────────────────────────────
        // Creation + anchor resolution
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a hanger that is hosted on the given fabrication part at the
        /// specified distance (feet) along the host axis from <paramref name="hostConn"/>.
        /// Internally translates the distance so it's measured from the host's
        /// LocationCurve START, since Revit's CreateHanger appears to use the
        /// curve direction (set when the pipe was drawn) rather than the supplied
        /// hostConnector. Without this normalization, identical offsets land on
        /// opposite ends of pipes that happen to be drawn in opposite directions.
        /// </summary>
        private static FabricationPart? CreateHangerOnHost(
            Document doc, FabricationServiceButton btn, int condition,
            FabricationPart host, Connector hostConn, double distanceFt,
            bool attachToStructure)
        {
            // Find the connector at the curve START so distance is measured
            // consistently regardless of which physical end is c0.
            Connector refConn = hostConn;
            double refDist = distanceFt;
            try
            {
                if (host.Location is LocationCurve lc && lc.Curve != null)
                {
                    XYZ curveStart = lc.Curve.GetEndPoint(0);
                    double curveLen = lc.Curve.Length;
                    var ends = ConnectorHelper.GetPhysicalConnectors(host)
                        .Where(c => c.ConnectorType == ConnectorType.End).ToList();
                    Connector? startConn = null;
                    foreach (var c in ends)
                    {
                        if (c.Origin.IsAlmostEqualTo(curveStart, 1e-3))
                        { startConn = c; break; }
                    }
                    if (startConn != null && ends.Count == 2)
                    {
                        // distanceFt was measured from hostConn going inward.
                        // If hostConn is at the curve start, use distance as-is.
                        // Else hostConn is at the curve END, so the equivalent
                        // distance from curveStart is (curveLen - distanceFt).
                        //
                        // CRITICAL: compare by Origin position, NOT ReferenceEquals.
                        // hostConn and startConn came from separate connector-set
                        // iterations and the wrapper objects are never reference-
                        // equal even when they point to the same physical connector.
                        bool hostIsCurveStart =
                            hostConn.Origin.IsAlmostEqualTo(curveStart, 1e-3);
                        if (hostIsCurveStart)
                        {
                            refConn = startConn;
                            refDist = distanceFt;
                        }
                        else
                        {
                            refConn = startConn;
                            refDist = curveLen - distanceFt;
                        }
                    }
                }
            }
            catch { /* fall back to original hostConn / distance */ }


            // Preferred: (Document, button, int condition, ElementId hostId,
            //             Connector hostConnector, double distance, bool attachToStructure)
            var m1 = typeof(FabricationPart).GetMethod("CreateHanger", new[] {
                typeof(Document), typeof(FabricationServiceButton),
                typeof(int), typeof(ElementId), typeof(Connector),
                typeof(double), typeof(bool) });
            if (m1 != null)
                return InvokeUnwrapped<FabricationPart?>(m1, new object[] {
                    doc, btn, condition, host.Id, refConn, refDist, attachToStructure });

            // Variant without condition (some catalogs wire the size differently)
            var m2 = typeof(FabricationPart).GetMethod("CreateHanger", new[] {
                typeof(Document), typeof(FabricationServiceButton),
                typeof(ElementId), typeof(Connector),
                typeof(double), typeof(bool) });
            if (m2 != null)
                return InvokeUnwrapped<FabricationPart?>(m2, new object[] {
                    doc, btn, host.Id, refConn, refDist, attachToStructure });

            throw new InvalidOperationException(
                "Hosted FabricationPart.CreateHanger overload not found in this Revit API.");
        }

        /// <summary>Invoke a static MethodInfo and unwrap TargetInvocationException so callers see the real Revit error.</summary>
        private static T InvokeUnwrapped<T>(System.Reflection.MethodInfo m, object[] args)
        {
            try { return (T)m.Invoke(null, args)!; }
            catch (System.Reflection.TargetInvocationException tie)
                when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        /// <summary>
        /// The saddle axis of a hanger — the horizontal direction along which the
        /// host pipe passes through the clamp. Found by walking pairs of the
        /// hanger's connectors looking for two whose Z directions are anti-parallel
        /// AND whose origin displacement is roughly horizontal (the rod-top connector
        /// has a vertical displacement, so it's filtered out).
        /// </summary>
        private static XYZ? FindHangerSaddleAxis(FabricationPart hanger)
        {
            List<Connector> conns;
            try { conns = ConnectorHelper.GetPhysicalConnectors(hanger); }
            catch { return null; }
            if (conns.Count < 2) return null;

            for (int i = 0; i < conns.Count; i++)
            {
                for (int j = i + 1; j < conns.Count; j++)
                {
                    XYZ d0, d1, disp;
                    try
                    {
                        d0 = conns[i].CoordinateSystem.BasisZ;
                        d1 = conns[j].CoordinateSystem.BasisZ;
                        disp = conns[j].Origin - conns[i].Origin;
                    }
                    catch { continue; }

                    if (d0.DotProduct(d1) > -0.95) continue; // not anti-parallel
                    double len = disp.GetLength();
                    if (len < 1e-6) continue;
                    if (Math.Abs(disp.Z) > 0.5 * len) continue; // displacement isn't horizontal
                    return disp.Normalize();
                }
            }

            // Fallback: use BasisZ of the connector whose Z is most horizontal
            Connector? best = null;
            double bestHoriz = 0;
            foreach (var c in conns)
            {
                try
                {
                    var bz = c.CoordinateSystem.BasisZ;
                    double horiz = Math.Sqrt(bz.X * bz.X + bz.Y * bz.Y);
                    if (horiz > bestHoriz) { bestHoriz = horiz; best = c; }
                }
                catch { }
            }
            if (best == null) return null;
            var bestZ = best.CoordinateSystem.BasisZ;
            return new XYZ(bestZ.X, bestZ.Y, 0).Normalize();
        }

        /// <summary>
        /// The point on the hanger that should land on the host pipe centerline.
        /// Preference order:
        ///   1. A hanger connector that's already linked to the host (IsConnected).
        ///   2. The hanger connector closest to the host axis (the saddle/clamp that wraps the pipe).
        ///   3. null — caller falls back to host midpoint.
        /// </summary>
        private static XYZ? FindHangerHostAnchor(FabricationPart hanger, XYZ pA, XYZ pB)
        {
            List<Connector> conns;
            try { conns = ConnectorHelper.GetPhysicalConnectors(hanger); }
            catch { return null; }
            if (conns.Count == 0) return null;

            // Pass 1: a connector connected to anything (the host attachment)
            foreach (var c in conns)
            {
                try { if (c.IsConnected) return c.Origin; }
                catch { }
            }

            // Pass 2: connector whose origin is closest to the host axis line
            // (the saddle on a hanger sits on the host centerline)
            XYZ axis = (pB - pA);
            double axisLen = axis.GetLength();
            if (axisLen < 1e-9) return conns[0].Origin;
            XYZ axisUnit = axis / axisLen;

            Connector? best = null;
            double bestDist = double.MaxValue;
            foreach (var c in conns)
            {
                XYZ p   = c.Origin;
                XYZ rel = p - pA;
                double tAlong = rel.DotProduct(axisUnit);
                XYZ proj = pA + axisUnit * tAlong;
                double d = p.DistanceTo(proj);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best?.Origin;
        }

        // ────────────────────────────────────────────────────────────────────────
        // Chain walking + chain-spanning placement
        // ────────────────────────────────────────────────────────────────────────

        /// <summary>One segment of a logical run (a single straight FabricationPart).
        /// LeftConn is the connector facing toward chain index 0 (the chain's
        /// outer-left end); RightConn faces toward chain[Count-1].</summary>
        private class ChainSegment
        {
            public FabricationPart Part = null!;
            public Connector LeftConn  = null!;
            public Connector RightConn = null!;
            public double LengthFt;
        }

        /// <summary>A logical run = ordered list of straight segments joined
        /// by joint-only interfaces (joint pieces walked past via
        /// FindNeighborAcross, or direct duct-to-duct).
        /// JointGapsFt[i] is the PHYSICAL gap between Segments[i]'s RightConn
        /// and Segments[i+1]'s LeftConn — typically the length of the joint
        /// piece (TDC ≈ 0.375", flange ≈ 1") or 0 for direct duct-to-duct.
        /// TotalLengthFt INCLUDES these gaps so chain coordinates match
        /// physical-space distance — hangers stepped at spacingFt intervals
        /// in chain coordinates end up spacingFt apart in physical space.</summary>
        private class ChainInfo
        {
            public List<ChainSegment> Segments = new();
            public List<double> JointGapsFt = new();
            public double TotalLengthFt;
        }

        /// <summary>Build the full chain that <paramref name="seed"/> belongs to,
        /// walking both directions through joint→straight neighbours. The chain
        /// is truncated at any segment NOT in <paramref name="selectionIds"/>
        /// so we don't place hangers on parts the user didn't select.
        /// Returns a single-segment ChainInfo if the seed has no chainable
        /// neighbours in the selection.
        ///
        /// If <paramref name="flowMap"/> is provided AND the chain's
        /// leftmost-by-walk end is actually the FAR side of the user's
        /// Start Node, the chain is reversed so Segments[0] is the NEAR
        /// (start-side) end. This keeps the algorithm's "step from the left"
        /// behaviour aligned with the user's start-of-run intent — and lands
        /// any sub-spacing remainder at the FAR end, not the near end.</summary>
        /// <summary>Flip chain end-for-end: segment order, joint-gap order,
        /// and per-segment LeftConn/RightConn swap. Pure mechanical reorientation.</summary>
        private static void ReverseChainInPlace(ChainInfo info)
        {
            info.Segments.Reverse();
            info.JointGapsFt.Reverse();
            foreach (var seg in info.Segments)
            {
                var tmp = seg.LeftConn;
                seg.LeftConn = seg.RightConn;
                seg.RightConn = tmp;
            }
        }

        private static ChainInfo BuildChainInfo(
            Document doc, FabricationPart seed, HashSet<long> selectionIds,
            HangerFlowMap? flowMap = null,
            bool useMechEqAsStart = false,
            Outcome? outcome = null)
        {
            var info = new ChainInfo();

            var seedAll = ConnectorHelper.GetPhysicalConnectors(seed);
            var seedEnds = seedAll.Where(c => c.ConnectorType == ConnectorType.End).ToList();
            var seedConns = seedEnds.Count == 2 ? seedEnds : seedAll;
            if (seedConns.Count != 2) return info;
            var seedC0 = seedConns[0];
            var seedC1 = seedConns[1];
            double seedLen = seedC0.Origin.DistanceTo(seedC1.Origin);

            // Walk c0 direction (will be prepended). Each walked segment's
            // RightConn faces back toward the seed.
            var leftWalk = new List<ChainSegment>();
            WalkChainOneWay(doc, seed, seedC0, isLeftWalk: true, selectionIds, leftWalk);

            // Walk c1 direction (appended). Each segment's LeftConn faces
            // back toward the seed.
            var rightWalk = new List<ChainSegment>();
            WalkChainOneWay(doc, seed, seedC1, isLeftWalk: false, selectionIds, rightWalk);

            // Assemble: leftWalk reversed, then seed, then rightWalk
            for (int i = leftWalk.Count - 1; i >= 0; i--) info.Segments.Add(leftWalk[i]);
            info.Segments.Add(new ChainSegment {
                Part = seed, LeftConn = seedC0, RightConn = seedC1, LengthFt = seedLen
            });
            info.Segments.AddRange(rightWalk);

            // Joint-piece gap between each consecutive pair. Origin distance
            // between segment[i]'s RightConn and segment[i+1]'s LeftConn
            // captures both joint-piece length (~0.375" TDC, ~1" flange) and
            // direct duct-to-duct (~0).
            for (int i = 0; i < info.Segments.Count - 1; i++)
            {
                double gap = info.Segments[i].RightConn.Origin
                    .DistanceTo(info.Segments[i + 1].LeftConn.Origin);
                info.JointGapsFt.Add(gap);
            }
            info.TotalLengthFt = info.Segments.Sum(s => s.LengthFt) + info.JointGapsFt.Sum();

            // Orient. Three sources of truth, in priority order:
            //   1. User-picked Start Node (flow map)  — highest priority
            //   2. Mechanical Equipment heuristic     — when enabled and Start Node didn't fire
            //   3. Auto                               — fallback; chain stays as-walked
            //
            // BuildPositions steps left-to-right and any sub-spacing
            // remainder lands at the right (= far end), so getting the
            // start side at Segments[0] matters for ergonomics.
            bool oriented = false;
            if (flowMap != null && info.Segments.Count > 1)
            {
                var firstSeg = info.Segments[0];
                if (flowMap.IsKnown(firstSeg.Part.Id))
                {
                    oriented = true;
                    if (flowMap.IsFarEnd(firstSeg.Part.Id, firstSeg.LeftConn))
                        ReverseChainInPlace(info);
                    if (outcome != null) outcome.ChainsOrientedByStartNode++;
                }
            }
            if (!oriented && useMechEqAsStart && info.Segments.Count > 1)
            {
                var chainPartIds = new HashSet<long>(info.Segments.Select(s => s.Part.Id.Value));
                int leftHops  = HangerFlowMap.HopsToMechanicalEquipment(
                    info.Segments[0].LeftConn, chainPartIds);
                int rightHops = HangerFlowMap.HopsToMechanicalEquipment(
                    info.Segments[info.Segments.Count - 1].RightConn, chainPartIds);
                bool leftFound  = leftHops  >= 0;
                bool rightFound = rightHops >= 0;
                if (leftFound || rightFound)
                {
                    oriented = true;
                    // Prefer the side that reaches Mech Eq in fewer hops.
                    // Ties between two equal-distance Mech Eq instances fall
                    // through to auto — better than silently picking one.
                    if (leftFound && (!rightFound || leftHops < rightHops))
                    {
                        // Mech Eq is on the left — already at Segments[0]; no flip.
                        if (outcome != null) outcome.ChainsOrientedByMechEq++;
                    }
                    else if (rightFound && (!leftFound || rightHops < leftHops))
                    {
                        ReverseChainInPlace(info);
                        if (outcome != null) outcome.ChainsOrientedByMechEq++;
                    }
                    else
                    {
                        // Tie — fall back to auto, don't claim Mech Eq.
                        oriented = false;
                    }
                }
            }
            if (!oriented && info.Segments.Count > 1 && outcome != null)
                outcome.ChainsOrientedAuto++;
            return info;
        }

        private static void WalkChainOneWay(
            Document doc, FabricationPart from, Connector exitConn, bool isLeftWalk,
            HashSet<long> selectionIds, List<ChainSegment> result)
        {
            var current = from;
            var exit = exitConn;
            var seen = new HashSet<long> { from.Id.Value };
            while (true)
            {
                var next = FindNextStraightInChain(doc, current, exit);
                if (next == null || seen.Contains(next.Id.Value)) break;
                if (!selectionIds.Contains(next.Id.Value)) break;  // outside selection — chain breaks

                var nextAll = ConnectorHelper.GetPhysicalConnectors(next);
                var nextEnds = nextAll.Where(c => c.ConnectorType == ConnectorType.End).ToList();
                var nextConns = nextEnds.Count == 2 ? nextEnds : nextAll;
                if (nextConns.Count != 2) break;

                // Enter conn: the connector on `next` closest to the connection
                // point with `current`. That's the one we'd just walked into.
                // Comparing by origin (not ReferenceEquals — Connector wrappers
                // from separate iterations aren't reference-equal).
                Connector? enter = null;
                double bestD = double.MaxValue;
                foreach (var c in nextConns)
                {
                    // The exit point and the enter point may be separated by a
                    // joint piece (1-2"). Pick whichever connector on `next`
                    // is closest to `exit`.
                    double d = c.Origin.DistanceTo(exit.Origin);
                    if (d < bestD) { bestD = d; enter = c; }
                }
                if (enter == null) break;
                Connector? newExit = null;
                foreach (var c in nextConns)
                {
                    if (c.Origin.DistanceTo(enter.Origin) > 0.01)
                    { newExit = c; break; }
                }
                if (newExit == null) break;

                double lenFt = enter.Origin.DistanceTo(newExit.Origin);
                // For a LEFT walk, enter faces back toward `from` (which is to
                // OUR right), so enter = RightConn. For a RIGHT walk it's the
                // opposite.
                var leftConn  = isLeftWalk ? newExit : enter;
                var rightConn = isLeftWalk ? enter   : newExit;
                result.Add(new ChainSegment {
                    Part = next, LeftConn = leftConn, RightConn = rightConn, LengthFt = lenFt
                });
                seen.Add(next.Id.Value);
                current = next;
                exit = newExit;
            }
        }

        /// <summary>Returns the straight segment on the other side of one of
        /// <paramref name="part"/>'s ends, or null if the neighbour is a
        /// direction-change fitting (elbow / tee / etc.). Mirrors the
        /// joint-classification logic in ClassifyEnd so that what we recognise
        /// as a "joint" here matches what the user sees as a straight joint
        /// in the placer's other code paths.</summary>
        private static FabricationPart? FindNextStraightInChain(
            Document doc, FabricationPart part, Connector conn)
        {
            var neighbor = FindNeighbor(doc, part, conn);
            if (neighbor == null) return null;

            bool isJointByName = IsJointPart(neighbor);
            bool neighborIsInline = false;
            try
            {
                neighborIsInline =
                    PartTypeClassifier.IsStraightPipe(neighbor) ||
                    PartTypeClassifier.IsStraightDuctByCid(neighbor);
            }
            catch { }
            if (!isJointByName && !neighborIsInline) return null;  // direction-change fitting

            double neighborLenIn = 0;
            try
            {
                var nc = ConnectorHelper.GetPhysicalConnectors(neighbor);
                if (nc.Count == 2)
                    neighborLenIn = nc[0].Origin.DistanceTo(nc[1].Origin) * 12.0;
            }
            catch { }
            bool neighborIsShort = neighborLenIn > 0 && neighborLenIn < 12.0;

            if (isJointByName || neighborIsShort)
            {
                // Joint piece — walk past it to find the next straight beyond.
                var beyond = FindNeighborAcross(doc, part, neighbor);
                if (beyond == null) return null;
                bool beyondStraight = false;
                try
                {
                    beyondStraight =
                        PartTypeClassifier.IsStraightPipe(beyond) ||
                        PartTypeClassifier.IsStraightDuctByCid(beyond);
                }
                catch { }
                return beyondStraight ? beyond : null;
            }
            // Direct duct-to-duct interface — the neighbor IS the next straight.
            return neighbor;
        }

        /// <summary>Chain-spanning placement. Anchors come from the chain's
        /// outer-left and outer-right connector ends (classified per the
        /// spec's SupportPositions); internal joint→straight boundaries
        /// contribute no anchors. Hangers are placed at chain-wide positions
        /// and dispatched to whichever segment hosts that offset.</summary>
        private static void PlaceForChain(
            Document doc, ChainInfo chain, SupportSpec spec,
            List<SupportSpecRow> sortedRows, Outcome outcome,
            HangerFlowMap? flowMap, bool attachToStructure,
            double minSpacingFt = 0.0)
        {
            if (chain.Segments.Count == 0 || chain.TotalLengthFt <= 0) return;

            var leftSeg  = chain.Segments[0];
            var rightSeg = chain.Segments[chain.Segments.Count - 1];

            // Resolve the band off the largest segment size in the chain — keeps
            // the spacing rule consistent across the run.
            double maxSizeInches = chain.Segments
                .Select(s => ResolveSizeInches(s.LeftConn, s.RightConn))
                .DefaultIfEmpty(0).Max();
            SupportSpecRow row = sortedRows[sortedRows.Count - 1];
            bool oversize = true;
            foreach (var r in sortedRows)
            {
                if (maxSizeInches <= r.MaxSizeInches) { row = r; oversize = false; break; }
            }
            if (oversize) outcome.OversizeBand++;

            // Classify each chain-outer end using the per-side ClassifyEnd
            // logic (same as PlaceForPart). For Before/After flow modes we
            // need the near/far state of each chain-outer connector.
            bool? leftIsFar = null, rightIsFar = null;
            if (flowMap != null)
            {
                if (flowMap.IsKnown(leftSeg.Part.Id))
                    leftIsFar = flowMap.IsFarEnd(leftSeg.Part.Id, leftSeg.LeftConn);
                if (flowMap.IsKnown(rightSeg.Part.Id))
                    rightIsFar = flowMap.IsFarEnd(rightSeg.Part.Id, rightSeg.RightConn);
            }
            var endLeft  = ClassifyEnd(doc, leftSeg.Part,  leftSeg.LeftConn,  row, spec, leftIsFar);
            var endRight = ClassifyEnd(doc, rightSeg.Part, rightSeg.RightConn, row, spec, rightIsFar);

            double spacingFt    = InchesToFeet(row.StraightSpacingInches);
            double freeStartFt  = endLeft.Anchored  ? InchesToFeet(endLeft.SetbackInches)  : 0;
            double freeEndFt    = chain.TotalLengthFt -
                                  (endRight.Anchored ? InchesToFeet(endRight.SetbackInches) : 0);
            const double epsFt  = 1e-4;

            var positionsFt = BuildPositions(
                chain.TotalLengthFt, freeStartFt, freeEndFt, spacingFt,
                endLeft.Anchored, endRight.Anchored, epsFt);
            positionsFt = ApplyMinSpacing(positionsFt, minSpacingFt, outcome);
            if (positionsFt.Count == 0)
            {
                outcome.SkippedShort++;
                outcome.Notes.Add(
                    $"[skip chain head={leftSeg.Part.Id.Value}] " +
                    $"no positions computed (chain {chain.Segments.Count} seg, " +
                    $"{chain.TotalLengthFt * 12:F1}\")");
                return;
            }

            outcome.Notes.Add(
                $"[chain head={leftSeg.Part.Id.Value}] " +
                $"{chain.Segments.Count} seg total {chain.TotalLengthFt * 12:F1}\", " +
                $"placing {positionsFt.Count} hanger(s) — " +
                $"L:{(endLeft.Anchored ? endLeft.SetbackInches.ToString("0.#") : "no")} " +
                $"R:{(endRight.Anchored ? endRight.SetbackInches.ToString("0.#") : "no")}");

            // For each chain-wide position, find the host segment + local
            // offset. The hanger is anchored to that segment's LeftConn.
            // Joint-piece gaps between segments are skipped — a position
            // that lands inside a joint gap is clamped to the adjacent
            // segment's left edge (local 0). Boundary positions land on the
            // segment to the LEFT of the boundary.
            foreach (var posFt in positionsFt)
            {
                double cum = 0;
                ChainSegment? host = null;
                double localFt = 0;
                for (int i = 0; i < chain.Segments.Count; i++)
                {
                    var seg = chain.Segments[i];
                    double segStart = cum;
                    double segEnd = cum + seg.LengthFt;
                    bool isLast = i == chain.Segments.Count - 1;
                    // posFt belongs to this segment if it's at-or-before
                    // segEnd (right boundary inclusive — boundary goes left).
                    if (posFt < segEnd + epsFt || isLast)
                    {
                        host = seg;
                        // Clamp negative localFt (= posFt in the previous
                        // joint gap) to 0 = left edge of this segment.
                        localFt = Math.Max(0, Math.Min(seg.LengthFt, posFt - segStart));
                        break;
                    }
                    double gapAfter = i < chain.JointGapsFt.Count ? chain.JointGapsFt[i] : 0;
                    cum = segEnd + gapAfter;
                }
                if (host == null) continue;

                // Per-segment size lookup for the hanger button — the button
                // condition depends on the host segment's actual size, not
                // the chain-wide max.
                double hostSize = ResolveSizeInches(host.LeftConn, host.RightConn);
                string serviceName = host.Part.ServiceName ?? string.Empty;
                ConnectorProfileType hostShape = ConnectorProfileType.Round;
                try { hostShape = host.LeftConn.Shape; } catch { }
                var (button, condition, hangerNote) = ResolveHangerButton(
                    doc, serviceName, spec.HangerOverride, hostSize, hostShape);
                if (button == null)
                {
                    outcome.SkippedNoButton++;
                    outcome.Notes.Add(
                        $"[no hanger {host.Part.Id.Value}] service '{serviceName}': {hangerNote}");
                    continue;
                }

                FabricationPart? hanger = null;
                try
                {
                    hanger = CreateHangerOnHost(
                        doc, button, condition, host.Part, host.LeftConn, localFt, attachToStructure);
                }
                catch (Exception ex)
                {
                    outcome.CreateFailed++;
                    outcome.Notes.Add($"[create-fail {host.Part.Id.Value}@{localFt * 12:F1}\"] {ex.Message}");
                    continue;
                }
                if (hanger != null)
                {
                    outcome.Placed++;
                    outcome.CreatedIds.Add(hanger.Id);
                }
                else outcome.CreateFailed++;
            }
        }
    }
}
