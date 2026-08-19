using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace El.Plugin.Ui
{
    /// <summary>
    /// Парящая легенда слоёв: цвет + имя, только слои, используемые в чертеже.
    /// Двойной клик — вкл/выкл; кнопки: обновить, покрасить, перенести выделенное.
    /// </summary>
    public sealed class LayersPaletteControl : UserControl
    {
        private readonly ListView _lv;
        private readonly List<string> _names = new List<string>();

        public LayersPaletteControl()
        {
            Width = 340;
            Height = 480;

            _lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            _lv.Columns.Add("Цвет", 40);
            _lv.Columns.Add("Слой", 140);
            _lv.Columns.Add("Об.", 40);
            _lv.Columns.Add("LINE", 45);
            _lv.Columns.Add("Вкл", 40);
            _lv.DoubleClick += (s, e) => ToggleSelected();

            var btnRefresh = new Button { Text = "Обновить", Dock = DockStyle.Bottom, Height = 26 };
            btnRefresh.Click += (s, e) => RefreshLayers();
            var btnToggle = new Button { Text = "Вкл/Выкл", Dock = DockStyle.Bottom, Height = 26 };
            btnToggle.Click += (s, e) => ToggleSelected();
            var btnColor = new Button { Text = "Покрасить…", Dock = DockStyle.Bottom, Height = 26 };
            btnColor.Click += (s, e) => ColorSelected();
            var btnMove = new Button { Text = "Перенести выделенное сюда", Dock = DockStyle.Bottom, Height = 26 };
            btnMove.Click += (s, e) => MoveSelectedToLayer();

            Controls.Add(_lv);
            Controls.Add(btnRefresh);
            Controls.Add(btnToggle);
            Controls.Add(btnColor);
            Controls.Add(btnMove);
        }

        /// <summary>Пересканировать чертёж: только используемые слои.</summary>
        public void RefreshLayers()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            _names.Clear();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lineCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var colors = new Dictionary<string, System.Drawing.Color>(StringComparer.OrdinalIgnoreCase);
            var onOff = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                    string layer = ent.Layer ?? "";
                    counts.TryGetValue(layer, out int c);
                    counts[layer] = c + 1;
                    if (ent is Line)
                    {
                        lineCounts.TryGetValue(layer, out int lc);
                        lineCounts[layer] = lc + 1;
                    }
                }
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    if (counts.ContainsKey(ltr.Name))
                    {
                        try { colors[ltr.Name] = ltr.Color.ColorValue; }
                        catch { colors[ltr.Name] = Color.White; }
                        onOff[ltr.Name] = !ltr.IsOff;
                    }
                }
                tr.Commit();
            }

            _names.AddRange(counts.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            _lv.BeginUpdate();
            _lv.Items.Clear();
            foreach (var name in _names)
            {
                var item = new ListViewItem("   ");
                item.SubItems.Add(name);
                item.SubItems.Add(counts[name].ToString());
                item.SubItems.Add(lineCounts.TryGetValue(name, out int lc) ? lc.ToString() : "0");
                item.SubItems.Add(onOff.TryGetValue(name, out bool on) && on ? "да" : "нет");
                if (colors.TryGetValue(name, out var col)) item.SubItems[0].BackColor = col;
                else item.SubItems[0].BackColor = Color.White;
                item.Tag = name;
                _lv.Items.Add(item);
            }
            _lv.EndUpdate();
        }

        private string SelectedName()
        {
            return _lv.SelectedItems.Count > 0 ? _lv.SelectedItems[0].Tag as string : null;
        }

        private void ToggleSelected()
        {
            string name = SelectedName();
            if (name == null) return;
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (lt.Has(name))
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                    ltr.IsOff = !ltr.IsOff;
                }
                tr.Commit();
            }
            RefreshLayers();
        }

        private void ColorSelected()
        {
            string name = SelectedName();
            if (name == null) return;
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            short cur = 7;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (lt.Has(name))
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForRead);
                    cur = ltr.Color.ColorIndex;
                }
                tr.Commit();
            }
            var dlg = new AciColorDialog(cur);
            if (dlg.Show() != DialogResult.OK) return;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                if (lt.Has(name))
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                    ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci, dlg.ColorIndex);
                }
                tr.Commit();
            }
            RefreshLayers();
        }

        private void MoveSelectedToLayer()
        {
            string name = SelectedName();
            if (name == null) return;
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            // запускаем команду переноса (она спросит выбор объектов)
            doc.SendStringToExecute("EL-LAYER-MOVE ", true, false, false);
        }
    }
}
