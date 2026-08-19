using System;
using System.Collections.Generic;

namespace El.Core
{
    /// <summary>Результат построения графа: смежность + линии по индексу.</summary>
    public sealed class WireGraph
    {
        /// <summary>Смежность: lineId -> соседние lineId (неориентированный граф).</summary>
        public Dictionary<int, HashSet<int>> Adj { get; } = new Dictionary<int, HashSet<int>>();

        private readonly Dictionary<int, LineSeg> _lines;

        public WireGraph(Dictionary<int, LineSeg> lines)
        {
            _lines = lines ?? new Dictionary<int, LineSeg>();
            foreach (var id in _lines.Keys)
                Adj[id] = new HashSet<int>();
        }

        public IReadOnlyDictionary<int, LineSeg> Lines => _lines;

        public LineSeg GetLine(int id) => _lines.TryGetValue(id, out var l) ? l : null;

        public int LineCount => _lines.Count;

        public void AddEdge(int a, int b)
        {
            if (!Adj.TryGetValue(a, out var sa)) Adj[a] = sa = new HashSet<int>();
            if (!Adj.TryGetValue(b, out var sb)) Adj[b] = sb = new HashSet<int>();
            sa.Add(b);
            sb.Add(a);
        }

        public List<int> Neighbors(int id)
        {
            return Adj.TryGetValue(id, out var s) ? new List<int>(s) : new List<int>();
        }

        /// <summary>BFS от линии: вся связная компонента (в порядке обхода).</summary>
        public List<int> Trace(int start)
        {
            var visited = new HashSet<int> { start };
            var queue = new Queue<int>();
            var order = new List<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                order.Add(cur);
                if (!Adj.TryGetValue(cur, out var nb)) continue;
                foreach (int n in nb)
                {
                    if (visited.Add(n)) queue.Enqueue(n);
                }
            }
            return order;
        }

        /// <summary>Все связные компоненты (цепи).</summary>
        public List<List<int>> AllChains()
        {
            var visited = new HashSet<int>();
            var chains = new List<List<int>>();
            foreach (int id in _lines.Keys)
            {
                if (visited.Contains(id)) continue;
                var ch = Trace(id);
                foreach (var x in ch) visited.Add(x);
                chains.Add(ch);
            }
            return chains;
        }

        /// <summary>
        /// Терминалы цепи: точки с числом соединений 1.
        /// Концы, лежащие ближе tolerance друг к другу, считаются одной точкой.
        /// </summary>
        public List<Point2D> ChainTerminals(List<int> chain, double tolerance)
        {
            double tol2 = tolerance * tolerance;
            var pts = new List<Point2D>(); // уникальные точки-узлы
            var deg = new List<int>();

            void Inc(Point2D p)
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    if (pts[i].Approx(p, tol2)) { deg[i]++; return; }
                }
                pts.Add(p);
                deg.Add(1);
            }

            foreach (int id in chain)
            {
                var l = GetLine(id);
                if (l == null) continue;
                Inc(l.A);
                Inc(l.B);
            }

            var terms = new List<Point2D>();
            for (int i = 0; i < deg.Count; i++)
                if (deg[i] == 1) terms.Add(pts[i]);
            return terms;
        }

        /// <summary>Граф без ребра (для EL-WHATIF). Не мутирует оригинал.</summary>
        public WireGraph RemoveEdge(int edge)
        {
            var g = new WireGraph(_lines);
            foreach (var kv in Adj)
            {
                var nb = new HashSet<int>();
                foreach (int n in kv.Value)
                {
                    if (kv.Key == edge || n == edge) continue;
                    nb.Add(n);
                }
                g.Adj[kv.Key] = nb;
            }
            return g;
        }
    }

    /// <summary>Построение графа из отрезков: стыковка концов с tolerance через grid.</summary>
    public static class GraphBuilder
    {
        public static WireGraph Build(IReadOnlyList<LineSeg> lines, double tolerance)
        {
            double tol2 = tolerance * tolerance;
            double cell = Math.Max(tolerance, 50.0);

            var grid = new SpatialGrid<int>(cell);
            var byId = new Dictionary<int, LineSeg>(lines.Count);
            foreach (var l in lines)
            {
                byId[l.Id] = l;
                grid.Add(l.A, l.Id);
                grid.Add(l.B, l.Id);
            }

            var g = new WireGraph(byId);

            foreach (var l in lines)
            {
                var cand = new HashSet<int>();
                foreach (var c in grid.QueryNear(l.A)) cand.Add(c);
                foreach (var c in grid.QueryNear(l.B)) cand.Add(c);
                cand.Remove(l.Id);

                foreach (int otherId in cand)
                {
                    var o = byId[otherId];
                    if (Connects(l, o, tol2))
                        g.AddEdge(l.Id, otherId);
                }
            }
            return g;
        }

        private static bool Connects(LineSeg a, LineSeg b, double tol2)
        {
            return a.A.Approx(b.A, tol2) || a.A.Approx(b.B, tol2) ||
                   a.B.Approx(b.A, tol2) || a.B.Approx(b.B, tol2);
        }
    }
}
