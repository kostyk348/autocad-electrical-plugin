using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using El.Core;
using El.Plugin.Ui;

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

        private static void SetWireXData(Transaction tr, Entity ent, WireRecord w)
        {
            EnsureRegApp(tr);
            var vals = w.ToXData();
            var tvs = new TypedValue[vals.Length];
            for (int i = 0; i < vals.Length; i++) tvs[i] = new TypedValue(1000, vals[i] ?? "");
            ent.XData = new ResultBuffer(tvs);
        }

        /// <summary>Все провода-препятствия: LINE + сегменты LWPOLYLINE.</summary>
        private static List<LineSeg> CollectObstacles(Transaction tr)
        {
            var result = DwgAccess.CollectLines(tr);
            var bt = (BlockTable)tr.GetObject(DwgAccess.Doc.Database.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.ObjectClass.DxfName != "LWPOLYLINE") continue;
                var pl = (Polyline)tr.GetObject(id, OpenMode.ForRead);
                for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                {
                    var p1 = pl.GetPoint2dAt(i);
                    var p2 = pl.GetPoint2dAt(i + 1);
                    result.Add(new LineSeg((int)(id.Handle.Value * 1000) + i,
                                           new Point2D(p1.X, p1.Y), new Point2D(p2.X, p2.Y))
                    {
                        Layer = pl.Layer,
                        Tag = id
                    });
                }
            }
            return result;
        }

        // ============================================================
        // EL-WIRE — полуавтоматическая трассировка (maze-роутер)
        // ============================================================
        [CommandMethod("EL-WIRE")]
        public static void ElWire()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var ed = Ed;
                ed.WriteMessage("\n=== EL-WIRE: трассировка провода (maze) ===");
                var pa = ed.GetPoint("\n→ Точка А: ");
                if (pa.Status != PromptStatus.OK) return;
                var pb = ed.GetPoint("\n→ Точка Б: ");
                if (pb.Status != PromptStatus.OK) return;
                var a = new Point2D(pa.Value.X, pa.Value.Y);
                var b = new Point2D(pb.Value.X, pb.Value.Y);

                // диалог: наконечники/цвет/кол-во/длина
                var wd = new El.Plugin.Ui.WireDialog();
                if (wd.Show() != System.Windows.Forms.DialogResult.OK) return;

                // препятствия и маршрут
                List<LineSeg> obstacles;
                RouteResult route;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    obstacles = CollectObstacles(tr);
                    tr.Commit();
                }
                route = MazeRouter.Route(a, b, obstacles, 5.0, DwgAccess.DefaultTolerance);

                var wire = new WireRecord
                {
                    Num = NextWireNumber(),
                    Dev1 = wd.Dev1, Term1 = wd.Term1, Tip1 = wd.Tip1,
                    Dev2 = wd.Dev2, Term2 = wd.Term2, Tip2 = wd.Tip2,
                    Color = wd.Color, Qty = wd.Qty
                };
                double h = 2.5;

                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, "WIRE", 256);
                    DwgAccess.EnsureLayer(tr, "WIRE_NUM", 30);
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    if (route.Found)
                    {
                        // провод — полилиния по маршруту
                        var pl = new Polyline();
                        for (int i = 0; i < route.Points.Count; i++)
                            pl.AddVertexAt(i, new Point2d(route.Points[i].X, route.Points[i].Y), 0, 0, 0);
                        pl.Layer = "WIRE";
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        SetWireXData(tr, pl, wire);
                        var mid = pl.GetPointAtDist(pl.Length / 2.0);
                        PlaceWireTag(tr, ms, mid, wire.Num, h);
                        ed.WriteMessage($"\n; Провод №{wire.Num}: {route.Points.Count} вершин, длина {pl.Length / 1000.0:F2} м");
                    }
                    else
                    {
                        // пути нет — стрелки-переходы от А и Б (взаимные), провод не рисуем
                        wire.IsJump = true;
                        DrawJumpArrows(tr, ms, a, b, wire, h);
                        ed.WriteMessage($"\n; Путь не найден — переход №{wire.Num}: стрелки от А и Б.");
                    }
                    tr.Commit();
                }

                // предложить открыть редактируемую таблицу
                var ask = new PromptKeywordOptions("\nОткрыть таблицу проводов (редактирование)? [Да/Нет] <Нет>: ");
                ask.Keywords.Add("Да"); ask.Keywords.Add("Нет"); ask.Keywords.Default = "Нет";
                var kr = Ed.GetKeywords(ask);
                if (kr.Status == PromptStatus.OK && kr.StringResult == "Да")
                {
                    WireTable();
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-WIRE: " + ex.Message); Plugin.Log(ex); }
        }

        /// <summary>
        /// Стрелки-переходы (ГОСТ): короткий отрезок со стрелкой от точки А к точке Б
        /// (и от Б к А — взаимно), «немного вверх», рядом номер провода. Линия не рисуется.
        /// XData вешается на стрелку от А (для таблицы проводов).
        /// </summary>
        private static void DrawJumpArrows(Transaction tr, BlockTableRecord ms, Point2D a, Point2D b,
                                           WireRecord wire, double h)
        {
            double len = h * 3.0;
            double up = -30.0 * Math.PI / 180.0; // «немного вверх»

            ObjectId arrowA = DrawOneArrow(tr, ms, a, b, len, up, wire.Num, h);
            DrawOneArrow(tr, ms, b, a, len, up, wire.Num, h);

            // XData на стрелке от А
            if (!arrowA.IsNull)
            {
                var ent = (Entity)tr.GetObject(arrowA, OpenMode.ForWrite);
                SetWireXData(tr, ent, wire);
            }
        }

        private static ObjectId DrawOneArrow(Transaction tr, BlockTableRecord ms, Point2D from, Point2D to,
                                             double len, double upAngle, int num, double h)
        {
            double ang = Math.Atan2(to.Y - from.Y, to.X - from.X) + upAngle;
            var u = new Point2D(Math.Cos(ang), Math.Sin(ang));
            var end = new Point2D(from.X + u.X * len, from.Y + u.Y * len);

            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(from.X, from.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(end.X, end.Y), 0, 0, 0);
            pl.Layer = "WIRE";
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);

            // штрихи стрелки
            double s = h * 1.2;
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                double a2 = ang + sgn * 160.0 * Math.PI / 180.0;
                var l2 = new Line
                {
                    StartPoint = new Point3d(end.X, end.Y, 0),
                    EndPoint = new Point3d(end.X + Math.Cos(a2) * s, end.Y + Math.Sin(a2) * s, 0),
                    Layer = "WIRE"
                };
                ms.AppendEntity(l2);
                tr.AddNewlyCreatedDBObject(l2, true);
            }

            // номер рядом (над стрелкой)
            var pos = new Point3d(end.X + u.X * (h + 1.0), end.Y + u.Y * (h + 1.0), 0);
            var mt = new MText { Location = pos, Contents = num.ToString(), TextHeight = h, Attachment = AttachmentPoint.MiddleCenter, Layer = "WIRE_NUM" };
            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);

            return pl.ObjectId;
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
            catch (System.Exception ex) { Ed.WriteMessage("\n! DrawWire: " + ex.Message); Plugin.Log(ex); }
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

        private sealed class WireRow
        {
            public WireRecord W;
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
                var data = new List<WireRowData>();
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
                        var w = WireRecord.FromXData(vals);
                        double len = w.IsJump ? 0.0 : pl.Length / 1000.0;
                        data.Add(new WireRowData { W = w, LenM = len, Id = id, IsJump = w.IsJump });
                    }
                    tr.Commit();
                }
                if (data.Count == 0) { ed.WriteMessage("\n! Проводов с XData WIRE_DATA не найдено (DrawWire / EL-WIRE)"); return; }

                // GUI-редактор: наконечники/цвет/кол-во/длина + сохранение в XData
                var dlg = new El.Plugin.Ui.WireTableDialog(data);
                var dr = dlg.Show();
                if (dr != System.Windows.Forms.DialogResult.OK && dr != System.Windows.Forms.DialogResult.Cancel)
                    return;
                if (dlg.Saved) ed.WriteMessage("\n; Изменения сохранены в чертёж.");
                if (!dlg.InsertTable) return;

                var pp = ed.GetPoint("\nТочка вставки таблицы: ");
                if (pp.Status != PromptStatus.OK) return;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var header = new[] { "№", "Откуда", "Клемма", "Нак.", "Куда", "Клемма", "Нак.", "Цвет", "Кол-во", "Длина, м", "Прим." };
                    var rows = data.OrderBy(d => d.W.Num)
                        .Select(d => new[]
                        {
                            d.W.Num.ToString(), d.W.Dev1, d.W.Term1, d.W.Tip1,
                            d.W.Dev2, d.W.Term2, d.W.Tip2, d.W.Color, d.W.Qty.ToString(),
                            d.W.IsJump ? "—" : d.LenM.ToString("F2"),
                            d.W.IsJump ? "переход" : ""
                        })
                        .ToList();
                    DwgAccess.AddTable(tr, pp.Value, header, rows, 8, 30);
                    tr.Commit();
                }
                ed.WriteMessage($"\n; Таблица проводов вставлена: {data.Count} шт (переходов: {data.Count(d => d.W.IsJump)})");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! WireTable: " + ex.Message); Plugin.Log(ex); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n! WireNodes: " + ex.Message); Plugin.Log(ex); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n! WireSegAddr: " + ex.Message); Plugin.Log(ex); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n! WT: " + ex.Message); Plugin.Log(ex); }
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
