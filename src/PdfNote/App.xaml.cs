using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace PdfNote;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // 予期しない例外でもアプリを落とさない (授業中のデータ保護優先)。
        // 例外内容はローカルログに記録するのみ (通信なし)。
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            LogError(args.Message, args.Exception);
        };
    }

    internal static void LogError(string message, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PdfNote");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n{exception}\n\n");
        }
        catch
        {
            // ログ書き込み失敗は無視。
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
