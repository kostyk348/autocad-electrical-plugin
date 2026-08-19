using System;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;

namespace El.Plugin.Ui
{
    /// <summary>Минимальная реализация ICommand для кнопок ленты.</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _action;
        public RelayCommand(Action action) { _action = action; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _action();
    }

    /// <summary>Вкладка «Электроавтоматика» на ленте.</summary>
    public static class Ribbon
    {
        private static RibbonTab _tab;
        private static bool _added;

        public static void Add()
        {
            if (_added) return;
            var rc = ComponentManager.Ribbon;
            if (rc == null) return;

            _tab = new RibbonTab { Title = "Электроавтоматика", Id = "EL_TOOLS_TAB" };
            rc.Tabs.Add(_tab);

            _tab.Panels.Add(MakePanel("Цепи и Граф",
                MakeButton("Трассировать", "EL-TRACE", "Трассировка цепи по клику"),
                MakeButton("Путь между точками", "EL-PATH", "Клик A → клик B → путь по линиям"),
                MakeButton("Разрыв (что если)", "EL-WHATIF", "Симуляция разрыва цепи"),
                MakeButton("Таблица соединений", "EL-TABLE", "Откуда → Куда"),
                MakeButton("Граф (отладка)", "EL-GRAPH", "Информация о графе"),
                MakeButton("Экспорт графа", "EL-GRAPH-EXPORT", "Топология в Graphviz DOT/PNG")));

            _tab.Panels.Add(MakePanel("Аудит и Дефекты",
                MakeButton("Дефектоскоп", "EL-CHECK", "Проверки схемы"),
                MakeButton("Пересечения", "EL-CROSSING", "X-пересечения линий без узла"),
                MakeButton("Петли", "EL-LOOPS", "Поиск колец"),
                MakeButton("Узкие места", "EL-BOTTLENECK", "Топ-10"),
                MakeButton("Статистика", "EL-STATS", "Статистика чертежа"),
                MakeButton("Раскрасить цепи", "EL-COLOR-CHAINS", "Цвет по цепям")));

            _tab.Panels.Add(MakePanel("Спецификации",
                MakeButton("Спецификация AW33", "AW33", "Постраничный сбор проводов"),
                MakeButton("Спецификация HTML", "AW33-HTML", "В HTML-таблицу + браузер"),
                MakeButton("Экспорт CSV", "AW33-CSV", "Спецификация в CSV"),
                MakeButton("Провод", "DrawWire", "Полилиния + XData"),
                MakeButton("Таблица проводов", "WireTable", "Из XData"),
                MakeButton("Узлы проводов", "WireNodes", "Кружки в вершинах"),
                MakeButton("Адреса точек", "WireSegAddr", "Разбивка линии"),
                MakeButton("Выноска", "WT", "Стрелка с номером")));

            _tab.Panels.Add(MakePanel("Автоматизация",
                MakeButton("Отчёт по чертежу", "EL-REPORT", "Весь чертёж → HTML: спека+BOM+дефекты+соединения"),
                MakeButton("Сводный отчёт проекта", "EL-PROJECT-REPORT", "Все DWG папки → сводный HTML"),
                MakeButton("Сравнить с ревизией", "EL-REVISION-DIFF", "Текущий vs OMNI-снап: провода+блоки+топология"),
                MakeButton("Спецификация блоков", "EL-BOM", "Подсчёт вхождений блоков → таблица"),
                MakeButton("Штамп (лист/дата)", "EL-TITLE", "Автозаполнение атрибутов штампа"),
                MakeButton("Реестр листов", "EL-SHEET-LIST", "Все DWG в папке → CSV (фоновое чтение)"),
                MakeButton("XREF-статусы", "EL-XREF-LIST", "Список внешних ссылок и их состояние"),
                MakeButton("Отчёт EL-CHECK", "EL-CHECK-REPORT", "Дефектоскоп → md/html файл"),
                MakeButton("Номера цепей", "EL-AUTOTAG", "Номера выбранных цепей (без префикса)"),
                MakeButton("Объединить в полилинии", "EL-JOIN", "LINE → полилинии, контроль порядка")));

            _tab.Panels.Add(MakePanel("OMNI (версии)",
                MakeButton("Слепок", "OMNI-SNAP", "Копия DWG в _OMNI_HISTORY"),
                MakeButton("Список", "OMNI-LOG", "Открыть ревизию"),
                MakeButton("Сравнить", "OMNI-DIFF", "Наложить ревизию"),
                MakeButton("Снять", "OMNI-CLEAR", "Удалить наложение"),
                MakeButton("Показ/скрыть", "OMNI-TOGGLE", "Слои наложения"),
                MakeButton("Заметка", "OMNI-NOTE", "Круг-комментарий")));

            _added = true;
        }

        private static RibbonPanel MakePanel(string title, params RibbonButton[] buttons)
        {
            var src = new RibbonPanelSource { Title = title };
            foreach (var b in buttons) src.Items.Add(b);
            return new RibbonPanel { Source = src };
        }

        private static RibbonButton MakeButton(string text, string command, string tooltip)
        {
            return new RibbonButton
            {
                Text = text,
                Name = command,
                ToolTip = tooltip,
                ShowText = true,
                Size = RibbonItemSize.Large,
                CommandHandler = new RelayCommand(() => RunCommand(command))
            };
        }

        private static void RunCommand(string cmd)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            doc.SendStringToExecute(cmd + " ", true, false, false);
        }

        public static void Remove()
        {
            if (_tab == null || !_added) return;
            var rc = ComponentManager.Ribbon;
            if (rc == null) return;
            try { rc.Tabs.Remove(_tab); } catch { }
            _tab = null;
            _added = false;
        }
    }
}
