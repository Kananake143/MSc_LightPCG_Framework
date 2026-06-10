using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using LightPCG.Core;

namespace LightPCG.Systems
{
    [RequireComponent(typeof(CharacterController))]
    public class AISolverAgent : MonoBehaviour
    {
        [Header("References")]
        public GridVisualizer gridVisualizer;

        [Header("Movement")]
        public float moveSpeed = 8f;
        public float rotationSpeed = 15f;

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
        private CharacterController cc;
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
            cc = GetComponent<CharacterController>();
            cc.radius = 0.28f;
            cc.height = 1.0f;
            cc.center = new Vector3(0, 0.5f, 0);
            cc.minMoveDistance = 0f;
            cc.skinWidth = 0.08f;   // เพิ่มนี้ ป้องกัน jitter กับพื้น
            cc.slopeLimit = 0f;      // ไม่ขึ้นลาด
            cc.stepOffset = 0.1f;    // ก้าวข้ามขอบเล็กๆ ได้
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

            // Teleport — ปิด cc ก่อน แล้วตั้ง Y ให้อยู่เหนือพื้น
            cc.enabled = false;
            Vector3 spawnPos = gridVisualizer.GridToWorld(em.x, em.y);
            spawnPos.y = 0.5f;
            transform.position = spawnPos;
            yield return new WaitForEndOfFrame(); // รอ 1 frame ให้ physics settle
            cc.enabled = true;
            yield return new WaitForSeconds(0.3f);

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
                    if (!gridVisualizer.SpawnedObjects.TryGetValue(cell, out var go) || go == null)
                        continue;
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

            cc.enabled = false;
            transform.position = gridVisualizer.GridToWorld(emitter.x, emitter.y);
            cc.enabled = true;
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

                    // FIX: คำนวณ prevScore BEFORE วางวัตถุ
                    int prevScore = ScoreBeam(receiver);
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

                        // เปรียบเทียบ score หลังวาง vs ก่อนวาง
                        if (ScoreBeam(receiver) > prevScore)
                        {
                            Debug.Log($"[AI] Keep {objType}@{targetCell} rot {rot}deg " +
                                      $"score {prevScore}->{ScoreBeam(receiver)}");
                            cellKept = true;
                            go = null;
                            break;
                        }

                        // reset rotation แล้วลอง rotation ถัดไป
                        if (gridVisualizer.SpawnedObjects.TryGetValue(targetCell, out var gR)
                            && gR != null)
                            gR.transform.rotation = Quaternion.identity;
                    }

                    if (cellKept) break;

