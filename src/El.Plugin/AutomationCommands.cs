using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>Автоматизация рутины: BOM, штампы, реестр листов, XREF, отчёты, нумерация цепей.</summary>
    public static class AutomationCommands
    {
        private static Editor Ed => DwgAccess.Ed;

        // ============================================================
        // EL-BOM — спецификация компонентов (подсчёт вхождений блоков)
        // ============================================================
        [CommandMethod("EL-BOM")]
        public static void ElBom()
        {
            try
            {
                var doc = DwgAccess.Doc;
                Ed.WriteMessage("\n=== EL-BOM: подсчёт вхождений блоков ===");
                Ed.WriteMessage("\nВыберите блоки (Enter — все в ModelSpace): ");
                var sr = Ed.GetSelection(new PromptSelectionOptions());
                ObjectId[] ids = sr.Status == PromptStatus.OK
                    ? sr.Value.GetObjectIds()
                    : null;

                var counts = new SortedDictionary<string, int>();
                var layerOf = new Dictionary<string, string>();
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    var wanted = ids != null ? new HashSet<ObjectId>(ids) : null;
                    foreach (ObjectId id in ms)
                    {
                        if (id.ObjectClass.DxfName != "INSERT") continue;
                        if (wanted != null && !wanted.Contains(id)) continue;
                        var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                        var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                        if (btr.IsFromExternalReference || btr.IsLayout) continue;
                        string name = btr.Name;
                        counts.TryGetValue(name, out int c);
                        counts[name] = c + 1;
                        if (!layerOf.ContainsKey(name)) layerOf[name] = br.Layer;
                    }
                    tr.Commit();
                }
                if (counts.Count == 0) { Ed.WriteMessage("\n! Блоки не найдены."); return; }

                Ed.WriteMessage($"\n--- ВСЕГО: {counts.Count} типов блоков, {counts.Values.Sum()} вхождений ---");
                var bomDlg = new El.Plugin.Ui.BomDialog(counts.ToList());
                var dr = bomDlg.Show();
                if (dr == System.Windows.Forms.DialogResult.Cancel) return;
                if (bomDlg.ExportCsv)
                {
                    var csvDlg = new System.Windows.Forms.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "bom.csv" };
                    if (csvDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Блок;Слой;Кол-во");
                        foreach (var kv in counts)
                            sb.AppendLine($"{kv.Key};{layerOf[kv.Key]};{kv.Value}");
                        System.IO.File.WriteAllText(csvDlg.FileName, sb.ToString(), new UTF8Encoding(true));
                        Ed.WriteMessage($"\n; BOM сохранён: {csvDlg.FileName}");
                    }
                    return;
                }
                if (!bomDlg.InsertTable) return;

                var pp = Ed.GetPoint("\nТочка вставки таблицы (Enter — без таблицы): ");
                if (pp.Status == PromptStatus.OK)
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var header = new[] { "Блок", "Слой", "Кол-во, шт" };
                        var rows = counts.Select(kv => new[] { kv.Key, layerOf[kv.Key], kv.Value.ToString() }).ToList();
                        DwgAccess.AddTable(tr, pp.Value, header, rows, 6, 80);
                        tr.Commit();
                    }
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-BOM: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-TITLE — автозаполнение штампа (лист N, всего, дата, обозначение)
        // ============================================================
        [CommandMethod("EL-TITLE")]
        public static void ElTitle()
        {
            try
            {
                var doc = DwgAccess.Doc;
                Ed.WriteMessage("\n=== EL-TITLE: автозаполнение штампа ===");
                var pe = new PromptEntityOptions("\n→ Кликни на блок штампа (или Enter — отмена): ");
                pe.SetRejectMessage("\n! Это не блок");
                pe.AddAllowedClass(typeof(BlockReference), true);
                pe.AllowNone = true;
                var pr = Ed.GetEntity(pe);
                if (pr.Status != PromptStatus.OK) return;
                var blockId = pr.ObjectId;

                // запросы значений в диалоге
                var tbd = new El.Plugin.Ui.TitleBlockDialog(DateTime.Now.ToString("dd.MM.yyyy"));
                if (tbd.Show() != System.Windows.Forms.DialogResult.OK) return;
                int sheet = tbd.Sheet;
                int total = tbd.Total;
                string dateStr = tbd.Date;
                string design = tbd.Design;

                int updated = 0;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    // обновляем выбранный блок
                    updated += FillAttributes(tr, blockId, sheet, total, dateStr, design);

                    // и все остальные вхождения того же блока?
                    var br0 = (BlockReference)tr.GetObject(blockId, OpenMode.ForRead);
                    ObjectId btrId = br0.BlockTableRecord;
                    string blockName = ((BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead)).Name;
                    var ask = new PromptKeywordOptions($"\nОбновить ВСЕ вхождения блока \"{blockName}\" ({updated} шт)? [Да/Нет] <Нет>: ");
                    ask.Keywords.Add("Да"); ask.Keywords.Add("Нет"); ask.Keywords.Default = "Нет";
                    var kr = Ed.GetKeywords(ask);
                    if (kr.Status == PromptStatus.OK && kr.StringResult == "Да")
                    {
                        var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms)
                        {
                            if (id.ObjectClass.DxfName != "INSERT") continue;
                            var cand = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                            if (cand.BlockTableRecord != btrId) continue;
                            if (id != blockId) updated += FillAttributes(tr, id, sheet, total, dateStr, design);
                        }
                    }
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Штамп обновлён: {updated} вхождений");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-TITLE: " + ex.Message); Plugin.Log(ex); }
        }

        /// <summary>Заполнение атрибутов по тегам (регистронезависимо).</summary>
        private static int FillAttributes(Transaction tr, ObjectId blockId, int sheet, int total, string date, string design)
        {
            var br = (BlockReference)tr.GetObject(blockId, OpenMode.ForRead);
            if (br.AttributeCollection.Count == 0) return 0;
            int n = 0;
            foreach (ObjectId attId in br.AttributeCollection)
            {
                var att = (AttributeReference)tr.GetObject(attId, OpenMode.ForWrite);
                string tag = att.Tag.ToUpperInvariant().Trim();
                if (tag.Contains("ЛИСТ") && (tag.Contains("N") || tag.Contains("№") || tag.Contains("ИЗМ")))
                {
                    if (tag.Contains("ВСЕГО") || tag.Contains("ИЗМ")) att.TextString = total.ToString();
                    else att.TextString = sheet.ToString();
                    n++;
                }
                else if (tag.Contains("ДАТА")) { att.TextString = date; n++; }
                else if (tag.Contains("ОБОЗНАЧ") && !string.IsNullOrEmpty(design)) { att.TextString = design; n++; }
            }
            return n;
        }

        // ============================================================
        // EL-SHEET-LIST — реестр листов проекта (все DWG в папке, фоновое чтение)
        // ============================================================
        [CommandMethod("EL-SHEET-LIST")]
        public static void ElSheetList()
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

                Ed.WriteMessage($"\n; Читаю {files.Count} файлов (без открытия в редакторе)...");
                var rows = new List<string[]>();
                var sw = Stopwatch.StartNew();
                foreach (var f in files)
                {
                    var info = ReadTitleBlock(f);
                    rows.Add(new[] { Path.GetFileName(f), info.Sheet, info.Total, info.Title });
                }
                sw.Stop();
                Ed.WriteMessage($"\n; Готово за {sw.ElapsedMilliseconds} мс");

                foreach (var r in rows)
                    Ed.WriteMessage($"\n  {r[0]}: лист {r[1]}/{r[2]} — {r[3]}");

                var dlg = new System.Windows.Forms.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "sheet-list.csv" };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine("Файл;Лист;Всего;Наименование");
                foreach (var r in rows)
                    sb.AppendLine($"{r[0]};{r[1]};{r[2]};{r[3]}");
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                Ed.WriteMessage($"\n; Реестр сохранён: {dlg.FileName}");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-SHEET-LIST: " + ex.Message); Plugin.Log(ex); }
        }

        public sealed class TitleInfo
        {
            public string Sheet = "";
            public string Total = "";
            public string Title = "";
        }

        /// <summary>Публичная обёртка чтения штампа (для отчётов).</summary>
        public static TitleInfo ReadTitleBlockPublic(string dwgPath) => ReadTitleBlock(dwgPath);

        /// <summary>Прочитать номер листа/наименование из штампа DWG без открытия в UI.</summary>
        private static TitleInfo ReadTitleBlock(string dwgPath)
        {
            var res = new TitleInfo();
            try
            {
                using (var db = new Database(false, true))
                {
                    db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        foreach (ObjectId id in ms)
                        {
                            if (id.ObjectClass.DxfName != "INSERT") continue;
                            var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                            if (br.AttributeCollection.Count == 0) continue;
                            string sheet = "", total = "", title = "";
                            foreach (ObjectId attId in br.AttributeCollection)
                            {
                                var att = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                                string tag = att.Tag.ToUpperInvariant();
                                if (tag.Contains("ЛИСТ") && (tag.Contains("N") || tag.Contains("№"))) sheet = att.TextString.Trim();
                                else if (tag.Contains("ВСЕГО")) total = att.TextString.Trim();
                                else if (tag.Contains("НАИМЕН")) title = att.TextString.Trim();
                                else if (tag.Contains("ОБОЗНАЧ") && title == "") title = att.TextString.Trim();
                            }
                            if (sheet != "" || total != "" || title != "")
                            {
                                res.Sheet = sheet; res.Total = total; res.Title = title;
                                break;
                            }
                        }
                        tr.Commit();
                    }
                }
            }
            catch { }
            return res;
        }

        // ============================================================
        // EL-XREF-LIST — статусы внешних ссылок
        // ============================================================
        [CommandMethod("EL-XREF-LIST")]
        public static void ElXrefList()
        {
            try
            {
                var db = DwgAccess.Doc.Database;
                var graph = db.GetHostDwgXrefGraph(true);
                if (graph == null || graph.NumNodes <= 1)
                {
                    Ed.WriteMessage("\n=== EL-XREF-LIST: внешних ссылок нет ===");
                    return;
                }
                Ed.WriteMessage($"\n=== EL-XREF-LIST: {graph.NumNodes - 1} XREF ===");
                var rows = new List<string[]>();
                for (int i = 1; i < graph.NumNodes; i++)
                {
                    var node = graph.GetXrefNode(i);
                    if (node == null) continue;
                    string status = node.XrefStatus == XrefStatus.Resolved ? "OK"
                                  : node.XrefStatus == XrefStatus.FileNotFound ? "ФАЙЛ НЕ НАЙДЕН"
                                  : node.XrefStatus == XrefStatus.Unloaded ? "ВЫГРУЖЕН"
                                  : node.XrefStatus.ToString();
                    if (node.IsNested) status += " (вложенный)";
                    string name = node.Name ?? "";
                    Ed.WriteMessage($"\n  [{status}] {name}");
                    rows.Add(new[] { name, status });
                }
                var ask = new PromptKeywordOptions("\nСохранить в CSV? [Да/Нет] <Нет>: ");
                ask.Keywords.Add("Да"); ask.Keywords.Add("Нет"); ask.Keywords.Default = "Нет";
                var kr = Ed.GetKeywords(ask);
                if (kr.Status == PromptStatus.OK && kr.StringResult == "Да")
                {
                    var dlg = new System.Windows.Forms.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "xref-list.csv" };
                    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Имя;Статус;Файл");
                        foreach (var r in rows) sb.AppendLine($"{r[0]};{r[1]};{r[2]}");
                        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                        Ed.WriteMessage($"\n; Сохранено: {dlg.FileName}");
                    }
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-XREF-LIST: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-CHECK-REPORT — дефектоскоп + отчёт в файл
        // ============================================================
        [CommandMethod("EL-CHECK-REPORT")]
        public static void ElCheckReport()
        {
            try
            {
                var res = Commands.BuildCheckReport();
                foreach (var line in res.Lines) Ed.WriteMessage(line);

                var dlg = new System.Windows.Forms.SaveFileDialog
                {
                    Filter = "Markdown (*.md)|*.md|HTML (*.html)|*.html",
                    FileName = "el-check-report.md"
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
                string dwg = Path.GetFileName(DwgAccess.Doc.Database.Filename);
                string body;
                if (ext == ".html")
                {
                    body = $@"<!DOCTYPE html><html lang=""ru""><head><meta charset=""utf-8""><title>EL-CHECK — {dwg}</title>
<style>body{{font-family:'Segoe UI',sans-serif;margin:16px}}table{{border-collapse:collapse}}td,th{{border:1px solid #999;padding:4px 8px;font-size:13px}}th{{background:#e8e8e8}}</style></head><body>
<h2>EL-CHECK — {dwg}</h2>
<p>Сформировано {DateTime.Now:dd.MM.yyyy HH:mm}</p>
<table>
<tr><th>Дефект</th><th>Кол-во</th></tr>
<tr><td>Изолированные линии</td><td>{res.Isolated}</td></tr>
<tr><td>Близкие разрывы</td><td>{res.Gaps}</td></tr>
<tr><td>Дубликаты текста</td><td>{res.Duplicates}</td></tr>
<tr><td>Цепи без подписей</td><td>{res.Textless}</td></tr>
<tr><td>Цепи из одной линии</td><td>{res.SingleLines}</td></tr>
</table>
<pre>{System.Net.WebUtility.HtmlEncode(string.Join("\n", res.Lines))}</pre></body></html>";
                }
                else
                {
                    body = $"# EL-CHECK — {dwg}\n\nСформировано {DateTime.Now:dd.MM.yyyy HH:mm}\n\n" +
                           "| Дефект | Кол-во |\n|---|---|\n" +
                           $"| Изолированные линии | {res.Isolated} |\n" +
                           $"| Близкие разрывы | {res.Gaps} |\n" +
                           $"| Дубликаты текста | {res.Duplicates} |\n" +
                           $"| Цепи без подписей | {res.Textless} |\n" +
                           $"| Цепи из одной линии | {res.SingleLines} |\n\n```\n{string.Join("\n", res.Lines)}\n```\n";
                }
                File.WriteAllText(dlg.FileName, body, new UTF8Encoding(true));
                Ed.WriteMessage($"\n; Отчёт сохранён: {dlg.FileName}");
                try { Process.Start(dlg.FileName); } catch { }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-CHECK-REPORT: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-AUTOTAG — номера выбранных цепей (без префикса)
        // ============================================================
        [CommandMethod("EL-AUTOTAG")]
        public static void ElAutoTag()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null || CommandState.Chains.Count == 0) { Ed.WriteMessage("\n! Нет цепей"); return; }

                Ed.WriteMessage("\n=== EL-AUTOTAG: выбери линии (Enter — отмена) ===");
                Ed.WriteMessage("\nКаждая затронутая цепь получит ОДИН номер (1, 2, 3…).");
                var sr = Ed.GetSelection(new PromptSelectionOptions());
                if (sr.Status != PromptStatus.OK) return;

                // какие цепи затронуты выбором
                var selected = new HashSet<int>();
                foreach (var id in sr.Value.GetObjectIds())
                {
                    var line = CommandState.Lines.FirstOrDefault(l => (ObjectId)l.Tag == id);
                    if (line == null) continue;
                    var chain = CommandState.Graph.Trace(line.Id);
                    foreach (var cid in chain) selected.Add(cid);
                }
                if (selected.Count == 0) { Ed.WriteMessage("\n! Линии не найдены в графе."); return; }

                double h = Ed.GetDouble(new PromptDoubleOptions("\nВысота текста (Enter — 2.5): ") { DefaultValue = 2.5 }).Value;

                // нумерация только затронутых цепей (по порядку их номера в схеме)
                var taggedChains = CommandState.Chains.Where(ch => ch.Any(selected.Contains)).ToList();
                int tagged = 0;
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, "EL_TAGS", 30);
                    var bt = (BlockTable)tr.GetObject(DwgAccess.Doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    int num = 1;
                    foreach (var ch in taggedChains)
                    {
                        // самая длинная линия цепи (в выбранной части) — туда номер
                        LineSeg best = null;
                        double bestLen = -1;
                        foreach (var id in ch)
                        {
                            if (!selected.Contains(id)) continue;
                            var l = CommandState.Lines.First(x => x.Id == id);
                            if (l.Length > bestLen) { bestLen = l.Length; best = l; }
                        }
                        if (best == null) continue;
                        var mid = new Point3d(best.Mid.X, best.Mid.Y, 0);
                        double r = h * 0.7;
                        var circ = new Circle { Center = mid, Radius = r, Layer = "EL_TAGS", ColorIndex = 30 };
                        ms.AppendEntity(circ);
                        tr.AddNewlyCreatedDBObject(circ, true);
                        var mt = new MText { Location = mid, Contents = num.ToString(), TextHeight = r * 0.65, Attachment = AttachmentPoint.MiddleCenter, Layer = "EL_TAGS" };
                        ms.AppendEntity(mt);
                        tr.AddNewlyCreatedDBObject(mt, true);
                        tagged++;
                        num++;
                    }
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Пронумеровано цепей: {tagged} (слой EL_TAGS, номера без префикса).");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-AUTOTAG: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-JOIN — объединение разрозненных LINE в полилинии
        // ============================================================
        [CommandMethod("EL-JOIN")]
        public static void ElJoin()
        {
            try
            {
                var doc = DwgAccess.Doc;
                Ed.WriteMessage("\n=== EL-JOIN: объединение LINE в полилинии ===");
                Ed.WriteMessage("\nВыберите линии (Enter — все в ModelSpace): ");
                var sr = Ed.GetSelection(new PromptSelectionOptions());
                var selectedIds = sr.Status == PromptStatus.OK
                    ? new HashSet<ObjectId>(sr.Value.GetObjectIds())
                    : null;

                List<LineSeg> lines;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    lines = DwgAccess.CollectLines(tr);
                    tr.Commit();
                }
                if (selectedIds != null)
                    lines = lines.Where(l => selectedIds.Contains((ObjectId)l.Tag)).ToList();
                if (lines.Count == 0) { Ed.WriteMessage("\n! Линии не выбраны."); return; }

                var segs = PolylineBuilder.BuildSegments(lines, DwgAccess.DefaultTolerance);
                int created = 0;
                int skipped = 0;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    foreach (var seg in segs)
                    {
                        if (seg.Points.Count < 2) continue;
                        // пропускаем одиночные линии — из них полилиния не нужна
                        if (seg.LineIds.Count == 1)
                        {
                            skipped++;
                            continue;
                        }
                        var pl = new Polyline();
                        for (int i = 0; i < seg.Points.Count; i++)
                            pl.AddVertexAt(i, new Point2d(seg.Points[i].X, seg.Points[i].Y), 0, 0, 0);
                        // слой — как у первой линии
                        var firstLine = (Line)tr.GetObject((ObjectId)lines.First(l => l.Id == seg.LineIds[0]).Tag, OpenMode.ForRead);
                        pl.Layer = firstLine.Layer;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        created++;
                    }
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Полилиний создано: {created} (одиночных линий пропущено: {skipped})");

                if (created > 0)
                {
                    var ask = new PromptKeywordOptions("\nУдалить исходные LINE? [Да/Нет] <Да>: ");
                    ask.Keywords.Add("Да"); ask.Keywords.Add("Нет"); ask.Keywords.Default = "Да";
                    var kr = Ed.GetKeywords(ask);
                    if (kr.Status == PromptStatus.OK && kr.StringResult == "Да")
                    {
                        using (var tr = doc.Database.TransactionManager.StartTransaction())
                        {
                            foreach (var l in lines)
                            {
                                var id = (ObjectId)l.Tag;
                                if (!id.IsValid || id.IsErased) continue;
                                var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite, true);
                                if (ent.IsErased) continue;
                                ent.Erase();
                            }
                            tr.Commit();
                        }
                        Ed.WriteMessage("\n; Исходные LINE удалены.");
                    }
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-JOIN: " + ex.Message); Plugin.Log(ex); }
        }
    }
}
