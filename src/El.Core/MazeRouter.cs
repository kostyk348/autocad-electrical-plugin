using System;
using System.Collections.Generic;
using System.Linq;

namespace El.Core
{
    /// <summary>Результат трассировки.</summary>
    public sealed class RouteResult
    {
        public bool Found;                                  // путь найден
        public List<Point2D> Points = new List<Point2D>();  // путь (включая A и B)
        public List<Point2D> Crossings = new List<Point2D>(); // неизбежные пересечения (если путь проложен с проколами)
        public bool GridCoarsened;                          // сетка укрупнена, чтобы влезть в бюджет ячеек
        public double CellUsed;                             // фактический шаг сетки
        public int GridCells;                               // фактическое число ячеек
        public double Length => PathLength(Points);

        public static double PathLength(IReadOnlyList<Point2D> pts)
        {
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += pts[i].Dist(pts[i - 1]);
            return len;
        }
    }

    /// <summary>
    /// Полноценный лабиринтный роутер (алгоритм Ли / BFS по сетке):
    /// - растровая сетка; ячейки, задетые препятствиями, блокируются;
    /// - BFS (4-связный) от точки A к точке B — гарантированный обход препятствий;
    /// - восстановление пути и упрощение («string pulling»): промежуточные точки
    ///   выбрасываются, если прямая не пересекает препятствия;
    /// - если пути нет (тупик) — Found=false: вызывающий рисует стрелки-переходы.
    /// </summary>
    public static class MazeRouter
    {
        /// <summary>Жёсткий бюджет сетки: 4 млн ячеек (bool[] 4 МБ + int[] 16 МБ) — потолок памяти.</summary>
        public const int MaxGridCells = 4000000;
        /// <summary>Не больше 4096 проб на препятствие: длинная линия не превращается в миллион итераций.</summary>
        public const int MaxSamplesPerObstacle = 4096;
        /// <summary>Бюджет проверок «отрезок свободен» при сглаживании — ограничивает время сверху.</summary>
        public const int MaxSimplifyTests = 2000000;

        /// <param name="a">начало</param>
        /// <param name="b">конец</param>
        /// <param name="obstacles">существующие провода (препятствия)</param>
        /// <param name="cell">шаг сетки (ортогональная «канавка» провода)</param>
        /// <param name="tol">допуск стыковки (касания концов не считаются пересечениями)</param>
        public static RouteResult Route(Point2D a, Point2D b, IReadOnlyList<LineSeg> obstacles, double cell, double tol)
        {
            var res = new RouteResult();
            if (cell <= 0) cell = 5.0;

            double pad = Math.Max(cell * 6, 30.0);
            double minX = Math.Min(a.X, b.X) - pad;
            double maxX = Math.Max(a.X, b.X) + pad;
            double minY = Math.Min(a.Y, b.Y) - pad;
            double maxY = Math.Max(a.Y, b.Y) + pad;
            double width = Math.Max(1e-6, maxX - minX);
            double height = Math.Max(1e-6, maxY - minY);
            double cellEff = cell;
            // Бюджет ячеек: длинный обход мелким шагом не должен съесть память (O(cols*rows)).
            double estCells = Math.Ceiling(width / cellEff) * Math.Ceiling(height / cellEff);
            if (estCells > MaxGridCells)
            {
                cellEff = Math.Sqrt(width * height / MaxGridCells);
                if (cellEff < cell) cellEff = cell;
                res.GridCoarsened = true;
            }
            // ceil() округляет вверх — добиваем шаг, пока произведение не влезет в бюджет
            // (цикл ограничен 64 итерациями, рост шага геометрический: сходится всегда)
            int cols = 3, rows = 3;
            for (int guard = 0; guard < 64; guard++)
            {
                cols = Math.Max(3, (int)Math.Ceiling(width / cellEff));
                rows = Math.Max(3, (int)Math.Ceiling(height / cellEff));
                if ((long)cols * rows <= MaxGridCells) break;
                cellEff *= 1.05;
            }
            res.CellUsed = cellEff;
            res.GridCells = cols * rows;

            // блокировка ячеек вдоль препятствий
            bool[] blocked = new bool[cols * rows];
            foreach (var o in obstacles)
            {
                double len = o.Length;
                if (len < 1e-9) continue;
                int steps = Math.Max(2, (int)(len / (cellEff * 0.5)));
                if (steps > MaxSamplesPerObstacle) steps = MaxSamplesPerObstacle;
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    double px = o.A.X + (o.B.X - o.A.X) * t;
                    double py = o.A.Y + (o.B.Y - o.A.Y) * t;
                    int cx = (int)Math.Floor((px - minX) / cellEff);
                    int cy = (int)Math.Floor((py - minY) / cellEff);
                    if (cx >= 0 && cx < cols && cy >= 0 && cy < rows)
                        blocked[cy * cols + cx] = true;
                }
            }

            int ca = Cell(a, minX, minY, cellEff, cols, rows);
            int cb = Cell(b, minX, minY, cellEff, cols, rows);

