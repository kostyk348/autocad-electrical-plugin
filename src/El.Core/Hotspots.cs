using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace El.Core
{
    /// <summary>
    /// Тепловая карта плотности линий: порт LISP-команды <c>EL-HOTSPOTS</c>.
    /// Плотность считается по серединам отрезков в сетке <c>cells × cells</c> поверх габаритов чертежа.
    /// Один проход по линиям — O(n); сетка заранее ограничена <see cref="MaxCells"/>.
    /// </summary>
    public static class Hotspots
    {
        public const int MaxCells = 256;
        public const int DefaultCells = 10;

        public sealed class HeatMap
        {
            public int Cells { get; set; }
            public double MinX { get; set; }
            public double MinY { get; set; }
            public double CellWidth { get; set; }
            public double CellHeight { get; set; }
            public int[,] Counts { get; set; }
            public int Max { get; set; }

            /// <summary>Индекс цвета ACI для ячейки: плотнее — «горячее» (1 красный … 40 зелёный).</summary>
            public int AciFor(int cx, int cy)
            {
                if (Counts == null || Max <= 0) return 40;
                int value = Counts[cy, cx];
                if (value <= 0) return 40;
                double ratio = (double)value / Max;
                if (ratio > 0.8) return 1;
                if (ratio > 0.6) return 2;
                if (ratio > 0.45) return 3;
                if (ratio > 0.3) return 30;
                if (ratio > 0.15) return 20;
                return 10;
            }

            public double CellMinX(int cx) => MinX + cx * CellWidth;
            public double CellMinY(int cy) => MinY + cy * CellHeight;
        }

        /// <summary>
        /// Builds the heat map. Knows nothing about AutoCAD: takes plain segments and bounds.
        /// Lines outside the bounds are clamped into the edge cell, so nothing is lost.
        /// </summary>
        public static HeatMap Build(IReadOnlyList<LineSeg> lines, double minX, double minY, double maxX, double maxY,
                                    int cells = DefaultCells)
        {
            if (cells < 2) cells = 2;
            if (cells > MaxCells) cells = MaxCells;

            var map = new HeatMap
            {
                Cells = cells,
                MinX = minX,
                MinY = minY,
                Counts = new int[cells, cells]
            };

            double width = maxX - minX;
            double height = maxY - minY;
            if (width <= 1e-9) width = 1.0;
            if (height <= 1e-9) height = 1.0;
            map.CellWidth = width / cells;
            map.CellHeight = height / cells;

            if (lines == null) return map;
            int max = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line == null) continue;
                double mx = (line.A.X + line.B.X) * 0.5;
                double my = (line.A.Y + line.B.Y) * 0.5;
                int cx = (int)Math.Floor((mx - minX) / map.CellWidth);
                int cy = (int)Math.Floor((my - minY) / map.CellHeight);
                if (cx < 0) cx = 0; else if (cx >= cells) cx = cells - 1;
                if (cy < 0) cy = 0; else if (cy >= cells) cy = cells - 1;
                int value = ++map.Counts[cy, cx];
                if (value > max) max = value;
            }
            map.Max = max;
            return map;
        }
    }

    /// <summary>
    /// Ссылки на другие листы внутри текста чертежа: порт LISP-команды <c>EL-CROSSREF</c>.
    /// Ищет «лист 5», «л. 12», «sheet 3», «см. лист 7/2» и подобные формы. Один проход — O(n·длина текста).
    /// </summary>
    public static class CrossReferences
    {
        /// <summary>Ограничение на длину текста, который вообще разбирается (защита от мусорных MTEXT).</summary>
        public const int MaxTextLength = 2000;

        private static readonly Regex Pattern = new Regex(
            @"(?:^|[\s\[(])(?:лист|лл?\.|sheet|sh\.)\s*[№#:]?\s*(\d{1,4})(?:\s*[/\-]\s*(\d{1,3}))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public sealed class Reference
        {
            public string Text { get; set; }
            public int Sheet { get; set; }
            public int? Position { get; set; }      // номер элемента на листе, если указан
            public Point2D Where { get; set; }

            public override string ToString()
            {
                return "\"" + Text + "\" → лист " + Sheet.ToString(CultureInfo.InvariantCulture)
                       + (Position.HasValue ? "/" + Position.Value.ToString(CultureInfo.InvariantCulture) : "");
            }
        }

        /// <summary>Все ссылки на другие листы; по одной записи на найденное совпадение.</summary>
        public static List<Reference> Find(IReadOnlyList<TextLabel> texts)
        {
            var result = new List<Reference>();
            if (texts == null) return result;
            for (int i = 0; i < texts.Count; i++)
            {
                var label = texts[i];
                if (label == null || string.IsNullOrEmpty(label.Text)) continue;
                string text = label.Text.Length > MaxTextLength ? label.Text.Substring(0, MaxTextLength) : label.Text;

                int from = 0;
                while (from < text.Length)
                {
                    var match = Pattern.Match(text, from);
                    if (!match.Success) break;
                    int sheet;
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sheet))
                    {
                        int? position = null;
                        if (match.Groups[2].Success)
                        {
                            int pos;
                            if (int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pos))
                                position = pos;
                        }
                        result.Add(new Reference
                        {
                            Text = Clean(label.Text),
                            Sheet = sheet,
                            Position = position,
                            Where = label.Position
                        });
                    }
                    // двигаемся за найденным совпадением: вперёд гарантированно, зацикливание невозможно
                    from = match.Index + Math.Max(1, match.Length);
                }
            }
            return result;
        }

        private static string Clean(string text)
        {
            string trimmed = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return trimmed.Length <= 80 ? trimmed : trimmed.Substring(0, 79) + "…";
        }
    }
}
