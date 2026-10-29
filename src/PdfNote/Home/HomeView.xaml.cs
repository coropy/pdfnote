using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfNote.Storage;

namespace PdfNote.Home;

/// <summary>ホーム画面の1アイコン (ノートブック or フォルダ)。</summary>
public sealed class HomeItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    /// <summary>ノートブック項目用コンストラクタ。</summary>
    public HomeItem(NotebookSummary summary)
    {
        Summary = summary;
    }

    /// <summary>フォルダ項目用コンストラクタ (FolderName はディレクトリ名 = 表示名)。</summary>
    public HomeItem(string folderName)
    {
        FolderName = folderName;
    }

    public NotebookSummary? Summary { get; }

    public string? FolderName { get; }

    public bool IsFolder => FolderName is not null;

    public string Title => IsFolder ? FolderName! : Summary!.Name;

    public string SubText => IsFolder ? "フォルダ" : Summary!.UpdatedText;

    /// <summary>1 ページ目サムネイル (null = 未生成 / フォルダ)。</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public Visibility FolderVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoteVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>「移動」メニューはノートブックのみ表示。</summary>
    public Visibility MoveVisibility => IsFolder ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>破損オーバーレイの表示状態。</summary>
    public Visibility BrokenVisibility => Summary is { IsBroken: true }
        ? Visibility.Visible
        : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// ホーム画面 (ノートブック一覧)。UI のみを担当し、ファイル操作は NotebookLibrary に委譲する。
/// アイコンをクリックで開き、ホバー中の右クリックメニューから名前変更 / 削除などを行う。
/// </summary>
public sealed partial class HomeView : UserControl
{
    /// <summary>ノートブックを開く要求 (UI スレッド)。</summary>
    public event Action<NotebookSummary>? OpenRequested;

    /// <summary>白紙ノートの新規作成要求 (表示名・所属フォルダ付き、UI スレッド)。</summary>
    public event Action<string, string>? CreateRequested;

    /// <summary>ノートブック名変更要求 (UI スレッド)。</summary>
    public event Action<NotebookSummary>? RenameRequested;

    /// <summary>ノートブック削除要求 (UI スレッド)。</summary>
    public event Action<NotebookSummary>? DeleteRequested;

    /// <summary>ノートブック移動要求 (UI スレッド)。</summary>
    public event Action<NotebookSummary>? MoveRequested;

    /// <summary>フォルダ新規作成要求 (UI スレッド)。</summary>
    public event Action<string>? CreateFolderRequested;

    /// <summary>フォルダ名変更要求 (フォルダのディレクトリ名付き、UI スレッド)。</summary>
    public event Action<string>? RenameFolderRequested;

    /// <summary>フォルダ削除要求 (フォルダのディレクトリ名付き、UI スレッド)。</summary>
    public event Action<string>? DeleteFolderRequested;

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, (DateTime UpdatedUtc, WriteableBitmap Bitmap)> _thumbnailCache = new();
    private IReadOnlyList<NotebookSummary> _allNotebooks = Array.Empty<NotebookSummary>();
    private IReadOnlyList<string> _allFolders = Array.Empty<string>();
    private string _currentFolder = string.Empty;

    public HomeView()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// 一覧を差し替える (ノートブック + フォルダ)。現在のフォルダ階層で絞り込んで表示し、
    /// 表示中ノートのサムネイル生成を開始する。
    /// </summary>
    public void Refresh(IEnumerable<NotebookSummary> notebooks, IEnumerable<string> folders)
    {
        _allNotebooks = notebooks.ToList();
        _allFolders = folders.ToList();
        RebuildItems();
    }

