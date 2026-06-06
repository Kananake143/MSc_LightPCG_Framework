using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LightPCG.Core;

namespace LightPCG.Systems
{
    /// <summary>
    /// AI Solver v15 — Multi-object Chain Solver (no timeout)
    /// ─────────────────────────────────────────────────────────────────
    /// Solve strategy (in order):
    ///   1. SINGLE-OBJECT BEAM: trace each laser segment → place one
    ///      object on segment to redirect toward Receiver
    ///   2. TWO-OBJECT CHAIN: try pairs (A, B) — place A to extend
    ///      laser, then B to redirect to Receiver (handles multi-bounce)
    ///   3. GREEDY FALLBACK: maximise laser path length per move,
    ///      with visited set to avoid repeating failed moves
    ///
    /// No timeout — runs until solved or exhausts all options.
    /// ─────────────────────────────────────────────────────────────────
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
        public float actionDelay = 0.2f;

        private GridModel grid;
        private float spacing;
        private CharacterController cc;
        private LaserSystem[] allLasers;

        private HashSet<(Vector2Int, int)> triedMoves = new HashSet<(Vector2Int, int)>();

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right,Vector2Int.left,
            new Vector2Int(0,1),new Vector2Int(0,-1)
        };

        private Dictionary<Vector2Int, float> _rot = new Dictionary<Vector2Int, float>();

        struct Move
        {
            public bool valid;
            public Vector2Int from, to;
            public TileType type;
            public int rot;
        }

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            cc.radius = 0.28f; cc.height = 1.0f; cc.center = new Vector3(0, 0.5f, 0); cc.minMoveDistance = 0f;
        }

        void Start()
        {
            if (gridVisualizer == null) gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (gridVisualizer == null) { Debug.LogError("[AI] GridVisualizer not found!"); return; }
            StartCoroutine(Pipeline());
        }

        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.8f);
            grid = gridVisualizer.LevelGrid; spacing = gridVisualizer.Spacing;
            allLasers = FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);

            Vector2Int em = FindFirst(TileType.Emitter);
            if (em == -Vector2Int.one) { Debug.LogError("[AI] No Emitter!"); yield break; }
            cc.enabled = false; transform.position = gridVisualizer.GridToWorld(em.x, em.y); cc.enabled = true;

            if (LaserSolved()) { yield return StartCoroutine(ExitDoor()); yield break; }
            yield return StartCoroutine(Solve());
        }

        // ════════════════════════════════════════════════════════════════
        // MAIN SOLVE LOOP — no timeout, keeps trying until done
        // ════════════════════════════════════════════════════════════════
        IEnumerator Solve()
        {
            Debug.Log("[AI] Starting solve...");
            int pass = 0;

            while (!LaserSolved())
            {
                pass++;
                Debug.Log($"[AI] Pass {pass}");
                bool moved = false;

                // ── Strategy 1: Single-object beam placement ──────────
                var beamMove = FindBeamMove();
                if (beamMove.valid && !Already(beamMove))
                {
                    Remember(beamMove);
                    yield return StartCoroutine(DoMove(beamMove));
                    yield return new WaitForSeconds(actionDelay);
                    if (LaserSolved()) break;
                    moved = true; continue;
                }

                // ── Strategy 2: Two-object chain ──────────────────────
                var chainMove = FindChainMove();
                if (chainMove.valid && !Already(chainMove))
                {
                    Remember(chainMove);
                    yield return StartCoroutine(DoMove(chainMove));
                    yield return new WaitForSeconds(actionDelay);
                    if (LaserSolved()) break;
                    moved = true; continue;
                }

                // ── Strategy 3: Greedy path-length fallback ───────────
                var greedyMove = FindGreedyMove();
                if (greedyMove.valid && !Already(greedyMove))
                {
                    Remember(greedyMove);
                    yield return StartCoroutine(DoMove(greedyMove));
                    yield return new WaitForSeconds(actionDelay);
                    if (LaserSolved()) break;
                    moved = true; continue;
                }

                // No moves found this pass
                if (!moved)
                {
                    Debug.LogWarning("[AI] No valid moves found — puzzle unsolvable with current objects.");
                    yield break;
                }
            }

            Debug.Log("[AI] ✓ SOLVED!");
            yield return new WaitForSeconds(0.4f);
            yield return StartCoroutine(ExitDoor());
        }

        // ════════════════════════════════════════════════════════════════
        // STRATEGY 1: SINGLE-OBJECT BEAM
        // Trace laser segments → find cell on segment → place object to solve
        // ════════════════════════════════════════════════════════════════
        Move FindBeamMove()
        {
            var receiverCell = FindFirst(TileType.Receiver);
            var segments = TraceSegments();
            var interactables = GetInteractables();

            foreach (var seg in segments)
            {
                // Sort cells closest to Receiver first
                var cells = new List<Vector2Int>(seg);
                cells.Sort((a, b) => Manhattan(a, receiverCell).CompareTo(Manhattan(b, receiverCell)));

                foreach (var candidate in cells)
                {
                    foreach (var objCell in interactables)
                    {
                        var objType = grid.GetTile(objCell.x, objCell.y);
                        grid.SetTile(objCell.x, objCell.y, TileType.Empty);
                        if (grid.GetTile(candidate.x, candidate.y) != TileType.Empty)
                        { grid.SetTile(objCell.x, objCell.y, objType); continue; }

                        grid.SetTile(candidate.x, candidate.y, objType);
                        for (int r = 0; r < 8; r++)
                        {
                            _rot.Clear(); _rot[candidate] = r * 45f;
                            if (GridSolved())
                            {
                                _rot.Clear();
                                grid.SetTile(candidate.x, candidate.y, TileType.Empty);
                                grid.SetTile(objCell.x, objCell.y, objType);
                                return new Move { valid = true, from = objCell, to = candidate, type = objType, rot = r * 45 };
                            }
                        }
                        _rot.Clear();
                        grid.SetTile(candidate.x, candidate.y, TileType.Empty);
                        grid.SetTile(objCell.x, objCell.y, objType);
                    }
                }
            }
            return new Move();
        }

        // ════════════════════════════════════════════════════════════════
        // STRATEGY 2: TWO-OBJECT CHAIN
        // Place object A to extend laser → then find cell for object B
        // that redirects to Receiver. Handles multi-bounce scenarios.
        // ════════════════════════════════════════════════════════════════
        Move FindChainMove()
        {
            var interactables = GetInteractables();
            if (interactables.Count < 2) return new Move();

            var receiverCell = FindFirst(TileType.Receiver);
            var laserCells = GetLaserCells();

            // Try each object as "object A" (placed first to extend path)
            foreach (var cellA in interactables)
            {
                var typeA = grid.GetTile(cellA.x, cellA.y);
                grid.SetTile(cellA.x, cellA.y, TileType.Empty);

                // Try placing A at each laser cell candidate
                foreach (var candA in laserCells)
                {
                    if (grid.GetTile(candA.x, candA.y) != TileType.Empty) continue;
                    grid.SetTile(candA.x, candA.y, typeA);

                    // Try each rotation for A
                    for (int rA = 0; rA < 8; rA++)
                    {
                        _rot.Clear(); _rot[candA] = rA * 45f;

                        // After placing A, re-trace laser and look for B placement
                        var newCells = GetLaserCells();
                        foreach (var cellB in interactables)
                        {
                            if (cellB == cellA) continue;
                            var typeB = grid.GetTile(cellB.x, cellB.y);
                            if (typeB == TileType.Empty) continue; // already removed
                            grid.SetTile(cellB.x, cellB.y, TileType.Empty);

                            foreach (var candB in newCells)
                            {
                                if (grid.GetTile(candB.x, candB.y) != TileType.Empty) continue;
                                grid.SetTile(candB.x, candB.y, typeB);
                                for (int rB = 0; rB < 8; rB++)
                                {
                                    _rot[candB] = rB * 45f;
                                    if (GridSolved())
                                    { // Found a two-object solution — execute A first
                                        _rot.Clear();
                                        grid.SetTile(candB.x, candB.y, TileType.Empty);
                                        grid.SetTile(cellB.x, cellB.y, typeB);
                                        grid.SetTile(candA.x, candA.y, TileType.Empty);
                                        grid.SetTile(cellA.x, cellA.y, typeA);
                                        // Return move for A; B will be handled next pass
                                        return new Move { valid = true, from = cellA, to = candA, type = typeA, rot = rA * 45 };
                                    }
                                }
                                _rot.Remove(candB);
                                grid.SetTile(candB.x, candB.y, TileType.Empty);
                            }
                            grid.SetTile(cellB.x, cellB.y, typeB);
                        }
                    }
                    _rot.Clear();
                    grid.SetTile(candA.x, candA.y, TileType.Empty);
                }
                grid.SetTile(cellA.x, cellA.y, typeA);
            }
            return new Move();
        }

        // ════════════════════════════════════════════════════════════════
        // STRATEGY 3: GREEDY — maximise laser path length
        // ════════════════════════════════════════════════════════════════
        Move FindGreedyMove()
        {
            Move best = new Move();
            int bestLen = GridLen();
            var laserCells = GetLaserCells();

            foreach (var objCell in GetInteractables())
            {
                var objType = grid.GetTile(objCell.x, objCell.y);
                grid.SetTile(objCell.x, objCell.y, TileType.Empty);

                foreach (var candidate in laserCells)
                {
                    if (grid.GetTile(candidate.x, candidate.y) != TileType.Empty) continue;
                    grid.SetTile(candidate.x, candidate.y, objType);
                    for (int r = 0; r < 8; r++)
                    {
                        _rot.Clear(); _rot[candidate] = r * 45f;
                        if (GridSolved())
                        {
                            _rot.Clear();
                            grid.SetTile(candidate.x, candidate.y, TileType.Empty);
                            grid.SetTile(objCell.x, objCell.y, objType);
                            return new Move { valid = true, from = objCell, to = candidate, type = objType, rot = r * 45 };
                        }
                        int len = GridLen();
                        if (len > bestLen)
                        {
                            bestLen = len;
                            best = new Move { valid = true, from = objCell, to = candidate, type = objType, rot = r * 45 };
                        }
                    }
                    _rot.Clear();
                    grid.SetTile(candidate.x, candidate.y, TileType.Empty);
                }
                grid.SetTile(objCell.x, objCell.y, objType);
            }
            return best;
        }

        // ════════════════════════════════════════════════════════════════
        // EXECUTE MOVE
        // ════════════════════════════════════════════════════════════════
        IEnumerator DoMove(Move m)
        {
            Debug.Log($"[AI] {m.type}: {m.from}→{m.to} rot {m.rot}°");
            bool sameCell = m.from == m.to;

            if (!sameCell)
            {
                yield return StartCoroutine(WalkTo(m.from));
                yield return new WaitForSeconds(actionDelay * 0.2f);
            }

            gridVisualizer.SpawnedObjects.TryGetValue(m.from, out GameObject go);
            grid.SetTile(m.from.x, m.from.y, TileType.Empty);
            gridVisualizer.SpawnedObjects.Remove(m.from);
            if (go != null && !sameCell) go.SetActive(false);

            if (!sameCell) { yield return StartCoroutine(WalkTo(m.to)); }

            grid.SetTile(m.to.x, m.to.y, m.type);
            gridVisualizer.SpawnedObjects[m.to] = go;
            if (go != null)
            {
                go.transform.position = gridVisualizer.GridToWorld(m.to.x, m.to.y);
                go.transform.rotation = Quaternion.Euler(0f, m.rot, 0f);
                go.SetActive(true);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // LASER & GRID SIMULATION
        // ════════════════════════════════════════════════════════════════
        bool LaserSolved()
        {
            if (allLasers == null || allLasers.Length == 0)
                allLasers = FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);
            foreach (var l in allLasers) if (l != null && l.IsHittingReceiver) return true;
            return false;
        }

        bool GridSolved()
        {
            bool any = false;
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue; any = true;
                    if (!SimLaser(new Vector2Int(x, y))) return false;
                }
            return any;
        }

        bool SimLaser(Vector2Int em)
        {
            Vector2Int pos = em, dir = EmDir(em);
            var seen = new HashSet<(Vector2Int, Vector2Int)>(); int bends = 0;
            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir; if (!InB(pos)) break;
                var s = (pos, dir); if (seen.Contains(s)) break; seen.Add(s);
                TileType t = grid.GetTile(pos.x, pos.y);
                if (t == TileType.Receiver) return bends >= 1;
                if (t == TileType.Wall || t == TileType.Emitter) break;
                if (t == TileType.Mirror || t == TileType.Refractor)
                {
                    float r = GetR(pos);
                    if (Mathf.Abs(Mathf.DeltaAngle(r, 0f)) > 5f)
                    { dir = (t == TileType.Mirror) ? Bounce(dir, pos) : Refract(dir, pos); bends++; }
                    continue;
                }
            }
            return false;
        }

        int GridLen()
        {
            int total = 0;
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == TileType.Emitter) total += SimLen(new Vector2Int(x, y));
            return total;
        }

        int SimLen(Vector2Int em)
        {
            int c = 0; Vector2Int pos = em, dir = EmDir(em); var seen = new HashSet<(Vector2Int, Vector2Int)>();
            for (int i = 0; i < grid.Width * grid.Height * 2; i++)
            {
                pos += dir; if (!InB(pos)) break; var s = (pos, dir); if (seen.Contains(s)) break; seen.Add(s); c++;
                TileType t = grid.GetTile(pos.x, pos.y);
                if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                if (t == TileType.Mirror || t == TileType.Refractor)
                {
                    float r = GetR(pos); if (Mathf.Abs(Mathf.DeltaAngle(r, 0f)) > 5f)
                        dir = (t == TileType.Mirror) ? Bounce(dir, pos) : Refract(dir, pos); continue;
                }
            }
            return c;
        }

        // Trace laser path and return empty cells per segment
        List<List<Vector2Int>> TraceSegments()
        {
            var segs = new List<List<Vector2Int>>();
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue;
                    Vector2Int pos = new Vector2Int(x, y), dir = EmDir(pos);
                    var seen = new HashSet<(Vector2Int, Vector2Int)>();
                    var seg = new List<Vector2Int>();
                    for (int i = 0; i < grid.Width * grid.Height * 2; i++)
                    {
                        pos += dir; if (!InB(pos)) break; var s = (pos, dir); if (seen.Contains(s)) break; seen.Add(s);
                        TileType t = grid.GetTile(pos.x, pos.y);
                        if (t == TileType.Empty) seg.Add(pos);
                        if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) { segs.Add(seg); break; }
                        if (t == TileType.Mirror || t == TileType.Refractor)
                        {
                            segs.Add(seg); seg = new List<Vector2Int>();
                            float r = GetR(pos); Vector2Int nd = (t == TileType.Mirror) ? Bounce(dir, pos) : Refract(dir, pos);
                            dir = nd; continue;
                        }
                    }
                }
            return segs;
        }

        List<Vector2Int> GetLaserCells()
        {
            var cells = new List<Vector2Int>();
            foreach (var seg in TraceSegments()) foreach (var c in seg) if (!cells.Contains(c)) cells.Add(c);
            return cells;
        }

        float GetR(Vector2Int cell)
        { if (_rot.TryGetValue(cell, out float v)) return v; return GetYRot(cell); }

        Vector2Int Bounce(Vector2Int d, Vector2Int cell)
        {
            float a = GetR(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 45f)) < 22.5f || Mathf.Abs(Mathf.DeltaAngle(a, 225f)) < 22.5f)
                ? new Vector2Int(d.y, d.x) : new Vector2Int(-d.y, -d.x);
        }

        Vector2Int Refract(Vector2Int d, Vector2Int cell)
        {
            float a = GetR(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 0f)) < 22.5f || Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22.5f)
                ? new Vector2Int(-d.y, d.x) : new Vector2Int(d.y, -d.x);
        }

        float GetYRot(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject obj) && obj != null)
                return obj.transform.eulerAngles.y; return 0f;
        }

        Vector2Int EmDir(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject go) && go != null)
            {
                Vector3 f = go.transform.forward;
                if (Mathf.Abs(f.x) >= Mathf.Abs(f.z)) return f.x >= 0 ? Vector2Int.right : Vector2Int.left;
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

        // ════════════════════════════════════════════════════════════════
        // EXIT DOOR
        // ════════════════════════════════════════════════════════════════
        IEnumerator ExitDoor()
        {
            yield return new WaitForSeconds(0.4f);
            Vector2Int dc = FindFirst(TileType.Door);
            if (dc != -Vector2Int.one)
            {
                grid.SetTile(dc.x, dc.y, TileType.Empty);
                if (gridVisualizer.SpawnedObjects.TryGetValue(dc, out GameObject dgo) && dgo != null) Destroy(dgo);
                gridVisualizer.SpawnedObjects.Remove(dc);
                yield return StartCoroutine(WalkTo(dc));
                Vector3 bw = gridVisualizer.GridToWorld((dc + OutDir(dc)).x, (dc + OutDir(dc)).y);
                float to = 3f;
                while (Vector3.Distance(transform.position, bw) > 0.2f && to > 0)
                {
                    to -= Time.deltaTime;
                    cc.Move((bw - transform.position).normalized * moveSpeed * Time.deltaTime + Vector3.down * 2f * Time.deltaTime);
                    yield return null;
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
                Vector3 wt = gridVisualizer.GridToWorld(step.x, step.y); wt.y = transform.position.y;
                float to = 5f;
                while (Vector3.Distance(transform.position, wt) > 0.1f && to > 0)
                {
                    to -= Time.deltaTime; Vector3 d = (wt - transform.position).normalized;
                    if (d.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(transform.rotation,
                            Quaternion.LookRotation(d), rotationSpeed * Time.deltaTime);
                    cc.Move(d * moveSpeed * Time.deltaTime + Vector3.down * 2f * Time.deltaTime); yield return null;
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

        // ════════════════════════════════════════════════════════════════
        // UTILITIES
        // ════════════════════════════════════════════════════════════════
        bool Already(Move m) => triedMoves.Contains((m.to, m.rot));
        void Remember(Move m) => triedMoves.Add((m.to, m.rot));

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
