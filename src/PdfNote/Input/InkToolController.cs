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

    /// <summary>バレルボタンのアクション (InkSession からの互換取得用)。</summary>
    public InputAction BarrelAction => _buttonMap.Get(InkButton.Barrel);

    /// <summary>パンが一時アクションとして有効 (押している間パン)。</summary>
    public bool IsPanTemporary => _temporaryAction == InputAction.Pan;

    /// <summary>何らかの一時アクション中か。</summary>
    public bool IsTemporaryActive => _temporaryAction is not null;

    private InkTool _baseTool = InkTool.Pen;

    /// <summary>一時アクション (押している間だけ有効)。</summary>
    private InputAction? _temporaryAction;

    /// <summary>一時アクションの発生源 (ボタン/消しゴム端)。設定変更中でも解放時に正しく復帰させるために保持する。</summary>
    private InkButton? _temporarySource;

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

    /// <summary>
    /// 設定UIからボタン割当を変更する (設定変更の唯一の入口)。
    /// 実行中の一時アクションは「押している間」はそのまま維持し、解放時に復帰する。
    /// </summary>
    public void ApplyButtonAssignment(InkButton button, InputAction action)
    {
        if (!PenButtonMap.IsSupported(action))
        {
            return;
        }

        _buttonMap.Set(button, action);
    }

    /// <summary>既定の割当 (バレル=TemporaryEraser, 消しゴム端=Eraser) に戻す。</summary>
    public void ResetButtonAssignments()
    {
        _buttonMap.ResetToDefaults();
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
                    _temporarySource = button;
                    ToolChanged?.Invoke(CurrentTool);
                }
                break;

            case InputAction.Undo:
            case InputAction.Redo:
            case InputAction.PreviousPage:
            case InputAction.NextPage:
                ActionRequested?.Invoke(action);
                break;
        }
    }

    /// <summary>ボタン解放時: 一時アクションの終了。発生源を見るため設定変更中でも安全。</summary>
    public void ButtonUp(InkButton button)
    {
        if (_temporarySource == button)
        {
            _temporaryAction = null;
            _temporarySource = null;
            ToolChanged?.Invoke(CurrentTool);
        }
    }

    /// <summary>裏消しゴム端の接触による一時ツール切替 (割当が Eraser 相当のとき)。</summary>
    public void SetEraserEndActive(bool active)
    {
        if (active)
        {
            if (_temporaryAction is null)
            {
                // 消しゴム端はハードウェアとして「消す」機能のため、割当が None でも Eraser を優先する。
                var assigned = _buttonMap.Get(InkButton.EraserEnd);
                _temporaryAction = assigned == InputAction.Eraser || assigned == InputAction.TemporaryEraser
                    ? assigned
                    : InputAction.Eraser;
                _temporarySource = InkButton.EraserEnd;
                ToolChanged?.Invoke(CurrentTool);
            }
        }
        else
        {
            ButtonUp(InkButton.EraserEnd);
        }
    }
}
