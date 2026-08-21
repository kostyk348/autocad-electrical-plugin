using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using El.Core;

namespace El.Plugin.Ui
{
    /// <summary>Строка таблицы проводов (для GUI-редактора).</summary>
    public sealed class WireRowData
    {
        public ObjectId Id;
        public WireRecord W;
        public double LenM;
        public bool IsJump;
    }

    /// <summary>
    /// Редактируемая таблица проводов: наконечники, цвет, кол-во, длина —
    /// с сохранением обратно в XData чертежа.
    /// </summary>
    public sealed class WireTableDialog
    {
        private readonly Form _f;
        private readonly DataGridView _g;
        private readonly List<Row> _rows = new List<Row>();

        public bool Saved { get; private set; }
        public bool InsertTable { get; private set; }

        private sealed class Row
        {
            public ObjectId Id;
            public WireRecord W;
            public double LenM;
            public bool IsJump;
        }

        private static readonly string[] Tips = { "", "Н", "Обжим", "Вилка", "Кольцо", "Лопатка", "Штырь", "Трубка", "Н/У" };
        private static readonly string[] Colors = { "", "КРАСН", "СИН", "ЧЕРН", "БЕЛ", "ЖЕЛТ", "ЗЕЛ", "СЕР", "КОРИЧ", "ОРАНЖ", "ФИОЛЕТ", "РОЗОВ", "ГОЛУБ" };

        public WireTableDialog(List<WireRowData> data)
        {
            foreach (var d in data)
                _rows.Add(new Row { Id = d.Id, W = d.W, LenM = d.LenM, IsJump = d.IsJump });

            _f = new Form
            {
                Text = $"Таблица проводов ({_rows.Count} шт) — редактирование",
                Width = 900,
                Height = 520,
                StartPosition = FormStartPosition.CenterParent
            };

            _g = new DataGridView
            {
                Left = 12, Top = 12, Width = 860, Height = 400,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
            };
            _g.Columns.Add("num", "№");
            _g.Columns.Add("dev1", "Откуда");
            _g.Columns.Add("term1", "Клемма");
            var tip1 = new DataGridViewComboBoxColumn { Name = "tip1", HeaderText = "Нак." };
            tip1.Items.AddRange(Tips);
            _g.Columns.Add(tip1);
            _g.Columns.Add("dev2", "Куда");
            _g.Columns.Add("term2", "Клемма");
            var tip2 = new DataGridViewComboBoxColumn { Name = "tip2", HeaderText = "Нак." };
            tip2.Items.AddRange(Tips);
            _g.Columns.Add(tip2);
            var col = new DataGridViewComboBoxColumn { Name = "color", HeaderText = "Цвет" };
            col.Items.AddRange(Colors);
            _g.Columns.Add(col);
            _g.Columns.Add("qty", "Кол-во");
            _g.Columns.Add("len", "Длина, м");
            _g.Columns.Add("prm", "Прим.");

            foreach (var r in _rows)
            {
                int i = _g.Rows.Add(
                    r.W.Num, r.W.Dev1, r.W.Term1, r.W.Tip1,
                    r.W.Dev2, r.W.Term2, r.W.Tip2, r.W.Color,
                    r.W.Qty, r.IsJump ? "—" : r.LenM.ToString("F2"),
                    r.IsJump ? "переход" : "");
                _g.Rows[i].Tag = r;
                if (r.IsJump) _g.Rows[i].Cells["len"].ReadOnly = true;
            }
            _g.Columns["num"].ReadOnly = true;
            _g.Columns["prm"].ReadOnly = true;

            int y = 424;
            var btnSave = new Button { Text = "Сохранить в чертёж", Left = 12, Top = y, Width = 150 };
            var btnLen = new Button { Text = "Длина по геометрии", Left = 170, Top = y, Width = 150 };
            var btnTable = new Button { Text = "Таблица в чертёж", Left = 328, Top = y, Width = 140 };
            var btnCsv = new Button { Text = "Экспорт CSV", Left = 476, Top = y, Width = 110 };
            var btnClose = new Button { Text = "Закрыть", Left = 760, Top = y, Width = 110 };

            btnSave.Click += (s, e) => SaveToDrawing();
            btnLen.Click += (s, e) => RecalcLengths();
            btnTable.Click += (s, e) =>
            {
                SaveToDrawing();
                InsertTable = true;
                _f.Close();
            };
            btnCsv.Click += (s, e) => ExportCsv();
            btnClose.Click += (s, e) => _f.Close();

            _f.Controls.AddRange(new Control[] { _g, btnSave, btnLen, btnTable, btnCsv, btnClose });
        }

