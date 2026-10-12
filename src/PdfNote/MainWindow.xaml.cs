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
using PdfNote.Home;
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
    private readonly PdfResourceRenderService _pdfRenderService = new();
    private PdfObjectLayer? _objectLayer;
    private PdfObjectSelectionController? _selectionController;
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
            var layer = _objectLayer;
            var objInfo = layer is not null
                ? $"\nObj {layer.ObjectCountOnCurrentPage} / Tex {layer.TextureStats.Count}" +
                  $" / {layer.TextureStats.EstimatedBytes / (1024.0 * 1024.0):F0} MB(推定)" +
                  $" / 待 {layer.PendingCount} / 破棄 {layer.StaleDiscardedCount}" +
                  (layer.DisplayingStale ? " / 暫定画質" : string.Empty)
                : string.Empty;
            return $"Zoom {ZoomPercent:F0}%  Page {PageDisplayText()}\n" +
                   $"Cache {cache.Count} pages / {cache.Bytes / (1024.0 * 1024.0):F0} MB" + noteInfo + objInfo;
        });
        _perf.Start(DispatcherQueue, text => PerfText.Text = text);

        RegisterShortcuts();
        UpdateToolbar();

        // ホーム画面 (ノートブック一覧)。起動時はホームを表示する。
        HomeViewElement.CreateRequested += name => _ = CreateAndOpenNotebookAsync(name, PageBackgroundKind.Plain);
        HomeViewElement.OpenRequested += summary => _ = OpenNotebookAsync(summary);
        HomeViewElement.RenameRequested += summary => _ = RenameNotebookAsync(summary);
        HomeViewElement.DeleteRequested += summary => _ = DeleteNotebookAsync(summary);
        ShowHome();

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
            _pdfRenderService.Dispose();
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

    private void OnCanvasZoomSettled(double pxPerPt)
    {
        _session.OnZoomSettled();
        _objectLayer?.OnZoomSettled(); // ズーム確定後に必要な場合だけ再レンダリング (±15%)。
        if (_selectionController is not null)
        {
            _selectionController.DisplayPxPerPt = pxPerPt;
        }
    }

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
        var selected = _selectionController?.SelectedObjectId is not null;
        PrevButton.IsEnabled = hasPages && current > 0;
        NextButton.IsEnabled = hasPages && count > 0 && current < count - 1;
        UndoButton.IsEnabled = _ink.CanUndo;
        RedoButton.IsEnabled = _ink.CanRedo;
        PenToolButton.IsEnabled = hasPages;
        EraserToolButton.IsEnabled = hasPages;
        BackgroundToggleButton.IsEnabled = IsNotebookMode;
        SelectToolButton.IsEnabled = IsNotebookMode;
        AddPdfButton.IsEnabled = IsNotebookMode;
        RotateSelectedButton.IsEnabled = IsNotebookMode && selected;
        BringFrontButton.IsEnabled = IsNotebookMode && selected;
        SendBackButton.IsEnabled = IsNotebookMode && selected;
        PenToolButton.IsChecked = _ink.CurrentTool == InkTool.Pen;
        EraserToolButton.IsChecked = _ink.CurrentTool == InkTool.Eraser;
        if (IsNotebookMode)
        {
            BackgroundToggleButton.IsChecked = _notebook!.CurrentPageView.Background == PageBackgroundKind.Grid;
            SelectToolButton.IsChecked = _canvas.Tool == PdfCanvas.PdfCanvasTool.Select;
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
        // ホーム表示中はエディタへ切り替えてからピッカーを出す。
        HideHome();

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
    // ホーム画面 (ノートブック一覧) とノートブックモード。
    // 保存は NotebookLibrary (<名前>-<短ID>/notebook.json + assets/) で行う。
    // ------------------------------------------------------------
    private bool IsHomeVisible => HomeViewElement.Visibility == Visibility.Visible;

    private void ShowHome()
    {
        HomeViewElement.Visibility = Visibility.Visible;
        ToolbarGrid.Visibility = Visibility.Collapsed;
        RefreshHome();
    }

    private void HideHome()
    {
        HomeViewElement.Visibility = Visibility.Collapsed;
        ToolbarGrid.Visibility = Visibility.Visible;
    }

    private void RefreshHome()
    {
        _ = Task.Run(() => NotebookLibrary.List(NotebookLibrary.DefaultRoot))
            .ContinueWith(
                task => DispatcherQueue.TryEnqueue(() =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        HomeViewElement.Refresh(task.Result);
                    }
                }),
                TaskScheduler.Default);
    }

    private void OnHomeClick(object sender, RoutedEventArgs e)
    {
        if (IsHomeVisible)
        {
            HideHome();
        }
        else
        {
            ShowHome();
        }
    }

    /// <summary>現在のコンテキストの未保存データを確定させる (ノート切替 / 削除 / 名前変更の前)。</summary>
    private async Task QuiesceCurrentContextAsync()
    {
        if (IsNotebookMode && _notebookAutoSave is not null)
        {
            await _notebookAutoSave.FlushPendingAsync();
        }
        else if (_autoSave.IsDirty)
        {
            _autoSave.SaveNowSync();
        }
    }

    private async void OnNewBlankNotebookClick(object sender, RoutedEventArgs e) =>
        await CreateAndOpenNotebookAsync("無題ノート", PageBackgroundKind.Plain);

    /// <summary>白紙ノートを作成して開く。保存先は NotebookLibrary のフォルダ。</summary>
    private async Task CreateAndOpenNotebookAsync(string displayName, PageBackgroundKind background)
    {
        if (_dialogOpen)
        {
            return;
        }

        var session = NotebookSession.CreateNew(
            NotebookDefaults.A4WidthPt,
            NotebookDefaults.A4HeightPt,
            background,
            pushOperation: _ink.PushOperation);

        string folder;
        try
        {
            folder = await Task.Run(() =>
                NotebookLibrary.Create(NotebookLibrary.DefaultRoot, displayName, session.Notebook));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("ノートブックを作成できませんでした", Describe(ex));
            return;
        }

        HideHome();
        ActivateNotebook(session, Path.Combine(folder, NotebookLibrary.NotebookFileName));
    }

    /// <summary>既存ノートブックを開き、ページ・背景・ストロークを復元する。</summary>
    private async Task OpenNotebookAsync(NotebookSummary summary)
    {
        if (_dialogOpen)
        {
            return;
        }

        if (summary.IsBroken)
        {
            await ShowErrorAsync(
                "ノートブックを開けませんでした",
                $"このノートブックのデータは壊れています:\n{summary.FolderPath}");
            return;
        }

        try
        {
            var jsonPath = Path.Combine(summary.FolderPath, NotebookLibrary.NotebookFileName);
            var notebook = await Task.Run(() =>
                NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(jsonPath)));

            await QuiesceCurrentContextAsync();
            HideHome();
            ActivateNotebook(new NotebookSession(notebook, _ink.PushOperation), jsonPath);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("ノートブックを開けませんでした", Describe(ex));
        }
    }

    /// <summary>名前変更 (確認ダイアログ → json 更新 → フォルダ名変更)。</summary>
    private async Task RenameNotebookAsync(NotebookSummary summary)
    {
        if (_dialogOpen)
        {
            return;
        }

        _dialogOpen = true;
        try
        {
            var input = new TextBox { Text = summary.Name };
            var dialog = new ContentDialog
            {
                Title = "ノートブック名を変更",
                Content = input,
                PrimaryButtonText = "変更",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var isCurrentlyOpen = IsNotebookMode && _notebook!.Notebook.Id == summary.Id;
            if (isCurrentlyOpen)
            {
                await _notebookAutoSave!.FlushPendingAsync();
            }

            string finalFolder;
            try
            {
                finalFolder = await Task.Run(() => NotebookLibrary.Rename(summary.FolderPath, input.Text));
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("名前を変更できませんでした", Describe(ex));
                return;
            }

            if (isCurrentlyOpen)
            {
                _notebookAutoSave!.SavePath = Path.Combine(finalFolder, NotebookLibrary.NotebookFileName);
            }
        }
        finally
        {
            _dialogOpen = false;
        }

        RefreshHome();
    }

    /// <summary>削除 (確認ダイアログでパス・ページ数・ストローク数を明示)。</summary>
    private async Task DeleteNotebookAsync(NotebookSummary summary)
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
                Title = "ノートブックを削除しますか？",
                Content = $"次のフォルダとその内容を削除します。この操作は取り消せません。\n\n" +
                          $"{summary.FolderPath}\n\n" +
                          $"ページ数: {summary.PageCount}\nストローク数: {summary.StrokeCount}",
                PrimaryButtonText = "削除",
                CloseButtonText = "キャンセル",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            // 対象が開いていれば保存を完了させてからモードを破棄 (削除後の再保存を防ぐ)。
            if (IsNotebookMode && _notebook!.Notebook.Id == summary.Id)
            {
                await _notebookAutoSave!.FlushPendingAsync();
                DetachNotebookCore(save: false);
            }
            else
            {
                await QuiesceCurrentContextAsync();
            }

            try
            {
                await Task.Run(() => NotebookLibrary.Delete(summary.FolderPath));
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("削除できませんでした", Describe(ex));
                return;
            }
        }
        finally
        {
            _dialogOpen = false;
        }

        RefreshHome();
    }

    private void OnSelectToolClick(object sender, RoutedEventArgs e)
    {
        if (!IsNotebookMode)
        {
            SelectToolButton.IsChecked = false;
            return;
        }

        _canvas.Tool = SelectToolButton.IsChecked == true
            ? PdfCanvas.PdfCanvasTool.Select
            : PdfCanvas.PdfCanvasTool.Pen;
    }

    private void OnRotateSelectedClick(object sender, RoutedEventArgs e) =>
        _selectionController?.RotateSelectedClockwise();

    private void OnBringFrontClick(object sender, RoutedEventArgs e) =>
        _selectionController?.BringToFront();

    private void OnSendBackClick(object sender, RoutedEventArgs e) =>
        _selectionController?.SendToBack();

    /// <summary>PDF 配置の後始末: 未使用リソースと資産コピーを取り除く。</summary>
    private void RemoveResourceFromNotebook(Resource resource)
    {
        if (_notebook is null)
        {
            return;
        }

        var used = _notebook.Notebook.Pages.Any(p => p.Objects.OfType<PdfObject>().Any(o => o.ResourceId == resource.Id));
        if (used)
        {
            return;
        }

        _notebook.Notebook.Resources.RemoveAll(r => r.Id == resource.Id);
        try
        {
            var folder = Path.GetDirectoryName(_notebookAutoSave!.SavePath)!;
            var assetPath = NotebookLibrary.FindResourceFile(folder, resource);
            if (assetPath is not null)
            {
                File.Delete(assetPath);
            }
        }
        catch (Exception ex)
        {
            App.LogError("未使用 PDF 資産の削除に失敗しました (放置しても害はありません)。", ex);
        }
    }

    private static string DescribeOutcome(NotebookSession.PdfObjectAddStatus status) => status switch
    {
        NotebookSession.PdfObjectAddStatus.PageLimitReached => "1ページのオブジェクト数が上限に達しています。",
        NotebookSession.PdfObjectAddStatus.UnknownResource => "PDFリソースが登録されていません。",
        NotebookSession.PdfObjectAddStatus.PageIndexOutOfRange => "ページ番号が不正です。",
        NotebookSession.PdfObjectAddStatus.InvalidTransform => "配置サイズが不正です。",
        _ => status.ToString(),
    };

    /// <summary>配置する PDF ページを選ぶダイアログ。キャンセルは null。</summary>
    private async System.Threading.Tasks.Task<int?> ShowPdfPagePickerAsync(int pageCount)
    {
        var comboBox = new ComboBox { MinWidth = 200, SelectedIndex = 0 };
        for (var i = 1; i <= pageCount; i++)
        {
            comboBox.Items.Add($"{i} / {pageCount}");
        }

        var dialog = new ContentDialog
        {
            Title = "配置するPDFページを選択",
            Content = comboBox,
            PrimaryButtonText = "配置",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return comboBox.SelectedIndex;
    }

    private async System.Threading.Tasks.Task<bool> ShowConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnAddPdfClick(object sender, RoutedEventArgs e) => await AddPdfObjectToCurrentPageAsync();

    /// <summary>
    /// PDF ファイルを選んで現在のページへ配置 (最小限の UI)。
    /// 取り込み → ページ選択 → 配置。取り込み後のキャンセルではコピー資産を削除して後始末する。
    /// </summary>
    private async System.Threading.Tasks.Task AddPdfObjectToCurrentPageAsync()
    {
        if (_dialogOpen || !IsNotebookMode || _selectionController is null || _objectLayer is null)
        {
            return;
        }

        _dialogOpen = true;
        Resource? resource = null;
        try
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

            var notebookFolder = Path.GetDirectoryName(_notebookAutoSave!.SavePath)!;
            resource = await System.Threading.Tasks.Task.Run(
                () => NotebookLibrary.AddResource(notebookFolder, file.Path));

            // ページ数取得 (バックグラウンド。UI スレッドはブロックしない)。
            await _pdfRenderService.OpenAsync(resource.Id, file.Path);
            var pageCount = _pdfRenderService.TryGetPageCount(resource.Id) ?? 0;
            if (pageCount <= 0)
            {
                await ShowErrorAsync("PDFを配置できませんでした", "ページが含まれていないPDFです。");
                return;
            }

            var pdfPageIndex = await ShowPdfPagePickerAsync(pageCount);
            if (pdfPageIndex is null)
            {
                return; // キャンセル (finally で資産を後始末)。
            }

            // 初期寸法: PDF ページを現在ページの 80% に収め、中央へ配置 (はみ出し防止)。
            var dims = await _pdfRenderService.GetPageSizePtAsync(resource.Id, pdfPageIndex.Value);
            var notebookPage = _notebook!.CurrentPageView;
            var scale = Math.Min(
                notebookPage.WidthPt * 0.8 / dims.WidthPt,
                notebookPage.HeightPt * 0.8 / dims.HeightPt);
            var width = (float)(dims.WidthPt * scale);
            var height = (float)(dims.HeightPt * scale);
            var x = Math.Max(8f, (float)((notebookPage.WidthPt - width) / 2));
            var y = Math.Max(8f, (float)((notebookPage.HeightPt - height) / 2));

            // ノートブックへリソース登録 → オブジェクト配置。
            if (_notebook.Notebook.Resources.All(r => r.Id != resource.Id))
            {
                _notebook.Notebook.Resources.Add(resource);
            }

            var outcome = _notebook.AddPdfObject(
                _notebook.CurrentPage, resource.Id, pdfPageIndex.Value,
                new ObjectTransform(x, y, width, height, 0));
            if (outcome.Status == NotebookSession.PdfObjectAddStatus.PageLimitReached)
            {
                var proceed = await ShowConfirmAsync(
                    "PDFを配置しますか？",
                    $"1ページのオブジェクト数が上限 ({PageObjectLimits.MaxObjectsPerPage}) に達しています。\n" +
                    "性能に影響する可能性がありますが、続行しますか？",
                    "配置する");
                if (!proceed)
                {
                    RemoveResourceFromNotebook(resource);
                    return;
                }

                outcome = _notebook.AddPdfObject(
                    _notebook.CurrentPage, resource.Id, pdfPageIndex.Value,
                    new ObjectTransform(x, y, width, height, 0), ignorePageLimit: true);
            }

            if (outcome.Status != NotebookSession.PdfObjectAddStatus.Added || outcome.Object is null)
            {
                RemoveResourceFromNotebook(resource);
                await ShowErrorAsync("PDFを配置できませんでした", DescribeOutcome(outcome.Status));
                return;
            }

            // 追加結果をすぐに選択して描画へ反映 (ContentChanged 経由で層も更新される)。
            _selectionController.ClearSelection();
            _objectLayer.SelectedObjectId = outcome.Object.Id;
            _canvas.Invalidate();
        }
        catch (Exception ex)
        {
            if (resource is not null)
            {
                RemoveResourceFromNotebook(resource);
            }

            App.LogError("PDF の配置に失敗しました。", ex);
            await ShowErrorAsync("PDFを配置できませんでした", Describe(ex));
        }
        finally
        {
            _dialogOpen = false;
        }
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
    /// <param name="savePath">自動保存先 (notebook.json の完全パス)。Id と内容はノートブック側と一致していること。</param>
    private void ActivateNotebook(NotebookSession session, string savePath)
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

        // PDF オブジェクト合成層 (ステップ4)。ノートフォルダから assets を解決する。
        _objectLayer?.Detach();
        var notebookFolder = Path.GetDirectoryName(savePath)!;
        _objectLayer = new PdfObjectLayer(_pdfRenderService, DispatcherQueue, () => _canvas.Invalidate());
        _objectLayer.Attach(session, notebookFolder);
        _canvas.ObjectLayer = _objectLayer;

        // 選択ツール (ステップ5)。既定はペンツール。
        _selectionController = new PdfObjectSelectionController(session, _objectLayer, () => _canvas.Invalidate())
        {
            DisplayPxPerPt = _canvas.DisplayPxPerPt,
        };
        _canvas.SelectionController = _selectionController;
        _canvas.Tool = PdfCanvas.PdfCanvasTool.Pen;

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
        _selectionController = null;
        _canvas.SelectionController = null;
        _canvas.Tool = PdfCanvas.PdfCanvasTool.Pen;
        _objectLayer?.Detach();
        _objectLayer = null;
        _canvas.ObjectLayer = null;
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
        _selectionController?.ClearSelection(); // ページ切替で選択解除。
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

