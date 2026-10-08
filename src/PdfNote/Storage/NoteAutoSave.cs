using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using PdfNote.Ink;

namespace PdfNote.Storage;

/// <summary>PDF と .note の関連付け状態。</summary>
public enum NoteAssociationState
{
    /// <summary>.note が存在しない (新規ノート)。</summary>
    None,

    /// <summary>PDF は一致している。</summary>
    Matched,

    /// <summary>PDF が差し替えられた可能性がある (サイズ/ページ数不一致)。</summary>
    PdfMismatched,
}

/// <summary>PDF を開いたときのノート読み込み結果。</summary>
public sealed class NoteLoadOutcome
{
    public InkDocument Document { get; init; } = new();
    public NoteAssociationState Association { get; init; }
    public string? Warning { get; init; }
    public bool LoadedFromBackup { get; init; }
}

/// <summary>
/// 自動保存サービス。
/// Dirty → デバウンス → スナップショット (UI スレッド、点列コピーのみ) →
/// バックグラウンドで JSON 化 + 原子保存。
/// 同時保存は 1 件に制限し、保存中の変更は保存完了後に再保存する (latest-wins)。
/// </summary>
public sealed class NoteAutoSave : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ResaveDelay = TimeSpan.FromMilliseconds(200);

    private readonly InkSession _ink;
    private readonly DispatcherQueueTimer _timer;
    private readonly object _gate = new();

    private string _notePath = string.Empty;
    private PdfInfoDto? _pdfInfo;
    private volatile bool _dirty;
    private volatile bool _resaveQueued;
    private int _saving;
    private bool _hasContext;

    public double LastSaveMs { get; private set; } = -1;
    public long LastNoteBytes { get; private set; } = -1;
    public bool IsDirty => _dirty;

    /// <summary>保存状態の変化 ("未保存" / "保存中…" / "保存済み" / "保存失敗")。</summary>
    public event Action<string>? StatusChanged;

    public NoteAutoSave(InkSession ink, DispatcherQueue dispatcher)
    {
        _ink = ink;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = DebounceDelay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => BeginSave();
    }

    public string NotePath
    {
        get { lock (_gate) { return _notePath; } }
    }

    /// <summary>
    /// PDF を開いたときに呼び出す (バックグラウンドスレッド可)。
    /// &lt;PDF名&gt;.note を読み込み、結果を返す。
    /// </summary>
    public NoteLoadOutcome PrepareForOpen(string pdfPath, int pdfPageCount)
    {
        var info = new FileInfo(pdfPath);
        lock (_gate)
        {
            _notePath = Path.ChangeExtension(pdfPath, ".note");
            _pdfInfo = new PdfInfoDto
            {
                FileName = Path.GetFileName(pdfPath),
                FileSize = info.Exists ? info.Length : 0,
                LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc.ToString("o") : string.Empty,
                PageCount = pdfPageCount,
            };
            _hasContext = true;
            _dirty = false;
        }

        _timer.Stop();
        return LoadNote(pdfPageCount);
    }

    /// <summary>手書き内容が変化した (InkSession から)。デバウンスを再起動する。</summary>
    public void OnContentChanged()
    {
        if (!_hasContext)
        {
            return;
        }

        _dirty = true;
        StatusChanged?.Invoke("未保存");
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>即時保存 (アプリ終了時)。呼び出しは UI スレッドから。</summary>
    public void SaveNowSync()
    {
        if (!_hasContext || !_dirty)
        {
            return;
        }

        _timer.Stop();
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
        {
            _resaveQueued = true;
            return;
        }

        try
        {
            SaveCore();
        }
        finally
        {
            _saving = 0;
            if (_dirty || _resaveQueued)
            {
                _resaveQueued = false;
                StartTimer(ResaveDelay);
            }
        }
    }

    private void BeginSave()
    {
        if (!_hasContext || !_dirty)
        {
            return;
        }

        // 保存中なら完了後にもう一度保存する (latest-wins)。
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
        {
            _resaveQueued = true;
            return;
        }

        _ = SaveAsyncCore();
    }

    private async Task SaveAsyncCore()
    {
        try
        {
            StatusChanged?.Invoke("保存中…");
            await SaveCoreAsync();
            StatusChanged?.Invoke(_dirty ? "未保存" : "保存済み");
        }
        catch (Exception ex)
        {
            App.LogError("ノートの保存に失敗しました。", ex);
            StatusChanged?.Invoke("保存失敗");
        }
        finally
        {
            _saving = 0;
            if (_dirty || _resaveQueued)
            {
                _resaveQueued = false;
                StartTimer(ResaveDelay);
            }
        }
    }

    /// <summary>スナップショット (UI スレッド) → シリアライズ + 原子保存 (バックグラウンド)。</summary>
    private async Task SaveCoreAsync()
    {
        var snapshot = _ink.CaptureSnapshot();
        PdfInfoDto pdfInfo;
        string notePath;
        lock (_gate)
        {
            pdfInfo = _pdfInfo!;
            notePath = _notePath;
        }

        var stopwatch = Stopwatch.StartNew();
        await Task.Run(() => NoteSerializer.Save(notePath, snapshot, pdfInfo));
        stopwatch.Stop();

        LastSaveMs = stopwatch.Elapsed.TotalMilliseconds;
        try
        {
            LastNoteBytes = new FileInfo(notePath).Length;
        }
        catch
        {
            // 情報取得失敗は無視。
        }

        _dirty = false;
    }

    /// <summary>同期的な保存 (終了時フォールバック)。</summary>
    private void SaveCore()
    {
        var snapshot = _ink.CaptureSnapshot();
        PdfInfoDto pdfInfo;
        string notePath;
        lock (_gate)
        {
            pdfInfo = _pdfInfo!;
            notePath = _notePath;
        }

        var stopwatch = Stopwatch.StartNew();
        NoteSerializer.Save(notePath, snapshot, pdfInfo);
        stopwatch.Stop();

        LastSaveMs = stopwatch.Elapsed.TotalMilliseconds;
        _dirty = false;
    }

    private NoteLoadOutcome LoadNote(int pdfPageCount)
    {
        string notePath;
        lock (_gate)
        {
            notePath = _notePath;
        }

        if (!File.Exists(notePath))
        {
            return new NoteLoadOutcome { Association = NoteAssociationState.None };
        }

        NoteFileDto file;
        try
        {
            file = NoteSerializer.LoadFile(notePath);
        }
        catch (Exception)
        {
            // 破損 → バックアップからの復元を試みる (データは勝手に削除しない)。
            var backupPath = notePath + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    file = NoteSerializer.LoadFile(backupPath);
                    return new NoteLoadOutcome
                    {
                        Document = NoteSerializer.ToInkDocument(file, pdfPageCount),
                        Association = ClassifyAssociation(file.Pdf),
                        LoadedFromBackup = true,
                        Warning = "ノートファイルが壊れていたため、バックアップから復元しました。",
                    };
                }
                catch
                {
                    // バックアップも復元不可。
                }
            }

            return new NoteLoadOutcome
            {
                Association = NoteAssociationState.None,
                Warning = "ノートデータを読み込めませんでした (ファイルが壊れている可能性があります)。" +
                          "新しいノートとして開きます (旧データは .note/.bak に保持されています)。",
            };
        }

        return new NoteLoadOutcome
        {
            Document = NoteSerializer.ToInkDocument(file, pdfPageCount),
            Association = ClassifyAssociation(file.Pdf),
        };
    }

    private NoteAssociationState ClassifyAssociation(PdfInfoDto? stored)
    {
        PdfInfoDto? current;
        lock (_gate)
        {
            current = _pdfInfo;
        }

        if (stored is null || current is null)
        {
            return NoteAssociationState.PdfMismatched;
        }

        var matched = string.Equals(stored.FileName, current.FileName, StringComparison.OrdinalIgnoreCase) &&
                      stored.FileSize == current.FileSize &&
                      stored.PageCount == current.PageCount;

        return matched ? NoteAssociationState.Matched : NoteAssociationState.PdfMismatched;
    }

    private void StartTimer(TimeSpan delay)
    {
        _timer.Interval = delay;
        _timer.Stop();
        _timer.Start();
        _timer.Interval = DebounceDelay; // 次回用に戻す
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}

