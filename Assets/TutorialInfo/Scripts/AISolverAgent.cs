using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LightPCG.Core;

namespace LightPCG.Systems
{
    /// <summary>
    /// Backtracking Search Solver v4
    ///
    /// Strategy (Phase 1 — Logical search):
    ///   1A-OnBeam  : try all rotation combos for on-beam objects only
    ///   1A-Full    : try all rotation combos for ALL objects (fallback)
    ///   1B         : beam-guided relocation search
    ///
    /// Strategy (Phase 2 — Physical execution):
    ///   ExecutePlan → CorrectionSweep (if plan fails under physics)
    ///
    /// CorrectionSweep v4 — three targeted stages:
    ///   S1 : Rotate on-beam objects only (fast, beam-guided)
    ///   S2 : Relocate off-beam objects INTO beam path, then retry S1
    ///        ← NEW: handles the "beam hits no objects" edge case
    ///        ← replaces old S3 random-rotate-all which was unguided
    ///   S3 : Exhaustive rotate-all fallback (kept as last resort only)
    ///
    /// SolvePhase values written out (used as the difficulty signal for RQ2):
    ///   Trivial    — solved with no manipulation at all
    ///   1A         — solved by rotation search only
    ///   1B         — solved by beam-guided relocation search
    ///   Sweep-S1   — Phase 1 search failed; solved by on-beam rotation fallback
    ///   Sweep-S2   — solved only after relocating an object onto the beam first
    ///   Sweep-S3   — solved only by exhaustive last-resort rotation
    ///   Sweep      — entered the sweep fallback but never reached a solution
    ///   None       — failed before/without ever entering the sweep
    ///
    /// NOTE: Sweep-S1/S2/S3 used to be collapsed into a single "Sweep" label,
    /// which made it impossible to tell the difference between a puzzle that
    /// needed one quick correction rotation and one that needed exhaustive
    /// brute force. This distinction is added because RQ2's evaluation
    /// depends on showing that harder (higher-MID) puzzles require deeper
    /// solver stages — that signal was lost when all three were unified.
    ///
    /// Key fix vs v3:
    ///   Old S3 rotated every object including decoys outside the beam,
    ///   which cannot redirect light and only wasted time or solved by accident.
    ///   New S2 detects beam-too-short / no-on-beam-object situations and
    ///   physically moves one off-beam object into the beam path before
    ///   re-running S1. This makes every Sweep action purposeful.
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

        // ── Public stats (read by BatchRunner) ────────────────────
        [HideInInspector] public bool WasSolved;
        [HideInInspector] public int SolveIterations;
        [HideInInspector] public float SolveTimeMs;
        [HideInInspector] public float SearchTimeMs;
        [HideInInspector] public float ExecutionTimeMs;
        [HideInInspector] public int TotalPlacements;
        [HideInInspector] public int InPlaceRotations;
        [HideInInspector] public int Relocations;
        [HideInInspector] public string SolvePhase = "None";

        // ── Difficulty-signal stats (added for RQ2 sweep-stage tracking) ──
        // These exist so BatchRunner can tell, for puzzles that fall back to
        // CorrectionSweep, exactly how much extra work the solver needed and
        // which sub-stage (S1/S2/S3) actually produced the solution. Without
        // these, every Sweep-solved puzzle was indistinguishable in the CSV,
        // which hid the difficulty signal exactly where it matters most.
        [HideInInspector] public int SweepIterations;   // rotation/placement attempts made inside S1+S2+S3
        [HideInInspector] public int SweepRelocations;  // relocations performed specifically by Sweep S2
                                                        // (kept separate from Phase-1B's `Relocations` above)

        public System.Action<bool> OnSolveComplete;

        // ── Private ───────────────────────────────────────────────
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
        struct PlacementAction
        {
            public Vector2Int SourceCell;
            public Vector2Int TargetCell;
            public TileType ObjType;
            public int Rotation;
            public bool IsRotateOnly => SourceCell == TargetCell;
        }

        struct GridSnapshot
        {
            public Dictionary<Vector2Int, (TileType type, int rot)> objects;
            public GridSnapshot Clone()
                => new GridSnapshot
                { objects = new Dictionary<Vector2Int, (TileType, int)>(objects) };
        }

        struct LogicalBeamResult
        {
            public List<Vector2Int> path;
            public HashSet<Vector2Int> pathSet;
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
            StopAllCoroutines();
            running = false;

