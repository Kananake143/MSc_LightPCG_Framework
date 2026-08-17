using LightPCG.Core;
using UnityEngine;
using System.Collections.Generic;

namespace LightPCG.Systems
{
    public class GridVisualizer : MonoBehaviour
    {
        [Header("Grid Size")]
        public int desiredWidth = 14;
        public int desiredHeight = 14;
        public float spacing = 1.1f;

        [Header("Difficulty")]
        [Range(2, 8)] public int minSteps = 3;
        [Range(3, 10)] public int maxSteps = 6;
        [Range(1, 3)] public int emitterCount = 1;

        [Header("Decoy Objects")]
        [Range(0, 4)] public int decoyCount = 1;

        [Header("Prefabs")]
        public GameObject emptyTilePrefab;
        public GameObject wallPrefab;
        public GameObject emitterPrefab;
        public GameObject receiverPrefab;
        public GameObject mirrorPrefab;
        public GameObject doorPrefab;
        public GameObject refractorPrefab;

        // ── Colours ───────────────────────────────────────────────────
        private static readonly Color CFloor = new Color(0.78f, 0.74f, 0.68f);
        private static readonly Color CWall = new Color(0.22f, 0.28f, 0.35f);
        private static readonly Color CWallInner = new Color(0.35f, 0.42f, 0.50f);
        private static readonly Color CEmitter = new Color(1.00f, 0.85f, 0.10f);
        private static readonly Color CReceiver = new Color(0.90f, 0.15f, 0.10f);
        private static readonly Color CMirror = new Color(0.88f, 0.96f, 1.00f);
        private static readonly Color CDoor = new Color(0.10f, 0.85f, 0.25f);
        private static readonly Color CRefractor = new Color(0.45f, 0.05f, 0.80f);

        private const float EE = 1.8f, ER = 1.8f, ED = 1.0f, ERf = 2.2f, EMirror = 0.5f;

        // ── Public runtime references ─────────────────────────────────
        [HideInInspector] public GridModel LevelGrid;
        [HideInInspector] public float Spacing => spacing;
        public Dictionary<Vector2Int, GameObject> SpawnedObjects
            = new Dictionary<Vector2Int, GameObject>();

        // ── Stats from last GenerateLevel() call ──────────────────────
        [HideInInspector] public int LastSolutionObjectCount;
        [HideInInspector] public int LastDecoyCount;
        [HideInInspector] public int LastTotalObjectCount;
        [HideInInspector] public int LastSteps;

        // ── NEW: MID component data (from BackwardChainingGenerator) ──
        /// <summary>Mirror count on solution path (for Cᵢ numerator complement).</summary>
        [HideInInspector] public int LastMirrorCount;

        /// <summary>Refractor count on solution path (drives Cᵢ in MID).</summary>
        [HideInInspector] public int LastRefractorCount;

        /// <summary>
        /// Number of bend-type transitions on solution path (drives Cₕ in MID).
        /// A transition is counted each time a Mirror bend is followed by a Refractor
        /// bend or vice-versa.
        /// </summary>
        [HideInInspector] public int LastRuleTransitions;

        // ════════════════════════════════════════════════════════════
        void Start() => GenerateLevel();

        // ════════════════════════════════════════════════════════════
        public void GenerateLevel()
        {
            foreach (Transform c in transform) Destroy(c.gameObject);
            SpawnedObjects.Clear();

            LevelGrid = new GridModel(desiredWidth, desiredHeight);
            LastSteps = Random.Range(minSteps, maxSteps + 1);

            var pcg = new BackwardChainingGenerator(LevelGrid);
            pcg.GenerateValidPuzzle(LastSteps, emitterCount, decoyCount);

            // ── Capture all stats from generator ──
            LastSolutionObjectCount = pcg.SolutionObjectCount;
            LastDecoyCount = pcg.DecoyCount;
            LastTotalObjectCount = pcg.TotalObjectCount;
            LastMirrorCount = pcg.MirrorCount;
            LastRefractorCount = pcg.RefractorCount;
            LastRuleTransitions = pcg.RuleTransitions;

            BuildVisuals();

            // Reset all ReceiverDetectors so state from previous level does not carry over.
            foreach (var rd in GetComponentsInChildren<ReceiverDetector>())
                rd.ResetState();

            Debug.Log($"[GridVisualizer] Level ready steps={LastSteps} " +
                      $"solutionObjs={LastSolutionObjectCount} " +
                      $"mirrors={LastMirrorCount} refractors={LastRefractorCount} " +
                      $"ruleTransitions={LastRuleTransitions} decoys={LastDecoyCount}");
        }

