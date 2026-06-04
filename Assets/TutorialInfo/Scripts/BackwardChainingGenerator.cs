using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Core
{
    public class BackwardChainingGenerator
    {
        private GridModel grid;
        private int maxWidth;
        private int maxHeight;

        public BackwardChainingGenerator(GridModel targetGrid)
        {
            this.grid = targetGrid;
            this.maxWidth = targetGrid.Width;
            this.maxHeight = targetGrid.Height;
        }

        // ── Public API ────────────────────────────────────────────────────────
        // steps        = number of mirror/refractor bends
        // emitterCount = how many Emitters to place (default 1)
        public void GenerateValidPuzzle(int steps, int emitterCount = 1)
        {
            grid.ClearGrid();

            // 1. Outer walls
            for (int x = 0; x < maxWidth; x++)
                for (int y = 0; y < maxHeight; y++)
                    if (x == 0 || x == maxWidth - 1 || y == 0 || y == maxHeight - 1)
                        grid.SetTile(x, y, TileType.Wall);

            // 2. Place Door randomly on one of the four walls (not a corner)
            PlaceRandomDoor();

            // 3. Place Receiver one step inside from the door
            Vector2Int doorPos = FindCellOfType(TileType.Door);
            Vector2Int receiverPos = GetReceiverFromDoor(doorPos);
            grid.SetTile(receiverPos.x, receiverPos.y, TileType.Receiver);

            // 4. Backward-chain mirrors/refractors from Receiver → Emitter(s)
            // First emitter is on the main chain; extras are decoy chains
            for (int e = 0; e < emitterCount; e++)
            {
                Vector2Int startPos = (e == 0) ? receiverPos : FindRandomEmptyInner();
                if (startPos == -Vector2Int.one) continue;
                BuildChain(startPos, steps);
            }

            Debug.Log($"[PCG] Puzzle built — {steps} bends, {emitterCount} emitter(s), door at {doorPos}.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        void PlaceRandomDoor()
        {
            // Collect all wall cells that are not corners
            var candidates = new List<Vector2Int>();
            for (int x = 1; x < maxWidth - 1; x++)
            {
                candidates.Add(new Vector2Int(x, 0));
                candidates.Add(new Vector2Int(x, maxHeight - 1));
            }
            for (int y = 1; y < maxHeight - 1; y++)
            {
                candidates.Add(new Vector2Int(0, y));
                candidates.Add(new Vector2Int(maxWidth - 1, y));
            }

            Vector2Int chosen = candidates[Random.Range(0, candidates.Count)];
            grid.SetTile(chosen.x, chosen.y, TileType.Door);
        }

        Vector2Int GetReceiverFromDoor(Vector2Int door)
        {
            // Step one tile inward from the door
            if (door.x == 0) return new Vector2Int(1, door.y);
            if (door.x == maxWidth - 1) return new Vector2Int(maxWidth - 2, door.y);
            if (door.y == 0) return new Vector2Int(door.x, 1);
            return new Vector2Int(door.x, maxHeight - 2);
        }

        void BuildChain(Vector2Int startPos, int steps)
        {
            int currentX = startPos.x;
            int currentY = startPos.y;

            // Pick a random initial direction that points inward
            Vector2Int currentDir = RandomInwardDir(currentX, currentY);

            for (int i = 0; i < steps; i++)
            {
                int stepLength = Random.Range(2, 5);
                for (int s = 0; s < stepLength; s++)
                {
                    int nx = currentX + currentDir.x;
                    int ny = currentY + currentDir.y;
                    if (nx <= 1 || nx >= maxWidth - 2 || ny <= 1 || ny >= maxHeight - 2)
                        break;
                    currentX = nx;
                    currentY = ny;
                }

                if (i < steps - 1)
                {
                    TileType bend = (Random.value > 0.4f) ? TileType.Mirror : TileType.Refractor;
                    grid.SetTile(currentX, currentY, bend);
                    currentDir = Rotate90Degrees(currentDir);
                }
            }

            // Place emitter at chain end (only if cell is free)
            if (grid.GetTile(currentX, currentY) == TileType.Empty ||
                grid.GetTile(currentX, currentY) == TileType.Receiver)
            {
                grid.SetTile(currentX, currentY, TileType.Emitter);
            }
        }

        Vector2Int RandomInwardDir(int x, int y)
        {
            // Try all four; keep ones that point away from nearest wall
            var dirs = new List<Vector2Int>
            {
                Vector2Int.right, Vector2Int.left,
                new Vector2Int(0, 1), new Vector2Int(0, -1)
            };
            // Shuffle
            for (int i = dirs.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                var tmp = dirs[i]; dirs[i] = dirs[j]; dirs[j] = tmp;
            }
            // Prefer direction that keeps us inside for at least 2 steps
            foreach (var d in dirs)
            {
                int nx = x + d.x * 2;
                int ny = y + d.y * 2;
                if (nx > 1 && nx < maxWidth - 2 && ny > 1 && ny < maxHeight - 2)
                    return d;
            }
            return dirs[0];
        }

        Vector2Int FindCellOfType(TileType t)
        {
            for (int x = 0; x < maxWidth; x++)
                for (int y = 0; y < maxHeight; y++)
                    if (grid.GetTile(x, y) == t)
                        return new Vector2Int(x, y);
            return -Vector2Int.one;
        }

        Vector2Int FindRandomEmptyInner()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                int x = Random.Range(2, maxWidth - 2);
                int y = Random.Range(2, maxHeight - 2);
                if (grid.GetTile(x, y) == TileType.Empty)
                    return new Vector2Int(x, y);
            }
            return -Vector2Int.one;
        }

        private Vector2Int Rotate90Degrees(Vector2Int dir)
        {
            return (Random.value > 0.5f)
                ? new Vector2Int(-dir.y, dir.x)   // counter-clockwise
                : new Vector2Int(dir.y, -dir.x);  // clockwise
        }
    }
}