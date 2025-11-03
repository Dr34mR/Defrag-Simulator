using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Documents;
using DefragSimulator.Core;

namespace DefragSimulator.UI
{
    public partial class MainWindow : Window
    {
        private Brush BootBrush => TryFindResource("BootCategoryBrush") as Brush ?? (Brush)new BrushConverter().ConvertFrom("#7B4CFF")!;
        private Brush SystemBrush => TryFindResource("SystemCategoryBrush") as Brush ?? (Brush)new BrushConverter().ConvertFrom("#648CDC")!;
        private Brush PagingBrush => TryFindResource("PagingCategoryBrush") as Brush ?? (Brush)new BrushConverter().ConvertFrom("#C8A03C")!;
        private Brush UserBrush => TryFindResource("UserCategoryBrush") as Brush ?? (Brush)new BrushConverter().ConvertFrom("#C35046")!;
        private Brush FreeBrush => TryFindResource("FreeCategoryBrush") as Brush ?? Brushes.Transparent;
        
        private Brush MovingBrush => TryFindResource("MovingCategoryBrush") as Brush ?? (Brush)new BrushConverter().ConvertFrom("#D0D0D0")!;

        private DefragEngine _engine = new();
        private List<Rectangle> _rects = new();
        private GridSize _gridSize = new GridSize(20, 40);
        private CancellationTokenSource? _cts;
        private bool _loadedInitialized;
        private bool _darkMode = true;

        // Highlight tracking
        private readonly HashSet<int> _highlighted = new();
        private readonly HashSet<int> _finalized = new();
        private readonly Dictionary<int, InnerBorderAdorner> _finalInnerBorders = new();
        private Brush? _themeStrokeBrushCache;
        private double _defaultStrokeThickness = 0.25;
        private double _highlightStrokeThickness = 3.0; // doubled thickness for moving and complete borders
        private Brush HighlightBrush => TryFindResource("DangerAccentBrush") as Brush ?? Brushes.Red;
        private Brush FinalBorderBrush => TryFindResource("FinalAccentBrush") as Brush ?? (Brush)new BrushConverter().ConvertFrom("#46B45A")!;
        private Brush InnerBorderBrush => TryFindResource("AppBackgroundBrush") as Brush ?? Brushes.Black;
        private double _innerBorderThickness = 1.0;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Unloaded += MainWindow_Unloaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Defaults
            GridSizeCombo.SelectedIndex = 0; // 20x40
            SpeedSlider.Value = 50;
            UpdateDelayLabel();

            // Ensure default theme (dark) toggle reflects current resources
            ThemeToggle.IsChecked = true;

            HookEngineEvents();
            RebuildGrid(_gridSize.Rows, _gridSize.Columns);
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _engine.Dispose();
        }

        private void HookEngineEvents()
        {
            _engine.StatusUpdated += (s, msg) => Dispatcher.Invoke((Action)(() =>
            {
                var friendly = MapStatusMessage(msg);
                if (!string.IsNullOrEmpty(friendly))
                    StatusText.Text = friendly!;
            }));
            _engine.ProgressUpdated += (s, pr) => Dispatcher.Invoke((Action)(() =>
            {
                OverallProgress.Value = pr.OverallPercent;
                PercentText.Text = $"{pr.OverallPercent:F0}%";
            }));
            _engine.PhaseStarted += (s, ph) => Dispatcher.Invoke((Action)(() => StatusText.Text = FriendlyPhaseText(ph, true)));
            _engine.PhaseCompleted += (s, ph) => Dispatcher.Invoke((Action)(() => StatusText.Text = FriendlyPhaseText(ph, false)));
        }

