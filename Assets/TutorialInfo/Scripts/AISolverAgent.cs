using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.AI;
using LightPCG.Core;

namespace LightPCG.Systems
{
    [RequireComponent(typeof(NavMeshAgent))]
    public class AISolverAgent : MonoBehaviour
    {
        [Header("References")]
        public GridVisualizer gridVisualizer;

        [Header("Movement")]
        public float moveSpeed = 5f;
        public float arrivalDist = 0.15f;

        [Header("Timing")]
        public float physicsWait = 0.15f;
        public float stepDelay = 0.05f;

        [Header("Limits")]
        public int maxBacktrackRounds = 5;
        public int maxIterations = 60;

        [HideInInspector] public bool WasSolved;
        [HideInInspector] public int SolveIterations;
        [HideInInspector] public float SolveTimeMs;
        [HideInInspector] public int TotalPlacements;
        public System.Action<bool> OnSolveComplete;

        private GridModel grid;
        private float spacing;
        private NavMeshAgent agent;
        private LaserSystem[] allLasers;
        private float solveStart;
        private bool running;

        private Dictionary<Vector2Int, HashSet<(Vector2Int, int)>> memory
            = new Dictionary<Vector2Int, HashSet<(Vector2Int, int)>>();

        private static readonly Vector2Int[] Dirs4 = {
            Vector2Int.right, Vector2Int.left,
            new Vector2Int(0,1), new Vector2Int(0,-1)
        };

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.speed = moveSpeed;
            agent.angularSpeed = 360f;
            agent.acceleration = 20f;
            agent.stoppingDistance = arrivalDist;
            agent.autoBraking = true;
        }

        void Start()
        {
            if (gridVisualizer == null)
                gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (gridVisualizer == null)
            { Debug.LogError("[AI] GridVisualizer not found!"); return; }
            StartSolve();
        }

        public void StartSolve()
        {
            if (running) return;
            running = true;
            WasSolved = false; SolveIterations = 0; TotalPlacements = 0;
            memory.Clear();
            StartCoroutine(Pipeline());
        }

        IEnumerator Pipeline()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(0.5f);

            grid = gridVisualizer.LevelGrid;
            spacing = gridVisualizer.Spacing;
            allLasers = FindLaserSystems();
            solveStart = Time.realtimeSinceStartup;

            Vector2Int em = FindFirst(TileType.Emitter);
            if (em == -Vector2Int.one)
            { Debug.LogError("[AI] No Emitter!"); Finish(false); yield break; }

            agent.enabled = false;
            transform.position = gridVisualizer.GridToWorld(em.x, em.y);
            yield return new WaitForEndOfFrame();
            agent.enabled = true;
            yield return new WaitForSeconds(physicsWait);

            if (RealSolved())
            { Finish(true); yield return StartCoroutine(ExitDoor()); yield break; }