            if (ca < 0 || cb < 0 || blocked[ca] || blocked[cb])
            {
                // концы на препятствии — всё равно пытаемся (стыковка к проводу)
                blocked[ca] = false;
                blocked[cb] = false;
            }

            // BFS 4-связный
            int[] parent = new int[cols * rows];
            for (int i = 0; i < parent.Length; i++) parent[i] = -1;
            var queue = new Queue<int>();
            queue.Enqueue(ca);
            parent[ca] = ca;
            bool found = false;
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            while (queue.Count > 0 && !found)
            {
                int cur = queue.Dequeue();
                int cx = cur % cols, cy = cur / cols;
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d], ny = cy + dy[d];
                    if (nx < 0 || nx >= cols || ny < 0 || ny >= rows) continue;
                    int ni = ny * cols + nx;
                    if (blocked[ni] || parent[ni] != -1) continue;
                    parent[ni] = cur;
                    if (ni == cb) { found = true; break; }
                    queue.Enqueue(ni);
                }
            }

            if (!found)
            {
                res.Found = false;
                return res;
            }

            // восстановление пути (центры ячеек) — в обратном порядке
            var cellPath = new List<Point2D>();
            int node = cb;
            while (node != ca)
            {
                int cx = node % cols, cy = node / cols;
                cellPath.Add(new Point2D(minX + (cx + 0.5) * cellEff, minY + (cy + 0.5) * cellEff));
                node = parent[node];
            }
            cellPath.Reverse();

            // путь: A → центры → B
            var path = new List<Point2D> { a };
            path.AddRange(cellPath);
            path.Add(b);

            // сглаживание (string pulling)
            var simplified = Simplify(path, obstacles, tol);

            res.Found = true;
            res.Points = simplified;
            // пересечений нет по построению (BFS обходит препятствия) — оставляем список пустым
            return res;
        }

        private static int Cell(Point2D p, double minX, double minY, double cell, int cols, int rows)
        {
            int cx = (int)Math.Floor((p.X - minX) / cell);
            int cy = (int)Math.Floor((p.Y - minY) / cell);
            if (cx < 0 || cy < 0 || cx >= cols || cy >= rows) return -1;
            return cy * cols + cx;
        }

        /// <summary>
        /// Жадное упрощение: от текущей точки ищем самую дальнюю точку,
        /// до которой прямая не пересекает препятствия (касания концов — ок).
        /// </summary>
        public static List<Point2D> Simplify(List<Point2D> path, IReadOnlyList<LineSeg> obstacles, double tol)
        {
            if (path.Count <= 2) return path;
            double tol2 = tol * tol;
            var result = new List<Point2D>();
            // Бюджет проверок: время сглаживания ограничено сверху даже на 100-тысячной ломаной.
            int budget = MaxSimplifyTests;
            int i = 0;
            while (i < path.Count - 1)
            {
                result.Add(path[i]);
                int farthest = i + 1;
                if (budget > 0)
                {
                    for (int j = path.Count - 1; j > i; j--)
                    {
                        budget -= (obstacles == null ? 1 : obstacles.Count + 1);
                        if (budget <= 0) break;
                        if (ClearSegment(path[i], path[j], obstacles, tol2))
                        {
                            farthest = j;
                            break;
                        }
                    }
                }
                if (farthest <= i) farthest = i + 1;
                i = farthest;
            }
            result.Add(path[path.Count - 1]);

            // убрать коллинеарные точки
            var final = new List<Point2D>();
            for (int k = 0; k < result.Count; k++)
            {
                if (k >= 2)
                {
                    var p0 = final[final.Count - 2];
                    var p1 = final[final.Count - 1];
                    var p2 = result[k];
                    if (Math.Abs(Cross(p1, p0, p2)) < 1e-6 && Math.Sign(p2.X - p1.X) == Math.Sign(p1.X - p0.X) &&
                        Math.Sign(p2.Y - p1.Y) == Math.Sign(p1.Y - p0.Y))
                    {
                        final.RemoveAt(final.Count - 1);
                    }
                }
                final.Add(result[k]);
            }
            return final;
        }

        private static double Cross(Point2D o, Point2D a, Point2D b)
        {
            return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        }

        private static bool ClearSegment(Point2D p, Point2D q, IReadOnlyList<LineSeg> obstacles, double tol2)
        {
            if (p.Dist2(q) < 1e-12) return true;
            foreach (var o in obstacles)
            {
                var pt = GraphAlgorithms.SegmentIntersect(p, q, o.A, o.B);
                if (pt == null) continue;
                // касание концов отрезка пути или концов препятствия — не пересечение
                if (pt.Value.Dist2(p) < tol2 || pt.Value.Dist2(q) < tol2) continue;
                if (pt.Value.Dist2(o.A) < tol2 || pt.Value.Dist2(o.B) < tol2) continue;
                return false;
            }
            return true;
        }
    }
}
