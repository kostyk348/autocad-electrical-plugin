using System;
using System.Collections.Generic;
using System.Globalization;

namespace El.Core
{
    /// <summary>Содержимое таблицы AutoCAD (как есть, для «картинки» в HTML).</summary>
    public sealed class TableData
    {
        public List<List<string>> Cells = new List<List<string>>();
        public int Rows => Cells.Count;
        public int Cols => Cells.Count > 0 ? Cells[0].Count : 0;
    }

    /// <summary>Статистика числовой колонки.</summary>
    public sealed class ColumnStats
    {
        public int Index;
        public double Sum;
        public double Min = double.PositiveInfinity;
        public double Max = double.NegativeInfinity;
        public int Numbers;
        public bool HasNumbers => Numbers > 0;
    }

    /// <summary>Анализ таблицы: числовые колонки, суммы (для «расчёта под таблицей»).</summary>
    public static class TableAnalyzer
    {
        public static bool TryParseNum(string s, out double v)
        {
            if (s == null) { v = 0; return false; }
            s = s.Trim().Replace(' ', '\u00A0');
            if (s.Length == 0) { v = 0; return false; }
            // убираем суффиксы единиц (м, см, шт, мм и т.п.)
            int cut = s.Length;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!(char.IsDigit(c) || c == ',' || c == '.' || c == '-' || c == '+' || c == ' ' || c == '\u00A0' || c == '№'))
                {
                    cut = i;
                    break;
                }
            }
            string num = s.Substring(0, cut).Replace("\u00A0", "").Trim();
            if (num.Length == 0) { v = 0; return false; }
            // «№5» — не число
            if (num.Contains("№")) { v = 0; return false; }
            if (double.TryParse(num.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return true;
            return false;
        }

        /// <summary>Статистика по каждой колонке (только числовые колонки дают HasNumbers).</summary>
        public static List<ColumnStats> AnalyzeColumns(TableData t)
        {
            var res = new List<ColumnStats>();
            int cols = t.Cols;
            for (int c = 0; c < cols; c++)
            {
                var st = new ColumnStats { Index = c };
                for (int r = 0; r < t.Rows; r++)
                {
                    if (r < t.Cells.Count && c < t.Cells[r].Count &&
                        TryParseNum(t.Cells[r][c], out double v))
                    {
                        st.Sum += v;
                        st.Numbers++;
                        if (v < st.Min) st.Min = v;
                        if (v > st.Max) st.Max = v;
                    }
                }
                res.Add(st);
            }
            return res;
        }

        /// <summary>Сумма всех чисел в таблице.</summary>
        public static double SumAll(TableData t)
        {
            double s = 0;
            foreach (var st in AnalyzeColumns(t)) s += st.Sum;
            return s;
        }
    }
}