    /// <summary>現在の階層でアイコン一覧を組み立てる (フォルダ → ノートの順)。</summary>
    private void RebuildItems()
    {
        var items = new List<HomeItem>();
        if (string.IsNullOrEmpty(_currentFolder))
        {
            // ルート: フォルダ + ルート直下のノート。
            items.AddRange(_allFolders.Select(f => new HomeItem(f)));
            items.AddRange(_allNotebooks
                .Where(n => string.IsNullOrEmpty(n.Folder))
                .Select(n => new HomeItem(n)));
        }
        else
        {
            // フォルダ内: そのフォルダのノートのみ。
            items.AddRange(_allNotebooks
                .Where(n => string.Equals(n.Folder, _currentFolder, StringComparison.Ordinal))
                .Select(n => new HomeItem(n)));
        }

        NotebookGrid.ItemsSource = items;
        SyncNav();

        foreach (var item in items.Where(i => i.Summary is { IsBroken: false }))
        {
            StartThumbnailLoad(item);
        }
    }

    /// <summary>パンくず (戻るボタン / 現在位置) を更新する。</summary>
    private void SyncNav()
    {
        if (string.IsNullOrEmpty(_currentFolder))
        {
            BackButton.Visibility = Visibility.Collapsed;
            PathText.Text = "すべてのノート";
        }
        else
        {
            BackButton.Visibility = Visibility.Visible;
            PathText.Text = $"すべてのノート / {_currentFolder}";
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _currentFolder = string.Empty;
        RebuildItems();
    }

    /// <summary>1 ページ目のサムネイルを生成してアイコンへ反映 (バックグラウンド読み込み + UI スレッド描画)。</summary>
    private async void StartThumbnailLoad(HomeItem item)
    {
        if (item.Summary is not { } summary || summary.IsBroken)
        {
            return;
        }

        var jsonPath = Path.Combine(summary.FolderPath, NotebookLibrary.NotebookFileName);

        NotebookThumbnail.PageSnapshot? snapshot;
        try
        {
            snapshot = await Task.Run(() => NotebookThumbnail.LoadPage(jsonPath));
        }
        catch
        {
            return; // 読み込み失敗時は白紙ページのまま。
        }

        if (snapshot is null)
        {
            return;
        }

        // 同一内容 (更新日時一致) のキャッシュがあれば再描画しない。
        if (_thumbnailCache.TryGetValue(summary.FolderPath, out var cached) &&
            cached.UpdatedUtc == summary.UpdatedUtc)
        {
            item.Thumbnail = cached.Bitmap;
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                var bitmap = NotebookThumbnail.Render(snapshot);
                if (bitmap is null)
                {
                    return;
                }

                _thumbnailCache[summary.FolderPath] = (summary.UpdatedUtc, bitmap);
                item.Thumbnail = bitmap;
            }
            catch
            {
                // 描画失敗時は白紙ページのまま (ホーム操作を妨げない)。
            }
        });
    }

    private void OnItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeItem item)
        {
            if (item.IsFolder)
            {
                // フォルダは「開く」= 中へ移動。
                _currentFolder = item.FolderName!;
                RebuildItems();
            }
            else
            {
                OpenRequested?.Invoke(item.Summary!);
            }
        }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e) =>
        CreateRequested?.Invoke("無題ノート", _currentFolder);

    private void OnCreateFolderClick(object sender, RoutedEventArgs e) =>
        CreateFolderRequested?.Invoke("新しいフォルダ");

    private void OnMenuOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HomeItem item)
        {
            if (item.IsFolder)
            {
                _currentFolder = item.FolderName!;
                RebuildItems();
            }
            else
            {
                OpenRequested?.Invoke(item.Summary!);
            }
        }
    }

    private void OnMenuMoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HomeItem { IsFolder: false } item)
        {
            MoveRequested?.Invoke(item.Summary!);
        }
    }

    private void OnMenuRenameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HomeItem item)
        {
            return;
        }

        if (item.IsFolder)
        {
            RenameFolderRequested?.Invoke(item.FolderName!);
        }
        else if (!item.Summary!.IsBroken)
        {
            RenameRequested?.Invoke(item.Summary);
        }
    }

    private void OnMenuDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HomeItem item)
        {
            return;
        }

        if (item.IsFolder)
        {
            DeleteFolderRequested?.Invoke(item.FolderName!);
        }
        else
        {
            DeleteRequested?.Invoke(item.Summary!);
        }
    }
}

