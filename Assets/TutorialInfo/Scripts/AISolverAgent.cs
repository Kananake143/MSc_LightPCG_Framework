using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LightPCG.Core;

namespace LightPCG.Systems
{
    /// <summary>
    /// AI Solver v8
    
    /// - OnDrawGizmos removed — no more thin debug laser lines in Scene view
    /// -GetEmitterDir now reads transform.forward of the ACTUAL Emitter
    ///   GameObject so grid math matches the real LaserSystem direction
    /// - PuzzleSolved requires ≥1 Mirror/Refractor bend in path
    
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class AISolverAgent : MonoBehaviour
    {
        [Header("References — auto-found if empty")]
        public GridVisualizer gridVisualizer;

        [Header("Movement")]
        public float moveSpeed = 5f;
        public float rotationSpeed = 12f;

        [Header("Solve pacing (seconds)")]
        public float rotateStepDelay = 0.20f;
        public float pickupDelay = 0.15f;

        private GridModel grid;
        private float spacing;
        private CharacterController cc;

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0,1), new Vector2Int(0,-1)
        };

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

       
        // PIPELINE
        
        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.8f);

            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;

            Vector2Int emitterCell = FindFirst(TileType.Emitter);
            if (emitterCell == -Vector2Int.one)
            { Debug.LogError("[AI Solver] No Emitter found!"); yield break; }

            cc.enabled = false;
            transform.position = gridVisualizer.GridToWorld(emitterCell.x, emitterCell.y);
            cc.enabled = true;

            if (PuzzleSolved())
            {
                Debug.Log("[AI Solver] Already solved by construction.");
                yield return StartCoroutine(WalkToDoor());
                yield break;
            }

            yield return StartCoroutine(SequentialSolver());
        }

        
        // SEQUENTIAL GREEDY SOLVER
        
        IEnumerator SequentialSolver()
        {
            List<Vector2Int> objects = GetInteractables();
            Debug.Log($"[AI Solver] {objects.Count} interactable(s) to process.");

            foreach (Vector2Int objCell in objects)
            {
                if (PuzzleSolved()) break;

                TileType objType = grid.GetTile(objCell.x, objCell.y);
                gridVisualizer.SpawnedObjects.TryGetValue(objCell, out GameObject objGO);

                // Phase 1: Rotate in-place
                yield return StartCoroutine(WalkTo(objCell));
                int bestRot = 0, bestLen = LaserPathLength();

                for (int rot = 0; rot < 8; rot++)
                {
                    if (objGO != null)
                        objGO.transform.rotation = Quaternion.Euler(0f, rot * 45f, 0f);
                    yield return new WaitForSeconds(rotateStepDelay);

                    if (PuzzleSolved())
                    {
                        Debug.Log($"[AI Solver] ✓ SOLVED Phase1 — {objType} at {objCell} rot {rot * 45}°");
                        yield return StartCoroutine(WalkToDoor()); yield break;
                    }
                    int len = LaserPathLength();
                    if (len > bestLen) { bestLen = len; bestRot = rot; }
                }
                if (objGO != null)
                    objGO.transform.rotation = Quaternion.Euler(0f, bestRot * 45f, 0f);
                if (PuzzleSolved()) { yield return StartCoroutine(WalkToDoor()); yield break; }

                // Phase 2: Relocate to a cell on the laser path
                yield return StartCoroutine(WalkTo(objCell));
                grid.SetTile(objCell.x, objCell.y, TileType.Empty);
                if (objGO != null) { objGO.SetActive(false); gridVisualizer.SpawnedObjects.Remove(objCell); }
                yield return new WaitForSeconds(pickupDelay);

                List<Vector2Int> candidates = GetLaserPathCells();
                bool placed = false;

                foreach (Vector2Int candidate in candidates)
                {
                    if (grid.GetTile(candidate.x, candidate.y) != TileType.Empty) continue;
                    yield return StartCoroutine(WalkTo(candidate));

                    grid.SetTile(candidate.x, candidate.y, objType);
                    if (objGO != null)
                    {
                        objGO.transform.position = gridVisualizer.GridToWorld(candidate.x, candidate.y);
                        objGO.SetActive(true);
                        gridVisualizer.SpawnedObjects[candidate] = objGO;
                    }

                    for (int rot = 0; rot < 8; rot++)
                    {
                        if (objGO != null)
                            objGO.transform.rotation = Quaternion.Euler(0f, rot * 45f, 0f);
                        yield return new WaitForSeconds(rotateStepDelay);
                        if (PuzzleSolved())
                        {
                            Debug.Log($"[AI Solver] ✓ SOLVED Phase2 — {objType} at {candidate} rot {rot * 45}°");
                            placed = true; break;
                        }
                    }
                    if (placed) break;

                    grid.SetTile(candidate.x, candidate.y, TileType.Empty);
                    if (objGO != null) { objGO.SetActive(false); gridVisualizer.SpawnedObjects.Remove(candidate); }
                }

                if (placed) { yield return StartCoroutine(WalkToDoor()); yield break; }

                // Restore
                grid.SetTile(objCell.x, objCell.y, objType);
                if (objGO != null)
                {
                    objGO.transform.position = gridVisualizer.GridToWorld(objCell.x, objCell.y);
                    objGO.SetActive(true);
                    gridVisualizer.SpawnedObjects[objCell] = objGO;
                }
                yield return new WaitForSeconds(pickupDelay);
            }

            if (PuzzleSolved()) yield return StartCoroutine(WalkToDoor());
            else Debug.LogWarning("[AI Solver] Could not solve — check grid layout.");
        }

        
        // PUZZLE SOLVED — requires ≥1 bend before Receiver
        
        bool PuzzleSolved()
        {
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == TileType.Emitter)
                        if (!LaserReachesReceiverViaBend(new Vector2Int(x, y)))
                            return false;
            return true;
        }

        bool LaserReachesReceiverViaBend(Vector2Int emitter)
        {
            Vector2Int pos = emitter;
            // Read firing direction from actual GameObject transform (matches LaserSystem)
            Vector2Int dir = GetEmitterDirFromGO(emitter);
            var seen = new HashSet<(Vector2Int, Vector2Int)>();
            int bends = 0;

            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir;
                if (!InBounds(pos)) break;
                var state = (pos, dir);
                if (seen.Contains(state)) break;
                seen.Add(state);

                TileType t = grid.GetTile(pos.x, pos.y);
                if (t == TileType.Receiver) return bends >= 1;
                if (t == TileType.Wall) break;
                if (t == TileType.Emitter) break;
                if (t == TileType.Mirror) { dir = BounceDir(dir, pos); bends++; continue; }
                if (t == TileType.Refractor) { dir = RefractDir(dir, pos); bends++; continue; }
            }
            return false;
        }

        
        // LASER PATH LENGTH (greedy heuristic)
        
        int LaserPathLength()
        {
            int total = 0;
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == TileType.Emitter)
                        total += SingleEmitterLen(new Vector2Int(x, y));
            return total;
        }

        int SingleEmitterLen(Vector2Int emitter)
        {
            int count = 0;
            Vector2Int pos = emitter;
            Vector2Int dir = GetEmitterDirFromGO(emitter);
            var seen = new HashSet<(Vector2Int, Vector2Int)>();
            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir; if (!InBounds(pos)) break;
                var state = (pos, dir); if (seen.Contains(state)) break;
                seen.Add(state); count++;
                TileType t = grid.GetTile(pos.x, pos.y);
                if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                if (t == TileType.Mirror) { dir = BounceDir(dir, pos); continue; }
                if (t == TileType.Refractor) { dir = RefractDir(dir, pos); continue; }
            }
            return count;
        }

        List<Vector2Int> GetLaserPathCells()
        {
            var cells = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue;
                    Vector2Int pos = new Vector2Int(x, y);
                    Vector2Int dir = GetEmitterDirFromGO(pos);
                    var seen = new HashSet<(Vector2Int, Vector2Int)>();
                    for (int i = 0; i < grid.Width * grid.Height * 2; i++)
                    {
                        pos += dir; if (!InBounds(pos)) break;
                        var state = (pos, dir); if (seen.Contains(state)) break;
                        seen.Add(state);
                        if (!cells.Contains(pos)) cells.Add(pos);
                        TileType t = grid.GetTile(pos.x, pos.y);
                        if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                        if (t == TileType.Mirror) { dir = BounceDir(dir, pos); continue; }
                        if (t == TileType.Refractor) { dir = RefractDir(dir, pos); continue; }
                    }
                }
            return cells;
        }

        
        // EMITTER DIRECTION — reads actual GameObject transform.forward
        // so AI math matches the real LaserSystem (no more divergence)
        
        Vector2Int GetEmitterDirFromGO(Vector2Int emitterCell)
        {
            // Try to get the real forward from the spawned Emitter object
            if (gridVisualizer.SpawnedObjects.TryGetValue(emitterCell, out GameObject go) && go != null)
            {
                Vector3 fwd = go.transform.forward;
                // Convert world XZ forward to grid direction
                // Use the dominant axis (X or Z)
                if (Mathf.Abs(fwd.x) >= Mathf.Abs(fwd.z))
                    return fwd.x >= 0 ? Vector2Int.right : Vector2Int.left;
                else
                    return fwd.z >= 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);
            }

            // Fallback: infer from grid (which neighbour is not a wall)
            foreach (var d in Dirs4)
            {
                var next = emitterCell + d;
                if (!InBounds(next)) continue;
                TileType t = grid.GetTile(next.x, next.y);
                if (t != TileType.Wall && t != TileType.Door && t != TileType.Emitter) return d;
            }
            return Vector2Int.right;
        }

        
        // BFS MOVEMENT
        
        IEnumerator WalkTo(Vector2Int targetCell)
        {
            if (targetCell == -Vector2Int.one) yield break;
            var path = BFS(WorldToGrid(transform.position), targetCell);
            if (path == null || path.Count == 0) yield break;

            foreach (Vector2Int step in path)
            {
                Vector3 wt = gridVisualizer.GridToWorld(step.x, step.y);
                wt.y = transform.position.y;
                float timeout = 5f;
                while (Vector3.Distance(transform.position, wt) > 0.1f && timeout > 0)
                {
                    timeout -= Time.deltaTime;
                    Vector3 d = (wt - transform.position).normalized;
                    if (d.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(d), rotationSpeed * Time.deltaTime);
                    cc.Move(d * moveSpeed * Time.deltaTime + Vector3.down * 2f * Time.deltaTime);
                    yield return null;
                }
            }
        }

        IEnumerator WalkToDoor()
        {
            Vector2Int rc = FindFirst(TileType.Receiver);
            if (rc != -Vector2Int.one) yield return StartCoroutine(WalkTo(rc));
            Debug.Log("[AI Solver] Reached goal — door opens!");
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
                    if (visited.Contains(next)) continue;
                    TileType t = grid.GetTile(next.x, next.y);
                    if (t != TileType.Empty && t != TileType.Door && next != goal) continue;
                    visited.Add(next); parent[next] = cur;
                    if (next == goal)
                    {
                        var path = new List<Vector2Int>();
                        for (var c = goal; c != start; c = parent[c]) path.Add(c);
                        path.Reverse(); return path;
                    }
                    queue.Enqueue(next);
                }
            }
            return null;
        }

        
        // LASER MATH HELPERS
       
        Vector2Int BounceDir(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
            if (Mathf.Abs(Mathf.DeltaAngle(a, 45f)) < 22.5f ||
                Mathf.Abs(Mathf.DeltaAngle(a, 225f)) < 22.5f)
                return new Vector2Int(d.y, d.x);
            return new Vector2Int(-d.y, -d.x);
        }

        Vector2Int RefractDir(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
            if (Mathf.Abs(Mathf.DeltaAngle(a, 0f)) < 22.5f ||
                Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22.5f)
                return new Vector2Int(-d.y, d.x);
            return new Vector2Int(d.y, -d.x);
        }

        float GetYRot(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject obj) && obj != null)
                return obj.transform.eulerAngles.y;
            return 45f;
        }

        
        // UTILITIES
        
        List<Vector2Int> GetInteractables()
        {
            var list = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    TileType t = grid.GetTile(x, y);
                    if (t == TileType.Mirror || t == TileType.Refractor)
                        list.Add(new Vector2Int(x, y));
                }
            return list;
        }

        Vector2Int FindFirst(TileType target)
        {
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == target) return new Vector2Int(x, y);
            return -Vector2Int.one;
        }

        Vector2Int WorldToGrid(Vector3 w)
        {
            float ox = (grid.Width - 1) * spacing / 2f;
            float oz = (grid.Height - 1) * spacing / 2f;
            return new Vector2Int(
                Mathf.RoundToInt((w.x + ox) / spacing),
                Mathf.RoundToInt((w.z + oz) / spacing));
        }

        bool InBounds(Vector2Int p) =>
            p.x >= 0 && p.x < grid.Width && p.y >= 0 && p.y < grid.Height;

        // OnDrawGizmos intentionally removed — no more thin debug lines in Scene view
    }
}