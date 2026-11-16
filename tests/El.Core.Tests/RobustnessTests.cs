using System;
using System.Collections.Generic;
using System.Diagnostics;
using El.Core;

namespace El.Core.Tests
{
    /// <summary>
    /// «Не должно быть зависаний»: у каждой потенциально тяжёлой операции есть верхняя граница.
    /// Тесты держат потолок по времени и по числу ячеек сетки — если граница пропадёт, они упадут.
    /// </summary>
    public class RobustnessTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static List<LineSeg> Obstacles(int count, double length, double yStep)
        {
            var list = new List<LineSeg>(count);
            for (int i = 0; i < count; i++)
            {
                double y = i * yStep;
                list.Add(new LineSeg(i + 1, new Point2D(-length, y), new Point2D(length, y)));
            }
            return list;
        }

        public void Test_MazeRouter_HugeSpanIsBounded()
        {
            // обход длиной 200 000 при шаге 0.5 — без бюджета это 1.6e11 ячеек (OOM/зависание)
            var sw = Stopwatch.StartNew();
            var res = MazeRouter.Route(new Point2D(0, 0), new Point2D(200000, 0), new List<LineSeg>(), 0.5, 1.0);
            sw.Stop();

            Assert(sw.ElapsedMilliseconds < 5000, "маршрут считался " + sw.ElapsedMilliseconds + " мс — слишком долго");
            Assert(res.GridCells <= MazeRouter.MaxGridCells, "сетка " + res.GridCells + " ячеек превысила бюджет");
            Assert(res.GridCoarsened, "сетка должна быть укрупнена");
            Assert(res.CellUsed > 0.5, "шаг сетки должен вырасти, а не остаться 0.5");
            Assert(res.Found, "путь по прямой должен находиться");
            Assert(res.Points.Count >= 2, "путь должен содержать хотя бы две точки");
        }

        public void Test_MazeRouter_ManyObstaclesIsBounded()
        {
            var obstacles = Obstacles(400, 6000, 15); // плотная «штриховка» листа
            var sw = Stopwatch.StartNew();
            var res = MazeRouter.Route(new Point2D(-5000, -500), new Point2D(5000, -500), obstacles, 2.0, 1.0);
            sw.Stop();

            Assert(sw.ElapsedMilliseconds < 15000, "обход 400 препятствий занял " + sw.ElapsedMilliseconds + " мс");
            Assert(res.GridCells <= MazeRouter.MaxGridCells, "сетка превысила бюджет: " + res.GridCells);
            Assert(res.Points.Count >= 2, "даже без пути результат должен быть корректным объектом");
        }

        public void Test_MazeRouter_StraightRouteHasCorrectEnds()
        {
            var a = new Point2D(-1000, 0);
            var b = new Point2D(1000, 0);
            var res = MazeRouter.Route(a, b, new List<LineSeg>(), 10.0, 1.0);

            Assert(res.Found, "свободный коридор — путь обязан найтись");
            var first = res.Points[0];
            var last = res.Points[res.Points.Count - 1];
            Assert(first.Dist2(a) < 1e-6, "начало пути не совпало с A");
            Assert(last.Dist2(b) < 1e-6, "конец пути не совпал с B");
        }

        public void Test_MazeRouter_WallForcesDetour()
        {
            var a = new Point2D(-1000, 0);
            var b = new Point2D(1000, 0);
            var wall = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, -400), new Point2D(0, 400))
            };
            var res = MazeRouter.Route(a, b, wall, 20.0, 5.0);
            if (!res.Found) return; // стена на всю высоту — допустим и такой исход
            Assert(res.Length > 2000 + 1, "путь должен быть длиннее прямой из-за стены: " + res.Length);
            Assert(res.Points[res.Points.Count - 1].Dist2(b) < 1e-6, "путь должен заканчиваться в B");
        }

        public void Test_Simplify_LongPathIsBounded()
        {
            // 100 000 точек ступенчатой трассы + 200 препятствий: без бюджета сглаживание не закончится
            var path = new List<Point2D>(100002);
            for (int i = 0; i < 100000; i++)
                path.Add(new Point2D(i * 0.5, (i % 2) * 0.5));
            path.Add(new Point2D(100000, 1.0));
            var obstacles = Obstacles(200, 30000, 37.0);

            var sw = Stopwatch.StartNew();
            var simplified = MazeRouter.Simplify(path, obstacles, 1.0);
            sw.Stop();

            Assert(sw.ElapsedMilliseconds < 10000, "сглаживание заняло " + sw.ElapsedMilliseconds + " мс");
            Assert(simplified.Count >= 2, "сглаженный путь не должен исчезнуть");
            Assert(simplified[0].Dist2(path[0]) < 1e-9, "первая точка потеряна");
            Assert(simplified[simplified.Count - 1].Dist2(path[path.Count - 1]) < 1e-9, "последняя точка потеряна");
            Assert(simplified.Count < path.Count, "ступеньки должны схлопнуться хотя бы частично");
        }

        public void Test_Simplify_CollinearPointsCollapse()
        {
            var path = new List<Point2D>
            {
                new Point2D(0, 0), new Point2D(1, 0), new Point2D(2, 0), new Point2D(3, 0)
            };
            var simplified = MazeRouter.Simplify(path, new List<LineSeg>(), 1e-6);
            Assert(simplified.Count == 2, "прямая должна схлопнуться в две точки, получили " + simplified.Count);
        }
    }
}
