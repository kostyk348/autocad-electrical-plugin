# Electrical Tools — плагин AutoCAD

.NET-плагин для AutoCAD **2024** и **2014**: топология электрических схем,
аудит, спецификации проводов, BOM, сравнение ревизий — с лентой,
контекстными меню и палитрой.

> LISP-версии инструментов: [el-tools](https://github.com/kostyk348/el-tools) · [omni](https://github.com/kostyk348/omni)

## Установка

**Скачайте релиз** → распакуйте `El.Plugin.<версия>.installer.zip` → в AutoCAD: `NETLOAD` → выберите `El.Plugin.dll` → **Enter** на вопрос об автозагрузке → перезапуск AutoCAD. Плагин сам скопирует себя в `%APPDATA%\Autodesk\ApplicationPlugins\` (bundle).

Альтернатива: `El.Plugin.<версия>.bundle.zip` → распакуйте папку в `%APPDATA%\Autodesk\ApplicationPlugins\`.

Версии: `El.Plugin.2024.*` (AutoCAD 2024), `El.Plugin.2014.*` (AutoCAD 2014, собрано против API из ISO).

## Команды

| Группа | Команды |
|---|---|
| **Отчёты** | `EL-REPORT` (чертёж → HTML), `EL-PROJECT-REPORT` (папка DWG → сводный HTML), `EL-REVISION-DIFF` (текущий vs OMNI-ревизия), `EL-CHECK-REPORT` (дефектоскоп → md/html) |
| **Топология** | `EL-TRACE`, `EL-PATH`, `EL-WHATIF`, `EL-TABLE`, `EL-GRAPH`, `EL-GRAPH-EXPORT` (Graphviz DOT/PNG) |
| **Аудит** | `EL-CHECK`, `EL-CROSSING` (X-пересечения), `EL-LOOPS`, `EL-BOTTLENECK`, `EL-STATS`, `EL-COLOR-CHAINS` |
| **Спецификации** | `AW33` (провода, длина × кол-во), `AW33-HTML`, `AW33-CSV`, `DrawWire`, `WireTable`, `WireNodes`, `WireSegAddr`, `WT` |
| **Автоматизация** | `EL-JOIN` (LINE → полилинии), `EL-BOM`, `EL-TITLE` (штамп), `EL-SHEET-LIST` (реестр листов), `EL-XREF-LIST`, `EL-AUTOTAG` (номера цепей) |
| **OMNI (версии)** | `OMNI-SNAP`, `OMNI-LOG`, `OMNI-DIFF`, `OMNI-CLEAR`, `OMNI-TOGGLE`, `OMNI-NOTE` |

Интерфейс: вкладка ленты **«Электроавтоматика»** (6 панелей), контекстные меню по типу объекта (LINE/TEXT/пусто), палитра `EL-PALETTE` (дефекты с зумом).

## Сборка и тесты

```
dotnet build src/El.Plugin/El.Plugin.csproj -c Release          # 2024 (net48)
dotnet build src/El.Plugin.2014/El.Plugin.2014.csproj -c Release -p:Acad2014Path="...\refs\2014"   # 2014 (net45)
dotnet run --project tests/El.Core.Tests                         # 33 теста
```

Ядро (`El.Core`) — без зависимостей от AutoCAD: граф (SpatialGrid, O(n)), BFS, парсер AW33, пересечения, полилинии, diff, HTML-отчёты.

## Лицензия

Внутренний инструмент. Использование — по согласованию.
