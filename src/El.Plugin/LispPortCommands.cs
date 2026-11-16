using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using El.Core;

namespace El.Plugin
{
    /// <summary>
    /// Две команды, которые были только в LISP (el-tools) и перенесены в плагин:
    /// <c>EL-HOTSPOTS</c> — тепловая карта плотности линий, <c>EL-CROSSREF</c> — ссылки на другие листы.
    /// Обе линейны по числу объектов; сетка карты ограничена <see cref="Hotspots.MaxCells"/>.
    /// </summary>
    public static class LispPortCommands
    {
        private static Editor Ed => DwgAccess.Ed;

        // ---------- EL-HOTSPOTS ----------

        [CommandMethod("EL-HOTSPOTS")]
        public static void HotspotsCommand()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var db = doc.Database;

                var pio = new PromptIntegerOptions("\n[EL] Ячеек в сетке (2–" + Hotspots.MaxCells + ")")
                {
                    DefaultValue = Hotspots.DefaultCells,
                    LowerLimit = 2,
                    UpperLimit = Hotspots.MaxCells
                };
                var res = Ed.GetInteger(pio);
                int cells = res.Status == PromptStatus.OK ? res.Value : Hotspots.DefaultCells;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lines = DwgAccess.CollectLines(tr);
                    if (lines.Count == 0)
                    {
                        Ed.WriteMessage("\n[EL] Линий нет — карта не нужна.");
                        return;
                    }

                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        var l = lines[i];
                        minX = Math.Min(minX, Math.Min(l.A.X, l.B.X));
                        minY = Math.Min(minY, Math.Min(l.A.Y, l.B.Y));
                        maxX = Math.Max(maxX, Math.Max(l.A.X, l.B.X));
                        maxY = Math.Max(maxY, Math.Max(l.A.Y, l.B.Y));
                    }
                    if (maxX - minX < 1e-9) maxX = minX + 1;
                    if (maxY - minY < 1e-9) maxY = minY + 1;

                    var map = Hotspots.Build(lines, minX, minY, maxX, maxY, cells);
                    DwgAccess.EnsureLayer(tr, "EL_HOTSPOTS", 8);

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    int drawn = 0;
                    for (int cy = 0; cy < map.Cells; cy++)
                    {
                        for (int cx = 0; cx < map.Cells; cx++)
                        {
                            if (map.Counts[cy, cx] <= 0) continue;
                            double x = map.CellMinX(cx);
                            double y = map.CellMinY(cy);
                            var pl = new Polyline(4)
                            {
                                Layer = "EL_HOTSPOTS",
                                Closed = true,
                                ColorIndex = (short)map.AciFor(cx, cy)
                            };
                            pl.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
                            pl.AddVertexAt(1, new Point2d(x + map.CellWidth, y), 0, 0, 0);
                            pl.AddVertexAt(2, new Point2d(x + map.CellWidth, y + map.CellHeight), 0, 0, 0);
                            pl.AddVertexAt(3, new Point2d(x, y + map.CellHeight), 0, 0, 0);
                            ms.AppendEntity(pl);
                            tr.AddNewlyCreatedDBObject(pl, true);
                            drawn++;
                        }
                    }
                    tr.Commit();

                    Ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                        "\n[EL] Тепловая карта: сетка {0}×{0}, ячеек с линиями {1}, максимум {2} линий в ячейке.",
                        map.Cells, drawn, map.Max));
                    Ed.WriteMessage("\n[EL] Слой EL_HOTSPOTS — цвет от красного (плотно) к зелёному (свободно).");
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[EL] HOTSPOTS: " + ex.Message); Plugin.Log(ex); }
        }

        // ---------- EL-CROSSREF ----------

        [CommandMethod("EL-CROSSREF")]
        public static void CrossReference()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var db = doc.Database;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var texts = DwgAccess.CollectTexts(tr);
                    var refs = CrossReferences.Find(texts);
                    if (refs.Count == 0)
                    {
                        Ed.WriteMessage("\n[EL] Ссылок на другие листы не найдено.");
                        return;
                    }

                    Ed.WriteMessage("\n[EL] Ссылки на другие листы: " + refs.Count);
                    var bySheet = refs.GroupBy(r => r.Sheet).OrderBy(g => g.Key).ToList();
                    foreach (var group in bySheet)
                        Ed.WriteMessage("\n  лист " + group.Key + ": " + group.Count() + " ссыл.");

                    int shown = 0;
                    foreach (var r in refs)
                    {
                        if (shown++ >= 30) { Ed.WriteMessage("\n  ... показаны первые 30"); break; }
                        Ed.WriteMessage("\n  " + r);
                    }

                    // пометить найденные тексты рамкой на отдельном слое — видно, что именно найдено
                    DwgAccess.EnsureLayer(tr, "EL_CROSSREF", 2);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var r in refs)
                    {
                        string key = r.Where.X.ToString("0.###", CultureInfo.InvariantCulture) + "|" +
                                     r.Where.Y.ToString("0.###", CultureInfo.InvariantCulture);
                        if (!seen.Add(key)) continue; // одна метка на текст, даже если в нём три ссылки
                        var circle = new Circle
                        {
                            Center = new Point3d(r.Where.X, r.Where.Y, 0),
                            Radius = 60,
                            Layer = "EL_CROSSREF"
                        };
                        ms.AppendEntity(circle);
                        tr.AddNewlyCreatedDBObject(circle, true);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[EL] CROSSREF: " + ex.Message); Plugin.Log(ex); }
        }
    }
}
