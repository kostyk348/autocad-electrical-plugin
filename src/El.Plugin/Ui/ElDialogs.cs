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

    /// <summary>Выбор цвета ACI (1-255) с предпросмотром.</summary>
    public sealed class AciColorDialog
    {
        private readonly Form _f;
        private readonly NumericUpDown _num;
        private readonly Panel _preview;
        public short ColorIndex => (short)_num.Value;

        public AciColorDialog(short initial = 7)
        {
            _f = new Form
            {
                Text = "Цвет слоя (ACI)",
                Width = 260,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            };
            _num = new NumericUpDown { Left = 12, Top = 14, Width = 90, Minimum = 1, Maximum = 255, Value = initial };
            _preview = new Panel { Left = 120, Top = 12, Width = 110, Height = 26, BorderStyle = BorderStyle.FixedSingle };
            _num.ValueChanged += (s, e) => UpdatePreview();
            UpdatePreview();
            var ok = new Button { Text = "OK", Left = 60, Top = 78, Width = 84 };
            var cancel = new Button { Text = "Отмена", Left = 150, Top = 78, Width = 84 };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            _f.Controls.AddRange(new Control[] { _num, _preview, ok, cancel });
            _f.AcceptButton = ok;
            _f.CancelButton = cancel;
        }

        private void UpdatePreview()
        {
            try
            {
                var c = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, (short)_num.Value);
                _preview.BackColor = c.ColorValue;
            }
            catch { _preview.BackColor = Color.White; }
        }

        public DialogResult Show() => _f.ShowDialog(WindowWrapper.Acad);
    }

    /// <summary>Диалог провода: откуда/куда (устройство, клемма, наконечник), цвет, кол-во, длина.</summary>
    public sealed class WireDialog
    {
        private readonly Form _f;
        private readonly TextBox _dev1, _term1, _dev2, _term2, _len;
        private readonly ComboBox _tip1, _tip2, _color;
        private readonly NumericUpDown _qty;

        public string Dev1 => _dev1.Text;
        public string Term1 => _term1.Text;
        public string Tip1 => _tip1.Text;
        public string Dev2 => _dev2.Text;
        public string Term2 => _term2.Text;
        public string Tip2 => _tip2.Text;
        public string Color => _color.Text;
        public int Qty => (int)_qty.Value;
        /// <summary>null — длина по геометрии; иначе — введённая (м).</summary>
        public double? LengthM
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_len.Text)) return null;
                return double.TryParse(_len.Text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
            }
        }

        public WireDialog(string tipDefault = "Н")
        {
            _f = new Form
            {
                Text = "Провод",
                Width = 420,
                Height = 330,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent
            };
            string[] tips = { "", "Н", "Обжим", "Вилка", "Кольцо", "Лопатка", "Штырь", "Трубка", "Н/У" };
            string[] colors = { "", "КРАСН", "СИН", "ЧЕРН", "БЕЛ", "ЖЕЛТ", "ЗЕЛ", "СЕР", "КОРИЧ", "ОРАНЖ", "ФИОЛЕТ", "РОЗОВ", "ГОЛУБ" };

            int y = 12;
            AddLabel("Откуда (устройство):", y);
            _dev1 = new TextBox { Left = 150, Top = y - 3, Width = 240 }; y += 26;
            AddLabel("Клемма:", y);
            _term1 = new TextBox { Left = 150, Top = y - 3, Width = 120 };
            AddLabel("Наконечник:", y);
            _tip1 = Cmb(tips, tipDefault, 280, y - 3); y += 26;
            AddLabel("Куда (устройство):", y);
            _dev2 = new TextBox { Left = 150, Top = y - 3, Width = 240 }; y += 26;
            AddLabel("Клемма:", y);
            _term2 = new TextBox { Left = 150, Top = y - 3, Width = 120 };
            AddLabel("Наконечник:", y);
            _tip2 = Cmb(tips, tipDefault, 280, y - 3); y += 26;
            AddLabel("Цвет:", y);
            _color = Cmb(colors, "", 150, y - 3);
            AddLabel("Кол-во:", y);
            _qty = new NumericUpDown { Left = 250, Top = y - 3, Width = 60, Minimum = 1, Maximum = 999, Value = 1 }; y += 26;
            AddLabel("Длина, м (пусто — авто):", y);
            _len = new TextBox { Left = 150, Top = y - 3, Width = 120 }; y += 34;

            var ok = new Button { Text = "ОК", Left = 200, Top = y, Width = 90 };
            var cancel = new Button { Text = "Отмена", Left = 298, Top = y, Width = 90 };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            _f.Controls.AddRange(new Control[] { _dev1, _term1, _dev2, _term2, _tip1, _tip2, _color, _qty, _len, ok, cancel });
            _f.AcceptButton = ok;
            _f.CancelButton = cancel;
        }

        private static ComboBox Cmb(string[] items, string sel, int left, int top)
        {
            var c = new ComboBox { Left = left, Top = top, Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var it in items) c.Items.Add(it);
            c.SelectedItem = sel;
            return c;
        }

        private void AddLabel(string text, int top)
        {
            _f.Controls.Add(new Label { Text = text, Left = 12, Top = top + 3, Width = 140 });
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
