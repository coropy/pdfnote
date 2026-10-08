using System;
using System.Numerics;

namespace PdfNote.Ink;

/// <summary>消しゴムのヒットテスト (Stroke 単位消去)。</summary>
public static class InkHitTest
{
    /// <summary>
    /// 点がストロークに当たるか判定。バウンディングボックスで粗く除外してから
    /// セグメント距離で判定する。
    /// </summary>
    /// <param name="stroke">対象ストローク。</param>
    /// <param name="point">消しゴム中心 (pt)。</param>
    /// <param name="radiusPt">消しゴム半径 (pt)。</param>
    public static bool Hit(Stroke stroke, Vector2 point, float radiusPt)
    {
        var points = stroke.Points;
        if (points.Count == 0)
        {
            return false;
        }

        if (points.Count == 1)
        {
            return Vector2.DistanceSquared(points[0].Position, point) <= radiusPt * radiusPt;
        }

        stroke.GetBounds(out var minX, out var minY, out var maxX, out var maxY);
        var margin = radiusPt + stroke.WidthPt;
        if (point.X < minX - margin || point.X > maxX + margin ||
            point.Y < minY - margin || point.Y > maxY + margin)
        {
            return false;
        }

        var threshold = radiusPt + stroke.WidthPt * 0.5f;
        var thresholdSq = threshold * threshold;

        var previous = points[0].Position;
        for (var i = 1; i < points.Count; i++)
        {
            var current = points[i].Position;
            if (DistanceSqToSegment(point, previous, current) <= thresholdSq)
            {
                return true;
            }

            previous = current;
        }

        return false;
    }

    /// <summary>点と線分の距離の2乗。</summary>
    private static float DistanceSqToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared();
        if (lengthSq < 0.0001f)
        {
            return Vector2.DistanceSquared(p, a);
        }

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSq, 0f, 1f);
        var closest = a + ab * t;
        return Vector2.DistanceSquared(p, closest);
    }
}
