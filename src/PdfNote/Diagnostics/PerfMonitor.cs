using System;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace PdfNote.Diagnostics;

/// <summary>
/// 性能計測: FPS / frame time / PDF render time / GPU upload time / メモリ / キャッシュ使用量。
/// CompositionTarget.Rendering のたびに <see cref="RecordFrameTick"/> を呼び出し、
/// 一定周期 (500ms) ごとに集計テキストを生成する。
/// </summary>
public sealed class PerfMonitor
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Process _process = Process.GetCurrentProcess();

    private double _prevTickMs = -1;
    private double _windowStartMs;
    private int _frames;
    private double _frameSumMs;
    private double _frameMaxMs;
    private double _lastRenderMs = -1;
    private double _lastUploadMs = -1;
    private Func<string>? _extraProvider;

    /// <summary>CompositionTarget.Rendering ハンドラから呼び出す。</summary>
    public void RecordFrameTick()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        if (_prevTickMs > 0)
        {
            var delta = now - _prevTickMs;
            _frames++;
            _frameSumMs += delta;
            if (delta > _frameMaxMs)
            {
                _frameMaxMs = delta;
            }
        }
        else
        {
            _windowStartMs = now;
        }

        _prevTickMs = now;
    }

    /// <summary>バックグラウンド PDF ラスタライズ時間 (ms) を記録する。</summary>
    public void RecordRender(double milliseconds) => _lastRenderMs = milliseconds;

    /// <summary>GPU テクスチャ (CanvasBitmap) 生成時間 (ms) を記録する。</summary>
    public void RecordUpload(double milliseconds) => _lastUploadMs = milliseconds;

    /// <summary>集計テキストに追加情報 (ズーム/キャッシュ等) を混ぜるプロバイダ。</summary>
    public void SetExtraProvider(Func<string>? provider) => _extraProvider = provider;

    /// <summary>定期集計を開始する。</summary>
    public void Start(DispatcherQueue dispatcherQueue, Action<string> onTextUpdated)
    {
        var timer = dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => onTextUpdated(BuildText());
        timer.Start();
    }

    private string BuildText()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var elapsed = now - _windowStartMs;

        var fps = _frames > 0 && elapsed > 0 ? _frames * 1000.0 / elapsed : 0.0;
        var avgFrame = _frames > 0 ? _frameSumMs / _frames : 0.0;
        var maxFrame = _frameMaxMs;

        // ウィンドウをリセット
        _windowStartMs = now;
        _frames = 0;
        _frameSumMs = 0;
        _frameMaxMs = 0;

        _process.Refresh();
        var workingSetMb = _process.WorkingSet64 / (1024.0 * 1024.0);
        var managedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

        var render = _lastRenderMs < 0 ? "-" : $"{_lastRenderMs:F0} ms";
        var upload = _lastUploadMs < 0 ? "-" : $"{_lastUploadMs:F0} ms";
        var extra = _extraProvider?.Invoke() ?? string.Empty;

        return
            $"FPS {fps,5:F1}   Frame avg {avgFrame,5:F1} ms (max {maxFrame,5:F1})\n" +
            $"PDF render {render}   GPU upload {upload}\n" +
            $"Memory {workingSetMb:F0} MB (managed {managedMb:F0} MB)" +
            (string.IsNullOrEmpty(extra) ? string.Empty : "\n" + extra);
    }
}
