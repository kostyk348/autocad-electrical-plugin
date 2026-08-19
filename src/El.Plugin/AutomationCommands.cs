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
                foreach (var kv in counts)
                    Ed.WriteMessage($"\n  {kv.Key}: {kv.Value} шт");

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
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-BOM: " + ex.Message); }
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

                // запросы значений
                var all = Ed.GetInteger(new PromptIntegerOptions("\nЛист №") { AllowNegative = false, AllowZero = false });
                if (all.Status != PromptStatus.OK) return;
                int sheet = all.Value;
                var tot = Ed.GetInteger(new PromptIntegerOptions("\nВсего листов") { AllowNegative = false });
                int total = tot.Status == PromptStatus.OK ? tot.Value : 1;
                var date = Ed.GetString(new PromptStringOptions($"\nДата (Enter — {DateTime.Now:dd.MM.yyyy}): ") { AllowSpaces = true });
                string dateStr = string.IsNullOrEmpty(date.StringResult) ? DateTime.Now.ToString("dd.MM.yyyy") : date.StringResult;
                var name = Ed.GetString(new PromptStringOptions("\nОбозначение (Enter — пропустить): ") { AllowSpaces = true });
                string design = name.StringResult;

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
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-TITLE: " + ex.Message); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-SHEET-LIST: " + ex.Message); }
        }

        private sealed class TitleInfo
        {
            public string Sheet = "";
            public string Total = "";
            public string Title = "";
        }

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
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-XREF-LIST: " + ex.Message); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-CHECK-REPORT: " + ex.Message); }
        }

        // ============================================================
        // EL-AUTOTAG — номера цепей C1..Cn в кружках
        // ============================================================
        [CommandMethod("EL-AUTOTAG")]
        public static void ElAutoTag()
        {
            try
            {
                CommandState.Refresh();
                if (CommandState.Graph == null || CommandState.Chains.Count == 0) { Ed.WriteMessage("\n! Нет цепей"); return; }
                var pref = Ed.GetString(new PromptStringOptions("\nПрефикс номера (Enter — C): ") { AllowSpaces = true }).StringResult;
                if (string.IsNullOrEmpty(pref)) pref = "C";
                double h = Ed.GetDouble(new PromptDoubleOptions("\nВысота текста (Enter — 2.5): ") { DefaultValue = 2.5 }).Value;

                int tagged = 0;
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, "EL_TAGS", 30);
                    var bt = (BlockTable)tr.GetObject(DwgAccess.Doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    int i = 0;
                    foreach (var ch in CommandState.Chains)
                    {
                        i++;
                        // самая длинная линия цепи — туда ставим номер
                        LineSeg best = null;
                        double bestLen = -1;
                        foreach (var id in ch)
                        {
                            var l = CommandState.Lines.First(x => x.Id == id);
                            if (l.Length > bestLen) { bestLen = l.Length; best = l; }
                        }
                        if (best == null) continue;
                        var mid = new Point3d(best.Mid.X, best.Mid.Y, 0);
                        double r = h * 0.7;
                        var circ = new Circle { Center = mid, Radius = r, Layer = "EL_TAGS", ColorIndex = 30 };
                        ms.AppendEntity(circ);
                        tr.AddNewlyCreatedDBObject(circ, true);
                        var mt = new MText { Location = mid, Contents = pref + i, TextHeight = r * 0.65, Attachment = AttachmentPoint.MiddleCenter, Layer = "EL_TAGS" };
                        ms.AppendEntity(mt);
                        tr.AddNewlyCreatedDBObject(mt, true);
                        tagged++;
                    }
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Пронумеровано цепей: {tagged} (слой EL_TAGS). REGEN при необходимости.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-AUTOTAG: " + ex.Message); }
        }
    }
}
