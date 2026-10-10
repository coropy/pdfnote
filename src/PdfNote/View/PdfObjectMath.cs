using System;
using System.Numerics;
using PdfNote.Core;

namespace PdfNote.View;

/// <summary>PDF オブジェクト GPU テクスチャのキー (同一性判定に必要な値すべてを含む)。</summary>
public readonly record struct PdfObjectTextureKey(Guid ResourceId, int PdfPageIndex, double PxPerPt);

/// <summary>
/// PDF オブジェクト描画の純粋計算 (Win2D 非依存・テスト可能)。
/// オブジェクトの X/Y/寸法/回転はページ座標で解釈され、PDF の変換とは独立。
/// </summary>
public static class PdfObjectMath
{
    /// <summary>ズーム確定後の再レンダリング判定許容差 (既存キャッシュと同じ ±15%)。</summary>
    public const double RerenderTolerance = 0.15;

    public static bool IsAllowedRotation(int rotationDeg) =>
        PageObjectLimits.IsAllowedRotation(rotationDeg);

    /// <summary>
    /// オブジェクト配置から「テクスチャ内容の矩形」を求める。
    /// X/Y/WidthPt/HeightPt は回転後のバウンディング矩形 (ページ座標)、
    /// 回転はその中心基準。90/270 度では内容矩形の幅と高さが入れ替わる。
    /// </summary>
    public static (float ContentWidthPt, float ContentHeightPt, Vector2 Center, float RotationRadians)
        GetContentRect(float x, float y, float widthPt, float heightPt, int rotationDeg)
    {
        if (!PageObjectLimits.IsAllowedRotation(rotationDeg))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDeg), "回転角は 0/90/180/270 のいずれかである必要があります。");
        }

        var center = new Vector2(x + widthPt / 2f, y + heightPt / 2f);
        var radians = rotationDeg switch
        {
            90 => MathF.PI / 2f,
            180 => MathF.PI,
            270 => MathF.PI * 1.5f,
            _ => 0f,
        };
        var swap = rotationDeg is 90 or 270;
        return (swap ? heightPt : widthPt, swap ? widthPt : heightPt, center, radians);
    }

    /// <summary>
    /// 表示スケール → テクスチャに必要な実効解像度 (px / PDF pt)。
    /// PDF はオブジェクト寸法に従って伸縮する (縦横比を勝手に固定しない) ため、
    /// 2 方向のうち大きい方を採用する。
    /// </summary>
    public static double EffectivePxPerPt(
        double displayPxPerPt, double contentWidthPt, double contentHeightPt, double pdfPageWidthPt, double pdfPageHeightPt)
    {
        if (pdfPageWidthPt <= 0 || pdfPageHeightPt <= 0 || contentWidthPt <= 0 || contentHeightPt <= 0)
        {
            return displayPxPerPt;
        }

        var horizontal = displayPxPerPt * (contentWidthPt / pdfPageWidthPt);
        var vertical = displayPxPerPt * (contentHeightPt / pdfPageHeightPt);
        return Math.Max(horizontal, vertical);
    }

    /// <summary>既存テクスチャの解像度で要求を満たせるか (乖離 ±tolerance 以内)。</summary>
    public static bool IsTextureUsable(double texPxPerPt, double requiredPxPerPt, double tolerance = RerenderTolerance)
    {
        if (texPxPerPt <= 0 || requiredPxPerPt <= 0)
        {
            return false;
        }

        var ratio = texPxPerPt / requiredPxPerPt;
        return ratio >= 1.0 - tolerance && ratio <= 1.0 + tolerance;
    }

    /// <summary>RenderedFrame (BGRA, 4バイト/px) 1 枚の推定バイト数。GPU テクスチャのメモリ見積りに使用。</summary>
    public static long EstimateFrameBytes(int pixelWidth, int pixelHeight) =>
        (long)pixelWidth * pixelHeight * 4;

    /// <summary>16Mpx テクスチャ上限の超過判定 (RenderScale の規約に従う)。</summary>
    public static bool ExceedsTextureBudget(int pixelWidth, int pixelHeight) =>
        (long)pixelWidth * pixelHeight > RenderScale.MaxRenderPixels;
}

/// <summary>描画時の判断 (純粋関数・テスト可能)。</summary>
public enum PdfObjectDrawAction
{
    /// <summary>有効テクスチャを描画。</summary>
    DrawTexture,

    /// <summary>旧テクスチャを拡縮表示しつつ、新しい解像度で再要求する。</summary>
    ReuseStaleTexture,

    /// <summary>テクスチャなし → 非同期要求を開始し、プレースホルダを表示。</summary>
    RequestTexture,

    /// <summary>同一条件の要求が既に飛行中 (重複要求しない。プレースホルダ表示)。</summary>
    PendingAlready,

    /// <summary>リソース欠落 / オープン失敗 (プレースホルダ + 警告状態。要求しない)。</summary>
    MissingResource,

    /// <summary>PDF ページ番号が範囲外 / 描画不可エラー中 (要求しない。プレースホルダ表示)。</summary>
    PageOutOfRange,

    /// <summary>リソースは登録済みだが未オープン / 寸法未取得 (要求開始前。プレースホルダ表示)。</summary>
    ResourceNotReady,
}

/// <summary>PDF オブジェクト描画の判断 (状態を持たない純粋関数)。</summary>
public static class PdfObjectDrawPlanner
{
    public static PdfObjectDrawAction Decide(
        bool missingResource,
        bool pageOutOfRange,
        bool hasTexture,
        bool textureUsable,
        bool pendingSameScale,
        bool pendingOtherScale)
    {
        if (missingResource)
        {
            return PdfObjectDrawAction.MissingResource;
        }

        if (pageOutOfRange)
        {
            return PdfObjectDrawAction.PageOutOfRange;
        }

        if (hasTexture && textureUsable)
        {
            return PdfObjectDrawAction.DrawTexture;
        }

        if (hasTexture)
        {
            // 旧テクスチャを表示しつつ再要求 (空白化しない)。
            return pendingSameScale ? PdfObjectDrawAction.PendingAlready : PdfObjectDrawAction.ReuseStaleTexture;
        }

        if (pendingSameScale)
        {
            return PdfObjectDrawAction.PendingAlready;
        }

        // 飛行中の異なる解像度の要求は陳腐化するため、最新の解像度で再要求する。
        _ = pendingOtherScale;
        return PdfObjectDrawAction.RequestTexture;
    }
}
