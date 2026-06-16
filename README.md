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



**Next Steps:**

* Utilize the data compiled in `PCG_Results.csv` to generate performance metrics visualizations, mapping out the correlation between level complexity and AI computation time.
* Refine the implicit tutorial guidance mechanics by analyzing player onboarding flow relative to the generated layout complexities.

## 🛠️ Technical Stack
*   **Engine:** Unity 
*   **Programming:** C# / Visual Studio 2022
*   **Core Systems:** Procedural Content Generation (PCG), AI Pathfinding, Backtracking Algorithms.
