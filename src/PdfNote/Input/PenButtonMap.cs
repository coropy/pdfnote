using System.Collections.Generic;

namespace PdfNote.Input;

/// <summary>
/// ペンボタン (バレルボタン) と InputAction の割り当て。
/// v0.4 で設定UIを追加する。ここでは既定値と割当変更APIのみ提供する。
/// </summary>
public sealed class PenButtonMap
{
    private readonly Dictionary<InkButton, InputAction> _map = new();

    public PenButtonMap()
    {
        _map[InkButton.Barrel] = InputAction.TemporaryEraser;
        _map[InkButton.EraserEnd] = InputAction.Eraser;
    }

    public InputAction Get(InkButton button) =>
        _map.TryGetValue(button, out var action) ? action : InputAction.None;

    public void Set(InkButton button, InputAction action) => _map[button] = action;

    /// <summary>既定の割当 (バレル=TemporaryEraser, 消しゴム端=Eraser) に戻す。</summary>
    public void ResetToDefaults()
    {
        _map[InkButton.Barrel] = InputAction.TemporaryEraser;
        _map[InkButton.EraserEnd] = InputAction.Eraser;
    }

    /// <summary>
    /// 設定UIの候補にできるアクション (実装済みのもののみ)。
    /// RightClick / CustomShortcut は未実装のため除外する (見かけだけ選べる状態を作らない)。
    /// </summary>
    public static bool IsSupported(InputAction action) => action switch
    {
        InputAction.None => true,
        InputAction.Pen => true,
        InputAction.Eraser => true,
        InputAction.TemporaryEraser => true,
        InputAction.Highlighter => true,
        InputAction.Undo => true,
        InputAction.Redo => true,
        InputAction.PreviousPage => true,
        InputAction.NextPage => true,
        InputAction.Pan => true,
        _ => false,
    };
}

/// <summary>ペンの拡張入力ボタン。</summary>
public enum InkButton
{
    /// <summary>ペン本体の裏ボタン (バレルボタン)。</summary>
    Barrel,

    /// <summary>ペンの裏消しゴム端。</summary>
    EraserEnd,
}
