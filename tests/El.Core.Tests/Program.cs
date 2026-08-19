using System;
using System.Collections.Generic;
using System.Linq;
using El.Core;

namespace El.Core.Tests
{
    /// <summary>Минимальный раннер: методы, начинающиеся с "Test_" + класс [TestFixture].</summary>
    public static class Program
    {
        private static int _passed, _failed;

        public static int Main()
        {
            var tests = typeof(Program).Assembly.GetTypes()
                .Where(t => t.Name.EndsWith("Tests"))
                .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(m => m.Name.StartsWith("Test_")))
                .ToList();

            foreach (var m in tests)
            {
                var inst = Activator.CreateInstance(m.DeclaringType);
                try
                {
                    m.Invoke(inst, null);
                    _passed++;
                    Console.WriteLine($"  PASS  {m.DeclaringType.Name}.{m.Name}");
                }
                catch (Exception ex)
                {
                    _failed++;
                    var inner = ex.InnerException ?? ex;
                    Console.WriteLine($"  FAIL  {m.DeclaringType.Name}.{m.Name}: {inner.Message}");
                }
            }

            Console.WriteLine($"\n=== {_passed} passed, {_failed} failed ===");
            return _failed == 0 ? 0 : 1;
        }

        public static void AssertTrue(bool cond, string msg = "assert failed")
        {
            if (!cond) throw new Exception(msg);
        }

