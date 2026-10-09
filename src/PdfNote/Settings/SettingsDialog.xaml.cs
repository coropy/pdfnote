using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace PdfNote.Settings;

/// <summary>
/// 設定ダイアログ (ペンボタン割当 + カスタムショートカット)。
/// 保存時は AppSettings を返すだけ (適用・永続化は MainWindow 側の設定境界で行う)。
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    /// <summary>キーキャプチャ中フラグ (取得中は通常操作と誤認されないよう全キーを握る)。</summary>
    private bool _capturing;

    private int _capturedKey;
    private int _capturedModifiers;

    /// <summary>既存ショートカット (組み込み + 登録済みカスタム) との競合検証用。</summary>
    private readonly HashSet<(int Key, int Modifiers)> _reservedKeys;

    /// <summary>ダイアログ内で編集中のカスタムショートカット。</summary>
    private readonly List<(string Command, int Key, int Modifiers)> PendingShortcuts = new();

    /// <summary>保存が押された場合のみ null 以外。MainWindow が適用・保存する。</summary>
    public AppSettings? Result { get; private set; }

    public SettingsDialog(AppSettings current, IEnumerable<(int Key, int Modifiers)> reservedKeys)
    {
        InitializeComponent();
        _reservedKeys = new HashSet<(int, int)>(reservedKeys);
        foreach (var shortcut in current.CustomShortcuts)
        {
            PendingShortcuts.Add(shortcut);
            _reservedKeys.Add((shortcut.Key, shortcut.Modifiers));
        }

        PrimaryButtonClick += OnPrimaryClick;

        // ペンボタン候補 (実装済みのみ: RightClick / CustomShortcut は除外)。
        foreach (var (action, label) in ButtonActionOptions())
        {
            BarrelCombo.Items.Add(new ComboBoxItem { Content = label, Tag = action.ToString() });
            EraserEndCombo.Items.Add(new ComboBoxItem { Content = label, Tag = action.ToString() });
        }

        SelectAction(BarrelCombo, current.BarrelAction);
        SelectAction(EraserEndCombo, current.EraserEndAction);

        foreach (var command in AppCommands.All)
        {
            CommandCombo.Items.Add(new ComboBoxItem { Content = command.DisplayName, Tag = command.Id });
        }

        RefreshShortcutList();
    }

    private static List<(Input.InputAction Action, string Label)> ButtonActionOptions() => new()
    {
        (Input.InputAction.None, "なし"),
        (Input.InputAction.Pen, "ペン (押している間)"),
        (Input.InputAction.Eraser, "消しゴム (押している間)"),
        (Input.InputAction.TemporaryEraser, "消しゴム (押している間だけ)"),
        (Input.InputAction.Highlighter, "蛍光ペン (押している間・β)"),
        (Input.InputAction.Undo, "元に戻す"),
        (Input.InputAction.Redo, "やり直し"),
        (Input.InputAction.PreviousPage, "前のページ"),
        (Input.InputAction.NextPage, "次のページ"),
        (Input.InputAction.Pan, "パン (押している間)"),
    };

    private static void SelectAction(ComboBox combo, Input.InputAction action)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Tag as string == action.ToString())
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static Input.InputAction SelectedAction(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
        Enum.TryParse(tag, ignoreCase: true, out Input.InputAction action)
            ? action
            : Input.InputAction.None;

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        SelectAction(BarrelCombo, Input.InputAction.TemporaryEraser);
        SelectAction(EraserEndCombo, Input.InputAction.Eraser);
    }


    // ------------------------------------------------------------
    // カスタムショートカット。
    // ------------------------------------------------------------

    /// <summary>キー入力の取得を開始する (取得中は通常操作と誤認されないよう全キーを握る)。</summary>
    private void OnKeyCaptureClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        _capturedKey = 0;
        _capturedModifiers = 0;
        KeyCaptureButton.Content = "キーを押してください (Esc = キャンセル)";
        KeyCaptureButton.Focus(FocusState.Programmatic);
    }

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            // Escで入力取得をキャンセル。
            _capturing = false;
            KeyCaptureButton.Content = "クリックしてキーを指定";
            ShortcutMessage.Text = string.Empty;
            return;
        }

        // 修飾キー単体はまだ「組み合わせ」として無効 (押し続けて他のキーを待つ)。
        if (e.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
            VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            return;
        }

        var modifiers = CurrentModifiers();
        if (modifiers == VirtualKeyModifiers.None)
        {
            ShortcutMessage.Text = "修飾キー (Ctrl / Shift / Alt / Win) と組み合わせてください。";
            return;
        }

        _capturedKey = (int)e.Key;
        _capturedModifiers = (int)modifiers;
        _capturing = false;
        KeyCaptureButton.Content = DescribeKey(_capturedKey, _capturedModifiers);
        ShortcutMessage.Text = string.Empty;
    }

    private static VirtualKeyModifiers CurrentModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= VirtualKeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= VirtualKeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= VirtualKeyModifiers.Menu;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= VirtualKeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) != 0;

    private void OnAddShortcutClick(object sender, RoutedEventArgs e)
    {
        if (CommandCombo.SelectedItem is not ComboBoxItem commandItem || commandItem.Tag is not string commandId)
        {
            ShortcutMessage.Text = "コマンドを選択してください。";
            return;
        }

        if (_capturedKey == 0)
        {
            ShortcutMessage.Text = "キーを指定してください。";
            return;
        }

        var combo = (_capturedKey, _capturedModifiers);
        if (_reservedKeys.Contains(combo))
        {
            ShortcutMessage.Text = "そのキーは既に別の操作に使われています。別の組み合わせを指定してください。";
            return;
        }

        _reservedKeys.Add(combo);
        PendingShortcuts.Add((commandId, _capturedKey, _capturedModifiers));
        ShortcutMessage.Text = "追加しました。";
        RefreshShortcutList();
    }

    private void OnRemoveShortcutClick(object sender, RoutedEventArgs e)
    {
        var index = ShortcutList.SelectedIndex;
        if (index < 0 || index >= PendingShortcuts.Count)
        {
            ShortcutMessage.Text = "削除する項目を選択してください。";
            return;
        }

        var removed = PendingShortcuts[index];
        PendingShortcuts.RemoveAt(index);
        _reservedKeys.Remove((removed.Key, removed.Modifiers));
        RefreshShortcutList();
    }

    private void RefreshShortcutList()
    {
        ShortcutList.Items.Clear();
        foreach (var shortcut in PendingShortcuts)
        {
            var command = AppCommands.DisplayName(shortcut.Command) ?? shortcut.Command;
            ShortcutList.Items.Add($"{DescribeKey(shortcut.Key, shortcut.Modifiers)} → {command}");
        }
    }

    private static string DescribeKey(int key, int modifiers)
    {
        var text = new System.Text.StringBuilder();
        if ((modifiers & (int)VirtualKeyModifiers.Control) != 0)
        {
            text.Append("Ctrl+");
        }

        if ((modifiers & (int)VirtualKeyModifiers.Shift) != 0)
        {
            text.Append("Shift+");
        }

        if ((modifiers & (int)VirtualKeyModifiers.Menu) != 0)
        {
            text.Append("Alt+");
        }

        if ((modifiers & (int)VirtualKeyModifiers.Windows) != 0)
        {
            text.Append("Win+");
        }

        text.Append(((VirtualKey)key).ToString());
        return text.ToString();
    }

    // ------------------------------------------------------------
    // 保存 (適用は MainWindow 側)。
    // ------------------------------------------------------------

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Result = new AppSettings
        {
            BarrelAction = SelectedAction(BarrelCombo),
            EraserEndAction = SelectedAction(EraserEndCombo),
        };
        foreach (var shortcut in PendingShortcuts)
        {
            Result.CustomShortcuts.Add(shortcut);
        }
    }
}