                    // ทุก rotation ไม่ดี → หยิบกลับ ลอง cell ถัดไป
                    go = PickupObject(targetCell);
                }

                // ลองทุก cell แล้วไม่ได้ → restore กลับที่เดิม
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

        // FIX: ScoreBeam เป็น method ของ class ไม่ใช่ local function
        // Score = beam length - (distance จาก beam endpoint ถึง receiver × 2)
        // ยิ่งสูง = beam ยาวและใกล้ receiver มากขึ้น
        int ScoreBeam(Vector2Int receiver)
        {
            if (RealSolved()) return int.MaxValue;
            var b = ObserveBeam();
            int distToReceiver = Manhattan(b.endCell, receiver);
            return b.pathLength - distToReceiver * 2;
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

                        if (gridVisualizer.SpawnedObjects.TryGetValue(objCell, out var go)
                            && go != null)
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

                            if (gridVisualizer.SpawnedObjects.TryGetValue(newTarget, out var gr)
                                && gr != null)
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
        // WALK TO — CharacterController + BFS
        // ════════════════════════════════════════════════════════════
        IEnumerator WalkTo(Vector2Int target)
        {
            if (target == -Vector2Int.one) yield break;
            if (!InB(target)) yield break;

            Vector3 worldTarget = gridVisualizer.GridToWorld(target.x, target.y);
            if (Vector3.Distance(transform.position, worldTarget) < 0.2f) yield break;

            var path = BFS(WorldToGrid(transform.position), target);
            if (path == null || path.Count == 0)
            {
                Debug.LogWarning($"[AI] BFS no path: {WorldToGrid(transform.position)} -> {target}");
                yield break;
            }

            foreach (var step in path)
            {
                Vector3 wt = gridVisualizer.GridToWorld(step.x, step.y);
                // ใช้ Y เดิมของ agent ไม่เปลี่ยน
                wt.y = transform.position.y;

                float timeout = 5f;
                while (timeout > 0f)
                {
                    timeout -= Time.deltaTime;

                    Vector3 toTarget = wt - transform.position;
                    toTarget.y = 0f; // เดินแนวราบเท่านั้น

                    // ถึงจุดหมายแล้ว
                    if (toTarget.magnitude < 0.15f) break;

                    // หมุนหน้าไปทาง target
                    if (toTarget.sqrMagnitude > 0.001f)
                        transform.rotation = Quaternion.Slerp(
                            transform.rotation,
                            Quaternion.LookRotation(toTarget),
                            rotationSpeed * Time.deltaTime);

                    // SimpleMove จัดการ gravity ให้เอง ไม่ต้องเพิ่ม move.y
                    cc.SimpleMove(toTarget.normalized * moveSpeed);

                    yield return null;
                }
            }
        }
        List<Vector2Int> BFS(Vector2Int start, Vector2Int goal)
        {
            if (start == goal) return new List<Vector2Int>();

            var visited = new HashSet<Vector2Int> { start };
            var parent = new Dictionary<Vector2Int, Vector2Int>();
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var d in Dirs4)
                {
                    var next = cur + d;
                    if (!InB(next)) continue;
                    if (visited.Contains(next)) continue;

                    var t = grid.GetTile(next.x, next.y);

                    // FIX: walkable = Empty หรือ Door หรือ goal เท่านั้น
                    // Mirror/Refractor/Emitter/Receiver/Wall = ไม่ผ่าน
                    bool walkable = (t == TileType.Empty || t == TileType.Door || next == goal);
                    if (!walkable) continue;

                    visited.Add(next);
                    parent[next] = cur;

                    if (next == goal)
                    {
                        var p = new List<Vector2Int>();
                        for (var c = goal; c != start; c = parent[c]) p.Add(c);
                        p.Reverse();
                        return p;
                    }
                    queue.Enqueue(next);
                }
            }

            // FIX: ถ้าหา path ไม่เจอ ลอง teleport ไปใกล้ๆ goal แทน
            Debug.LogWarning($"[AI] BFS failed {start}->{goal}");
            return null;
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
                float to = 3f;

                while (Vector3.Distance(transform.position, bw) > 0.3f && to > 0)
                {
                    to -= Time.deltaTime;
                    Vector3 d = bw - transform.position;
                    d.y = 0f;
                    d = d.normalized;
                    cc.SimpleMove(d * moveSpeed); // ใช้ SimpleMove
                    yield return null;
                }
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
                        if (t == TileType.Empty && !s.emptyCells.Contains(pos))
                            s.emptyCells.Add(pos);
                        if (t == TileType.Receiver || t == TileType.Wall || t == TileType.Emitter)
                            break;
                        if (t == TileType.Mirror) { dir = GridBounce(dir, pos); continue; }
                        if (t == TileType.Refractor) { dir = GridRefract(dir, pos); continue; }
                    }
                }
            return s;
        }

        int BeamLength() => ObserveBeam().pathLength;
        BeamState SimulateBeamWithout(Vector2Int excludeCell)
        {
            // ชั่วคราว: เอา object ออกจาก grid
            TileType savedType = grid.GetTile(excludeCell.x, excludeCell.y);
            grid.SetTile(excludeCell.x, excludeCell.y, TileType.Empty);

            // simulate beam โดยไม่มี object นั้น
            BeamState result = ObserveBeam();

            // คืนค่าเดิม
            grid.SetTile(excludeCell.x, excludeCell.y, savedType);

            return result;
        }
        // ════════════════════════════════════════════════════════════
        // SELECTION HELPERS
        // ════════════════════════════════════════════════════════════
        List<Vector2Int> PrioritisedCells(BeamState beam, Vector2Int receiver)
        {
            // ขั้น 1: ใช้ empty cells บน beam path ก่อน
            var sorted = new List<Vector2Int>(beam.emptyCells);
            sorted.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            var result = new List<Vector2Int>();
            foreach (var c in sorted) if (!IsExhausted(c)) result.Add(c);
            if (result.Count > 0) return result;

            // ขั้น 2: fallback — เฉพาะ row/col ของ beam endpoint และ receiver
            Vector2Int endCell = beam.endCell;
            Vector2Int recvCell = receiver;

            for (int x = 1; x < grid.Width - 1; x++)
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Empty) continue;
                    var v = new Vector2Int(x, y);
                    if (IsExhausted(v)) continue;

                    bool onBeamRow = (y == endCell.y);
                    bool onBeamCol = (x == endCell.x);
                    bool onRecvRow = (y == recvCell.y);
                    bool onRecvCol = (x == recvCell.x);

                    if (onBeamRow || onBeamCol || onRecvRow || onRecvCol)
                        result.Add(v);
                }
            result.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            if (result.Count > 0) return result;

            // ขั้น 3: fallback สุดท้าย — จำกัด Manhattan ≤ ครึ่งด่าน
            int threshold = Mathf.Max(grid.Width, grid.Height) / 2;
            for (int x = 1; x < grid.Width - 1; x++)
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    if (grid.GetTile(x, y) != TileType.Empty) continue;
                    var v = new Vector2Int(x, y);
                    if (!IsExhausted(v) && Manhattan(v, receiver) <= threshold)
                        result.Add(v);
                }
            result.Sort((a, b) => Manhattan(a, receiver).CompareTo(Manhattan(b, receiver)));
            return result;
        }
        Vector2Int PickObjectToRelocate(Vector2Int receiver)
        {
            var list = GetInteractables();
            if (list.Count == 0) return -Vector2Int.one;

            var beam = ObserveBeam();

            // แยก: object ที่อยู่บน beam path vs ไม่อยู่
            var onBeam = new List<Vector2Int>();
            var offBeam = new List<Vector2Int>();
            foreach (var obj in list)
            {
                // ถ้า object อยู่ใน path ของ beam ปัจจุบัน = "useful"
                bool isUseful = false;
                var tempBeam = SimulateBeamWithout(obj); // simulate ถ้าไม่มี obj นี้
                if (tempBeam.pathLength < beam.pathLength) isUseful = true;

                if (isUseful) onBeam.Add(obj);
                else offBeam.Add(obj);
            }

            // ย้าย offBeam ก่อน (ไม่ได้ใช้งาน), ถ้าไม่มีค่อยย้าย onBeam
            var candidates = offBeam.Count > 0 ? offBeam : onBeam;
            candidates.Sort((a, b) => Manhattan(b, receiver).CompareTo(Manhattan(a, receiver)));
            return candidates[0];
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
            if (!memory.ContainsKey(from))
                memory[from] = new HashSet<(Vector2Int, int)>();
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
                    if (t == TileType.Mirror || t == TileType.Refractor)
                        l.Add(new Vector2Int(x, y));
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
            // FIX: clamp ให้อยู่ใน bounds เสมอ
            int gx = Mathf.RoundToInt((w.x + ox) / spacing);
            int gz = Mathf.RoundToInt((w.z + oz) / spacing);
            gx = Mathf.Clamp(gx, 0, grid.Width - 1);
            gz = Mathf.Clamp(gz, 0, grid.Height - 1);
            return new Vector2Int(gx, gz);
        }

        bool InB(Vector2Int p)
            => p.x >= 0 && p.x < grid.Width && p.y >= 0 && p.y < grid.Height;
    }
}