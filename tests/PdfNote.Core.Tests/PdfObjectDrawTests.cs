using System.Numerics;
using PdfNote.Core;
using PdfNote.View;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ4: PDF オブジェクト描画の純粋計算 (座標 / 解像度 / 判断 / キャッシュ管理)。</summary>
internal static class PdfObjectDrawTests
{
    internal static void ContentRectAndRotation(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectDraw_ContentRect");
        var (cw, ch, contentCenter, rad) = PdfObjectMath.GetContentRect(10f, 20f, 200f, 100f, 0);
        check(Math.Abs(cw - 200f) < 0.001 && Math.Abs(ch - 100f) < 0.001, "0° は内容=バウンディング");
        check(Math.Abs(contentCenter.X - 100f) < 0.001 && Math.Abs(contentCenter.Y - 50f) < 0.001,
            "0° の内容中心 = 内容矩形の中心 (バウンディング中心と一致)");
        check(Math.Abs(rad) < 0.001, "0° の回転角");

        (cw, ch, contentCenter, rad) = PdfObjectMath.GetContentRect(10f, 20f, 200f, 100f, 90);
        check(Math.Abs(cw - 100f) < 0.001 && Math.Abs(ch - 200f) < 0.001, "90° は内容矩形が入替");
        check(Math.Abs(contentCenter.X - 50f) < 0.001 && Math.Abs(contentCenter.Y - 100f) < 0.001,
            "90° の内容中心 (内容矩形基準。バウンディング中心とは異なる)");
        check(Math.Abs(rad - MathF.PI / 2f) < 0.001, "90° の回転角");

        (cw, ch, _, _) = PdfObjectMath.GetContentRect(0f, 0f, 200f, 100f, 270);
        check(Math.Abs(cw - 100f) < 0.001 && Math.Abs(ch - 200f) < 0.001, "270° も入替");
        check(PdfObjectMath.IsAllowedRotation(180), "180° は許可");
    }

    internal static void EffectiveScaleAndStaleness(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectDraw_EffectiveScale");
        // PDF 200×100pt をオブジェクト 400×100pt (横に伸縮) で表示、表示 2px/pt。
        var scale = PdfObjectMath.EffectivePxPerPt(2.0, 400, 100, 200, 100);
        check(Math.Abs(scale - 4.0) < 0.001, "伸縮方向 (大きい方) の実効解像度");

        var equal = PdfObjectMath.EffectivePxPerPt(2.0, 200, 100, 200, 100);
        check(Math.Abs(equal - 2.0) < 0.001, "等倍表示は表示スケールそのもの");

        check(PdfObjectMath.IsTextureUsable(2.0, 2.0), "同解像度は使用可");
        check(PdfObjectMath.IsTextureUsable(2.14, 2.0), "+14% は許容内");
        check(!PdfObjectMath.IsTextureUsable(2.31, 2.0), "+15% 超は再レンダリング");
        check(!PdfObjectMath.IsTextureUsable(1.5, 2.0), "-25% は再レンダリング");
        check(PdfObjectMath.IsTextureUsable(1.86, 2.0), "-14% は許容内");
    }

    internal static void DrawDecision(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectDraw_Planner");
        check(PdfObjectDrawPlanner.Decide(true, false, false, false, false, false) ==
            PdfObjectDrawAction.MissingResource, "リソース欠落は要求しない");
        check(PdfObjectDrawPlanner.Decide(false, true, false, false, false, false) ==
            PdfObjectDrawAction.PageOutOfRange, "範囲外ページは要求しない");
        check(PdfObjectDrawPlanner.Decide(false, false, true, true, false, false) ==
            PdfObjectDrawAction.DrawTexture, "有効テクスチャを描画");
        check(PdfObjectDrawPlanner.Decide(false, false, true, false, false, false) ==
            PdfObjectDrawAction.ReuseStaleTexture, "陳腐化テクスチャは表示しつつ再要求");
        check(PdfObjectDrawPlanner.Decide(false, false, true, false, true, false) ==
            PdfObjectDrawAction.PendingAlready, "同条件飛行中は重複要求しない");
        check(PdfObjectDrawPlanner.Decide(false, false, false, false, true, false) ==
            PdfObjectDrawAction.PendingAlready, "同条件飛行中 (テクスチャなし) も待つ");
        check(PdfObjectDrawPlanner.Decide(false, false, false, false, false, true) ==
            PdfObjectDrawAction.RequestTexture, "異なる解像度の飛行中は最新解像度で再要求");
        check(PdfObjectDrawPlanner.Decide(false, false, false, false, false, false) ==
            PdfObjectDrawAction.RequestTexture, "初回は要求");
    }

