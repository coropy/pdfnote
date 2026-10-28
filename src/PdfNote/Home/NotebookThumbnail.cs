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
/// 背景が白紙でストロークも無い場合は null (XAML 側で純白アイコンを表示)。
/// </summary>
public static class NotebookThumbnail
{
    /// <summary>サムネイルの目標幅 (px)。A4 比で高さを決める。</summary>
    private const int BasePixelWidth = 440;

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
    /// 描画不要な場合は null を返す。
    /// </summary>
    public static WriteableBitmap? Render(PageSnapshot snapshot)
    {
        if (!snapshot.NeedsRender)
        {
            return null;
        }

        var pxPerPt = BasePixelWidth / snapshot.WidthPt;
        var widthPx = Math.Max(1, (int)Math.Round(snapshot.WidthPt * pxPerPt));
        var heightPx = Math.Max(1, (int)Math.Round(snapshot.HeightPt * pxPerPt));

        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), widthPx, heightPx, 96f);
        byte[] pixels;
        try
        {
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Colors.White);
                // pt 空間 → px 空間への一括スケール (グリッド / ストローク共通)。
                session.Transform = Matrix3x2.CreateScale((float)pxPerPt);
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
