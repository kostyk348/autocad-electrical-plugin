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

        /// <summary>Публичная обёртка списка слепков (для диффов).</summary>
        public static List<string> SnapshotFilesPublic() => SnapshotFiles();

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
                var dlg = new El.Plugin.Ui.InputDialog("OMNI — слепок", "Описание изменения:");
                if (dlg.Show() != System.Windows.Forms.DialogResult.OK) return;
                string msg = dlg.Value;

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
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] SNAP: " + ex.Message); Plugin.Log(ex); }
        }

        // ---------- OMNI-LOG ----------
        [CommandMethod("OMNI-LOG")]
        public static void OmniLog()
        {
            try
            {
                var files = SnapshotFiles();
                if (files.Count == 0) { Ed.WriteMessage("\n[OMNI] Слепков нет. Сначала OMNI-SNAP."); return; }
                var dlg = new El.Plugin.Ui.OmniLogDialog(files);
                if (dlg.Show() != System.Windows.Forms.DialogResult.OK || dlg.SelectedFile == null) return;
                if (dlg.Action == "open")
                {
                    Application.DocumentManager.Open(dlg.SelectedFile);
                }
                else if (dlg.Action == "delete")
                {
                    try { System.IO.File.Delete(dlg.SelectedFile); Ed.WriteMessage("\n[OMNI] Слепок удалён."); }
                    catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] Ошибка удаления: " + ex.Message); Plugin.Log(ex); }
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] LOG: " + ex.Message); Plugin.Log(ex); }
        }

        // ---------- OMNI-DIFF ----------
        [CommandMethod("OMNI-DIFF")]
        public static void OmniDiff()
        {
            try
            {
                var files = SnapshotFiles();
                if (files.Count == 0) { Ed.WriteMessage("\n[OMNI] Слепков нет."); return; }
                var names = files.Select(f => System.IO.Path.GetFileName(f)).ToList();
                var dlg = new El.Plugin.Ui.ListPickDialog("OMNI — сравнение", "Ревизия для наложения:", names);
                if (dlg.Show() != System.Windows.Forms.DialogResult.OK || dlg.Selected == null) return;
                int idx = names.IndexOf(dlg.Selected);
                if (idx < 0) return;
                string snap = files[idx];
                string blockName = Path.GetFileNameWithoutExtension(snap);
                var doc = DwgAccess.Doc;
                var db = doc.Database;

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
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] DIFF: " + ex.Message); Plugin.Log(ex); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] CLEAR: " + ex.Message); Plugin.Log(ex); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] TOGGLE: " + ex.Message); Plugin.Log(ex); }
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
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] NOTE: " + ex.Message); Plugin.Log(ex); }
        }
    }
}