        // ════════════════════════════════════════════════════════════
        public Vector3 GridToWorld(int x, int y)
        {
            float ox = (LevelGrid.Width - 1) * spacing / 2f;
            float oz = (LevelGrid.Height - 1) * spacing / 2f;
            return new Vector3((x * spacing) - ox, 0.5f, (y * spacing) - oz);
        }

        // ════════════════════════════════════════════════════════════
        void BuildVisuals()
        {
            float ox = (LevelGrid.Width - 1) * spacing / 2f;
            float oz = (LevelGrid.Height - 1) * spacing / 2f;

            for (int x = 0; x < LevelGrid.Width; x++)
                for (int y = 0; y < LevelGrid.Height; y++)
                {
                    TileType t = LevelGrid.GetTile(x, y);
                    Vector3 base3 = new Vector3((x * spacing) - ox, 0f, (y * spacing) - oz);

                    // Floor tile always present
                    if (emptyTilePrefab != null)
                    {
                        var fl = Instantiate(emptyTilePrefab, base3, Quaternion.identity, transform);
                        fl.transform.localScale = new Vector3(1f, 0.1f, 1f);
                        ApplyColor(fl, CFloor);
                        EnsureCollider(fl);
                    }

                    if (t == TileType.Empty) continue;

                    GameObject prefab = GetPrefab(t);
                    if (prefab == null) continue;

                    Quaternion rot = Quaternion.identity;
                    if (t == TileType.Mirror) rot = Quaternion.Euler(0f, 45f, 0f);
                    else if (t == TileType.Emitter) rot = EmitterFacingRot(x, y);
                    else if (t == TileType.Receiver) rot = ReceiverFacingRot(x, y);
                    else if (t == TileType.Door) rot = DoorFacingRot(x, y);

                    var obj = Instantiate(prefab, base3 + Vector3.up * 0.5f, rot, transform);
                    obj.transform.localScale = GetScale(t);
                    ColorObject(obj, t, IsOuterWall(x, y));
                    EnsureCollider(obj);
                    TrySetTag(obj, GetTag(t));

                    if (t != TileType.Wall)
                        SpawnedObjects[new Vector2Int(x, y)] = obj;
                }
        }

        // ── Layout helpers ────────────────────────────────────────────
        bool IsOuterWall(int x, int y)
            => x == 0 || x == LevelGrid.Width - 1 || y == 0 || y == LevelGrid.Height - 1;

        Quaternion EmitterFacingRot(int ex, int ey)
        {
            int dL = ex, dR = LevelGrid.Width - 1 - ex,
                dB = ey, dT = LevelGrid.Height - 1 - ey,
                m = Mathf.Min(dL, dR, dB, dT);
            if (m == dL) return Quaternion.LookRotation(Vector3.right);
            if (m == dR) return Quaternion.LookRotation(Vector3.left);
            if (m == dB) return Quaternion.LookRotation(Vector3.forward);
            return Quaternion.LookRotation(Vector3.back);
        }