            running = true;
            WasSolved = false;
            SolveIterations = 0;
            SolveTimeMs = SearchTimeMs = ExecutionTimeMs = 0f;
            TotalPlacements = InPlaceRotations = Relocations = 0;
            SweepIterations = 0;
            SweepRelocations = 0;
            SolvePhase = "None";
            allLasers = null;
            _searchResult = null;
            _searchDone = false;
            solveStart = searchStart = execStart = 0f;

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

            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;
            allLasers = FindLaserSystems();
            solveStart = Time.realtimeSinceStartup;

            Vector2Int em = FindFirst(TileType.Emitter);
            if (em == -Vector2Int.one)
            {
                Debug.LogError("[AI] No Emitter!");
                execStart = Time.realtimeSinceStartup;
                Finish(false); yield break;
            }

            TeleportTo(em);
            yield return new WaitForSeconds(0.3f);

            if (RealSolved())
            {
                SolvePhase = "Trivial";
                execStart = Time.realtimeSinceStartup;
                Finish(true);
                yield return StartCoroutine(ExitDoor());
                yield break;
            }

            // ── Phase 1: logical search ──
            searchStart = Time.realtimeSinceStartup;
            _searchResult = null;
            _searchDone = false;
            StartCoroutine(RunSearchWithDoneFlag());

            float deadline = Time.realtimeSinceStartup + MAX_SEARCH_SECONDS;
            while (!_searchDone && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (!_searchDone)
                Debug.LogWarning($"[AI] Search timeout after {MAX_SEARCH_SECONDS}s — going to sweep.");

            SearchTimeMs = (Time.realtimeSinceStartup - searchStart) * 1000f;

            // ── Phase 2: physical execution ──
            execStart = Time.realtimeSinceStartup;

            if (_searchResult != null)
                yield return StartCoroutine(ExecutePlan(_searchResult));
            else
            {
                SolvePhase = "Sweep";
                Debug.LogWarning("[AI] Logical search exhausted — sweep fallback.");
                yield return StartCoroutine(CorrectionSweep());
            }

            if (running)
            {
                Debug.LogWarning("[AI] Pipeline ended without Finish() — forcing Finish(false).");
                SolvePhase = "None";
                Finish(false);
            }
        }

        // ════════════════════════════════════════════════════════════
        // LOGICAL SEARCH
        // ════════════════════════════════════════════════════════════
        private List<PlacementAction> _searchResult;
        private bool _searchDone = false;
        private const int YIELD_EVERY = 200;
        private const float MAX_SEARCH_SECONDS = 8f;

        IEnumerator RunSearchWithDoneFlag()
        {
            yield return StartCoroutine(LogicalSearch());
            _searchDone = true;
        }

        IEnumerator LogicalSearch()
        {
            _searchResult = null;
            Vector2Int receiver = FindFirst(TileType.Receiver);
            GridSnapshot initial = SnapshotGrid();

            if (LogicalBeamReachesReceiver(initial, receiver))
            { _searchResult = new List<PlacementAction>(); yield break; }

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

            if (onBeam.Count == 0 && offBeam.Count > 0)
            {
                Debug.LogWarning("[AI] 0 on-beam objects — beam too short. Skipping 1A, going to 1B.");
                yield return StartCoroutine(RelocationSearch(initial, receiver));
                if (_searchResult != null) SolvePhase = "1B";
                yield break;
            }

            if (onBeam.Count > 0)
            {
                yield return StartCoroutine(RotationSearch(initial, onBeam, receiver, "1A-OnBeam"));
                if (_searchResult != null) { SolvePhase = "1A"; yield break; }
            }

            var allObjs = new List<(Vector2Int, TileType)>(onBeam);
            allObjs.AddRange(offBeam);
            if (allObjs.Count > onBeam.Count)
            {
                yield return StartCoroutine(RotationSearch(initial, allObjs, receiver, "1A-Full"));
                if (_searchResult != null) { SolvePhase = "1A"; yield break; }
            }

            Debug.Log("[AI] Phase 1A exhausted — trying relocation search (1B).");
            yield return StartCoroutine(RelocationSearch(initial, receiver));
            if (_searchResult != null) SolvePhase = "1B";
        }

