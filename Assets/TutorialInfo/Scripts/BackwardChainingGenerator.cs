using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Core
{
    /// <summary>
    /// Backward Chaining Generator v9
    
    /// Key rules:
    ///   - Door   — on outer wall, ≥3 cells from any corner
    ///   - Receiver — INNER cell (NOT on wall), beside door, ≥1 Mirror/Refractor
    ///                guaranteed between Emitter and Receiver
    ///   - Emitter — inner-edge cell flush against a wall, NOT corner zone
    ///   - Laser path MUST pass through ≥1 bend node before reaching Receiver
    
    /// </summary>
    public class BackwardChainingGenerator
    {
        private GridModel grid;
        private int W, H;

        public BackwardChainingGenerator(GridModel g)
        { grid = g; W = g.Width; H = g.Height; }

        public void GenerateValidPuzzle(int steps, int emitterCount = 1)
        {
            grid.ClearGrid();

            // 1. Outer walls
            for (int x = 0; x < W; x++)
                for (int y = 0; y < H; y++)
                    if (x == 0 || x == W - 1 || y == 0 || y == H - 1)
                        grid.SetTile(x, y, TileType.Wall);

            // 2. Door on wall, safe from corners
            Vector2Int door = RandomSafeWallCell();
            grid.SetTile(door.x, door.y, TileType.Door);

            // 3. Receiver — INNER cell near door (1 step inward + 1-2 sideways)
            //    Must NOT be on the wall itself, and NOT adjacent to door
            Vector2Int recv = ReceiverNearDoor(door);
            grid.SetTile(recv.x, recv.y, TileType.Receiver);

            // 4. Main chain: Receiver ← bends ← Emitter
            //    Force steps ≥ 2 so there is always ≥1 bend between Emitter and Receiver
            int mainSteps = Mathf.Max(steps, 2);
            BuildChain(recv, mainSteps);

            // 5. Extra emitters
            for (int e = 1; e < emitterCount; e++)
            {
                Vector2Int start = RandomInnerEmpty();
                if (start != -Vector2Int.one)
                    BuildChain(start, Mathf.Max(2, steps - 1));
            }

            Debug.Log($"[PCG] door@{door} recv@{recv} steps={steps} emitters={emitterCount}");
        }

        
        // CHAIN BUILDER — builds backward: recv → bends → emitter
        
        void BuildChain(Vector2Int chainStart, int steps)
        {
            int cx = chainStart.x, cy = chainStart.y;
            Vector2Int dir = RandomInwardDir(cx, cy);

            for (int i = 0; i < steps; i++)
            {
                // Walk random steps in current direction (stay inside inner area)
                int stepLen = Random.Range(2, 5);
                for (int s = 0; s < stepLen; s++)
                {
                    int nx = cx + dir.x, ny = cy + dir.y;
                    if (nx <= 1 || nx >= W - 2 || ny <= 1 || ny >= H - 2) break;
                    cx = nx; cy = ny;
                }

                // Place a bend (Mirror or Refractor) — guaranteed at every step except last
                if (i < steps - 1)
                {
                    TileType bend = (Random.value > 0.4f) ? TileType.Mirror : TileType.Refractor;
                    if (grid.GetTile(cx, cy) == TileType.Empty)
                        grid.SetTile(cx, cy, bend);
                    dir = Rotate90(dir);
                }
            }

            // Place Emitter flush against the nearest wall
            PlaceEmitterAgainstWall(cx, cy, dir);
        }

        void PlaceEmitterAgainstWall(int cx, int cy, Vector2Int dir)
        {
            for (int s = 0; s < Mathf.Max(W, H); s++)
            {
                int nx = cx + dir.x, ny = cy + dir.y;
                bool nextIsBoundary =
                    nx <= 0 || nx >= W - 1 || ny <= 0 || ny >= H - 1 ||
                    grid.GetTile(nx, ny) == TileType.Wall ||
                    grid.GetTile(nx, ny) == TileType.Door;

                if (nextIsBoundary)
                {
                    if (grid.GetTile(cx, cy) == TileType.Empty && !IsCornerZone(cx, cy))
                        grid.SetTile(cx, cy, TileType.Emitter);
                    else
                    {
                        var fb = FindSafeInnerEdgeEmpty();
                        if (fb != -Vector2Int.one) grid.SetTile(fb.x, fb.y, TileType.Emitter);
                    }
                    return;
                }

                if (grid.GetTile(nx, ny) == TileType.Empty)
                { cx = nx; cy = ny; }
                else
                {
                    if (grid.GetTile(cx, cy) == TileType.Empty && !IsCornerZone(cx, cy))
                        grid.SetTile(cx, cy, TileType.Emitter);
                    return;
                }
            }

            if (grid.GetTile(cx, cy) == TileType.Empty && !IsCornerZone(cx, cy))
                grid.SetTile(cx, cy, TileType.Emitter);
        }

        
        // RECEIVER PLACEMENT — inner cell near door, never on wall
       
        Vector2Int ReceiverNearDoor(Vector2Int door)
        {
            // Step inward from door to get the inner-edge cell (1 step)
            Vector2Int inDir = InwardDirFromWall(door);
            Vector2Int inner = door + inDir; // 1 step inside

            // Now slide along the wall direction to be "beside" the door
            // Wall direction = perpendicular to inward direction
            Vector2Int wallDir = new Vector2Int(inDir.y, inDir.x); // 90° rotation of inDir

            // Try offsets: 2 and 3 cells sideways from inner point
            int[] offsets = { 2, 3, -2, -3, 1, -1 };
            foreach (int offset in offsets)
            {
                Vector2Int candidate = inner + wallDir * offset;
                if (!InBounds(candidate.x, candidate.y)) continue;
                if (IsCornerZone(candidate.x, candidate.y)) continue;

                TileType t = grid.GetTile(candidate.x, candidate.y);
                // Must be empty inner cell (not wall, not door)
                if (t == TileType.Empty || t == TileType.Wall)
                {
                    // Make sure it's not on the outer wall
                    if (candidate.x > 0 && candidate.x < W - 1 &&
                        candidate.y > 0 && candidate.y < H - 1)
                    {
                        return candidate;
                    }
                }
            }

            // Fallback: 2 steps inward from door
            Vector2Int fallback = door + inDir * 2;
            if (InBounds(fallback.x, fallback.y) && !IsCornerZone(fallback.x, fallback.y))
                return fallback;

            return door + inDir; // last resort
        }

        Vector2Int InwardDirFromWall(Vector2Int wall)
        {
            if (wall.x == 0) return Vector2Int.right;
            if (wall.x == W - 1) return Vector2Int.left;
            if (wall.y == 0) return new Vector2Int(0, 1);
            return new Vector2Int(0, -1);
        }

        
        // HELPERS
        
        Vector2Int RandomSafeWallCell()
        {
            var candidates = new List<Vector2Int>();
            int safe = 3;
            for (int x = safe; x < W - safe; x++)
            {
                candidates.Add(new Vector2Int(x, 0));
                candidates.Add(new Vector2Int(x, H - 1));
            }
            for (int y = safe; y < H - safe; y++)
            {
                candidates.Add(new Vector2Int(0, y));
                candidates.Add(new Vector2Int(W - 1, y));
            }
            if (candidates.Count == 0) return new Vector2Int(W / 2, 0);
            return candidates[Random.Range(0, candidates.Count)];
        }

        bool IsCornerZone(int x, int y)
        {
            int m = 2;
            return (x <= m || x >= W - 1 - m) && (y <= m || y >= H - 1 - m);
        }

        bool InBounds(int x, int y) => x > 0 && x < W - 1 && y > 0 && y < H - 1;

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
            var dirs = new List<Vector2Int> {
                Vector2Int.right, Vector2Int.left,
                new Vector2Int(0,1), new Vector2Int(0,-1)
            };
            for (int i = dirs.Count - 1; i > 0; i--)
            { int j = Random.Range(0, i + 1); var t = dirs[i]; dirs[i] = dirs[j]; dirs[j] = t; }
            foreach (var d in dirs)
            {
                int nx = x + d.x * 2, ny = y + d.y * 2;
                if (nx > 1 && nx < W - 2 && ny > 1 && ny < H - 2) return d;
            }
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

        Vector2Int Rotate90(Vector2Int d) =>
            (Random.value > 0.5f)
                ? new Vector2Int(-d.y, d.x)
                : new Vector2Int(d.y, -d.x);
    }
}