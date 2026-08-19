using System;
using System.Collections.Generic;

namespace El.Core
{
    /// <summary>Подбор текстовых подписей рядом с концами линий цепи.</summary>
    public static class ChainTexts
    {
        /// <summary>Все подписи в радиусе radius от любого конца цепи (без дубликатов).</summary>
        public static List<string> NearEnds(WireGraph graph, List<int> chain, IReadOnlyList<TextLabel> labels, double radius)
        {
            var grid = new SpatialGrid<TextLabel>(Math.Max(radius, 10.0));
            foreach (var lb in labels)
                grid.Add(lb.Position, lb);

            var seen = new HashSet<string>();
            var result = new List<string>();
            double r2 = radius * radius;

            foreach (int id in chain)
            {
                var l = graph.GetLine(id);
                if (l == null) continue;
                CollectNear(l.A, grid, r2, seen, result);
                CollectNear(l.B, grid, r2, seen, result);
            }
            return result;
        }

        /// <summary>Подписи у конкретной точки.</summary>
        public static List<string> NearPoint(Point2D p, IReadOnlyList<TextLabel> labels, double radius)
        {
            var result = new List<string>();
            double r2 = radius * radius;
            foreach (var lb in labels)
            {
                if (p.Dist2(lb.Position) <= r2)
                    result.Add(lb.Text);
            }
            return result;
        }

        private static void CollectNear(Point2D p, SpatialGrid<TextLabel> grid, double r2,
                                        HashSet<string> seen, List<string> result)
        {
            foreach (var lb in grid.QueryNear(p))
            {
                if (p.Dist2(lb.Position) <= r2 && seen.Add(lb.Text))
                    result.Add(lb.Text);
            }
        }
    }
}
