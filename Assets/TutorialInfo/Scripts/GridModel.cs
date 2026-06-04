using UnityEngine;

namespace LightPCG.Core
{
    // 1. Identify the objects located in the puzzle level.
    public enum TileType
    {
        Empty,       // space
        Wall,        // An impermeable wall
        Emitter,     // Light source (Source)
        Receiver,    // Target point for light reception (Goal)
        Mirror,      // Mirror reflects light
        Refractor,    // Refracting prism
        Door // Added a new type of door object for unlocking levels.
    }

    public class GridModel
    {
        // 2. Variables storing the level size and a 2D data table.
        public int Width { get; private set; }
        public int Height { get; private set; }
        private TileType[,] gridMatrix;

        // Constructor for creating a dummy table of the desired size.
        public GridModel(int width, int height)
        {
            Width = width;
            Height = height;
            gridMatrix = new TileType[width, height];
            ClearGrid();
        }

        // Function to clear the board to make it completely blank.
        public void ClearGrid()
        {
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    gridMatrix[x, y] = TileType.Empty;
                }
            }
        }

        // Function for placing an object at specified coordinates.
        public void SetTile(int x, int y, TileType type)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                gridMatrix[x, y] = type;
            }
        }

        // Function for retrieving coordinate data for use.
        public TileType GetTile(int x, int y)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                return gridMatrix[x, y];
            }
            return TileType.Wall; // If it goes beyond the edge of the board, assume it's a wall.
        }


        // A function for an AI agent to check if a grid square is traversable without actually colliding with it. Research calls this a Walkable Grid Check.
        public bool IsWalkable(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return false;

            TileType type = gridMatrix[x, y];
            // If it's an empty space or an open door, it's considered passable.
            // Wall, Mirror, Refractor, Emitter, and Receiver will return false to prevent the AI ??from walking through them.
            return (type == TileType.Empty || type == TileType.Door);
        }

        // Insert into GridModel.cs (at the end of the file, after the IsWalkable function)
        // Function picks object from old coordinates Move to place the new coordinates in the mathematical back-end table.
        public void MoveObjectOnGrid(int oldX, int oldY, int newX, int newY)
        {
            // 1. Check the safety rating of the table.
            if (oldX >= 0 && oldX < Width && oldY >= 0 && oldY < Height &&
                newX >= 0 && newX < Width && newY >= 0 && newY < Height)
            {
                // 2. Retrieve the object type from the previous location and store it in a temporary variable.
                TileType objectToMove = gridMatrix[oldX, oldY];

                // 3. Convert the old spot into an empty space (Empty).
                gridMatrix[oldX, oldY] = TileType.Empty;

                // 4. Place the object at the new coordinates.
                gridMatrix[newX, newY] = objectToMove;

                Debug.Log($"[Grid Research] Successfully moved object {objectToMove} from coordinates ({oldX},{oldY}) to ({newX},{newY}) on the grid!");
            }
        }
    }
}