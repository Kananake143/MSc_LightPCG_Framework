using LightPCG.Core;
using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    public class GridVisualizer : MonoBehaviour
    {
        [Header("Grid Size")]
        public int desiredWidth = 12;
        public int desiredHeight = 12;
        public float spacing = 1.1f;

        [Header("Difficulty")]
        [Range(2, 8)] public int minSteps = 3;
        [Range(3, 9)] public int maxSteps = 6;
        [Range(1, 3)] public int emitterCount = 1;

        [Header("Prefabs")]
        public GameObject emptyTilePrefab;
        public GameObject wallPrefab;
        public GameObject emitterPrefab;
        public GameObject receiverPrefab;
        public GameObject mirrorPrefab;
        public GameObject doorPrefab;
        public GameObject refractorPrefab;

        // Colours
        private static readonly Color CFloor = new Color(0.82f, 0.82f, 0.82f);
        private static readonly Color CWall = new Color(0.30f, 0.20f, 0.12f);
        private static readonly Color CEmitter = new Color(1.00f, 0.85f, 0.10f);
        private static readonly Color CReceiver = new Color(0.90f, 0.15f, 0.10f);
        private static readonly Color CMirror = new Color(0.65f, 0.88f, 1.00f);
        private static readonly Color CDoor = new Color(0.10f, 0.85f, 0.25f);
        private static readonly Color CRefractor = new Color(0.80f, 0.40f, 1.00f);
        private const float EE = 1.8f, ER = 1.8f, ED = 1.0f, ERf = 1.2f;

        [HideInInspector] public GridModel LevelGrid;
        [HideInInspector] public float Spacing => spacing;
        public Dictionary<Vector2Int, GameObject> SpawnedObjects
            = new Dictionary<Vector2Int, GameObject>();

        void Start() => GenerateLevel();

        public void GenerateLevel()
        {
            foreach (Transform c in transform) Destroy(c.gameObject);
            SpawnedObjects.Clear();
            LevelGrid = new GridModel(desiredWidth, desiredHeight);
            int steps = Random.Range(minSteps, maxSteps + 1);
            new BackwardChainingGenerator(LevelGrid).GenerateValidPuzzle(steps, emitterCount);
            BuildVisuals();
            Debug.Log($"[GridVisualizer] Level ready — {steps} bends, {emitterCount} emitter(s).");
        }

        public Vector3 GridToWorld(int x, int y)
        {
            float ox = (LevelGrid.Width - 1) * spacing / 2f;
            float oz = (LevelGrid.Height - 1) * spacing / 2f;
            return new Vector3((x * spacing) - ox, 0.5f, (y * spacing) - oz);
        }

        void BuildVisuals()
        {
            float ox = (LevelGrid.Width - 1) * spacing / 2f;
            float oz = (LevelGrid.Height - 1) * spacing / 2f;

            for (int x = 0; x < LevelGrid.Width; x++)
                for (int y = 0; y < LevelGrid.Height; y++)
                {
                    TileType t = LevelGrid.GetTile(x, y);
                    Vector3 basePos = new Vector3((x * spacing) - ox, 0f, (y * spacing) - oz);

                    // Floor always
                    if (emptyTilePrefab != null)
                    {
                        var fl = Instantiate(emptyTilePrefab, basePos, Quaternion.identity, transform);
                        fl.transform.localScale = new Vector3(1f, 0.1f, 1f);
                        ApplyColor(fl, CFloor);
                        EnsureCollider(fl);
                    }

                    if (t == TileType.Empty) continue;

                    GameObject prefab = GetPrefab(t);
                    if (prefab == null) continue;

                    Quaternion rot = Quaternion.identity;
                    if (t == TileType.Mirror)
                        rot = Quaternion.Euler(0f, 45f, 0f);
                    else if (t == TileType.Emitter)
                        rot = EmitterFacingRot(x, y);
                    else if (t == TileType.Receiver)
                        rot = ReceiverFacingRot(x, y); // face inward to "receive" laser

                    var obj = Instantiate(prefab, basePos + Vector3.up * 0.5f, rot, transform);
                    obj.transform.localScale = GetScale(t);
                    ColorObject(obj, t);
                    EnsureCollider(obj);
                    TrySetTag(obj, GetTag(t));

                    if (t != TileType.Wall)
                        SpawnedObjects[new Vector2Int(x, y)] = obj;
                }
        }

        // Emitter: flush against nearest wall, fires INWARD
        Quaternion EmitterFacingRot(int ex, int ey)
        {
            int dL = ex, dR = LevelGrid.Width - 1 - ex;
            int dB = ey, dT = LevelGrid.Height - 1 - ey;
            int m = Mathf.Min(dL, dR, dB, dT);
            if (m == dL) return Quaternion.LookRotation(Vector3.right);
            if (m == dR) return Quaternion.LookRotation(Vector3.left);
            if (m == dB) return Quaternion.LookRotation(Vector3.forward);
            return Quaternion.LookRotation(Vector3.back);
        }

        // Receiver: on a wall tile, face INWARD so its "sensor face" points into the room
        Quaternion ReceiverFacingRot(int rx, int ry)
        {
            // Same logic as emitter — face away from nearest wall
            int dL = rx, dR = LevelGrid.Width - 1 - rx;
            int dB = ry, dT = LevelGrid.Height - 1 - ry;
            int m = Mathf.Min(dL, dR, dB, dT);
            if (m == dL) return Quaternion.LookRotation(Vector3.right);
            if (m == dR) return Quaternion.LookRotation(Vector3.left);
            if (m == dB) return Quaternion.LookRotation(Vector3.forward);
            return Quaternion.LookRotation(Vector3.back);
        }

        //  Helpers 
        void TrySetTag(GameObject obj, string tag)
        {
            try { obj.tag = tag; }
            catch { Debug.LogWarning($"[GridVisualizer] Tag '{tag}' missing — add in Project Settings → Tags."); }
        }

        GameObject GetPrefab(TileType t)
        {
            switch (t)
            {
                case TileType.Wall: return wallPrefab;
                case TileType.Emitter: return emitterPrefab;
                case TileType.Receiver: return receiverPrefab;
                case TileType.Mirror: return mirrorPrefab;
                case TileType.Door: return doorPrefab;
                case TileType.Refractor: return refractorPrefab;
                default: return null;
            }
        }

        string GetTag(TileType t)
        {
            switch (t)
            {
                case TileType.Wall: return "Wall";
                case TileType.Emitter: return "Emitter";
                case TileType.Receiver: return "Receiver";
                case TileType.Mirror: return "Mirror";
                case TileType.Door: return "Door";
                case TileType.Refractor: return "Refractor";
                default: return "Untagged";
            }
        }

        Vector3 GetScale(TileType t)
        {
            switch (t)
            {
                case TileType.Wall: return new Vector3(1.1f, 3.0f, 1.1f);
                case TileType.Emitter: return new Vector3(0.9f, 0.9f, 0.9f); // compact cube, flush against wall
                case TileType.Mirror: return new Vector3(0.5f, 2.0f, 1.2f);
                case TileType.Receiver: return new Vector3(0.9f, 0.9f, 0.9f); // compact cube on wall beside door
                case TileType.Door: return new Vector3(0.3f, 2.5f, 1.0f);
                default: return Vector3.one;
            }
        }

        void ColorObject(GameObject obj, TileType t)
        {
            switch (t)
            {
                case TileType.Wall: ApplyColor(obj, CWall); break;
                case TileType.Emitter: ApplyColor(obj, CEmitter); ApplyEmissive(obj, CEmitter, EE); break;
                case TileType.Receiver: ApplyColor(obj, CReceiver); ApplyEmissive(obj, CReceiver, ER); break;
                case TileType.Mirror: ApplyColor(obj, CMirror); break;
                case TileType.Door: ApplyColor(obj, CDoor); ApplyEmissive(obj, CDoor, ED); break;
                case TileType.Refractor: ApplyColor(obj, CRefractor); ApplyEmissive(obj, CRefractor, ERf); break;
            }
        }

        void EnsureCollider(GameObject obj)
        {
            if (obj.GetComponentInChildren<Collider>() == null)
                obj.AddComponent<BoxCollider>();
        }

        void ApplyColor(GameObject obj, Color c)
        {
            foreach (var r in obj.GetComponentsInChildren<Renderer>())
            {
                var m = new MaterialPropertyBlock(); r.GetPropertyBlock(m);
                m.SetColor("_Color", c); r.SetPropertyBlock(m);
            }
        }

        void ApplyEmissive(GameObject obj, Color c, float i)
        {
            Color ec = c * Mathf.Pow(2f, i);
            foreach (var r in obj.GetComponentsInChildren<Renderer>())
            { var mat = r.material; mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", ec); }
        }
    }
}
