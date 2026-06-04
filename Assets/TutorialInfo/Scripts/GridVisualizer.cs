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

        private const float EE = 1.8f, ER = 1.8f, ED = 0.6f, ERf = 1.2f;

        [HideInInspector] public GridModel LevelGrid;
        [HideInInspector] public float Spacing => spacing;
        public Dictionary<Vector2Int, GameObject> SpawnedObjects = new Dictionary<Vector2Int, GameObject>();

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
                    Vector3 basePos = new Vector3((x * spacing) - ox, 0, (y * spacing) - oz);

                    // Floor
                    if (emptyTilePrefab != null)
                    {
                        var fl = Instantiate(emptyTilePrefab, basePos, Quaternion.identity, transform);
                        fl.transform.localScale = new Vector3(1f, 0.1f, 1f);
                        ApplyColor(fl, CFloor);
                        EnsureCollider(fl, isTrigger: false);
                    }

                    if (t == TileType.Empty) continue;

                    GameObject prefab = GetPrefab(t);
                    if (prefab == null) continue;

                    // ── Rotation per type ─────────────────────────────────────
                    Quaternion rot = Quaternion.identity;
                    if (t == TileType.Mirror)
                        rot = Quaternion.Euler(0f, 45f, 0f);
                    else if (t == TileType.Emitter)
                        rot = EmitterRotation(x, y);   // ← face INWARD toward grid

                    var obj = Instantiate(prefab, basePos + Vector3.up * 0.5f, rot, transform);
                    SetScale(obj, t);
                    ColorObject(obj, t);
                    EnsureCollider(obj, isTrigger: false);
                    TrySetTag(obj, GetTag(t));

                    if (t != TileType.Wall)
                        SpawnedObjects[new Vector2Int(x, y)] = obj;
                }
        }

        // ── Emitter faces the first empty neighbour (inward) ─────────────────
        Quaternion EmitterRotation(int ex, int ey)
        {
            Vector2Int[] dirs = {
                Vector2Int.right, Vector2Int.left,
                new Vector2Int(0,1), new Vector2Int(0,-1)
            };
            foreach (var d in dirs)
            {
                int nx = ex + d.x, ny = ey + d.y;
                if (nx < 0 || nx >= LevelGrid.Width || ny < 0 || ny >= LevelGrid.Height) continue;
                TileType t = LevelGrid.GetTile(nx, ny);
                if (t != TileType.Wall && t != TileType.Door)
                {
                    // Rotate so transform.forward points toward (d.x, 0, d.y)
                    return Quaternion.LookRotation(new Vector3(d.x, 0, d.y));
                }
            }
            return Quaternion.identity;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        void TrySetTag(GameObject obj, string tag)
        {
            try { obj.tag = tag; }
            catch { Debug.LogWarning($"[GridVisualizer] Tag '{tag}' missing. Add in Project Settings → Tags."); }
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

        void SetScale(GameObject obj, TileType t)
        {
            switch (t)
            {
                case TileType.Wall: obj.transform.localScale = new Vector3(1.1f, 3.0f, 1.1f); break;
                case TileType.Mirror: obj.transform.localScale = new Vector3(0.5f, 2.0f, 1.2f); break;
                case TileType.Receiver: obj.transform.localScale = new Vector3(1.3f, 1.3f, 1.3f); break;
                case TileType.Door: obj.transform.localScale = new Vector3(0.3f, 2.5f, 1.0f); break;
                default: obj.transform.localScale = Vector3.one; break;
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

        void EnsureCollider(GameObject obj, bool isTrigger)
        {
            var col = obj.GetComponentInChildren<Collider>();
            if (col == null) col = obj.AddComponent<BoxCollider>();
            col.isTrigger = isTrigger;
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