    internal static void TransformMatrixOrder(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectDraw_TransformMatrixOrder");
        // オブジェクト: X=100, Y=50, W=200, H=100 (バウンディング)。テクスチャ 2px/pt。
        var pageTransform = Matrix3x2.CreateScale(2f) * Matrix3x2.CreateTranslation(30f, 40f);
        var (cw, ch, contentCenter, rad) = PdfObjectMath.GetContentRect(100f, 50f, 200f, 100f, 0);
        var boundingCenter = new Vector2(100f + 200f / 2f, 50f + 100f / 2f);

        var transform = PdfObjectMath.CreateObjectTransform(
            2.0, cw, ch, 200f, 100f, boundingCenter, rad, pageTransform);

        // ビットマップ左上 (0,0) → ページ (X, Y) → 画面 (X*2+30, Y*2+40)。
        var topLeft = Vector2.Transform(Vector2.Zero, transform);
        check(Math.Abs(topLeft.X - (100f * 2f + 30f)) < 0.01 &&
              Math.Abs(topLeft.Y - (50f * 2f + 40f)) < 0.01, "平行移動 / 拡大の適用順序 (0°)");

        // ビットマップ右下 (pdfW*2, pdfH*2) → ページ (X+W, Y+H)。
        var bottomRight = Vector2.Transform(new Vector2(200f * 2f, 100f * 2f), transform);
        check(Math.Abs(bottomRight.X - ((100f + 200f) * 2f + 30f)) < 0.01 &&
              Math.Abs(bottomRight.Y - ((50f + 100f) * 2f + 40f)) < 0.01, "右下角の対応 (トリミングなしで全域)");

        // 90° 回転: 内容矩形 (100, 200) を内容中心で回し、バウンディング中心に配置すると、
        // ビットマップ4隅 (px: 0..pdfW*tex, 0..pdfH*tex) の変換結果がバウンディング矩形 (100..300, 50..150) に一致する。
        (cw, ch, contentCenter, rad) = PdfObjectMath.GetContentRect(100f, 50f, 200f, 100f, 90);
        transform = PdfObjectMath.CreateObjectTransform(
            2.0, cw, ch, 200f, 100f, boundingCenter, rad, Matrix3x2.Identity);
        var pixelCorners = new[]
        {
            Vector2.Transform(Vector2.Zero, transform),
            Vector2.Transform(new Vector2(200f * 2f, 0f), transform),
            Vector2.Transform(new Vector2(0f, 100f * 2f), transform),
            Vector2.Transform(new Vector2(200f * 2f, 100f * 2f), transform),
        };
        var minX = pixelCorners.Min(c => c.X); var maxX = pixelCorners.Max(c => c.X);
        var minY = pixelCorners.Min(c => c.Y); var maxY = pixelCorners.Max(c => c.Y);
        check(Math.Abs(minX - 100f) < 0.01 && Math.Abs(maxX - 300f) < 0.01 &&
              Math.Abs(minY - 50f) < 0.01 && Math.Abs(maxY - 150f) < 0.01,
            "90° 回転の4隅はバウンディング矩形に一致 (内容中心回転 → バウンディング中心配置)");

        // 回転後、内容中心 (px) はバウンディング中心に一致する。
        var rotatedContentCenter = Vector2.Transform(
            new Vector2(200f * 2f / 2f, 100f * 2f / 2f), transform);
        check(Math.Abs(rotatedContentCenter.X - boundingCenter.X) < 0.01 &&
              Math.Abs(rotatedContentCenter.Y - boundingCenter.Y) < 0.01, "内容中心 → バウンディング中心");
    }

