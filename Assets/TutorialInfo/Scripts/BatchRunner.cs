using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LightPCG.Systems;

namespace LightPCG.Research
{
    public class BatchRunner : MonoBehaviour
    {
        [Header("Experiment")]
        public int totalRuns = 1000;
        public bool runOnStart = true;

        [Header("Progressive Difficulty")]
        public int startSteps = 2;
        public int maxSteps = 9;
        public int startDecoys = 0;
        public int decoyEveryN = 3;

        [Header("References")]
        public GridVisualizer gridVisualizer;
        public AISolverAgent solverAgent;

        [Header("CSV Export")]
        public string csvFileName = "PCG_Results.csv";

        // FIX: Add a delay so the agent finishes exiting the door before a new level is generated.
        [Header("Timing")]
        [Tooltip("Wait for the agent to walk out the door before generating a new level (seconds).")]
        public float exitDoorWait = 3.5f;

        private List<RunRecord> records = new List<RunRecord>();
        private int currentRun = 0;
        private int currentSteps;
        private int currentDecoys;
        private int solvedInSession = 0;
        private bool waiting = false;

        struct RunRecord
        {
            public int run, level, steps, solObjs, decoys, totalObjs, iters, placements;
            public bool solved;
            public float solMs, genMs;
        }

        void Start()
        {
            if (gridVisualizer == null)
                gridVisualizer = FindFirstObjectByType<GridVisualizer>();
            if (solverAgent == null)
                solverAgent = FindFirstObjectByType<AISolverAgent>();
            currentSteps = startSteps;
            currentDecoys = startDecoys;
            if (runOnStart) StartCoroutine(RunBatch());
        }

        public void StartBatch() => StartCoroutine(RunBatch());

        IEnumerator RunBatch()
        {
            Debug.Log("[Batch] Starting " + totalRuns + " runs | initialSteps=" + startSteps);
            records.Clear();
            currentRun = 0;

            for (int i = 0; i < totalRuns; i++)
            {
                currentRun = i + 1;

                // Apply difficulty
                gridVisualizer.minSteps = currentSteps;
                gridVisualizer.maxSteps = currentSteps;
                gridVisualizer.decoyCount = currentDecoys;

                Debug.Log("[Batch] Run " + currentRun + "/" + totalRuns +
                          " steps=" + currentSteps + " decoys=" + currentDecoys);

                // Generate level
                float gStart = Time.realtimeSinceStartup;
                gridVisualizer.GenerateLevel();
                float gMs = (Time.realtimeSinceStartup - gStart) * 1000f;

                yield return new WaitForEndOfFrame();
                yield return new WaitForSeconds(0.3f);

                // Solve
                waiting = true;
                solverAgent.OnSolveComplete = _ => { waiting = false; };
                solverAgent.StartSolve();
                while (waiting) yield return null;

                // FIX: Wait for the agent to finish exiting the door.
                // OnSolveComplete fires during Finish(), which is before ExitDoor is completed.
                yield return new WaitForSeconds(exitDoorWait);

                bool ok = solverAgent.WasSolved;

                records.Add(new RunRecord
                {
                    run = currentRun,
                    level = solvedInSession + 1,
                    solved = ok,
                    steps = currentSteps,
                    solObjs = gridVisualizer.LastSolutionObjectCount,
                    decoys = gridVisualizer.LastDecoyCount,
                    totalObjs = gridVisualizer.LastTotalObjectCount,
                    iters = solverAgent.SolveIterations,
                    placements = solverAgent.TotalPlacements,
                    solMs = solverAgent.SolveTimeMs,
                    genMs = gMs
                });

                if (ok)
                {
                    solvedInSession++;
                    if (currentSteps < maxSteps) currentSteps++;
                    currentDecoys = Mathf.Min(startDecoys + solvedInSession / decoyEveryN, 4);
                    Debug.Log("[Batch] Solved! Next steps=" + currentSteps +
                              " decoys=" + currentDecoys);
                }

                if (currentRun % 50 == 0)
                {
                    int s = 0;
                    foreach (var r in records) if (r.solved) s++;
                    Debug.Log("[Batch] " + currentRun + "/" + totalRuns +
                              " Rate=" + (s * 100f / records.Count).ToString("F1") +
                              "% Steps=" + currentSteps);
                }

                yield return new WaitForSeconds(0.1f);
            }

            ExportCSV();
        }

        void ExportCSV()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Run,Level,Solved,Steps,SolutionObjects,Decoys," +
                          "TotalObjects,Iterations,Placements,SolveTimeMs,GenerationTimeMs");
            foreach (var r in records)
                sb.AppendLine(
                    r.run + "," + r.level + "," + (r.solved ? 1 : 0) + "," +
                    r.steps + "," + r.solObjs + "," + r.decoys + "," +
                    r.totalObjs + "," + r.iters + "," + r.placements + "," +
                    r.solMs.ToString("F2") + "," + r.genMs.ToString("F2"));

            string path = Path.Combine(Application.dataPath, csvFileName);
            File.WriteAllText(path, sb.ToString());
            int s2 = 0;
            foreach (var r in records) if (r.solved) s2++;
            Debug.Log("[Batch] COMPLETE " + s2 + "/" + records.Count + " CSV: " + path);
        }
    }
}