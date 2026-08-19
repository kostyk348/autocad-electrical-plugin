using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using El.Core;
using El.Plugin.Ui;

namespace El.Plugin
{
    public static class CommandState
    {
        /// <summary>Последний граф (для контекстного меню/палитры).</summary>
        public static WireGraph Graph;
        public static List<LineSeg> Lines;
        public static List<TextLabel> Texts;
        public static List<List<int>> Chains;
        public static List<string> CheckReport = new List<string>();
        public static List<List<int>> DefectChains = new List<List<int>>();

        /// <summary>Фильтр слоёв для анализа (пусто = все). Задаётся EL-LAYER-FILTER, хранится в реестре.</summary>
        public static List<string> LayerFilter = new List<string>();

        private const string FilterRegKey = @"Software\ElTools\LayerFilter";

        public static void LoadLayerFilter()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(FilterRegKey))
                {
                    string v = key?.GetValue("Layers") as string;
                    LayerFilter = string.IsNullOrEmpty(v) || v == "*"
                        ? new List<string>()
                        : new List<string>(v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                               .Select(s => s.Trim()));
                }
            }
            catch { LayerFilter = new List<string>(); }
        }

        public static void SaveLayerFilter()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(FilterRegKey))
                {
                    key.SetValue("Layers", LayerFilter.Count == 0 ? "*" : string.Join(",", LayerFilter));
                }
            }
            catch { }
        }

        public static bool IsLayerAllowed(string layer)
        {
            if (LayerFilter.Count == 0) return true;
            return LayerFilter.Contains(layer ?? "", StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Собрать модель чертежа (граф + цепи) с учётом фильтра слоёв.</summary>
        public static void Refresh()
        {
            using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
            {
                Lines = DwgAccess.CollectLines(tr).Where(l => IsLayerAllowed(l.Layer)).ToList();
                Texts = DwgAccess.CollectTexts(tr);
                tr.Commit();
            }
            if (Lines.Count < 2) { Graph = null; Chains = new List<List<int>>(); return; }
            Graph = GraphBuilder.Build(Lines, DwgAccess.DefaultTolerance);
            Chains = Graph.AllChains();
        }
    }

    public static class Commands
    {
        private static Editor Ed => DwgAccess.Ed;

        // ============================================================
        // EL-GRAPH (отладка)
        // ============================================================
        [CommandMethod("EL-GRAPH")]
        public static void ElGraph()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }
                Ed.WriteMessage($"\n--- EL-GRAPH: {CommandState.Lines.Count} линий, {CommandState.Chains.Count} цепей ---");
                for (int i = 0; i < CommandState.Chains.Count; i++)
                {
                    var texts = ChainTexts.NearEnds(CommandState.Graph, CommandState.Chains[i], CommandState.Texts, DwgAccess.DefaultTextRadius);
                    Ed.WriteMessage($"\n  #{i + 1}: {CommandState.Chains[i].Count} линий [{string.Join(" ", texts)}]");
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-GRAPH: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-TRACE
        // ============================================================
        [CommandMethod("EL-TRACE")]
        public static void ElTrace()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE в чертеже (нужно >=2)"); return; }
                Ed.WriteMessage("\n=== EL-TRACE: кликай на LINE цепи. ENTER — выход ===");
                while (true)
                {
                    var po = new PromptEntityOptions("\n→ Выбери линию: ");
                    po.SetRejectMessage("\n! Это не LINE");
                    po.AddAllowedClass(typeof(Line), true);
                    var pr = Ed.GetEntity(po);
                    if (pr.Status != PromptStatus.OK) break;
                    var id = pr.ObjectId;
                    var line = CommandState.Lines.FirstOrDefault(l => (ObjectId)l.Tag == id);
                    if (line == null) continue;
                    var chain = CommandState.Graph.Trace(line.Id);
                    var segs = chain.Select(c => CommandState.Lines.First(l => l.Id == c)).ToList();
                    DwgAccess.Highlight(segs, true);
                    var texts = ChainTexts.NearEnds(CommandState.Graph, chain, CommandState.Texts, DwgAccess.DefaultTextRadius);
                    double len = segs.Sum(s => s.Length);
                    Ed.WriteMessage($"\n=== ЦЕПЬ: {chain.Count} отрезков, длина {len:F2} мм ===");
                    Ed.WriteMessage("\nТексты: " + string.Join(" ", texts.Select(t => $"[{t}]")));
                    Ed.WriteMessage("\nENTER — снять подсветку: ");
                    Ed.GetString("");
                    DwgAccess.Highlight(segs, false);
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-TRACE: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-CHECK
        // ============================================================
        [CommandMethod("EL-CHECK")]
        public static void ElCheck()
        {
            try
            {
                var report = BuildCheckReport();
                foreach (var line in report.Lines)
                    Ed.WriteMessage(line);
                Palette.Instance?.ShowReport(CommandState.CheckReport, CommandState.DefectChains);
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-CHECK: " + ex.Message); Plugin.Log(ex); }
        }

        /// <summary>Результат дефектоскопа (строки для вывода + структурированные данные).</summary>
        public sealed class CheckReportResult
        {
            public List<string> Lines = new List<string>();
            public int Isolated;
            public int Gaps;
            public int Duplicates;
            public int Textless;
            public int SingleLines;
        }

        /// <summary>Собрать отчёт дефектоскопа (переиспользуется EL-CHECK и EL-CHECK-REPORT).</summary>
        public static CheckReportResult BuildCheckReport()
        {
            var res = new CheckReportResult();
            CommandState.Refresh();
            if (CommandState.Graph == null) { res.Lines.Add("\n! Мало LINE"); return res; }
            var g = CommandState.Graph;
            var lines = CommandState.Lines;
            var chains = CommandState.Chains;
            CommandState.CheckReport.Clear();
            CommandState.DefectChains.Clear();

            res.Lines.Add($"\n=== EL-CHECK: {lines.Count} линий, {chains.Count} цепей ===");

            // 1. изолированные
            var isolated = g.Adj.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            res.Isolated = isolated.Count;
            res.Lines.Add($"\n--- ИЗОЛИРОВАННЫЕ ЛИНИИ: {isolated.Count} шт ---");
            foreach (var id in isolated)
            {
                var l = lines.First(x => x.Id == id);
                res.Lines.Add($"\n  LINE {l.A} — {l.B}");
                CommandState.CheckReport.Add($"Изолированная линия: {l.A}");
            }

            // 2. near-miss разрывы (grid)
            var gaps = FindGaps(lines, DwgAccess.DefaultTolerance);
            res.Gaps = gaps.Count;
            res.Lines.Add($"\n--- БЛИЗКИЕ РАЗРЫВЫ (gap < {DwgAccess.DefaultTolerance * 6:F1} мм): {gaps.Count} ---");
            foreach (var gp in gaps)
            {
                res.Lines.Add($"\n  gap {gp.D:F2} мм: {gp.L1.A} — {gp.L2.A}");
                CommandState.CheckReport.Add($"Разрыв {gp.D:F1} мм: {gp.L1.A}");
            }

            // 3. дубликаты текста в разных цепях
            var dup = FindDuplicateTexts(chains, g, lines);
            res.Duplicates = dup.Count;
            res.Lines.Add($"\n--- ДУБЛИКАТЫ ТЕКСТА: {dup.Count} ---");
            foreach (var d in dup)
            {
                res.Lines.Add($"\n  \"{d.Text}\" в цепях: {string.Join(", ", d.Chains.Select(c => "#" + (c + 1)))}");
                CommandState.CheckReport.Add($"Дубликат \"{d.Text}\"");
            }

            // 4. цепи без подписей
            int textless = 0;
            for (int i = 0; i < chains.Count; i++)
            {
                var texts = ChainTexts.NearEnds(g, chains[i], CommandState.Texts, DwgAccess.DefaultTextRadius);
                if (texts.Count == 0)
                {
                    textless++;
                    res.Lines.Add($"\n  Цепь #{i + 1}: {chains[i].Count} линий, без текста");
                    CommandState.DefectChains.Add(chains[i]);
                }
            }
            res.Textless = textless;
            res.Lines.Add($"\nЦепи без подписей: {textless}");

            // 5. цепи из 1 линии
            int singles = chains.Count(c => c.Count == 1);
            res.SingleLines = singles;
            res.Lines.Add($"\nЦепи из одной линии: {singles}");

            res.Lines.Add("\n=== EL-CHECK завершён ===");
            return res;
        }

        public sealed class GapInfo
        {
            public LineSeg L1;
            public LineSeg L2;
            public double D;
        }

        private sealed class DupInfo
        {
            public string Text;
            public List<int> Chains = new List<int>();
        }

        /// <summary>Публичная обёртка поиска разрывов (для отчётов).</summary>
        public static List<GapInfo> FindGapsPublic(List<LineSeg> lines, double tol) => FindGaps(lines, tol);

        private static List<GapInfo> FindGaps(List<LineSeg> lines, double tol)
        {
            double gs = tol * 6.0, tol2 = tol * tol;
            var grid = new SpatialGrid<int>(gs);
            foreach (var l in lines) { grid.Add(l.A, l.Id); grid.Add(l.B, l.Id); }
            var result = new List<GapInfo>();
            var seen = new HashSet<long>();
            foreach (var l in lines)
            {
                foreach (var p in new[] { l.A, l.B })
                {
                    foreach (var oid in grid.QueryNear(p))
                    {
                        var o = lines.First(x => x.Id == oid);
                        if (o == l) continue;
                        long key = ((long)Math.Min(l.Id, o.Id) << 32) | (uint)Math.Max(l.Id, o.Id);
                        if (!seen.Add(key)) continue;
                        foreach (var op in new[] { o.A, o.B })
                        {
                            double d = p.Dist(op);
                            if (d > tol && d < gs)
                            {
                                result.Add(new GapInfo { L1 = l, L2 = o, D = d });
                                break;
                            }
                        }
                    }
                }
            }
            return result;
        }

        private static List<DupInfo> FindDuplicateTexts(List<List<int>> chains, WireGraph g, List<LineSeg> lines)
        {
            var map = new Dictionary<string, DupInfo>();
            for (int i = 0; i < chains.Count; i++)
            {
                var texts = ChainTexts.NearEnds(g, chains[i], CommandState.Texts, DwgAccess.DefaultTextRadius);
                foreach (var t in texts.Distinct())
                {
                    if (!map.TryGetValue(t, out var info)) map[t] = info = new DupInfo { Text = t };
                    info.Chains.Add(i);
                }
            }
            return map.Values.Where(v => v.Chains.Count > 1).ToList();
        }

        // ============================================================
        // EL-WHATIF
        // ============================================================
        [CommandMethod("EL-WHATIF")]
        public static void ElWhatif()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }
                Ed.WriteMessage("\n=== EL-WHATIF: кликни на линию, которую разрываем ===");
                var po = new PromptEntityOptions("\n→ Линия: ");
                po.SetRejectMessage("\n! Это не LINE");
                po.AddAllowedClass(typeof(Line), true);
                var pr = Ed.GetEntity(po);
                if (pr.Status != PromptStatus.OK) return;
                var id = pr.ObjectId;
                var line = CommandState.Lines.First(l => (ObjectId)l.Tag == id);
                var g = CommandState.Graph;
                var adj = g.Neighbors(line.Id);
                if (adj.Count < 2)
                {
                    Ed.WriteMessage("\n! Линия концевая — разрыв не разделит цепь");
                    DwgAccess.Highlight(new[] { line }, true);
                    Ed.GetString("\nENTER:");
                    DwgAccess.Highlight(new[] { line }, false);
                    return;
                }
                var g2 = g.RemoveEdge(line.Id);
                var s1 = g2.Trace(adj[0]).Select(c => CommandState.Lines.First(l => l.Id == c)).ToList();
                var s2 = g2.Trace(adj[1]).Select(c => CommandState.Lines.First(l => l.Id == c)).ToList();
                Ed.WriteMessage($"\n=== РЕЗУЛЬТАТ РАЗРЫВА ===");
                Ed.WriteMessage($"\nЧасть 1: {s1.Count} линий");
                Ed.WriteMessage($"\nЧасть 2: {s2.Count} линий");
                DwgAccess.Highlight(s1, true);
                Ed.GetString("\nЧасть 1 подсвечена. ENTER — показать часть 2:");
                DwgAccess.Highlight(s1, false);
                DwgAccess.Highlight(s2, true);
                Ed.GetString("\nЧасть 2 подсвечена. ENTER — снять:");
                DwgAccess.Highlight(s2, false);
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-WHATIF: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-TABLE — таблица соединений
        // ============================================================
        [CommandMethod("EL-TABLE")]
        public static void ElTable()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }
                var pr = Ed.GetPoint("\n→ Точка вставки таблицы: ");
                if (pr.Status != PromptStatus.OK) return;

                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    var header = new[] { "Цепь", "Откуда", "Куда" };
                    var rows = new List<string[]>();
                    for (int i = 0; i < CommandState.Chains.Count; i++)
                    {
                        var ch = CommandState.Chains[i];
                        var terms = CommandState.Graph.ChainTerminals(ch, DwgAccess.DefaultTolerance);
                        string from = "", to = "";
                        if (terms.Count >= 2)
                        {
                            from = string.Join(" ", ChainTexts.NearPoint(terms[0], CommandState.Texts, DwgAccess.DefaultTextRadius));
                            to = string.Join(" ", ChainTexts.NearPoint(terms[1], CommandState.Texts, DwgAccess.DefaultTextRadius));
                        }
                        else if (terms.Count == 1)
                            from = "[петля] " + string.Join(" ", ChainTexts.NearPoint(terms[0], CommandState.Texts, DwgAccess.DefaultTextRadius));
                        else
                            from = $"[кольцо #{i + 1}]";
                        rows.Add(new[] { (i + 1).ToString(), from, to });
                    }
                    DwgAccess.AddTable(tr, pr.Value, header, rows, 6, 60);
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Таблица соединений: {CommandState.Chains.Count} цепей");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-TABLE: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-STATS
        // ============================================================
        [CommandMethod("EL-STATS")]
        public static void ElStats()
        {
            try
            {
                CommandState.Refresh();
                Ed.WriteMessage($"\n=== EL-STATS ===");
                Ed.WriteMessage($"\nLINE: {CommandState.Lines.Count}");
                Ed.WriteMessage($"\nTEXT: {CommandState.Texts.Count}");
                if (CommandState.Graph == null) return;
                var chains = CommandState.Chains;
                Ed.WriteMessage($"\nЦЕПЕЙ: {chains.Count}");
                if (chains.Count > 0)
                {
                    var lens = chains.Select(c => c.Count).ToList();
                    Ed.WriteMessage($"\nДлина цепи: мин={lens.Min()} макс={lens.Max()} сред={lens.Average():F1}");
                    int withText = 0;
                    foreach (var ch in chains)
                        if (ChainTexts.NearEnds(CommandState.Graph, ch, CommandState.Texts, DwgAccess.DefaultTextRadius).Count > 0) withText++;
                    Ed.WriteMessage($"\nЦепи с подписями: {withText}, без: {chains.Count - withText}");
                    var hist = new int[6];
                    foreach (var n in lens)
                    {
                        if (n <= 1) hist[0]++;
                        else if (n <= 3) hist[1]++;
                        else if (n <= 10) hist[2]++;
                        else if (n <= 30) hist[3]++;
                        else if (n <= 100) hist[4]++;
                        else hist[5]++;
                    }
                    Ed.WriteMessage("\nРаспределение: 1={0} 2-3={1} 4-10={2} 11-30={3} 31-100={4} >100={5}",
                        hist[0], hist[1], hist[2], hist[3], hist[4], hist[5]);
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-STATS: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-COLOR-CHAINS
        // ============================================================
        [CommandMethod("EL-COLOR-CHAINS")]
        public static void ElColorChains()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }
                short[] colors = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200, 210, 220, 230, 240, 250 };
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    int i = 0;
                    foreach (var ch in CommandState.Chains)
                    {
                        short col = colors[i % colors.Length];
                        foreach (var id in ch)
                        {
                            var l = CommandState.Lines.First(x => x.Id == id);
                            var ent = (Entity)tr.GetObject((ObjectId)l.Tag, OpenMode.ForWrite);
                            var line = (Line)ent;
                            line.ColorIndex = col;
                        }
                        i++;
                    }
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Раскрашено {CommandState.Chains.Count} цепей. REGEN для обновления.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-COLOR-CHAINS: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-LOOPS
        // ============================================================
        [CommandMethod("EL-LOOPS")]
        public static void ElLoops()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }
                int found = 0;
                foreach (var ch in CommandState.Chains)
                {
                    var terms = CommandState.Graph.ChainTerminals(ch, DwgAccess.DefaultTolerance);
                    if (terms.Count == 0)
                    {
                        found++;
                        var segs = ch.Select(c => CommandState.Lines.First(l => l.Id == c)).ToList();
                        var texts = ChainTexts.NearEnds(CommandState.Graph, ch, CommandState.Texts, DwgAccess.DefaultTextRadius);
                        Ed.WriteMessage($"\n! КОЛЬЦО/ПЕТЛЯ: {ch.Count} линий [{string.Join(" ", texts)}]");
                        DwgAccess.Highlight(segs, true);
                        Ed.GetString("\nENTER — продолжить:");
                        DwgAccess.Highlight(segs, false);
                    }
                }
                Ed.WriteMessage(found == 0 ? "\n; Петли не найдены" : $"\n; Найдено петель: {found}");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-LOOPS: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-BOTTLENECK
        // ============================================================
        [CommandMethod("EL-BOTTLENECK")]
        public static void ElBottleneck()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }
                var counts = new Dictionary<int, int>();
                foreach (var ch in CommandState.Chains)
                    foreach (var id in ch)
                    {
                        counts.TryGetValue(id, out int v);
                        counts[id] = v + 1;
                    }
                var top = counts.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).Take(10);
                Ed.WriteMessage("\nТоп-10 узких мест (линий через несколько цепей):");
                foreach (var kv in top)
                {
                    var l = CommandState.Lines.First(x => x.Id == kv.Key);
                    var texts = ChainTexts.NearPoint(l.Mid, CommandState.Texts, DwgAccess.DefaultTextRadius);
                    Ed.WriteMessage($"\n  LINE через {kv.Value} цепей: {string.Join(" ", texts)}");
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-BOTTLENECK: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // AW33 — постраничная спецификация проводов
        // ============================================================
        [CommandMethod("AW33")]
        public static void Aw33()
        {
            try
            {
                var ed = Ed;
                var pages = new List<Aw33PageResult>();
                int page = 1;
                ed.WriteMessage("\n=== AW33: выделяй тексты листа (TEXT/MTEXT). Enter — сводная ===");
                while (true)
                {
                    ed.WriteMessage($"\n--- Лист {page} ---");
                    ed.WriteMessage("\nВыделите провода НА ОДНОМ ЛИСТЕ (Enter — сводная): ");
                    var so = new PromptSelectionOptions();
                    var sr = ed.GetSelection(so);
                    if (sr.Status != PromptStatus.OK) break;

                    var raws = new List<Aw33Parser.RawText>();
                    using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (var id in sr.Value.GetObjectIds())
                        {
                            var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            string text = ent is DBText dt ? dt.TextString : ent is MText mt ? mt.Contents : null;
                            if (text == null) continue;
                            Point3d pos = ent is DBText d2 ? d2.Position : ent is MText m2 ? m2.Location : default;
                            raws.Add(new Aw33Parser.RawText { Y = pos.Y, X = pos.X, Text = text });
                        }
                        tr.Commit();
                    }
                    var res = Aw33Parser.ParsePage(raws);
                    pages.Add(res);
                    ed.WriteMessage($"\n; Лист {page}: проводов={res.Wires.Count}, деталей={res.Terms.Count}");
                    page++;
                }

                var total = Aw33Parser.Merge(pages);
                if (total.Wires.Count == 0 && total.Terms.Count == 0)
                {
                    ed.WriteMessage("\n! Данные не собраны.");
                    return;
                }

                var pp = ed.GetPoint("\nКликните точку для таблиц листа 1: ");
                if (pp.Status != PromptStatus.OK) return;
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    var cur = pp.Value;
                    foreach (var p in pages)
                    {
                        if (p.Wires.Count > 0)
                        {
                            var header = new[] { "Марка / Цвет", "Сечение", "Кол-во", "Длина, см" };
                            var rows = p.Wires.Select(w => new[] { w.Color, w.Size, w.Qty.ToString(), w.LengthCm.ToString("F1") }).ToList();
                            DwgAccess.AddTable(tr, cur, header, rows, 8, 50);
                        }
                        if (p.Terms.Count > 0)
                        {
                            var header = new[] { "Наименование", "Кол-во, шт" };
                            var rows = p.Terms.Select(t => new[] { t.Name, t.Qty.ToString() }).ToList();
                            DwgAccess.AddTable(tr, new Point3d(cur.X + 180, cur.Y, 0), header, rows, 8, 80);
                        }
                        cur = new Point3d(cur.X, cur.Y - (Math.Max(p.Wires.Count, p.Terms.Count) + 4) * 8.0 - 20, 0);
                    }

                    if (total.Wires.Count > 0)
                    {
                        var gp = ed.GetPoint("\nКликните точку для ИТОГОВЫХ таблиц: ");
                        if (gp.Status == PromptStatus.OK)
                        {
                            var header = new[] { "Марка / Цвет", "Сечение", "Кол-во", "Общая длина, см" };
                            var rows = total.Wires.Select(w => new[] { w.Color, w.Size, w.Qty.ToString(), w.LengthCm.ToString("F1") }).ToList();
                            DwgAccess.AddTable(tr, gp.Value, header, rows, 8, 50);
                            if (total.Terms.Count > 0)
                            {
                                var h2 = new[] { "Наименование", "Общее кол-во, шт" };
                                var r2 = total.Terms.Select(t => new[] { t.Name, t.Qty.ToString() }).ToList();
                                DwgAccess.AddTable(tr, new Point3d(gp.Value.X + 180, gp.Value.Y, 0), h2, r2, 8, 80);
                            }
                        }
                    }
                    tr.Commit();
                }
                ed.WriteMessage("\n; AW33: спецификация построена.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! AW33: " + ex.Message); Plugin.Log(ex); }
        }

        /// <summary>Экспорт спецификации в CSV (для Excel/HTML).</summary>
        [CommandMethod("AW33-CSV")]
        public static void Aw33Csv()
        {
            try
            {
                var ed = Ed;
                var pages = new List<Aw33PageResult>();
                ed.WriteMessage("\n=== AW33-CSV: выдели тексты. Enter — сводная ===");
                while (true)
                {
                    ed.WriteMessage("\nВыделите лист (Enter — готово): ");
                    var sr = ed.GetSelection(new PromptSelectionOptions());
                    if (sr.Status != PromptStatus.OK) break;
                    var raws = new List<Aw33Parser.RawText>();
                    using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (var id in sr.Value.GetObjectIds())
                        {
                            var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            string text = ent is DBText dt ? dt.TextString : ent is MText mt ? mt.Contents : null;
                            if (text == null) continue;
                            Point3d pos = ent is DBText d2 ? d2.Position : ent is MText m2 ? m2.Location : default;
                            raws.Add(new Aw33Parser.RawText { Y = pos.Y, X = pos.X, Text = text });
                        }
                        tr.Commit();
                    }
                    pages.Add(Aw33Parser.ParsePage(raws));
                }
                var total = Aw33Parser.Merge(pages);
                var dlg = new System.Windows.Forms.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "specification.csv" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Марка/Цвет;Сечение;Кол-во;Длина,см");
                foreach (var w in total.Wires)
                    sb.AppendLine($"{w.Color};{w.Size};{w.Qty};{w.LengthCm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                sb.AppendLine();
                sb.AppendLine("Наименование;Кол-во,шт");
                foreach (var t in total.Terms)
                    sb.AppendLine($"{t.Name};{t.Qty}");
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
                ed.WriteMessage($"\n; CSV сохранён: {dlg.FileName}");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! AW33-CSV: " + ex.Message); Plugin.Log(ex); }
        }
    }
}
