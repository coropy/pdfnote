using System;
using PdfNote.Ink;

namespace PdfNote.Input;

/// <summary>
/// 「基本ツール + ボタンによる一時上書き」のツール状態機械。
/// 設定変更時に Ink Layer 自体を変更する必要がない。
/// </summary>
public sealed class InkToolController
{
    private readonly PenButtonMap _buttonMap = new();

    /// <summary>現在の実効ツール (一時上書きを反映)。</summary>
    public InkTool CurrentTool
    {
        get
        {
            var action = _temporaryAction ?? InputAction.None;
            return action switch
            {
                InputAction.Pen => InkTool.Pen,
                InputAction.Eraser => InkTool.Eraser,
                InputAction.TemporaryEraser => InkTool.Eraser,
                InputAction.Highlighter => InkTool.Highlighter,
                _ => _baseTool,
            };
        }
    }

    /// <summary>バレルボタンのアクション (一時上書き判定に使用)。</summary>
    public InputAction BarrelAction => _buttonMap.Get(InkButton.Barrel);

    private InkTool _baseTool = InkTool.Pen;
    private InputAction? _temporaryAction;

    /// <summary>ツールが変わった (UI 更新用)。</summary>
    public event Action<InkTool>? ToolChanged;

    /// <summary>一時アクション (Undo/Redo/ページ移動等) が要求された。</summary>
    public event Action<InputAction>? ActionRequested;

    public void SetBaseTool(InkTool tool)
    {
        if (_baseTool == tool)
        {
            return;
        }

        _baseTool = tool;
        ToolChanged?.Invoke(CurrentTool);
    }

    /// <summary>ボタン押下時: 一時アクションの開始 / 単発アクションの実行要求。</summary>
    public void ButtonDown(InkButton button)
    {
        var action = _buttonMap.Get(button);
        switch (action)
        {
            case InputAction.None:
                break;

            case InputAction.Pen:
            case InputAction.Eraser:
            case InputAction.TemporaryEraser:
            case InputAction.Highlighter:
            case InputAction.Pan:
                // 「押している間だけ」の一時アクション。
                if (_temporaryAction is null)
                {
                    _temporaryAction = action;
                    ToolChanged?.Invoke(CurrentTool);
                }
                break;

            case InputAction.Undo:
            case InputAction.Redo:
            case InputAction.PreviousPage:
            case InputAction.NextPage:
            case InputAction.RightClick:
            case InputAction.CustomShortcut:
                ActionRequested?.Invoke(action);
                break;
        }
    }

    /// <summary>ボタン解放時: 一時アクションの終了。</summary>
    public void ButtonUp(InkButton button)
    {
        var action = _buttonMap.Get(button);
        if (_temporaryAction == action)
        {
            _temporaryAction = null;
            ToolChanged?.Invoke(CurrentTool);
        }
    }

    /// <summary>裏消しゴム端が接触した場合の一時ツール切替。</summary>
    public void SetEraserEndActive(bool active)
    {
        if (active)
        {
            if (_temporaryAction is null)
            {
                _temporaryAction = InputAction.Eraser;
                ToolChanged?.Invoke(CurrentTool);
            }
        }
        else if (_temporaryAction == InputAction.Eraser && _buttonMap.Get(InkButton.EraserEnd) == InputAction.Eraser)
        {
            _temporaryAction = null;
            ToolChanged?.Invoke(CurrentTool);
        }
    }
}
