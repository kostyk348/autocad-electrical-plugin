using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using El.Core;

namespace El.Plugin
{
    /// <summary>
    /// Конвейер «чертёж → данные → документ»:
    /// EL-REPORT (текущий чертёж), EL-PROJECT-REPORT (вся папка),
    /// EL-REVISION-DIFF (текущий vs OMNI-ревизия).
    /// </summary>
    public static class ReportCommands
    {
        private static Editor Ed => DwgAccess.Ed;

        /// <summary>Анализ чертежа: всё, что нужно для отчётов и диффов.</summary>
        private sealed class DbAnalysis
        {
            public List<LineSeg> Lines = new List<LineSeg>();
            public List<TextLabel> Texts = new List<TextLabel>();
            public WireGraph Graph;
            public List<List<int>> Chains = new List<List<int>>();
            public Aw33PageResult Wires = new Aw33PageResult();
            public Dictionary<string, int> Bom = new Dictionary<string, int>();
            public List<List<string>> ChainTextsList = new List<List<string>>();
            public int Isolated, Textless, Single;

            /// <summary>Собрать из открытой транзакции указанной базы.</summary>
            public static DbAnalysis Collect(Database db, Transaction tr)
            {
                var a = new DbAnalysis();
                a.Lines = DwgAccess.CollectLines(tr, db);
                a.Texts = DwgAccess.CollectTexts(tr, db);
                a.Bom = DwgAccess.CountBlocks(tr, db);
                if (a.Lines.Count >= 2)
                {
                    a.Graph = GraphBuilder.Build(a.Lines, DwgAccess.DefaultTolerance);
                    a.Chains = a.Graph.AllChains();
                    a.Isolated = a.Graph.Adj.Count(kv => kv.Value.Count == 0);
                    foreach (var ch in a.Chains)
                    {
                        var texts = ChainTexts.NearEnds(a.Graph, ch, a.Texts, DwgAccess.DefaultTextRadius);
                        a.ChainTextsList.Add(texts);
                        if (texts.Count == 0) a.Textless++;
                        if (ch.Count == 1) a.Single++;
                    }
                }
                // спецификация проводов: автоматический парсинг по всему ModelSpace
                var raws = new List<Aw33Parser.RawText>();
                foreach (var t in a.Texts)
                    raws.Add(new Aw33Parser.RawText { Y = t.Position.Y, X = t.Position.X, Text = t.Text });
                a.Wires = Aw33Parser.ParsePage(raws);
                return a;
            }
        }

        // ============================================================
        // EL-REPORT — полный HTML-отчёт по текущему чертежу
        // ============================================================
        [CommandMethod("EL-REPORT")]
        public static void ElReport()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var db = doc.Database;
                Ed.WriteMessage("\n=== EL-REPORT: сбор данных чертежа...");
                DbAnalysis a;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    a = DbAnalysis.Collect(db, tr);
                    tr.Commit();
                }

                var d = new ReportData
                {
                    Title = "Отчёт по чертежу",
                    Source = Path.GetFileName(db.Filename),
                    SheetInfo = ReadSheetInfo(),
                    Lines = a.Lines.Count,
                    Texts = a.Texts.Count,
                    Chains = a.Chains.Count,
                    DefectsIsolated = a.Isolated,
                    DefectsTextless = a.Textless,
                    DefectsSingle = a.Single,
                    DefectsGaps = CountGaps(a),
                    Wires = a.Wires,
                    Bom = a.Bom,
                    Connections = BuildConnections(a)
                };
                d.CheckDetails = Commands.BuildCheckReport().Lines;

