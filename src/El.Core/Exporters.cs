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
}
