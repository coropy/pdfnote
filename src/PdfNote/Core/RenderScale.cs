namespace PdfNote.Core;

/// <summary>
/// レンダリング解像度に関する共通定数。
/// スケールは px/pt (ピクセル / ポイント) で表現する。100% 表示 = 96dpi = 1.333... px/pt。
/// </summary>
public static class RenderScale
{
    /// <summary>100% ズーム時の px/pt (96dpi / 72pt)。</summary>
    public const double DpiScale = 96.0 / 72.0;

    /// <summary>初期値 (100%)。</summary>
    public const double DefaultPxPerPt = DpiScale;

    /// <summary>最小ズーム (25%)。</summary>
    public const double MinPxPerPt = 0.25 * DpiScale;

    /// <summary>最大ズーム (800%)。</summary>
    public const double MaxPxPerPt = 8.0 * DpiScale;

    /// <summary>1回のレンダリングで許容する最大ピクセル数 (約4096x4096)。</summary>
    public const long MaxRenderPixels = 16_000_000;
}
