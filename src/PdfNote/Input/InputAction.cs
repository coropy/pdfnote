namespace PdfNote.Input;

/// <summary>
/// 入力デバイスのボタンに割り当て可能なアクション。
/// 入力と機能を分離するため、描画処理に直接結びつけない。
/// </summary>
public enum InputAction
{
    /// <summary>何もしない。</summary>
    None,

    /// <summary>押している間ペンに一時変更。</summary>
    Pen,

    /// <summary>押している間消しゴムに一時変更。</summary>
    Eraser,

    /// <summary>押している間だけ消しゴム (TemporaryEraser)。</summary>
    TemporaryEraser,

    /// <summary>押している間蛍光ペンに一時変更 (v1.0)。</summary>
    Highlighter,

    /// <summary>Undo を実行。</summary>
    Undo,

    /// <summary>Redo を実行。</summary>
    Redo,

    /// <summary>前のページへ。</summary>
    PreviousPage,

    /// <summary>次のページへ。</summary>
    NextPage,

    /// <summary>押している間パン。</summary>
    Pan,

    /// <summary>右クリック相当 (将来拡張)。</summary>
    RightClick,

    /// <summary>カスタムショートカット (将来拡張)。</summary>
    CustomShortcut,
}