        // ── Rotation Search ───────────────────────────────────────
        IEnumerator RotationSearch(
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
                if (SolveIterations % YIELD_EVERY == 0) yield return null;

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
                    _searchResult = plan;
                    yield break;
                }
            }
        }

        // ── Relocation Search (1B) ────────────────────────────────
        private const int BEAM_WIDTH = 64;

        IEnumerator RelocationSearch(GridSnapshot initial, Vector2Int receiver)
        {
            var frontier = new List<(GridSnapshot snap, List<PlacementAction> plan)>
            {
                (initial, new List<PlacementAction>())
            };

            for (int depth = 0; depth < maxPlanDepth && frontier.Count > 0; depth++)
            {
                var candidates = new List<(GridSnapshot snap, List<PlacementAction> plan, int score)>();

                foreach (var (snap, plan) in frontier)
                {
                    SolveIterations++;
                    if (SolveIterations % YIELD_EVERY == 0) yield return null;
                    if (SolveIterations >= maxSearchNodes)
                    {
                        Debug.LogWarning($"[AI] 1B node limit hit at depth {depth}.");
                        yield break;
                    }

                    var beam = LogicalBeam(snap, receiver);
                    var actions = GenerateRelocateActions(snap, beam, receiver);

                    foreach (var action in actions)
                    {
                        var next = ApplyAction(snap, action);
                        if (LogicalBeamReachesReceiver(next, receiver))
                        {
                            _searchResult = new List<PlacementAction>(plan) { action };
                            Debug.Log($"[AI] 1B (beam): {_searchResult.Count} moves, " +
                                      $"{SolveIterations} nodes, depth {depth + 1}.");
                            yield break;
                        }
                        int score = LogicalBeamScore(next, receiver);
                        candidates.Add((next, new List<PlacementAction>(plan) { action }, score));
                    }
                }

                if (candidates.Count == 0) break;
                candidates.Sort((a, b) => b.score.CompareTo(a.score));
                int keep = Mathf.Min(BEAM_WIDTH, candidates.Count);
                frontier.Clear();
                for (int i = 0; i < keep; i++)
                    frontier.Add((candidates[i].snap, candidates[i].plan));
            }

            Debug.LogWarning($"[AI] 1B exhausted after {SolveIterations} nodes.");
        }

        List<PlacementAction> GenerateRelocateActions(GridSnapshot snap,
                                                      LogicalBeamResult beam,
                                                      Vector2Int receiver)
        {
            var actions = new List<PlacementAction>();
            var objList = snap.objects.Select(kv => (kv.Key, kv.Value.type)).ToList();
            var cands = LogicalCandidates(snap, beam, receiver);

            foreach (var (src, objType) in objList)
                foreach (var tgt in cands)
                {
                    if (tgt == src || snap.objects.ContainsKey(tgt)) continue;
                    var inDir = LogicalIncomingDir(snap, tgt, beam);
                    foreach (int rot in DeflectionRotationsForDir(objType, inDir, receiver - tgt))
                        actions.Add(new PlacementAction
                        { SourceCell = src, TargetCell = tgt, ObjType = objType, Rotation = rot });
                }

            var scored = new List<(PlacementAction action, int score)>(actions.Count);
            foreach (var a in actions)
                scored.Add((a, LogicalBeamScore(ApplyAction(snap, a), receiver)));
            scored.Sort((x, y) => y.score.CompareTo(x.score));

            actions.Clear();
            foreach (var (a, _) in scored) actions.Add(a);
            return actions;
        }

        // ════════════════════════════════════════════════════════════
        // CORRECTION SWEEP v4
        //
        // S1 — Rotate on-beam objects (unchanged from v3, fast)
        //
        // S2 — NEW: Beam-guided relocation
        //   Detects when beam hits no interactable objects at all
        //   (beam too short OR all objects are off-beam).
        //   Picks the best off-beam object, physically moves it to
        //   the nearest empty cell on the beam path, then re-runs S1.
        //   Repeats up to MAX_S2_RELOCATIONS times.
        //   Rationale: rotating an object outside the beam path can
        //   never redirect light — relocation is the only meaningful action.
        //
        // S3 — Exhaustive rotate-all (last resort, kept for completeness)
        //   Only reached when S1 and S2 both fail. Limited to
        //   MAX_S3_OBJECTS to avoid spending minutes on unsolvable states.
        // ════════════════════════════════════════════════════════════


        IEnumerator CorrectionSweep()
        {
            int totalObjs = gridVisualizer.LastTotalObjectCount;
            int MAX_S2_RELOCATIONS = Mathf.Clamp(totalObjs / 3, 2, 6);
            int MAX_S3_OBJECTS = Mathf.Clamp(totalObjs, 4, 10);


            float fastWait = Mathf.Min(physicsWait, 0.08f);
            int[] rots = { 0, 45, 90, 135, 180, 225, 270, 315 };
            Vector2Int receiver = FindFirst(TileType.Receiver);

            var allObjects = GetInteractables();
            if (allObjects.Count == 0)
            {
                Debug.LogWarning("[AI] Sweep: no interactable objects.");
                ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
                Finish(RealSolved());
                yield break;
            }

            // ── S1: rotate on-beam objects ────────────────────────
            Debug.Log("[AI] Sweep S1: on-beam rotation.");
            yield return StartCoroutine(SweepS1(allObjects, receiver, rots, fastWait));
            if (RealSolved()) yield break;

            // ── S2: relocate off-beam object into beam path ───────
            // Triggered when beam contacts no interactable object at all.
            Debug.Log("[AI] Sweep S2: beam-guided relocation of off-beam objects.");
            for (int attempt = 0; attempt < MAX_S2_RELOCATIONS && !RealSolved(); attempt++)
            {
                var snap = SnapshotGrid();
                var beam = LogicalBeam(snap, receiver);

                // Check: are there any on-beam interactable objects?
                bool hasOnBeam = allObjects.Any(c => beam.pathSet.Contains(c));
                if (hasOnBeam)
                {
                    // beam already touches something — re-run S1 instead
                    yield return StartCoroutine(SweepS1(allObjects, receiver, rots, fastWait));
                    if (RealSolved()) yield break;
                    break; // S1 made no progress either — fall through to S3
                }

                // Find empty cells along the beam path to receive a relocated object
                var beamEmptyCells = beam.path
                    .Where(c => grid.GetTile(c.x, c.y) == TileType.Empty
                             && !snap.objects.ContainsKey(c))
                    .OrderBy(c => Manhattan(c, receiver))
                    .ToList();

                if (beamEmptyCells.Count == 0)
                {
                    Debug.LogWarning("[AI] S2: no empty cell on beam path. Falling to S3.");
                    break;
                }

                // Pick off-beam object closest to the best beam cell
                var offBeamObjects = allObjects
                    .Where(c => !beam.pathSet.Contains(c))
                    .ToList();

                if (offBeamObjects.Count == 0) break;

                Vector2Int bestTarget = beamEmptyCells[0];
                Vector2Int bestSrc = offBeamObjects
                    .OrderBy(c => Manhattan(c, bestTarget))
                    .First();

                // Get object type before picking up
                TileType movedType = grid.GetTile(bestSrc.x, bestSrc.y);

                Debug.Log($"[AI] S2 attempt {attempt + 1}: relocate {movedType}@{bestSrc} → {bestTarget}");
                SolveIterations++;
                SweepIterations++;

                // Physical move: walk to source → pickup → walk to target → place
                TeleportTo(bestSrc);
                yield return new WaitForSeconds(fastWait);
                GameObject held = PickupObject(bestSrc);
                TotalPlacements++;
                Relocations++;
                SweepRelocations++;

                TeleportTo(bestTarget);
                yield return new WaitForSeconds(fastWait);

                // Try each rotation at the new position
                bool relocateSolved = false;
                foreach (int rot in rots)
                {
                    SolveIterations++;
                    SweepIterations++;
                    PlaceObject(bestTarget, movedType, held, rot);
                    yield return new WaitForSeconds(fastWait);

                    if (RealSolved())
                    {
                        ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
                        SolvePhase = "Sweep-S2"; // solved via off-beam-object relocation
                        Debug.Log($"[AI] S2 solved! {movedType}@{bestTarget} rot={rot}°");
                        Finish(true);
                        yield return StartCoroutine(ExitDoor());
                        relocateSolved = true;
                        yield break;
                    }
                }

                if (relocateSolved) yield break;

                // Object is now on beam path — update allObjects list and re-run S1
                allObjects.Remove(bestSrc);
                allObjects.Add(bestTarget);
                allObjects = allObjects.Distinct().ToList();

                // Label as Sweep-S2: the solution only became reachable because
                // S2 moved an object onto the beam first.
                yield return StartCoroutine(SweepS1(allObjects, receiver, rots, fastWait, "Sweep-S2"));
                if (RealSolved()) yield break;
            }

            // ── S3: exhaustive rotate-all (last resort) ───────────
            if (!RealSolved())
            {
                // Cap object count to avoid spending minutes on unsolvable states
                var s3Objects = allObjects.Take(MAX_S3_OBJECTS).ToList();
                Debug.Log($"[AI] Sweep S3: exhaustive rotate on {s3Objects.Count} objects (cap={MAX_S3_OBJECTS}).");

                foreach (var objCell in s3Objects)
                {
                    if (RealSolved()) break;
                    TeleportTo(objCell);
                    yield return new WaitForSeconds(fastWait);

                    foreach (int rot in rots)
                    {
                        SolveIterations++;
                        SweepIterations++;
                        if (gridVisualizer.SpawnedObjects.TryGetValue(objCell, out var go) && go != null)
                            go.transform.rotation = Quaternion.Euler(0f, rot, 0f);
                        yield return new WaitForSeconds(fastWait);

                        if (RealSolved())
                        {
                            ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
                            SolvePhase = "Sweep-S3"; // last-resort exhaustive rotation
                            Debug.Log($"[AI] S3 solved! rot={rot}°@{objCell}");
                            Finish(true);
                            yield return StartCoroutine(ExitDoor());
                            yield break;
                        }
                    }
                }
            }

            ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
            if (!RealSolved())
            {
                Debug.LogWarning("[AI] All sweep stages failed.");
                Finish(false);
            }
        }

        // ── S1 helper: rotate on-beam objects (extracted for reuse by S2) ──
        // phaseLabel lets callers distinguish "pure S1" solves from solves that
        // only became possible after S2 relocated an object onto the beam first.
        IEnumerator SweepS1(List<Vector2Int> allObjects,
                            Vector2Int receiver,
                            int[] rots,
                            float fastWait,
                            string phaseLabel = "Sweep-S1")
        {
            for (int pass = 0; pass < 5 && !RealSolved(); pass++)
            {
                var beam = LogicalBeam(SnapshotGrid(), receiver);
                var candidates = allObjects
                    .Where(c => beam.pathSet.Contains(c))
                    .OrderBy(c => Manhattan(c, receiver))
                    .ToList();

                if (candidates.Count == 0) yield break; // no on-beam objects — exit S1

                bool improved = false;
                foreach (var objCell in candidates)
                {
                    if (RealSolved()) yield break;
                    TeleportTo(objCell);
                    yield return new WaitForSeconds(fastWait);

                    int baseScore = ScoreBeamLogical();
                    foreach (int rot in rots)
                    {
                        SolveIterations++;
                        SweepIterations++;
                        if (gridVisualizer.SpawnedObjects.TryGetValue(objCell, out var go) && go != null)
                            go.transform.rotation = Quaternion.Euler(0f, rot, 0f);
                        yield return new WaitForSeconds(fastWait);

                        if (RealSolved())
                        {
                            ExecutionTimeMs = (Time.realtimeSinceStartup - execStart) * 1000f;
                            SolvePhase = phaseLabel;
                            Debug.Log($"[AI] S1 solved! rot={rot}°@{objCell} pass={pass + 1} ({phaseLabel})");
                            Finish(true);
                            yield return StartCoroutine(ExitDoor());
                            yield break;
                        }
                        if (ScoreBeamLogical() > baseScore) { improved = true; break; }
                    }
                }
                if (!improved) yield break;
            }
        }

        // Score current beam using logical grid simulation.
        int ScoreBeamLogical()
        {
            var receiver = FindFirst(TileType.Receiver);
            var b = LogicalBeam(SnapshotGrid(), receiver);
            if (b.hitReceiver) return int.MaxValue;
            return b.path.Count - Manhattan(b.endCell, receiver) * 2;
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 2 — PHYSICAL EXECUTION
        // ════════════════════════════════════════════════════════════
        IEnumerator ExecutePlan(List<PlacementAction> plan)
        {
            Debug.Log($"[AI] Executing plan: {plan.Count} steps.");
            var remaining = new List<PlacementAction>(plan);

            while (remaining.Count > 0)
            {
                if (RealSolved()) break;

                Vector2Int agentCell = WorldToGrid(transform.position);
                int bestIdx = 0, bestDist = int.MaxValue;
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
                    InPlaceRotations++;
                    Debug.Log($"[AI] Rotate {action.ObjType}@{action.TargetCell} → {action.Rotation}°");
                    yield return StartCoroutine(WalkTo(action.TargetCell));
                    if (gridVisualizer.SpawnedObjects.TryGetValue(action.TargetCell, out var goR) && goR != null)
                        goR.transform.rotation = Quaternion.Euler(0f, action.Rotation, 0f);
                    yield return new WaitForSeconds(physicsWait);
                }
                else
                {
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
        // LOGICAL BEAM SIMULATION
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
            var t1 = beam.path
                .Where(c => !snap.objects.ContainsKey(c) &&
                             grid.GetTile(c.x, c.y) == TileType.Empty &&
                             (c.y == receiver.y || c.x == receiver.x) &&
                             LogicalClearLine(snap, c, receiver))
                .OrderBy(c => Manhattan(c, receiver)).ToList();
            if (t1.Count > 0) return t1;

            var t2 = beam.path
                .Where(c => !snap.objects.ContainsKey(c) &&
                             grid.GetTile(c.x, c.y) == TileType.Empty &&
                             (c.y == receiver.y || c.x == receiver.x))
                .OrderBy(c => Manhattan(c, receiver)).ToList();
            if (t2.Count > 0) return t2;

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

        int SnapshotHash(GridSnapshot snap)
        {
            var entries = new List<(int x, int y, int t, int r)>(snap.objects.Count);
            foreach (var kv in snap.objects)
                entries.Add((kv.Key.x, kv.Key.y, (int)kv.Value.type, kv.Value.rot));
            entries.Sort((a, b) => a.x != b.x ? a.x - b.x : a.y != b.y ? a.y - b.y : 0);
            unchecked
            {
                int hash = (int)2166136261;
                foreach (var e in entries)
                {
                    hash ^= e.x; hash *= 16777619;
                    hash ^= e.y; hash *= 16777619;
                    hash ^= e.t; hash *= 16777619;
                    hash ^= e.r; hash *= 16777619;
                }
                return hash;
            }
        }

        string SnapshotKey(GridSnapshot snap)
        {
            var parts = snap.objects
                .Select(kv => $"{kv.Key.x},{kv.Key.y},{(int)kv.Value.type},{kv.Value.rot}")
                .OrderBy(s => s).ToList();
            return string.Join("|", parts);
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
                float timeout = 3f;
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
                if (timeout <= 0f)
                {
                    Debug.LogWarning($"[AI] WalkTo stuck at step {step} — teleporting.");
                    TeleportTo(step);
                    yield return new WaitForEndOfFrame();
                }
            }

            if (Vector3.Distance(transform.position, wt) > 0.5f)
            {
                Debug.LogWarning($"[AI] WalkTo did not reach {target} — teleporting.");
                TeleportTo(target);
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
                    bool logicallyWalkable = (t == TileType.Empty || t == TileType.Door);
                    bool hasPhysicalObject = next != goal &&
                        gridVisualizer.SpawnedObjects.TryGetValue(next, out var obj) &&
                        obj != null && obj.activeSelf;

                    if ((!logicallyWalkable && next != goal) || hasPhysicalObject) continue;

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
            if (Mathf.Abs(Mathf.DeltaAngle(rot, 0f)) <= 5f ||
                Mathf.Abs(Mathf.DeltaAngle(rot, 180f)) <= 5f)
                return d; // pass-through

            return (Mathf.Abs(Mathf.DeltaAngle(rot, 90f)) < 45f ||
                    Mathf.Abs(Mathf.DeltaAngle(rot, 270f)) < 45f)
                ? new Vector2Int(-d.y, d.x)
                : new Vector2Int(d.y, -d.x);
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
        // REAL LASER CHECK
        // ════════════════════════════════════════════════════════════
        bool RealSolved()
        {
            if (allLasers != null)
            {
                bool hasStale = false;
                foreach (var l in allLasers) if (l == null) { hasStale = true; break; }
                if (hasStale) allLasers = null;
            }
            if (allLasers == null || allLasers.Length == 0)
                allLasers = FindLaserSystems();
            if (allLasers.Length == 0) return false;

            int hitting = 0;
            foreach (var l in allLasers)
                if (l != null && l.IsHittingReceiver) hitting++;
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