    internal static void ViewportCulling(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectDraw_ViewportCulling");
        // ビューポート: ページ座標 (0,0)-(595, 842)。
        check(PdfObjectMath.IntersectsViewport(500, 800, 200, 100, 0, 0, 595, 842, 64),
            "ビューポート端と重なるオブジェクトは可視");
        check(!PdfObjectMath.IntersectsViewport(700, 0, 200, 100, 0, 0, 595, 842, 64),
            "完全に画面外のオブジェクトは不可視");
        check(PdfObjectMath.IntersectsViewport(640, 0, 200, 100, 0, 0, 595, 842, 64),
            "マージン (64pt) 内のオブジェクトは可視");
        check(!PdfObjectMath.IntersectsViewport(700, 0, 200, 100, 0, 0, 595, 842, 0),
            "マージンなしでは不可視");
    }

    internal static void TextureBudget(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectDraw_TextureBudget");
        check(PdfObjectMath.EstimateFrameBytes(400, 200) == 400L * 200 * 4, "BGRA 4バイト/px の推定");
        check(PdfObjectMath.ExceedsTextureBudget(4097, 4097), "16Mpx 超過を検出");
        check(!PdfObjectMath.ExceedsTextureBudget(4000, 4000), "16Mpx ちょうどは許可");
    }

    internal static void TextureCacheCoreLru(Action<bool, string> check)
    {
        Console.WriteLine("PdfObjectDraw_TextureCacheCore");
        var core = new TextureCacheCore<string>(capacity: 6, maxBytes: 1000);
        var evicted = new List<string>();

        for (var i = 0; i < 6; i++)
        {
            core.Add($"k{i}", $"v{i}", 100, evicted);
        }

        check(evicted.Count == 0 && core.Stats.Count == 6 && core.Stats.Bytes == 600, "上限内は退避なし");

        // 最古 (k0) を再使用 → LRU 末尾は k1。
        core.TryGet("k0", out _);
        core.Add("k6", "v6", 100, evicted);
        check(evicted.Count == 1 && evicted[0] == "k1", "LRU 末尾を退避");
        check(!core.TryGet("k1", out _) && core.TryGet("k0", out var kept) && (string)kept == "v0",
            "退避済みは再利用できず、再使用分は残る");

        // バイト予算: 700 バイト追加で 900 超過 → 末尾から 2 枚退避。
        var evicted2 = new List<string>();
        core.Add("k7", "v7", 700, evicted2);
        check(evicted2.Contains("k6") || evicted2.Contains("k2") || evicted2.Contains("k0"),
            "バイト予算超過で LRU 退避");
        check(core.Stats.Bytes <= 1000, "予算内に収まる");

        // Remove。
        core.Remove("k7");
        check(!core.TryGet("k7", out _) && core.Stats.Count >= 1, "Remove で参照解除");

        // キャッシュキーの同一性 (オブジェクト / リソース / ページ / 解像度)。
        var objectId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var keyA = new PdfObjectTextureKey(objectId, resourceId, 0, 2.0);
        var keyB = keyA with { PxPerPt = 2.5 };
        var keyC = keyA with { PdfPageIndex = 1 };
        var keyD = keyA with { ObjectId = Guid.NewGuid() };
        check(!keyA.Equals(keyB) && !keyA.Equals(keyC) && !keyA.Equals(keyD) && keyA.Equals(keyA with { }),
            "キーはオブジェクト・解像度・ページを含む");
    }
}
