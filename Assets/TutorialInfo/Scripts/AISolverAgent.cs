using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LightPCG.Core;

namespace LightPCG.Systems
{
    /// <summary>
    /// AI Solver v5 — Research-aligned Backtracking Search Solver
    /// ──────────────────────────────────────────────────────────
    /// Strategy:
    ///   1. Simulate laser path from every Emitter (pure grid math)
    ///   2. For each Mirror/Refractor on the grid:
    ///      a. Walk to the object (BFS — never tunnels through walls)
    ///      b. Pick it up (hide + clear grid tile)
    ///      c. Try every EMPTY cell the current laser path passes through
    ///      d. At each candidate cell, try all 8 rotations (45° steps)
    ///      e. If laser reaches Receiver → keep placement, walk to Receiver
    ///      f. Otherwise undo and try next candidate
    /// ──────────────────────────────────────────────────────────
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
        public float stepDelay = 0.25f;  // delay between rotation attempts
        public float pickupDelay = 0.15f;  // pause after pickup/drop

        // ── Runtime ──────────────────────────────────────────────────────────
        private GridModel grid;
        private float spacing;
        private CharacterController cc;

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0,  1), new Vector2Int(0, -1)
        };
        // ────────────────────────────────────────────────────────────────────

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.radius = 0.28f;
            cc.height = 1.0f;
            cc.center = new Vector3(0, 0.5f, 0);
            cc.minMoveDistance = 0f;
        }

        void Start()
        {
            if (gridVisualizer == null)
                gridVisualizer = FindObjectOfType<GridVisualizer>();

            if (gridVisualizer == null)
            { Debug.LogError("[AI Solver] GridVisualizer not found!"); return; }

            StartCoroutine(Pipeline());
        }

        // ════════════════════════════════════════════════════════════════════
        // PIPELINE
        // ════════════════════════════════════════════════════════════════════
        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.8f);

            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;

            // ── Teleport to Emitter ─────────────────────────────────────────
            Vector2Int emitterCell = FindFirst(TileType.Emitter);
            if (emitterCell == -Vector2Int.one)
            { Debug.LogError("[AI Solver] No Emitter found!"); yield break; }

            cc.enabled = false;
            transform.position = gridVisualizer.GridToWorld(emitterCell.x, emitterCell.y);
            cc.enabled = true;

            Debug.Log($"[AI Solver] Start at Emitter {emitterCell}");

            // ── Already solved? (Backward Chaining guarantee) ───────────────
            if (AllEmittersSolved())
            {
                Debug.Log("[AI Solver] Puzzle already solved by construction.");
                yield return StartCoroutine(WalkTo(FindFirst(TileType.Receiver)));
                yield break;
            }

            yield return StartCoroutine(BacktrackingSolve());
        }

        // ════════════════════════════════════════════════════════════════════
        // BACKTRACKING SOLVER
        // For each interactable object: try every (empty-cell, rotation) pair
        // ════════════════════════════════════════════════════════════════════
        IEnumerator BacktrackingSolve()
        {
            List<Vector2Int> objects = GetInteractables(); // Mirrors + Refractors
            Debug.Log($"[AI Solver] {objects.Count} interactable(s) available.");

            foreach (Vector2Int objCell in objects)
            {
                TileType objType = grid.GetTile(objCell.x, objCell.y);
                GameObject objGO = null;
                gridVisualizer.SpawnedObjects.TryGetValue(objCell, out objGO);

                // ── Walk to the object ──────────────────────────────────────
                yield return StartCoroutine(WalkTo(objCell));

                // ── Pick up: hide object, clear grid tile ───────────────────
                grid.SetTile(objCell.x, objCell.y, TileType.Empty);
                if (objGO != null) objGO.SetActive(false);
                gridVisualizer.SpawnedObjects.Remove(objCell);
                yield return new WaitForSeconds(pickupDelay);

                // ── Get laser path with this object removed ─────────────────
                List<Vector2Int> laserCells = SimulateLaserCells();

                bool solved = false;

                // ── Try every empty cell on the laser path ──────────────────
                foreach (Vector2Int candidate in laserCells)
                {
                    if (grid.GetTile(candidate.x, candidate.y) != TileType.Empty) continue;

                    // Walk to candidate position
                    yield return StartCoroutine(WalkTo(candidate));

                    // Place object at candidate
                    grid.SetTile(candidate.x, candidate.y, objType);
                    if (objGO != null)
                    {
                        objGO.transform.position = gridVisualizer.GridToWorld(candidate.x, candidate.y);
                        objGO.SetActive(true);
                        gridVisualizer.SpawnedObjects[candidate] = objGO;
                    }

                    // ── Try all 8 rotations (45° × 8) ──────────────────────
                    for (int rot = 0; rot < 8; rot++)
                    {
                        if (objGO != null)
                            objGO.transform.rotation = Quaternion.Euler(0f, rot * 45f, 0f);

                        yield return new WaitForSeconds(stepDelay);

                        if (AllEmittersSolved())
                        {
                            Debug.Log($"[AI Solver] ✓ SOLVED — {objType} at {candidate}, rot {rot * 45}°");
                            solved = true;
                            break;
                        }
                    }

                    if (solved) break;

                    // ── Undo: remove from candidate, continue search ────────
                    grid.SetTile(candidate.x, candidate.y, TileType.Empty);
                    if (objGO != null)
                    {
                        objGO.SetActive(false);
                        gridVisualizer.SpawnedObjects.Remove(candidate);
                    }
                }

                if (solved)
                {
                    // Puzzle solved — walk to Receiver to "open" door
                    yield return StartCoroutine(WalkTo(FindFirst(TileType.Receiver)));
                    Debug.Log("[AI Solver] Reached Receiver — door should open!");
                    yield break;
                }

                // ── No candidate worked: put object back where it was ───────
                grid.SetTile(objCell.x, objCell.y, objType);
                if (objGO != null)
                {
                    objGO.transform.position = gridVisualizer.GridToWorld(objCell.x, objCell.y);
                    objGO.SetActive(true);
                    gridVisualizer.SpawnedObjects[objCell] = objGO;
                }
                yield return StartCoroutine(WalkTo(objCell)); // return to original spot visually
            }

            // Fallback — Backward Chaining guarantees a solution exists,
            // so this should not be reached in normal operation
            Debug.LogWarning("[AI Solver] Backtracking exhausted — puzzle may already be solved.");
            AllEmittersSolved();
        }

        // ════════════════════════════════════════════════════════════════════
        // BFS MOVEMENT — uses CharacterController, respects all colliders
        // ════════════════════════════════════════════════════════════════════
        IEnumerator WalkTo(Vector2Int targetCell)
        {
            if (targetCell == -Vector2Int.one) yield break;

            Vector2Int agentCell = WorldToGrid(transform.position);
            List<Vector2Int> path = BFS(agentCell, targetCell);

            if (path == null || path.Count == 0)
            {
                // Try to get one step closer even if goal is blocked
                Debug.LogWarning($"[AI Solver] No path to {targetCell} — skipping.");
                yield break;
            }

            foreach (Vector2Int step in path)
            {
                Vector3 wTarget = gridVisualizer.GridToWorld(step.x, step.y);
                wTarget.y = transform.position.y;

                float timeout = 4f;
                while (Vector3.Distance(transform.position, wTarget) > 0.1f && timeout > 0)
                {
                    timeout -= Time.deltaTime;
                    Vector3 moveDir = (wTarget - transform.position).normalized;

                    if (moveDir.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(moveDir), rotationSpeed * Time.deltaTime);

                    // gravity-lite so agent stays grounded
                    Vector3 motion = moveDir * moveSpeed * Time.deltaTime;
                    motion.y = -2f * Time.deltaTime;
                    cc.Move(motion);
                    yield return null;
                }
            }
        }

        // BFS on logical grid
        List<Vector2Int> BFS(Vector2Int start, Vector2Int goal)
        {
            if (start == goal) return new List<Vector2Int>();

            var visited = new HashSet<Vector2Int> { start };
            var parent = new Dictionary<Vector2Int, Vector2Int>();
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                Vector2Int cur = queue.Dequeue();
                foreach (Vector2Int d in Dirs4)
                {
                    Vector2Int next = cur + d;
                    if (visited.Contains(next)) continue;

                    TileType t = grid.GetTile(next.x, next.y);
                    // Walkable: Empty, Door, or the exact goal tile
                    bool ok = (t == TileType.Empty || t == TileType.Door || next == goal);
                    if (!ok) continue;

                    visited.Add(next);
                    parent[next] = cur;

                    if (next == goal)
                    {
                        var path = new List<Vector2Int>();
                        for (var c = goal; c != start; c = parent[c]) path.Add(c);
                        path.Reverse();
                        return path;
                    }
                    queue.Enqueue(next);
                }
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════════════
        // LASER SIMULATION — pure grid math, reads object Y rotation
        // ════════════════════════════════════════════════════════════════════

        bool AllEmittersSolved()
        {
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == TileType.Emitter)
                        if (!LaserHitsReceiver(new Vector2Int(x, y)))
                            return false;
            return true;
        }

        bool LaserHitsReceiver(Vector2Int emitter)
        {
            Vector2Int pos = emitter;
            Vector2Int dir = EmitterFiringDir(emitter);
            var seen = new HashSet<(Vector2Int, Vector2Int)>();

            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir;
                if (!InBounds(pos)) break;
                var state = (pos, dir);
                if (seen.Contains(state)) break;
                seen.Add(state);

                TileType t = grid.GetTile(pos.x, pos.y);
                if (t == TileType.Receiver) return true;
                if (t == TileType.Wall) break;
                if (t == TileType.Emitter) break;
                if (t == TileType.Mirror) { dir = BounceDir(dir, pos); continue; }
                if (t == TileType.Refractor) { dir = RefractDir(dir, pos); continue; }
            }
            return false;
        }

        // Returns all cells the laser passes through (for candidate placement)
        List<Vector2Int> SimulateLaserCells()
        {
            var cells = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue;
                    var emitter = new Vector2Int(x, y);
                    Vector2Int pos = emitter;
                    Vector2Int dir = EmitterFiringDir(emitter);
                    var seen = new HashSet<(Vector2Int, Vector2Int)>();

                    for (int i = 0; i < grid.Width * grid.Height * 2; i++)
                    {
                        pos += dir;
                        if (!InBounds(pos)) break;
                        var state = (pos, dir);
                        if (seen.Contains(state)) break;
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

        // Direction helpers — reads actual visual rotation of spawned object
        Vector2Int BounceDir(Vector2Int d, Vector2Int cell)
        {
            float angle = GetYRot(cell);
            // 45° / 225° mirror: swap axes  →  right↔up, left↔down
            if (Mathf.Abs(Mathf.DeltaAngle(angle, 45f)) < 22.5f ||
                Mathf.Abs(Mathf.DeltaAngle(angle, 225f)) < 22.5f)
                return new Vector2Int(d.y, d.x);
            // 135° / 315° mirror: negate-swap
            return new Vector2Int(-d.y, -d.x);
        }

        Vector2Int RefractDir(Vector2Int d, Vector2Int cell)
        {
            float angle = GetYRot(cell);
            if (Mathf.Abs(Mathf.DeltaAngle(angle, 0f)) < 22.5f ||
                Mathf.Abs(Mathf.DeltaAngle(angle, 180f)) < 22.5f)
                return new Vector2Int(-d.y, d.x);  // 90° CCW
            return new Vector2Int(d.y, -d.x);  // 90° CW
        }

        float GetYRot(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject obj) && obj != null)
                return obj.transform.eulerAngles.y;
            return 45f;
        }

        // Emitter fires toward the first non-wall, non-door neighbour
        Vector2Int EmitterFiringDir(Vector2Int cell)
        {
            foreach (Vector2Int d in Dirs4)
            {
                Vector2Int next = cell + d;
                if (!InBounds(next)) continue;
                TileType t = grid.GetTile(next.x, next.y);
                if (t != TileType.Wall && t != TileType.Door)
                    return d;
            }
            return Vector2Int.right;
        }

        // ════════════════════════════════════════════════════════════════════
        // UTILITIES
        // ════════════════════════════════════════════════════════════════════
        Vector2Int FindFirst(TileType target)
        {
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == target) return new Vector2Int(x, y);
            return -Vector2Int.one;
        }

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

        // Scene gizmo: draw all emitter laser paths
        void OnDrawGizmos()
        {
            if (grid == null || gridVisualizer == null) return;
            Gizmos.color = new Color(1f, 1f, 0f, 0.6f);

            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue;
                    DrawLaserGizmo(new Vector2Int(x, y));
                }
        }

        void DrawLaserGizmo(Vector2Int emitter)
        {
            Vector2Int pos = emitter;
            Vector2Int dir = EmitterFiringDir(emitter);
            var seen = new HashSet<(Vector2Int, Vector2Int)>();

            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                Vector2Int next = pos + dir;
                if (!InBounds(next)) break;
                var state = (next, dir);
                if (seen.Contains(state)) break;
                seen.Add(state);

                Vector3 a = gridVisualizer.GridToWorld(pos.x, pos.y) + Vector3.up * 0.5f;
                Vector3 b = gridVisualizer.GridToWorld(next.x, next.y) + Vector3.up * 0.5f;
                Gizmos.DrawLine(a, b);

                TileType t = grid.GetTile(next.x, next.y);
                pos = next;
                if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                if (t == TileType.Mirror) dir = BounceDir(dir, next);
                if (t == TileType.Refractor) dir = RefractDir(dir, next);
            }
        }
    }
}