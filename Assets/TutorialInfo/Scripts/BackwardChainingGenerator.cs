using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Core
{
    /// <summary>
    /// v14 — Pillar-based maze obstacles
    /// Obstacles are single-cell pillars only (no L-shapes or long segments)
    /// → guarantees no dead zones, always walkable around each pillar
    /// → placed with strict radius-2 buffer from all solution cells
    /// → placed with radius-1 buffer from each other (no clusters)
    /// </summary>
    public class BackwardChainingGenerator
    {
        private GridModel grid;
        private int W, H;
        private HashSet<Vector2Int> solutionCells = new HashSet<Vector2Int>();
        private HashSet<Vector2Int> pillarCells = new HashSet<Vector2Int>();

        public int SolutionObjectCount { get; private set; }
        public int DecoyCount { get; private set; }
        public int TotalObjectCount => SolutionObjectCount + DecoyCount;

        public BackwardChainingGenerator(GridModel g)
        { grid = g; W = g.Width; H = g.Height; }

        public void GenerateValidPuzzle(int steps, int emitterCount = 1, int decoys = 1)
        {
            grid.ClearGrid();
            solutionCells.Clear();
            pillarCells.Clear();
            SolutionObjectCount = 0; DecoyCount = 0;

            // 1. Outer walls
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
                    if (x == 0 || x == W - 1 || y == 0 || y == H - 1) grid.SetTile(x, y, TileType.Wall);

            // 2. Door + Receiver
            Vector2Int door = RandomSafeWallCell();
            grid.SetTile(door.x, door.y, TileType.Door);
            Vector2Int recv = ReceiverNearDoor(door);
            grid.SetTile(recv.x, recv.y, TileType.Receiver);
            solutionCells.Add(recv); solutionCells.Add(door);

            // 3. Main chain
            BuildChain(recv, Mathf.Max(steps, 2));

            // 4. Extra emitters
            for (int e = 1; e < emitterCount; e++)
            {
                var s = RandomInnerEmpty();
                if (s != -Vector2Int.one) BuildChain(s, Mathf.Max(2, steps - 1));
            }

            // 5. Decoys
            PlaceDecoys(decoys);
            DecoyCount = decoys;

            // 6. Pillar obstacles — single cells, well-separated, never on solution
            PlacePillars(steps);

            Debug.Log($"[PCG] door@{door} recv@{recv} steps={steps} " +
                      $"solutionObjs={SolutionObjectCount} decoys={DecoyCount}");
        }

        // ════════════════════════════════════════════════════════════════
        // PILLAR OBSTACLES
        // Single-cell walls, scattered randomly, never adjacent to solution
        // Always leave a walkable path around them (pillar ≠ wall line)
        // ════════════════════════════════════════════════════════════════
        void PlacePillars(int steps)
        {
            // Number of pillars scales with difficulty but stays moderate
            int targetPillars = Mathf.Clamp(steps * 2, 3, 10);
            int attempts = targetPillars * 15;

            for (int a = 0; a < attempts && targetPillars > 0; a++)
            {
                int px = Random.Range(2, W - 2);
                int py = Random.Range(2, H - 2);

                // Must be empty
                if (grid.GetTile(px, py) != TileType.Empty) continue;

                // Must not be on or adjacent to solution path (radius 2)
                if (IsOnSolutionPath(px, py, 2)) continue;

                // Must not be adjacent to another pillar (keep pillars separated)
                if (IsAdjacentToPillar(px, py)) continue;

                // Must not create a 2x2 wall block (would create dead zone)
                if (Would2x2Block(px, py)) continue;

                // Must not completely block a corridor (check all 4 neighbours remain passable)
                if (WouldBlockCorridor(px, py)) continue;

                grid.SetTile(px, py, TileType.Wall);
                pillarCells.Add(new Vector2Int(px, py));
                targetPillars--;
            }
        }

        bool IsAdjacentToPillar(int x, int y)
        {
            int[] dx = { 0, 0, 1, -1 };
            int[] dy = { 1, -1, 0, 0 };
            for (int i = 0; i < 4; i++)
                if (pillarCells.Contains(new Vector2Int(x + dx[i], y + dy[i]))) return true;
            return false;
        }

        bool Would2x2Block(int x, int y)
        {
            // Check if placing a wall here would form a 2x2 wall block
            int[,] corners = { { 0, 0 }, { 1, 0 }, { 0, 1 }, { 1, 1 } };
            for (int cx = x - 1; cx <= x; cx++)
                for (int cy = y - 1; cy <= y; cy++)
                {
                    bool allWall = true;
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = cx + corners[i, 0], ny = cy + corners[i, 1];
                        if (nx == x && ny == y) continue; // the new pillar
                        if (nx < 0 || nx >= W || ny < 0 || ny >= H) { allWall = false; break; }
                        TileType t = grid.GetTile(nx, ny);
                        if (t != TileType.Wall) { allWall = false; break; }
                    }
                    if (allWall) return true;
                }
            return false;
        }

        bool WouldBlockCorridor(int x, int y)
        {
            // A pillar blocks a corridor if it's in a 1-cell wide passage
            // Check: horizontal passage blocked (both N and S are walls/pillars)
            bool nWall = IsWallOrEdge(x, y + 1);
            bool sWall = IsWallOrEdge(x, y - 1);
            bool eWall = IsWallOrEdge(x + 1, y);
            bool wWall = IsWallOrEdge(x - 1, y);

            // Would completely block horizontal or vertical passage
            if (nWall && sWall) return true;
            if (eWall && wWall) return true;
            return false;
        }

        bool IsWallOrEdge(int x, int y)
        {
            if (x < 0 || x >= W || y < 0 || y >= H) return true;
            TileType t = grid.GetTile(x, y);
            return t == TileType.Wall;
        }

        bool IsOnSolutionPath(int x, int y, int radius)
        {
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                    if (solutionCells.Contains(new Vector2Int(x + dx, y + dy))) return true;
            return false;
        }

        // ════════════════════════════════════════════════════════════════
        // CHAIN BUILDER
        // ════════════════════════════════════════════════════════════════
        void BuildChain(Vector2Int chainStart, int steps)
        {
            int cx = chainStart.x, cy = chainStart.y;
            Vector2Int dir = RandomInwardDir(cx, cy);

            for (int i = 0; i < steps; i++)
            {
                int stepLen = Random.Range(3, 7);
                for (int s = 0; s < stepLen; s++)
                {
                    int nx = cx + dir.x, ny = cy + dir.y;
                    if (nx <= 1 || nx >= W - 2 || ny <= 1 || ny >= H - 2) break;
                    cx = nx; cy = ny;
                    solutionCells.Add(new Vector2Int(cx, cy));
                }

                if (i < steps - 1)
                {
                    TileType bend = TileType.Mirror;
                    if (grid.GetTile(cx, cy) == TileType.Empty)
                    {
                        grid.SetTile(cx, cy, bend);
                        solutionCells.Add(new Vector2Int(cx, cy));
                        SolutionObjectCount++;
                    }
                    dir = Rotate90(dir);
                }
            }
            PlaceEmitterAgainstWall(cx, cy, dir);
        }

        void PlaceDecoys(int count)
        {
            for (int i = 0; i < count * 20 && count > 0; i++)
            {
                int x = Random.Range(2, W - 2), y = Random.Range(2, H - 2);
                if (grid.GetTile(x, y) != TileType.Empty) continue;
                if (IsCornerZone(x, y)) continue;
                if (solutionCells.Contains(new Vector2Int(x, y))) continue;
                grid.SetTile(x, y, (Random.value > 0.5f) ? TileType.Mirror : TileType.Refractor);
                count--;
            }
        }

        void PlaceEmitterAgainstWall(int cx, int cy, Vector2Int dir)
        {
            for (int s = 0; s < Mathf.Max(W, H); s++)
            {
                int nx = cx + dir.x, ny = cy + dir.y;
                bool boundary = nx <= 0 || nx >= W - 1 || ny <= 0 || ny >= H - 1 ||
                    grid.GetTile(nx, ny) == TileType.Wall || grid.GetTile(nx, ny) == TileType.Door;
                if (boundary)
                {
                    if (grid.GetTile(cx, cy) == TileType.Empty && !IsCornerZone(cx, cy))
                    { grid.SetTile(cx, cy, TileType.Emitter); solutionCells.Add(new Vector2Int(cx, cy)); }
                    else
                    {
                        var fb = FindSafeInnerEdgeEmpty();
                        if (fb != -Vector2Int.one) { grid.SetTile(fb.x, fb.y, TileType.Emitter); solutionCells.Add(fb); }
                    }
                    return;
                }
                if (grid.GetTile(nx, ny) == TileType.Empty)
                { cx = nx; cy = ny; solutionCells.Add(new Vector2Int(cx, cy)); }
                else
                {
                    if (grid.GetTile(cx, cy) == TileType.Empty && !IsCornerZone(cx, cy))
                    { grid.SetTile(cx, cy, TileType.Emitter); solutionCells.Add(new Vector2Int(cx, cy)); }
                    return;
                }
            }
            if (grid.GetTile(cx, cy) == TileType.Empty && !IsCornerZone(cx, cy))
            { grid.SetTile(cx, cy, TileType.Emitter); solutionCells.Add(new Vector2Int(cx, cy)); }
        }

        // ── Helpers ───────────────────────────────────────────────────
        Vector2Int ReceiverNearDoor(Vector2Int door)
        {
            Vector2Int inDir = InwardDirFromWall(door), inner = door + inDir;
            Vector2Int wallDir = new Vector2Int(inDir.y, inDir.x);
            int[] offsets = { 2, 3, -2, -3, 1, -1 };
            foreach (int o in offsets)
            {
                Vector2Int c = inner + wallDir * o;
                if (!InBoundsInner(c.x, c.y) || IsCornerZone(c.x, c.y)) continue;
                TileType t = grid.GetTile(c.x, c.y);
                if ((t == TileType.Empty || t == TileType.Wall) && c.x > 0 && c.x < W - 1 && c.y > 0 && c.y < H - 1) return c;
            }
            Vector2Int fb = door + inDir * 2;
            return (InBoundsInner(fb.x, fb.y) && !IsCornerZone(fb.x, fb.y)) ? fb : door + inDir;
        }

        Vector2Int InwardDirFromWall(Vector2Int w)
        {
            if (w.x == 0) return Vector2Int.right; if (w.x == W - 1) return Vector2Int.left;
            if (w.y == 0) return new Vector2Int(0, 1); return new Vector2Int(0, -1);
        }

        Vector2Int RandomSafeWallCell()
        {
            var c = new List<Vector2Int>();
            for (int x = 3; x < W - 3; x++) { c.Add(new Vector2Int(x, 0)); c.Add(new Vector2Int(x, H - 1)); }
            for (int y = 3; y < H - 3; y++) { c.Add(new Vector2Int(0, y)); c.Add(new Vector2Int(W - 1, y)); }
            return c.Count > 0 ? c[Random.Range(0, c.Count)] : new Vector2Int(W / 2, 0);
        }

        bool IsCornerZone(int x, int y) { int m = 2; return (x <= m || x >= W - 1 - m) && (y <= m || y >= H - 1 - m); }
        bool InBoundsInner(int x, int y) => x > 0 && x < W - 1 && y > 0 && y < H - 1;

        Vector2Int FindSafeInnerEdgeEmpty()
        {
            for (int x = 3; x < W - 3; x++)
            {
                if (grid.GetTile(x, 1) == TileType.Empty) return new Vector2Int(x, 1);
                if (grid.GetTile(x, H - 2) == TileType.Empty) return new Vector2Int(x, H - 2);
            }
            for (int y = 3; y < H - 3; y++)
            {
                if (grid.GetTile(1, y) == TileType.Empty) return new Vector2Int(1, y);
                if (grid.GetTile(W - 2, y) == TileType.Empty) return new Vector2Int(W - 2, y);
            }
            return -Vector2Int.one;
        }

        Vector2Int RandomInwardDir(int x, int y)
        {
            var dirs = new List<Vector2Int>{Vector2Int.right,Vector2Int.left,
              new Vector2Int(0,1),new Vector2Int(0,-1)};
            for (int i = dirs.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); var t = dirs[i]; dirs[i] = dirs[j]; dirs[j] = t; }
            foreach (var d in dirs) { int nx = x + d.x * 2, ny = y + d.y * 2; if (nx > 1 && nx < W - 2 && ny > 1 && ny < H - 2) return d; }
            return dirs[0];
        }

        Vector2Int RandomInnerEmpty()
        {
            for (int a = 0; a < 100; a++)
            {
                int x = Random.Range(2, W - 2), y = Random.Range(2, H - 2);
                if (grid.GetTile(x, y) == TileType.Empty) return new Vector2Int(x, y);
            }
            return -Vector2Int.one;
        }

        Vector2Int Rotate90(Vector2Int d) => (Random.value > 0.5f)
            ? new Vector2Int(-d.y, d.x) : new Vector2Int(d.y, -d.x);
    }
}
