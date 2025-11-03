# Defrag Simulator

A visual defragmentation simulator built on .NET 9 with a WPF UI. It demonstrates planning, batching, and safe application of disk "moves" across multiple phases, plus a final verification scan.

Mostly built to have colours update on the screen for those that miss the good-old HDD defrag type experience.

<img width="965" height="576" alt="image" src="https://github.com/user-attachments/assets/0b68a541-f738-4218-8c01-b6e4724a9878" />

## Solution layout
- DefragSimulator.Core (net9.0)
  - Engine, data model, planners, progress/events, speed mapping, batching.
- DefragSimulator.UI (net9.0-windows)
  - WPF UI with a scrollable grid, status bar, legend, dark/light themes, and animated batches.
- DefragSimulator.Runner (net9.0)
  - Minimal console runner for diagnostics and headless verification.

## Requirements
- Windows 10/11
- .NET SDK 9.x with WindowsDesktop workload (WPF)
- Rider or Visual Studio with “.NET Desktop Development” workload

## Getting started (UI)
1. Open `DefragSimulator.sln` in Rider/Visual Studio.
2. Set `DefragSimulator.UI` as the startup project.
3. Run.
4. In the app:
   - Choose a grid size: 20×40, 25×50, 30×60, or 40×90.
   - Adjust the Speed slider. The label shows the computed animation delay in ms.
   - Click Analyze to seed and reveal blocks.
   - Click Defrag to run all phases then the Final Scan.
   - Click Stop to cancel gracefully (current batch may still commit).

## Controls & UI
- Top bar: Analyze, Defrag, Stop; grid size presets; speed slider with delay label; dark mode toggle.
- Main area: scrollable `UniformGrid` of cells with automatic sizing, 3px gaps between cells, and a 10px outer margin.
- Bottom status bar: left-aligned status text; right side shows a compact progress bar with percent and a right-aligned legend.
- Legend colors (single source of truth via theme resources):
  - Boot: `BootCategoryBrush`
  - System: `SystemCategoryBrush`
  - Paging: `PagingCategoryBrush`
  - User: `UserCategoryBrush`
  - Moving: `MovingCategoryBrush`
  - Complete (visual): category fill + green border (`FinalAccentBrush`)
  - Free: `FreeCategoryBrush` (transparent fill) + `CellStrokeBrush` outline

## Themes
- Dark mode is the default (modern blue-leaning palette). Toggle to switch to Light.
- Colors and thickness values are centralized in:
  - `DefragSimulator.UI/Themes/Theme.Dark.xaml`
  - `DefragSimulator.UI/Themes/Theme.Light.xaml`
- Key resources you can edit:
  - `AppBackgroundBrush`, `AppForegroundBrush`, `CellStrokeBrush`, `StatusBarBackgroundBrush`
  - `BootCategoryBrush`, `SystemCategoryBrush`, `PagingCategoryBrush`, `UserCategoryBrush`, `MovingCategoryBrush`, `FreeCategoryBrush`
  - `FinalAccentBrush` (green completion border), `DangerAccentBrush` (highlight)
  - `HighlightStrokeThickness`, `CellBorderThicknessDefault`

## Visual rules
- No green fill until the Final Scan. Completion is indicated by a green border only.
- Completed cells retain their original category fill and receive:
  - A thick green outer border (`FinalAccentBrush`, thickness = `HighlightStrokeThickness`).
  - A subtle 1px inner border (drawn via an adorner) using `AppBackgroundBrush` for a crisp edge.
- Moving batches:
  - Sources are highlighted with a red border (`DangerAccentBrush`) and filled with `MovingCategoryBrush` during the animation window.
- Free cells are transparent (fill) with a subtle grid outline (`CellStrokeBrush`).

