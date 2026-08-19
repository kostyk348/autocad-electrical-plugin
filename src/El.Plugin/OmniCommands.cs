using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace El.Plugin
{
    /// <summary>OMNI: слепки DWG и сравнение наложением (C#-порт LISP OMNI v0.4).</summary>
    public static class OmniCommands
    {
        private static Editor Ed => DwgAccess.Ed;

        private static string HistoryDir()
        {
            var doc = DwgAccess.Doc;
            var db = doc.Database;
            string prefix = Path.GetDirectoryName(db.Filename) + Path.DirectorySeparatorChar;
            string dir = prefix + "_OMNI_HISTORY" + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static List<string> SnapshotFiles()
        {
            var dir = HistoryDir();
            return Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.dwg").OrderBy(f => f).ToList()
                : new List<string>();
        }

        // ---------- OMNI-SNAP ----------
        [CommandMethod("OMNI-SNAP")]
        public static void OmniSnap()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var db = doc.Database;
                var msg = Ed.GetString(new PromptStringOptions("\n[OMNI] Описание изменения (или Enter): ") { AllowSpaces = true }).StringResult;
                if (msg == null) msg = "";

                string src = db.Filename;
                if (string.IsNullOrEmpty(src)) { Ed.WriteMessage("\n[OMNI] Файл не сохранён — сначала QSAVE."); return; }
                db.SaveAs(src, true, DwgVersion.Current, db.SecurityParameters);

                string date = DateTime.Now.ToString("yyyyMMdd.HHmmss");
                string ms = DateTime.Now.Millisecond.ToString("000");
                string user = Environment.UserName;
                string dwgName = Path.GetFileName(src);
                string backup = Path.Combine(HistoryDir(), $"{date}_{ms}_{user}_{msg}_{dwgName}");
                File.Copy(src, backup);
                Ed.WriteMessage($"\n[OMNI] Снимок сохранён: {backup}");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] SNAP: " + ex.Message); }
        }

        // ---------- OMNI-LOG ----------
        [CommandMethod("OMNI-LOG")]
        public static void OmniLog()
        {
            try
            {
                var files = SnapshotFiles();
                if (files.Count == 0) { Ed.WriteMessage("\n[OMNI] Слепков нет. Сначала OMNI-SNAP."); return; }
                Ed.WriteMessage("\n--- Слепки OMNI ---");
                for (int i = 0; i < files.Count; i++)
                    Ed.WriteMessage($"\n[{i + 1}] {Path.GetFileName(files[i])}");
                var pi = Ed.GetInteger(new PromptIntegerOptions("\n[OMNI] Номер для открытия (0 — отмена): ") { AllowNegative = false });
                if (pi.Status == PromptStatus.OK && pi.Value > 0 && pi.Value <= files.Count)
                {
                    Application.DocumentManager.Open(files[pi.Value - 1]);
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] LOG: " + ex.Message); }
        }

        // ---------- OMNI-DIFF ----------
        [CommandMethod("OMNI-DIFF")]
        public static void OmniDiff()
        {
            try
            {
                var files = SnapshotFiles();
                if (files.Count == 0) { Ed.WriteMessage("\n[OMNI] Слепков нет."); return; }
                Ed.WriteMessage("\n--- Для сравнения ---");
                for (int i = 0; i < files.Count; i++)
                    Ed.WriteMessage($"\n[{i + 1}] {Path.GetFileName(files[i])}");
                var pi = Ed.GetInteger(new PromptIntegerOptions("\n[OMNI] Номер ревизии (0 — отмена): ") { AllowNegative = false });
                if (pi.Status != PromptStatus.OK || pi.Value <= 0 || pi.Value > files.Count) return;

                var doc = DwgAccess.Doc;
                var db = doc.Database;
                string snap = files[pi.Value - 1];
                string blockName = Path.GetFileNameWithoutExtension(snap);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId xrefId = db.AttachXref(snap, blockName);
                    var btr = (BlockTableRecord)tr.GetObject(xrefId, OpenMode.ForRead);
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var br = new BlockReference(Point3d.Origin, xrefId);
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    // обесцветить слои xref
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId lid in lt)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        if (ltr.Name.Contains("|")) // слои xref: "имя|*"
                        {
                            ltr.UpgradeOpen();
                            ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 1);
                        }
                    }
                    tr.Commit();
                }
                Ed.WriteMessage($"\n[OMNI] Ревизия наложена: {blockName}. OMNI-TOGGLE/CLEAR для управления.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] DIFF: " + ex.Message); }
        }

        // ---------- OMNI-CLEAR ----------
        [CommandMethod("OMNI-CLEAR")]
        public static void OmniClear()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var db = doc.Database;
                string dwgBase = Path.GetFileNameWithoutExtension(db.Filename);
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var toDetach = new List<ObjectId>();
                    foreach (ObjectId bid in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead);
                        if (!btr.IsFromExternalReference) continue;
                        if (btr.Name.Contains("_" + dwgBase) || btr.Name.Contains(dwgBase + "_"))
                            toDetach.Add(bid);
                    }
                    foreach (var bid in toDetach)
                    {
                        db.DetachXref(bid);
                        Ed.WriteMessage($"\n[OMNI] Откреплён XREF: {((BlockTableRecord)tr.GetObject(bid, OpenMode.ForRead)).Name}");
                    }
                    tr.Commit();
                }
                Ed.WriteMessage("\n[OMNI] Наложение снято.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] CLEAR: " + ex.Message); }
        }

        // ---------- OMNI-TOGGLE ----------
        [CommandMethod("OMNI-TOGGLE")]
        public static void OmniToggle()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var db = doc.Database;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    var xrefLayers = new List<LayerTableRecord>();
                    foreach (ObjectId lid in lt)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        if (ltr.Name.Contains("|")) xrefLayers.Add(ltr);
                    }
                    if (xrefLayers.Count == 0)
                    {
                        Ed.WriteMessage("\n[OMNI] Наложение не найдено. Сначала OMNI-DIFF.");
                        tr.Commit();
                        return;
                    }
                    bool anyOn = xrefLayers.Any(l => l.IsOff == false);
                    foreach (var ltr in xrefLayers)
                    {
                        ltr.UpgradeOpen();
                        ltr.IsOff = anyOn; // anyOn=true → выключить
                    }
                    tr.Commit();
                }
                DwgAccess.Ed.Regen();
                Ed.WriteMessage("\n[OMNI] Слои наложения переключены.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] TOGGLE: " + ex.Message); }
        }

        // ---------- OMNI-NOTE ----------
        [CommandMethod("OMNI-NOTE")]
        public static void OmniNote()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var ed = Ed;
                var pp = ed.GetPoint("\n[OMNI] Точка заметки: ");
                if (pp.Status != PromptStatus.OK) return;
                var msg = ed.GetString(new PromptStringOptions("\n[OMNI] Текст: ") { AllowSpaces = true }).StringResult;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, "OMNI_NOTES", 1);
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var circ = new Circle { Center = pp.Value, Radius = 50, Layer = "OMNI_NOTES" };
                    ms.AppendEntity(circ); tr.AddNewlyCreatedDBObject(circ, true);
                    var mt = new MText { Location = pp.Value, Contents = msg ?? "", TextHeight = 25, Attachment = AttachmentPoint.MiddleCenter, Layer = "OMNI_NOTES" };
                    ms.AppendEntity(mt); tr.AddNewlyCreatedDBObject(mt, true);
                    tr.Commit();
                }
                Ed.WriteMessage("\n[OMNI] Заметка добавлена.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] NOTE: " + ex.Message); }
        }
    }
}
