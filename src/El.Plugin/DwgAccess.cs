using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using El.Core;

namespace El.Plugin
{
    /// <summary>
    /// Доступ к чертежу: сбор LINE/TEXT из ModelSpace, подсветка, зум,
    /// создание таблиц и слоёв. Всё — через Transaction.
    /// </summary>
    public static class DwgAccess
    {
        public const double DefaultTolerance = 0.5;   // мм, стыковка LINE
        public const double DefaultTextRadius = 5.0;  // мм, поиск текста

        public static Document Doc => Application.DocumentManager.MdiActiveDocument;
        public static Editor Ed => Doc.Editor;

        /// <summary>Все LINE из ModelSpace (текущий документ).</summary>
        public static List<LineSeg> CollectLines(Transaction tr)
        {
            var result = new List<LineSeg>();
            var bt = (BlockTable)tr.GetObject(Doc.Database.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.ObjectClass.DxfName != "LINE") continue;
                var line = (Line)tr.GetObject(id, OpenMode.ForRead);
                result.Add(new LineSeg((int)id.Handle.Value, new Point2D(line.StartPoint.X, line.StartPoint.Y),
                                                                   new Point2D(line.EndPoint.X, line.EndPoint.Y))
                {
                    Tag = id
                });
            }
            return result;
        }

        /// <summary>Все TEXT/MTEXT из ModelSpace.</summary>
        public static List<TextLabel> CollectTexts(Transaction tr)
        {
            var result = new List<TextLabel>();
            var bt = (BlockTable)tr.GetObject(Doc.Database.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                string dxf = id.ObjectClass.DxfName;
                if (dxf != "TEXT" && dxf != "MTEXT") continue;
                var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                string text = dxf == "TEXT"
                    ? ((DBText)ent).TextString
                    : ((MText)ent).Contents;
                var pos = dxf == "TEXT" ? ((DBText)ent).Position : ((MText)ent).Location;
                result.Add(new TextLabel(new Point2D(pos.X, pos.Y), text) { Tag = id });
            }
            return result;
        }

        /// <summary>Подсветить сущности (по ObjectId из Tag).</summary>
        public static void Highlight(IEnumerable<LineSeg> lines, bool on)
        {
            using (var tr = Doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var l in lines)
                {
                    if (l.Tag is ObjectId id && id.IsValid && !id.IsErased)
                    {
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        if (on) ent.Highlight(); else ent.Unhighlight();
                    }
                }
                tr.Commit();
            }
        }

        public static void UnhighlightAll()
        {
            using (var tr = Doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(Doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    try
                    {
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForRead, true);
                        ent.Unhighlight();
                    }
                    catch { }
                }
                tr.Commit();
            }
        }

        /// <summary>Зум к сущностям (через ZOOM Window).</summary>
        public static void ZoomTo(IEnumerable<LineSeg> lines)
        {
            var ids = new List<ObjectId>();
            foreach (var l in lines)
                if (l.Tag is ObjectId id && id.IsValid && !id.IsErased) ids.Add(id);
            if (ids.Count == 0) return;

            using (var tr = Doc.Database.TransactionManager.StartTransaction())
            {
                Extents3d ext = default;
                bool first = true;
                foreach (var id in ids)
                {
                    var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    var e = ent.GeometricExtents;
                    if (first) { ext = e; first = false; }
                    else ext.AddExtents(e);
                }
                tr.Commit();
                if (!first)
                {
                    double pad = Math.Max(10.0, Math.Max(ext.MaxPoint.X - ext.MinPoint.X, ext.MaxPoint.Y - ext.MinPoint.Y) * 0.05);
                    var view = Ed.GetCurrentView();
                    // CenterPoint — Point2d (вид лежит в плоскости XY)
                    view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                                                   (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0);
                    view.Height = (ext.MaxPoint.Y - ext.MinPoint.Y) + pad * 2;
                    view.Width = (ext.MaxPoint.X - ext.MinPoint.X) + pad * 2;
                    Ed.SetCurrentView(view);
                }
            }
        }

        public static void ZoomTo(Point2D p, double size)
        {
            var view = Ed.GetCurrentView();
            view.CenterPoint = new Point2d(p.X, p.Y);
            view.Height = size;
            view.Width = size;
            Ed.SetCurrentView(view);
        }

        /// <summary>Создать слой (если нет).</summary>
        public static void EnsureLayer(Transaction tr, string name, short colorIndex)
        {
            var lt = (LayerTable)tr.GetObject(Doc.Database.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            var ltr = new LayerTableRecord
            {
                Name = name,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
            };
            lt.UpgradeOpen();
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        /// <summary>Вставить таблицу AutoCAD в ModelSpace.</summary>
        public static ObjectId AddTable(Transaction tr, Point3d insertPoint,
                                        string[] header, List<string[]> rows,
                                        double rowHeight = 8.0, double colWidth = 50.0)
        {
            var bt = (BlockTable)tr.GetObject(Doc.Database.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            int nCols = header.Length;
            var table = new Table();
            table.Position = insertPoint;
            table.SetSize(rows.Count + 2, nCols);
            table.SetRowHeight(rowHeight);
            for (int c = 0; c < nCols; c++) table.SetColumnWidth(colWidth);
            for (int r = 0; r <= rows.Count + 1; r++)
            {
                for (int c = 0; c < nCols; c++)
                {
                    if (r < 2) table.Cells[r, c].TextString = header[c];
                    else if (r - 2 < rows.Count && c < rows[r - 2].Length)
                        table.Cells[r, c].TextString = rows[r - 2][c];
                }
            }
            ms.AppendEntity(table);
            tr.AddNewlyCreatedDBObject(table, true);
            return table.ObjectId;
        }

        /// <summary>Вставить MTEXT.</summary>
        public static ObjectId AddMText(Transaction tr, Point3d pos, string text, double height = 2.5)
        {
            var bt = (BlockTable)tr.GetObject(Doc.Database.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var mt = new MText { Location = pos, Contents = text, TextHeight = height };
            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
            return mt.ObjectId;
        }
    }
}
