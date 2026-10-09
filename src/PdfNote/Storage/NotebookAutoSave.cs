using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using PdfNote.Session;

namespace PdfNote.Storage;

/// <summary>
/// ノートブック自動保存 (段階2 最小実装)。NoteAutoSave と同じ規約:
/// Dirty → デバウンス 800ms → スナップショット (UI スレッド) → バックグラウンド JSON 化 + 原子保存。
/// 同時保存は 1 件に制限し、保存中の変更は完了後 200ms で再保存 (latest-wins)。
/// PDF 関連付け (v1) には関与しない。
/// </summary>
public sealed class NotebookAutoSave : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ResaveDelay = TimeSpan.FromMilliseconds(200);

    private readonly NotebookSession _session;
    private readonly DispatcherQueueTimer _timer;
    private readonly object _gate = new();

    private string _savePath = string.Empty;
    private volatile bool _dirty;
    private volatile bool _resaveQueued;
    private int _saving;
    private bool _hasContext;
    private Task? _saveTask;

    public double LastSaveMs { get; private set; } = -1;
    public long LastFileBytes { get; private set; } = -1;
    public bool IsDirty => _dirty;

    /// <summary>保存状態の変化 ("未保存" / "保存中…" / "保存済み" / "保存失敗")。</summary>
    public event Action<string>? StatusChanged;

    public NotebookAutoSave(NotebookSession session, DispatcherQueue dispatcher)
    {
        _session = session;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = DebounceDelay;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => BeginSave();
    }

    public string SavePath
    {
        get { lock (_gate) { return _savePath; } }
        set
        {
            lock (_gate)
            {
                _savePath = value;
                _hasContext = !string.IsNullOrWhiteSpace(value);
                _dirty = false;
            }

            _timer.Stop();
        }
    }

    /// <summary>内容が変化した。デバウンスを再起動する (UI スレッド)。</summary>
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

    /// <summary>即時保存 (アプリ終了時 / モード切替時)。呼び出しは UI スレッドから。</summary>
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
            StatusChanged?.Invoke("保存済み");
        }
        catch (Exception ex)
        {
            App.LogError("ノートブックの保存に失敗しました。", ex);
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

        _saveTask = SaveAsyncCore();
    }

    /// <summary>
    /// 保留中の保存を完了させる (ノート切替 / 削除 / 名前変更の前)。UI スレッドから呼ぶ。
    /// 完了後も Dirty な場合のみ、その場で同期保存する (不要な同期 I/O は行わない)。
    /// </summary>
    public async Task FlushPendingAsync()
    {
        if (!_hasContext)
        {
            return;
        }

        _timer.Stop();
        Task? task;
        lock (_gate)
        {
            task = _saveTask;
            _saveTask = null;
        }

        if (task is not null)
        {
            try
            {
                await task; // 例外は SaveAsyncCore 内で処理済み。
            }
            catch
            {
                // 到達しないが安全側で握りつぶす。
            }
        }

        if (_dirty)
        {
            SaveNowSync();
        }
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
            App.LogError("ノートブックの保存に失敗しました。", ex);
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
        var snapshot = _session.CaptureSnapshot();
        string savePath;
        lock (_gate)
        {
            savePath = _savePath;
        }

        var stopwatch = Stopwatch.StartNew();
        await Task.Run(() => NotebookSerializer.Save(savePath, snapshot));
        stopwatch.Stop();

        LastSaveMs = stopwatch.Elapsed.TotalMilliseconds;
        try
        {
            LastFileBytes = new FileInfo(savePath).Length;
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
        var snapshot = _session.CaptureSnapshot();
        string savePath;
        lock (_gate)
        {
            savePath = _savePath;
        }

        var stopwatch = Stopwatch.StartNew();
        NotebookSerializer.Save(savePath, snapshot);
        stopwatch.Stop();

        LastSaveMs = stopwatch.Elapsed.TotalMilliseconds;
        _dirty = false;
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
