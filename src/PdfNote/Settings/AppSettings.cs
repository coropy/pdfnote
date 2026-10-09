using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PdfNote.Settings;

/// <summary>アプリ設定のDTO。JSONシリアライズ専用 (内部状態は AppSettings が保持)。</summary>
public sealed class AppSettingsDto
{
    /// <summary>設定フォーマットバージョン。欠損は null → 不明バージョンとして拒否 (.note と同じ規約)。</summary>
    [JsonPropertyName("formatVersion")]
    public int? FormatVersion { get; set; }

    [JsonPropertyName("barrelAction")]
    public string? BarrelAction { get; set; }

    [JsonPropertyName("eraserEndAction")]
    public string? EraserEndAction { get; set; }

    [JsonPropertyName("customShortcuts")]
    public List<ShortcutDto>? CustomShortcuts { get; set; }
}

/// <summary>カスタムショートカット1件分。</summary>
public sealed class ShortcutDto
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>Windows VirtualKey の数値。</summary>
    [JsonPropertyName("key")]
    public int Key { get; set; }

    /// <summary>VirtualKeyModifiers の数値。</summary>
    [JsonPropertyName("modifiers")]
    public int Modifiers { get; set; }
}

/// <summary>アプリ設定 (メモリモデル)。</summary>
public sealed class AppSettings
{
    public Input.InputAction BarrelAction { get; set; } = Input.InputAction.TemporaryEraser;
    public Input.InputAction EraserEndAction { get; set; } = Input.InputAction.Eraser;

    /// <summary>カスタムショートカット (コマンドID → キー割当)。</summary>
    public List<(string Command, int Key, int Modifiers)> CustomShortcuts { get; } = new();

    /// <summary>既定値に戻す。</summary>
    public void ResetToDefaults()
    {
        BarrelAction = Input.InputAction.TemporaryEraser;
        EraserEndAction = Input.InputAction.Eraser;
        CustomShortcuts.Clear();
    }
}
