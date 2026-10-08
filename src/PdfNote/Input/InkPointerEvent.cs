namespace PdfNote.Input;

/// <summary>ポインタイベントのフェーズ。</summary>
public enum InkPhase
{
    Pressed,
    Moved,
    Released,
    Canceled,
}

/// <summary>ポインタの種別。</summary>
public enum PointerKind
{
    Mouse,
    Pen,
    Touch,
}

/// <summary>
/// Ink 入力の転送形式。PdfCanvas がポインタ情報をページ pt 座標に変換して発行する。
/// </summary>
public readonly struct InkPointerEvent
{
    public InkPointerEvent(
        InkPhase phase,
        PointerKind kind,
        uint pointerId,
        float xPt,
        float yPt,
        float pressure,
        bool barrelButton,
        bool isEraser)
    {
        Phase = phase;
        Kind = kind;
        PointerId = pointerId;
        XPt = xPt;
        YPt = yPt;
        Pressure = pressure;
        BarrelButton = barrelButton;
        IsEraser = isEraser;
    }

    public InkPhase Phase { get; }
    public PointerKind Kind { get; }
    public uint PointerId { get; }

    /// <summary>ページ左上原点の X (pt)。</summary>
    public float XPt { get; }

    /// <summary>ページ左上原点の Y (pt)。</summary>
    public float YPt { get; }

    /// <summary>筆圧 (0..1)。</summary>
    public float Pressure { get; }

    /// <summary>ペンの裏ボタン (バレルボタン) が押されているか。</summary>
    public bool BarrelButton { get; }

    /// <summary>ペンの裏消しゴム端か。</summary>
    public bool IsEraser { get; }
}
