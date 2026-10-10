using System;
using System.Numerics;
using PdfNote.Core;

namespace PdfNote.View;

/// <summary>
/// PDF オブジェクト GPU テクスチャのキー (同一性判定に必要な値すべてを含む)。
/// オブジェクト ID を含むため、同じリソース+ページでもオブジェクトごとに独立したテクスチャを持つ
/// (CPU レンダリング要求はサービス側で資源+ページ単位に統合されるため、GPU アップロードのみが複製される)。
/// </summary>
public readonly record struct PdfObjectTextureKey(
    Guid ObjectId, Guid ResourceId, int PdfPageIndex, double PxPerPt);

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
    /// X/Y/WidthPt/HeightPt は回転後のバウンディング矩形 (ページ座標)。
    /// 90/270 度では内容矩形の幅と高さが入れ替わり、内容中心とバウンディング中心は一致しない
    /// (CreateObjectTransform が両者を正しく接続する)。
    /// </summary>
    public static (float ContentWidthPt, float ContentHeightPt, Vector2 ContentCenter, float RotationRadians)
        GetContentRect(float x, float y, float widthPt, float heightPt, int rotationDeg)
    {
        if (!PageObjectLimits.IsAllowedRotation(rotationDeg))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDeg), "回転角は 0/90/180/270 のいずれかである必要があります。");
        }

        var radians = rotationDeg switch
        {
            90 => MathF.PI / 2f,
            180 => MathF.PI,
            270 => MathF.PI * 1.5f,
            _ => 0f,
        };
        var swap = rotationDeg is 90 or 270;
        var contentWidth = swap ? heightPt : widthPt;
        var contentHeight = swap ? widthPt : heightPt;
        return (contentWidth, contentHeight, new Vector2(contentWidth / 2f, contentHeight / 2f), radians);
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

    /// <summary>
    /// オブジェクト描画の変換行列を構築する (Win2D と同じ行ベクトル規約: A * B は A を先に適用)。
    /// ビットマップ px → PDF pt → 内容矩形 (伸縮) → 内容中心基準の回転 → バウンディング中心へ配置 → ページ → 画面。
    /// PDF はオブジェクトの表示領域に合わせて伸縮する (トリミングなし。縦横比はオブジェクト寸法が定義)。
    /// </summary>
    public static Matrix3x2 CreateObjectTransform(
        double texPxPerPt,
        float contentWidthPt, float contentHeightPt,
        float pdfPageWidthPt, float pdfPageHeightPt,
        Vector2 boundingCenter, float rotationRadians,
        Matrix3x2 pageTransform)
    {
        // px → 内容 pt: (内容寸法 / (PDF 寸法 × texScale))。texScale は px / PDF pt。
        var sx = (float)(contentWidthPt / Math.Max(pdfPageWidthPt * texPxPerPt, 0.0001));
        var sy = (float)(contentHeightPt / Math.Max(pdfPageHeightPt * texPxPerPt, 0.0001));
        return Matrix3x2.CreateScale(sx, sy)
               * Matrix3x2.CreateTranslation(-contentWidthPt / 2f, -contentHeightPt / 2f)
               * Matrix3x2.CreateRotation(rotationRadians)
               * Matrix3x2.CreateTranslation(boundingCenter.X, boundingCenter.Y)
               * pageTransform;
    }

    /// <summary>オブジェクト矩形がビューポート (ページpt座標) と交わるか (マージン付き)。画面外への無制限要求を防ぐ。</summary>
    public static bool IntersectsViewport(
        double objX, double objY, double objWidth, double objHeight,
        double viewX, double viewY, double viewWidth, double viewHeight,
        double marginPt)
    {
        var margin = Math.Max(marginPt, 0);
        return objX - margin < viewX + viewWidth &&
               objX + objWidth + margin > viewX &&
               objY - margin < viewY + viewHeight &&
               objY + objHeight + margin > viewY;
    }
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