        public DialogResult Show() => _f.ShowDialog(WindowWrapper.Acad);

        private WireRecord FromRow(DataGridViewRow r)
        {
            var w = new WireRecord
            {
                Num = _num(r, "num"),
                Dev1 = _str(r, "dev1"),
                Term1 = _str(r, "term1"),
                Tip1 = _str(r, "tip1"),
                Dev2 = _str(r, "dev2"),
                Term2 = _str(r, "term2"),
                Tip2 = _str(r, "tip2"),
                Color = _str(r, "color"),
                Qty = _int(r, "qty", 1)
            };
            if (r.Tag is Row row && row.IsJump) w.IsJump = true;
            return w;
        }

        private static string _str(DataGridViewRow r, string col)
        {
            return r.Cells[col].Value?.ToString()?.Trim() ?? "";
        }

        private static int _num(DataGridViewRow r, string col)
        {
            int.TryParse(_str(r, col), out int v);
            return v;
        }

        private static int _int(DataGridViewRow r, string col, int def)
        {
            int.TryParse(_str(r, col), out int v);
            return v > 0 ? v : def;
        }

        private void SaveToDrawing()
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                int saved = 0;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (DataGridViewRow gr in _g.Rows)
                    {
                        if (!(gr.Tag is Row row) || row.Id.IsNull || row.Id.IsErased) continue;
                        var w = FromRow(gr);
                        var ent = (Entity)tr.GetObject(row.Id, OpenMode.ForWrite);
                        var rb = new ResultBuffer(w.ToXData().Select(v => new TypedValue(1000, v ?? "")).ToArray());
                        ent.XData = rb;
                        saved++;
                    }
                    tr.Commit();
                }
                Saved = true;
                MessageBox.Show(WindowWrapper.Acad, $"Сохранено в чертёж: {saved} проводов.", "Таблица проводов",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(WindowWrapper.Acad, "Ошибка сохранения: " + ex.Message, "Таблица проводов",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Plugin.Log(ex);
            }
        }

        private void RecalcLengths()
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (DataGridViewRow gr in _g.Rows)
                    {
                        if (!(gr.Tag is Row row) || row.Id.IsNull || row.Id.IsErased) continue;
                        if (row.IsJump) continue;
                        var pl = (Polyline)tr.GetObject(row.Id, OpenMode.ForRead);
                        gr.Cells["len"].Value = (pl.Length / 1000.0).ToString("F2");
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex) { Plugin.Log(ex); }
        }

        private void ExportCsv()
        {
            try
            {
                var dlg = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "wires.csv" };
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var sb = new StringBuilder();
                sb.AppendLine("№;Откуда;Клемма;Нак.;Куда;Клемма;Нак.;Цвет;Кол-во;Длина,м;Прим.");
                foreach (DataGridViewRow gr in _g.Rows)
                {
                    var w = FromRow(gr);
                    sb.AppendLine($"{w.Num};{w.Dev1};{w.Term1};{w.Tip1};{w.Dev2};{w.Term2};{w.Tip2};{w.Color};{w.Qty};{gr.Cells["len"].Value};{gr.Cells["prm"].Value}");
                }
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            }
            catch (System.Exception ex) { Plugin.Log(ex); }
        }
    }
}