## Speed mapping (non-linear)
Given slider value `s` in [0, 100]:
- `normalized = s / 100.0`
- `speedFactor = Math.Pow(1 - normalized, 2)`
- `defragDelayMs = MinDelayMs + speedFactor * (MaxDelayMs - MinDelayMs)`
- `analyzeDelayMs = Max(1, defragDelayMs / 3)`
Defaults in core:
- `MinDelayMs = 5`, `MaxDelayMs = 300`, `AnalyzeRevealBatchSize = 6`, `MaxConcurrentOpsPerBatch = 2`

## Core model & public API (summary)
- Array model: `BlockInfo[]` with `CellCategory`, `CategoryId`, `IsFinal`.
- Snapshot DTO: `CellState` for reveals and UI painting.
- Grid: `GridSize(Rows, Columns)`.
- Phases: `Phase1_FirstMove`, `Phase2_BootToFront`, `Phase3_SystemPagingOrder`, `Phase4_UserOrdering`, `PhaseFinalScan`.
- Engine: `IDefragEngine` with async APIs and events (`ProgressUpdated`, `StatusUpdated`, `PhaseStarted`, `PhaseCompleted`).

## Analyze behavior
- Picks a random used percentage between 35% and 85% of total cells.
- Category counts (of used): Boot 5–10%, System 10–15%, Paging 5–10%, User = remainder.
- Assigns `CategoryId` 1..N within each category to randomly positioned cells.
- Reveals in deterministic batches of 6 with delay ≈ defrag/3, cancelable.
- Returns `AnalyzeResult` with counts and used%.

## Defrag phases (planning + application)
- Planning uses a simulated copy of the array and yields `MoveBatch` items; the UI animates then calls `ApplyMoveBatchAsync` to commit.
- Batching: up to 2 logical operations per batch (swaps count as 1).
- Robustness: if a commit mismatch is detected, the engine logs and recomputes the remaining plan for the phase.

Phase details:
- Phase 1 — First Move (Boot/System/Paging are fixed)
  - Moves User blocks toward the tail to free the front.
  - Chooses a tail window that minimizes future distance to the Users’ final region, preserving User ID order.
- Phase 2 — Boot to Front
  - Places Boot blocks at the start by ascending `CategoryId` (swaps allowed).
- Phase 3 — System then Paging
  - Orders System then Paging by `CategoryId` right after Boot.
- Phase 4 — User ordering
  - Orders Users by `CategoryId` after System+Paging.
- Final Scan — Verification/marking
  - Confirms final order and marks blocks `IsFinal=true`.
  - Optimized to scan up to 4 blocks per batch for speed.

## Events, progress, and cancellation
- `ProgressUpdated`: phase and overall percent; planned vs. completed batches.
- `StatusUpdated`: friendly user messages (e.g., plan creation/recompute).
- `PhaseStarted` / `PhaseCompleted` events for UI status.
- Cancellation: Stop cancels further planning but allows the in-flight batch to finish and commit.

## Console runner (optional)
- Set `DefragSimulator.Runner` as startup or run `dotnet run` in its folder.
- It initializes a 10×10 grid, runs Analyze, then plans/applies the full defrag while printing logs.

## Configuration knobs (from `IDefragEngine`)
- `MinDelayMs`, `MaxDelayMs`, `AnalyzeDelayFactor`
- `AnalyzeRevealBatchSize`
- `MaxConcurrentOpsPerBatch`

## Troubleshooting
- Build errors about WPF/WindowsDesktop SDK:
  - Ensure you are on Windows and have the WindowsDesktop workload installed.
  - In Visual Studio Installer, add “.NET Desktop Development”.
- If the UI doesn’t repaint after a grid-size change, wait briefly: the app cancels and re-initializes the grid before rebuilding.
- After pressing Stop, give the current animation one delay interval to finish committing before starting a new action.

## Acceptance checklist
- Builds and runs on net9.0-windows.
- Analyze meets the seeding and reveal criteria above.
- Defrag runs phases 1–4 then Final Scan (4-at-a-time), maintaining category and ID ordering.
- No green fill before Final Scan; completion is a green border with an inner 1px border.
- Up to 2 logical operations animate concurrently per batch; swaps count as 1.
- Stop cancels subsequent batches but allows the current one to finish and commit.
