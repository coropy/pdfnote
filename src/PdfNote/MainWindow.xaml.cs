using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PdfNote.Core;
using PdfNote.Diagnostics;
using PdfNote.Ink;
using PdfNote.Input;
using PdfNote.Session;
using PdfNote.Settings;
using PdfNote.Storage;
using PdfNote.View;
using Windows.System;

namespace PdfNote;

public sealed partial class MainWindow : Window
{
    private readonly PdfSession _session = new();
    private readonly PdfCanvas _canvas;
    private readonly PerfMonitor _perf = new();
    private readonly InkSession _ink;
    private readonly NoteAutoSave _autoSave;
    private readonly Stopwatch _stopwatch = new();
    private AppSettings _settings = new();
    private readonly List<KeyboardAccelerator> _customAccelerators = new();
    private readonly HashSet<(int Key, int Modifiers)> _reservedKeys = new();
    private bool _dialogOpen;
    private bool _renderErrorShown;

    public MainWindow()
    {
        InitializeComponent();
        Title = "PdfNote";

        // 設定をローカルから読み込む (初回は既定値。破損時は既定値 + 警告)。
        _settings = AppSettingsStore.Load(out var settingsWarning);

        _canvas = new PdfCanvas();
        ContentHost.Children.Insert(0, _canvas);

        _ink = new InkSession(_canvas, _perf);

        // 設定境界を通してペンボタン割当を適用 (Ink Model / 入力処理には直接触れない)。
        _ink.ApplyButtonAssignment(InkButton.Barrel, _settings.BarrelAction);
        _ink.ApplyButtonAssignment(InkButton.EraserEnd, _settings.EraserEndAction);
        if (settingsWarning is not null)
        {
            var warning = settingsWarning;
            DispatcherQueue.TryEnqueue(() => _ = ShowSettingsWarningAsync(warning));
        }
        _autoSave = new NoteAutoSave(_ink, DispatcherQueue);
        _ink.ContentChanged += () => _autoSave.OnContentChanged();
        _autoSave.StatusChanged += status => DispatcherQueue.TryEnqueue(
            () => SaveStatusText.Text = status);
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
        _session.RenderFailed += ex => DispatcherQueue.TryEnqueue(() =>
        {
            App.LogError("レンダリングエラー", ex);
            if (_renderErrorShown)
            {
                return;
            }

            _renderErrorShown = true;
            _ = ShowErrorAsync("レンダリングエラー", Describe(ex));
        });

        CompositionTarget.Rendering += OnRenderingTick;
        _perf.SetExtraProvider(() =>
        {
            var cache = _session.CacheStats;
            var noteInfo = _autoSave.LastNoteBytes >= 0
                ? $"\nNote {_autoSave.LastNoteBytes / 1024.0:F0} KB (save {_autoSave.LastSaveMs:F0} ms)"
                : string.Empty;
            return $"Zoom {ZoomPercent:F0}%  Page {_session.CurrentPage + 1}/{_session.PageCount}\n" +
                   $"Cache {cache.Count} pages / {cache.Bytes / (1024.0 * 1024.0):F0} MB" + noteInfo;
        });
        _perf.Start(DispatcherQueue, text => PerfText.Text = text);

        RegisterShortcuts();
        UpdateToolbar();

        Closed += (_, _) =>
        {
            // 未保存データを確実に保存してから終了する (同期、例外は握りつぶしてログ)。
            try
            {
                _autoSave.SaveNowSync();
            }
            catch (Exception closeEx)
            {
                App.LogError("終了時保存に失敗しました。", closeEx);
            }

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
            try
            {
                _stopwatch.Restart();
                _canvas.SetFrame(frame);
                _stopwatch.Stop();
                _perf.RecordUpload(_stopwatch.Elapsed.TotalMilliseconds);
                UpdateToolbar();
            }
            catch (Exception ex)
            {
                // GPU アップロード失敗時もアプリを継続させる。
                App.LogError("SetFrame に失敗しました。", ex);
                _ = ShowErrorAsync("ページの表示に失敗しました", Describe(ex));
            }
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
        try
        {
            if (_session.HasDocument)
            {
                var (widthPt, heightPt) = _session.GetPageSizePt(page);
                _ink.SetPage(page, widthPt, heightPt);
            }
        }
        catch (Exception ex)
        {
            // ページ寸法の取得に失敗してもアプリは継続 (レンダリング時にエラー表示)。
            App.LogError($"ページ {page + 1} の寸法取得に失敗しました。", ex);
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

            // ノート (<PDF名>.note) をバックグラウンドで読み込む。
            var outcome = await Task.Run(() => _autoSave.PrepareForOpen(file.Path, _session.PageCount));

            var (widthPt, heightPt) = _session.GetPageSizePt(_session.CurrentPage);
            _canvas.SetPageGeometry(widthPt, heightPt);
            _renderErrorShown = false;
            _ink.ResetDocument();
            if (outcome.Document.Pages.Count > 0)
            {
                _ink.LoadDocument(outcome.Document);
            }
            _ink.SetPage(_session.CurrentPage, widthPt, heightPt);
            _canvas.FitToViewport();
            // 初回レンダリングを保証 (同じ値でも強制評価)。
            _session.SetDesiredScale(_canvas.DisplayPxPerPt, force: true);

            if (outcome.Warning is not null)
            {
                await ShowErrorAsync("ノートの読み込み", outcome.Warning);
            }
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

        RegisterCustomShortcuts();
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
        _reservedKeys.Add(((int)key, (int)modifiers));
    }

    /// <summary>設定に基づいてカスタムショートカットを (再) 登録する。既存のカスタム分は一度解除。</summary>
    private void RegisterCustomShortcuts()
    {
        foreach (var accelerator in _customAccelerators)
        {
            _ = RootGrid.KeyboardAccelerators.Remove(accelerator);
        }

        _customAccelerators.Clear();

        foreach (var shortcut in _settings.CustomShortcuts)
        {
            if (!AppCommands.Exists(shortcut.Command) || shortcut.Key == 0)
            {
                continue;
            }

            var key = (VirtualKey)shortcut.Key;
            var modifiers = (VirtualKeyModifiers)shortcut.Modifiers;
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                RunCommand(shortcut.Command);
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
            _customAccelerators.Add(accelerator);
        }
    }

    /// <summary>カスタムショートカット / コマンドID の実行。</summary>
    private void RunCommand(string commandId)
    {
        switch (commandId)
        {
            case AppCommands.OpenPdf: _ = OpenPdfAsync(); break;
            case AppCommands.Undo: _ink.Undo(); break;
            case AppCommands.Redo: _ink.Redo(); break;
            case AppCommands.PreviousPage: _session.PreviousPage(); break;
            case AppCommands.NextPage: _session.NextPage(); break;
            case AppCommands.FirstPage: _session.FirstPage(); break;
            case AppCommands.LastPage: _session.LastPage(); break;
            case AppCommands.FitToViewport: _canvas.FitToViewport(); break;
            case AppCommands.ZoomIn: ZoomIn(); break;
            case AppCommands.ZoomOut: ZoomOut(); break;
            case AppCommands.PenTool: _ink.SetTool(InkTool.Pen); break;
            case AppCommands.EraserTool: _ink.SetTool(InkTool.Eraser); break;
            case AppCommands.TogglePerfOverlay: SetPerfOverlay(PerfText.Visibility != Visibility.Visible); break;
            case AppCommands.ToggleToolbar: ToggleToolbar(); break;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _ = ShowSettingsAsync();

    private async Task ShowSettingsAsync()
    {
        if (_dialogOpen || Content.XamlRoot is null)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new SettingsDialog(_settings, _reservedKeys) { XamlRoot = Content.XamlRoot };
            await dialog.ShowAsync();

            if (dialog.Result is null)
            {
                return; // キャンセル: 現在の設定を維持。
            }

            try
            {
                AppSettingsStore.Save(dialog.Result);
            }
            catch (Exception saveEx)
            {
                // 保存失敗時は現在の動作設定を維持し、ユーザーへ通知。
                App.LogError("設定の保存に失敗しました。", saveEx);
                await ShowErrorAsync("設定の保存に失敗しました", Describe(saveEx));
                return;
            }

            // 保存成功後に適用 (適用中のペン入力整合性は InkToolController 側で保証)。
            _settings = dialog.Result;
            _ink.ApplyButtonAssignment(InkButton.Barrel, _settings.BarrelAction);
            _ink.ApplyButtonAssignment(InkButton.EraserEnd, _settings.EraserEndAction);
            RegisterCustomShortcuts();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task ShowSettingsWarningAsync(string warning) =>
        await ShowErrorAsync("設定の読み込み", warning);
}

