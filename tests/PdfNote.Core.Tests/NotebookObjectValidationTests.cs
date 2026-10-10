using System.Numerics;
using PdfNote.Core;
using PdfNote.Ink;
using PdfNote.Storage;

namespace PdfNote.Core.Tests;

/// <summary>段階5 ステップ1: オブジェクト検証 (回転 / 寸法 / ソフト上限) の検証。</summary>
internal static class NotebookObjectValidationTests
{
    internal static void RotationAndSizeValidation(Action<bool, string> check)
    {
        Console.WriteLine("RotationAndSizeValidation");
        var obj = new PdfObject(Guid.NewGuid(), Guid.NewGuid(), 0, 0f, 0f, 100f, 50f, 0, 0);
        check(PageObjectLimits.IsAllowedRotation(270) && !PageObjectLimits.IsAllowedRotation(45), "許容回転の判定");

        var threw = false;
        try
        {
            obj.SetRotation(45);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        check(threw && obj.RotationDeg == 0, "不正回転は拒否され値は不変");

        obj.SetRotation(270);
        check(obj.RotationDeg == 270, "270° は設定可能");

        threw = false;
        try
        {
            obj.SetSize(0f, 10f);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        check(threw, "寸法 0 は拒否");
    }

    internal static void SoftCapDoesNotBlockModel(Action<bool, string> check)
    {
        Console.WriteLine("SoftCapDoesNotBlockModel");
        check(PageObjectLimits.MaxObjectsPerPage == 10, "ソフト上限は 10");
        check(PageObjectLimits.MaxResourceSessions == 4, "リソース束上限は 4");

        // ソフト上限は UI の警告仕様であり、モデル / シリアライズは 10 超でも保存・復元できる。
        var page = new NotebookPage(0, 595f, 842f, PageBackgroundKind.Plain);
        var resourceId = Guid.NewGuid();
        for (var i = 0; i < 12; i++)
        {
            page.Objects.Add(new PdfObject(Guid.NewGuid(), resourceId, 0, i, i, 10f, 10f, 0, i));
        }

        var notebook = new Notebook(Guid.NewGuid(), "t");
        notebook.Resources.Add(new Resource(resourceId, "Pdf", "assets/x.pdf", 1, DateTime.UtcNow));
        notebook.Pages.Add(page);
        var restored = NotebookSerializer.ToNotebook(NotebookSerializer.ToFile(notebook));
        check(restored.Pages[0].Objects.Count == 12, "10 超の保存 / 復元 (ソフト上限は UI 責務)");
    }
}
