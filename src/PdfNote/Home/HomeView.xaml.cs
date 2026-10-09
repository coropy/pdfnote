using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfNote.Storage;

namespace PdfNote.Home;

/// <summary>
/// ホーム画面 (ノートブック一覧)。UI のみを担当し、ファイル操作は NotebookLibrary に委譲する。
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

    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>一覧を差し替える (最近更新順)。</summary>
    public void Refresh(IEnumerable<NotebookSummary> notebooks)
    {
        NotebookList.ItemsSource = notebooks.ToList();
        SyncButtons();
    }

    private NotebookSummary? Selected =>
        NotebookList.SelectedItem as NotebookSummary;

    private void SyncButtons()
    {
        var selected = Selected;
        var usable = selected is { IsBroken: false };
        OpenButton.IsEnabled = usable;
        RenameButton.IsEnabled = usable;
        DeleteButton.IsEnabled = selected is not null;
    }

    private void OnItemClicked(object sender, ItemClickEventArgs e)
    {
        SyncButtons();
        if (e.ClickedItem is NotebookSummary summary)
        {
            OpenRequested?.Invoke(summary);
        }
    }

    private void OnCreateClick(object sender, RoutedEventArgs e) =>
        CreateRequested?.Invoke("無題ノート");

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsBroken: false } summary)
        {
            OpenRequested?.Invoke(summary);
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsBroken: false } summary)
        {
            RenameRequested?.Invoke(summary);
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } summary)
        {
            DeleteRequested?.Invoke(summary);
        }
    }
}
