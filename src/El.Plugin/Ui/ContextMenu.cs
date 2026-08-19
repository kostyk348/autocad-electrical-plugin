using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

namespace El.Plugin.Ui
{
    /// <summary>
    /// Контекстные меню «Электроавтоматика» по типу выделенного объекта:
    /// - LINE: трассировка, разрыв, адреса точек, выноска
    /// - TEXT/MTEXT: спецификация AW33
    /// - пустое место (default): дефектоскоп, таблица, OMNI
    /// </summary>
    public static class ContextMenu
    {
        private static ContextMenuExtension _lineMenu;
        private static ContextMenuExtension _textMenu;
        private static ContextMenuExtension _defaultMenu;
        private static bool _added;

        private static void Run(string cmd)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.SendStringToExecute(cmd + " ", true, false, false);
        }

        private static MenuItem Item(string text, string command)
        {
            var mi = new MenuItem(text);
            mi.Click += (s, e) => Run(command);
            return mi;
        }

        public static void Add()
        {
            if (_added) return;
            // --- меню для LINE ---
            _lineMenu = new ContextMenuExtension { Title = "Электроавтоматика" };
            _lineMenu.MenuItems.Add(Item("⚡ Трассировать цепь (EL-TRACE)", "EL-TRACE"));
            _lineMenu.MenuItems.Add(Item("✂️ Симулировать разрыв (EL-WHATIF)", "EL-WHATIF"));
            _lineMenu.MenuItems.Add(Item("📍 Адреса точек разбивки (WireSegAddr)", "WireSegAddr"));
            _lineMenu.MenuItems.Add(Item("🏷️ Выноска с номером (WT)", "WT"));

            // --- меню для TEXT/MTEXT ---
            _textMenu = new ContextMenuExtension { Title = "Электроавтоматика" };
            _textMenu.MenuItems.Add(Item("📊 Собрать спецификацию проводов (AW33)", "AW33"));
            _textMenu.MenuItems.Add(Item("📊 Экспорт спецификации CSV (AW33-CSV)", "AW33-CSV"));

            // --- меню по умолчанию (пустое место) ---
            _defaultMenu = new ContextMenuExtension { Title = "Электроавтоматика" };
            _defaultMenu.MenuItems.Add(Item("🛠️ Дефектоскоп схемы (EL-CHECK)", "EL-CHECK"));
            _defaultMenu.MenuItems.Add(Item("📑 Таблица соединений (EL-TABLE)", "EL-TABLE"));
            _defaultMenu.MenuItems.Add(Item("📸 OMNI: слепок (OMNI-SNAP)", "OMNI-SNAP"));
            _defaultMenu.MenuItems.Add(Item("🔍 OMNI: сравнить с ревизией (OMNI-DIFF)", "OMNI-DIFF"));

            try
            {
                Application.AddObjectContextMenuExtension(RXObject.GetClass(typeof(Line)), _lineMenu);
                Application.AddObjectContextMenuExtension(RXObject.GetClass(typeof(DBText)), _textMenu);
                Application.AddObjectContextMenuExtension(RXObject.GetClass(typeof(MText)), _textMenu);
                Application.AddDefaultContextMenuExtension(_defaultMenu);
                _added = true;
            }
            catch (System.Exception)
            {
                _added = false;
            }
        }

        public static void Remove()
        {
            if (!_added) return;
            try
            {
                Application.RemoveObjectContextMenuExtension(RXObject.GetClass(typeof(Line)), _lineMenu);
                Application.RemoveObjectContextMenuExtension(RXObject.GetClass(typeof(DBText)), _textMenu);
                Application.RemoveObjectContextMenuExtension(RXObject.GetClass(typeof(MText)), _textMenu);
                Application.RemoveDefaultContextMenuExtension(_defaultMenu);
            }
            catch { }
            _lineMenu = _textMenu = _defaultMenu = null;
            _added = false;
        }
    }
}
