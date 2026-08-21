using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using El.Core;

namespace El.Plugin
{
    /// <summary>Новые функции: EL-PATH, EL-CROSSING, AW33-HTML, EL-GRAPH-EXPORT.</summary>
    public static class FeatureCommands
    {
        private static Editor Ed => DwgAccess.Ed;

        // ============================================================
        // EL-PATH — путь между двумя линиями/подписями
        // ============================================================
        [CommandMethod("EL-PATH")]
        public static void ElPath()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }

                Ed.WriteMessage("\n=== EL-PATH: кликни на ЛИНИЮ или ТЕКСТ (начало) ===");
                int? a = PickLineOrText("→ Начало: ");
                if (a == null) return;
                Ed.WriteMessage("\n=== EL-PATH: кликни на ЛИНИЮ или ТЕКСТ (конец) ===");
                int? b = PickLineOrText("→ Конец: ");
                if (b == null) return;

                var path = GraphAlgorithms.FindPath(CommandState.Graph, a.Value, b.Value);
                if (path == null)
                {
                    Ed.WriteMessage("\n! Путь не найден — линии в разных цепях.");
                    return;
                }
                var segs = path.Select(id => CommandState.Lines.First(l => l.Id == id)).ToList();
                double len = segs.Sum(s => s.Length);
                DwgAccess.Highlight(segs, true);
                Ed.WriteMessage($"\n=== ПУТЬ: {path.Count} линий, длина {len:F2} мм ===");
                foreach (var l in segs)
                {
                    var texts = ChainTexts.NearPoint(l.Mid, CommandState.Texts, DwgAccess.DefaultTextRadius);
                    if (texts.Count > 0) Ed.WriteMessage($"\n  {l.A} → {l.B} [{string.Join(" ", texts)}]");
                }
                DwgAccess.ZoomTo(segs);
                Ed.WriteMessage("\nENTER — снять подсветку: ");
                Ed.GetString("");
                DwgAccess.Highlight(segs, false);
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-PATH: " + ex.Message); Plugin.Log(ex); }
        }

        /// <summary>Клик: LINE — сразу; TEXT/MTEXT — ближайшая по расстоянию линия.</summary>
        private static int? PickLineOrText(string prompt)
        {
            while (true)
            {
                var pe = new PromptEntityOptions(prompt);
                pe.SetRejectMessage("\n! LINE или TEXT");
                pe.AddAllowedClass(typeof(Line), true);
                pe.AddAllowedClass(typeof(DBText), true);
                pe.AddAllowedClass(typeof(MText), true);
                var pr = Ed.GetEntity(pe);
                if (pr.Status != PromptStatus.OK) return null;
                var id = pr.ObjectId;
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    if (ent is Line)
                    {
                        var l = CommandState.Lines.FirstOrDefault(x => (ObjectId)x.Tag == id);
                        tr.Commit();
                        return l?.Id;
                    }
                    // текст: позиция
                    Point3d pos = ent is DBText dt ? dt.Position : ((MText)ent).Location;
                    tr.Commit();
                    var p = new Point2D(pos.X, pos.Y);
                    // ближайшая линия по расстоянию до отрезка
                    LineSeg best = null;
                    double bestD = double.MaxValue;
                    foreach (var l in CommandState.Lines)
                    {
                        double d = PointToSegment(p, l.A, l.B);
                        if (d < bestD) { bestD = d; best = l; }
                    }
                    if (best == null) return null;
                    Ed.WriteMessage($"\n; Текст → ближайшая линия {best.A}—{best.B} (расст. {bestD:F1} мм)");
                    return best.Id;
                }
            }
        }

        private static double PointToSegment(Point2D p, Point2D a, Point2D b)
        {
            double abX = b.X - a.X, abY = b.Y - a.Y;
            double len2 = abX * abX + abY * abY;
            if (len2 < 1e-12) return p.Dist(a);
            double t = ((p.X - a.X) * abX + (p.Y - a.Y) * abY) / len2;
            t = Math.Max(0, Math.Min(1, t));
            double px = a.X + t * abX, py = a.Y + t * abY;
            return p.Dist(new Point2D(px, py));
        }

        // ============================================================
        // EL-CROSSING — пересечения линий без узла
        // ============================================================
        [CommandMethod("EL-CROSSING")]
        public static void ElCrossing()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Lines.Count < 2) { Ed.WriteMessage("\n! Мало LINE"); return; }
                var sw = Stopwatch.StartNew();
                var crossings = GraphAlgorithms.FindCrossings(CommandState.Lines, DwgAccess.DefaultTolerance);
                sw.Stop();

                Ed.WriteMessage($"\n=== EL-CROSSING: {crossings.Count} пересечений (за {sw.ElapsedMilliseconds} мс) ===");
                if (crossings.Count == 0) return;

                // маркеры-кружки на слое EL_CROSSING
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, "EL_CROSSING", 1);
                    var bt = (BlockTable)tr.GetObject(DwgAccess.Doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    double r = Math.Max(1.0, DwgAccess.DefaultTolerance * 4);
                    foreach (var c in crossings)
                    {
                        var circ = new Circle
                        {
                            Center = new Point3d(c.Point.X, c.Point.Y, 0),
                            Radius = r,
                            Layer = "EL_CROSSING",
                            ColorIndex = 1
                        };
                        ms.AppendEntity(circ);
                        tr.AddNewlyCreatedDBObject(circ, true);
                    }
                    tr.Commit();
                }

                int shown = 0;
                foreach (var c in crossings.Take(30))
                {
                    Ed.WriteMessage($"\n  X в ({c.Point.X:F1}, {c.Point.Y:F1}): LINE {c.LineA.A}—{c.LineA.B} × LINE {c.LineB.A}—{c.LineB.B}");
                    shown++;
                }
                if (crossings.Count > shown) Ed.WriteMessage($"\n  ... и ещё {crossings.Count - shown}");
                Ed.WriteMessage($"\n; Маркеры на слое EL_CROSSING (цвет 1). ENTER — зум к первому: ");
                Ed.GetString("");
                DwgAccess.ZoomTo(crossings[0].Point, 500);
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-CROSSING: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // AW33-HTML — спецификация в HTML (страницы + таблицы + расчёт + сводная)
        // ============================================================
        [CommandMethod("AW33-HTML")]
        public static void Aw33Html()
        {
            try
            {
                var pages = new List<Aw33PageResult>();
                Ed.WriteMessage("\n=== AW33-HTML: выделяй тексты И таблицы листов. Enter — генерация HTML ===");
                Ed.WriteMessage("\n(выделение захватывает TEXT/MTEXT и объекты TABLE)");
                while (true)
                {
                    Ed.WriteMessage("\nВыделите лист (Enter — готово): ");
                    var sr = Ed.GetSelection(new PromptSelectionOptions());
                    if (sr.Status != PromptStatus.OK) break;
                    var raws = new List<Aw33Parser.RawText>();
                    var page = new Aw33PageResult();
                    using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (var id in sr.Value.GetObjectIds())
                        {
                            var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            if (ent is Table tbl)
                            {
                                // таблица AutoCAD — «картинка» страницы
                                var td = new TableData();
                                int rows = tbl.Rows.Count, cols = tbl.Columns.Count;
                                for (int r = 0; r < rows; r++)
                                {
                                    var row = new List<string>();
                                    for (int c = 0; c < cols; c++)
                                    {
                                        try { row.Add(tbl.Cells[r, c].TextString ?? ""); }
                                        catch { row.Add(""); }
                                    }
                                    td.Cells.Add(row);
                                }
                                if (td.Rows > 0) page.Tables.Add(td);
                                continue;
                            }
                            string text = ent is DBText dt ? dt.TextString : ent is MText mt ? mt.Contents : null;
                            if (text == null) continue;
                            Point3d pos = ent is DBText d2 ? d2.Position : ent is MText m2 ? m2.Location : default;
                            raws.Add(new Aw33Parser.RawText { Y = pos.Y, X = pos.X, Text = text });
                        }
                        tr.Commit();
                    }
                    var parsed = Aw33Parser.ParsePage(raws);
                    foreach (var w in parsed.Wires) page.Wires.Add(w);
                    foreach (var t in parsed.Terms) page.Terms.Add(t);
                    pages.Add(page);
                    Ed.WriteMessage($"\n; Лист {pages.Count}: проводов={page.Wires.Count}, таблиц={page.Tables.Count}");
                }
                if (pages.Count == 0) { Ed.WriteMessage("\n! Ничего не выделено."); return; }
                var dlg = new System.Windows.Forms.SaveFileDialog
                {
                    Filter = "HTML (*.html)|*.html",
                    FileName = "specification.html"
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string dwg = Path.GetFileName(DwgAccess.Doc.Database.Filename);
                string html = Aw33HtmlReport.Build(pages, $"Спецификация — {dwg}");
                File.WriteAllText(dlg.FileName, html, new System.Text.UTF8Encoding(true));
                Ed.WriteMessage($"\n; HTML сохранён: {dlg.FileName}");
                try { Process.Start(dlg.FileName); } catch { }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! AW33-HTML: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-GRAPH-EXPORT — топология в Graphviz DOT (+PNG если есть dot)
        // ============================================================
        [CommandMethod("EL-GRAPH-EXPORT")]
        public static void ElGraphExport()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null) { Ed.WriteMessage("\n! Мало LINE"); return; }

                var dlg = new System.Windows.Forms.SaveFileDialog
                {
                    Filter = "Graphviz DOT (*.dot)|*.dot",
                    FileName = "scheme.gv"
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                var lineLabels = new Dictionary<int, string>();
                foreach (var l in CommandState.Lines)
                {
                    var texts = ChainTexts.NearPoint(l.Mid, CommandState.Texts, DwgAccess.DefaultTextRadius);
                    if (texts.Count > 0) lineLabels[l.Id] = string.Join(", ", texts);
                }
                string dot = DotExporter.ToDot(CommandState.Graph, CommandState.Texts, lineLabels, DwgAccess.DefaultTextRadius);
                File.WriteAllText(dlg.FileName, dot, new System.Text.UTF8Encoding(true));
                Ed.WriteMessage($"\n; DOT сохранён: {dlg.FileName}");

                // рендер PNG через dot (если найден)
                string dotExe = FindDot();
                if (dotExe != null)
                {
                    string png = Path.ChangeExtension(dlg.FileName, ".png");
                    var psi = new ProcessStartInfo(dotExe, $"\"{dlg.FileName}\" -Tpng -o\"{png}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit(30000);
                    }
                    if (File.Exists(png))
                    {
                        Ed.WriteMessage($"\n; PNG: {png}");
                        try { Process.Start(png); } catch { }
                    }
                }
                else
                {
                    Ed.WriteMessage("\n; dot (Graphviz) не найден — только .dot. Установите graphviz или откройте в webgraphviz.com");
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-GRAPH-EXPORT: " + ex.Message); Plugin.Log(ex); }
        }

        private static string FindDot()
        {
            try
            {
                var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in paths.Split(';'))
                {
                    string cand = Path.Combine(dir.Trim(), "dot.exe");
                    if (File.Exists(cand)) return cand;
                }
                string[] known =
                {
                    @"C:\Program Files\Graphviz\bin\dot.exe",
                    @"C:\Program Files (x86)\Graphviz\bin\dot.exe",
                    @"C:\Program Files\Graphviz2.38\bin\dot.exe"
                };
                return known.FirstOrDefault(File.Exists);
            }
            catch { return null; }
        }
    }
}
