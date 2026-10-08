namespace PdfNote.Core;

/// <summary>
/// 1ページ分のレンダリング結果。
/// <see cref="Pixels"/> は BGRA (1ピクセル4バイト)、上→下順のラスタ順。
/// </summary>
/// <param name="PageIndex">ページ番号 (0 始まり)。</param>
/// <param name="PixelWidth">ピクセル幅。</param>
/// <param name="PixelHeight">ピクセル高。</param>
/// <param name="Pixels">BGRA ピクセルバッファ。</param>
/// <param name="PxPerPt">レンダリング時に使用した解像度 (px/pt)。</param>
/// <param name="RenderMs">ラスタライズにかかった時間 (ミリ秒、性能計測用)。</param>
public sealed record RenderedFrame(
    int PageIndex,
    int PixelWidth,
    int PixelHeight,
    byte[] Pixels,
    double PxPerPt,
    double RenderMs);