        private void RebuildGrid(int rows, int cols)
        {
            // Remove any existing adorners before rebuilding
            RemoveAllFinalInnerBorders();

            CellsGrid.Rows = rows;
            CellsGrid.Columns = cols;
            CellsGrid.Children.Clear();
            _rects.Clear();
            _highlighted.Clear();
            _finalized.Clear();
            _finalInnerBorders.Clear();
            int total = rows * cols;
            var stroke = TryFindResource("CellStrokeBrush") as Brush ?? Brushes.Black;
            // Load thickness values from theme if provided
            if (TryFindResource("CellBorderThicknessDefault") is double defThick) _defaultStrokeThickness = defThick;
            if (TryFindResource("HighlightStrokeThickness") is double hiThick) _highlightStrokeThickness = hiThick;
            _themeStrokeBrushCache = stroke;
            for (int i = 0; i < total; i++)
            {
                var r = new Rectangle
                {
                    Fill = FreeBrush,
                    Stroke = stroke,
                    StrokeThickness = _defaultStrokeThickness,
                    Margin = new Thickness(1.5), // 3px gap between cells
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                _rects.Add(r);
                CellsGrid.Children.Add(r);
            }
        }

        private Brush BrushFor(BlockInfo b)
        {
            // Always use the original category color for fill; completion is indicated via green border only.
            return b.Category switch
            {
                CellCategory.Boot => BootBrush,
                CellCategory.System => SystemBrush,
                CellCategory.Paging => PagingBrush,
                CellCategory.User => UserBrush,
                _ => FreeBrush
            };
        }

        private static (int defragMs, int analyzeMs) ComputeDelays(double sliderValue)
        {
            double normalized = sliderValue / 100.0;
            double speedFactor = Math.Pow(1 - normalized, 2);
            int defragDelayMs = (int)Math.Round(IDefragEngine.MinDelayMs + speedFactor * (IDefragEngine.MaxDelayMs - IDefragEngine.MinDelayMs));
            int analyzeDelayMs = Math.Max(1, defragDelayMs / 3);
            return (defragDelayMs, analyzeDelayMs);
        }

        private void UpdateDelayLabel()
        {
            var (defragMs, analyzeMs) = ComputeDelays(SpeedSlider.Value);
            DelayLabel.Text = $"{defragMs} ms";
        }

        private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            ToggleControls(isRunning: true);
            _cts = new CancellationTokenSource();
            try
            {
                var (defragMs, analyzeMs) = ComputeDelays(SpeedSlider.Value);

                // Clear the grid immediately so the UI is empty before Analyze starts revealing
                ClearVisualGrid();

                await _engine.InitializeAsync(_gridSize, _cts.Token);

                var progress = new Progress<CellState>(cell =>
                {
                    // Update the single cell appearance
                    if (cell.Index >= 0 && cell.Index < _rects.Count)
                    {
                        _rects[cell.Index].Fill = cell.Category switch
                        {
                            CellCategory.Boot => BootBrush,
                            CellCategory.System => SystemBrush,
                            CellCategory.Paging => PagingBrush,
                            CellCategory.User => UserBrush,
                            _ => FreeBrush
                        };
                    }
                });

                StatusText.Text = "Analyzing...";
                var result = await _engine.AnalyzeAsync(progress, analyzeMs, _cts.Token);
                StatusText.Text = $"Analyze complete. Used {result.UsedPercentage:F1}% (Boot {result.CategoryCounts[CellCategory.Boot]}, System {result.CategoryCounts[CellCategory.System]}, Paging {result.CategoryCounts[CellCategory.Paging]}, User {result.CategoryCounts[CellCategory.User]})";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Analyze canceled.";
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                ToggleControls(isRunning: false);
            }
        }

