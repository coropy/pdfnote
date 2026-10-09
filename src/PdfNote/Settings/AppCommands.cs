using System.Collections.Generic;

namespace PdfNote.Settings;

/// <summary>カスタムショートカットに割り当て可能なコマンド (実装済みのみ)。</summary>
public static class AppCommands
{
    /// <summary>コマンド定義 (ID / 表示名)。</summary>
    public sealed record Command(string Id, string DisplayName);

    public const string OpenPdf = "openPdf";
    public const string Undo = "undo";
    public const string Redo = "redo";
    public const string PreviousPage = "previousPage";
    public const string NextPage = "nextPage";
    public const string FirstPage = "firstPage";
    public const string LastPage = "lastPage";
    public const string FitToViewport = "fitToViewport";
    public const string ZoomIn = "zoomIn";
    public const string ZoomOut = "zoomOut";
    public const string PenTool = "penTool";
    public const string EraserTool = "eraserTool";
    public const string TogglePerfOverlay = "togglePerfOverlay";
    public const string ToggleToolbar = "toggleToolbar";

    /// <summary>登録可能な全コマンド (実行可能なもののみ。設定UIの候補になる)。</summary>
    public static readonly IReadOnlyList<Command> All = new[]
    {
        new Command(PreviousPage, "前のページ"),
        new Command(NextPage, "次のページ"),
        new Command(FirstPage, "最初のページ"),
        new Command(LastPage, "最後のページ"),
        new Command(Undo, "元に戻す (Undo)"),
        new Command(Redo, "やり直し (Redo)"),
        new Command(ZoomIn, "ズームイン"),
        new Command(ZoomOut, "ズームアウト"),
        new Command(FitToViewport, "ページを画面に合わせる"),
        new Command(OpenPdf, "PDFを開く"),
        new Command(PenTool, "ペンツール"),
        new Command(EraserTool, "消しゴムツール"),
        new Command(TogglePerfOverlay, "FPS表示の切り替え"),
        new Command(ToggleToolbar, "ツールバー表示の切り替え"),
    };

    public static bool Exists(string commandId)
    {
        foreach (var command in All)
        {
            if (command.Id == commandId)
            {
                return true;
            }
        }

        return false;
    }

    public static string? DisplayName(string commandId)
    {
        foreach (var command in All)
        {
            if (command.Id == commandId)
            {
                return command.DisplayName;
            }
        }

        return null;
    }
}
