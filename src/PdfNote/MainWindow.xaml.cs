using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PdfNote.Core;
using PdfNote.Diagnostics;
using PdfNote.Ink;
using PdfNote.Session;
using PdfNote.View;
using Windows.System;

namespace PdfNote;

public sealed partial class MainWindow : Window
{
    private readonly PdfSession _session = new();
    private readonly PdfCanvas _canvas;
    private readonly PerfMonitor _perf = new();
    private readonly InkSession _ink;
    private readonly Stopwatch _stopwatch = new();
    private bool _dialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        Title = "PdfNote";

        _canvas = new PdfCanvas();
        ContentHost.Children.Insert(0, _canvas);

        _ink = new InkSession(_canvas, _perf);
        _ink.ToolChanged += SyncToolButtons;
        _ink.UndoRedoChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateToolbar);
        _ink.PageActionRequested += next => DispatcherQueue.TryEnqueue(() =>
        {
            if (next)
            {
                _session.NextPage();
            }
            else
            {
                _session.PreviousPage();
            }
        });

        _canvas.DisplayScaleChanged += OnCanvasDisplayScaleChanged;
        _canvas.ZoomSettled += OnCanvasZoomSettled;

        _session.FrameReady += OnFrameReady;
        _session.CurrentPageChanged += OnCurrentPageChanged;
        _session.RenderFailed += ex => DispatcherQueue.TryEnqueue(
            () => _ = ShowErrorAsync("レンダリングエラー", Describe(ex)));

        CompositionTarget.Rendering += OnRenderingTick;
        _perf.SetExtraProvider(() =>
        {
            var cache = _session.CacheStats;
            return $"Zoom {ZoomPercent:F0}%  Page {_session.CurrentPage + 1}/{_session.PageCount}\n" +
                   $"Cache {cache.Count} pages / {cache.Bytes / (1024.0 * 1024.0):F0} MB";
        });
        _perf.Start(DispatcherQueue, text => PerfText.Text = text);

        RegisterShortcuts();
        UpdateToolbar();

        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRenderingTick;
            _session.Dispose();
        };
    }

    private double ZoomPercent => _canvas.DisplayPxPerPt / RenderScale.DpiScale * 100.0;

    private void OnRenderingTick(object? sender, object args) => _perf.RecordFrameTick();

    private void OnFrameReady(RenderedFrame frame)
    {
        _perf.RecordRender(frame.RenderMs);
        DispatcherQueue.TryEnqueue(() =>
        {
            _stopwatch.Restart();
            _canvas.SetFrame(frame);
            _stopwatch.Stop();
            _perf.RecordUpload(_stopwatch.Elapsed.TotalMilliseconds);
            UpdateToolbar();
        });
    }

    private void OnCanvasDisplayScaleChanged(double pxPerPt)
    {
        // 表示倍率を要求解像度としてセッションに反映 (ズーム中は解像度が近い既存テクスチャを再利用)。
        _session.SetDesiredScale(pxPerPt);
        UpdateToolbar();
    }

    private void OnCanvasZoomSettled(double pxPerPt) => _session.OnZoomSettled();

    private void OnCurrentPageChanged(int page) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_session.HasDocument)
        {
            var (widthPt, heightPt) = _session.GetPageSizePt(page);
            _ink.SetPage(page, widthPt, heightPt);
        }
        UpdateToolbar();
    });

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = OpenPdfAsync();

    private void OnPrevPageClick(object sender, RoutedEventArgs e) => _session.PreviousPage();

    private void OnNextPageClick(object sender, RoutedEventArgs e) => _session.NextPage();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnFpsToggleClick(object sender, RoutedEventArgs e) =>
        SetPerfOverlay(FpsToggle.IsChecked == true);

    private void OnPenToolClick(object sender, RoutedEventArgs e) => _ink.SetTool(InkTool.Pen);

    private void OnEraserToolClick(object sender, RoutedEventArgs e) => _ink.SetTool(InkTool.Eraser);

    private void OnUndoClick(object sender, RoutedEventArgs e) => _ink.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => _ink.Redo();

    private void SyncToolButtons(InkTool tool)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            PenToolButton.IsChecked = tool == InkTool.Pen;
            EraserToolButton.IsChecked = tool == InkTool.Eraser;
        });
    }

    private void ZoomIn() => _canvas.SetDisplayScale(_canvas.DisplayPxPerPt * 1.25);

    private void ZoomOut() => _canvas.SetDisplayScale(_canvas.DisplayPxPerPt / 1.25);

    private void SetPerfOverlay(bool visible)
    {
        PerfText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FpsToggle.IsChecked = visible;
    }

    private void ToggleToolbar() =>
        ToolbarGrid.Visibility = ToolbarGrid.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void UpdateToolbar()
    {
        PageText.Text = _session.HasDocument
            ? $"{_session.CurrentPage + 1} / {_session.PageCount}"
            : "-";
        ZoomText.Text = $"{ZoomPercent:F0}%";
        PrevButton.IsEnabled = _session.HasDocument && _session.CurrentPage > 0;
        NextButton.IsEnabled = _session.HasDocument && _session.CurrentPage < _session.PageCount - 1;
        UndoButton.IsEnabled = _ink.CanUndo;
        RedoButton.IsEnabled = _ink.CanRedo;
        PenToolButton.IsEnabled = _session.HasDocument;
        EraserToolButton.IsEnabled = _session.HasDocument;
        PenToolButton.IsChecked = _ink.CurrentTool == InkTool.Pen;
        EraserToolButton.IsChecked = _ink.CurrentTool == InkTool.Eraser;
    }

    private async Task OpenPdfAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pdf");

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            // ページ寸法の一括取得を含むため UI スレッド外で開く。
            await Task.Run(() => _session.Open(file.Path));

            var (widthPt, heightPt) = _session.GetPageSizePt(_session.CurrentPage);
            _canvas.SetPageGeometry(widthPt, heightPt);
            _ink.ResetDocument();
            _ink.SetPage(_session.CurrentPage, widthPt, heightPt);
            _canvas.FitToViewport();
            // 初回レンダリングを保証 (同じ値でも強制評価)。
            _session.SetDesiredScale(_canvas.DisplayPxPerPt, force: true);
        }
        catch (PdfOpenException ex)
        {
            await ShowErrorAsync("PDFを開けませんでした", ex.Message);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("PDFを開けませんでした", Describe(ex));
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (_dialogOpen)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "閉じる",
                XamlRoot = Content.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static string Describe(Exception ex) => $"{ex.Message}\n({ex.GetType().Name})";

    private void RegisterShortcuts()
    {
        Add(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = OpenPdfAsync());
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, () => _ink.Undo());
        Add(VirtualKey.Y, VirtualKeyModifiers.Control, () => _ink.Redo());
        Add(VirtualKey.PageUp, VirtualKeyModifiers.None, () => _session.PreviousPage());
        Add(VirtualKey.PageDown, VirtualKeyModifiers.None, () => _session.NextPage());
        Add(VirtualKey.Home, VirtualKeyModifiers.None, () => _session.FirstPage());
        Add(VirtualKey.End, VirtualKeyModifiers.None, () => _session.LastPage());
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, () => _canvas.FitToViewport());
        Add(VirtualKey.F9, VirtualKeyModifiers.None, () => SetPerfOverlay(PerfText.Visibility != Visibility.Visible));
        Add(VirtualKey.F10, VirtualKeyModifiers.None, ToggleToolbar);
        Add(VirtualKey.Add, VirtualKeyModifiers.None, ZoomIn);
        Add(VirtualKey.Subtract, VirtualKeyModifiers.None, ZoomOut);
        Add((VirtualKey)187, VirtualKeyModifiers.None, ZoomIn);  // メインキーボード "+"
        Add((VirtualKey)189, VirtualKeyModifiers.None, ZoomOut); // メインキーボード "-"
    }

    private void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }
}

