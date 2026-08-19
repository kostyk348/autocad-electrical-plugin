using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;
using El.Core;

namespace El.Plugin.Ui
{
    /// <summary>
    /// Палитра «Цепи»: список цепей с дефектами, зум к ним двойным кликом.
    /// </summary>
    public sealed class Palette : IDisposable
    {
        public static Palette Instance;

        private readonly PaletteSet _ps;
        private readonly ListView _list;
        private readonly LayersPaletteControl _layers;
        private List<List<int>> _defectChains = new List<List<int>>();

        public LayersPaletteControl Layers => _layers;

        public Palette()
        {
            _ps = new PaletteSet("Электроавтоматика", new Guid("3B2F1C4A-9E6D-4C5B-8A3F-1E2D4F6A8B0C"));
            _ps.Style = PaletteSetStyles.Snappable | PaletteSetStyles.ShowPropertiesMenu;

            var host = new UserControl { Width = 320, Height = 480 };
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false
            };
            _list.Columns.Add("Цепь", 60);
            _list.Columns.Add("Линий", 50);
            _list.Columns.Add("Дефект", 200);
            _list.DoubleClick += OnDoubleClick;

            var btnZoom = new Button { Text = "Зум к дефекту", Dock = DockStyle.Bottom, Height = 28 };
            btnZoom.Click += (s, e) => ZoomSelected();
            var btnRefresh = new Button { Text = "Обновить (EL-CHECK)", Dock = DockStyle.Bottom, Height = 28 };
            btnRefresh.Click += (s, e) =>
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute("EL-CHECK ", true, false, false);
            };

            host.Controls.Add(_list);
            host.Controls.Add(btnZoom);
            host.Controls.Add(btnRefresh);

            _layers = new LayersPaletteControl();

            _ps.Add("Цепи", host);
            _ps.Add("Слои", _layers);
        }

        /// <summary>Показать палитру; при первом показе — заполнить легенду слоёв.</summary>
        public void Show()
        {
            _layers.RefreshLayers();
            _ps.Visible = true;
        }

        public void ShowReport(List<string> report, List<List<int>> defectChains)
        {
            _defectChains = defectChains ?? new List<List<int>>();
            _list.BeginUpdate();
            _list.Items.Clear();
            if (CommandState.Chains != null)
            {
                for (int i = 0; i < CommandState.Chains.Count; i++)
                {
                    var ch = CommandState.Chains[i];
                    string defect = "";
                    if (_defectChains.Contains(ch)) defect = "без подписей";
                    var item = new ListViewItem((i + 1).ToString());
                    item.SubItems.Add(ch.Count.ToString());
                    item.SubItems.Add(defect);
                    item.Tag = ch;
                    _list.Items.Add(item);
                }
            }
            _list.EndUpdate();
            _ps.Visible = true;
        }

        private void OnDoubleClick(object sender, EventArgs e) => ZoomSelected();

        private void ZoomSelected()
        {
            if (_list.SelectedItems.Count == 0) return;
            var ch = (List<int>)_list.SelectedItems[0].Tag;
            if (ch == null || CommandState.Lines == null) return;
            var segs = ch.Select(id => CommandState.Lines.First(l => l.Id == id)).ToList();
            DwgAccess.ZoomTo(segs);
        }

        public void Dispose()
        {
            try { _ps.Dispose(); } catch { }
        }
    }
}
