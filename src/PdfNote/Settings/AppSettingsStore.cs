using System;
using System.IO;
using System.Text.Json;

namespace PdfNote.Settings;

/// <summary>設定ファイルが読めない/不正な場合。</summary>
public sealed class SettingsLoadException : Exception
{
    public SettingsLoadException(string message) : base(message) { }
}

/// <summary>
/// アプリ設定の永続化。%LOCALAPPDATA%\PdfNote\settings.json
/// 原子保存 (.tmp → Flush → Replace)、バージョン検証、破損時は既定値で復旧。
/// ユーザー固有情報 (パス、アカウント等) は記録しない。
/// </summary>
public static class AppSettingsStore
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>テスト用の保存先ディレクトリ差し替え (null = 通常の %LOCALAPPDATA%\PdfNote)。</summary>
    internal static Func<string>? SettingsDirectoryOverride { get; set; }

    public static string SettingsPath
    {
        get
        {
            var directory = SettingsDirectoryOverride?.Invoke() ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PdfNote");
            return Path.Combine(directory, "settings.json");
        }
    }

    /// <summary>設定を読み込む。破損/不明バージョンの場合は既定値を返し警告メッセージを出す。</summary>
    public static AppSettings Load(out string? warning)
    {
        warning = null;
        var path = SettingsPath;

        if (!File.Exists(path))
        {
            return new AppSettings(); // 初回起動時は既定値。
        }

        AppSettingsDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<AppSettingsDto>(File.ReadAllText(path))
                  ?? throw new SettingsLoadException("設定ファイルが空です。");
        }
        catch (Exception ex) when (ex is JsonException or SettingsLoadException or IOException)
        {
            warning = "設定ファイルが壊れているため既定値を使用します。";
            App.LogError("設定ファイルの読み込みに失敗しました。既定値を使用します。", ex);
            return new AppSettings();
        }

        if (dto.FormatVersion is not { } version || version != CurrentFormatVersion)
        {
            warning = $"設定ファイルのバージョンが不明のため既定値を使用します (version={dto.FormatVersion?.ToString() ?? "欠損"})。";
            return new AppSettings();
        }

        return FromDto(dto);
    }

    /// <summary>設定を保存する (原子保存)。失敗時は例外を投げる (呼び出し側がユーザーへ通知)。</summary>
    public static void Save(AppSettings settings)
    {
        var path = SettingsPath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"設定の保存先が不正です ({path})。");
        }

        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(ToDto(settings), WriteOptions);
        var tempPath = path + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            // 保存失敗時は既存設定ファイルを破壊しない (tmp のみ掃除)。
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 掃除失敗は無視。
            }

            throw;
        }
    }

    // ------------------------------------------------------------
    // DTO 変換 (境界)。未知のアクション名は既定値にフォールバック。
    // ------------------------------------------------------------

    private static AppSettings FromDto(AppSettingsDto dto)
    {
        var settings = new AppSettings();

        if (Enum.TryParse(dto.BarrelAction, ignoreCase: true, out Input.InputAction barrel) &&
            Input.PenButtonMap.IsSupported(barrel))
        {
            settings.BarrelAction = barrel;
        }

        if (Enum.TryParse(dto.EraserEndAction, ignoreCase: true, out Input.InputAction eraserEnd) &&
            Input.PenButtonMap.IsSupported(eraserEnd))
        {
            settings.EraserEndAction = eraserEnd;
        }

        if (dto.CustomShortcuts is { } shortcuts)
        {
            foreach (var shortcut in shortcuts)
            {
                // 空の組み合わせ / 修飾キーのみは拒否 (AppCommands.TryNormalize と同じ規約)。
                if (shortcut.Command is { } command &&
                    shortcut.Key != 0 &&
                    shortcut.Modifiers != 0 &&
                    AppCommands.Exists(command))
                {
                    settings.CustomShortcuts.Add((command, shortcut.Key, shortcut.Modifiers));
                }
            }
        }

        return settings;
    }

    private static AppSettingsDto ToDto(AppSettings settings) => new()
    {
        FormatVersion = CurrentFormatVersion,
        BarrelAction = settings.BarrelAction.ToString(),
        EraserEndAction = settings.EraserEndAction.ToString(),
        CustomShortcuts = [.. settings.CustomShortcuts.ConvertAll(s => new ShortcutDto
        {
            Command = s.Command,
            Key = s.Key,
            Modifiers = s.Modifiers,
        })],
    };
}
