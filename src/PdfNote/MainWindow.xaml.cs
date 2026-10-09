using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private NotebookSession? _notebook;
    private NotebookAutoSave? _notebookAutoSave;
    private readonly Stopwatch _stopwatch = new();
    private AppSettings _settings = new();
    private readonly List<KeyboardAccelerator> _customAccelerators = new();
    private readonly HashSet<(int Key, int Modifiers)> _reservedKeys = new();
    private bool _dialogOpen;
    private bool _renderErrorShown;

    /// <summary>ノートブックモード (白紙ノート) 中か。null でなければノートブックモード。</summary>
    private bool IsNotebookMode => _notebook is not null;

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
        // 手書きの変更はモードに応じて PDF の .note / ノートブックのいずれかに振り分ける。
        _ink.ContentChanged += () =>
        {
            if (IsNotebookMode)
            {
                _notebookAutoSave?.OnContentChanged();
            }
            else
            {
                _autoSave.OnContentChanged();
            }
        };
        _autoSave.StatusChanged += status => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsNotebookMode)
            {
                SaveStatusText.Text = status;
            }
        });
        _ink.ToolChanged += SyncToolButtons;
        _ink.UndoRedoChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateToolbar);
        _ink.PageActionRequested += next => DispatcherQueue.TryEnqueue(() =>
        {
            if (next)
            {
                GoToNextPage();
            }
            else
            {
                GoToPreviousPage();
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
            return $"Zoom {ZoomPercent:F0}%  Page {PageDisplayText()}\n" +
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
                _notebookAutoSave?.SaveNowSync();
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

    private void OnPrevPageClick(object sender, RoutedEventArgs e) => GoToPreviousPage();

    private void OnNextPageClick(object sender, RoutedEventArgs e) => GoToNextPage();

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
        var hasPages = IsNotebookMode || _session.HasDocument;
        PageText.Text = hasPages ? PageDisplayText() : "-";
        ZoomText.Text = $"{ZoomPercent:F0}%";
        var current = IsNotebookMode ? _notebook!.CurrentPage : _session.CurrentPage;
        var count = IsNotebookMode ? _notebook!.PageCount : _session.PageCount;
        PrevButton.IsEnabled = hasPages && current > 0;
        NextButton.IsEnabled = hasPages && count > 0 && current < count - 1;
        UndoButton.IsEnabled = _ink.CanUndo;
        RedoButton.IsEnabled = _ink.CanRedo;
        PenToolButton.IsEnabled = hasPages;
        EraserToolButton.IsEnabled = hasPages;
        BackgroundToggleButton.IsEnabled = IsNotebookMode;
        PenToolButton.IsChecked = _ink.CurrentTool == InkTool.Pen;
        EraserToolButton.IsChecked = _ink.CurrentTool == InkTool.Eraser;
        if (IsNotebookMode)
        {
            BackgroundToggleButton.IsChecked = _notebook!.CurrentPageView.Background == PageBackgroundKind.Grid;
        }
    }

    private string PageDisplayText()
    {
        if (IsNotebookMode)
        {
            return _notebook!.PageCount > 0 ? $"{_notebook.CurrentPage + 1} / {_notebook.PageCount}" : "-";
        }

        return _session.HasDocument ? $"{_session.CurrentPage + 1} / {_session.PageCount}" : "-";
    }

    // ------------------------------------------------------------
    // ページ移動 (モード別に振り分ける)。
    // ------------------------------------------------------------
    private void GoToNextPage()
    {
        if (IsNotebookMode)
        {
            _notebook!.NextPage();
        }
        else
        {
            _session.NextPage();
        }
    }

    private void GoToPreviousPage()
    {
        if (IsNotebookMode)
        {
            _notebook!.PreviousPage();
        }
        else
        {
            _session.PreviousPage();
        }
    }

    private void GoToFirstPage()
    {
        if (IsNotebookMode)
        {
            _notebook!.FirstPage();
        }
        else
        {
            _session.FirstPage();
        }
    }

    private void GoToLastPage()
    {
        if (IsNotebookMode)
        {
            _notebook!.LastPage();
        }
        else
        {
            _session.LastPage();
        }
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

        // PDF を開く前にノートブックモードを終了する (未保存データは確定保存)。
        DetachNotebook();

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

    // ------------------------------------------------------------
    // ノートブック (白紙ノート) モード。段階2 最小実装:
    // 作成はツールバー「白紙ノート」のみ。保存は自動保存 (v2 最小形式) のみで、
    // 開き直し UI は段階4 (ホーム画面) で対応する。
    // ------------------------------------------------------------
    private void OnNewBlankNotebookClick(object sender, RoutedEventArgs e)
    {
        var session = NotebookSession.CreateNew(
            NotebookDefaults.A4WidthPt,
            NotebookDefaults.A4HeightPt,
            PageBackgroundKind.Plain);
        ActivateNotebook(session);
    }

    private void OnToggleBackgroundClick(object sender, RoutedEventArgs e)
    {
        if (!IsNotebookMode)
        {
            // ノートブックモード外では操作せず表示だけ同期する。
            BackgroundToggleButton.IsChecked = false;
            return;
        }

        var kind = BackgroundToggleButton.IsChecked == true
            ? PageBackgroundKind.Grid
            : PageBackgroundKind.Plain;
        _notebook!.SetCurrentPageBackground(kind);
        var page = _notebook.CurrentPageView;
        _canvas.SetPageBackground(page.Background, page.GridSpacingPt);
        _canvas.Invalidate();
    }

    /// <summary>ノートブックモードへ切り替える (呼び出しは UI スレッド)。</summary>
    private void ActivateNotebook(NotebookSession session)
    {
        // 直前のコンテキストを確定保存してから切替える。
        if (IsNotebookMode)
        {
            _notebookAutoSave?.SaveNowSync();
        }
        else if (_autoSave.IsDirty)
        {
            _autoSave.SaveNowSync();
        }

        DetachNotebookCore(save: false);

        _notebook = session;
        _ink.KeepPageTextures = true;
        _ink.AttachPages(session.GetInkPages());

        // PDF の再レンダリングを停止し、表示をノートブックに置き換える。
        _session.Close();
        _canvas.ClearFrame();

        // 自動保存先: %LOCALAPPDATA%\PdfNote\Notebooks\blank-notebook.nb.json (単一スロット)。
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PdfNote",
            "Notebooks");
        Directory.CreateDirectory(directory);
        var savePath = Path.Combine(directory, "blank-notebook.nb.json");

        var autoSave = new NotebookAutoSave(session, DispatcherQueue) { SavePath = savePath };
        autoSave.StatusChanged += status => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsNotebookMode && ReferenceEquals(_notebookAutoSave, autoSave))
            {
                SaveStatusText.Text = status;
            }
        });
        session.CurrentPageChanged += page => DispatcherQueue.TryEnqueue(() =>
        {
            if (IsNotebookMode && ReferenceEquals(_notebook, session))
            {
                OnNotebookPageChanged(page);
            }
        });
        session.ContentChanged += () => autoSave.OnContentChanged();
        _notebookAutoSave = autoSave;

        // 先頭ページを表示 (SetPage(0) が CurrentPageChanged を発火する)。
        session.SetPage(0);
        OnNotebookPageChanged(session.CurrentPage);
        _canvas.FitToViewport();
        UpdateToolbar();
    }

    /// <summary>ノートブックモードから抜ける (PDF を開く前など)。未保存データは確定保存する。</summary>
    private void DetachNotebook()
    {
        DetachNotebookCore(save: true);
    }

    private void DetachNotebookCore(bool save)
    {
        if (_notebook is null)
        {
            return;
        }

        if (save)
        {
            _notebookAutoSave?.SaveNowSync();
        }

        _notebookAutoSave?.Dispose();
        _notebookAutoSave = null;
        _notebook = null;
        _ink.KeepPageTextures = false;
        _canvas.SetPageBackground(PageBackgroundKind.Plain, NotebookDefaults.GridSpacingPt);
        _canvas.ClearFrame();
        UpdateToolbar();
    }

    /// <summary>ノートブックのページ切替後処理 (UI スレッド)。</summary>
    private void OnNotebookPageChanged(int page)
    {
        if (_notebook is null)
        {
            return;
        }

        var notebookPage = _notebook.CurrentPageView;
        _canvas.SetPageBackground(notebookPage.Background, notebookPage.GridSpacingPt);
        _canvas.SetPageGeometry(notebookPage.WidthPt, notebookPage.HeightPt);
        _ink.SetPage(page, notebookPage.WidthPt, notebookPage.HeightPt);
        UpdateToolbar();
    }

    private static string Describe(Exception ex) => $"{ex.Message}\n({ex.GetType().Name})";

    private void RegisterShortcuts()
    {
        Add(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = OpenPdfAsync());
        Add(VirtualKey.Z, VirtualKeyModifiers.Control, () => _ink.Undo());
        Add(VirtualKey.Y, VirtualKeyModifiers.Control, () => _ink.Redo());
        Add(VirtualKey.PageUp, VirtualKeyModifiers.None, GoToPreviousPage);
        Add(VirtualKey.PageDown, VirtualKeyModifiers.None, GoToNextPage);
        Add(VirtualKey.Home, VirtualKeyModifiers.None, GoToFirstPage);
        Add(VirtualKey.End, VirtualKeyModifiers.None, GoToLastPage);
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
            case AppCommands.PreviousPage: GoToPreviousPage(); break;
            case AppCommands.NextPage: GoToNextPage(); break;
            case AppCommands.FirstPage: GoToFirstPage(); break;
            case AppCommands.LastPage: GoToLastPage(); break;
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