        Quaternion ReceiverFacingRot(int rx, int ry)
        {
            int dL = rx, dR = LevelGrid.Width - 1 - rx,
                dB = ry, dT = LevelGrid.Height - 1 - ry,
                m = Mathf.Min(dL, dR, dB, dT);
            if (m == dL) return Quaternion.LookRotation(Vector3.right);
            if (m == dR) return Quaternion.LookRotation(Vector3.left);
            if (m == dB) return Quaternion.LookRotation(Vector3.forward);
            return Quaternion.LookRotation(Vector3.back);
        }

        Quaternion DoorFacingRot(int dx, int dy)
        {
            bool tb = (dy == 0 || dy == LevelGrid.Height - 1);
            return tb ? Quaternion.Euler(0f, 0f, 0f) : Quaternion.Euler(0f, 90f, 0f);
        }

        void TrySetTag(GameObject obj, string tag)
        {
            try { obj.tag = tag; }
            catch { Debug.LogWarning($"[GridVisualizer] Tag '{tag}' missing."); }
        }

        // ── Prefab / tag / scale / colour mapping ─────────────────────
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
                case TileType.Emitter: return new Vector3(0.9f, 0.9f, 0.9f);
                case TileType.Mirror: return new Vector3(0.1f, 2.2f, 1.0f);
                case TileType.Refractor: return new Vector3(0.3f, 1.8f, 0.9f);
                case TileType.Receiver: return new Vector3(0.9f, 0.9f, 0.9f);
                case TileType.Door: return new Vector3(1.0f, 3.0f, 0.08f);
                default: return Vector3.one;
            }
        }

        void ColorObject(GameObject obj, TileType t, bool outer = false)
        {
            switch (t)
            {
                case TileType.Wall:
                    ApplyColor(obj, outer ? CWall : CWallInner);
                    break;
                case TileType.Emitter:
                    ApplyColor(obj, CEmitter);
                    ApplyEmissive(obj, CEmitter, EE);
                    break;
                case TileType.Receiver:
                    ApplyColor(obj, CReceiver);
                    ApplyEmissive(obj, CReceiver, ER);
                    break;
                case TileType.Mirror:
                    ApplyColor(obj, CMirror);
                    ApplyEmissive(obj, CMirror, EMirror);
                    SetTransparent(obj, 0.45f);
                    break;
                case TileType.Door:
                    ApplyColor(obj, CDoor);
                    ApplyEmissive(obj, CDoor, ED);
                    break;
                case TileType.Refractor:
                    ApplyColor(obj, CRefractor);
                    ApplyEmissive(obj, CRefractor, ERf);
                    break;
            }
        }

        // ── Material / renderer helpers ───────────────────────────────
        void EnsureCollider(GameObject obj)
        {
            if (obj.GetComponentInChildren<Collider>() == null)
                obj.AddComponent<BoxCollider>();
        }

        void ApplyColor(GameObject obj, Color c)
        {
            foreach (var r in obj.GetComponentsInChildren<Renderer>())
            {
                var mat = r.sharedMaterial;
                var m = new MaterialPropertyBlock();
                r.GetPropertyBlock(m);

                if (mat != null && mat.HasProperty("_BaseColor"))
                    m.SetColor("_BaseColor", c);   // URP / HDRP
                if (mat != null && mat.HasProperty("_Color"))
                    m.SetColor("_Color", c);       // Built-in RP

                r.SetPropertyBlock(m);
            }
        }

        void ApplyEmissive(GameObject obj, Color c, float intensity)
        {
            Color ec = c * Mathf.Pow(2f, intensity);
            foreach (var r in obj.GetComponentsInChildren<Renderer>())
            {
                var mat = r.material;
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", ec);
            }
        }

        void SetTransparent(GameObject obj, float alpha)
        {
            foreach (var r in obj.GetComponentsInChildren<Renderer>())
            {
                var mat = r.material;
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetFloat("_AlphaClip", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = 3000;
                
                var mpb = new MaterialPropertyBlock();
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_BaseColor", new Color(0.88f, 0.96f, 1f, alpha));
                r.SetPropertyBlock(mpb);
            }
        }
    }
}