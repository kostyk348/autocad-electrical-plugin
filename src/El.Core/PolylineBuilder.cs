using System;
using System.Collections.Generic;
using System.Linq;

namespace El.Core
{
    /// <summary>
    /// Разложение сети линий на простые цепочки (для объединения в полилинии).
    /// Правила:
    /// - узлы — концы линий, стянутые по tolerance;
    /// - «особые» узлы: degree != 2 (терминалы 1, развилки 3+);
    /// - каждый сегмент — минимальный путь между особыми узлами;
    /// - линейная цепочка даёт 1 сегмент (от терминала к терминалу) — главный случай.
    /// </summary>
    public static class PolylineBuilder
    {
        public sealed class Segment
        {
            public List<Point2D> Points = new List<Point2D>();
            public List<int> LineIds = new List<int>();
        }

        private struct LineNodes
        {
            public int A;
            public int B;
        }

        public static List<Segment> BuildSegments(IReadOnlyList<LineSeg> lines, double tolerance)
        {
            double tol2 = tolerance * tolerance;
            // узлы: уникальные точки (стяжка по tolerance)
            var nodes = new List<Point2D>();
            var lineNodes = new List<LineNodes>(); // индексы узлов для каждой линии
            foreach (var l in lines)
            {
                int a = GetNode(nodes, l.A, tol2);
                int b = GetNode(nodes, l.B, tol2);
                lineNodes.Add(new LineNodes { A = a, B = b });
            }

            int n = nodes.Count;
            var adj = new List<List<int>>(n);
            for (int i = 0; i < n; i++) adj.Add(new List<int>());
            for (int i = 0; i < lineNodes.Count; i++)
            {
                var e = lineNodes[i];
                adj[e.A].Add(i);
                adj[e.B].Add(i);
            }
            int[] deg = new int[n];
            for (int i = 0; i < lineNodes.Count; i++)
            {
                deg[lineNodes[i].A]++;
                deg[lineNodes[i].B]++;
            }

            // особые узлы
            var special = new HashSet<int>();
            for (int i = 0; i < n; i++)
                if (deg[i] != 2) special.Add(i);

            // сегменты: walk от каждого особого узла по непосещённым рёбрам
            var usedLines = new bool[lines.Count];
            var segments = new List<Segment>();

            foreach (int start in special)
            {
                foreach (int edgeId in adj[start])
                {
                    if (usedLines[edgeId]) continue;
                    var seg = new Segment();
                    int curNode = start;
                    int curEdge = edgeId;
                    // идём, пока не упрёмся в особый узел
                    while (true)
                    {
                        usedLines[curEdge] = true;
                        var en = lineNodes[curEdge];
                        int nextNode = (en.A == curNode) ? en.B : en.A;
                        // точка текущего узла
                        if (seg.Points.Count == 0)
                            seg.Points.Add(nodes[curNode]);
                        // второй конец ребра
                        seg.Points.Add(nodes[nextNode]);
                        seg.LineIds.Add(curEdge);
                        if (special.Contains(nextNode)) break;
                        // ищем следующее ребро от nextNode (не использованное)
                        int nextEdge = -1;
                        foreach (var e in adj[nextNode])
                        {
                            if (!usedLines[e]) { nextEdge = e; break; }
                        }
                        if (nextEdge == -1) break; // тупик (кольцо без особых узлов)
                        curNode = nextNode;
                        curEdge = nextEdge;
                    }
                    if (seg.Points.Count >= 2)
                        segments.Add(seg);
                }
            }

            // кольца без особых узлов (degree 2 везде): обойти с любой линии
            for (int i = 0; i < lines.Count; i++)
            {
                if (usedLines[i]) continue;
                var seg = new Segment();
                int curEdge = i;
                int curNode = lineNodes[i].A;
                while (!usedLines[curEdge])
                {
                    usedLines[curEdge] = true;
                    var en = lineNodes[curEdge];
                    int nextNode = (en.A == curNode) ? en.B : en.A;
                    if (seg.Points.Count == 0) seg.Points.Add(nodes[curNode]);
                    seg.Points.Add(nodes[nextNode]);
                    seg.LineIds.Add(curEdge);
                    int nextEdge = -1;
                    foreach (var e in adj[nextNode])
                        if (!usedLines[e]) { nextEdge = e; break; }
                    if (nextEdge == -1) break;
                    curNode = nextNode;
                    curEdge = nextEdge;
                }
                if (seg.Points.Count >= 3) segments.Add(seg); // замкнутое кольцо
            }
            return segments;
        }

        private static int GetNode(List<Point2D> nodes, Point2D p, double tol2)
        {
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].Approx(p, tol2)) return i;
            nodes.Add(p);
            return nodes.Count - 1;
        }
    }
}
