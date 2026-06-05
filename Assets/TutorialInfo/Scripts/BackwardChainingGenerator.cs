using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Core
{
    /// <summary>
    /// v12 — Maze-style interior obstacles placed AFTER solution path is fixed,
    /// so they never block the guaranteed laser route.
    /// </summary>
    public class BackwardChainingGenerator
    {
        private GridModel grid;
        private int W, H;

        // Store solution path cells so obstacles never block them
        private HashSet<Vector2Int> solutionCells = new HashSet<Vector2Int>();

        public BackwardChainingGenerator(GridModel g)
        { grid = g; W = g.Width; H = g.Height; }

        public void GenerateValidPuzzle(int steps, int emitterCount = 1)
        {
            grid.ClearGrid();
            solutionCells.Clear();

            // 1. Outer walls
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                    if (x == 0 || x == W - 1 || y == 0 || y == H - 1)
                        grid.SetTile(x, y, TileType.Wall);

            // 2. Door + Receiver
            Vector2Int door = RandomSafeWallCell();
            grid.SetTile(door.x, door.y, TileType.Door);
            Vector2Int recv = ReceiverNearDoor(door);
            grid.SetTile(recv.x, recv.y, TileType.Receiver);
            solutionCells.Add(recv);
            solutionCells.Add(door);

            // 3. Main chain — record all cells used
            BuildChain(recv, Mathf.Max(steps, 3));

            // 4. Extra emitters
            for (int e = 1; e < emitterCount; e++)
            {
                Vector2Int start = RandomInnerEmpty();
                if (start != -Vector2Int.one)
                    BuildChain(start, Mathf.Max(3, steps - 1));
            }

            // 5. Decoy objects (NOT on solution cells)
            int decoys = Mathf.Clamp(steps - 1, 2, 5);
            PlaceDecoys(decoys);

            // 6. Maze walls — placed LAST, never on solution cells
            PlaceMazeObstacles(steps);

            Debug.Log($"[PCG] door@{door} recv@{recv} steps={steps} solutionCells={solutionCells.Count}");
        }

       
        // MAZE OBSTACLES — placed after solution path is recorded
        // Uses corridor-style segments to create a maze feel
        
        void PlaceMazeObstacles(int steps)
        {
            int attempts = Mathf.Clamp(steps * 3, 6, 20);

            for (int a = 0; a < attempts * 5 && attempts > 0; a++)
            {
                int cx = Random.Range(3, W - 3);
                int cy = Random.Range(3, H - 3);
                if (IsOnSolutionPath(cx, cy, radius: 1)) continue;

                // Short wall segment (1-3 cells)
                bool horizontal = (Random.value > 0.5f);
                int length = Random.Range(1, 4);
                bool canPlace = true;

                // Pre-check: none of the cells overlap solution path
                for (int i = 0; i < length; i++)
                {
                    int nx = horizontal ? cx + i : cx;
                    int ny = horizontal ? cy : cy + i;
                    if (nx <= 1 || nx >= W - 2 || ny <= 1 || ny >= H - 2) { canPlace = false; break; }
                    if (IsOnSolutionPath(nx, ny, radius: 1)) { canPlace = false; break; }
                    if (grid.GetTile(nx, ny) != TileType.Empty) { canPlace = false; break; }
                }

                if (!canPlace) continue;

                for (int i = 0; i < length; i++)
                {
                    int nx = horizontal ? cx + i : cx;
                    int ny = horizontal ? cy : cy + i;
                    grid.SetTile(nx, ny, TileType.Wall);
                }
                attempts--;
            }
        }

        bool IsOnSolutionPath(int x, int y, int radius = 0)
        {
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                    if (solutionCells.Contains(new Vector2Int(x + dx, y + dy)))
                        return true;
            return false;
        }

        
        // CHAIN BUILDER
        
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
                    TileType bend = (Random.value > 0.35f) ? TileType.Mirror : TileType.Refractor;
                    if (grid.GetTile(cx, cy) == TileType.Empty)
                    {
                        grid.SetTile(cx, cy, bend);
                        solutionCells.Add(new Vector2Int(cx, cy));
                    }
                    dir = Rotate90(dir);
                }
            }
            PlaceEmitterAgainstWall(cx, cy, dir);
        }

        void PlaceDecoys(int count)
        {
            for (int i = 0; i < count * 10 && count > 0; i++)
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
                if (grid.GetTile(nx, ny) == TileType.Empty) { cx = nx; cy = ny; solutionCells.Add(new Vector2Int(cx, cy)); }
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

        //  Helpers
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
            if (w.x == 0) return Vector2Int.right;
            if (w.x == W - 1) return Vector2Int.left;
            if (w.y == 0) return new Vector2Int(0, 1);
            return new Vector2Int(0, -1);
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
            for (int x = 3; x < W - 3; x++) { if (grid.GetTile(x, 1) == TileType.Empty) return new Vector2Int(x, 1); if (grid.GetTile(x, H - 2) == TileType.Empty) return new Vector2Int(x, H - 2); }
            for (int y = 3; y < H - 3; y++) { if (grid.GetTile(1, y) == TileType.Empty) return new Vector2Int(1, y); if (grid.GetTile(W - 2, y) == TileType.Empty) return new Vector2Int(W - 2, y); }
            return -Vector2Int.one;
        }

        Vector2Int RandomInwardDir(int x, int y)
        {
            var dirs = new List<Vector2Int> { Vector2Int.right, Vector2Int.left, new Vector2Int(0, 1), new Vector2Int(0, -1) };
            for (int i = dirs.Count - 1; i > 0; i--) { int j = Random.Range(0, i + 1); var t = dirs[i]; dirs[i] = dirs[j]; dirs[j] = t; }
            foreach (var d in dirs) { int nx = x + d.x * 2, ny = y + d.y * 2; if (nx > 1 && nx < W - 2 && ny > 1 && ny < H - 2) return d; }
            return dirs[0];
        }

        Vector2Int RandomInnerEmpty()
        {
            for (int a = 0; a < 100; a++) { int x = Random.Range(2, W - 2), y = Random.Range(2, H - 2); if (grid.GetTile(x, y) == TileType.Empty) return new Vector2Int(x, y); }
            return -Vector2Int.one;
        }

        Vector2Int Rotate90(Vector2Int d) => (Random.value > 0.5f) ? new Vector2Int(-d.y, d.x) : new Vector2Int(d.y, -d.x);
    }
}