                var dlg = new System.Windows.Forms.SaveFileDialog { Filter = "HTML (*.html)|*.html", FileName = "el-report.html" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, ReportHtml.Build(d), new UTF8Encoding(true));
                Ed.WriteMessage($"\n; Отчёт сохранён: {dlg.FileName}");
                try { Process.Start(dlg.FileName); } catch { }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-REPORT: " + ex.Message); }
        }

        // ============================================================
        // EL-PROJECT-REPORT — сводный отчёт по всем DWG в папке
        // ============================================================
        [CommandMethod("EL-PROJECT-REPORT")]
        public static void ElProjectReport()
        {
            try
            {
                string folder = Ed.GetString(new PromptStringOptions("\nПапка с DWG (Enter — папка текущего чертежа): ") { AllowSpaces = true }).StringResult;
                if (string.IsNullOrEmpty(folder))
                    folder = Path.GetDirectoryName(DwgAccess.Doc.Database.Filename);
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { Ed.WriteMessage("\n! Папка не найдена."); return; }

                var files = Directory.GetFiles(folder, "*.dwg", SearchOption.TopDirectoryOnly)
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                if (files.Count == 0) { Ed.WriteMessage("\n! DWG в папке нет."); return; }

                Ed.WriteMessage($"\n; Анализ {files.Count} файлов (фоновое чтение)...");
                var sw = Stopwatch.StartNew();
                var sheets = new List<ReportData>();
                int totalWires = 0, totalBom = 0, totalDefects = 0;

                foreach (var f in files)
                {
                    try
                    {
                        using (var db = new Database(false, true))
                        {
                            db.ReadDwgFile(f, FileOpenMode.OpenForReadAndAllShare, true, "");
                            DbAnalysis a;
                            using (var tr = db.TransactionManager.StartTransaction())
                            {
                                a = DbAnalysis.Collect(db, tr);
                                tr.Commit();
                            }
                            var ti = AutomationCommands.ReadTitleBlockPublic(f);
                            var d = new ReportData
                            {
                                Title = "Лист",
                                Source = Path.GetFileName(f),
                                SheetInfo = $"{ti.Sheet}/{ti.Total} — {ti.Title}",
                                Lines = a.Lines.Count,
                                Texts = a.Texts.Count,
                                Chains = a.Chains.Count,
                                DefectsIsolated = a.Isolated,
                                DefectsTextless = a.Textless,
                                DefectsSingle = a.Single,
                                Wires = a.Wires,
                                Bom = a.Bom
                            };
                            sheets.Add(d);
                            totalWires += a.Wires.Wires.Count;
                            totalBom += a.Bom.Count;
                            totalDefects += a.Isolated + a.Textless + a.Single;
                            Ed.WriteMessage($"\n  {Path.GetFileName(f)}: {a.Lines.Count} линий, {a.Chains.Count} цепей, проводов {a.Wires.Wires.Count}");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Ed.WriteMessage($"\n  ! {Path.GetFileName(f)}: {ex.Message}");
                    }
                }
                sw.Stop();
                Ed.WriteMessage($"\n; Анализ завершён за {sw.ElapsedMilliseconds} мс");

                var html = BuildProjectHtml(folder, sheets, totalWires, totalBom, totalDefects);
                var dlg = new System.Windows.Forms.SaveFileDialog { Filter = "HTML (*.html)|*.html", FileName = "project-report.html" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, html, new UTF8Encoding(true));
                Ed.WriteMessage($"\n; Сводный отчёт: {dlg.FileName}");
                try { Process.Start(dlg.FileName); } catch { }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-PROJECT-REPORT: " + ex.Message); }
        }

        private static string BuildProjectHtml(string folder, List<ReportData> sheets,
                                               int totalWires, int totalBom, int totalDefects)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>Сводный отчёт по проекту</title><style>");
            sb.AppendLine("  body{font-family:'Segoe UI',Arial,sans-serif;margin:16px;color:#222}");
            sb.AppendLine("  h1{font-size:20px}.sub{color:#666;font-size:13px;margin-bottom:14px}");
            sb.AppendLine("  table{border-collapse:collapse;width:100%;margin-bottom:14px}");
            sb.AppendLine("  th,td{border:1px solid #999;padding:3px 8px;font-size:13px;text-align:left}");
            sb.AppendLine("  th{background:#e8e8e8}.num{text-align:right}");
            sb.AppendLine("  .bad{color:#c62828;font-weight:600}.ok{color:#2e7d32}");
            sb.AppendLine("  h2{font-size:15px;border-bottom:2px solid #1a4f8b;padding-bottom:3px;margin-top:24px}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine("<h1>Сводный отчёт по проекту</h1>");
            sb.AppendLine($"<div class=\"sub\">{folder} · {DateTime.Now:dd.MM.yyyy HH:mm} · листов: {sheets.Count}</div>");

            // сводная таблица
            sb.AppendLine("<h2>Листы</h2>");
            sb.AppendLine("<table><tr><th>Файл</th><th>Лист</th><th>Наименование</th><th class=\"num\">Линий</th><th class=\"num\">Цепей</th><th class=\"num\">Проводов</th><th class=\"num\">Блоков</th><th class=\"num\">Дефектов</th></tr>");
            foreach (var s in sheets)
            {
                int def = s.DefectsIsolated + s.DefectsTextless + s.DefectsSingle;
                string cls = def > 0 ? "bad" : "ok";
                string[] sheetParts = s.SheetInfo.Split(new[] { " — " }, StringSplitOptions.None);
                string sheetNo = sheetParts.Length > 0 ? sheetParts[0] : "";
                string sheetTitle = sheetParts.Length > 1 ? sheetParts[1] : "";
                sb.AppendLine($"<tr><td>{H(s.Source)}</td><td>{H(sheetNo)}</td><td>{H(sheetTitle)}</td><td class=\"num\">{s.Lines}</td><td class=\"num\">{s.Chains}</td><td class=\"num\">{s.Wires.Wires.Count}</td><td class=\"num\">{s.Bom.Count}</td><td class=\"num {cls}\">{def}</td></tr>");
            }
            sb.AppendLine("</table>");

            // итоги
            sb.AppendLine("<h2>Итоги</h2>");
            sb.AppendLine("<table><tr><th>Показатель</th><th class=\"num\">Значение</th></tr>");
            sb.AppendLine($"<tr><td>Листов</td><td class=\"num\">{sheets.Count}</td></tr>");
            sb.AppendLine($"<tr><td>Типов проводов (всего)</td><td class=\"num\">{totalWires}</td></tr>");
            sb.AppendLine($"<tr><td>Типов блоков (всего)</td><td class=\"num\">{totalBom}</td></tr>");
            sb.AppendLine($"<tr><td>Дефектов (всего)</td><td class=\"num\">{totalDefects}</td></tr>");
            sb.AppendLine("</table>");

            // провода по листам (только те, где есть)
            var sheetsWithWires = sheets.Where(s => s.Wires.Wires.Count > 0).ToList();
            if (sheetsWithWires.Count > 0)
            {
                sb.AppendLine("<h2>Провода по листам</h2>");
                foreach (var s in sheetsWithWires)
                {
                    sb.AppendLine($"<h3>{H(s.Source)}</h3>");
                    sb.AppendLine("<table><tr><th>Цвет</th><th>Сечение</th><th class=\"num\">Кол-во</th><th class=\"num\">Длина, м</th></tr>");
                    foreach (var w in s.Wires.Wires)
                        sb.AppendLine($"<tr><td>{H(w.Color)}</td><td>{H(w.Size)}</td><td class=\"num\">{w.Qty}</td><td class=\"num\">{(w.LengthCm / 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}</td></tr>");
                    sb.AppendLine("</table>");
                }
            }
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        // ============================================================
        // EL-REVISION-DIFF — текущий чертёж vs OMNI-ревизия
        // ============================================================
        [CommandMethod("EL-REVISION-DIFF")]
        public static void ElRevisionDiff()
        {
            try
            {
                var files = OmniCommands.SnapshotFilesPublic();
                if (files.Count == 0) { Ed.WriteMessage("\n[EL] Слепков OMNI нет. Сначала OMNI-SNAP."); return; }
                Ed.WriteMessage("\n--- Ревизии OMNI ---");
                for (int i = 0; i < files.Count; i++)
                    Ed.WriteMessage($"\n[{i + 1}] {Path.GetFileName(files[i])}");
                var pi = Ed.GetInteger(new PromptIntegerOptions("\nНомер ревизии для сравнения (0 — отмена): ") { AllowNegative = false });
                if (pi.Status != PromptStatus.OK || pi.Value <= 0 || pi.Value > files.Count) return;
                string snapPath = files[pi.Value - 1];

                // анализ ревизии (фоновое чтение)
                DbAnalysis oldA;
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(snapPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        oldA = DbAnalysis.Collect(db, tr);
                        tr.Commit();
                    }
                }
                // анализ текущего
                DbAnalysis newA;
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    newA = DbAnalysis.Collect(DwgAccess.Doc.Database, tr);
                    tr.Commit();
                }

                var diff = new SpecDiff.DiffResult();
                diff.Wires = SpecDiff.CompareWires(oldA.Wires, newA.Wires);
                diff.Bom = SpecDiff.CompareBom(oldA.Bom, newA.Bom);
                var topo = SpecDiff.CompareTopology(oldA.ChainTextsList, newA.ChainTextsList);
                diff.TopologyAdded = topo.Added;
                diff.TopologyRemoved = topo.Removed;

                string title = $"Сравнение: {Path.GetFileName(DwgAccess.Doc.Database.Filename)} ↔ {Path.GetFileName(snapPath)}";
                var dlg = new System.Windows.Forms.SaveFileDialog { Filter = "HTML (*.html)|*.html", FileName = "revision-diff.html" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, DiffHtml.Build(title, title, diff), new UTF8Encoding(true));
                Ed.WriteMessage("\n=== ИТОГ СРАВНЕНИЯ ===");
                Ed.WriteMessage($"\nПровода: +{diff.WiresAdded} −{diff.WiresRemoved} ~{diff.WiresChanged}");
                Ed.WriteMessage($"\nБлоки: +{diff.BomAdded} −{diff.BomRemoved} ~{diff.BomChanged}");
                Ed.WriteMessage($"\nЦепи: +{diff.TopologyAdded.Count} −{diff.TopologyRemoved.Count}");
                Ed.WriteMessage($"\n; Отчёт: {dlg.FileName}");
                try { Process.Start(dlg.FileName); } catch { }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-REVISION-DIFF: " + ex.Message); }
        }

        // ---------- помощники ----------

        private static int CountGaps(DbAnalysis a)
        {
            if (a.Lines.Count < 2) return 0;
            var gr = Commands.FindGapsPublic(a.Lines, DwgAccess.DefaultTolerance);
            return gr.Count;
        }

        private static List<string[]> BuildConnections(DbAnalysis a)
        {
            var rows = new List<string[]>();
            if (a.Graph == null) return rows;
            for (int i = 0; i < a.Chains.Count; i++)
            {
                var terms = a.Graph.ChainTerminals(a.Chains[i], DwgAccess.DefaultTolerance);
                string from = "", to = "";
                if (terms.Count >= 2)
                {
                    from = string.Join(" ", ChainTexts.NearPoint(terms[0], a.Texts, DwgAccess.DefaultTextRadius));
                    to = string.Join(" ", ChainTexts.NearPoint(terms[1], a.Texts, DwgAccess.DefaultTextRadius));
                }
                else if (terms.Count == 1)
                    from = "[петля] " + string.Join(" ", ChainTexts.NearPoint(terms[0], a.Texts, DwgAccess.DefaultTextRadius));
                else
                    from = $"[кольцо #{i + 1}]";
                rows.Add(new[] { (i + 1).ToString(), from, to });
            }
            return rows;
        }

        private static string ReadSheetInfo()
        {
            try
            {
                var info = AutomationCommands.ReadTitleBlockPublic(DwgAccess.Doc.Database.Filename);
                return $"{info.Sheet}/{info.Total} — {info.Title}";
            }
            catch { return ""; }
        }

        private static string H(string s) => s == null ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
