using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LightPCG.Core;

namespace LightPCG.Systems
{
    /// <summary>
    /// Backtracking Search Solver (v2 — handles in-place rotation + relocation).
    ///
    /// Search strategy:
    ///   Phase 1A — In-place rotation search (O(8^n), n = movable objects).
    ///              Covers the majority of cases because the generator guarantees
    ///              solution objects are already on the solution path.
    ///   Phase 1B — Relocation + rotation DFS, used when decoys block the path.
    ///   Phase 2  — Physical execution of the verified plan.
    ///   Fallback  — Correction sweep if physics disagrees with logic.
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

        // ── Public stats read by BatchRunner ──────────────────────
        [HideInInspector] public bool WasSolved;

        /// Total logical nodes / combos explored across Phase 1A + 1B.
        [HideInInspector] public int SolveIterations;

        /// Wall-clock time from StartSolve() to Finish() in milliseconds.
        [HideInInspector] public float SolveTimeMs;

        /// Time spent in Phase 1A + 1B logical search only (ms).
        [HideInInspector] public float SearchTimeMs;

        /// Time spent physically executing the plan (ms).
        [HideInInspector] public float ExecutionTimeMs;

        /// Number of physical pick-up + place actions performed.
        [HideInInspector] public int TotalPlacements;

        /// Number of in-place rotations performed (no move).
        [HideInInspector] public int InPlaceRotations;

        /// Number of full relocations performed (pickup + move + place).
        [HideInInspector] public int Relocations;

        /// Which search phase found the solution: "1A", "1B", "Sweep", "None".
        [HideInInspector] public string SolvePhase = "None";

        public System.Action<bool> OnSolveComplete;

