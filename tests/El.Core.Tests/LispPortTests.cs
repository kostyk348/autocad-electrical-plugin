using System;
using System.Collections.Generic;
using System.Diagnostics;
using El.Core;

namespace El.Core.Tests
{
    /// <summary>Порт LISP-команд: EL-HOTSPOTS (тепловая карта) и EL-CROSSREF (ссылки на листы).</summary>
    public class LispPortTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static LineSeg Seg(int id, double x1, double y1, double x2, double y2)
        {
            return new LineSeg(id, new Point2D(x1, y1), new Point2D(x2, y2));
        }

        public void Test_Hotspots_CountsByMidpoint()
        {
            // три линии в одной ячейке (0..50), одна в правой верхней
            var lines = new List<LineSeg>
            {
                Seg(1, 0, 0, 10, 10),
                Seg(2, 5, 5, 15, 15),
                Seg(3, 20, 20, 30, 30),
                Seg(4, 80, 80, 90, 90)
            };
            var map = Hotspots.Build(lines, 0, 0, 100, 100, 2);

            Assert(map.Cells == 2, "cells");
            Assert(map.Max == 3, "max должен быть 3, получили " + map.Max);
            Assert(map.Counts[0, 0] == 3, "левая-нижняя ячейка: " + map.Counts[0, 0]);
            Assert(map.Counts[1, 1] == 1, "правая-верхняя ячейка: " + map.Counts[1, 1]);
            Assert(map.AciFor(0, 0) == 1, "самая плотная ячейка должна быть красной (ACI 1)");
            Assert(map.AciFor(1, 1) == 30, "ячейка с 1/3 плотности — ACI 30, получили " + map.AciFor(1, 1));
        }

        public void Test_Hotspots_ClampsOutOfBounds()
        {
            var lines = new List<LineSeg> { Seg(1, -10000, -10000, -9990, -9990) };
            var map = Hotspots.Build(lines, 0, 0, 100, 100, 4);
            Assert(map.Counts[0, 0] == 1, "линия вне габаритов должна попасть в крайнюю ячейку");
            Assert(map.Max == 1, "max");
        }

        public void Test_Hotspots_EmptyAndDegenerate()
        {
            var map = Hotspots.Build(new List<LineSeg>(), 0, 0, 0, 0, 10);
            Assert(map.Max == 0, "пустой чертёж: max = 0");
            Assert(map.CellWidth > 0 && map.CellHeight > 0, "вырожденные габариты не должны давать нулевой шаг");
            Assert(Hotspots.Build(null, 0, 0, 10, 10).Max == 0, "null-список не должен падать");
        }

        public void Test_Hotspots_CellsAreClamped()
        {
            var map = Hotspots.Build(new List<LineSeg>(), 0, 0, 10, 10, 100000);
            Assert(map.Cells == Hotspots.MaxCells, "ячеек не больше " + Hotspots.MaxCells + ", получили " + map.Cells);
        }

        public void Test_Hotspots_LargeDrawingIsLinear()
        {
            var lines = new List<LineSeg>(200000);
            for (int i = 0; i < 200000; i++) lines.Add(Seg(i, i % 1000, i % 1000, i % 1000 + 1, i % 1000 + 1));
            var sw = Stopwatch.StartNew();
            var map = Hotspots.Build(lines, 0, 0, 1000, 1000, 32);
            sw.Stop();
            Assert(sw.ElapsedMilliseconds < 3000, "200 000 линий считались " + sw.ElapsedMilliseconds + " мс");
            Assert(map.Max > 0, "карта не должна быть пустой");
        }

        // ------------------------------------------------------------ cross-refs

        public void Test_CrossRef_FindsSheetReferences()
        {
            var texts = new List<TextLabel>
            {
                new TextLabel(new Point2D(0, 0), "см. лист 5"),
                new TextLabel(new Point2D(10, 0), "Л. 12/3"),
                new TextLabel(new Point2D(20, 0), "Sheet 8"),
                new TextLabel(new Point2D(30, 0), "рядом нет ссылок"),
                new TextLabel(new Point2D(40, 0), "листопад 77")
            };
            var refs = CrossReferences.Find(texts);

            Assert(refs.Count == 3, "должно быть 3 ссылки, получили " + refs.Count);
            Assert(refs[0].Sheet == 5, "первая ссылка — лист 5");
            Assert(refs[1].Sheet == 12 && refs[1].Position == 3, "Л. 12/3 → лист 12 поз. 3");
            Assert(refs[2].Sheet == 8, "Sheet 8 → 8");
        }

        public void Test_CrossRef_CollectsPosition()
        {
            var refs = CrossReferences.Find(new List<TextLabel> { new TextLabel(new Point2D(1, 1), "лист 4/7") });
            Assert(refs.Count == 1 && refs[0].Position == 7, "позиция на листе должна читаться");
            Assert(refs[0].ToString().Contains("лист 4"), "текстовое представление: " + refs[0]);
        }

        public void Test_CrossRef_ManyMatchesTerminate()
        {
            // много вхождений в одном тексте: цикл обязан идти вперёд и закончиться
            // (текст длиннее CrossRef.MaxTextLength не разбирается целиком — это тоже граница)
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 200; i++) sb.Append("лист 1 ");
            var text = sb.ToString();
            var sw = Stopwatch.StartNew();
            var refs = CrossReferences.Find(new List<TextLabel> { new TextLabel(new Point2D(0, 0), text) });
            sw.Stop();
            Assert(refs.Count == 200, "ожидалось 200 ссылок, получили " + refs.Count);
            Assert(sw.ElapsedMilliseconds < 3000, "разбор занял " + sw.ElapsedMilliseconds + " мс");
        }

        public void Test_CrossRef_HandlesNullsAndEmptyText()
        {
            var refs = CrossReferences.Find(new List<TextLabel>
            {
                null,
                new TextLabel(new Point2D(0, 0), ""),
                new TextLabel(new Point2D(0, 0), "см. лист")
            });
            Assert(refs.Count == 0, "без номера ссылки нет");
            Assert(CrossReferences.Find(null).Count == 0, "null-список не должен падать");
        }
    }
}
