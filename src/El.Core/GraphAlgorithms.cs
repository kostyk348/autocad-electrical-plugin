using System;
using System.Collections.Generic;
using System.Linq;

namespace El.Core
{
    /// <summary>Пути в графе и поиск пересечений линий.</summary>
    public static class GraphAlgorithms
    {
        /// <summary>
        /// Кратчайший путь (BFS) от start до target по числу рёбер.
        /// Возвращает список lineId от start до target включительно, или null.
        /// </summary>
        public static List<int> FindPath(WireGraph g, int start, int target)
        {
            if (start == target) return new List<int> { start };
            var parent = new Dictionary<int, int>();
            var queue = new Queue<int>();
            var visited = new HashSet<int> { start };
            queue.Enqueue(start);
            bool found = false;

            while (queue.Count > 0 && !found)
            {
                int cur = queue.Dequeue();
                foreach (int n in g.Neighbors(cur))
                {
                    if (visited.Add(n))
                    {
                        parent[n] = cur;
                        if (n == target) { found = true; break; }
                        queue.Enqueue(n);
                    }
                }
            }
            if (!found) return null;

            var path = new List<int>();
            int node = target;
            while (node != start)
            {
                path.Add(node);
                node = parent[node];
            }
            path.Add(start);
            path.Reverse();
            return path;
        }

        /// <summary>Точка пересечения двух отрезков (или null).</summary>
        public static Point2D? SegmentIntersect(Point2D p1, Point2D p2, Point2D p3, Point2D p4, double eps = 1e-9)
        {
            double rX = p2.X - p1.X, rY = p2.Y - p1.Y;
            double sX = p4.X - p3.X, sY = p4.Y - p3.Y;
            double denom = rX * sY - rY * sX;
            if (Math.Abs(denom) < eps) return null; // параллельны/коллинеарны

            double qX = p3.X - p1.X, qY = p3.Y - p1.Y;
            double t = (qX * sY - qY * sX) / denom;
            double u = (qX * rY - qY * rX) / denom;
            if (t < -eps || t > 1 + eps || u < -eps || u > 1 + eps) return null;
            return new Point2D(p1.X + t * rX, p1.Y + t * rY);
        }

        /// <summary>
        /// Найти истинные пересечения линий (X-образные, НЕ стыковки концов):
        /// точка пересечения не совпадает ни с одним концом (в пределах margin).
        /// </summary>
        public static List<CrossingInfo> FindCrossings(IReadOnlyList<LineSeg> lines, double tolerance)
        {
            double cell = Math.Max(tolerance, 50.0);
            var grid = new SpatialGrid<int>(cell);
            foreach (var l in lines)
            {
                // сегмент может пересекать несколько ячеек — добавляем в ячейку
                // середины и в обе концевые (достаточно для окна ~cell)
                grid.Add(l.A, l.Id);
                grid.Add(l.B, l.Id);
                grid.Add(l.Mid, l.Id);
            }

            double margin = tolerance * 2.0;
            var result = new List<CrossingInfo>();
            var seen = new HashSet<long>();

            foreach (var l in lines)
            {
                foreach (var oid in grid.QueryNear(l.Mid).Union(grid.QueryNear(l.A)).Union(grid.QueryNear(l.B)))
                {
                    if (oid == l.Id) continue;
                    long key = ((long)Math.Min(l.Id, oid) << 32) | (uint)Math.Max(l.Id, oid);
                    if (!seen.Add(key)) continue;
                    var o = lines.First(x => x.Id == oid);
                    var pt = SegmentIntersect(l.A, l.B, o.A, o.B);
                    if (pt == null) continue;
                    var p = pt.Value;
                    // отсекаем стыковки концов и касания
                    if (NearAnyEndpoint(p, l, margin) || NearAnyEndpoint(p, o, margin)) continue;
                    result.Add(new CrossingInfo { LineA = l, LineB = o, Point = p });
                }
            }
            return result;
        }

        private static bool NearAnyEndpoint(Point2D p, LineSeg l, double margin)
        {
            double m2 = margin * margin;
            return p.Dist2(l.A) < m2 || p.Dist2(l.B) < m2;
        }
    }

    public sealed class CrossingInfo
    {
        public LineSeg LineA;
        public LineSeg LineB;
        public Point2D Point;
    }
}
