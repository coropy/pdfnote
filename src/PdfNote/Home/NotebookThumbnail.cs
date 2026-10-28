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
        IReadOnlyList<Stroke> Strokes)
    {
        /// <summary>描画の必要があるか (白紙かつストローク無しは不要)。</summary>
        public bool NeedsRender =>
            Strokes.Count > 0 || Background != PageBackgroundKind.Plain;
    }

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
    /// 正方形キャンバスに長辺基準でスケールし、余白 (白) を含めて中央寄せする。
    /// 描画不要な場合は null を返す。
    /// </summary>
    public static WriteableBitmap? Render(PageSnapshot snapshot)
    {
        if (!snapshot.NeedsRender)
        {
            return null;
        }

        // 長辺がキャンバス一辺に収まるスケール。余白は白で埋める (見た目上ページと一体化)。
        var pxPerPt = CanvasPixels / Math.Max(snapshot.WidthPt, snapshot.HeightPt);
        var offsetX = (CanvasPixels - snapshot.WidthPt * pxPerPt) / 2;
        var offsetY = (CanvasPixels - snapshot.HeightPt * pxPerPt) / 2;

        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), CanvasPixels, CanvasPixels, 96f);
        byte[] pixels;
        try
        {
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Colors.White);
                // pt 空間 → 正方形 px 空間 (スケール後に中央へ平行移動)。
                session.Transform =
                    Matrix3x2.CreateScale((float)pxPerPt) *
                    Matrix3x2.CreateTranslation((float)offsetX, (float)offsetY);
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
