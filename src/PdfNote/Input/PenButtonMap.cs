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
}

/// <summary>ペンの拡張入力ボタン。</summary>
public enum InkButton
{
    /// <summary>ペン本体の裏ボタン (バレルボタン)。</summary>
    Barrel,

    /// <summary>ペンの裏消しゴム端。</summary>
    EraserEnd,
}
