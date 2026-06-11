using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LightPCG.Core;

namespace LightPCG.Systems
{
    /// <summary>
    /// Backtracking Search Solver v3
    ///
    /// Core fix: Phase 1A now separates objects INTO two groups before searching:
    ///   - "on-beam" objects: those the initial beam already passes through
    ///   - "off-beam" objects: decoys that are not on the beam path
    ///
    /// Strategy:
    ///   1A-OnBeam  : try all rotation combos for on-beam objects only (small n → fast)
    ///   1A-Full    : try all rotation combos for ALL objects (fallback if above fails)
    ///   1B         : relocation DFS for decoys blocking the path
    ///   Phase 2    : physically execute the verified plan in proximity order
    ///   Sweep      : last-resort in-place rotation sweep
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class AISolverAgent : MonoBehaviour
    {
        [Header("References")]
        public GridVisualizer gridVisualizer;

        [Header("Movement")]
        public float moveSpeed = 8f;
        public float rotationSpeed = 15f;

        [Header("Timing")]
        public float physicsWait = 0.15f;
        public float stepDelay = 0.05f;

        [Header("Search Limits")]
        public int maxSearchNodes = 100000;
        public int maxPlanDepth = 12;

        // ── Public stats (read by BatchRunner) ───────────────────
        [HideInInspector] public bool WasSolved;
        [HideInInspector] public int SolveIterations;
        [HideInInspector] public float SolveTimeMs;
        [HideInInspector] public float SearchTimeMs;
        [HideInInspector] public float ExecutionTimeMs;
        [HideInInspector] public int TotalPlacements;
        [HideInInspector] public int InPlaceRotations;
        [HideInInspector] public int Relocations;
        [HideInInspector] public string SolvePhase = "None";

        public System.Action<bool> OnSolveComplete;

        // ── Private ──────────────────────────────────────────────
        private GridModel grid;
        private float spacing;
        private CharacterController cc;
        private LaserSystem[] allLasers;
        private float solveStart, searchStart, execStart;
        private bool running;

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0, 1), new Vector2Int(0, -1)
        };

        // ════════════════════════════════════════════════════════════
        // DATA STRUCTURES
        // ════════════════════════════════════════════════════════════

        /// One action in the solution plan.
        /// SourceCell == TargetCell → in-place rotation (no pickup needed).
        struct PlacementAction
        {
            public Vector2Int SourceCell;
            public Vector2Int TargetCell;
            public TileType ObjType;
            public int Rotation;
            public bool IsRotateOnly => SourceCell == TargetCell;
        }

        /// Logical grid state for the search (mirrors/refractors only).
        struct GridSnapshot
        {
            public Dictionary<Vector2Int, (TileType type, int rot)> objects;
            public GridSnapshot Clone()
                => new GridSnapshot
                { objects = new Dictionary<Vector2Int, (TileType, int)>(objects) };
        }

        struct LogicalBeamResult
        {
            public List<Vector2Int> path;      // every cell the beam visits
            public HashSet<Vector2Int> pathSet; // fast lookup
            public Vector2Int endCell;
            public bool hitReceiver;
        }

        // ════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════
        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.radius = 0.28f; cc.height = 1.0f;
            cc.center = new Vector3(0, 0.5f, 0);
            cc.minMoveDistance = 0f; cc.skinWidth = 0.08f;
            cc.slopeLimit = 0f; cc.stepOffset = 0.1f;
        }

        void Start()
        {
            if (gridVisualizer == null)
                gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (gridVisualizer == null) { Debug.LogError("[AI] GridVisualizer not found!"); return; }
            StartSolve();
        }

        // ════════════════════════════════════════════════════════════
        // PUBLIC ENTRY POINT
        // ════════════════════════════════════════════════════════════
        public void StartSolve()
        {
            if (running) return;
            running = true;
            WasSolved = false; SolveIterations = 0;
            SolveTimeMs = SearchTimeMs = ExecutionTimeMs = 0f;
            TotalPlacements = InPlaceRotations = Relocations = 0;
            SolvePhase = "None"; allLasers = null;

            // Reset grid reference immediately — not inside the coroutine —
            // so that any code running before the first yield uses the correct
            // GridModel for the NEW level, not the one from the previous level.
            if (gridVisualizer != null)
            {
                grid = gridVisualizer.LevelGrid;
                spacing = gridVisualizer.Spacing;
            }

            StartCoroutine(Pipeline());
        }

        // ════════════════════════════════════════════════════════════
        // PIPELINE
        // ════════════════════════════════════════════════════════════
        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.5f);

            // Re-assign grid here after the initial wait, in case the visualiser
            // regenerated the level between StartSolve() and this point.
            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;
            allLasers = FindLaserSystems();
            solveStart = Time.realtimeSinceStartup;

            Vector2Int em = FindFirst(TileType.Emitter);
            if (em == -Vector2Int.one) { Debug.LogError("[AI] No Emitter!"); Finish(false); yield break; }

            TeleportTo(em);
            yield return new WaitForSeconds(0.3f);

            if (RealSolved()) { SolvePhase = "Trivial"; Finish(true); yield return StartCoroutine(ExitDoor()); yield break; }

            // ── Phase 1: logical search ──
            searchStart = Time.realtimeSinceStartup;
            List<PlacementAction> plan = LogicalSearch();
            SearchTimeMs = (Time.realtimeSinceStartup - searchStart) * 1000f;

            // ── Phase 2: physical execution ──
            execStart = Time.realtimeSinceStartup;
            if (plan != null)
                yield return StartCoroutine(ExecutePlan(plan));
            else
            {
                SolvePhase = "Sweep";
                Debug.LogWarning("[AI] Logical search exhausted — sweep fallback.");
                yield return StartCoroutine(CorrectionSweep());
            }
        }

        // ════════════════════════════════════════════════════════════
        // LOGICAL SEARCH  (Phase 1)
        //
        // Step 1 — Separate objects into on-beam vs off-beam
        //          by simulating the initial beam.
        //
        // Step 2 — 1A-OnBeam: enumerate all rotation combos for
        //          on-beam objects ONLY. This is the core insight:
        //          the generator placed solution objects on the beam path,
        //          so we only need to find the right rotations for them.
        //          Decoys are off-beam and irrelevant to this search.
        //          Complexity: O(8^k) where k = on-beam objects (usually 1-3).
        //
        // Step 3 — 1A-Full: if on-beam search fails (edge case where the
        //          initial beam doesn't reach all solution objects yet),
        //          try all objects.
        //
        // Step 4 — 1B: relocation DFS. Moves decoys away from the beam
        //          or repositions solution objects to new cells.
        // ════════════════════════════════════════════════════════════
        List<PlacementAction> LogicalSearch()
        {
            Vector2Int receiver = FindFirst(TileType.Receiver);
            GridSnapshot initial = SnapshotGrid();

            if (LogicalBeamReachesReceiver(initial, receiver))
                return new List<PlacementAction>();

            // Step 1: classify objects by whether the initial beam hits them
            var initialBeam = LogicalBeam(initial, receiver);

            var onBeam = new List<(Vector2Int cell, TileType type)>();
            var offBeam = new List<(Vector2Int cell, TileType type)>();

            foreach (var kv in initial.objects)
            {
                var entry = (kv.Key, kv.Value.type);
                if (initialBeam.pathSet.Contains(kv.Key)) onBeam.Add(entry);
                else offBeam.Add(entry);
            }

            Debug.Log($"[AI] Objects: {onBeam.Count} on-beam, {offBeam.Count} off-beam.");

            // Step 2: 1A-OnBeam — rotate on-beam objects only
            if (onBeam.Count > 0)
            {
                var plan = RotationSearch(initial, onBeam, receiver, "1A-OnBeam");
                if (plan != null) { SolvePhase = "1A"; return plan; }
            }

            // Step 3: 1A-Full — rotate all objects (handles cases where beam
            //         doesn't yet reach all solution objects)
            var allObjs = new List<(Vector2Int, TileType)>(onBeam);
            allObjs.AddRange(offBeam);
            if (allObjs.Count > onBeam.Count)
            {
                var plan = RotationSearch(initial, allObjs, receiver, "1A-Full");
                if (plan != null) { SolvePhase = "1A"; return plan; }
            }

            // Step 4: 1B — relocation DFS
            Debug.Log("[AI] Phase 1A exhausted — trying relocation search (1B).");
            var plan1B = RelocationSearch(initial, receiver);
            if (plan1B != null) { SolvePhase = "1B"; return plan1B; }

            return null;
        }

        // ────────────────────────────────────────────────────────────
        // ROTATION SEARCH
        // Enumerate all rotation combinations for a given object subset.
        // Only objects whose rotation differs from initial are included
        // in the returned plan (no-op rotations are omitted).
        // ────────────────────────────────────────────────────────────
        List<PlacementAction> RotationSearch(
            GridSnapshot initial,
            List<(Vector2Int cell, TileType type)> objects,
            Vector2Int receiver,
            string label)
        {
            int[] rots = { 0, 45, 90, 135, 180, 225, 270, 315 };
            int n = objects.Count;
            int total = (int)Mathf.Pow(8, n);

            for (int combo = 0; combo < total; combo++)
            {
                SolveIterations++;
                var snap = initial.Clone();
                int tmp = combo;

                for (int i = 0; i < n; i++)
                {
                    int rotIdx = tmp % 8; tmp /= 8;
                    var (cell, type) = objects[i];
                    snap.objects[cell] = (type, rots[rotIdx]);
                }

                if (LogicalBeamReachesReceiver(snap, receiver))
                {
                    var plan = new List<PlacementAction>();
                    for (int i = 0; i < n; i++)
                    {
                        var (cell, type) = objects[i];
                        int newRot = snap.objects[cell].rot;
                        int oldRot = initial.objects[cell].rot;
                        if (newRot != oldRot)
                            plan.Add(new PlacementAction
                            {
                                SourceCell = cell,
                                TargetCell = cell,
                                ObjType = type,
                                Rotation = newRot
                            });
                    }
                    Debug.Log($"[AI] {label}: {plan.Count} rotations in {SolveIterations} combos.");
                    return plan;
                }
            }
            return null;
        }

        // ────────────────────────────────────────────────────────────
        // RELOCATION SEARCH (Phase 1B)
        // DFS over (object, targetCell, rotation) with beam-score ordering.
        // ────────────────────────────────────────────────────────────
        List<PlacementAction> RelocationSearch(GridSnapshot initial, Vector2Int receiver)
        {
            var visited = new HashSet<string>();
            var stack = new Stack<(GridSnapshot snap, List<PlacementAction> plan)>();
            stack.Push((initial, new List<PlacementAction>()));

            while (stack.Count > 0 && SolveIterations < maxSearchNodes)
            {
                var (snap, plan) = stack.Pop();
                SolveIterations++;

                string key = SnapshotKey(snap);
                if (visited.Contains(key)) continue;
                visited.Add(key);
                if (plan.Count >= maxPlanDepth) continue;

                var beam = LogicalBeam(snap, receiver);
                var actions = GenerateRelocateActions(snap, beam, receiver);

                foreach (var action in actions)
                {
                    var next = ApplyAction(snap, action);
                    if (LogicalBeamReachesReceiver(next, receiver))
                    {
                        var fp = new List<PlacementAction>(plan) { action };
                        Debug.Log($"[AI] 1B: {fp.Count} moves, {SolveIterations} nodes.");
                        return fp;
                    }
                    if (!visited.Contains(SnapshotKey(next)))
                        stack.Push((next, new List<PlacementAction>(plan) { action }));
                }
            }
            Debug.LogWarning($"[AI] 1B exhausted after {SolveIterations} nodes.");
            return null;
        }

        List<PlacementAction> GenerateRelocateActions(GridSnapshot snap,
                                                      LogicalBeamResult beam,
                                                      Vector2Int receiver)
        {
            var actions = new List<PlacementAction>();
            var objList = snap.objects.Select(kv => (kv.Key, kv.Value.type)).ToList();
            var candidates = LogicalCandidates(snap, beam, receiver);

            foreach (var (src, objType) in objList)
                foreach (var tgt in candidates)
                {
                    if (tgt == src || snap.objects.ContainsKey(tgt)) continue;
                    var inDir = LogicalIncomingDir(snap, tgt, beam);
                    foreach (int rot in DeflectionRotationsForDir(objType, inDir, receiver - tgt))
                        actions.Add(new PlacementAction
                        { SourceCell = src, TargetCell = tgt, ObjType = objType, Rotation = rot });
                }

            // Sort best-first: highest beam score after applying the action
            actions.Sort((a, b) =>
                LogicalBeamScore(ApplyAction(snap, b), receiver)
                    .CompareTo(LogicalBeamScore(ApplyAction(snap, a), receiver)));
            return actions;
        }

        // ════════════════════════════════════════════════════════════
        // LOGICAL BEAM SIMULATION
        // Pure logic — reads only from GridSnapshot + fixed grid tiles.
        // Never touches GameObjects or Unity physics.
        // ════════════════════════════════════════════════════════════
        LogicalBeamResult LogicalBeam(GridSnapshot snap, Vector2Int receiver)
        {
            var result = new LogicalBeamResult
            {
                path = new List<Vector2Int>(),
                pathSet = new HashSet<Vector2Int>(),
                endCell = Vector2Int.zero,
                hitReceiver = false
            };

            Vector2Int emitter = FindFirst(TileType.Emitter);
            if (emitter == -Vector2Int.one) return result;

            var pos = emitter;
            var dir = EmDirLogical(emitter);
            var seen = new HashSet<(Vector2Int, Vector2Int)>();

            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir;
                if (!InB(pos)) break;
                var st = (pos, dir);
                if (seen.Contains(st)) break;
                seen.Add(st);

                result.path.Add(pos);
                result.pathSet.Add(pos);
                result.endCell = pos;

                TileType ft = grid.GetTile(pos.x, pos.y);
                if (ft == TileType.Wall || ft == TileType.Emitter) break;
                if (ft == TileType.Receiver) { result.hitReceiver = true; break; }

                if (snap.objects.TryGetValue(pos, out var obj))
                    dir = obj.type == TileType.Mirror
                        ? MirrorDeflect(dir, obj.rot)
                        : RefractorDeflect(dir, obj.rot);
            }
            return result;
        }

        bool LogicalBeamReachesReceiver(GridSnapshot snap, Vector2Int receiver)
            => LogicalBeam(snap, receiver).hitReceiver;

        int LogicalBeamScore(GridSnapshot snap, Vector2Int receiver)
        {
            var b = LogicalBeam(snap, receiver);
            if (b.hitReceiver) return int.MaxValue;
            return b.path.Count - Manhattan(b.endCell, receiver) * 2;
        }

        // ════════════════════════════════════════════════════════════
        // CANDIDATE CELLS FOR RELOCATION
        // ════════════════════════════════════════════════════════════
        List<Vector2Int> LogicalCandidates(GridSnapshot snap,
                                           LogicalBeamResult beam,
                                           Vector2Int receiver)
        {
            // Tier 1: beam cells on same row/col as receiver with a clear line
            var t1 = beam.path
                .Where(c => !snap.objects.ContainsKey(c) &&
                             grid.GetTile(c.x, c.y) == TileType.Empty &&
                             (c.y == receiver.y || c.x == receiver.x) &&
                             LogicalClearLine(snap, c, receiver))
                .OrderBy(c => Manhattan(c, receiver)).ToList();
            if (t1.Count > 0) return t1;

            // Tier 2: beam cells on receiver row/col (line may be blocked)
            var t2 = beam.path
                .Where(c => !snap.objects.ContainsKey(c) &&
                             grid.GetTile(c.x, c.y) == TileType.Empty &&
                             (c.y == receiver.y || c.x == receiver.x))
                .OrderBy(c => Manhattan(c, receiver)).ToList();
            if (t2.Count > 0) return t2;

            // Tier 3: any empty cell on receiver row/col
            var t3 = new List<Vector2Int>();
            for (int x = 1; x < grid.Width - 1; x++)
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    var v = new Vector2Int(x, y);
                    if (!snap.objects.ContainsKey(v) &&
                         grid.GetTile(x, y) == TileType.Empty &&
                        (v.y == receiver.y || v.x == receiver.x)) t3.Add(v);
                }
            t3 = t3.OrderBy(c => Manhattan(c, receiver)).ToList();
            if (t3.Count > 0) return t3;

            // Tier 4: any non-occupied empty cell
            return Enumerable.Range(1, grid.Width - 2)
                .SelectMany(x => Enumerable.Range(1, grid.Height - 2)
                    .Select(y => new Vector2Int(x, y)))
                .Where(v => !snap.objects.ContainsKey(v) &&
                             grid.GetTile(v.x, v.y) == TileType.Empty)
                .OrderBy(v => Manhattan(v, receiver))
                .ToList();
        }

        bool LogicalClearLine(GridSnapshot snap, Vector2Int from, Vector2Int to)
        {
            if (from == to) return true;
            if (from.x != to.x && from.y != to.y) return false;
            var step = new Vector2Int(
                from.x == to.x ? 0 : (int)Mathf.Sign(to.x - from.x),
                from.y == to.y ? 0 : (int)Mathf.Sign(to.y - from.y));
            for (var c = from + step; c != to; c += step)
            {
                TileType t = grid.GetTile(c.x, c.y);
                if (t == TileType.Wall || t == TileType.Emitter || t == TileType.Door) return false;
                if (snap.objects.ContainsKey(c)) return false;
            }
            return true;
        }

        Vector2Int LogicalIncomingDir(GridSnapshot snap, Vector2Int cell, LogicalBeamResult beam)
        {
            var pos = FindFirst(TileType.Emitter);
            var dir = EmDirLogical(pos);
            var seen = new HashSet<(Vector2Int, Vector2Int)>();
            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                if (pos + dir == cell) return dir;
                pos += dir;
                if (!InB(pos)) break;
                var st = (pos, dir);
                if (seen.Contains(st)) break;
                seen.Add(st);
                TileType ft = grid.GetTile(pos.x, pos.y);
                if (ft == TileType.Wall || ft == TileType.Receiver || ft == TileType.Emitter) break;
                if (snap.objects.TryGetValue(pos, out var obj))
                    dir = obj.type == TileType.Mirror
                        ? MirrorDeflect(dir, obj.rot) : RefractorDeflect(dir, obj.rot);
            }
            return dir;
        }

        // ════════════════════════════════════════════════════════════
        // DEFLECTION ROTATION SELECTOR
        // Returns rotations sorted: correct deflection first.
        // ════════════════════════════════════════════════════════════
        List<int> DeflectionRotationsForDir(TileType objType,
                                            Vector2Int inDir, Vector2Int toReceiver)
        {
            Vector2Int outDir = Mathf.Abs(toReceiver.x) >= Mathf.Abs(toReceiver.y)
                ? (toReceiver.x >= 0 ? Vector2Int.right : Vector2Int.left)
                : (toReceiver.y >= 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1));

            var good = new List<int>(); var fallback = new List<int>();
            foreach (int rot in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
            {
                var result = objType == TileType.Mirror
                    ? MirrorDeflect(inDir, rot) : RefractorDeflect(inDir, rot);
                if (result == outDir) good.Add(rot); else fallback.Add(rot);
            }
            good.AddRange(fallback);
            return good;
        }

        // ════════════════════════════════════════════════════════════
        // SNAPSHOT HELPERS
        // ════════════════════════════════════════════════════════════
        GridSnapshot SnapshotGrid()
        {
            var snap = new GridSnapshot
            { objects = new Dictionary<Vector2Int, (TileType, int)>() };
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    TileType t = grid.GetTile(x, y);
                    if (t != TileType.Mirror && t != TileType.Refractor) continue;
                    var cell = new Vector2Int(x, y);
                    int rawRot = Mathf.RoundToInt(GetYRot(cell));
                    int rot = ((rawRot % 360) + 360) % 360;
                    rot = Mathf.RoundToInt(rot / 45f) * 45 % 360;
                    snap.objects[cell] = (t, rot);
                }
            return snap;
        }

        GridSnapshot ApplyAction(GridSnapshot snap, PlacementAction a)
        {
            var next = snap.Clone();
            if (a.SourceCell != a.TargetCell) next.objects.Remove(a.SourceCell);
            next.objects[a.TargetCell] = (a.ObjType, a.Rotation);
            return next;
        }

        string SnapshotKey(GridSnapshot snap)
        {
            var parts = snap.objects
                .Select(kv => $"{kv.Key.x},{kv.Key.y},{(int)kv.Value.type},{kv.Value.rot}")
                .OrderBy(s => s).ToList();
            return string.Join("|", parts);
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 2 — PHYSICAL EXECUTION
        //
        // Execute the verified plan physically.
        // Plan steps are sorted by proximity to the agent's current position
        // to minimise total walking distance.
        //
        // In-place rotation: walk to cell, rotate — no pickup needed.
        // Relocation:        walk to source, pickup, walk to target, place.
        // ════════════════════════════════════════════════════════════
        IEnumerator ExecutePlan(List<PlacementAction> plan)
        {
            Debug.Log($"[AI] Executing plan: {plan.Count} steps.");

            // Sort steps by proximity to current agent position (greedy nearest-first)
            // This avoids the bot running back and forth across the grid.
            var remaining = new List<PlacementAction>(plan);

            while (remaining.Count > 0)
            {
                if (RealSolved()) break;

                // Pick the step whose source cell is closest to current agent position
                Vector2Int agentCell = WorldToGrid(transform.position);
                int bestIdx = 0;
                int bestDist = int.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int d = Manhattan(agentCell, remaining[i].SourceCell);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }

                var action = remaining[bestIdx];
                remaining.RemoveAt(bestIdx);
                TotalPlacements++;

                if (action.IsRotateOnly)
                {
                    // In-place rotation: walk there and rotate the GameObject
                    InPlaceRotations++;
                    Debug.Log($"[AI] Rotate {action.ObjType}@{action.TargetCell} → {action.Rotation}°");
                    yield return StartCoroutine(WalkTo(action.TargetCell));
                    if (gridVisualizer.SpawnedObjects.TryGetValue(action.TargetCell, out var goR) && goR != null)
                        goR.transform.rotation = Quaternion.Euler(0f, action.Rotation, 0f);
                    yield return new WaitForSeconds(physicsWait);
                }
                else
                {
                    // Relocation: walk → pickup → walk → place
                    Relocations++;
                    Debug.Log($"[AI] Move {action.ObjType} {action.SourceCell}→{action.TargetCell} {action.Rotation}°");
                    yield return StartCoroutine(WalkTo(action.SourceCell));
                    yield return new WaitForSeconds(stepDelay);
                    GameObject held = PickupObject(action.SourceCell);
                    yield return StartCoroutine(WalkTo(action.TargetCell));
                    PlaceObject(action.TargetCell, action.ObjType, held, action.Rotation);
                    yield return new WaitForSeconds(physicsWait);
                }
            }

            ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
            yield return new WaitForSeconds(physicsWait * 2f);

            if (RealSolved())
            {
                Debug.Log("[AI] Plan verified — SOLVED!");
                Finish(true);
                yield return StartCoroutine(ExitDoor());
            }
            else
            {
                Debug.LogWarning("[AI] Plan failed under physics — running sweep.");
                SolvePhase = "Sweep";
                yield return StartCoroutine(CorrectionSweep());
            }
        }

        // ════════════════════════════════════════════════════════════
        // CORRECTION SWEEP (last-resort fallback)
        // Tries all 8 rotations on every object in place.
        // ════════════════════════════════════════════════════════════
        IEnumerator CorrectionSweep()
        {
            int[] rots = { 0, 45, 90, 135, 180, 225, 270, 315 };
            for (int pass = 0; pass < 3 && !RealSolved(); pass++)
            {
                foreach (var objCell in GetInteractables())
                {
                    if (RealSolved()) break;
                    yield return StartCoroutine(WalkTo(objCell));
                    foreach (int rot in rots)
                    {
                        if (gridVisualizer.SpawnedObjects.TryGetValue(objCell, out var go) && go != null)
                            go.transform.rotation = Quaternion.Euler(0f, rot, 0f);
                        yield return new WaitForSeconds(physicsWait);
                        if (RealSolved())
                        {
                            ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
                            Debug.Log($"[AI] Sweep solved! {rot}°@{objCell}");
                            Finish(true);
                            yield return StartCoroutine(ExitDoor());
                            yield break;
                        }
                    }
                }
            }
            ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
            if (!RealSolved()) { Debug.LogWarning("[AI] All phases failed."); Finish(false); }
        }

        // ════════════════════════════════════════════════════════════
        // WALK TO / TELEPORT / BFS
        // ════════════════════════════════════════════════════════════
        IEnumerator WalkTo(Vector2Int target)
        {
            if (target == -Vector2Int.one || !InB(target)) yield break;
            Vector3 wt = gridVisualizer.GridToWorld(target.x, target.y);
            if (Vector3.Distance(transform.position, wt) < 0.2f) yield break;

            var path = BFS(WorldToGrid(transform.position), target);
            if (path == null || path.Count == 0) { TeleportTo(target); yield break; }

            foreach (var step in path)
            {
                Vector3 dest = gridVisualizer.GridToWorld(step.x, step.y);
                dest.y = transform.position.y;
                float timeout = 5f;
                while (timeout > 0f)
                {
                    timeout -= Time.deltaTime;
                    Vector3 delta = dest - transform.position; delta.y = 0f;
                    if (delta.magnitude < 0.15f) break;
                    if (delta.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(delta), rotationSpeed * Time.deltaTime);
                    cc.SimpleMove(delta.normalized * moveSpeed);
                    yield return null;
                }
            }
        }

        void TeleportTo(Vector2Int cell)
        {
            cc.enabled = false;
            Vector3 p = gridVisualizer.GridToWorld(cell.x, cell.y); p.y = 0.5f;
            transform.position = p;
            cc.enabled = true;
        }

        List<Vector2Int> BFS(Vector2Int start, Vector2Int goal)
        {
            if (start == goal) return new List<Vector2Int>();
            var visited = new HashSet<Vector2Int> { start };
            var parent = new Dictionary<Vector2Int, Vector2Int>();
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var d in Dirs4)
                {
                    var next = cur + d;
                    if (!InB(next) || visited.Contains(next)) continue;
                    TileType t = grid.GetTile(next.x, next.y);
                    if (t != TileType.Empty && t != TileType.Door && next != goal) continue;
                    visited.Add(next); parent[next] = cur;
                    if (next == goal)
                    {
                        var p = new List<Vector2Int>();
                        for (var c = goal; c != start; c = parent[c]) p.Add(c);
                        p.Reverse(); return p;
                    }
                    queue.Enqueue(next);
                }
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        // EXIT DOOR
        // ════════════════════════════════════════════════════════════
        IEnumerator ExitDoor()
        {
            yield return new WaitForSeconds(0.4f);
            Vector2Int dc = FindFirst(TileType.Door);
            if (dc == -Vector2Int.one) yield break;
            grid.SetTile(dc.x, dc.y, TileType.Empty);
            if (gridVisualizer.SpawnedObjects.TryGetValue(dc, out var dgo) && dgo != null) Destroy(dgo);
            gridVisualizer.SpawnedObjects.Remove(dc);
            yield return StartCoroutine(WalkTo(dc));
            Vector2Int beyond = dc + OutDir(dc);
            Vector3 bw = gridVisualizer.GridToWorld(beyond.x, beyond.y);
            float timeout = 3f;
            while (Vector3.Distance(transform.position, bw) > 0.3f && timeout > 0)
            {
                timeout -= Time.deltaTime;
                Vector3 dir = (bw - transform.position); dir.y = 0f; dir.Normalize();
                cc.SimpleMove(dir * moveSpeed);
                yield return null;
            }
            Debug.Log("[AI] Exited door!");
        }

        Vector2Int OutDir(Vector2Int c)
        {
            int cx = grid.Width / 2, cy = grid.Height / 2;
            return Mathf.Abs(cx - c.x) > Mathf.Abs(cy - c.y)
                ? new Vector2Int((int)Mathf.Sign(c.x - cx), 0)
                : new Vector2Int(0, (int)Mathf.Sign(c.y - cy));
        }

        // ════════════════════════════════════════════════════════════
        // GRID MATH
        // ════════════════════════════════════════════════════════════
        Vector2Int MirrorDeflect(Vector2Int d, int rot)
        {
            float a = rot;
            return (Mathf.Abs(Mathf.DeltaAngle(a, 45f)) < 22.5f ||
                    Mathf.Abs(Mathf.DeltaAngle(a, 225f)) < 22.5f)
                ? new Vector2Int(d.y, d.x) : new Vector2Int(-d.y, -d.x);
        }

        Vector2Int RefractorDeflect(Vector2Int d, int rot)
        {
            float a = rot;
            return (Mathf.Abs(Mathf.DeltaAngle(a, 0f)) < 22.5f ||
                    Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22.5f)
                ? new Vector2Int(-d.y, d.x) : new Vector2Int(d.y, -d.x);
        }

        float GetYRot(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out var obj) && obj != null)
                return obj.transform.eulerAngles.y;
            return 0f;
        }

        Vector2Int EmDirLogical(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out var go) && go != null)
            {
                var f = go.transform.forward;
                if (Mathf.Abs(f.x) >= Mathf.Abs(f.z))
                    return f.x >= 0 ? Vector2Int.right : Vector2Int.left;
                return f.z >= 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);
            }
            foreach (var d in Dirs4)
            {
                var n = cell + d; if (!InB(n)) continue;
                TileType t = grid.GetTile(n.x, n.y);
                if (t != TileType.Wall && t != TileType.Door && t != TileType.Emitter) return d;
            }
            return Vector2Int.right;
        }

        // ════════════════════════════════════════════════════════════
        // REAL LASER CHECK (Unity physics)
        // ════════════════════════════════════════════════════════════
        bool RealSolved()
        {
            if (allLasers == null || allLasers.Length == 0) allLasers = FindLaserSystems();
            if (allLasers.Length == 0) return false;
            int hitting = 0;
            foreach (var l in allLasers) if (l != null && l.IsHittingReceiver) hitting++;
            return hitting == allLasers.Length;
        }

        LaserSystem[] FindLaserSystems()
        {
            var list = new List<LaserSystem>();
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue;
                    var cell = new Vector2Int(x, y);
                    if (!gridVisualizer.SpawnedObjects.TryGetValue(cell, out var go) || go == null) continue;
                    var ls = go.GetComponent<LaserSystem>();
                    if (ls != null) list.Add(ls);
                }
            if (list.Count == 0) return FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);
            return list.ToArray();
        }

        // ════════════════════════════════════════════════════════════
        // ATOMIC PICKUP / PLACE
        // ════════════════════════════════════════════════════════════
        GameObject PickupObject(Vector2Int cell)
        {
            gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject go);
            grid.SetTile(cell.x, cell.y, TileType.Empty);
            gridVisualizer.SpawnedObjects.Remove(cell);
            if (go != null) go.SetActive(false);
            return go;
        }

        void PlaceObject(Vector2Int cell, TileType type, GameObject go, int rotDeg)
        {
            grid.SetTile(cell.x, cell.y, type);
            gridVisualizer.SpawnedObjects[cell] = go;
            if (go == null) return;
            go.transform.position = gridVisualizer.GridToWorld(cell.x, cell.y);
            go.transform.rotation = Quaternion.Euler(0f, rotDeg, 0f);
            go.SetActive(true);
        }

        // ════════════════════════════════════════════════════════════
        // UTILITIES
        // ════════════════════════════════════════════════════════════
        List<Vector2Int> GetInteractables()
        {
            var l = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    TileType t = grid.GetTile(x, y);
                    if (t == TileType.Mirror || t == TileType.Refractor) l.Add(new Vector2Int(x, y));
                }
            return l;
        }

        Vector2Int FindFirst(TileType target)
        {
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == target) return new Vector2Int(x, y);
            return -Vector2Int.one;
        }

        void Finish(bool solved)
        {
            WasSolved = solved;
            SolveTimeMs = (Time.realtimeSinceStartup - solveStart) * 1000f;
            if (ExecutionTimeMs <= 0f)
                ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
            running = false;
            OnSolveComplete?.Invoke(solved);
        }

        int Manhattan(Vector2Int a, Vector2Int b)
            => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

        Vector2Int WorldToGrid(Vector3 w)
        {
            float ox = (grid.Width - 1) * spacing / 2f;
            float oz = (grid.Height - 1) * spacing / 2f;
            return new Vector2Int(
                Mathf.Clamp(Mathf.RoundToInt((w.x + ox) / spacing), 0, grid.Width - 1),
                Mathf.Clamp(Mathf.RoundToInt((w.z + oz) / spacing), 0, grid.Height - 1));
        }

        bool InB(Vector2Int p)
            => p.x >= 0 && p.x < grid.Width && p.y >= 0 && p.y < grid.Height;
    }
}