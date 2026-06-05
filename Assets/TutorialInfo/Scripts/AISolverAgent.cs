using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LightPCG.Core;

namespace LightPCG.Systems
{
    /// <summary>
    /// AI Solver v13
    /// Key fixes:
    ///   1. Stops rotating immediately when laser hits Receiver
    ///      (checks LaserSystem.IsHittingReceiver — the real physics laser)
    ///   2. PuzzleSolved() checks BOTH grid math AND real laser state
    ///   3. No door logic here — ReceiverDetector handles door opening
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class AISolverAgent : MonoBehaviour
    {
        [Header("References")]
        public GridVisualizer gridVisualizer;

        [Header("Movement")]
        public float moveSpeed = 5f;
        public float rotationSpeed = 12f;

        [Header("Pacing")]
        public float actionDelay = 0.25f;

        private GridModel grid;
        private float spacing;
        private CharacterController cc;

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0,1), new Vector2Int(0,-1)
        };

        private Dictionary<Vector2Int, float> _rotOverride = new Dictionary<Vector2Int, float>();

        // Cache all LaserSystem components so we can check real hit state
        private LaserSystem[] allLasers;

        struct PlannedMove
        {
            public Vector2Int from, to;
            public int rot;
            public bool found, isSolution;
        }

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.radius = 0.28f; cc.height = 1.0f;
            cc.center = new Vector3(0, 0.5f, 0);
            cc.minMoveDistance = 0f;
        }

        void Start()
        {
            if (gridVisualizer == null)
                gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (gridVisualizer == null)
            { Debug.LogError("[AI Solver] GridVisualizer not found!"); return; }
            StartCoroutine(Pipeline());
        }

       
        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.8f);

            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;

            // Cache laser systems
            allLasers = FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);

            Vector2Int emitter = FindFirst(TileType.Emitter);
            if (emitter == -Vector2Int.one) { Debug.LogError("[AI Solver] No Emitter!"); yield break; }

            cc.enabled = false;
            transform.position = gridVisualizer.GridToWorld(emitter.x, emitter.y);
            cc.enabled = true;

            if (GridPuzzleSolved())
            {
                Debug.Log("[AI Solver] Already solved.");
                yield return StartCoroutine(WalkThroughDoor());
                yield break;
            }

            yield return StartCoroutine(PlanAndExecute());
        }

        
        // PLAN AND EXECUTE
        
        IEnumerator PlanAndExecute()
        {
            int maxPasses = 4;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                // Check real laser state first
                if (RealLaserHittingReceiver())
                {
                    Debug.Log("[AI Solver] ✓ Real laser hitting Receiver — done.");
                    yield return new WaitForSeconds(0.5f);
                    yield return StartCoroutine(WalkThroughDoor());
                    yield break;
                }

                List<Vector2Int> objects = GetInteractables();
                Debug.Log($"[AI Solver] Pass {pass + 1} — {objects.Count} interactables.");
                bool madeProgress = false;

                foreach (Vector2Int objCell in objects)
                {
                    // Stop immediately if real laser solved it
                    if (RealLaserHittingReceiver())
                    {
                        Debug.Log("[AI Solver] ✓ SOLVED mid-pass — stopping.");
                        yield return new WaitForSeconds(0.3f);
                        yield return StartCoroutine(WalkThroughDoor());
                        yield break;
                    }

                    if (grid.GetTile(objCell.x, objCell.y) == TileType.Empty) continue;

                    PlannedMove plan = PlanForObject(objCell);
                    if (!plan.found) continue;

                    madeProgress = true;
                    TileType objType = grid.GetTile(objCell.x, objCell.y);
                    Debug.Log($"[AI Solver] Move {objType} {objCell}→{plan.to} rot {plan.rot}°");

                    yield return StartCoroutine(WalkTo(objCell));
                    yield return new WaitForSeconds(actionDelay * 0.3f);

                    // PICKUP — preserve GO reference locally
                    gridVisualizer.SpawnedObjects.TryGetValue(objCell, out GameObject objGO);
                    grid.SetTile(objCell.x, objCell.y, TileType.Empty);
                    gridVisualizer.SpawnedObjects.Remove(objCell);
                    if (objGO != null) objGO.SetActive(false);

                    yield return new WaitForSeconds(actionDelay * 0.2f);
                    yield return StartCoroutine(WalkTo(plan.to));

                    // PLACE with planned rotation
                    grid.SetTile(plan.to.x, plan.to.y, objType);
                    gridVisualizer.SpawnedObjects[plan.to] = objGO;
                    if (objGO != null)
                    {
                        objGO.transform.position = gridVisualizer.GridToWorld(plan.to.x, plan.to.y);
                        objGO.transform.rotation = Quaternion.Euler(0f, plan.rot, 0f);
                        objGO.SetActive(true);
                    }

                    // Wait one physics frame then check REAL laser
                    yield return new WaitForSeconds(actionDelay);

                    if (RealLaserHittingReceiver())
                    {
                        Debug.Log($"[AI Solver] ✓ SOLVED! {objType} at {plan.to} rot {plan.rot}°");
                        yield return new WaitForSeconds(0.5f);
                        yield return StartCoroutine(WalkThroughDoor());
                        yield break;
                    }
                }

                if (!madeProgress) break;
            }

            // Final check
            if (RealLaserHittingReceiver())
            {
                yield return new WaitForSeconds(0.5f);
                yield return StartCoroutine(WalkThroughDoor());
            }
            else
                Debug.LogWarning("[AI Solver] No solution found.");
        }

        
        // REAL LASER CHECK — uses actual physics, not grid math
        // This is the ground truth for "is puzzle solved"
        
        bool RealLaserHittingReceiver()
        {
            if (allLasers == null || allLasers.Length == 0)
            {
                allLasers = FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);
            }
            foreach (var laser in allLasers)
                if (laser != null && laser.IsHittingReceiver) return true;
            return false;
        }

        
        // PLANNING (pure grid math)
        
        PlannedMove PlanForObject(Vector2Int fromCell)
        {
            TileType objType = grid.GetTile(fromCell.x, fromCell.y);

            // Phase A: rotate in-place
            for (int rot = 0; rot < 8; rot++)
            {
                _rotOverride.Clear();
                _rotOverride[fromCell] = rot * 45f;
                if (GridSolvedSim())
                {
                    _rotOverride.Clear();
                    return new PlannedMove { from = fromCell, to = fromCell, rot = rot * 45, found = true, isSolution = true };
                }
            }
            _rotOverride.Clear();

            // Phase B: relocate
            grid.SetTile(fromCell.x, fromCell.y, TileType.Empty);
            List<Vector2Int> candidates = GetPrioritizedCandidates();
            PlannedMove best = new PlannedMove { found = false };
            int bestLen = 0;

            foreach (Vector2Int candidate in candidates)
            {
                if (grid.GetTile(candidate.x, candidate.y) != TileType.Empty) continue;
                grid.SetTile(candidate.x, candidate.y, objType);

                for (int rot = 0; rot < 8; rot++)
                {
                    _rotOverride.Clear();
                    _rotOverride[candidate] = rot * 45f;

                    if (GridSolvedSim())
                    {
                        _rotOverride.Clear();
                        grid.SetTile(candidate.x, candidate.y, TileType.Empty);
                        grid.SetTile(fromCell.x, fromCell.y, objType);
                        return new PlannedMove { from = fromCell, to = candidate, rot = rot * 45, found = true, isSolution = true };
                    }

                    int len = GridSimLen();
                    if (len > bestLen)
                    { bestLen = len; best = new PlannedMove { from = fromCell, to = candidate, rot = rot * 45, found = true, isSolution = false }; }
                }
                _rotOverride.Clear();
                grid.SetTile(candidate.x, candidate.y, TileType.Empty);
            }

            grid.SetTile(fromCell.x, fromCell.y, objType);
            return best;
        }

        List<Vector2Int> GetPrioritizedCandidates()
        {
            var cells = new List<Vector2Int>();
            foreach (var c in GetLaserPathCells()) if (!cells.Contains(c)) cells.Add(c);
            foreach (var c in GetAdjacentToObjects()) if (!cells.Contains(c)) cells.Add(c);
            for (int x = 1; x < grid.Width - 1; x++) for (int y = 1; y < grid.Height - 1; y++)
                { var v = new Vector2Int(x, y); if (grid.GetTile(x, y) == TileType.Empty && !cells.Contains(v)) cells.Add(v); }
            return cells;
        }

       
        // GRID SIMULATION
        
        bool GridPuzzleSolved() { _rotOverride.Clear(); return GridSolvedSim(); }

        bool GridSolvedSim()
        {
            bool any = false;
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue; any = true;
                    if (!SimLaser(new Vector2Int(x, y))) return false;
                }
            return any;
        }

        bool SimLaser(Vector2Int emitter)
        {
            Vector2Int pos = emitter, dir = GetEmitterDir(emitter);
            var seen = new HashSet<(Vector2Int, Vector2Int)>(); int bends = 0;
            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir; if (!InBounds(pos)) break;
                var s = (pos, dir); if (seen.Contains(s)) break; seen.Add(s);
                TileType t = grid.GetTile(pos.x, pos.y);
                if (t == TileType.Receiver) return bends >= 1;
                if (t == TileType.Wall || t == TileType.Emitter) break;
                if (t == TileType.Mirror || t == TileType.Refractor)
                {
                    float r = GetSimRot(pos);
                    if (Mathf.Abs(Mathf.DeltaAngle(r, 0f)) > 5f)
                    { dir = (t == TileType.Mirror) ? BounceDir(dir, pos) : RefractDir(dir, pos); bends++; }
                    continue;
                }
            }
            return false;
        }

        int GridSimLen()
        {
            int total = 0;
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == TileType.Emitter) total += SimLen(new Vector2Int(x, y));
            return total;
        }

        int SimLen(Vector2Int emitter)
        {
            int count = 0; Vector2Int pos = emitter, dir = GetEmitterDir(emitter);
            var seen = new HashSet<(Vector2Int, Vector2Int)>();
            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir; if (!InBounds(pos)) break;
                var s = (pos, dir); if (seen.Contains(s)) break; seen.Add(s); count++;
                TileType t = grid.GetTile(pos.x, pos.y);
                if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                if (t == TileType.Mirror || t == TileType.Refractor)
                {
                    float r = GetSimRot(pos);
                    if (Mathf.Abs(Mathf.DeltaAngle(r, 0f)) > 5f)
                        dir = (t == TileType.Mirror) ? BounceDir(dir, pos) : RefractDir(dir, pos);
                    continue;
                }
            }
            return count;
        }

        float GetSimRot(Vector2Int cell)
        {
            if (_rotOverride.TryGetValue(cell, out float ov)) return ov;
            return GetYRot(cell);
        }

        
        // WALK THROUGH DOOR
        
        IEnumerator WalkThroughDoor()
        {
            Vector2Int doorCell = FindFirst(TileType.Door);
            if (doorCell != -Vector2Int.one)
            {
                // Wait for ReceiverDetector to destroy the door (sustainTime)
                yield return new WaitForSeconds(0.4f);

                // Clear grid entry (door may already be destroyed by ReceiverDetector)
                grid.SetTile(doorCell.x, doorCell.y, TileType.Empty);
                if (gridVisualizer.SpawnedObjects.TryGetValue(doorCell, out GameObject dgo) && dgo != null)
                    Destroy(dgo);
                gridVisualizer.SpawnedObjects.Remove(doorCell);

                yield return StartCoroutine(WalkTo(doorCell));

                Vector2Int beyond = doorCell + OutwardDir(doorCell);
                Vector3 bw = gridVisualizer.GridToWorld(beyond.x, beyond.y);
                float to = 3f;
                while (Vector3.Distance(transform.position, bw) > 0.2f && to > 0)
                {
                    to -= Time.deltaTime;
                    Vector3 d = (bw - transform.position).normalized;
                    cc.Move(d * moveSpeed * Time.deltaTime + Vector3.down * 2f * Time.deltaTime);
                    yield return null;
                }
            }
            Debug.Log("[AI Solver] Agent exited through the door!");
        }

        Vector2Int OutwardDir(Vector2Int cell)
        {
            int cx = grid.Width / 2, cy = grid.Height / 2;
            return (Mathf.Abs(cx - cell.x) > Mathf.Abs(cy - cell.y))
                ? new Vector2Int((int)Mathf.Sign(cell.x - cx), 0)
                : new Vector2Int(0, (int)Mathf.Sign(cell.y - cy));
        }

        
        // BFS MOVEMENT
        
        IEnumerator WalkTo(Vector2Int target)
        {
            if (target == -Vector2Int.one) yield break;
            var path = BFS(WorldToGrid(transform.position), target);
            if (path == null || path.Count == 0) yield break;
            foreach (var step in path)
            {
                Vector3 wt = gridVisualizer.GridToWorld(step.x, step.y); wt.y = transform.position.y;
                float to = 5f;
                while (Vector3.Distance(transform.position, wt) > 0.1f && to > 0)
                {
                    to -= Time.deltaTime;
                    Vector3 d = (wt - transform.position).normalized;
                    if (d.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(d), rotationSpeed * Time.deltaTime);
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
                    TileType t = grid.GetTile(next.x, next.y);
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

        
        // LASER MATH HELPERS
       
        Vector2Int BounceDir(Vector2Int d, Vector2Int cell)
        {
            float a = GetSimRot(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 45f)) < 22.5f || Mathf.Abs(Mathf.DeltaAngle(a, 225f)) < 22.5f)
                ? new Vector2Int(d.y, d.x) : new Vector2Int(-d.y, -d.x);
        }

        Vector2Int RefractDir(Vector2Int d, Vector2Int cell)
        {
            float a = GetSimRot(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 0f)) < 22.5f || Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22.5f)
                ? new Vector2Int(-d.y, d.x) : new Vector2Int(d.y, -d.x);
        }

        float GetYRot(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject obj) && obj != null)
                return obj.transform.eulerAngles.y;
            return 0f;
        }

        Vector2Int GetEmitterDir(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject go) && go != null)
            {
                Vector3 f = go.transform.forward;
                if (Mathf.Abs(f.x) >= Mathf.Abs(f.z)) return f.x >= 0 ? Vector2Int.right : Vector2Int.left;
                return f.z >= 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);
            }
            foreach (var d in Dirs4)
            {
                var n = cell + d; if (!InBounds(n)) continue;
                TileType t = grid.GetTile(n.x, n.y);
                if (t != TileType.Wall && t != TileType.Door && t != TileType.Emitter) return d;
            }
            return Vector2Int.right;
        }

        
        // UTILITIES
        
        List<Vector2Int> GetInteractables()
        {
            var list = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    TileType t = grid.GetTile(x, y);
                    if (t == TileType.Mirror || t == TileType.Refractor) list.Add(new Vector2Int(x, y));
                }
            return list;
        }

        List<Vector2Int> GetLaserPathCells()
        {
            var cells = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue;
                    Vector2Int pos = new Vector2Int(x, y), dir = GetEmitterDir(pos);
                    var seen = new HashSet<(Vector2Int, Vector2Int)>();
                    for (int i = 0; i < grid.Width * grid.Height * 2; i++)
                    {
                        pos += dir; if (!InBounds(pos)) break;
                        var s = (pos, dir); if (seen.Contains(s)) break; seen.Add(s);
                        if (!cells.Contains(pos)) cells.Add(pos);
                        TileType t = grid.GetTile(pos.x, pos.y);
                        if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                        if (t == TileType.Mirror) { dir = BounceDir(dir, pos); continue; }
                        if (t == TileType.Refractor) { dir = RefractDir(dir, pos); continue; }
                    }
                }
            return cells;
        }

        List<Vector2Int> GetAdjacentToObjects()
        {
            var cells = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    TileType t = grid.GetTile(x, y);
                    if (t != TileType.Mirror && t != TileType.Refractor) continue;
                    foreach (var d in Dirs4)
                    {
                        var n = new Vector2Int(x + d.x, y + d.y);
                        if (InBounds(n) && grid.GetTile(n.x, n.y) == TileType.Empty && !cells.Contains(n))
                            cells.Add(n);
                    }
                }
            return cells;
        }

        Vector2Int FindFirst(TileType target)
        {
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == target) return new Vector2Int(x, y);
            return -Vector2Int.one;
        }

        Vector2Int WorldToGrid(Vector3 w)
        {
            float ox = (grid.Width - 1) * spacing / 2f, oz = (grid.Height - 1) * spacing / 2f;
            return new Vector2Int(Mathf.RoundToInt((w.x + ox) / spacing), Mathf.RoundToInt((w.z + oz) / spacing));
        }

        bool InBounds(Vector2Int p) => p.x >= 0 && p.x < grid.Width && p.y >= 0 && p.y < grid.Height;
    }
}
