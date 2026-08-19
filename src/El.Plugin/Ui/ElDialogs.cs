using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace El.Plugin.Ui
{
    /// <summary>Обёртка IntPtr в IWin32Window (для owner-диалогов в AutoCAD).</summary>
    public sealed class WindowWrapper : IWin32Window
    {
        public IntPtr Handle { get; }
        public WindowWrapper(IntPtr handle) { Handle = handle; }
        public static IWin32Window Acad => new WindowWrapper(
            Autodesk.AutoCAD.ApplicationServices.Application.MainWindow.Handle);
    }

    /// <summary>Простой ввод текста (Ok/Cancel).</summary>
    public sealed class InputDialog
    {
        private readonly Form _f;
        private readonly TextBox _tb;

        public string Value => _tb.Text;

        public InputDialog(string title, string label, string defaultValue = "")
        {
            _f = new Form
            {
                Text = title,
                Width = 420,
                Height = 130,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            };
            var lbl = new Label { Text = label, Left = 12, Top = 14, Width = 380 };
            _tb = new TextBox { Left = 12, Top = 38, Width = 380, Text = defaultValue };
            var ok = new Button { Text = "OK", Left = 220, Top = 66, Width = 84 };
            var cancel = new Button { Text = "Отмена", Left = 312, Top = 66, Width = 84 };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            _f.Controls.AddRange(new Control[] { lbl, _tb, ok, cancel });
            _f.AcceptButton = ok;
            _f.CancelButton = cancel;
        }

        public DialogResult Show() => _f.ShowDialog(WindowWrapper.Acad);
    }

    /// <summary>Выбор одного элемента из списка (Ok/Cancel).</summary>
    public sealed class ListPickDialog
    {
        private readonly Form _f;
        private readonly ListBox _lb;

        public string Selected => _lb.SelectedItem as string;

        public ListPickDialog(string title, string label, IReadOnlyList<string> items)
        {
            _f = new Form
            {
                Text = title,
                Width = 460,
                Height = 420,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            };
            var lbl = new Label { Text = label, Left = 12, Top = 10, Width = 420 };
            _lb = new ListBox { Left = 12, Top = 34, Width = 420, Height = 300 };
            foreach (var it in items) _lb.Items.Add(it);
            if (_lb.Items.Count > 0) _lb.SelectedIndex = 0;
            var ok = new Button { Text = "OK", Left = 250, Top = 346, Width = 88 };
            var cancel = new Button { Text = "Отмена", Left = 344, Top = 346, Width = 88 };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            _f.Controls.AddRange(new Control[] { lbl, _lb, ok, cancel });
            _f.AcceptButton = ok;
            _f.CancelButton = cancel;
        }

        public DialogResult Show() => _f.ShowDialog(WindowWrapper.Acad);
    }

    /// <summary>Диалог штампа: лист/всего/дата/обозначение.</summary>
    public sealed class TitleBlockDialog
    {
        private readonly Form _f;
        private readonly NumericUpDown _sheet, _total;
        private readonly TextBox _date, _design;

        public int Sheet => (int)_sheet.Value;
        public int Total => (int)_total.Value;
        public string Date => _date.Text;
        public string Design => _design.Text;

        public TitleBlockDialog(string defaultDate)
        {
            _f = new Form
            {
                Text = "Штамп (основная надпись)",
                Width = 400,
                Height = 240,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            };
            _sheet = Num(12, 20, 1, 999, 1);
            _total = Num(12, 66, 1, 999, 1);
            _date = new TextBox { Left = 110, Top = 112, Width = 260, Text = defaultDate };
            _design = new TextBox { Left = 110, Top = 140, Width = 260 };
            AddLabel("Лист №", 10);
            AddLabel("Всего листов", 56);
            AddLabel("Дата", 102);
            AddLabel("Обозначение", 130);
            var ok = new Button { Text = "Применить", Left = 180, Top = 172, Width = 96 };
            var cancel = new Button { Text = "Отмена", Left = 282, Top = 172, Width = 96 };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            _f.Controls.AddRange(new Control[] { _sheet, _total, _date, _design, ok, cancel });
            _f.AcceptButton = ok;
            _f.CancelButton = cancel;
        }

        private static NumericUpDown Num(int left, int top, int min, int max, int val)
        {
            return new NumericUpDown
            {
                Left = left, Top = top, Width = 80, Minimum = min, Maximum = max, Value = val
            };
        }

        private void AddLabel(string text, int top)
        {
            _f.Controls.Add(new Label { Text = text, Left = 12, Top = top + 3, Width = 90 });
        }

        public DialogResult Show() => _f.ShowDialog(WindowWrapper.Acad);
    }

    /// <summary>Список слепков OMNI: открыть/удалить.</summary>
    public sealed class OmniLogDialog
    {
        private readonly Form _f;
        private readonly ListView _lv;
        public string SelectedFile { get; private set; }
        public string Action { get; private set; } // "open" | "delete" | ""

        public OmniLogDialog(IReadOnlyList<string> files)
        {
            _f = new Form
            {
                Text = "Слепки OMNI",
                Width = 560,
                Height = 420,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            };
            _lv = new ListView
            {
                Left = 12, Top = 12, Width = 520, Height = 320,
                View = View.Details, FullRowSelect = true, MultiSelect = false
            };
            _lv.Columns.Add("№", 40);
            _lv.Columns.Add("Имя", 250);
            _lv.Columns.Add("Дата", 130);
            _lv.Columns.Add("Размер, КБ", 90);
            for (int i = 0; i < files.Count; i++)
            {
                var fi = new System.IO.FileInfo(files[i]);
                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(fi.Name);
                item.SubItems.Add(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add((fi.Length / 1024).ToString());
                item.Tag = files[i];
                _lv.Items.Add(item);
            }
            if (_lv.Items.Count > 0) _lv.Items[0].Selected = true;
            var open = new Button { Text = "Открыть", Left = 12, Top = 342, Width = 100 };
            var del = new Button { Text = "Удалить", Left = 118, Top = 342, Width = 100 };
            var close = new Button { Text = "Закрыть", Left = 432, Top = 342, Width = 100 };
            open.Click += (s, e) => { SelectedFile = SelectedTag(); Action = "open"; _f.Close(); };
            del.Click += (s, e) =>
            {
                SelectedFile = SelectedTag();
                if (SelectedFile == null) return;
                if (MessageBox.Show(WindowWrapper.Acad, "Удалить слепок?\n" + SelectedFile, "OMNI",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    Action = "delete";
                    _f.Close();
                }
            };
            close.Click += (s, e) => _f.Close();
            _f.Controls.AddRange(new Control[] { _lv, open, del, close });
        }

        private string SelectedTag()
        {
            return _lv.SelectedItems.Count > 0 ? _lv.SelectedItems[0].Tag as string : null;
        }

        public DialogResult Show() => _f.ShowDialog(WindowWrapper.Acad);
    }

    /// <summary>Таблица BOM: показать, вставить в чертёж, экспорт CSV.</summary>
    public sealed class BomDialog
    {
        private readonly Form _f;
        private readonly ListView _lv;
        public bool InsertTable { get; private set; }
        public bool ExportCsv { get; private set; }

        public BomDialog(IReadOnlyList<KeyValuePair<string, int>> counts)
        {
            _f = new Form
            {
                Text = "Спецификация компонентов (BOM)",
                Width = 460,
                Height = 440,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            };
            _lv = new ListView
            {
                Left = 12, Top = 12, Width = 420, Height = 340,
                View = View.Details, FullRowSelect = true
            };
            _lv.Columns.Add("Блок", 260);
            _lv.Columns.Add("Кол-во", 120);
            foreach (var kv in counts)
            {
                var item = new ListViewItem(kv.Key);
                item.SubItems.Add(kv.Value.ToString());
                _lv.Items.Add(item);
            }
            var tbl = new Button { Text = "Таблица в чертёж", Left = 12, Top = 362, Width = 130 };
            var csv = new Button { Text = "Экспорт CSV", Left = 148, Top = 362, Width = 110 };
            var close = new Button { Text = "Закрыть", Left = 340, Top = 362, Width = 92 };
            tbl.Click += (s, e) => { InsertTable = true; _f.Close(); };
            csv.Click += (s, e) => { ExportCsv = true; _f.Close(); };
            close.Click += (s, e) => _f.Close();
            _f.Controls.AddRange(new Control[] { _lv, tbl, csv, close });
        }

        public DialogResult Show() => _f.ShowDialog(WindowWrapper.Acad);
    }
}
