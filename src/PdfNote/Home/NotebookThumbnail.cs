using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Storage;
using Windows.Foundation;
using Windows.UI;

namespace PdfNote.Home;

/// <summary>
/// ホーム画面アイコン用のサムネイル生成。
/// notebook.json の 1 ページ目 (白紙 / 方眼背景 + 手書きストローク) を
/// Win2D でオフスクリーン描画し、WriteableBitmap として返す。
/// 描画は常に正方形キャンバスに行い、縦長 / 横長どちらのページも
/// 長辺基準でスケール + 中央寄せする (アイコン上で見た目の大きさが揃う)。
/// 背景が白紙でストロークも無い場合は null (XAML 側で純白アイコンを表示)。
/// </summary>
public static class NotebookThumbnail
{
    /// <summary>サムネイル正方形キャンバスの一辺 (px)。</summary>
    private const int CanvasPixels = 440;

    /// <summary>ページ矩形の外側 (ノート外) の色 = ホーム背景色。</summary>
    private static readonly Color PageOutsideColor = Color.FromArgb(0xFF, 0x20, 0x20, 0x20);

    /// <summary>1 ページ目の描画に必要なデータ (バックグラウンドスレッドで収集)。</summary>
    public sealed record PageSnapshot(
        double WidthPt,
        double HeightPt,
        PageBackgroundKind Background,
        float GridSpacingPt,
        IReadOnlyList<Stroke> Strokes);

    /// <summary>notebook.json から 1 ページ目のスナップショットを取り出す (任意スレッド)。</summary>
    public static PageSnapshot? LoadPage(string notebookJsonPath)
    {
        var notebook = NotebookSerializer.ToNotebook(NotebookSerializer.LoadFile(notebookJsonPath));
        var page = notebook.Pages.FirstOrDefault();
        if (page is null || page.WidthPt <= 0 || page.HeightPt <= 0)
        {
            return null;
        }

        return new PageSnapshot(
            page.WidthPt,
            page.HeightPt,
            page.Background,
            page.GridSpacingPt,
            page.Ink.Strokes.ToList());
    }

    /// <summary>
    /// 1 ページ目をレンダリングする (UI スレッド。Win2D 共有デバイスを使用)。
    /// 描画範囲 = ページ矩形 ∪ 全ストロークの境界。その長辺が固定サイズ (正方形フレーム)
    /// にすべて収まるようスケールする (ページ外のストロークも見切れない)。
    /// ページ矩形は白、その外側はホーム背景色でノートの形が見えるようにする。
    /// 白紙ノートも白ページとして描画する (null は読み込み不能時のみ)。
    /// </summary>
    public static WriteableBitmap? Render(PageSnapshot snapshot)
    {
        if (snapshot.WidthPt <= 0 || snapshot.HeightPt <= 0)
        {
            return null;
        }

        // 描画範囲を求める: ページ矩形を起点に、全ストロークの境界 (線幅 + 余白) を統合する。
        var minX = 0.0;
        var minY = 0.0;
        var maxX = snapshot.WidthPt;
        var maxY = snapshot.HeightPt;
        foreach (var stroke in snapshot.Strokes)
        {
            if (stroke.Points.Count == 0)
            {
                continue;
            }

            stroke.GetBounds(out var sx0, out var sy0, out var sx1, out var sy1);
            if (sx0 == float.MaxValue)
            {
                continue;
            }

            var pad = stroke.WidthPt * 0.6f + 1f;
            minX = Math.Min(minX, sx0 - pad);
            minY = Math.Min(minY, sy0 - pad);
            maxX = Math.Max(maxX, sx1 + pad);
            maxY = Math.Max(maxY, sy1 + pad);
        }

        // 枠に接して見えるのを避けるため外側に少し余白を足す。
        const double outerMarginPt = 2.0;
        minX -= outerMarginPt;
        minY -= outerMarginPt;
        maxX += outerMarginPt;
        maxY += outerMarginPt;

        var widthPt = Math.Max(1.0, maxX - minX);
        var heightPt = Math.Max(1.0, maxY - minY);

        // 長辺がキャンバス一辺に収まるスケール (極端な拡大 / 縮小をクランプ)。
        var pxPerPt = CanvasPixels / Math.Max(widthPt, heightPt);
        var widthPx = Math.Clamp((int)Math.Round(widthPt * pxPerPt), 1, 4096);
        var heightPx = Math.Clamp((int)Math.Round(heightPt * pxPerPt), 1, 4096);

        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), widthPx, heightPx, 96f);
        byte[] pixels;
        try
        {
            using (var session = target.CreateDrawingSession())
            {
                // ノート外 (ページ矩形の外側) はホーム背景色。ページ矩形を白で塗り、形を見せる。
                session.Clear(PageOutsideColor);
                // pt 空間 → px 空間: スケール後に -min オフセットで平行移動 (PdfCanvas と同じ S*T 順)。
                session.Transform =
                    Matrix3x2.CreateScale((float)pxPerPt) *
                    Matrix3x2.CreateTranslation((float)-minX, (float)-minY);
                session.FillRectangle(0f, 0f, (float)snapshot.WidthPt, (float)snapshot.HeightPt, Colors.White);
                if (snapshot.Background == PageBackgroundKind.Grid)
                {
                    DrawGrid(session, snapshot);
                }

                foreach (var stroke in snapshot.Strokes)
                {
                    InkRibbon.Draw(session, stroke);
                }
            }

            pixels = target.GetPixelBytes();
        }
        finally
        {
            target.Dispose();
        }

        var bitmap = new WriteableBitmap(widthPx, heightPx);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>方眼背景を描画する (pt 空間)。</summary>
    private static void DrawGrid(CanvasDrawingSession session, PageSnapshot snapshot)
    {
        var spacing = snapshot.GridSpacingPt > 0 ? snapshot.GridSpacingPt : NotebookDefaults.GridSpacingPt;
        var color = Color.FromArgb(0xFF, 0xD8, 0xD8, 0xD8);
        for (var x = spacing; x < snapshot.WidthPt; x += spacing)
        {
            session.DrawLine(x, 0, x, (float)snapshot.HeightPt, color, 0.5f);
        }

        for (var y = spacing; y < snapshot.HeightPt; y += spacing)
        {
            session.DrawLine(0, y, (float)snapshot.WidthPt, y, color, 0.5f);
        }
    }
}