        private async void DefragButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) return;
            ToggleControls(isRunning: true);
            _cts = new CancellationTokenSource();
            try
            {
                var (defragMs, _) = ComputeDelays(SpeedSlider.Value);
                await foreach (var batch in _engine.PlanFullDefragAsync(defragMs, _cts.Token))
                {
                    // Highlight indices under operation with a red border
                    var indicesToHighlight = batch.Moves.Select(m => m.SourceIndex).Distinct().ToList();
                    HighlightIndices(indicesToHighlight);

                    // Paint sources as moving (except during final scan where we must not show Moving gray)
                    if (batch.Phase != PhaseType.PhaseFinalScan)
                    {
                        foreach (var move in batch.Moves)
                        {
                            if (move.SourceIndex >= 0 && move.SourceIndex < _rects.Count)
                                _rects[move.SourceIndex].Fill = MovingBrush;
                        }
                    }

                    // Animate delay
                    await Task.Delay(defragMs, CancellationToken.None);

                    // Apply commit (token ignored or short grace)
                    await _engine.ApplyMoveBatchAsync(batch, CancellationToken.None);

                    // Repaint changed indices: take snapshot
                    var snap = await _engine.GetSnapshotAsync();
                    _finalized.Clear();
                    // If final scan phase, core will set IsFinal=true after commit; respect that
                    for (int i = 0; i < snap.Length && i < _rects.Count; i++)
                    {
                        var b = snap[i];
                        // Always use original category fill; green is shown as a border only
                        _rects[i].Fill = BrushFor(b);

                        // Determine border based on final/highlight state
                        if (b.IsFinal)
                        {
                            _finalized.Add(i);
                            if (_highlighted.Contains(i))
                            {
                                // If also highlighted (should not usually happen), red takes precedence during move
                                _rects[i].Stroke = HighlightBrush;
                                _rects[i].StrokeThickness = _highlightStrokeThickness;
                            }
                            else
                            {
                                _rects[i].Stroke = FinalBorderBrush;
                                _rects[i].StrokeThickness = _highlightStrokeThickness;
                            }
                            // Ensure inner 1px border is added for finalized cells
                            AttachFinalInnerBorder(i);
                        }
                        else if (_highlighted.Contains(i))
                        {
                            _rects[i].Stroke = HighlightBrush;
                            _rects[i].StrokeThickness = _highlightStrokeThickness;
                            RemoveFinalInnerBorder(i);
                        }
                        else
                        {
                            var strokeBrush = _themeStrokeBrushCache ?? (TryFindResource("CellStrokeBrush") as Brush ?? Brushes.Black);
                            _rects[i].Stroke = strokeBrush;
                            _rects[i].StrokeThickness = _defaultStrokeThickness;
                            RemoveFinalInnerBorder(i);
                        }
                    }

                    // Clear highlights after commit/repaint
                    ClearHighlights();
                }

                StatusText.Text = "Defrag completed.";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Defrag canceled.";
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                ToggleControls(isRunning: false);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private async void GridSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return; // guard early events

            var text = (GridSizeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "20×40";
            _gridSize = text switch
            {
                "20×40" => new GridSize(20, 40),
                "25×50" => new GridSize(25, 50),
                "30×60" => new GridSize(30, 60),
                "40×90" => new GridSize(40, 90),
                _ => new GridSize(20, 40)
            };

            _cts?.Cancel();
            while (_cts != null) await Task.Delay(50);

            RebuildGrid(_gridSize.Rows, _gridSize.Columns);
            try
            {
                await _engine.InitializeAsync(_gridSize, CancellationToken.None);
                StatusText.Text = $"Grid set to {_gridSize.Rows}×{_gridSize.Columns}. Ready.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Init failed: {ex.Message}";
            }
        }

        private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded) return;
            UpdateDelayLabel();
        }

        // Theme handling
        private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
        {
            _darkMode = true;
            ApplyTheme(_darkMode);
        }

        private void ThemeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            _darkMode = false;
            ApplyTheme(_darkMode);
        }

        private void ApplyTheme(bool dark)
        {
            try
            {
                var app = Application.Current;
                if (app == null) return;
                var dictionaries = app.Resources.MergedDictionaries;

                // Remove existing theme dictionaries
                for (int i = dictionaries.Count - 1; i >= 0; i--)
                {
                    var src = dictionaries[i].Source?.ToString() ?? string.Empty;
                    if (src.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                        src.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase))
                    {
                        dictionaries.RemoveAt(i);
                    }
                }

                var newUri = new Uri(dark
                    ? "/DefragSimulator.UI;component/Themes/Theme.Dark.xaml"
                    : "/DefragSimulator.UI;component/Themes/Theme.Light.xaml" , UriKind.Relative);
                dictionaries.Add(new ResourceDictionary { Source = newUri });

                // Update grid strokes to match theme
                // Reload theme brushes and thicknesses
                var stroke = TryFindResource("CellStrokeBrush") as Brush ?? Brushes.Black;
                _themeStrokeBrushCache = stroke;
                if (TryFindResource("CellBorderThicknessDefault") is double defThick) _defaultStrokeThickness = defThick;
                if (TryFindResource("HighlightStrokeThickness") is double hiThick) _highlightStrokeThickness = hiThick;

                for (int i = 0; i < _rects.Count; i++)
                {
                    if (_highlighted.Contains(i))
                    {
                        // Keep moving highlight (red) as-is
                        _rects[i].Stroke = HighlightBrush;
                        _rects[i].StrokeThickness = _highlightStrokeThickness;
                        continue;
                    }

                    if (_finalized.Contains(i))
                    {
                        // Preserve completed border (green) with new thickness
                        _rects[i].Stroke = FinalBorderBrush;
                        _rects[i].StrokeThickness = _highlightStrokeThickness;
                        // Refresh inner border brush for finalized cell
                        if (_finalInnerBorders.TryGetValue(i, out var ad))
                        {
                            ad.UpdateBrush(InnerBorderBrush);
                            ad.UpdateThickness(_innerBorderThickness);
                        }
                    }
                    else
                    {
                        _rects[i].Stroke = stroke;
                        _rects[i].StrokeThickness = _defaultStrokeThickness;
                        // Ensure no inner border on non-final cells
                        RemoveFinalInnerBorder(i);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Theme switch failed: {ex.Message}";
            }
        }

        // Friendly status mapping
        private string MapStatusMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return msg;
            if (msg.StartsWith("[warn]", StringComparison.OrdinalIgnoreCase))
                return "Adjusting the plan to keep things consistent…";
            if (msg.StartsWith("[info]", StringComparison.OrdinalIgnoreCase))
                return "Making quick adjustments…";
            if (msg.StartsWith("Initialized grid", StringComparison.OrdinalIgnoreCase))
                return "Ready. Pick a size and click Analyze.";
            return msg;
        }

        private string FriendlyPhaseText(PhaseType phase, bool starting)
        {
            return phase switch
            {
                PhaseType.Phase1_FirstMove => starting ? "Making space to get started…" : "Space prepared.",
                PhaseType.Phase2_BootToFront => starting ? "Putting boot files at the front…" : "Boot files positioned.",
                PhaseType.Phase3_SystemPagingOrder => starting ? "Organizing system and paging files…" : "System and paging organized.",
                PhaseType.Phase4_UserOrdering => starting ? "Sorting your files for best order…" : "Files sorted.",
                PhaseType.PhaseFinalScan => starting ? "Double‑checking everything…" : "All set! Layout confirmed.",
                _ => starting ? "Working…" : "Step complete."
            };
        }

        private void ToggleControls(bool isRunning)
        {
            AnalyzeButton.IsEnabled = !isRunning;
            DefragButton.IsEnabled = !isRunning;
            GridSizeCombo.IsEnabled = !isRunning;
            SpeedSlider.IsEnabled = !isRunning;
            StopButton.IsEnabled = isRunning;
        }

        private void ClearVisualGrid()
        {
            // Reset all cell visuals to Free/empty and default borders, and clear state trackers
            RemoveAllFinalInnerBorders();

            var stroke = _themeStrokeBrushCache ?? (TryFindResource("CellStrokeBrush") as Brush ?? Brushes.Black);
            if (TryFindResource("CellBorderThicknessDefault") is double defThick) _defaultStrokeThickness = defThick;

            _highlighted.Clear();
            _finalized.Clear();

            for (int i = 0; i < _rects.Count; i++)
            {
                var r = _rects[i];
                r.Fill = FreeBrush;
                r.Stroke = stroke;
                r.StrokeThickness = _defaultStrokeThickness;
            }

            // Reset progress UI
            OverallProgress.Value = 0;
            PercentText.Text = "0%";
            StatusText.Text = "Preparing analysis…";
        }

        // Highlight helpers
        private void HighlightIndices(IEnumerable<int> indices)
        {
            if (_rects.Count == 0) return;
            var stroke = HighlightBrush;
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= _rects.Count) continue;
                _highlighted.Add(idx);
                _rects[idx].Stroke = stroke;
                _rects[idx].StrokeThickness = _highlightStrokeThickness;
            }
        }

        private void ClearHighlights()
        {
            if (_rects.Count == 0) return;
            var stroke = _themeStrokeBrushCache ?? (TryFindResource("CellStrokeBrush") as Brush ?? Brushes.Black);
            foreach (var idx in _highlighted)
            {
                if (idx < 0 || idx >= _rects.Count) continue;
                if (_finalized.Contains(idx))
                {
                    _rects[idx].Stroke = FinalBorderBrush;
                    _rects[idx].StrokeThickness = _highlightStrokeThickness;
                }
                else
                {
                    _rects[idx].Stroke = stroke;
                    _rects[idx].StrokeThickness = _defaultStrokeThickness;
                }
            }
            _highlighted.Clear();
        }

        // Final inner border (adorner) helpers
        private void AttachFinalInnerBorder(int index)
        {
            if (index < 0 || index >= _rects.Count) return;
            var element = _rects[index];
            if (_finalInnerBorders.TryGetValue(index, out var existing))
            {
                existing.UpdateBrush(InnerBorderBrush);
                existing.UpdateThickness(_innerBorderThickness);
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(element);
            if (layer == null) return; // no adorner layer available

            var ad = new InnerBorderAdorner(element, InnerBorderBrush, _innerBorderThickness);
            layer.Add(ad);
            _finalInnerBorders[index] = ad;
        }

        private void RemoveFinalInnerBorder(int index)
        {
            if (index < 0 || index >= _rects.Count) return;
            if (_finalInnerBorders.TryGetValue(index, out var ad))
            {
                var layer = AdornerLayer.GetAdornerLayer(_rects[index]);
                if (layer != null)
                {
                    try { layer.Remove(ad); } catch { /* ignore */ }
                }
                _finalInnerBorders.Remove(index);
            }
        }

        private void RemoveAllFinalInnerBorders()
        {
            foreach (var kv in _finalInnerBorders.ToList())
            {
                var idx = kv.Key;
                var ad = kv.Value;
                var element = idx >= 0 && idx < _rects.Count ? _rects[idx] : null;
                if (element != null)
                {
                    var layer = AdornerLayer.GetAdornerLayer(element);
                    if (layer != null)
                    {
                        try { layer.Remove(ad); } catch { /* ignore */ }
                    }
                }
            }
            _finalInnerBorders.Clear();
        }
    }
}