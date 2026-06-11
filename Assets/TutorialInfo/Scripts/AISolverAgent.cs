using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LightPCG.Core;

namespace LightPCG.Systems
{
    [RequireComponent(typeof(CharacterController))]
    public class AISolverAgent : MonoBehaviour
    {
        [Header("References")]
        public GridVisualizer gridVisualizer;

        [Header("Movement")]
        public float moveSpeed = 8f;
        public float rotationSpeed = 15f;

        [Header("Timing")]
        public float physicsWait = 0.3f;
        public float stepDelay = 0.05f;

        [Header("Limits")]
        public int maxIterations = 300;
        public int maxBacktrackRounds = 20;

        [HideInInspector] public bool WasSolved;
        [HideInInspector] public int SolveIterations;
        [HideInInspector] public float SolveTimeMs;
        [HideInInspector] public int TotalPlacements;
        public System.Action<bool> OnSolveComplete;

        private GridModel grid;
        private float spacing;
        private CharacterController cc;
        private LaserSystem[] allLasers;
        private float solveStart;
        private bool running;

        // Memory: set of (sourceCell, targetCell, rotation) triples already evaluated
        private HashSet<(Vector2Int, Vector2Int, int)> triedSet
            = new HashSet<(Vector2Int, Vector2Int, int)>();

