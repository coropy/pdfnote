using System;

namespace PdfNote.Core;

/// <summary>
/// ページオブジェクトの変形 (移動 / 拡大縮小 / 回転) の値。
/// 適用前に <see cref="Validate"/> で全項目を検証し、適用は all-or-nothing とする。
/// この変形はオブジェクト自身にのみ適用され、ストローク座標には一切影響しない。
/// </summary>
public readonly record struct ObjectTransform(float X, float Y, float WidthPt, float HeightPt, int RotationDeg)
{
    /// <summary>オブジェクトの現在状態をキャプチャする (Undo 履歴用)。</summary>
    public static ObjectTransform Capture(PageObject obj) =>
        new(obj.X, obj.Y, obj.WidthPt, obj.HeightPt, obj.RotationDeg);

    /// <summary>全項目を検証する。不正値は <see cref="ArgumentOutOfRangeException"/>。</summary>
    public void Validate(string paramName = "transform")
    {
        if (!float.IsFinite(X) || !float.IsFinite(Y))
        {
            throw new ArgumentOutOfRangeException(paramName, "オブジェクトの位置は有限値である必要があります。");
        }

        if (!float.IsFinite(WidthPt) || WidthPt <= 0f || !float.IsFinite(HeightPt) || HeightPt <= 0f)
        {
            throw new ArgumentOutOfRangeException(paramName, "オブジェクトの寸法は正の有限値である必要があります。");
        }

        if (!PageObjectLimits.IsAllowedRotation(RotationDeg))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                $"回転角は {string.Join("/", PageObjectLimits.AllowedRotations)} のいずれかである必要があります。");
        }
    }

    /// <summary>検証済みの値をオブジェクトへ適用する (呼び出し前に Validate 済みであること)。</summary>
    public void ApplyTo(PageObject obj)
    {
        obj.X = X;
        obj.Y = Y;
        obj.SetSize(WidthPt, HeightPt);
        obj.SetRotation(RotationDeg);
    }
}
