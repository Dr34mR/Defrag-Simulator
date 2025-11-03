using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DefragSimulator.Core
{
    public enum CellCategory { Boot, System, Paging, User, FreeSpace }

    public enum PhaseType { Phase1_FirstMove, Phase2_BootToFront, Phase3_SystemPagingOrder, Phase4_UserOrdering, PhaseFinalScan }

    public record GridSize(int Rows, int Columns);

    public record BlockInfo(int Index, CellCategory Category, int? CategoryId, bool IsFinal);

    public record CellState(int Index, CellCategory Category, int? CategoryId, bool IsFinal);

    public record AnalyzeResult(int TotalCells, int UsedCount, IReadOnlyDictionary<CellCategory, int> CategoryCounts, double UsedPercentage);

    public record MovePlan(int SourceIndex, int DestinationIndex, bool IsSwap, CellCategory Category, int? CategoryId, int? OtherCategoryId = null);

    public record MoveBatch(IReadOnlyList<MovePlan> Moves, PhaseType Phase);

    public class ProgressInfo : EventArgs
    {
        public PhaseType Phase { get; set; }
        public double PhasePercent { get; set; }
        public double OverallPercent { get; set; }
        public int PhasePlannedBatches { get; set; }
        public int PhaseCompletedBatches { get; set; }
        public string? Status { get; set; }
    }

    public interface IDefragEngine : IDisposable
    {
        public static int MinDelayMs => 5;
        public static int MaxDelayMs => 300;
        public static double AnalyzeDelayFactor => 1.0 / 3.0;
        public static int AnalyzeRevealBatchSize => 6;
        public static int MaxConcurrentOpsPerBatch => 2;

        Task InitializeAsync(GridSize gridSize, CancellationToken token);
        Task<AnalyzeResult> AnalyzeAsync(IProgress<CellState> revealProgress, int analyzeDelayMs, CancellationToken token);
        IAsyncEnumerable<MoveBatch> PlanFullDefragAsync(int defragDelayMs, CancellationToken token);
        IAsyncEnumerable<MoveBatch> PlanPhaseAsync(PhaseType phase, int defragDelayMs, CancellationToken token);
        Task ApplyMoveBatchAsync(MoveBatch batch, CancellationToken token);
        Task<BlockInfo[]> GetSnapshotAsync();

        event EventHandler<ProgressInfo>? ProgressUpdated;
        event EventHandler<string>? StatusUpdated;
        event EventHandler<PhaseType>? PhaseStarted;
        event EventHandler<PhaseType>? PhaseCompleted;
    }

    // A basic implementation that satisfies the public API and the Analyze behavior from the spec.
    public class DefragEngine : IDefragEngine
    {
        private readonly object _lock = new();
        private readonly Random _rng = new();

        private GridSize _size = new GridSize(0, 0);
        private BlockInfo[] _blocks = Array.Empty<BlockInfo>();
        private volatile bool _recomputeRequested = false;

        public event EventHandler<ProgressInfo>? ProgressUpdated;
        public event EventHandler<string>? StatusUpdated;
        public event EventHandler<PhaseType>? PhaseStarted;
        public event EventHandler<PhaseType>? PhaseCompleted;

        public Task InitializeAsync(GridSize gridSize, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                _size = gridSize;
                int total = Math.Max(0, gridSize.Rows) * Math.Max(0, gridSize.Columns);
                _blocks = Enumerable.Range(0, total)
                    .Select(i => new BlockInfo(i, CellCategory.FreeSpace, null, false))
                    .ToArray();
            }
            StatusUpdated?.Invoke(this, $"Initialized grid {gridSize.Rows}×{gridSize.Columns}.");
            return Task.CompletedTask;
        }

        private static void Shuffle<T>(IList<T> list)
        {
            var rng = new Random();
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private BlockInfo[] CloneBlocks()
        {
            lock (_lock)
            {
                return _blocks.Select(b => new BlockInfo(b.Index, b.Category, b.CategoryId, b.IsFinal)).ToArray();
            }
        }

        // Add the missing RandomBetween method
        private double RandomBetween(double min, double max)
        {
            return min + (_rng.NextDouble() * (max - min));
        }

        public async Task<AnalyzeResult> AnalyzeAsync(IProgress<CellState> revealProgress, int analyzeDelayMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            // Read size and compute counts
            GridSize size;
            BlockInfo[] snapshot;
            lock (_lock)
            {
                size = _size;
                snapshot = _blocks;
            }

            var total = size.Rows * size.Columns;
            if (total == 0) return new AnalyzeResult(0, 0, new Dictionary<CellCategory, int>(), 0);

            // Used percentage between 35% and 85%
            var usedMin = (int)Math.Ceiling(0.35 * total);
            var usedMax = (int)Math.Floor(0.85 * total);
            var usedCount = _rng.Next(usedMin, usedMax + 1);

            // Category counts with improved calculation
            int BootCount = Math.Max(1, (int)Math.Round(usedCount * RandomBetween(0.05, 0.10)));
            int SystemCount = Math.Max(1, (int)Math.Round(usedCount * RandomBetween(0.10, 0.15)));
            int PagingCount = Math.Max(1, (int)Math.Round(usedCount * RandomBetween(0.05, 0.10)));
            int UserCount = usedCount - BootCount - SystemCount - PagingCount;
            if (UserCount < 0)
            {
                // adjust to ensure non-negative
                UserCount = Math.Max(0, usedCount - BootCount - SystemCount);
                PagingCount = Math.Max(0, usedCount - BootCount - SystemCount - UserCount);
            }

            var categoryCounts = new Dictionary<CellCategory, int>
            {
                [CellCategory.Boot] = BootCount,
                [CellCategory.System] = SystemCount,
                [CellCategory.Paging] = PagingCount,
                [CellCategory.User] = UserCount,
                [CellCategory.FreeSpace] = total - usedCount
            };

            // choose occupied indices
            var allIndices = Enumerable.Range(0, total).ToList();
            Shuffle(allIndices);
            var occupied = allIndices.Take(usedCount).ToList();

            // assign categories in the shuffled order
            var assignments = new List<(int Index, CellCategory Category, int? CategoryId)>();
            int pos = 0;
            void AssignRange(CellCategory cat, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    var idx = occupied[pos++];
                    assignments.Add((idx, cat, i + 1));
                }
            }

            AssignRange(CellCategory.Boot, BootCount);
            AssignRange(CellCategory.System, SystemCount);
            AssignRange(CellCategory.Paging, PagingCount);
            AssignRange(CellCategory.User, UserCount);

            // Build reveal list: occupied indices in randomized order
            var revealList = assignments.Select(a => a.Index).ToList();
            Shuffle(revealList);

            var revealBatchSize = IDefragEngine.AnalyzeRevealBatchSize;
            int revealed = 0;
            for (int i = 0; i < revealList.Count; i += revealBatchSize)
            {
                token.ThrowIfCancellationRequested();
                var batch = revealList.Skip(i).Take(revealBatchSize);
                foreach (var idx in batch)
                {
                    var assign = assignments.First(a => a.Index == idx);
                    var cell = new CellState(idx, assign.Category, assign.CategoryId, false);
                    revealProgress?.Report(cell);

                    // Update internal state for snapshot
                    lock (_lock)
                    {
                        _blocks[idx] = new BlockInfo(idx, assign.Category, assign.CategoryId, false);
                    }
                    revealed++;
                }

                // delay between reveal batches
                await Task.Delay(Math.Max(1, analyzeDelayMs), token);
            }

            var usedPercentage = 100.0 * usedCount / total;

            return new AnalyzeResult(total, usedCount, categoryCounts, usedPercentage);
        }

        public async IAsyncEnumerable<MoveBatch> PlanFullDefragAsync(int defragDelayMs, CancellationToken token)
        {
            // Plan and yield all phases in sequence.
            var phases = new[]
            {
                PhaseType.Phase1_FirstMove,
                PhaseType.Phase2_BootToFront,
                PhaseType.Phase3_SystemPagingOrder,
                PhaseType.Phase4_UserOrdering,
                PhaseType.PhaseFinalScan
            };

            double overallPlanned = 0;
            double overallCompleted = 0;

            foreach (var phase in phases)
            {
                token.ThrowIfCancellationRequested();
                PhaseStarted?.Invoke(this, phase);

                var planned = PlanPhaseInternal(phase, token);
                overallPlanned += planned.Count;
                int plannedOps = planned.Sum(b => b.Moves.Count);
                ProgressUpdated?.Invoke(this, new ProgressInfo
                {
                    Phase = phase,
                    PhasePercent = 0,
                    OverallPercent = overallPlanned == 0 ? 0 : overallCompleted / Math.Max(1, overallPlanned) * 100.0,
                    PhasePlannedBatches = planned.Count,
                    PhaseCompletedBatches = 0,
                    Status = string.Format("Plan created for {0}: {1} batches, {2} ops", phase, planned.Count, plannedOps)
                });

                int completedInPhase = 0;
                for (int i = 0; i < planned.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = planned[i];
                    yield return batch;
                    completedInPhase++;
                    overallCompleted++;

                    ProgressUpdated?.Invoke(this, new ProgressInfo
                    {
                        Phase = phase,
                        PhasePercent = planned.Count == 0 ? 100 : 100.0 * completedInPhase / Math.Max(1, planned.Count),
                        OverallPercent = overallPlanned == 0 ? 0 : overallCompleted / Math.Max(1, overallPlanned) * 100.0,
                        PhasePlannedBatches = planned.Count,
                        PhaseCompletedBatches = completedInPhase,
                        Status = string.Format("{0}: batch {1}/{2}", phase, completedInPhase, planned.Count)
                    });

                    // Provide pacing hint to UI (optional)
                    await Task.Delay(Math.Max(1, defragDelayMs), token);

                    // If ApplyMoveBatchAsync detected a mismatch, recompute remaining plan for this phase
                    if (_recomputeRequested)
                    {
                        _recomputeRequested = false;
                        StatusUpdated?.Invoke(this, $"[info] Recomputing remaining plan for {phase} due to mismatch.");
                        var newPlan = PlanPhaseInternal(phase, token);
                        // Replace remaining plan; keep completed count as-is
                        planned = newPlan;
                        // Reset loop to start from beginning of new plan but skip already completed batches count-wise
                        // We can't map old batches to new ones safely; continue yielding new plan from start
                        // Adjust overall planned to reflect new plan size roughly
                        // (We won't try to perfectly account for overallPlanned; keep it stable)
                        i = -1;
                        completedInPhase = 0;
                        ProgressUpdated?.Invoke(this, new ProgressInfo
                        {
                            Phase = phase,
                            PhasePercent = 0,
                            OverallPercent = overallPlanned == 0 ? 0 : overallCompleted / Math.Max(1, overallPlanned) * 100.0,
                            PhasePlannedBatches = planned.Count,
                            PhaseCompletedBatches = 0,
                            Status = string.Format("Plan recomputed for {0}: {1} batches", phase, planned.Count)
                        });
                    }
                }

                PhaseCompleted?.Invoke(this, phase);
            }
        }

        public async IAsyncEnumerable<MoveBatch> PlanPhaseAsync(PhaseType phase, int defragDelayMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PhaseStarted?.Invoke(this, phase);

            var planned = PlanPhaseInternal(phase, token);
            ProgressUpdated?.Invoke(this, new ProgressInfo
            {
                Phase = phase,
                PhasePercent = 0,
                OverallPercent = 0,
                PhasePlannedBatches = planned.Count,
                PhaseCompletedBatches = 0,
                Status = string.Format("Plan created for {0}: {1} batches", phase, planned.Count)
            });

            for (int i = 0, completed = 0; i < planned.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var batch = planned[i];
                yield return batch;
                completed++;
                ProgressUpdated?.Invoke(this, new ProgressInfo
                {
                    Phase = phase,
                    PhasePercent = planned.Count == 0 ? 100 : 100.0 * completed / Math.Max(1, planned.Count),
                    OverallPercent = 0,
                    PhasePlannedBatches = planned.Count,
                    PhaseCompletedBatches = completed,
                    Status = string.Format("{0}: batch {1}/{2}", phase, completed, planned.Count)
                });
                await Task.Delay(Math.Max(1, defragDelayMs), token);

                if (_recomputeRequested)
                {
                    _recomputeRequested = false;
                    StatusUpdated?.Invoke(this, $"[info] Recomputing remaining plan for {phase} due to mismatch.");
                    planned = PlanPhaseInternal(phase, token);
                    i = -1;
                    completed = 0;
                    ProgressUpdated?.Invoke(this, new ProgressInfo
                    {
                        Phase = phase,
                        PhasePercent = 0,
                        OverallPercent = 0,
                        PhasePlannedBatches = planned.Count,
                        PhaseCompletedBatches = 0,
                        Status = string.Format("Plan recomputed for {0}: {1} batches", phase, planned.Count)
                    });
                }
            }

            PhaseCompleted?.Invoke(this, phase);
        }

        public Task ApplyMoveBatchAsync(MoveBatch batch, CancellationToken token)
        {
            // Commit must be robust: apply atomically, accept even if token is canceled.
            lock (_lock)
            {
                if (batch.Phase == PhaseType.PhaseFinalScan)
                {
                    // Final scan: mark IsFinal = true for the indices provided (Source==Destination)
                    foreach (var move in batch.Moves)
                    {
                        var b = _blocks[move.SourceIndex];
                        _blocks[move.SourceIndex] = new BlockInfo(move.SourceIndex, b.Category, b.CategoryId, true);
                    }
                    return Task.CompletedTask;
                }

                foreach (var move in batch.Moves)
                {
                    // Validate source still matches expectation; if not, log warning and request recompute of remaining plan.
                    var src = _blocks[move.SourceIndex];
                    if (src.Category != move.Category || src.CategoryId != move.CategoryId)
                    {
                        StatusUpdated?.Invoke(this, $"[warn] Mismatch at source {move.SourceIndex}, expected {move.Category}/{move.CategoryId}, found {src.Category}/{src.CategoryId}. Requesting plan recompute.");
                        _recomputeRequested = true;
                        continue;
                    }

                    if (move.IsSwap)
                    {
                        var a = src;
                        var b = _blocks[move.DestinationIndex];
                        _blocks[move.SourceIndex] = new BlockInfo(move.SourceIndex, b.Category, b.CategoryId, b.IsFinal);
                        _blocks[move.DestinationIndex] = new BlockInfo(move.DestinationIndex, a.Category, a.CategoryId, a.IsFinal);
                    }
                    else
                    {
                        // Move src to destination, leaving source as FreeSpace
                        _blocks[move.DestinationIndex] = new BlockInfo(move.DestinationIndex, src.Category, src.CategoryId, src.IsFinal);
                        _blocks[move.SourceIndex] = new BlockInfo(move.SourceIndex, CellCategory.FreeSpace, null, false);
                    }
                }
            }
            return Task.CompletedTask;
        }

        public Task<BlockInfo[]> GetSnapshotAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(_blocks.Select(b => new BlockInfo(b.Index, b.Category, b.CategoryId, b.IsFinal)).ToArray());
            }
        }

        public void Dispose()
        {
            // no-op
        }

        // Planning internals
        private List<MoveBatch> PlanPhaseInternal(PhaseType phase, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var sim = CloneBlocks();
            var ops = new List<MovePlan>();

            // helpers
            Func<CellCategory, IEnumerable<int>> indicesOf = cat => Enumerable.Range(0, sim.Length).Where(i => sim[i].Category == cat);
            int bootCount = indicesOf(CellCategory.Boot).Count();
            int systemCount = indicesOf(CellCategory.System).Count();
            int pagingCount = indicesOf(CellCategory.Paging).Count();
            int userCount = indicesOf(CellCategory.User).Count();

            switch (phase)
            {
                case PhaseType.Phase1_FirstMove:
                {
                    // Strategy: move User blocks toward the tail to free the front, but pick tail positions that
                    // minimize the eventual distance back to their final region (right after Boot+System+Paging).
                    // Fixed: Boot/System/Paging (cannot be moved)
                    var fixedSet = new HashSet<CellCategory> { CellCategory.Boot, CellCategory.System, CellCategory.Paging };

                    // Movable users in ID ascending (preserve order)
                    var userIds = Enumerable.Range(1, userCount).ToList();

                    // Non-fixed indices (User or FreeSpace), ascending
                    var nonFixed = Enumerable.Range(0, sim.Length)
                        .Where(i => !fixedSet.Contains(sim[i].Category))
                        .ToList();

                    // Desired final region for Users starts after Boot+System+Paging
                    int finalStart = bootCount + systemCount + pagingCount;
                    var desiredFinal = Enumerable.Range(finalStart, Math.Max(0, userCount)).ToArray();

                    List<int> targetSlots;
                    if (nonFixed.Count >= userCount && userCount > 0)
                    {
                        // Slide a window of length userCount over nonFixed and pick the one with minimal
                        // total distance to desiredFinal (sum of abs(window[i] - desiredFinal[i])).
                        // Tie-breaker: prefer the latest window (closer to the tail) to keep space at the very front.
                        long bestScore = long.MaxValue;
                        int bestStart = 0;
                        for (int start = 0; start <= nonFixed.Count - userCount; start++)
                        {
                            long score = 0;
                            for (int j = 0; j < userCount; j++)
                            {
                                int pos = nonFixed[start + j];
                                int want = desiredFinal[j];
                                score += Math.Abs(pos - want);
                                if (score > bestScore) break; // early exit
                            }
                            if (score < bestScore || (score == bestScore && start > bestStart))
                            {
                                bestScore = score;
                                bestStart = start;
                            }
                        }
                        targetSlots = nonFixed.Skip(bestStart).Take(userCount).ToList();
                    }
                    else
                    {
                        // Fallback: previous behavior (last N non-fixed toward the end)
                        targetSlots = nonFixed.Skip(Math.Max(0, nonFixed.Count - userCount)).ToList();
                    }

                    int maxMoves = Math.Min(userIds.Count, targetSlots.Count);
                    for (int k = 0; k < maxMoves; k++)
                    {
                        token.ThrowIfCancellationRequested();
                        int desiredUserId = userIds[k];
                        int srcIdx = Array.FindIndex(sim, b => b.Category == CellCategory.User && b.CategoryId == desiredUserId);
                        if (srcIdx < 0) continue; // user not found

                        int tgtIdx = targetSlots[k];
                        if (srcIdx == tgtIdx) continue; // already at target

                        var tgt = sim[tgtIdx];
                        if (tgt.Category == CellCategory.FreeSpace)
                        {
                            // Simple move into chosen slot
                            ops.Add(new MovePlan(srcIdx, tgtIdx, false, CellCategory.User, desiredUserId));
                            // simulate
                            var src = sim[srcIdx];
                            sim[tgtIdx] = new BlockInfo(tgtIdx, src.Category, src.CategoryId, src.IsFinal);
                            sim[srcIdx] = new BlockInfo(srcIdx, CellCategory.FreeSpace, null, false);
                        }
                        else if (tgt.Category == CellCategory.User)
                        {
                            // Swap with another user occupying the target slot
                            int tgtId = tgt.CategoryId ?? int.MaxValue;
                            if (tgtId != desiredUserId)
                            {
                                ops.Add(new MovePlan(srcIdx, tgtIdx, true, CellCategory.User, desiredUserId, tgtId));
                                var a = sim[srcIdx];
                                var b = sim[tgtIdx];
                                // simulate swap
                                sim[srcIdx] = new BlockInfo(srcIdx, b.Category, b.CategoryId, b.IsFinal);
                                sim[tgtIdx] = new BlockInfo(tgtIdx, a.Category, a.CategoryId, a.IsFinal);
                            }
                        }
                        // Targets are selected from non-fixed, so shouldn't be fixed here.
                    }
                    break;
                }
                case PhaseType.Phase2_BootToFront:
                {
                    for (int i = 0; i < bootCount; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        int desiredId = i + 1;
                        int srcIdx = Array.FindIndex(sim, b => b.Category == CellCategory.Boot && b.CategoryId == desiredId);
                        if (srcIdx < 0) continue;
                        int tgtIdx = i;
                        if (srcIdx == tgtIdx) continue;

                        // plan swap into front slot
                        ops.Add(new MovePlan(srcIdx, tgtIdx, true, CellCategory.Boot, desiredId));
                        // simulate swap
                        var a = sim[srcIdx];
                        var b = sim[tgtIdx];
                        sim[srcIdx] = new BlockInfo(srcIdx, b.Category, b.CategoryId, b.IsFinal);
                        sim[tgtIdx] = new BlockInfo(tgtIdx, a.Category, a.CategoryId, a.IsFinal);
                    }
                    break;
                }
                case PhaseType.Phase3_SystemPagingOrder:
                {
                    int start = bootCount;
                    var desired = new List<(CellCategory cat, int id)>();
                    for (int id = 1; id <= systemCount; id++) desired.Add((CellCategory.System, id));
                    for (int id = 1; id <= pagingCount; id++) desired.Add((CellCategory.Paging, id));

                    for (int k = 0; k < desired.Count; k++)
                    {
                        token.ThrowIfCancellationRequested();
                        var (cat, id) = desired[k];
                        int tgtIdx = start + k;
                        int srcIdx = Array.FindIndex(sim, b => b.Category == cat && b.CategoryId == id);
                        if (srcIdx < 0) continue;
                        if (srcIdx == tgtIdx) continue;

                        if (sim[tgtIdx].Category == CellCategory.FreeSpace)
                        {
                            ops.Add(new MovePlan(srcIdx, tgtIdx, false, cat, id));
                            var src = sim[srcIdx];
                            sim[tgtIdx] = new BlockInfo(tgtIdx, src.Category, src.CategoryId, src.IsFinal);
                            sim[srcIdx] = new BlockInfo(srcIdx, CellCategory.FreeSpace, null, false);
                        }
                        else
                        {
                            ops.Add(new MovePlan(srcIdx, tgtIdx, true, cat, id));
                            var a = sim[srcIdx];
                            var b = sim[tgtIdx];
                            sim[srcIdx] = new BlockInfo(srcIdx, b.Category, b.CategoryId, b.IsFinal);
                            sim[tgtIdx] = new BlockInfo(tgtIdx, a.Category, a.CategoryId, a.IsFinal);
                        }
                    }
                    break;
                }
                case PhaseType.Phase4_UserOrdering:
                {
                    int start = bootCount + systemCount + pagingCount;
                    for (int id = 1; id <= userCount; id++)
                    {
                        token.ThrowIfCancellationRequested();
                        int tgtIdx = start + (id - 1);
                        int srcIdx = Array.FindIndex(sim, b => b.Category == CellCategory.User && b.CategoryId == id);
                        if (srcIdx < 0) continue;
                        if (srcIdx == tgtIdx) continue;

                        if (sim[tgtIdx].Category == CellCategory.FreeSpace)
                        {
                            ops.Add(new MovePlan(srcIdx, tgtIdx, false, CellCategory.User, id));
                            var src = sim[srcIdx];
                            sim[tgtIdx] = new BlockInfo(tgtIdx, src.Category, src.CategoryId, src.IsFinal);
                            sim[srcIdx] = new BlockInfo(srcIdx, CellCategory.FreeSpace, null, false);
                        }
                        else
                        {
                            ops.Add(new MovePlan(srcIdx, tgtIdx, true, CellCategory.User, id));
                            var a = sim[srcIdx];
                            var b = sim[tgtIdx];
                            sim[srcIdx] = new BlockInfo(srcIdx, b.Category, b.CategoryId, b.IsFinal);
                            sim[tgtIdx] = new BlockInfo(tgtIdx, a.Category, a.CategoryId, a.IsFinal);
                        }
                    }
                    break;
                }
                case PhaseType.PhaseFinalScan:
                {
                    // Group up to 4 cells per batch to accelerate final confirmation
                    var batches = new List<MoveBatch>();
                    var group = new List<MovePlan>(4);
                    for (int i = 0; i < sim.Length; i++)
                    {
                        if (sim[i].Category == CellCategory.FreeSpace) continue;
                        var m = new MovePlan(i, i, false, sim[i].Category, sim[i].CategoryId);
                        group.Add(m);
                        if (group.Count == 4)
                        {
                            batches.Add(new MoveBatch(new List<MovePlan>(group), PhaseType.PhaseFinalScan));
                            group.Clear();
                        }
                    }
                    if (group.Count > 0)
                    {
                        batches.Add(new MoveBatch(new List<MovePlan>(group), PhaseType.PhaseFinalScan));
                        group.Clear();
                    }
                    return batches;
                }
            }

            // Pack ops into batches (up to 2 logical ops per batch)
            var resultBatches = new List<MoveBatch>();
            int maxOps = IDefragEngine.MaxConcurrentOpsPerBatch;
            for (int i = 0; i < ops.Count; )
            {
                var group = new List<MovePlan>();
                for (int k = 0; k < maxOps && i < ops.Count; k++, i++)
                {
                    group.Add(ops[i]);
                }
                resultBatches.Add(new MoveBatch(group, phase));
            }
            return resultBatches;
        }
    }
}
