using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace El.Core
{
    /// <summary>Строка спецификации: цвет/сечение/кол-во/суммарная длина (см).</summary>
    public sealed class WireRow
    {
        public string Color { get; set; } = "";
        public string Size { get; set; } = "";
        public int Qty { get; set; } = 1;
        public double LengthCm { get; set; }

        public string Key => Color + "\u0001" + Size + "\u0001" + Qty;
    }

    /// <summary>Деталь (терминал): наименование + количество.</summary>
    public sealed class TermRow
    {
        public string Name { get; set; } = "";
        public int Qty { get; set; } = 1;
    }

    /// <summary>Результат парсинга одного листа.</summary>
    public sealed class Aw33PageResult
    {
        public List<WireRow> Wires { get; } = new List<WireRow>();
        public List<TermRow> Terms { get; } = new List<TermRow>();
    }

    /// <summary>
    /// Парсер спецификации проводов (логика AW33):
    /// - вход: тексты листа с координатами (y, x)
    /// - якоря — строки с "мм2"; между якорями — "коридоры" (строки проводов)
    /// - в строке: цвет, сечение, сумма длин (м/см), кол-во проводов ("N шт" или "Nx…")
    /// - длина строки умножается на кол-во проводов
    /// </summary>
    public static class Aw33Parser
    {
        private static readonly string[] StdColors =
        {
            "КРАСН", "СИН", "ЧЕРН", "БЕЛ", "ЖЕЛТ", "ЗЕЛ", "СЕР", "КОРИЧ",
            "ОРАНЖ", "ФИОЛЕТ", "РОЗОВ", "ГОЛУБ", "Ж/З", "ПРОЗР", "САЛАТ", "БИРЮЗ"
        };

        private static readonly string[] IgnoreWords =
        {
            "ЛУЖЕН", "ЗАЧИС", "ПАЙКА", "ПРИКЛЕПАТЬ", "ВЫВОД", "ВЕНТИЛЯТОР",
            "ИЗМ", "ЛИСТ", "ДОКУМ", "ПОДПИСЬ", "ДАТА", "КОП", "ФОРМАТ", "МАССА",
            "МАСШТАБ", "РАЗРАБ", "ПРОВ", "Т.КОНТР", "Н.КОНТР", "УТВ", "ПЕРЕГРЕВ",
            "СПРАВ", "ПЕРВОЕ ПРИМЕНЕНИЕ", "ТЕПЛОМАШ", "ООО"
        };

        private static readonly Regex RgSize = new Regex(@"\d+([.,]\d+)?", RegexOptions.Compiled);
        private static readonly Regex RgMeters = new Regex(@"(\d+([.,]\d+)?)\s*м\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RgQty = new Regex(@"[0-9]+(?=\s*[шШ][тТ])", RegexOptions.Compiled);
        private static readonly Regex RgQtyInSize = new Regex(@"^([0-9]+)\s*[xхXХ]", RegexOptions.Compiled);
        private static readonly Regex RgStripSizeMul = new Regex(@"^[0-9]+\s*[xхXХ]", RegexOptions.Compiled);
        private static readonly Regex RgTermQty = new Regex(@"[0-9]+(?=\s*[шШ][тТ])", RegexOptions.Compiled);
        private static readonly Regex RgStripTermQty = new Regex(@"\(?\d+\s*[шШ][тТ]\.?\)?", RegexOptions.Compiled);
        private static readonly Regex RgTermMul = new Regex(@"[0-9]+(?=\s*[xхXХ])", RegexOptions.Compiled);
        private static readonly Regex RgStripTermMul = new Regex(@"[0-9]+\s*[xхXХ]", RegexOptions.Compiled);
        private static readonly Regex RgHasLetters = new Regex(@"[a-zA-Zа-яА-Я]", RegexOptions.Compiled);

        /// <summary>Текст с координатами: (y, x, text).</summary>
        public sealed class RawText
        {
            public double Y { get; set; }
            public double X { get; set; }
            public string Text { get; set; }
        }

        public static Aw33PageResult ParsePage(IEnumerable<RawText> rawTexts)
        {
            var result = new Aw33PageResult();
            var items = new List<RawText>();
            foreach (var r in rawTexts)
            {
                var t = CleanMtext(r.Text);
                if (string.IsNullOrEmpty(t)) continue;
                items.Add(new RawText { Y = r.Y, X = r.X, Text = t });
            }

            // якоря: тексты с "мм2" (символ ² нормализуем в "2")
            var anchors = new List<RawText>();
            foreach (var it in items)
            {
                var up = it.Text.ToUpperInvariant().Replace('²', '2');
                if (up.Contains("ММ2") || up.Contains("MM2")) anchors.Add(it);
            }
            if (anchors.Count == 0) return result; // без якорей лист не разбираем

            anchors.Sort((a, b) => b.Y.CompareTo(a.Y)); // сверху вниз

            // чистка якорей, лежащих ближе 10 мм по Y
            var clean = new List<RawText>();
            double prevY = double.NaN;
            foreach (var a in anchors)
            {
                if (!double.IsNaN(prevY) && Math.Abs(prevY - a.Y) <= 10.0) continue;
                clean.Add(a);
                prevY = a.Y;
            }
            anchors = clean;

            // границы коридоров (середины между якорями)
            var bounds = new List<double>();
            for (int i = 0; i + 1 < anchors.Count; i++)
                bounds.Add((anchors[i].Y + anchors[i + 1].Y) / 2.0);

            // распределение по коридорам
            var rows = new List<List<RawText>>();
            for (int i = 0; i < anchors.Count; i++)
            {
                double yTop = i == 0 ? double.PositiveInfinity : bounds[i - 1];
                double yBot = i == bounds.Count ? double.NegativeInfinity : bounds[i];
                var row = new List<RawText>();
                foreach (var it in items)
                {
                    if (it.Y <= yTop && it.Y > yBot) row.Add(it);
                }
                rows.Add(row);
            }

            string lastSize = "Не указан", lastColor = "Не указан";

            foreach (var row in rows)
            {
                string color = "", size = "";
                double lengthSum = 0.0;
                int rowQty = 1;

                foreach (var it in row)
                {
                    string txt = it.Text;
                    string up = txt.ToUpperInvariant().Replace('²', '2');

                    // количество проводов: "N шт"
                    var mQty = RgQty.Match(up);
                    if (mQty.Success && up.Contains("ШТ"))
                        rowQty = Math.Max(rowQty, int.Parse(mQty.Value));

                    if (up.Contains(":")) continue;                       // адреса
                    if (ContainsAny(up, IgnoreWords)) continue;           // мусор

                    if (up.Contains("ММ2") || up.Contains("MM2"))
                    {
                        size = txt;
                        var mSize = RgQtyInSize.Match(up);                // "2х1,5 мм²"
                        if (mSize.Success)
                        {
                            rowQty = Math.Max(rowQty, int.Parse(mSize.Groups[1].Value));
                            size = RgStripSizeMul.Replace(size, "");
                        }
                        continue;
                    }

                    if (up.Contains("СМ") || up.Contains("CM"))
                    {
                        // сантиметры: все числа как есть
                        foreach (Match mm in RgSize.Matches(txt))
                        {
                            string num = mm.Value.Replace(',', '.');
                            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                                lengthSum += v;
                        }
                        continue;
                    }

                    // метры: "5 м", "15 м" -> *100 (в см)
                    var mMeters = RgMeters.Match(txt);
                    if (mMeters.Success)
                    {
                        string num = mMeters.Groups[1].Value.Replace(',', '.');
                        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                            lengthSum += v * 100.0;
                        continue;
                    }

                    if (ContainsAny(up, StdColors))
                    {
                        if (string.IsNullOrEmpty(color)) color = txt;     // первый цвет
                        continue;
                    }

                    // деталь/терминал
                    if (RgHasLetters.IsMatch(txt))
                    {
                        int qty = 1;
                        var tq = RgTermQty.Match(txt);
                        if (tq.Success) qty = int.Parse(tq.Value);
                        var cleanName = RgStripTermQty.Replace(txt, "");
                        var tm = RgTermMul.Match(cleanName);
                        if (tm.Success) qty = int.Parse(tm.Value);
                        cleanName = RgStripTermMul.Replace(cleanName, "");
                        cleanName = cleanName.Trim(" -_()[]\t\n\r.:".ToCharArray());
                        if (cleanName.Length > 0 && cleanName.Length < 25)
                        {
                            var exist = result.Terms.Find(t => t.Name == cleanName);
                            if (exist != null) exist.Qty += qty;
                            else result.Terms.Add(new TermRow { Name = cleanName, Qty = qty });
                        }
                    }
                }

                // наследование внутри страницы
                if (size != "") lastSize = size; else size = lastSize;
                if (color != "") lastColor = color; else color = lastColor;

                // умножение на количество проводов
                if (rowQty > 1) lengthSum *= rowQty;

                if (lengthSum > 0.0)
                {
                    var wr = new WireRow { Color = color, Size = size, Qty = rowQty, LengthCm = lengthSum };
                    var exist = result.Wires.Find(w => w.Key == wr.Key);
                    if (exist != null) exist.LengthCm += wr.LengthCm;
                    else result.Wires.Add(wr);
                }
            }

            result.Wires.Sort((a, b) => string.CompareOrdinal(a.Color, b.Color));
            result.Terms.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }

        /// <summary>Объединение результатов листов в сводную.</summary>
        public static Aw33PageResult Merge(IEnumerable<Aw33PageResult> pages)
        {
            var total = new Aw33PageResult();
            foreach (var p in pages)
            {
                foreach (var w in p.Wires)
                {
                    var exist = total.Wires.Find(x => x.Key == w.Key);
                    if (exist != null) exist.LengthCm += w.LengthCm;
                    else total.Wires.Add(new WireRow { Color = w.Color, Size = w.Size, Qty = w.Qty, LengthCm = w.LengthCm });
                }
                foreach (var t in p.Terms)
                {
                    var exist = total.Terms.Find(x => x.Name == t.Name);
                    if (exist != null) exist.Qty += t.Qty;
                    else total.Terms.Add(new TermRow { Name = t.Name, Qty = t.Qty });
                }
            }
            total.Wires.Sort((a, b) => string.CompareOrdinal(a.Color, b.Color));
            total.Terms.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return total;
        }

        private static string CleanMtext(string txt)
        {
            if (txt == null) return "";
            txt = Regex.Replace(txt, @"\\[A-Za-z0-9][^;]*;|[{}\\]", "");
            txt = txt.Trim();
            // legacy: "?" от шрифтов -> "2" (для "ММ2" и подобного)
            txt = txt.Replace('?', '2');
            return txt;
        }

        private static bool ContainsAny(string upper, string[] words)
        {
            foreach (var w in words)
                if (upper.Contains(w)) return true;
            return false;
        }
    }
}
