# 🎮 Procedural Puzzle Generator for Light-Based Puzzles
**MSc Computer Games Technology - Research Project**

---

## 📅 Work Diary

### 🆕 Week 1 (4 June 2026)
**Key Achievements:**

*   **2D Logical Grid Backend:** 
    *   Developed the core grid-based system in Unity to manage puzzle coordinates and object placement.
    *   Implemented a logical layer to separate game data from visual representation, ensuring the generation algorithm remains efficient.
*   **Laser and Mirror Logic:** 
    *   Programmed the reflection physics and interaction between laser emitters, mirrors, and targets.
    *   Ensured accurate path-tracing within the 2D grid environment.
*   **AI Solver Agent (Backtracking Search Bot):**
    *   Implemented a baseline AI solver using a **Backtracking Search** algorithm.
    *   The bot can currently navigate the grid to find valid paths and solve basic puzzle configurations to verify solvability.

**Next Steps:**
*   Refine and optimize the generation algorithm (Procedural Content Generation) to increase puzzle complexity.
*   Explore and implement **Backward Chaining** to improve the efficiency and quality of the generated puzzles.

---
### Week 2 (11 June 2026)

**Key Achievements:**

* **Advanced Procedural Content Generation (Backward Chaining):**
* **Backward Chaining Algorithm:** Implemented a reverse-engineered procedural generation (PCG) pipeline that constructs puzzles backward from the goal (Receiver) to the starting point (Emitter). This guarantees that generated levels have a strictly defined minimum number of steps required for a valid solution.


* **Pillar-Based Maze Obstacles:** Added a standalone pillar obstacle system utilizing a safe-distance buffer calculation. The algorithm ensures pillars maintain at least a 2-cell distance from the designated solution path and avoids creating dead ends, guaranteeing traversability for both players and AI agents.


* **Intelligent Decoy Placement:** Developed a sub-system to procedurally distribute decoy objects (such as mirrors or refractors) outside the critical path to elevate the puzzle's complexity and cognitive challenge.




* **Upgraded AI Solver Agent (v3 - Hybrid Search Engine):**
* **Phase 1A (On-Beam Separation Strategy):** Optimized rotation combo lookups by separating interactable objects directly intersecting the initial beam path (on-beam) from ambient decoys (off-beam). This drastically prunes the search space and accelerates processing speeds.


* **Phase 1B (Beam Search Implementation):** Replaced the legacy unbounded Depth-First Search (DFS) for positional manipulation with a structured **Beam Search** algorithm ($BEAM\_WIDTH = 64$). By expanding only the top-scoring beam states at each depth level, the engine completely eliminates exponential node explosion and potential memory leaks in highly complex rooms.


* **Smart Execution & Physics Net:** Upgraded the physical traversal layer with a greedy, nearest-first execution system to minimize grid movement overhead. Additionally, integrated a robust physics-stuck detection net that automatically teleports the agent if it collides with complex scene geometry, preventing soft-locks during automated testing.




* **Robust Physics & Laser System Fixes:**
* **3-Layer Refractor Logic:** Resolved erratic prism behavior by introducing a 3-layer filtering system that checks object rotation, cross-sectional plane alignment, and incoming beam vectors. Light now accurately refracts at a perfect $90^\circ$ angle only when striking valid optical surfaces, while naturally passing through if clipping outer edges or corners.


* **Automated Batch Runner & Data Export:** Formulated an automated batch-testing framework capable of executing consecutive puzzle-solving simulation routines (ranging from 50 to 1,000 runs) under a progressive difficulty curve. Results are seamlessly exported to a `PCG_Results.csv` file, archiving critical research metrics including the Metric of Intelligent Design (MID), total nodes expanded, and overall solver success rates.



## Week 3 (21–27 June 2026)

### Research & Evaluation
- Ran initial 1,000-run batch experiment using the existing
  adaptive difficulty protocol; identified two critical issues
  in the results: (1) difficulty tier coverage collapsed to a
  single tier (Steps=9, Decoys=4) within ~10 runs, accounting
  for 97.9% of all data, and (2) SearchTimeMs consistently
  exceeded the intended 8-second cap due to an orphaned search
  coroutine racing with CorrectionSweep after timeout.

### Bug Fixes
- **AISolverAgent.cs** — Fixed orphaned coroutine bug in
  `Pipeline()`: stored the Phase-1 search coroutine handle in
  `_searchCoroutine` and added an explicit `StopCoroutine()`
  call on timeout, preventing the search from silently
  overwriting `SolvePhase` and `SolveIterations` after
  CorrectionSweep had already concluded.
- **AISolverAgent.cs** — Added `SweepIterations` and
  `SweepRelocations` fields to track solver effort inside
  CorrectionSweep separately from Phase-1 search effort.
- **AISolverAgent.cs** — Refined `SolvePhase` labels from a
  single `"Sweep"` bucket into three distinct sub-stages:
  `Sweep-S1` (on-beam rotation), `Sweep-S2` (off-beam
  relocation), and `Sweep-S3` (exhaustive fallback), enabling
  finer-grained difficulty signal analysis for RQ2.

### Experimental Design Improvement
- **BatchRunner.cs** — Replaced the adaptive progressive
  difficulty protocol with a stratified sampling mode
  (`useStratifiedSampling = true`): all 40 (Steps × Decoys)
  tier combinations are now cycled deterministically, yielding
  exactly 25 runs per tier across 1,000 total runs. This
  ensures full difficulty-range coverage required for the
  Low/Medium/High MID comparison described in Section 3.4.2.

### Validated Experiment (N = 1,000, Stratified)
- Re-ran the full 1,000-run batch with both fixes applied.
- Overall solve rate: **55.6%** across all 40 difficulty tiers.
- MID components now correlate positively with solver effort
  (SweepIterations r ≈ 0.33–0.45; SolveTimeMs r ≈ 0.34–0.48)
  and negatively with solve probability, consistent with the
  design assumptions underlying the MID weighting scheme.
- Cₗ (Linear Complexity) shows the strongest correlation with
  solver effort among the three components across all three
  effort measures, providing empirical post-hoc support for
  the highest assigned weight (α = 0.5).

### Advisor Meeting Preparation
- Prepared 2-slide deck and one-page Q&A brief for Friday
  advisor meeting addressing: "How does your bot tell us how
  difficult a puzzle is?" with empirical evidence from the
  validated 1,000-run dataset.

### Next Steps
- [ ] Incorporate section-insertion paragraphs into the full
      manuscript Word document and send revised draft to advisor.
- [ ] Produce expressive range analysis figures (Section 3.4.3):
      scatter plot of generated puzzles in Cₗ×Cₕ feature space,
      showing coverage and spread across difficulty tiers.
- [ ] Reduce SearchTimeMs overshoot: lower `YIELD_EVERY` from
      200 to ~20 inside `RotationSearch` so the 8-second deadline
      check fires more frequently and timeout is respected tightly.
      (121/1,000 runs currently exceed 8 s due to long yield
      intervals — noted as a known limitation for now.)
- [ ] Advisor meeting — Friday 27 June: answer "How does your
      bot tell us how difficult a puzzle is?" and present
      correlation results from the validated dataset.
- [ ] Based on advisor feedback, finalise Limitations section
      entries covering: (a) a priori MID weights not empirically
      fitted, (b) SearchTimeMs overshoot, (c) human playtesting
      deferred to future work.
## 🛠️ Technical Stack
*   **Engine:** Unity 
*   **Programming:** C# / Visual Studio 2022
*   **Core Systems:** Procedural Content Generation (PCG), AI Pathfinding, Backtracking Algorithms.