        public static void AssertEqual<T>(T expected, T actual, string msg = "")
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"{msg} expected=<{expected}> actual=<{actual}>");
        }

        public static void AssertNear(double expected, double actual, double eps = 1e-6, string msg = "")
        {
            if (Math.Abs(expected - actual) > eps)
                throw new Exception($"{msg} expected={expected} actual={actual}");
        }
    }

    // ============================================================
    // Новые алгоритмы: путь, пересечения, экспорт
    // ============================================================

    public class GraphAlgorithmsTests
    {
        private static WireGraph Chain3()
        {
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(10, 0), new Point2D(20, 0)),
                new LineSeg(3, new Point2D(20, 0), new Point2D(30, 0)),
            };
            return GraphBuilder.Build(lines, 0.5);
        }

        public void Test_FindPath_ThreeLines()
        {
            var g = Chain3();
            var path = GraphAlgorithms.FindPath(g, 1, 3);
            Program.AssertEqual(3, path.Count, "путь 1->3 через 2");
            Program.AssertEqual(1, path[0]);
            Program.AssertEqual(2, path[1]);
            Program.AssertEqual(3, path[2]);
        }

        public void Test_FindPath_NoPath_ReturnsNull()
        {
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(100, 0), new Point2D(110, 0)),
            };
            var g = GraphBuilder.Build(lines, 0.5);
            Program.AssertTrue(GraphAlgorithms.FindPath(g, 1, 2) == null, "нет пути");
        }

        public void Test_SegmentIntersect_Core()
        {
            // горизонталь (0,5)-(10,5) и вертикаль (5,0)-(5,10) -> (5,5)
            var p = GraphAlgorithms.SegmentIntersect(
                new Point2D(0, 5), new Point2D(10, 5),
                new Point2D(5, 0), new Point2D(5, 10));
            Program.AssertTrue(p.HasValue, "должны пересечься");
            Program.AssertNear(5, p.Value.X, 1e-9);
            Program.AssertNear(5, p.Value.Y, 1e-9);
        }

        public void Test_SegmentIntersect_Parallel_Null()
        {
            var p = GraphAlgorithms.SegmentIntersect(
                new Point2D(0, 0), new Point2D(10, 0),
                new Point2D(0, 5), new Point2D(10, 5));
            Program.AssertTrue(!p.HasValue, "параллельные — без пересечения");
        }

        public void Test_SegmentIntersect_OffSegment_Null()
        {
            // вертикаль на x=20, горизонталь 0..10 — не пересекаются в пределах
            var p = GraphAlgorithms.SegmentIntersect(
                new Point2D(0, 5), new Point2D(10, 5),
                new Point2D(20, 0), new Point2D(20, 10));
            Program.AssertTrue(!p.HasValue, "вне отрезка");
        }

        public void Test_FindCrossings_TrueCross_Detected()
        {
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 5), new Point2D(10, 5)),
                new LineSeg(2, new Point2D(5, 0), new Point2D(5, 10)),
            };
            var cr = GraphAlgorithms.FindCrossings(lines, 0.5);
            Program.AssertEqual(1, cr.Count, "одно истинное пересечение");
        }

        public void Test_FindCrossings_EndpointJunction_Ignored()
        {
            // стык концов (L-образное соединение) — НЕ пересечение
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(10, 0), new Point2D(10, 10)),
            };
            var cr = GraphAlgorithms.FindCrossings(lines, 0.5);
            Program.AssertEqual(0, cr.Count, "стыковка концов не считается пересечением");
        }

        public void Test_FindCrossings_TeeJunction_Ignored()
        {
            // T-образное касание концом середины — НЕ пересечение
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 5), new Point2D(10, 5)),
                new LineSeg(2, new Point2D(5, 5), new Point2D(5, 10)),
            };
            var cr = GraphAlgorithms.FindCrossings(lines, 0.5);
            Program.AssertEqual(0, cr.Count, "касание концом не считается");
        }

        public void Test_DotExport_ProducesGraph()
        {
            var g = Chain3();
            var dot = DotExporter.ToDot(g, new List<TextLabel>());
            Program.AssertTrue(dot.Contains("graph scheme"), "заголовок графа");
            Program.AssertTrue(dot.Contains("n1 -- n2"), "ребро 1-2");
            Program.AssertTrue(dot.Contains("n2 -- n3"), "ребро 2-3");
            Program.AssertTrue(dot.Contains("cluster_1"), "кластер цепи");
        }

        public void Test_HtmlExport_ContainsRows()
        {
            var page = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                Aw33Tests.T(100, 0, "1,5 мм²"),
                Aw33Tests.T(100, 50, "КРАСН"),
                Aw33Tests.T(100, 100, "5 м"),
            });
            var html = HtmlExporter.Aw33ToHtml(page);
            Program.AssertTrue(html.Contains("КРАСН"), "цвет в HTML");
            Program.AssertTrue(html.Contains("500.0"), "длина 500 см");
            Program.AssertTrue(html.Contains("5.00"), "длина в метрах");
        }
    }

    // ============================================================
    // Граф
    // ============================================================

    public class GraphTests
    {
        public void Test_SingleChain_TwoLinesTouching()
        {
            // линия 1: (0,0)-(10,0); линия 2: (10,0)-(20,0) — стык в (10,0)
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(10, 0), new Point2D(20, 0)),
            };
            var g = GraphBuilder.Build(lines, 0.5);
            Program.AssertEqual(1, g.AllChains().Count, "должна быть 1 цепь");
            Program.AssertEqual(2, g.AllChains()[0].Count, "в цепи 2 линии");
        }

        public void Test_IsolatedLines_AreSeparateChains()
        {
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(100, 100), new Point2D(110, 110)),
            };
            var g = GraphBuilder.Build(lines, 0.5);
            Program.AssertEqual(2, g.AllChains().Count, "две изолированные цепи");
        }

        public void Test_NearMiss_NotConnected_WithTolerance()
        {
            // концы на расстоянии 1.0, tolerance 0.5 — не соединяются
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(11, 0), new Point2D(20, 0)),
            };
            var g = GraphBuilder.Build(lines, 0.5);
            Program.AssertEqual(2, g.AllChains().Count, "разрыв 1мм > tolerance 0.5");
        }

        public void Test_ChainTerminals_TwoEnds()
        {
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(10, 0), new Point2D(20, 0)),
            };
            var g = GraphBuilder.Build(lines, 0.5);
            var chain = g.AllChains()[0];
            var terms = g.ChainTerminals(chain, 0.5);
            Program.AssertEqual(2, terms.Count, "2 терминала у прямой цепи");
        }

        public void Test_RemoveEdge_SplitsChain()
        {
            var lines = new List<LineSeg>
            {
                new LineSeg(1, new Point2D(0, 0), new Point2D(10, 0)),
                new LineSeg(2, new Point2D(10, 0), new Point2D(20, 0)),
                new LineSeg(3, new Point2D(20, 0), new Point2D(30, 0)),
            };
            var g = GraphBuilder.Build(lines, 0.5);
            var g2 = g.RemoveEdge(2);
            // разорванная линия остаётся в графе изолированной:
            // компоненты {1}, {2}, {3}; для EL-WHATIF важны 2 части от соседей
            var chains = g2.AllChains();
            Program.AssertEqual(3, chains.Count, "разрыв по линии 2: {1}, {2}, {3}");
            Program.AssertEqual(1, g2.Trace(1).Count, "часть 1 от соседа = 1 линия");
            Program.AssertEqual(1, g2.Trace(3).Count, "часть 2 от соседа = 1 линия");
            Program.AssertEqual(1, g2.Neighbors(2).Count == 0 ? 1 : 0, "разорванная линия изолирована");
        }

        public void Test_LargeGrid_2000Lines_Runs()
        {
            // производительность: сетка не должна деградировать в O(n^2)
            var lines = new List<LineSeg>();
            var rnd = new Random(42);
            for (int i = 0; i < 2000; i++)
            {
                double x = rnd.NextDouble() * 10000;
                double y = rnd.NextDouble() * 10000;
                lines.Add(new LineSeg(i, new Point2D(x, y), new Point2D(x + 10, y + 10)));
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = GraphBuilder.Build(lines, 0.5);
            sw.Stop();
            Program.AssertTrue(sw.ElapsedMilliseconds < 5000, $"build слишком долго: {sw.ElapsedMilliseconds}ms");
            Program.AssertTrue(g.AllChains().Count > 0);
        }
    }

    // ============================================================
    // Парсер AW33
    // ============================================================

    public class Aw33Tests
    {
        public static Aw33Parser.RawText T(double y, double x, string text)
            => new Aw33Parser.RawText { Y = y, X = x, Text = text };

        public void Test_WireWithQty_MultipliesLength()
        {
            // реалистичная геометрия: вся строка провода на одной Y с якорем
            var page = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                T(100, 0, "1,5 мм²"),
                T(100, 50, "КРАСН"),
                T(100, 100, "5 м"),
                T(100, 150, "2 шт"),
                T(80, 0, "2,5 мм²"),
            });
            Program.AssertEqual(1, page.Wires.Count, "одна строка провода");
            var w = page.Wires[0];
            // 5 м = 500 см, умножено на 2 шт = 1000 см
            Program.AssertNear(1000.0, w.LengthCm, 1e-6, "длина × кол-во");
            Program.AssertEqual(2, w.Qty, "qty=2");
        }

        public void Test_WireWithQtyInSize_NxMul()
        {
            var page = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                T(100, 0, "2х1,5 мм²"),
                T(100, 50, "СИН"),
                T(100, 100, "3 м"),
                T(80, 0, "2,5 мм²"),
            });
            Program.AssertEqual(1, page.Wires.Count);
            var w = page.Wires[0];
            // 2x1,5 мм² -> qty=2, size="1,5 мм²", 3 м = 300 см * 2 = 600
            Program.AssertNear(600.0, w.LengthCm, 1e-6, "длина × qty из сечения");
            Program.AssertEqual(2, w.Qty, "qty из Nx");
            Program.AssertTrue(w.Size.Contains("1,5"), "сечение без множителя: " + w.Size);
        }

        public void Test_ColorInheritance_AndSum()
        {
            var page = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                T(100, 0, "1,5 мм²"),
                T(100, 50, "КРАСН"),
                T(100, 100, "5 м"),
                T(80, 0, "2,5 мм²"),
                T(80, 50, "2 м"),   // без цвета — наследует КРАСН (из строки выше)
                T(60, 0, "4 мм²"),
            });
            // 2 строки: КРАСН/1,5/500см и КРАСН/2,5/200см — разные сечения, 2 записи
            Program.AssertEqual(2, page.Wires.Count, "две строки с разными сечениями");
            var w1 = page.Wires[0];
            var w2 = page.Wires[1];
            Program.AssertEqual("КРАСН", w1.Color);
            Program.AssertEqual("КРАСН", w2.Color);
            Program.AssertTrue(w1.Size.Contains("1,5"), "size1=" + w1.Size);
            Program.AssertTrue(w2.Size.Contains("2,5"), "size2=" + w2.Size);
            Program.AssertNear(500.0, w1.LengthCm, 1e-6, "5м=500см");
            Program.AssertNear(200.0, w2.LengthCm, 1e-6, "2м=200см");
        }

        public void Test_TermsWithQty_Parsed()
        {
            var page = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                T(100, 0, "1,5 мм²"),
                T(100, 50, "Клемма 2шт"),
                T(80, 0, "2,5 мм²"),
            });
            Program.AssertEqual(1, page.Terms.Count, "одна деталь");
            Program.AssertEqual("Клемма", page.Terms[0].Name, "имя без количества");
            Program.AssertEqual(2, page.Terms[0].Qty, "qty детали");
        }

        public void Test_NoAnchors_ReturnsEmpty()
        {
            var page = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                T(90, 0, "КРАСН"),
                T(90, 50, "5 м"),
            });
            Program.AssertEqual(0, page.Wires.Count, "без якорей мм² — пусто");
        }

        public void Test_MergePages()
        {
            var p1 = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                T(100, 0, "1,5 мм²"), T(90, 0, "КРАСН"), T(90, 50, "5 м"),
            });
            var p2 = Aw33Parser.ParsePage(new List<Aw33Parser.RawText>
            {
                T(100, 0, "1,5 мм²"), T(90, 0, "КРАСН"), T(90, 50, "7 м"),
            });
            var total = Aw33Parser.Merge(new[] { p1, p2 });
            Program.AssertEqual(1, total.Wires.Count);
            Program.AssertNear(1200.0, total.Wires[0].LengthCm, 1e-6, "5м+7м=1200см");
        }
    }

    // ============================================================
    // Diff
    // ============================================================

    public class DiffTests
    {
        public void Test_AddedAndRemoved()
        {
            var snap = new TopologySnapshot();
            snap.Chains.Add(new List<string> { "A", "B" });
            snap.Chains.Add(new List<string> { "C" });

            var cur = new TopologySnapshot();
            cur.Chains.Add(new List<string> { "A", "B" });
            cur.Chains.Add(new List<string> { "D" });

            var res = TopologyDiff.Compare(snap, cur);
            Program.AssertEqual(1, res.Added.Count, "добавлена D");
            Program.AssertEqual(1, res.Removed.Count, "удалена C");
            Program.AssertEqual(0, res.Changed.Count);
        }

        public void Test_Changed()
        {
            var snap = new TopologySnapshot();
            snap.Chains.Add(new List<string> { "A", "B" });

            var cur = new TopologySnapshot();
            cur.Chains.Add(new List<string> { "A", "B", "C" });

            var res = TopologyDiff.Compare(snap, cur);
            Program.AssertEqual(1, res.Changed.Count, "цепь изменилась");
        }

        public void Test_SnapshotRoundTrip()
        {
            var snap = new TopologySnapshot();
            snap.LineCount = 42;
            snap.Chains.Add(new List<string> { "A", "B C", "D\"E" });
            snap.Chains.Add(new List<string> { "F" });

            var text = SnapshotSerializer.Serialize(snap);
            var back = SnapshotSerializer.Deserialize(text);
            Program.AssertEqual(2, back.Chains.Count);
            Program.AssertEqual(3, back.Chains[0].Count);
            Program.AssertEqual("B C", back.Chains[0][1]);
            Program.AssertEqual("D\"E", back.Chains[0][2]);
            Program.AssertEqual(42, back.LineCount);
        }
    }
}
