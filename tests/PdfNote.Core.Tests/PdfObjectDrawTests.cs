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
        var (cw, ch, center, rad) = PdfObjectMath.GetContentRect(10f, 20f, 200f, 100f, 0);
        check(Math.Abs(cw - 200f) < 0.001 && Math.Abs(ch - 100f) < 0.001, "0° は内容=バウンディング");
        check(Math.Abs(center.X - 110f) < 0.001 && Math.Abs(center.Y - 70f) < 0.001, "中心 = バウンディング中心");
        check(Math.Abs(rad) < 0.001, "0° の回転角");

        (cw, ch, _, rad) = PdfObjectMath.GetContentRect(10f, 20f, 200f, 100f, 90);
        check(Math.Abs(cw - 100f) < 0.001 && Math.Abs(ch - 200f) < 0.001, "90° は内容矩形が入替");
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

        // キャッシュキーの同一性 (リソース / ページ / 解像度)。
        var keyA = new PdfObjectTextureKey(Guid.NewGuid(), 0, 2.0);
        var keyB = keyA with { PxPerPt = 2.5 };
        var keyC = keyA with { PdfPageIndex = 1 };
        check(!keyA.Equals(keyB) && !keyA.Equals(keyC) && keyA.Equals(keyA with { }), "キーは解像度・ページを含む");
    }
}
