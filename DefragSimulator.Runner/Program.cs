using System;
using System.Threading;
using System.Threading.Tasks;
using DefragSimulator.Core;

class Program
{
    static async Task<int> Main()
    {
        using var engine = new DefragEngine();
        var cts = new CancellationTokenSource();

        engine.StatusUpdated += (s, msg) => Console.WriteLine($"[status] {msg}");
        engine.PhaseStarted += (s, p) => Console.WriteLine($"[phase-start] {p}");
        engine.PhaseCompleted += (s, p) => Console.WriteLine($"[phase-done] {p}");
        engine.ProgressUpdated += (s, pr) => Console.WriteLine($"[progress] {pr.Phase} {pr.PhaseCompletedBatches}/{pr.PhasePlannedBatches} phase%={pr.PhasePercent:F1} overall%={pr.OverallPercent:F1}");

        var grid = new GridSize(10, 10); // 100 cells
        await engine.InitializeAsync(grid, CancellationToken.None);

        Console.WriteLine("Starting Analyze...");
        var progress = new Progress<CellState>(cell =>
        {
            // Console.WriteLine($"Revealed: idx={cell.Index}, cat={cell.Category}, id={cell.CategoryId}");
        });

        int slider = 50; // pretend UI slider 0..100
        double normalized = slider / 100.0;
        double speedFactor = Math.Pow(1 - normalized, 2);
        int defragDelayMs = (int)Math.Round(IDefragEngine.MinDelayMs + speedFactor * (IDefragEngine.MaxDelayMs - IDefragEngine.MinDelayMs));
        int analyzeDelayMs = Math.Max(1, defragDelayMs / 3);
        Console.WriteLine($"Delays: defrag={defragDelayMs}ms analyze={analyzeDelayMs}ms");

        var result = await engine.AnalyzeAsync(progress, analyzeDelayMs, cts.Token);

        Console.WriteLine($"Analyze complete. TotalCells={result.TotalCells}, Used={result.UsedCount}, Used%={result.UsedPercentage:F2}");
        foreach (var kv in result.CategoryCounts)
        {
            Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }

        Console.WriteLine("Planning + applying full defrag...");
        await foreach (var batch in engine.PlanFullDefragAsync(defragDelayMs, cts.Token))
        {
            // Simulate UI animation delay
            await Task.Delay(defragDelayMs);
            await engine.ApplyMoveBatchAsync(batch, CancellationToken.None);
        }

        var snap = await engine.GetSnapshotAsync();
        Console.WriteLine("Final snapshot sample (first 20 cells):");
        for (int i = 0; i < Math.Min(20, snap.Length); i++)
        {
            var b = snap[i];
            Console.WriteLine($"idx={b.Index}, cat={b.Category}, id={b.CategoryId}, final={b.IsFinal}");
        }

        Console.WriteLine("Done.");
        return 0;
    }
}
