using System.Collections.Generic;
using System.IO;

namespace PdfNote.Core.Tests;

/// <summary>テスト用の最小 PDF (正しい xref 付き) を生成する。</summary>
internal static class TestPdf
{
    /// <summary>pageCount ページの PDF を生成してファイルに書き込む。</summary>
    public static string Write(string path, int pageCount, double widthPt = 200, double heightPt = 100)
    {
        File.WriteAllBytes(path, Create(pageCount, widthPt, heightPt));
        return path;
    }

    public static byte[] Create(int pageCount, double widthPt = 200, double heightPt = 100)
    {
        using var ms = new MemoryStream();
        var offsets = new List<int>();
        void WriteAscii(string text)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            ms.Write(bytes, 0, bytes.Length);
        }

        WriteAscii("%PDF-1.4\n");

        offsets.Add(-1); // 0 番オブジェクトは欠番 (xref の free エントリ)。
        offsets.Add((int)ms.Length);
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets.Add((int)ms.Length);
        var kids = new List<string>();
        for (var i = 0; i < pageCount; i++)
        {
            kids.Add($"{3 + i} 0 R");
        }

        WriteAscii($"2 0 obj\n<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pageCount} >>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            offsets.Add((int)ms.Length);
            WriteAscii(
                $"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPt.ToString(System.Globalization.CultureInfo.InvariantCulture)} {heightPt.ToString(System.Globalization.CultureInfo.InvariantCulture)}] >>\nendobj\n");
        }

        var xrefPos = (int)ms.Length;
        WriteAscii($"xref\n0 {offsets.Count}\n");
        WriteAscii("0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            WriteAscii($"{offsets[i]:D10} 00000 n \n");
        }

        WriteAscii($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
        return ms.ToArray();
    }
}
