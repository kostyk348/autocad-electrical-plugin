using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace El.Core
{
    /// <summary>Экспорт топологии в Graphviz DOT (цепи как кластеры).</summary>
    public static class DotExporter
    {
        /// <summary>
        /// Узлы — линии (id + подписи), рёбра — стыковки, цепи — кластеры.
        /// Для больших схем (10k+) используйте ReduceLabels.
        /// </summary>
        public static string ToDot(WireGraph g, IReadOnlyList<TextLabel> labels,
                                   IReadOnlyDictionary<int, string> lineLabels = null,
                                   double textRadius = 5.0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("graph scheme {");
            sb.AppendLine("  rankdir=LR;");
            sb.AppendLine("  node [shape=box, fontsize=9, style=filled, fillcolor=white];");
            sb.AppendLine("  edge [color=gray40, penwidth=1.2];");

            var chains = g.AllChains();
            int ci = 0;
            foreach (var ch in chains)
            {
                ci++;
                sb.AppendLine($"  subgraph cluster_{ci} {{");
                sb.AppendLine($"    label=\"Цепь #{ci} ({ch.Count} линий)\"; style=dashed; color=gray60;");
                var segs = ch.Select(id => g.GetLine(id)).Where(x => x != null).ToList();
                var texts = ChainTexts.NearEnds(g, ch, labels, textRadius);
                foreach (var l in segs)
                {
                    string lbl = lineLabels != null && lineLabels.TryGetValue(l.Id, out var ll) ? ll : "";
                    sb.AppendLine($"    n{l.Id} [label=\"{Escape(lbl)}\"];");
                }
                foreach (var id in ch)
                {
                    var l = g.GetLine(id);
                    if (l == null) continue;
                    // стыковка к соседям — по совпадению концов с tolerance
                    foreach (var nb in g.Neighbors(id))
                    {
                        if (nb < id) continue; // одно ребро на пару
                        sb.AppendLine($"    n{id} -- n{nb};");
                    }
                }
                if (texts.Count > 0)
                    sb.AppendLine($"    label=\"Цепь #{ci}: {Escape(string.Join(", ", texts.Take(6)))} ({(texts.Count > 6 ? "..." : "")})\";");
                sb.AppendLine("  }");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>Экспорт спецификации AW33 в HTML (таблица в стиле wire-table).</summary>
    public static class HtmlExporter
    {
        public static string Aw33ToHtml(Aw33PageResult total, string title = "Спецификация проводов")
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{title}</title><style>");
            sb.AppendLine("  body{font-family:'Segoe UI',Arial,sans-serif;margin:16px;color:#222}");
            sb.AppendLine("  h1{font-size:20px;margin:0 0 4px}.sub{color:#666;font-size:13px;margin-bottom:12px}");
            sb.AppendLine("  table{border-collapse:collapse;width:100%;margin-bottom:24px}");
            sb.AppendLine("  th,td{border:1px solid #999;padding:4px 8px;font-size:13px;text-align:left}");
            sb.AppendLine("  th{background:#e8e8e8;font-weight:600}");
            sb.AppendLine("  tr:nth-child(even) td{background:#f6f6f6}");
            sb.AppendLine("  .qty{text-align:right}.total{font-weight:600;background:#eef4fb!important}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine($"<h1>{title}</h1>");
            sb.AppendLine($"<div class=\"sub\">Сформировано {DateTime.Now:dd.MM.yyyy HH:mm}</div>");

            // провода
            sb.AppendLine("<h2>Провода</h2>");
            sb.AppendLine("<table><tr><th>Марка / Цвет</th><th>Сечение</th><th class=\"qty\">Кол-во</th><th class=\"qty\">Длина, см</th><th class=\"qty\">Длина, м</th></tr>");
            double totalLenM = 0;
            foreach (var w in total.Wires)
            {
                double m = w.LengthCm / 100.0;
                totalLenM += m;
                sb.AppendLine($"<tr><td>{H(w.Color)}</td><td>{H(w.Size)}</td><td class=\"qty\">{w.Qty}</td><td class=\"qty\">{w.LengthCm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}</td><td class=\"qty\">{m.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}</td></tr>");
            }
            sb.AppendLine($"<tr class=\"total\"><td colspan=\"4\">Итого длина</td><td class=\"qty\">{totalLenM.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} м</td></tr>");
            sb.AppendLine("</table>");

            // детали
            if (total.Terms.Count > 0)
            {
                sb.AppendLine("<h2>Детали</h2>");
                sb.AppendLine("<table><tr><th>Наименование</th><th class=\"qty\">Кол-во, шт</th></tr>");
                foreach (var t in total.Terms)
                    sb.AppendLine($"<tr><td>{H(t.Name)}</td><td class=\"qty\">{t.Qty}</td></tr>");
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string H(string s) => s == null ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>Данные для единого отчёта по чертежу/проекту.</summary>
    public sealed class ReportData
    {
        public string Title = "";
        public string Source = "";           // файл/папка
        public int Lines, Texts, Chains;
        public string SheetInfo = "";        // штамп: лист N/N — наименование
        public int DefectsIsolated, DefectsGaps, DefectsDuplicates, DefectsTextless, DefectsSingle;
        public Aw33PageResult Wires;         // спецификация проводов
        public Dictionary<string, int> Bom;  // блоки
        public List<string[]> Connections;   // таблица соединений (Откуда/Куда)
        public List<string> CheckDetails = new List<string>();
    }

    /// <summary>Единый HTML-отчёт: топология + дефекты + провода + BOM + соединения.</summary>
    public static class ReportHtml
    {
        public static string Build(ReportData d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{H(d.Title)}</title><style>");
            sb.AppendLine("  body{font-family:'Segoe UI',Arial,sans-serif;margin:16px;color:#222}");
            sb.AppendLine("  h1{font-size:20px;margin:0 0 2px}.sub{color:#666;font-size:13px;margin-bottom:14px}");
            sb.AppendLine("  h2{font-size:15px;border-bottom:2px solid #1a4f8b;padding-bottom:3px;margin-top:20px}");
            sb.AppendLine("  table{border-collapse:collapse;width:100%;margin-bottom:14px}");
            sb.AppendLine("  th,td{border:1px solid #999;padding:3px 8px;font-size:13px;text-align:left}");
            sb.AppendLine("  th{background:#e8e8e8;font-weight:600}");
            sb.AppendLine("  tr:nth-child(even) td{background:#f6f6f6}");
            sb.AppendLine("  .num{text-align:right}.ok{color:#2e7d32;font-weight:600}.bad{color:#c62828;font-weight:600}");
            sb.AppendLine("  .warn{color:#e65100}.kpi{display:inline-block;background:#eef4fb;border:1px solid #1a4f8b;border-radius:4px;padding:6px 12px;margin:0 6px 6px 0}");
            sb.AppendLine("  .kpi b{font-size:18px;display:block}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine($"<h1>{H(d.Title)}</h1>");
            sb.AppendLine($"<div class=\"sub\">{H(d.Source)} · {DateTime.Now:dd.MM.yyyy HH:mm}{(_dash(d.SheetInfo))}</div>");

            // KPI
            sb.AppendLine("<div>");
            sb.AppendLine($"<span class=\"kpi\"><b>{d.Lines}</b>линий</span>");
            sb.AppendLine($"<span class=\"kpi\"><b>{d.Texts}</b>текстов</span>");
            sb.AppendLine($"<span class=\"kpi\"><b>{d.Chains}</b>цепей</span>");
            sb.AppendLine($"<span class=\"kpi\"><b>{d.Wires?.Wires.Count ?? 0}</b>проводов (спека)</span>");
            sb.AppendLine($"<span class=\"kpi\"><b>{d.Bom?.Count ?? 0}</b>типов блоков</span>");
            sb.AppendLine("</div>");

            // дефекты
            sb.AppendLine("<h2>Дефекты</h2>");
            sb.AppendLine("<table><tr><th>Тип</th><th class=\"num\">Кол-во</th></tr>");
            Row(sb, "Изолированные линии", d.DefectsIsolated, d.DefectsIsolated == 0);
            Row(sb, "Близкие разрывы", d.DefectsGaps, d.DefectsGaps == 0);
            Row(sb, "Дубликаты текста", d.DefectsDuplicates, d.DefectsDuplicates == 0);
            Row(sb, "Цепи без подписей", d.DefectsTextless, d.DefectsTextless == 0);
            Row(sb, "Цепи из одной линии", d.DefectsSingle, d.DefectsSingle == 0);
            sb.AppendLine("</table>");
            if (d.CheckDetails.Count > 0)
                sb.AppendLine($"<pre>{H(string.Join("\n", d.CheckDetails))}</pre>");

            // провода
            if (d.Wires != null && d.Wires.Wires.Count > 0)
            {
                sb.AppendLine("<h2>Провода (спецификация)</h2>");
                sb.AppendLine("<table><tr><th>Марка / Цвет</th><th>Сечение</th><th class=\"num\">Кол-во</th><th class=\"num\">Длина, см</th><th class=\"num\">Длина, м</th></tr>");
                double totalM = 0;
                foreach (var w in d.Wires.Wires)
                {
                    double m = w.LengthCm / 100.0;
                    totalM += m;
                    sb.AppendLine($"<tr><td>{H(w.Color)}</td><td>{H(w.Size)}</td><td class=\"num\">{w.Qty}</td><td class=\"num\">{w.LengthCm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}</td><td class=\"num\">{m.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}</td></tr>");
                }
                sb.AppendLine($"<tr><td colspan=\"4\"><b>Итого</b></td><td class=\"num\"><b>{totalM.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} м</b></td></tr></table>");
            }

            // BOM
            if (d.Bom != null && d.Bom.Count > 0)
            {
                sb.AppendLine("<h2>Компоненты (BOM)</h2>");
                sb.AppendLine("<table><tr><th>Блок</th><th class=\"num\">Кол-во</th></tr>");
                foreach (var kv in d.Bom.OrderBy(kv => kv.Key))
                    sb.AppendLine($"<tr><td>{H(kv.Key)}</td><td class=\"num\">{kv.Value}</td></tr>");
                sb.AppendLine("</table>");
            }

            // соединения
            if (d.Connections != null && d.Connections.Count > 0)
            {
                sb.AppendLine("<h2>Соединения</h2>");
                sb.AppendLine("<table><tr><th>Цепь</th><th>Откуда</th><th>Куда</th></tr>");
                foreach (var c in d.Connections)
                    sb.AppendLine($"<tr><td>{H(c[0])}</td><td>{H(c[1])}</td><td>{H(c[2])}</td></tr>");
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static void Row(StringBuilder sb, string name, int value, bool ok)
        {
            string cls = ok ? "ok" : "bad";
            sb.AppendLine($"<tr><td>{H(name)}</td><td class=\"num {cls}\">{value}</td></tr>");
        }

        private static string _dash(string s) => string.IsNullOrEmpty(s) ? "" : " · " + s;

        private static string H(string s) => s == null ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>HTML-отчёт сравнения ревизий.</summary>
    public static class DiffHtml
    {
        public static string Build(string title, string source, SpecDiff.DiffResult d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{H(title)}</title><style>");
            sb.AppendLine("  body{font-family:'Segoe UI',Arial,sans-serif;margin:16px;color:#222}");
            sb.AppendLine("  h1{font-size:20px;margin:0 0 2px}.sub{color:#666;font-size:13px;margin-bottom:14px}");
            sb.AppendLine("  table{border-collapse:collapse;width:100%;margin-bottom:14px}");
            sb.AppendLine("  th,td{border:1px solid #999;padding:3px 8px;font-size:13px;text-align:left}");
            sb.AppendLine("  th{background:#e8e8e8}.num{text-align:right}");
            sb.AppendLine("  .add{color:#2e7d32;font-weight:600}.del{color:#c62828;font-weight:600}.chg{color:#e65100;font-weight:600}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine($"<h1>{H(title)}</h1>");
            sb.AppendLine($"<div class=\"sub\">{H(source)} · {DateTime.Now:dd.MM.yyyy HH:mm}</div>");

            sb.AppendLine("<h2>Провода</h2>");
            sb.AppendLine($"<div class=\"sub\">+{d.WiresAdded} добавлено, −{d.WiresRemoved} удалено, ~{d.WiresChanged} изменено</div>");
            sb.AppendLine("<table><tr><th>Цвет</th><th>Сечение</th><th class=\"num\">Было (шт)</th><th class=\"num\">Стало (шт)</th><th class=\"num\">Было, м</th><th class=\"num\">Стало, м</th><th>Изм.</th></tr>");
            foreach (var w in d.Wires)
            {
                if (w.Kind == "unchanged") continue;
                string cls = w.Kind == "added" ? "add" : w.Kind == "removed" ? "del" : "chg";
                string mark = w.Kind == "added" ? "+" : w.Kind == "removed" ? "−" : "~";
                sb.AppendLine($"<tr><td>{H(w.Color)}</td><td>{H(w.Size)}</td><td class=\"num\">{w.QtyOld}</td><td class=\"num\">{w.QtyNew}</td>" +
                              $"<td class=\"num\">{(w.LenCmOld / 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}</td>" +
                              $"<td class=\"num\">{(w.LenCmNew / 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}</td>" +
                              $"<td class=\"{cls}\">{mark}</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h2>Компоненты (BOM)</h2>");
            sb.AppendLine($"<div class=\"sub\">+{d.BomAdded} добавлено, −{d.BomRemoved} удалено, ~{d.BomChanged} изменено</div>");
            sb.AppendLine("<table><tr><th>Блок</th><th class=\"num\">Было</th><th class=\"num\">Стало</th><th>Изм.</th></tr>");
            foreach (var b in d.Bom)
            {
                string cls = b.Kind == "added" ? "add" : b.Kind == "removed" ? "del" : "chg";
                string mark = b.Kind == "added" ? "+" : b.Kind == "removed" ? "−" : "~";
                sb.AppendLine($"<tr><td>{H(b.Block)}</td><td class=\"num\">{b.CountOld}</td><td class=\"num\">{b.CountNew}</td><td class=\"{cls}\">{mark}</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h2>Топология</h2>");
            sb.AppendLine($"<div class=\"sub\">+{d.TopologyAdded.Count} цепей, −{d.TopologyRemoved.Count} цепей</div>");
            if (d.TopologyAdded.Count > 0)
            {
                sb.AppendLine("<b>Добавлены:</b><ul>");
                foreach (var t in d.TopologyAdded) sb.AppendLine($"<li class=\"add\">{H(t)}</li>");
                sb.AppendLine("</ul>");
            }
            if (d.TopologyRemoved.Count > 0)
            {
                sb.AppendLine("<b>Удалены:</b><ul>");
                foreach (var t in d.TopologyRemoved) sb.AppendLine($"<li class=\"del\">{H(t)}</li>");
                sb.AppendLine("</ul>");
            }
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static string H(string s) => s == null ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
