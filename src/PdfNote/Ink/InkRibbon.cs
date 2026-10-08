using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace PdfNote.Ink;

/// <summary>
/// ストロークを「可変幅リボン」として描画するレンダラー。
/// 1) Catmull-Rom スプラインで中心線を平滑化 (分割数でカーブを補間)
/// 2) 各点の幅 (筆圧由来) で法線方向にオフセットしたリボンを生成
/// 3) 両端を丸キャップで閉じる
/// → 線分の継ぎ目が見えず、カーブも滑らかで筆圧による抜け感が出る。
/// </summary>
public static class InkRibbon
{
    /// <summary>セグメントあたりのスプライン分割数。</summary>
    private const int SmoothSubdivisions = 4;

    /// <summary>丸キャップの分割数。</summary>
    private const int CapSegments = 8;

    private const float MinWidthPt = 0.35f;
    private const float MergeDistancePt = 0.2f;

    /// <summary>ストロークを滑らかなリボン形状で塗りつぶす。</summary>
    public static void Draw(CanvasDrawingSession session, Stroke stroke)
    {
        var points = stroke.Points;
        var count = points.Count;
        if (count == 0)
        {
            return;
        }

        var color = Color.FromArgb(stroke.A, stroke.R, stroke.G, stroke.B);

        if (count == 1)
        {
            session.FillCircle(points[0].Position, Math.Max(0.5f, stroke.WidthPt * 0.5f), color);
            return;
        }

        var geometry = CreateGeometry(session, stroke);
        using (geometry)
        {
            session.FillGeometry(geometry, color);
        }
    }

    /// <summary>ストロークのリボン形状ジオメトリを生成する。</summary>
    public static CanvasGeometry CreateGeometry(ICanvasResourceCreator creator, Stroke stroke)
    {
        var points = stroke.Points;

        // 1) Catmull-Rom で中心線を平滑化し、幅も補間する。
        var positions = new List<Vector2>(points.Count * SmoothSubdivisions + 2);
        var widths = new List<float>(points.Count * SmoothSubdivisions + 2);

        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)].Position;
            var p1 = points[i].Position;
            var p2 = points[i + 1].Position;
            var p3 = points[Math.Min(i + 2, points.Count - 1)].Position;
            var w1 = WidthOf(points[i]);
            var w2 = WidthOf(points[i + 1]);

            for (var s = 0; s < SmoothSubdivisions; s++)
            {
                var t = s / (float)SmoothSubdivisions;
                positions.Add(CatmullRom(p0, p1, p2, p3, t));
                widths.Add(w1 + (w2 - w1) * t);
            }
        }

        positions.Add(points[points.Count - 1].Position);
        widths.Add(WidthOf(points[points.Count - 1]));

        // 2) 近接点の統合 + 幅の平滑化。
        MergeClosePoints(positions, widths);
        SmoothWidths(widths);

        var centerCount = positions.Count;
        if (centerCount < 2)
        {
            // 退化 (極短ストローク) は円で描く。
            positions.Clear();
            positions.Add(points[0].Position);
            return CircleGeometry(creator, points[0].Position, Math.Max(0.5f, stroke.WidthPt * 0.5f));
        }

        // 3) 法線方向オフセットでリボンを生成。
        var normals = new Vector2[centerCount];
        ComputeNormals(positions, normals);

        var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        builder.BeginFigure(positions[0] + normals[0] * widths[0] * 0.5f);

        for (var i = 1; i < centerCount; i++)
        {
            builder.AddLine(positions[i] + normals[i] * widths[i] * 0.5f);
        }

        // 終端キャップ (進行方向に半円で膨らませる)。
        var endTangent = SafeTangent(positions, centerCount - 1, -1);
        var endNormal = normals[centerCount - 1];
        for (var s = 1; s < CapSegments; s++)
        {
            var theta = MathF.PI * s / CapSegments;
            var capPoint = positions[centerCount - 1]
                + endNormal * (MathF.Cos(theta) * widths[centerCount - 1] * 0.5f)
                + endTangent * (MathF.Sin(theta) * widths[centerCount - 1] * 0.5f);
            builder.AddLine(capPoint);
        }

        for (var i = centerCount - 1; i >= 0; i--)
        {
            builder.AddLine(positions[i] - normals[i] * widths[i] * 0.5f);
        }

        // 始端キャップ (逆方向に半円)。
        var startTangent = SafeTangent(positions, 0, 1);
        var startNormal = normals[0];
        for (var s = CapSegments - 1; s >= 1; s--)
        {
            var theta = MathF.PI * s / CapSegments;
            var capPoint = positions[0]
                + startNormal * (MathF.Cos(theta) * widths[0] * 0.5f)
                - startTangent * (MathF.Sin(theta) * widths[0] * 0.5f);
            builder.AddLine(capPoint);
        }

        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static float WidthOf(InkPoint point)
    {
        return Math.Max(MinWidthPt, Stroke.BaseWidthPt * InkRenderer.PressureToWidthFactor(point.Pressure));
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return 0.5f * (2f * p1
            + (-p0 + p2) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static void MergeClosePoints(List<Vector2> positions, List<float> widths)
    {
        var write = 0;
        for (var read = 0; read < positions.Count; read++)
        {
            if (write == 0 || Vector2.DistanceSquared(positions[read], positions[write - 1]) > MergeDistancePt * MergeDistancePt)
            {
                positions[write] = positions[read];
                widths[write] = widths[read];
                write++;
            }
        }

        if (write < positions.Count)
        {
            positions.RemoveRange(write, positions.Count - write);
            widths.RemoveRange(write, widths.Count - write);
        }
    }

    private static void SmoothWidths(List<float> widths)
    {
        var count = widths.Count;
        if (count < 3)
        {
            return;
        }

        var previous = widths[0];
        for (var i = 1; i < count - 1; i++)
        {
            var current = widths[i];
            widths[i] = (previous + 2f * current + widths[i + 1]) * 0.25f;
            previous = current;
        }
    }

    private static void ComputeNormals(List<Vector2> positions, Vector2[] normals)
    {
        var count = positions.Count;
        var tangent = new Vector2(0f, 1f);

        for (var i = 0; i < count; i++)
        {
            var next = positions[Math.Min(i + 1, count - 1)];
            var previous = positions[Math.Max(i - 1, 0)];
            var direction = next - previous;
            var length = direction.Length();
            if (length > 0.0001f)
            {
                tangent = direction / length;
            }
            // 純粋な法線 (中心線に対して左側)。
            normals[i] = new Vector2(-tangent.Y, tangent.X);
        }
    }

    private static Vector2 SafeTangent(List<Vector2> positions, int index, int direction)
    {
        var other = index + direction;
        if (other < 0)
        {
            other = 0;
        }
        if (other >= positions.Count)
        {
            other = positions.Count - 1;
        }

        var t = positions[other] - positions[index];
        var length = t.Length();
        if (length < 0.0001f)
        {
            return new Vector2(0f, 1f);
        }

        return t / length;
    }

    private static CanvasGeometry CircleGeometry(ICanvasResourceCreator creator, Vector2 center, float radius)
    {
        var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        builder.BeginFigure(center + new Vector2(radius, 0f));
        const int segments = 12;
        for (var s = 1; s <= segments; s++)
        {
            var angle = MathF.PI * 2f * s / segments;
            builder.AddLine(center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius);
        }
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }
}

