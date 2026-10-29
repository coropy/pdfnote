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
    /// 正方形キャンバスに「ページの中心」をキャンバス中心に合わせて描画する。
    /// スケールはページ矩形 ∪ 全ストロークの境界が全体に収まるように選ぶ
    /// (ページ外に描かれたストロークも見切れない)。ページ外は透明。
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

        // ページ中心をキャンバス中心に置きつつ、内容全体が収まるスケールを求める
        // (中心から左右 / 上下の遠い側に合わせる)。
        var centerX = snapshot.WidthPt / 2;
        var centerY = snapshot.HeightPt / 2;
        var halfW = Math.Max(centerX - minX, maxX - centerX);
        var halfH = Math.Max(centerY - minY, maxY - centerY);
        var pxPerPt = CanvasPixels / (2 * Math.Max(halfW, halfH));
        var offsetX = CanvasPixels / 2 - centerX * pxPerPt;
        var offsetY = CanvasPixels / 2 - centerY * pxPerPt;

        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), CanvasPixels, CanvasPixels, 96f);
        byte[] pixels;
        try
        {
            using (var session = target.CreateDrawingSession())
            {
                // ページ外は透明 (ホーム背景 / ホバーの上に直接ノートが浮かぶ)。
                session.Clear(Colors.Transparent);
                // pt 空間 → px 空間: スケール後に中央へ平行移動 (PdfCanvas と同じ S*T 順)。
                session.Transform =
                    Matrix3x2.CreateScale((float)pxPerPt) *
                    Matrix3x2.CreateTranslation((float)offsetX, (float)offsetY);
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

        var bitmap = new WriteableBitmap(CanvasPixels, CanvasPixels);
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
