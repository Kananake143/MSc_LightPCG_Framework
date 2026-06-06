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
        public float physicsWait = 0.15f;
        public float stepDelay = 0.05f;

        [Header("Limits")]
        public int maxBacktrackRounds = 5;

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

        private Dictionary<Vector2Int, HashSet<(Vector2Int, int)>> memory
            = new Dictionary<Vector2Int, HashSet<(Vector2Int, int)>>();

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0,1), new Vector2Int(0,-1)
        };

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.radius = 0.28f; cc.height = 1.0f;
            cc.center = new Vector3(0, 0.5f, 0); cc.minMoveDistance = 0f;
        }

        void Start()
        {
            if (gridVisualizer == null)
                gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (gridVisualizer == null) { Debug.LogError("[AI] GridVisualizer not found!"); return; }
            StartSolve();
        }

        public void StartSolve()
        {
            if (running) return;
            running = true;
            WasSolved = false; SolveIterations = 0; TotalPlacements = 0;
            memory.Clear();
            StartCoroutine(Pipeline());
        }

        // ════════════════════════════════════════════════════════════════
        // PIPELINE
        // ════════════════════════════════════════════════════════════════
        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.5f);

            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;
            allLasers = FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);
            solveStart = Time.realtimeSinceStartup;

            yield return StartCoroutine(Phase1_Scan());
        }

        // ════════════════════════════════════════════════════════════════
        // PHASE 1 — SCAN: locate Emitter & Receiver
        // ════════════════════════════════════════════════════════════════
        IEnumerator Phase1_Scan()
        {
            Vector2Int emitter = FindFirst(TileType.Emitter);
            Vector2Int receiver = FindFirst(TileType.Receiver);
            Debug.Log($"[AI] Phase 1 SCAN | Emitter:{emitter} Receiver:{receiver}");

            if (emitter == -Vector2Int.one || receiver == -Vector2Int.one)
            { Debug.LogError("[AI] Missing Emitter or Receiver!"); Finish(false); yield break; }

            cc.enabled = false;
            transform.position = gridVisualizer.GridToWorld(emitter.x, emitter.y);
            cc.enabled = true;
            yield return new WaitForSeconds(physicsWait);

            if (RealSolved())
            {
                Debug.Log("[AI] Already solved by PCG.");
                Finish(true); yield return StartCoroutine(ExitDoor()); yield break;
            }

            yield return StartCoroutine(Phase2_PlaceChain());
        }

        // ════════════════════════════════════════════════════════════════
        // PHASE 2+3 — PLACE & CHAIN: place objects one by one
        // ════════════════════════════════════════════════════════════════
        IEnumerator Phase2_PlaceChain()
        {
            Debug.Log("[AI] Phase 2+3 PLACE & CHAIN");
            var receiver = FindFirst(TileType.Receiver);
            var objects = GetInteractables();
            int idx = 0;

            foreach (var objCell in objects)
            {
                idx++;
                SolveIterations++;
                if (RealSolved()) break;

                BeamState beam = ObserveBeam();
                Vector2Int target = BestTargetCell(beam, receiver);
                if (target == -Vector2Int.one) continue;

                var objType = grid.GetTile(objCell.x, objCell.y);
                Debug.Log($"[AI] Phase {(idx == 1 ? 2 : 3)}: {objType} {objCell}→{target}");

                // Walk to object, pick up, carry to target, try rotations
                yield return StartCoroutine(WalkTo(objCell));
                yield return new WaitForSeconds(stepDelay);

                var go = PickupObject(objCell);
                yield return StartCoroutine(WalkTo(target));

                bool kept = false;
                int prevLen = BeamLength();

                foreach (int rot in PrioritisedRotations(objType, target, receiver))
                {
                    if (WasTried(objCell, target, rot)) continue;

                    PlaceObject(target, objType, go, rot);
                    TotalPlacements++;
                    yield return new WaitForSeconds(physicsWait);
                    RememberTried(objCell, target, rot);

                    if (RealSolved())
                    {
                        Debug.Log($"[AI] ✓ SOLVED Phase {(idx == 1 ? 2 : 3)}!");
                        Finish(true); yield return StartCoroutine(ExitDoor()); yield break;
                    }

                    if (BeamLength() > prevLen) { kept = true; break; }

                    // Rotation didn't help — reset rotation, try next
                    if (gridVisualizer.SpawnedObjects.TryGetValue(target, out var g) && g != null)
                        g.transform.rotation = Quaternion.identity;
                }

                if (!kept)
                {
                    // Nothing worked — restore to original cell
                    var rgo = PickupObject(target);
                    yield return StartCoroutine(WalkTo(objCell));
                    PlaceObject(objCell, objType, rgo, 0);
                    yield return new WaitForSeconds(physicsWait);
                }
            }

            if (!RealSolved())
                yield return StartCoroutine(Phase4_Backtrack());
        }

        // ════════════════════════════════════════════════════════════════
        // PHASE 4 — BACKTRACK
        // 4A: re-rotate placed objects
        // 4B: relocate least-useful object
        // ════════════════════════════════════════════════════════════════
        IEnumerator Phase4_Backtrack()
        {
            Debug.Log("[AI] Phase 4 BACKTRACK");
            var receiver = FindFirst(TileType.Receiver);

            for (int round = 0; round < maxBacktrackRounds && !RealSolved(); round++)
            {
                SolveIterations++;
                Debug.Log($"[AI] Backtrack round {round + 1}");
                bool improved = false;

                // ── 4A: re-rotate each object ─────────────────────────
                foreach (var objCell in GetInteractables())
                {
                    if (RealSolved()) break;
                    yield return StartCoroutine(WalkTo(objCell));

                    var objType = grid.GetTile(objCell.x, objCell.y);
                    int prevLen = BeamLength();

                    foreach (int rot in PrioritisedRotations(objType, objCell, receiver))
                    {
                        if (WasTried(objCell, objCell, rot)) continue;

                        if (gridVisualizer.SpawnedObjects.TryGetValue(objCell, out var go) && go != null)
                            go.transform.rotation = Quaternion.Euler(0f, rot, 0f);

                        yield return new WaitForSeconds(physicsWait);
                        RememberTried(objCell, objCell, rot);

                        if (RealSolved())
                        {
                            Debug.Log($"[AI] ✓ SOLVED Phase 4A!");
                            Finish(true); yield return StartCoroutine(ExitDoor()); yield break;
                        }

                        if (BeamLength() > prevLen) { prevLen = BeamLength(); improved = true; }
                    }
                }

                // ── 4B: relocate least-useful object ─────────────────
                if (!improved || round > 0)
                {
                    var beam = ObserveBeam();
                    var objCell = PickObjectToRelocate(receiver);
                    if (objCell == -Vector2Int.one) break;

                    var objType = grid.GetTile(objCell.x, objCell.y);
                    var newTarget = BestTargetCell(beam, receiver);
                    if (newTarget == -Vector2Int.one || newTarget == objCell) continue;

                    Debug.Log($"[AI] Phase 4B: relocate {objType} {objCell}→{newTarget}");

                    yield return StartCoroutine(WalkTo(objCell));
                    var go_ = PickupObject(objCell);
                    yield return StartCoroutine(WalkTo(newTarget));

                    bool foundRot = false;
                    foreach (int rot in PrioritisedRotations(objType, newTarget, receiver))
                    {
                        PlaceObject(newTarget, objType, go_, rot);
                        TotalPlacements++;
                        yield return new WaitForSeconds(physicsWait);
                        RememberTried(objCell, newTarget, rot);

                        if (RealSolved())
                        {
                            Debug.Log("[AI] ✓ SOLVED Phase 4B!");
                            Finish(true); yield return StartCoroutine(ExitDoor()); yield break;
                        }

                        if (BeamLength() > beam.pathLength) { foundRot = true; break; }

                        if (gridVisualizer.SpawnedObjects.TryGetValue(newTarget, out var g) && g != null)
                            g.transform.rotation = Quaternion.identity;
                    }

                    if (!foundRot)
                    {
                        var rgo = PickupObject(newTarget);
                        yield return StartCoroutine(WalkTo(objCell));
                        PlaceObject(objCell, objType, rgo, 0);
                        yield return new WaitForSeconds(physicsWait);
                    }
                }
            }

            if (!RealSolved())
            {
                Debug.LogWarning($"[AI] Failed | iters={SolveIterations} placements={TotalPlacements}");
                Finish(false);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // BEAM OBSERVATION
        // ════════════════════════════════════════════════════════════════
        struct BeamState
        {
            public List<Vector2Int> emptyCells;
            public int pathLength;
        }

        BeamState ObserveBeam()
        {
            var s = new BeamState { emptyCells = new List<Vector2Int>(), pathLength = 0 };
            for (int ex = 0; ex < grid.Width; ex++) for (int ey = 0; ey < grid.Height; ey++)
                {
                    if (grid.GetTile(ex, ey) != TileType.Emitter) continue;
                    var pos = new Vector2Int(ex, ey); var dir = EmDir(pos);
                    var seen = new HashSet<(Vector2Int, Vector2Int)>();
                    for (int i = 0; i < grid.Width * grid.Height * 2; i++)
                    {
                        pos += dir; if (!InB(pos)) break;
                        var st = (pos, dir); if (seen.Contains(st)) break; seen.Add(st);
                        s.pathLength++;
                        var t = grid.GetTile(pos.x, pos.y);
                        if (t == TileType.Empty && !s.emptyCells.Contains(pos)) s.emptyCells.Add(pos);
                        if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                        if (t == TileType.Mirror) { dir = GridBounce(dir, pos); continue; }
                        if (t == TileType.Refractor) { dir = GridRefract(dir, pos); continue; }
                    }
                }
            return s;
        }

        int BeamLength() => ObserveBeam().pathLength;

        // ════════════════════════════════════════════════════════════════
        // SELECTION HELPERS
        // ════════════════════════════════════════════════════════════════
        Vector2Int BestTargetCell(BeamState beam, Vector2Int receiver)
        {
            if (beam.emptyCells.Count == 0) return -Vector2Int.one;
            var sorted = new List<Vector2Int>(beam.emptyCells);
            sorted.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            foreach (var c in sorted) if (!IsExhausted(c)) return c;
            return -Vector2Int.one;
        }

        Vector2Int PickObjectToRelocate(Vector2Int receiver)
        {
            var list = GetInteractables();
            if (list.Count == 0) return -Vector2Int.one;
            list.Sort((a, b) => Manhattan(b, receiver).CompareTo(Manhattan(a, receiver)));
            return list[0];
        }

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

        // ════════════════════════════════════════════════════════════════
        // ATOMIC PICKUP / PLACE
        // ════════════════════════════════════════════════════════════════
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
            Vector3 wp = gridVisualizer.GridToWorld(cell.x, cell.y);
            grid.SetTile(cell.x, cell.y, type);
            gridVisualizer.SpawnedObjects[cell] = go;
            if (go != null)
            {
                go.transform.position = wp;
                go.transform.rotation = Quaternion.Euler(0f, rotDeg, 0f);
                go.SetActive(true);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // MEMORY
        // ════════════════════════════════════════════════════════════════
        void RememberTried(Vector2Int from, Vector2Int to, int rot)
        {
            if (!memory.ContainsKey(from)) memory[from] = new HashSet<(Vector2Int, int)>();
            memory[from].Add((to, rot));
        }

        bool WasTried(Vector2Int from, Vector2Int to, int rot)
        => memory.ContainsKey(from) && memory[from].Contains((to, rot));

        bool IsExhausted(Vector2Int cell)
        {
            foreach (var obj in GetInteractables())
                foreach (int r in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
                    if (!WasTried(obj, cell, r)) return false;
            return true;
        }

        // ════════════════════════════════════════════════════════════════
        // FINISH + EXIT
        // ════════════════════════════════════════════════════════════════
        void Finish(bool solved)
        {
            WasSolved = solved;
            SolveTimeMs = (Time.realtimeSinceStartup - solveStart) * 1000f;
            running = false; OnSolveComplete?.Invoke(solved);
        }

        IEnumerator ExitDoor()
        {
            yield return new WaitForSeconds(0.4f);
            Vector2Int dc = FindFirst(TileType.Door);
            if (dc != -Vector2Int.one)
            {
                grid.SetTile(dc.x, dc.y, TileType.Empty);
                if (gridVisualizer.SpawnedObjects.TryGetValue(dc, out var dgo) && dgo != null)
                    Destroy(dgo);
                gridVisualizer.SpawnedObjects.Remove(dc);
                yield return StartCoroutine(WalkTo(dc));
                var bw = gridVisualizer.GridToWorld((dc + OutDir(dc)).x, (dc + OutDir(dc)).y);
                float to = 3f;
                while (Vector3.Distance(transform.position, bw) > 0.2f && to > 0)
                {
                    to -= Time.deltaTime;
                    cc.Move((bw - transform.position).normalized * moveSpeed * Time.deltaTime
                        + Vector3.down * 2f * Time.deltaTime); yield return null;
                }
            }
            Debug.Log("[AI] Exited!");
        }

        Vector2Int OutDir(Vector2Int c)
        {
            int cx = grid.Width / 2, cy = grid.Height / 2;
            return Mathf.Abs(cx - c.x) > Mathf.Abs(cy - c.y)
                ? new Vector2Int((int)Mathf.Sign(c.x - cx), 0)
                : new Vector2Int(0, (int)Mathf.Sign(c.y - cy));
        }

        // ════════════════════════════════════════════════════════════════
        // BFS MOVEMENT
        // ════════════════════════════════════════════════════════════════
        IEnumerator WalkTo(Vector2Int target)
        {
            if (target == -Vector2Int.one) yield break;
            var path = BFS(WorldToGrid(transform.position), target);
            if (path == null || path.Count == 0) yield break;
            foreach (var step in path)
            {
                var wt = gridVisualizer.GridToWorld(step.x, step.y); wt.y = transform.position.y;
                float to = 5f;
                while (Vector3.Distance(transform.position, wt) > 0.1f && to > 0)
                {
                    to -= Time.deltaTime;
                    var d = (wt - transform.position).normalized;
                    if (d.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(d), rotationSpeed * Time.deltaTime);
                    cc.Move(d * moveSpeed * Time.deltaTime + Vector3.down * 2f * Time.deltaTime);
                    yield return null;
                }
            }
        }

        List<Vector2Int> BFS(Vector2Int start, Vector2Int goal)
        {
            if (start == goal) return new List<Vector2Int>();
            var visited = new HashSet<Vector2Int> { start };
            var parent = new Dictionary<Vector2Int, Vector2Int>();
            var queue = new Queue<Vector2Int>(); queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var d in Dirs4)
                {
                    var next = cur + d; if (visited.Contains(next)) continue;
                    var t = grid.GetTile(next.x, next.y);
                    if (t != TileType.Empty && t != TileType.Door && next != goal) continue;
                    visited.Add(next); parent[next] = cur;
                    if (next == goal)
                    {
                        var p = new List<Vector2Int>();
                        for (var c = goal; c != start; c = parent[c]) p.Add(c); p.Reverse(); return p;
                    }
                    queue.Enqueue(next);
                }
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════════
        // REAL LASER + GRID MATH
        // ════════════════════════════════════════════════════════════════
        bool RealSolved()
        {
            if (allLasers == null || allLasers.Length == 0)
                allLasers = FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);
            foreach (var l in allLasers) if (l != null && l.IsHittingReceiver) return true;
            return false;
        }

        Vector2Int GridBounce(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 45f)) < 22.5f || Mathf.Abs(Mathf.DeltaAngle(a, 225f)) < 22.5f)
                ? new Vector2Int(d.y, d.x) : new Vector2Int(-d.y, -d.x);
        }

        Vector2Int GridRefract(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 0f)) < 22.5f || Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22.5f)
                ? new Vector2Int(-d.y, d.x) : new Vector2Int(d.y, -d.x);
        }

        float GetYRot(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out var obj) && obj != null)
                return obj.transform.eulerAngles.y; return 0f;
        }

        Vector2Int EmDir(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out var go) && go != null)
            {
                var f = go.transform.forward;
                if (Mathf.Abs(f.x) >= Mathf.Abs(f.z)) return f.x >= 0 ? Vector2Int.right : Vector2Int.left;
                return f.z >= 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);
            }
            foreach (var d in Dirs4)
            {
                var n = cell + d; if (!InB(n)) continue;
                var t = grid.GetTile(n.x, n.y);
                if (t != TileType.Wall && t != TileType.Door && t != TileType.Emitter) return d;
            }
            return Vector2Int.right;
        }

        List<Vector2Int> GetInteractables()
        {
            var l = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    var t = grid.GetTile(x, y);
                    if (t == TileType.Mirror || t == TileType.Refractor) l.Add(new Vector2Int(x, y));
                }
            return l;
        }

        Vector2Int FindFirst(TileType target)
        {
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == target) return new Vector2Int(x, y);
            return -Vector2Int.one;
        }

        int Manhattan(Vector2Int a, Vector2Int b) => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

        Vector2Int WorldToGrid(Vector3 w)
        {
            float ox = (grid.Width - 1) * spacing / 2f, oz = (grid.Height - 1) * spacing / 2f;
            return new Vector2Int(Mathf.RoundToInt((w.x + ox) / spacing), Mathf.RoundToInt((w.z + oz) / spacing));
        }

        bool InB(Vector2Int p) => p.x >= 0 && p.x < grid.Width && p.y >= 0 && p.y < grid.Height;
    }
}
