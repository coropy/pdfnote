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

/// <summary>ホーム画面の1アイコン (NotebookSummary + サムネイル)。</summary>
public sealed class NotebookIconItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public NotebookIconItem(NotebookSummary summary)
    {
        Summary = summary;
    }

    public NotebookSummary Summary { get; }

    public string Name => Summary.Name;

    public string UpdatedText => Summary.UpdatedText;

    /// <summary>1 ページ目サムネイル (null = 未生成 / 純白)。</summary>
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

    /// <summary>破損オーバーレイの表示状態。</summary>
    public Visibility BrokenVisibility => Summary.IsBroken
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

    /// <summary>白紙ノートの新規作成要求 (表示名付き、UI スレッド)。</summary>
    public event Action<string>? CreateRequested;

    /// <summary>名前変更要求 (UI スレッド)。</summary>
    public event Action<NotebookSummary>? RenameRequested;

    /// <summary>削除要求 (UI スレッド)。</summary>
    public event Action<NotebookSummary>? DeleteRequested;

    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<string, (DateTime UpdatedUtc, WriteableBitmap Bitmap)> _thumbnailCache = new();

    public HomeView()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>一覧を差し替える (最近更新順) し、1 ページ目サムネイルの生成を開始する。</summary>
    public void Refresh(IEnumerable<NotebookSummary> notebooks)
    {
        var items = notebooks.Select(n => new NotebookIconItem(n)).ToList();
        NotebookGrid.ItemsSource = items;

        foreach (var item in items.Where(i => !i.Summary.IsBroken))
        {
            StartThumbnailLoad(item);
        }
    }

    /// <summary>1 ページ目のサムネイルを生成してアイコンへ反映 (バックグラウンド読み込み + UI スレッド描画)。</summary>
    private async void StartThumbnailLoad(NotebookIconItem item)
    {
        var summary = item.Summary;
        var jsonPath = Path.Combine(summary.FolderPath, NotebookLibrary.NotebookFileName);

        NotebookThumbnail.PageSnapshot? snapshot;
        try
        {
            snapshot = await Task.Run(() => NotebookThumbnail.LoadPage(jsonPath));
        }
        catch
        {
            return; // 読み込み失敗時は白紙アイコンのまま。
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
                    return; // 白紙 + 方眼無し: 純白アイコンのまま。
                }

                _thumbnailCache[summary.FolderPath] = (summary.UpdatedUtc, bitmap);
                item.Thumbnail = bitmap;
            }
            catch
            {
                // 描画失敗時は白紙アイコンのまま (ホーム操作を妨げない)。
            }
        });
    }

    private void OnItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NotebookIconItem item)
        {
            OpenRequested?.Invoke(item.Summary);
        }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e) =>
        CreateRequested?.Invoke("無題ノート");

    private void OnMenuOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotebookIconItem item)
        {
            OpenRequested?.Invoke(item.Summary);
        }
    }

    private void OnMenuRenameClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotebookIconItem item &&
            !item.Summary.IsBroken)
        {
            RenameRequested?.Invoke(item.Summary);
        }
    }

    private void OnMenuDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotebookIconItem item)
        {
            DeleteRequested?.Invoke(item.Summary);
        }
    }
}