            yield return StartCoroutine(Phase1_Scan());
        }

        LaserSystem[] FindLaserSystems()
        {
            var list = new List<LaserSystem>();
            for (int x = 0; x < grid.Width; x++)
                for (int y = 0; y < grid.Height; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Emitter) continue;
                    var cell = new Vector2Int(x, y);
                    if (!gridVisualizer.SpawnedObjects.TryGetValue(cell, out var go) || go == null) continue;
                    var ls = go.GetComponent<LaserSystem>();
                    if (ls != null) list.Add(ls);
                }
            if (list.Count == 0)
                return FindObjectsByType<LaserSystem>(FindObjectsSortMode.None);
            return list.ToArray();
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 1 - SCAN
        // ════════════════════════════════════════════════════════════
        IEnumerator Phase1_Scan()
        {
            Vector2Int emitter = FindFirst(TileType.Emitter);
            Vector2Int receiver = FindFirst(TileType.Receiver);
            Debug.Log($"[AI] Phase 1 SCAN | Emitter:{emitter} Receiver:{receiver}");

            if (emitter == -Vector2Int.one || receiver == -Vector2Int.one)
            { Debug.LogError("[AI] Missing Emitter or Receiver!"); Finish(false); yield break; }

            agent.enabled = false;
            transform.position = gridVisualizer.GridToWorld(emitter.x, emitter.y);
            yield return new WaitForEndOfFrame();
            agent.enabled = true;
            yield return new WaitForSeconds(physicsWait);

            if (RealSolved())
            { Finish(true); yield return StartCoroutine(ExitDoor()); yield break; }

            yield return StartCoroutine(Phase2_PlaceChain());
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 2+3 - PLACE AND CHAIN
        // ════════════════════════════════════════════════════════════
        IEnumerator Phase2_PlaceChain()
        {
            Debug.Log("[AI] Phase 2+3 PLACE & CHAIN");
            var receiver = FindFirst(TileType.Receiver);
            var objects = GetInteractables();

            for (int objIdx = 0; objIdx < objects.Count; objIdx++)
            {
                if (RealSolved()) break;
                if (SolveIterations >= maxIterations) break;
                SolveIterations++;

                Vector2Int objCell = objects[objIdx];
                TileType objType = grid.GetTile(objCell.x, objCell.y);
                GameObject go = null;

                BeamState beam = ObserveBeam();
                var cells = PrioritisedCells(beam, receiver);

                foreach (var targetCell in cells)
                {
                    if (RealSolved()) break;

                    if (go == null)
                    {
                        yield return StartCoroutine(WalkTo(objCell));
                        yield return new WaitForSeconds(stepDelay);
                        go = PickupObject(objCell);
                    }

                    yield return StartCoroutine(WalkTo(targetCell));

                    int prevLen = BeamLength();
                    bool cellKept = false;

                    foreach (int rot in PrioritisedRotations(objType, targetCell, receiver))
                    {
                        if (WasTried(objCell, targetCell, rot)) continue;

                        PlaceObject(targetCell, objType, go, rot);
                        TotalPlacements++;
                        yield return new WaitForSeconds(physicsWait);
                        RememberTried(objCell, targetCell, rot);

                        if (RealSolved())
                        {
                            Debug.Log($"[AI] SOLVED Phase {(objIdx == 0 ? 2 : 3)} " +
                                      $"{objType}@{targetCell} rot {rot}deg");
                            Finish(true);
                            yield return StartCoroutine(ExitDoor());
                            yield break;
                        }

                        if (BeamLength() > prevLen)
                        {
                            Debug.Log($"[AI] Keep {objType}@{targetCell} rot {rot}deg " +
                                      $"beam {prevLen}->{BeamLength()}");
                            cellKept = true;
                            go = null;
                            break;
                        }

                        if (gridVisualizer.SpawnedObjects.TryGetValue(targetCell, out var gR) && gR != null)
                            gR.transform.rotation = Quaternion.identity;
                    }

                    if (cellKept) break;
                    go = PickupObject(targetCell);
                }

                if (go != null)
                {
                    yield return StartCoroutine(WalkTo(objCell));
                    PlaceObject(objCell, objType, go, 0);
                    yield return new WaitForSeconds(physicsWait);
                    Debug.Log($"[AI] Restored {objType} -> {objCell}");
                    go = null;
                }
            }

            if (!RealSolved())
                yield return StartCoroutine(Phase4_Backtrack());
        }

        // ════════════════════════════════════════════════════════════
        // PHASE 4 - BACKTRACK
        // ════════════════════════════════════════════════════════════
        IEnumerator Phase4_Backtrack()
        {
            Debug.Log("[AI] Phase 4 BACKTRACK");
            var receiver = FindFirst(TileType.Receiver);

            for (int round = 0;
                round < maxBacktrackRounds && !RealSolved() && SolveIterations < maxIterations;
                round++)
            {
                SolveIterations++;
                Debug.Log($"[AI] Backtrack round {round + 1}");

                // 4A: re-rotate
                foreach (var objCell in GetInteractables())
                {
                    if (RealSolved()) break;
                    var objType = grid.GetTile(objCell.x, objCell.y);
                    yield return StartCoroutine(WalkTo(objCell));
                    int prevLen = BeamLength();

                    foreach (int rot in PrioritisedRotations(objType, objCell, receiver))
                    {
                        if (WasTried(objCell, objCell, rot)) continue;

                        if (gridVisualizer.SpawnedObjects.TryGetValue(objCell, out var go) && go != null)
                            go.transform.rotation = Quaternion.Euler(0f, rot, 0f);

                        yield return new WaitForSeconds(physicsWait);
                        RememberTried(objCell, objCell, rot);

                        if (RealSolved())
                        {
                            Debug.Log("[AI] SOLVED Phase 4A!");
                            Finish(true);
                            yield return StartCoroutine(ExitDoor());
                            yield break;
                        }

                        if (BeamLength() > prevLen) { prevLen = BeamLength(); break; }
                    }
                }

                // 4B: relocate worst
                if (!RealSolved())
                {
                    var beam = ObserveBeam();
                    var objCell = PickObjectToRelocate(receiver);
                    if (objCell == -Vector2Int.one) break;

                    var objType = grid.GetTile(objCell.x, objCell.y);
                    var cells = PrioritisedCells(beam, receiver);

                    yield return StartCoroutine(WalkTo(objCell));
                    var go_ = PickupObject(objCell);
                    bool relocated = false;

                    foreach (var newTarget in cells)
                    {
                        if (newTarget == objCell) continue;
                        if (grid.GetTile(newTarget.x, newTarget.y) != TileType.Empty) continue;

                        yield return StartCoroutine(WalkTo(newTarget));

                        foreach (int rot in PrioritisedRotations(objType, newTarget, receiver))
                        {
                            PlaceObject(newTarget, objType, go_, rot);
                            TotalPlacements++;
                            yield return new WaitForSeconds(physicsWait);
                            RememberTried(objCell, newTarget, rot);

                            if (RealSolved())
                            {
                                Debug.Log("[AI] SOLVED Phase 4B!");
                                Finish(true);
                                yield return StartCoroutine(ExitDoor());
                                yield break;
                            }

                            if (BeamLength() > beam.pathLength) { relocated = true; break; }

                            if (gridVisualizer.SpawnedObjects.TryGetValue(newTarget, out var gr) && gr != null)
                                gr.transform.rotation = Quaternion.identity;
                        }

                        if (relocated) break;
                        go_ = PickupObject(newTarget);
                    }

                    if (!relocated)
                    {
                        yield return StartCoroutine(WalkTo(objCell));
                        PlaceObject(objCell, objType, go_, 0);
                        yield return new WaitForSeconds(physicsWait);
                    }
                }
            }

            if (!RealSolved())
            {
                Debug.LogWarning($"[AI] Failed | iters={SolveIterations} placements={TotalPlacements}");
                Finish(false);
            }
        }

        // ════════════════════════════════════════════════════════════
        // WALK TO — NavMeshAgent
        // ════════════════════════════════════════════════════════════
        IEnumerator WalkTo(Vector2Int target)
        {
            if (target == -Vector2Int.one) yield break;

            Vector3 worldDest = gridVisualizer.GridToWorld(target.x, target.y);

            // ตรวจ NavMeshAgent active
            if (!agent.isActiveAndEnabled)
            {
                Debug.LogWarning("[AI] NavMeshAgent not active!");
                yield break;
            }

            // ตรวจว่า NavMesh มีจุดนั้นไหม
            NavMeshHit nmHit;
            if (!NavMesh.SamplePosition(worldDest, out nmHit, 2f, NavMesh.AllAreas))
            {
                Debug.LogWarning($"[AI] No NavMesh near {target} worldPos={worldDest}");
                yield break;
            }

            agent.isStopped = false;
            agent.SetDestination(nmHit.position);

            // รอ 1 frame ให้ path เริ่มคำนวณ
            yield return null;

            float timeout = 10f;
            while (timeout > 0f)
            {
                timeout -= Time.deltaTime;

                if (agent.pathPending)
                {
                    yield return null;
                    continue;
                }

                if (agent.remainingDistance <= agent.stoppingDistance)
                    break;

                if (agent.pathStatus == NavMeshPathStatus.PathInvalid)
                {
                    Debug.LogWarning($"[AI] Path invalid to {target}");
                    break;
                }

                yield return null;
            }

            if (timeout <= 0f)
                Debug.LogWarning($"[AI] WalkTo timeout! target={target} remaining={agent.remainingDistance:F2}");

            agent.isStopped = true;
        }

        // ════════════════════════════════════════════════════════════
        // EXIT DOOR
        // ════════════════════════════════════════════════════════════
        IEnumerator ExitDoor()
        {
            yield return new WaitForSeconds(0.4f);

            Vector2Int dc = FindFirst(TileType.Door);
            if (dc != -Vector2Int.one)
            {
                grid.SetTile(dc.x, dc.y, TileType.Empty);
                if (gridVisualizer.SpawnedObjects.TryGetValue(dc, out var dgo) && dgo != null)
                    Destroy(dgo);
                gridVisualizer.SpawnedObjects.Remove(dc);

                yield return StartCoroutine(WalkTo(dc));

                Vector2Int beyond = dc + OutDir(dc);
                Vector3 bw = gridVisualizer.GridToWorld(beyond.x, beyond.y);
                agent.isStopped = false;
                agent.SetDestination(bw);
                yield return new WaitForSeconds(2f);
                agent.isStopped = true;
            }

            Debug.Log("[AI] Exited door!");
        }

        Vector2Int OutDir(Vector2Int c)
        {
            int cx = grid.Width / 2, cy = grid.Height / 2;
            return Mathf.Abs(cx - c.x) > Mathf.Abs(cy - c.y)
                ? new Vector2Int((int)Mathf.Sign(c.x - cx), 0)
                : new Vector2Int(0, (int)Mathf.Sign(c.y - cy));
        }

        // ════════════════════════════════════════════════════════════
        // BEAM OBSERVATION
        // ════════════════════════════════════════════════════════════
        struct BeamState
        {
            public List<Vector2Int> emptyCells;
            public int pathLength;
            public Vector2Int endCell;
        }

        BeamState ObserveBeam()
        {
            var s = new BeamState
            { emptyCells = new List<Vector2Int>(), pathLength = 0, endCell = Vector2Int.zero };

            for (int ex = 0; ex < grid.Width; ex++)
                for (int ey = 0; ey < grid.Height; ey++)
                {
                    if (grid.GetTile(ex, ey) != TileType.Emitter) continue;
                    var pos = new Vector2Int(ex, ey);
                    var dir = EmDir(pos);
                    var seen = new HashSet<(Vector2Int, Vector2Int)>();

                    for (int i = 0; i < grid.Width * grid.Height * 2; i++)
                    {
                        pos += dir;
                        if (!InB(pos)) break;
                        var st = (pos, dir);
                        if (seen.Contains(st)) break;
                        seen.Add(st);
                        s.pathLength++;
                        s.endCell = pos;

                        var t = grid.GetTile(pos.x, pos.y);
                        if (t == TileType.Empty && !s.emptyCells.Contains(pos)) s.emptyCells.Add(pos);
                        if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter) break;
                        if (t == TileType.Mirror) { dir = GridBounce(dir, pos); continue; }
                        if (t == TileType.Refractor) { dir = GridRefract(dir, pos); continue; }
                    }
                }
            return s;
        }

        int BeamLength() => ObserveBeam().pathLength;

        // ════════════════════════════════════════════════════════════
        // SELECTION HELPERS
        // ════════════════════════════════════════════════════════════
        List<Vector2Int> PrioritisedCells(BeamState beam, Vector2Int receiver)
        {
            var sorted = new List<Vector2Int>(beam.emptyCells);
            sorted.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            var result = new List<Vector2Int>();
            foreach (var c in sorted) if (!IsExhausted(c)) result.Add(c);
            if (result.Count == 0)
            {
                for (int x = 1; x < grid.Width - 1; x++) for (int y = 1; y < grid.Height - 1; y++)
                    {
                        var v = new Vector2Int(x, y);
                        if (grid.GetTile(x, y) == TileType.Empty && !result.Contains(v)) result.Add(v);
                    }
                result.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            }
            return result;
        }

        Vector2Int PickObjectToRelocate(Vector2Int receiver)
        {
            var list = GetInteractables();
            if (list.Count == 0) return -Vector2Int.one;
            list.Sort((a, b) => Manhattan(b, receiver).CompareTo(Manhattan(a, receiver)));
            return list[0];
        }

        List<int> PrioritisedRotations(TileType t, Vector2Int cell, Vector2Int receiver)
        {
            var d = receiver - cell;
            bool rx = Mathf.Abs(d.x) > Mathf.Abs(d.y);
            if (t == TileType.Mirror)
                return rx
                    ? new List<int> { 45, 225, 135, 315, 0, 90, 180, 270 }
                    : new List<int> { 135, 315, 45, 225, 0, 90, 180, 270 };
            return rx
                ? new List<int> { 0, 180, 90, 270, 45, 135, 225, 315 }
                : new List<int> { 90, 270, 0, 180, 45, 135, 225, 315 };
        }

        // ════════════════════════════════════════════════════════════
        // ATOMIC PICKUP / PLACE
        // ════════════════════════════════════════════════════════════
        GameObject PickupObject(Vector2Int cell)
        {
            gridVisualizer.SpawnedObjects.TryGetValue(cell, out GameObject go);
            grid.SetTile(cell.x, cell.y, TileType.Empty);
            gridVisualizer.SpawnedObjects.Remove(cell);
            if (go != null) go.SetActive(false);
            return go;
        }

        void PlaceObject(Vector2Int cell, TileType type, GameObject go, int rotDeg)
        {
            Vector3 wp = gridVisualizer.GridToWorld(cell.x, cell.y);
            grid.SetTile(cell.x, cell.y, type);
            gridVisualizer.SpawnedObjects[cell] = go;
            if (go != null)
            {
                go.transform.position = wp;
                go.transform.rotation = Quaternion.Euler(0f, rotDeg, 0f);
                go.SetActive(true);
            }
        }

        // ════════════════════════════════════════════════════════════
        // MEMORY
        // ════════════════════════════════════════════════════════════
        void RememberTried(Vector2Int from, Vector2Int to, int rot)
        {
            if (!memory.ContainsKey(from)) memory[from] = new HashSet<(Vector2Int, int)>();
            memory[from].Add((to, rot));
        }

        bool WasTried(Vector2Int from, Vector2Int to, int rot)
            => memory.ContainsKey(from) && memory[from].Contains((to, rot));

        bool IsExhausted(Vector2Int cell)
        {
            foreach (var obj in GetInteractables())
                foreach (int r in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
                    if (!WasTried(obj, cell, r)) return false;
            return true;
        }

        // ════════════════════════════════════════════════════════════
        // FINISH
        // ════════════════════════════════════════════════════════════
        void Finish(bool solved)
        {
            WasSolved = solved;
            SolveTimeMs = (Time.realtimeSinceStartup - solveStart) * 1000f;
            running = false;
            OnSolveComplete?.Invoke(solved);
        }

        // ════════════════════════════════════════════════════════════
        // REAL LASER CHECK
        // ════════════════════════════════════════════════════════════
        bool RealSolved()
        {
            if (allLasers == null || allLasers.Length == 0)
                allLasers = FindLaserSystems();
            if (allLasers.Length == 0) return false;
            int hitting = 0;
            foreach (var l in allLasers)
                if (l != null && l.IsHittingReceiver) hitting++;
            return hitting == allLasers.Length;
        }

        // ════════════════════════════════════════════════════════════
        // GRID MATH
        // ════════════════════════════════════════════════════════════
        Vector2Int GridBounce(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 45f)) < 22.5f ||
                    Mathf.Abs(Mathf.DeltaAngle(a, 225f)) < 22.5f)
                ? new Vector2Int(d.y, d.x)
                : new Vector2Int(-d.y, -d.x);
        }

        Vector2Int GridRefract(Vector2Int d, Vector2Int cell)
        {
            float a = GetYRot(cell);
            return (Mathf.Abs(Mathf.DeltaAngle(a, 0f)) < 22.5f ||
                    Mathf.Abs(Mathf.DeltaAngle(a, 180f)) < 22.5f)
                ? new Vector2Int(-d.y, d.x)
                : new Vector2Int(d.y, -d.x);
        }

        float GetYRot(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out var obj) && obj != null)
                return obj.transform.eulerAngles.y;
            return 0f;
        }

        Vector2Int EmDir(Vector2Int cell)
        {
            if (gridVisualizer.SpawnedObjects.TryGetValue(cell, out var go) && go != null)
            {
                var f = go.transform.forward;
                if (Mathf.Abs(f.x) >= Mathf.Abs(f.z))
                    return f.x >= 0 ? Vector2Int.right : Vector2Int.left;
                return f.z >= 0 ? new Vector2Int(0, 1) : new Vector2Int(0, -1);
            }
            foreach (var d in Dirs4)
            {
                var n = cell + d; if (!InB(n)) continue;
                var t = grid.GetTile(n.x, n.y);
                if (t != TileType.Wall && t != TileType.Door && t != TileType.Emitter) return d;
            }
            return Vector2Int.right;
        }

        // ════════════════════════════════════════════════════════════
        // UTILITIES
        // ════════════════════════════════════════════════════════════
        List<Vector2Int> GetInteractables()
        {
            var l = new List<Vector2Int>();
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                {
                    var t = grid.GetTile(x, y);
                    if (t == TileType.Mirror || t == TileType.Refractor) l.Add(new Vector2Int(x, y));
                }
            return l;
        }

        Vector2Int FindFirst(TileType target)
        {
            for (int x = 0; x < grid.Width; x++) for (int y = 0; y < grid.Height; y++)
                    if (grid.GetTile(x, y) == target) return new Vector2Int(x, y);
            return -Vector2Int.one;
        }

        int Manhattan(Vector2Int a, Vector2Int b)
            => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

        Vector2Int WorldToGrid(Vector3 w)
        {
            float ox = (grid.Width - 1) * spacing / 2f;
            float oz = (grid.Height - 1) * spacing / 2f;
            return new Vector2Int(
                Mathf.RoundToInt((w.x + ox) / spacing),
                Mathf.RoundToInt((w.z + oz) / spacing));
        }

        bool InB(Vector2Int p)
            => p.x >= 0 && p.x < grid.Width && p.y >= 0 && p.y < grid.Height;
    }
}