        // Cells where every rotation from every source has been tested — skip entirely
        private HashSet<Vector2Int> exhaustedCells = new HashSet<Vector2Int>();

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0,  1), new Vector2Int(0, -1)
        };

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
            TotalPlacements = 0;
            triedSet.Clear();
            exhaustedCells.Clear();
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

            // Teleport to emitter — disable CC to avoid physics conflicts
            TeleportTo(em);
            yield return new WaitForSeconds(0.3f);

            if (RealSolved()) { Finish(true); yield return StartCoroutine(ExitDoor()); yield break; }

            yield return StartCoroutine(SolveLoop());
        }

        // ════════════════════════════════════════════════════════════
        // MAIN SOLVE LOOP
        // Unified loop that replaces Phase1/2/4.
        // Each iteration picks ONE object, evaluates ALL (cell, rotation)
        // combinations for it, keeps the single best placement if it
        // improves the beam score, then moves on.
        // Falls back to backtrack relocation after exhausting forward passes.
        // ════════════════════════════════════════════════════════════
        IEnumerator SolveLoop()
        {
            Vector2Int receiver = FindFirst(TileType.Receiver);
            Debug.Log($"[AI] SolveLoop start | receiver={receiver}");

            // ── Forward pass: try each object in score-priority order ──
            bool madeProgress = true;
            while (!RealSolved() && SolveIterations < maxIterations && madeProgress)
            {
                madeProgress = false;
                var objects = GetInteractables();

                foreach (var objCell in objects)
                {
                    if (RealSolved()) break;
                    if (SolveIterations >= maxIterations) break;
                    SolveIterations++;

                    // Snapshot score BEFORE touching this object
                    // (object is still on the grid at this point)
                    int baseScore = ScoreBeam(receiver);

                    TileType objType = grid.GetTile(objCell.x, objCell.y);
                    var cells = PrioritisedCells(ObserveBeam(), receiver);

                    // --- Find the single best (cell, rotation) for this object ---
                    Vector2Int bestCell = -Vector2Int.one;
                    int bestRot = 0;
                    int bestScore = baseScore; // must beat current score to keep

                    // Temporarily remove object so beam simulation is unaffected by it
                    var savedGo = PickupObject(objCell);

                    foreach (var targetCell in cells)
                    {
                        if (RealSolved()) break;
                        if (IsExhausted(targetCell)) continue;
                        if (grid.GetTile(targetCell.x, targetCell.y) != TileType.Empty) continue;

                        foreach (int rot in PrioritisedRotations(objType, targetCell, receiver))
                        {
                            if (WasTried(objCell, targetCell, rot)) continue;

                            // Place temporarily on grid (no GameObject move — pure logic)
                            grid.SetTile(targetCell.x, targetCell.y, objType);

                            // Rotate the GO in-memory so ObserveBeam reads the right direction
                            if (savedGo != null) savedGo.transform.rotation = Quaternion.Euler(0f, rot, 0f);

                            int score = ScoreBeamWithObject(targetCell, objType, rot, receiver);
                            grid.SetTile(targetCell.x, targetCell.y, TileType.Empty);

                            MarkTried(objCell, targetCell, rot);
                            TotalPlacements++;

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestCell = targetCell;
                                bestRot = rot;
                            }
                        }
                    }

                    if (bestCell != -Vector2Int.one)
                    {
                        // Commit the best placement physically
                        yield return StartCoroutine(WalkTo(bestCell));
                        PlaceObject(bestCell, objType, savedGo, bestRot);
                        yield return new WaitForSeconds(physicsWait);

                        if (RealSolved())
                        {
                            Debug.Log($"[AI] SOLVED | {objType}@{bestCell} rot={bestRot}");
                            Finish(true);
                            yield return StartCoroutine(ExitDoor());
                            yield break;
                        }

                        madeProgress = true;
                        Debug.Log($"[AI] Placed {objType}@{bestCell} rot={bestRot} " +
                                  $"score {baseScore}->{bestScore}");
                    }
                    else
                    {
                        // No improvement found — restore object to original cell
                        yield return StartCoroutine(WalkTo(objCell));
                        PlaceObject(objCell, objType, savedGo, 0);
                        yield return new WaitForSeconds(physicsWait);
                        Debug.Log($"[AI] No gain for {objType}@{objCell} — restored");
                    }

                    yield return null; // yield between objects so Unity doesn't freeze
                }
            }

            if (!RealSolved())
                yield return StartCoroutine(BacktrackLoop(receiver));
        }

        // ════════════════════════════════════════════════════════════
        // BACKTRACK LOOP
        // When the forward pass stalls, pick the least-useful object,
        // clear its tried-memory, and force it to a new location.
        // Also re-rotates every object in place each round (4A).
        // ════════════════════════════════════════════════════════════
        IEnumerator BacktrackLoop(Vector2Int receiver)
        {
            Debug.Log("[AI] BacktrackLoop start");

            for (int round = 0;
                 round < maxBacktrackRounds && !RealSolved() && SolveIterations < maxIterations;
                 round++)
            {
                SolveIterations++;
                Debug.Log($"[AI] Backtrack round {round + 1}/{maxBacktrackRounds}");

                // 4A — re-rotate every object in its current position
                foreach (var objCell in GetInteractables())
                {
                    if (RealSolved()) break;
                    var objType = grid.GetTile(objCell.x, objCell.y);
                    yield return StartCoroutine(WalkTo(objCell));

                    foreach (int rot in AllRotations())
                    {
                        if (WasTried(objCell, objCell, rot)) continue;

                        if (gridVisualizer.SpawnedObjects.TryGetValue(objCell, out var goR) && goR != null)
                            goR.transform.rotation = Quaternion.Euler(0f, rot, 0f);

                        yield return new WaitForSeconds(physicsWait);
                        MarkTried(objCell, objCell, rot);

                        if (RealSolved())
                        {
                            Debug.Log("[AI] SOLVED BacktrackLoop 4A!");
                            Finish(true);
                            yield return StartCoroutine(ExitDoor());
                            yield break;
                        }
                    }
                }

                // 4B — relocate the object that contributes least to current beam
                if (!RealSolved())
                {
                    var worstCell = PickObjectToRelocate(receiver);
                    if (worstCell == -Vector2Int.one) break;

                    var worstType = grid.GetTile(worstCell.x, worstCell.y);
                    var beam = ObserveBeam();
                    var cells = PrioritisedCells(beam, receiver);

                    // Clear this object's tried-memory so it can try positions again
                    ClearTriedFor(worstCell);

                    yield return StartCoroutine(WalkTo(worstCell));
                    var savedGo = PickupObject(worstCell);

                    bool relocated = false;
                    int baseScore = ScoreBeam(receiver);

                    foreach (var newTarget in cells)
                    {
                        if (newTarget == worstCell) continue;
                        if (grid.GetTile(newTarget.x, newTarget.y) != TileType.Empty) continue;
                        if (IsExhausted(newTarget)) continue;

                        foreach (int rot in PrioritisedRotations(worstType, newTarget, receiver))
                        {
                            if (WasTried(worstCell, newTarget, rot)) continue;

                            yield return StartCoroutine(WalkTo(newTarget));
                            PlaceObject(newTarget, worstType, savedGo, rot);
                            TotalPlacements++;
                            yield return new WaitForSeconds(physicsWait);
                            MarkTried(worstCell, newTarget, rot);

                            if (RealSolved())
                            {
                                Debug.Log("[AI] SOLVED BacktrackLoop 4B!");
                                Finish(true);
                                yield return StartCoroutine(ExitDoor());
                                yield break;
                            }

                            int newScore = ScoreBeam(receiver);
                            if (newScore > baseScore)
                            {
                                relocated = true;
                                Debug.Log($"[AI] 4B placed {worstType}@{newTarget} " +
                                          $"rot={rot} score {baseScore}->{newScore}");
                                savedGo = null; // now owned by the grid
                                break;
                            }

                            // Not better — pick back up and try next rotation
                            savedGo = PickupObject(newTarget);
                        }
                        if (relocated || savedGo == null) break;
                    }

                    // Restore if no better location was found
                    if (!relocated && savedGo != null)
                    {
                        yield return StartCoroutine(WalkTo(worstCell));
                        PlaceObject(worstCell, worstType, savedGo, 0);
                        yield return new WaitForSeconds(physicsWait);
                        Debug.Log($"[AI] 4B restored {worstType} -> {worstCell}");
                    }
                }
            }

            if (!RealSolved())
            {
                Debug.LogWarning($"[AI] Failed | iters={SolveIterations} placements={TotalPlacements}");
                Finish(false);
            }
        }

        // ════════════════════════════════════════════════════════════
        // BEAM SCORE
        // Evaluates the grid-logic beam.
        // ScoreBeamWithObject: temporarily places an object on the grid
        // (GameObject already has the right rotation set) then scores.
        // ════════════════════════════════════════════════════════════
        int ScoreBeam(Vector2Int receiver)
        {
            if (RealSolved()) return int.MaxValue;
            var b = ObserveBeam();
            return b.pathLength - Manhattan(b.endCell, receiver) * 2;
        }

        int ScoreBeamWithObject(Vector2Int cell, TileType type, int rot, Vector2Int receiver)
        {
            // Grid tile is already set by caller; just score
            var b = ObserveBeam();
            return b.pathLength - Manhattan(b.endCell, receiver) * 2;
        }

        // ════════════════════════════════════════════════════════════
        // WALK TO — BFS pathfinding with teleport fallback
        // ════════════════════════════════════════════════════════════
        IEnumerator WalkTo(Vector2Int target)
        {
            if (target == -Vector2Int.one || !InB(target)) yield break;

            Vector3 worldTarget = gridVisualizer.GridToWorld(target.x, target.y);
            if (Vector3.Distance(transform.position, worldTarget) < 0.2f) yield break;

            var path = BFS(WorldToGrid(transform.position), target);
            if (path == null || path.Count == 0)
            {
                Debug.LogWarning($"[AI] BFS no path -> {target}; teleporting.");
                TeleportTo(target);
                yield break;
            }

            foreach (var step in path)
            {
                Vector3 wt = gridVisualizer.GridToWorld(step.x, step.y);
                wt.y = transform.position.y;

                float timeout = 5f;
                while (timeout > 0f)
                {
                    timeout -= Time.deltaTime;
                    Vector3 toTarget = wt - transform.position;
                    toTarget.y = 0f;
                    if (toTarget.magnitude < 0.15f) break;
                    if (toTarget.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(toTarget), rotationSpeed * Time.deltaTime);
                    cc.SimpleMove(toTarget.normalized * moveSpeed);
                    yield return null;
                }
            }
        }

        void TeleportTo(Vector2Int cell)
        {
            cc.enabled = false;
            Vector3 p = gridVisualizer.GridToWorld(cell.x, cell.y);
            p.y = 0.5f;
            transform.position = p;
            cc.enabled = true;
        }

        // ════════════════════════════════════════════════════════════
        // BFS PATHFINDING
        // ════════════════════════════════════════════════════════════
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
                    var t = grid.GetTile(next.x, next.y);
                    if (t != TileType.Empty && t != TileType.Door && next != goal) continue;

                    visited.Add(next);
                    parent[next] = cur;

                    if (next == goal)
                    {
                        var p = new List<Vector2Int>();
                        for (var c = goal; c != start; c = parent[c]) p.Add(c);
                        p.Reverse();
                        return p;
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
            if (dc == -Vector2Int.one) { Debug.Log("[AI] Exited (no door)."); yield break; }

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
        // BEAM OBSERVATION (grid-logic laser simulation)
        // ════════════════════════════════════════════════════════════
        struct BeamState
        {
            public List<Vector2Int> emptyCells;
            public int pathLength;
            public Vector2Int endCell;
        }

        BeamState ObserveBeam()
        {
            var s = new BeamState
            { emptyCells = new List<Vector2Int>(), pathLength = 0, endCell = Vector2Int.zero };

            for (int ex = 0; ex < grid.Width; ex++)
                for (int ey = 0; ey < grid.Height; ey++)
                {
                    if (grid.GetTile(ex, ey) != TileType.Emitter) continue;
                    var pos = new Vector2Int(ex, ey);
                    var dir = EmDir(pos);
                    var seen = new HashSet<(Vector2Int, Vector2Int)>();

                    for (int i = 0; i < grid.Width * grid.Height * 2; i++)
                    {
                        pos += dir;
                        if (!InB(pos)) break;
                        var st = (pos, dir);
                        if (seen.Contains(st)) break;
                        seen.Add(st);

                        s.pathLength++;
                        s.endCell = pos;

                        var t = grid.GetTile(pos.x, pos.y);
                        if (t == TileType.Empty && !s.emptyCells.Contains(pos))
                            s.emptyCells.Add(pos);
                        if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                        if (t == TileType.Mirror) { dir = GridBounce(dir, pos); continue; }
                        if (t == TileType.Refractor) { dir = GridRefract(dir, pos); continue; }
                    }
                }
            return s;
        }

        // ════════════════════════════════════════════════════════════
        // SELECTION HELPERS
        // ════════════════════════════════════════════════════════════

        // Candidate cells sorted by proximity to receiver:
        // (1) cells on the current beam path, (2) same row/col as beam end or receiver,
        // (3) anything within half the grid width.
        List<Vector2Int> PrioritisedCells(BeamState beam, Vector2Int receiver)
        {
            var result = new List<Vector2Int>();

            var sorted = new List<Vector2Int>(beam.emptyCells);
            sorted.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            foreach (var c in sorted) if (!IsExhausted(c)) result.Add(c);
            if (result.Count > 0) return result;

            var endCell = beam.endCell;
            for (int x = 1; x < grid.Width - 1; x++)
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Empty) continue;
                    var v = new Vector2Int(x, y);
                    if (IsExhausted(v)) continue;
                    if (y == endCell.y || x == endCell.x || y == receiver.y || x == receiver.x)
                        result.Add(v);
                }
            result.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            if (result.Count > 0) return result;

            int threshold = Mathf.Max(grid.Width, grid.Height) / 2;
            for (int x = 1; x < grid.Width - 1; x++)
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Empty) continue;
                    var v = new Vector2Int(x, y);
                    if (!IsExhausted(v) && Manhattan(v, receiver) <= threshold) result.Add(v);
                }
            result.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            return result;
        }

        // Choose the object that contributes least to the current beam length
        Vector2Int PickObjectToRelocate(Vector2Int receiver)
        {
            var list = GetInteractables();
            if (list.Count == 0) return -Vector2Int.one;

            var beam = ObserveBeam();
            var onBeam = new List<Vector2Int>();
            var offBeam = new List<Vector2Int>();

            foreach (var obj in list)
            {
                TileType saved = grid.GetTile(obj.x, obj.y);
                grid.SetTile(obj.x, obj.y, TileType.Empty);
                bool useful = ObserveBeam().pathLength < beam.pathLength;
                grid.SetTile(obj.x, obj.y, saved);
                if (useful) onBeam.Add(obj); else offBeam.Add(obj);
            }

            var candidates = offBeam.Count > 0 ? offBeam : onBeam;
            candidates.Sort((a, b) => Manhattan(b, receiver).CompareTo(Manhattan(a, receiver)));
            return candidates[0];
        }

        // Rotations ordered by likelihood of deflecting toward the receiver
        List<int> PrioritisedRotations(TileType t, Vector2Int cell, Vector2Int receiver)
        {
            var d = receiver - cell;
            bool rx = Mathf.Abs(d.x) > Mathf.Abs(d.y);
            if (t == TileType.Mirror)
                return rx ? new List<int> { 45, 225, 135, 315, 0, 90, 180, 270 }
                          : new List<int> { 135, 315, 45, 225, 0, 90, 180, 270 };
            return rx ? new List<int> { 0, 180, 90, 270, 45, 135, 225, 315 }
                          : new List<int> { 90, 270, 0, 180, 45, 135, 225, 315 };
        }

        List<int> AllRotations() => new List<int> { 0, 45, 90, 135, 180, 225, 270, 315 };

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
        // MEMORY
        // ════════════════════════════════════════════════════════════
        void MarkTried(Vector2Int from, Vector2Int to, int rot)
        {
            triedSet.Add((from, to, rot));

            // Check if all 8 rotations from any source to this cell have been tried
            bool allDone = true;
            foreach (int r in AllRotations())
            {
                bool found = false;
                // We only need at least one source to have tried this (to, r) combo
                for (int x = 0; x < grid.Width && !found; x++)
                    for (int y = 0; y < grid.Height && !found; y++)
                        if (triedSet.Contains((new Vector2Int(x, y), to, r))) found = true;
                if (!found) { allDone = false; break; }
            }
            if (allDone) exhaustedCells.Add(to);
        }

        bool WasTried(Vector2Int from, Vector2Int to, int rot)
            => triedSet.Contains((from, to, rot));

        // Remove all memory entries for a given source cell (used in backtrack reset)
        void ClearTriedFor(Vector2Int from)
        {
            var toRemove = new List<(Vector2Int, Vector2Int, int)>();
            foreach (var entry in triedSet)
                if (entry.Item1 == from) toRemove.Add(entry);
            foreach (var e in toRemove) triedSet.Remove(e);

            // Also unmark any cells that were exhausted due to this source
            exhaustedCells.Clear(); // simplest safe approach: recompute on demand
        }

        bool IsExhausted(Vector2Int cell) => exhaustedCells.Contains(cell);

        // ════════════════════════════════════════════════════════════
        // FINISH
        // ════════════════════════════════════════════════════════════
        void Finish(bool solved)
        {
            WasSolved = solved;
            SolveTimeMs = (Time.realtimeSinceStartup - solveStart) * 1000f;
            running = false;
            OnSolveComplete?.Invoke(solved);
        }

        // ════════════════════════════════════════════════════════════
        // REAL LASER CHECK — uses Unity physics (LaserSystem raycasts)
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
        // GRID MATH
        // ════════════════════════════════════════════════════════════

        // Reflect beam direction off a mirror based on its Y rotation
        Vector2Int GridBounce(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 45f)) < 22.5f ||
                    Mathf.Abs(Mathf.DeltaAngle(a, 225f)) < 22.5f)
                ? new Vector2Int(d.y, d.x) : new Vector2Int(-d.y, -d.x);
        }

        // Deflect beam direction through a refractor based on its Y rotation
        Vector2Int GridRefract(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
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

        // Determine emitter facing direction from its transform or open neighbours
        Vector2Int EmDir(Vector2Int cell)
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
                var n = cell + d;
                if (!InB(n)) continue;
                var t = grid.GetTile(n.x, n.y);
                if (t != TileType.Wall && t != TileType.Door && t != TileType.Emitter) return d;
            }
            return Vector2Int.right;
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
                    var t = grid.GetTile(x, y);
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