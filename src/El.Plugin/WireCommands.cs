using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace El.Plugin
{
    /// <summary>Провода с XData (DrawWire/WireTable/WireNodes) и выноски (WT), адреса (WireSegAddr).</summary>
    public static class WireCommands
    {
        private static Editor Ed => DwgAccess.Ed;
        private const string AppName = "WIRE_DATA";

        // ---------- XData ----------
        private static void EnsureRegApp(Transaction tr)
        {
            var rat = (RegAppTable)tr.GetObject(DwgAccess.Doc.Database.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(AppName)) return;
            rat.UpgradeOpen();
            rat.Add(new RegAppTableRecord { Name = AppName });
        }

        private static void SetWireXData(Transaction tr, Entity ent, int num, string dev1, string term1, string dev2, string term2)
        {
            EnsureRegApp(tr);
            var rb = new ResultBuffer(
                new TypedValue(1000, num.ToString()),
                new TypedValue(1000, dev1),
                new TypedValue(1000, term1),
                new TypedValue(1000, dev2),
                new TypedValue(1000, term2));
            ent.XData = rb;
        }

        private static List<string> GetWireXData(Entity ent)
        {
            var vals = new List<string>();
            if (ent.XData == null) return vals;
            foreach (var tv in ent.XData)
                if (tv.TypeCode == 1000 && tv.Value is string s) vals.Add(s);
            return vals;
        }

        private static int NextWireNumber()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\ElTools"))
                {
                    int cur = key.GetValue("WireCount", 0) is int v ? v : 0;
                    key.SetValue("WireCount", cur + 1);
                    return cur + 1;
                }
            }
            catch { return 1; }
        }

        // ---------- DrawWire ----------
        [CommandMethod("DrawWire")]
        public static void DrawWire()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var ed = Ed;
                int wireNum = NextWireNumber();
                double txth = ed.GetDouble(new PromptDoubleOptions("\nВысота текста") { DefaultValue = 2.5 }).Value;

                var p1 = ed.GetPoint("\nНачальная точка: ");
                if (p1.Status != PromptStatus.OK) return;
                var pts = new List<Point3d> { p1.Value };
                while (true)
                {
                    var pn = ed.GetPoint("\nСледующая точка (Enter — конец): ");
                    if (pn.Status != PromptStatus.OK) break;
                    pts.Add(pn.Value);
                }
                if (pts.Count < 2) { ed.WriteMessage("\n! Нужно минимум 2 точки"); return; }

                var dev1 = ed.GetString(new PromptStringOptions("\nОткуда (устройство): ") { AllowSpaces = true }).StringResult;
                var term1 = ed.GetString(new PromptStringOptions("\nКлемма от: ") { AllowSpaces = true }).StringResult;
                var dev2 = ed.GetString(new PromptStringOptions("\nКуда (устройство): ") { AllowSpaces = true }).StringResult;
                var term2 = ed.GetString(new PromptStringOptions("\nКлемма на: ") { AllowSpaces = true }).StringResult;
                if (string.IsNullOrEmpty(dev1)) dev1 = "Х";
                if (string.IsNullOrEmpty(term1)) term1 = "1";
                if (string.IsNullOrEmpty(dev2)) dev2 = "Х";
                if (string.IsNullOrEmpty(term2)) term2 = "1";

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, "WIRE", 256);
                    DwgAccess.EnsureLayer(tr, "WIRE_NUM", 30);

                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var pl = new Polyline();
                    for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, 0, 0);
                    pl.Layer = "WIRE";
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    SetWireXData(tr, pl, wireNum, dev1, term1, dev2, term2);

                    // номер в кружке на середине
                    var mid = pl.GetPointAtDist(pl.Length / 2.0);
                    PlaceWireTag(tr, ms, mid, wireNum, txth);

                    tr.Commit();
                }
                ed.WriteMessage($"\n; Провод #{wireNum} начерчен. Данные в XData WIRE_DATA.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! DrawWire: " + ex.Message); }
        }

        private static void PlaceWireTag(Transaction tr, BlockTableRecord ms, Point3d pt, int num, double h)
        {
            double r = h * 0.7;
            var circ = new Circle { Center = pt, Radius = r, Layer = "WIRE_NUM" };
            ms.AppendEntity(circ);
            tr.AddNewlyCreatedDBObject(circ, true);
            var mt = new MText { Location = pt, Contents = num.ToString(), TextHeight = r * 0.65, Attachment = AttachmentPoint.MiddleCenter, Layer = "WIRE_NUM" };
            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
        }

        private sealed class WireRecord
        {
            public int Num;
            public string Dev1;
            public string Term1;
            public string Dev2;
            public string Term2;
            public double LenM;
        }

        // ---------- WireTable ----------
        [CommandMethod("WireTable")]
        public static void WireTable()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var ed = Ed;
                var data = new List<WireRecord>();
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        if (id.ObjectClass.DxfName != "LWPOLYLINE") continue;
                        var pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);
                        var vals = GetWireXData(pl);
                        if (vals.Count < 5) continue;
                        data.Add(new WireRecord { Num = int.Parse(vals[0]), Dev1 = vals[1], Term1 = vals[2], Dev2 = vals[3], Term2 = vals[4], LenM = pl.Length / 1000.0 });
                    }
                    tr.Commit();
                }
                if (data.Count == 0) { ed.WriteMessage("\n! Проводов с XData WIRE_DATA не найдено (DrawWire)"); return; }
                var pp = ed.GetPoint("\nТочка вставки таблицы: ");
                if (pp.Status != PromptStatus.OK) return;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var header = new[] { "№", "Откуда", "Клемма", "Куда", "Клемма", "Длина, м" };
                    var rows = data.OrderBy(d => d.Num)
                        .Select(d => new[] { d.Num.ToString(), d.Dev1, d.Term1, d.Dev2, d.Term2, d.LenM.ToString("F2") })
                        .ToList();
                    DwgAccess.AddTable(tr, pp.Value, header, rows, 8, 40);
                    tr.Commit();
                }
                ed.WriteMessage($"\n; Таблица проводов: {data.Count} шт");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! WireTable: " + ex.Message); }
        }

        // ---------- WireNodes ----------
        [CommandMethod("WireNodes")]
        public static void WireNodes()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var ed = Ed;
                var so = new PromptSelectionOptions { MessageForAdding = "\nВыберите провода (полилинии): " };
                var sr = ed.GetSelection(so);
                if (sr.Status != PromptStatus.OK) return;
                int nodes = 0;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, "WIRE_NODES", 8);
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    double r = ed.GetDouble(new PromptDoubleOptions("\nРадиус точки") { DefaultValue = 0.5 }).Value;
                    foreach (var id in sr.Value.GetObjectIds())
                    {
                        var pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);
                        if (pl.Layer != "WIRE") continue;
                        for (int i = 0; i < pl.NumberOfVertices; i++)
                        {
                            var v = pl.GetPoint3dAt(i);
                            var circ = new Circle { Center = v, Radius = r, Layer = "WIRE_NODES", ColorIndex = 8 };
                            ms.AppendEntity(circ);
                            tr.AddNewlyCreatedDBObject(circ, true);
                            nodes++;
                        }
                    }
                    tr.Commit();
                }
                ed.WriteMessage($"\n; Узлов начерчено: {nodes}");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! WireNodes: " + ex.Message); }
        }

        // ---------- WireSegAddr ----------
        [CommandMethod("WireSegAddr")]
        public static void WireSegAddr()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var ed = Ed;
                var pe = new PromptEntityOptions("\nВыберите линию/полилинию: ");
                pe.SetRejectMessage("\n! Не тот тип");
                pe.AddAllowedClass(typeof(Line), true);
                pe.AddAllowedClass(typeof(Polyline), true);
                var pr = ed.GetEntity(pe);
                if (pr.Status != PromptStatus.OK) return;

                double txth = ed.GetDouble(new PromptDoubleOptions("\nВысота текста") { DefaultValue = 2.5 }).Value;
                double offset = txth * 0.15;

                var pts = new List<Point3d>();
                ed.WriteMessage("\nКликай точки деления (Enter — готово): ");
                while (true)
                {
                    var gp = ed.GetPoint("\nТочка: ");
                    if (gp.Status != PromptStatus.OK) break;
                    pts.Add(gp.Value);
                }
                if (pts.Count == 0) { ed.WriteMessage("\n! Точки не выбраны"); return; }

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var ent = (Curve)tr.GetObject(pr.ObjectId, OpenMode.ForRead);
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    // проекция на кривую, дедупликация по fuzz
                    var proj = pts.Select(p => ent.GetClosestPointTo(p, false)).Distinct(new PtDistComparer(0.1)).ToList();
                    proj.Sort((a, b) => ent.GetDistAtPoint(a).CompareTo(ent.GetDistAtPoint(b)));

                    int i = 1;
                    foreach (var pt in proj)
                    {
                        var addr = ed.GetString(new PromptStringOptions($"\nАдрес для точки {i}: ") { AllowSpaces = true }).StringResult;
                        if (string.IsNullOrEmpty(addr)) { i++; continue; }
                        var pos = new Point3d(pt.X, pt.Y - offset, 0);
                        var mt = new MText { Location = pos, Contents = addr, TextHeight = txth / 5.0, Attachment = AttachmentPoint.TopCenter };
                        ms.AppendEntity(mt);
                        tr.AddNewlyCreatedDBObject(mt, true);
                        i++;
                    }
                    tr.Commit();
                    ed.WriteMessage($"\n; Расставлено адресов: {i - 1}");
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! WireSegAddr: " + ex.Message); }
        }

        private sealed class PtDistComparer : IEqualityComparer<Point3d>
        {
            private readonly double _fuzz;
            public PtDistComparer(double fuzz) { _fuzz = fuzz; }
            public bool Equals(Point3d a, Point3d b) => a.DistanceTo(b) < _fuzz;
            public int GetHashCode(Point3d p) => 0; // только через Equals
        }

        // ---------- WT (выноска) ----------
        [CommandMethod("WT")]
        public static void Wt()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var ed = Ed;
                var num = ed.GetString(new PromptStringOptions("\nНомер провода: ") { AllowSpaces = true }).StringResult;
                double txtH = ed.GetDouble(new PromptDoubleOptions("\nВысота текста") { DefaultValue = 2.5 }).Value;
                double len = txtH * 3.2;

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var p1 = ed.GetPoint("\nПервый конец: ");
                    var d1 = ed.GetPoint("\nНаправление: ");
                    if (p1.Status == PromptStatus.OK && d1.Status == PromptStatus.OK)
                        WtDraw(tr, ms, p1.Value, d1.Value, num, len, txtH);

                    var p2 = ed.GetPoint("\nВторой конец: ");
                    var d2 = ed.GetPoint("\nНаправление: ");
                    if (p2.Status == PromptStatus.OK && d2.Status == PromptStatus.OK)
                        WtDraw(tr, ms, p2.Value, d2.Value, num, len, txtH);

                    tr.Commit();
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! WT: " + ex.Message); }
        }

        private static void WtDraw(Transaction tr, BlockTableRecord ms, Point3d p, Point3d dir, string txt, double len, double h)
        {
            var ang = new Vector3d(dir.X - p.X, dir.Y - p.Y, 0).GetNormal();
            var end = p + ang * len;
            var line = new Line { StartPoint = p, EndPoint = end };
            ms.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);

            double s = 1.5;
            var v1 = ang.RotateBy(Math.PI * 0.85, Vector3d.ZAxis);
            var v2 = ang.RotateBy(-Math.PI * 0.85, Vector3d.ZAxis);
            var a1 = new Line { StartPoint = end, EndPoint = end + v1 * s };
            var a2 = new Line { StartPoint = end, EndPoint = end + v2 * s };
            ms.AppendEntity(a1); tr.AddNewlyCreatedDBObject(a1, true);
            ms.AppendEntity(a2); tr.AddNewlyCreatedDBObject(a2, true);

            var pos = end + ang * (h + 2.5);
            var mt = new MText { Location = pos, Contents = txt, TextHeight = h, Attachment = AttachmentPoint.MiddleCenter, Rotation = ang.AngleOnPlane(new Plane()) };
            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
        }
    }
}