        // ── Private state ─────────────────────────────────────────
        private GridModel grid;
        private float spacing;
        private CharacterController cc;
        private LaserSystem[] allLasers;
        private float solveStart;
        private float searchStart;
        private float execStart;
        private bool running;

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0,  1), new Vector2Int(0, -1)
        };

        // ════════════════════════════════════════════════════════════
        // DATA STRUCTURES
        // ════════════════════════════════════════════════════════════

        /// One step in a solution plan.
        /// SourceCell == TargetCell means in-place rotation only.
        struct PlacementAction
        {
            public Vector2Int SourceCell;
            public Vector2Int TargetCell;
            public TileType ObjType;
            public int Rotation;
            public bool IsRotateOnly => SourceCell == TargetCell;
        }

        /// Logical grid state: cell → (TileType, rotation) for every Mirror/Refractor.
        struct GridSnapshot
        {
            public Dictionary<Vector2Int, (TileType type, int rot)> objects;
            public GridSnapshot Clone()
                => new GridSnapshot
                { objects = new Dictionary<Vector2Int, (TileType, int)>(objects) };
        }

        // ════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════
        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.radius = 0.28f;
            cc.height = 1.0f;
            cc.center = new Vector3(0, 0.5f, 0);
            cc.minMoveDistance = 0f;
            cc.skinWidth = 0.08f;
            cc.slopeLimit = 0f;
            cc.stepOffset = 0.1f;
        }

        void Start()
        {
            if (gridVisualizer == null)
                gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (gridVisualizer == null)
            { Debug.LogError("[AI] GridVisualizer not found!"); return; }
            StartSolve();
        }

        // ════════════════════════════════════════════════════════════
        // PUBLIC ENTRY POINT
        // ════════════════════════════════════════════════════════════
        public void StartSolve()
        {
            if (running) return;
            running = true;
            WasSolved = false;
            SolveIterations = 0;
            SolveTimeMs = 0f;
            SearchTimeMs = 0f;
            ExecutionTimeMs = 0f;
            TotalPlacements = 0;
            InPlaceRotations = 0;
            Relocations = 0;
            SolvePhase = "None";
            allLasers = null;
            StartCoroutine(Pipeline());
        }

        // ════════════════════════════════════════════════════════════
        // PIPELINE
        // ════════════════════════════════════════════════════════════
        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.5f);

            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;
            allLasers = FindLaserSystems();
            solveStart = Time.realtimeSinceStartup;

            Vector2Int em = FindFirst(TileType.Emitter);
            if (em == -Vector2Int.one)
            { Debug.LogError("[AI] No Emitter!"); Finish(false); yield break; }

            TeleportTo(em);
            yield return new WaitForSeconds(0.3f);

            if (RealSolved())
            {
                SolvePhase = "Trivial";
                Finish(true);
                yield return StartCoroutine(ExitDoor());
                yield break;
            }

            // ── Phase 1: logical search (no physical movement) ──
            searchStart = Time.realtimeSinceStartup;

            List<PlacementAction> plan = SolveInPlaceRotations();
            if (plan != null) SolvePhase = "1A";

            if (plan == null)
            {
                Debug.Log("[AI] Phase 1A found no solution — trying relocation search.");
                plan = SolveWithRelocation();
                if (plan != null) SolvePhase = "1B";
            }

            SearchTimeMs = (Time.realtimeSinceStartup - searchStart) * 1000f;

            // ── Phase 2: physical execution ──
            execStart = Time.realtimeSinceStartup;

            if (plan != null)
                yield return StartCoroutine(ExecutePlan(plan));
            else
            {
                Debug.LogWarning("[AI] Both search phases exhausted — running sweep fallback.");
                SolvePhase = "Sweep";
                yield return StartCoroutine(CorrectionSweep());
            }
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 1A — IN-PLACE ROTATION SEARCH
        // ════════════════════════════════════════════════════════════
        List<PlacementAction> SolveInPlaceRotations()
        {
            Vector2Int receiver = FindFirst(TileType.Receiver);
            GridSnapshot initial = SnapshotGrid();

            if (LogicalBeamReachesReceiver(initial, receiver))
                return new List<PlacementAction>();

            var objects = new List<(Vector2Int cell, TileType type)>();
            foreach (var kv in initial.objects) objects.Add((kv.Key, kv.Value.type));

            int[] rotations = { 0, 45, 90, 135, 180, 225, 270, 315 };
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
                    snap.objects[cell] = (type, rotations[rotIdx]);
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
                    Debug.Log($"[AI] Phase 1A: {plan.Count} rotations in {SolveIterations} combos.");
                    return plan;
                }
            }
            Debug.Log($"[AI] Phase 1A exhausted {total} combos.");
            return null;
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 1B — RELOCATION + ROTATION SEARCH
        // ════════════════════════════════════════════════════════════
        List<PlacementAction> SolveWithRelocation()
        {
            Vector2Int receiver = FindFirst(TileType.Receiver);
            GridSnapshot initial = SnapshotGrid();

            if (LogicalBeamReachesReceiver(initial, receiver))
                return new List<PlacementAction>();

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
                        var finalPlan = new List<PlacementAction>(plan) { action };
                        Debug.Log($"[AI] Phase 1B: {finalPlan.Count} moves, {SolveIterations} nodes.");
                        return finalPlan;
                    }
                    string nextKey = SnapshotKey(next);
                    if (!visited.Contains(nextKey))
                        stack.Push((next, new List<PlacementAction>(plan) { action }));
                }
            }
            Debug.LogWarning($"[AI] Phase 1B exhausted after {SolveIterations} nodes.");
            return null;
        }

        List<PlacementAction> GenerateRelocateActions(GridSnapshot snap,
                                                      LogicalBeamResult beam,
                                                      Vector2Int receiver)
        {
            var actions = new List<PlacementAction>();
            var objList = new List<(Vector2Int cell, TileType type)>();
            foreach (var kv in snap.objects) objList.Add((kv.Key, kv.Value.type));
            var candidates = LogicalCandidates(snap, beam, receiver);

            foreach (var (srcCell, objType) in objList)
                foreach (var tgt in candidates)
                {
                    if (tgt == srcCell || snap.objects.ContainsKey(tgt)) continue;
                    Vector2Int inDir = LogicalIncomingDir(snap, tgt, beam);
                    foreach (int rot in DeflectionRotationsForDir(objType, inDir, receiver - tgt))
                        actions.Add(new PlacementAction
                        { SourceCell = srcCell, TargetCell = tgt, ObjType = objType, Rotation = rot });
                }

            actions.Sort((a, b) =>
            {
                int sA = LogicalBeamScore(ApplyAction(snap, a), receiver);
                int sB = LogicalBeamScore(ApplyAction(snap, b), receiver);
                return sB.CompareTo(sA);
            });
            return actions;
        }

        // ════════════════════════════════════════════════════════════
        // LOGICAL BEAM SIMULATION
        // ════════════════════════════════════════════════════════════
        struct LogicalBeamResult
        {
            public List<Vector2Int> path;
            public Vector2Int endCell;
            public bool hitReceiver;
        }

        LogicalBeamResult LogicalBeam(GridSnapshot snap, Vector2Int receiver)
        {
            var result = new LogicalBeamResult
            { path = new List<Vector2Int>(), endCell = Vector2Int.zero, hitReceiver = false };

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
                result.endCell = pos;

                TileType ft = grid.GetTile(pos.x, pos.y);
                if (ft == TileType.Wall || ft == TileType.Emitter) break;
                if (ft == TileType.Receiver) { result.hitReceiver = true; break; }

                if (snap.objects.TryGetValue(pos, out var obj))
                {
                    dir = obj.type == TileType.Mirror
                        ? MirrorDeflect(dir, obj.rot)
                        : RefractorDeflect(dir, obj.rot);
                }
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
        // CANDIDATE CELLS
        // ════════════════════════════════════════════════════════════
        List<Vector2Int> LogicalCandidates(GridSnapshot snap,
                                           LogicalBeamResult beam,
                                           Vector2Int receiver)
        {
            var tier1 = new List<Vector2Int>();
            foreach (var cell in beam.path)
            {
                if (snap.objects.ContainsKey(cell) || grid.GetTile(cell.x, cell.y) != TileType.Empty) continue;
                if ((cell.y == receiver.y || cell.x == receiver.x) &&
                     LogicalClearLine(snap, cell, receiver)) tier1.Add(cell);
            }
            tier1.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            if (tier1.Count > 0) return tier1;

            var tier2 = new List<Vector2Int>();
            foreach (var cell in beam.path)
            {
                if (snap.objects.ContainsKey(cell) || grid.GetTile(cell.x, cell.y) != TileType.Empty) continue;
                if (cell.y == receiver.y || cell.x == receiver.x) tier2.Add(cell);
            }
            tier2.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            if (tier2.Count > 0) return tier2;

            var tier3 = new List<Vector2Int>();
            for (int x = 1; x < grid.Width - 1; x++)
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    var v = new Vector2Int(x, y);
                    if (snap.objects.ContainsKey(v) || grid.GetTile(x, y) != TileType.Empty) continue;
                    if (v.y == receiver.y || v.x == receiver.x) tier3.Add(v);
                }
            tier3.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            if (tier3.Count > 0) return tier3;

            var tier4 = new List<Vector2Int>();
            for (int x = 1; x < grid.Width - 1; x++)
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    var v = new Vector2Int(x, y);
                    if (!snap.objects.ContainsKey(v) && grid.GetTile(x, y) == TileType.Empty)
                        tier4.Add(v);
                }
            tier4.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            return tier4;
        }

        bool LogicalClearLine(GridSnapshot snap, Vector2Int from, Vector2Int to)
        {
            if (from == to) return true;
            if (from.x != to.x && from.y != to.y) return false;
            Vector2Int step = new Vector2Int(
                from.x == to.x ? 0 : (int)Mathf.Sign(to.x - from.x),
                from.y == to.y ? 0 : (int)Mathf.Sign(to.y - from.y));
            var cur = from + step;
            while (cur != to)
            {
                TileType t = grid.GetTile(cur.x, cur.y);
                if (t == TileType.Wall || t == TileType.Emitter || t == TileType.Door) return false;
                if (snap.objects.ContainsKey(cur)) return false;
                cur += step;
            }
            return true;
        }

        Vector2Int LogicalIncomingDir(GridSnapshot snap, Vector2Int cell, LogicalBeamResult beam)
        {
            Vector2Int emitter = FindFirst(TileType.Emitter);
            var pos = emitter;
            var dir = EmDirLogical(emitter);
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
        // ════════════════════════════════════════════════════════════
        List<int> DeflectionRotationsForDir(TileType objType, Vector2Int inDir, Vector2Int toReceiver)
        {
            Vector2Int outDir;
            if (Mathf.Abs(toReceiver.x) >= Mathf.Abs(toReceiver.y))
                outDir = toReceiver.x >= 0 ? Vector2Int.right : Vector2Int.left;
            else
                outDir = toReceiver.y >= 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);

            var good = new List<int>(); var fallback = new List<int>();
            foreach (int rot in new int[] { 0, 45, 90, 135, 180, 225, 270, 315 })
            {
                Vector2Int res = objType == TileType.Mirror
                    ? MirrorDeflect(inDir, rot) : RefractorDeflect(inDir, rot);
                if (res == outDir) good.Add(rot); else fallback.Add(rot);
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
                    int rot = (((rawRot % 360) + 360) % 360);
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
            var parts = new List<string>(snap.objects.Count);
            foreach (var kv in snap.objects)
                parts.Add($"{kv.Key.x},{kv.Key.y},{(int)kv.Value.type},{kv.Value.rot}");
            parts.Sort();
            return string.Join("|", parts);
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 2 — PHYSICAL EXECUTION
        // ════════════════════════════════════════════════════════════
        IEnumerator ExecutePlan(List<PlacementAction> plan)
        {
            Debug.Log($"[AI] Executing plan: {plan.Count} steps.");

            foreach (var action in plan)
            {
                if (RealSolved()) break;
                TotalPlacements++;

                if (action.IsRotateOnly)
                {
                    // In-place rotation — walk to cell and rotate
                    InPlaceRotations++;
                    Debug.Log($"[AI] Rotate {action.ObjType}@{action.TargetCell} -> {action.Rotation}°");
                    yield return StartCoroutine(WalkTo(action.TargetCell));
                    if (gridVisualizer.SpawnedObjects.TryGetValue(
                            action.TargetCell, out var goR) && goR != null)
                        goR.transform.rotation = Quaternion.Euler(0f, action.Rotation, 0f);
                    yield return new WaitForSeconds(physicsWait);
                }
                else
                {
                    // Relocation — walk to source, pickup, walk to target, place
                    Relocations++;
                    Debug.Log($"[AI] Move {action.ObjType} " +
                              $"{action.SourceCell}->{action.TargetCell} rot={action.Rotation}°");
                    yield return StartCoroutine(WalkTo(action.SourceCell));
                    yield return new WaitForSeconds(stepDelay);
                    GameObject heldGo = PickupObject(action.SourceCell);
                    yield return StartCoroutine(WalkTo(action.TargetCell));
                    PlaceObject(action.TargetCell, action.ObjType, heldGo, action.Rotation);
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
            if (!RealSolved())
            {
                ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
                Debug.LogWarning("[AI] All phases failed.");
                Finish(false);
            }
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
            if (gridVisualizer.SpawnedObjects.TryGetValue(dc, out var dgo) && dgo != null)
                Destroy(dgo);
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
            int gx = Mathf.Clamp(Mathf.RoundToInt((w.x + ox) / spacing), 0, grid.Width - 1);
            int gz = Mathf.Clamp(Mathf.RoundToInt((w.z + oz) / spacing), 0, grid.Height - 1);
            return new Vector2Int(gx, gz);
        }

        bool InB(Vector2Int p)
            => p.x >= 0 && p.x < grid.Width && p.y >= 0 && p.y < grid.Height;
    }
}