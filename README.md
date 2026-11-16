<div align="center">

<img src="docs/banner.svg" alt="autocad-electrical-plugin banner" width="1000">

# Electrical Tools — плагин AutoCAD

### .NET-плагин для AutoCAD 2024 и 2014: топология электрических схем, аудит, спецификации проводов, BOM, сравнение ревизий — с лентой, контекстными меню и…

[![Release](https://img.shields.io/github/v/release/kostyk348/autocad-electrical-plugin?sort=semver&color=blue)](https://github.com/kostyk348/autocad-electrical-plugin/releases)
[![Last commit](https://img.shields.io/github/last-commit/kostyk348/autocad-electrical-plugin?branch=master)](https://github.com/kostyk348/autocad-electrical-plugin/commits/master)

</div>

---


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
| **Аудит** | `EL-CHECK`, `EL-CROSSING` (X-пересечения), `EL-LOOPS`, `EL-BOTTLENECK`, `EL-STATS`, `EL-COLOR-CHAINS`, `EL-HOTSPOTS` (тепловая карта плотности линий), `EL-CROSSREF` (ссылки «см. лист N») |
| **Спецификации** | `AW33` (провода, длина × кол-во), `AW33-HTML`, `AW33-CSV`, `DrawWire`, `WireTable`, `WireNodes`, `WireSegAddr`, `WT` |
| **Автоматизация** | `EL-JOIN` (LINE → полилинии), `EL-BOM`, `EL-TITLE` (штамп), `EL-SHEET-LIST` (реестр листов), `EL-XREF-LIST`, `EL-AUTOTAG` (номера цепей) |
| **OMNI (версии)** | `OMNI-SNAP`, `OMNI-HISTORY`, `OMNI-ARCHIVE`, `OMNI-RESTORE`, `OMNI-LOG`, `OMNI-DIFF`, `OMNI-CLEAR`, `OMNI-TOGGLE`, `OMNI-NOTE` |

Интерфейс: вкладка ленты **«Электроавтоматика»** (6 панелей), контекстные меню по типу объекта (LINE/TEXT/пусто), палитра `EL-PALETTE` (дефекты с зумом).

## OMNI — история ревизий чертежа

Слепки DWG лежат рядом с файлом, в `_OMNI_HISTORY\`, и записаны в цепочку с контрольными суммами:

```
_OMNI_HISTORY\
├── history.jsonl                 манифест: файл, время, автор, описание, размер, sha256, prev→next
├── 20251116.143012.512_user_добавил кабель_щит.dwg
└── archive\2025-11.zip           старые слепки, сжатые ZIP
```

| Команда | Что делает |
|---|---|
| `OMNI-SNAP` | слепок + запись в манифест (sha256 файла и цепочка `prev→next`); при переполнении — авто-сжатие |
| `OMNI-HISTORY` | список ревизий и проверка целостности цепочки: если манифест правили руками, покажет запись, на которой цепочка рвётся |
| `OMNI-ARCHIVE` | спросит, сколько последних слепков оставить распакованными, остальные упакует в `archive\ГГГГ-ММ.zip` и удалит оригиналы (после проверки, что запись в архиве есть) |
| `OMNI-RESTORE` | откат к выбранной ревизии; сначала делает страховочный слепок текущего состояния (`before-restore`), поэтому откат всегда обратим; ревизию достаёт и из ZIP |
| `OMNI-LOG` / `OMNI-DIFF` / `OMNI-CLEAR` / `OMNI-TOGGLE` | список слепков, наложение ревизии XREF'ом с подсветкой, снятие наложения, переключение его слоёв |
| `OMNI-NOTE` | заметка на чертеже (окружность + текст на слое `OMNI_NOTES`) |

Почему так: полный слепок DWG — единственный формат, который открывается в любой версии AutoCAD и не зависит от плагина. Манифест с цепочкой даёт «кто, когда, что и не подменили ли»; ZIP — чтобы история не занимала диск (DWG сжимается в разы).

## От LISP к плагину: покрытие

В [el-tools](https://github.com/kostyk348/el-tools) и [omni](https://github.com/kostyk348/omni) было **26 команд** AutoLISP. В плагине все 26 имеют C#-реализацию:

| LISP | В плагине |
|---|---|
| `AW`, `AW33`, `DrawWire`, `WireNodes`, `WireTable`, `WireSegAddr`, `WT` | `AW33`, `AW33-CSV`, `AW33-HTML`, `DrawWire`, `WireNodes`, `WireTable`, `WireSegAddr`, `WT` (+ отчёты) |
| `EL-TRACE`, `EL-GRAPH`, `EL-TABLE`, `EL-WHATIF`, `EL-PATH` | одноимённые + `EL-GRAPH-EXPORT` (Graphviz) |
| `EL-CHECK`, `EL-LOOPS`, `EL-BOTTLENECK`, `EL-STATS`, `EL-COLOR-CHAINS` | одноимённые + `EL-CHECK-REPORT`, `EL-CROSSING` |
| `EL-HOTSPOTS`, `EL-CROSSREF` | перенесены в 1.9.0 (ядро `Hotspots`, `CrossReferences`) |
| `EL-DIFF`, `EL-SAVE-SNAPSHOT` | заменены полноценной историей OMNI: `OMNI-SNAP`/`OMNI-HISTORY`/`OMNI-RESTORE`/`OMNI-DIFF`/`EL-REVISION-DIFF` |
| `OMNI-SNAP/LOG/DIFF/CLEAR/TOGGLE/NOTE` | одноимённые + `OMNI-HISTORY`, `OMNI-ARCHIVE`, `OMNI-RESTORE` |

Сверх LISP добавлено 26 команд: HTML/md-отчёты по чертежу и проекту, BOM, штамп, реестр листов, XREF-список, автонумерация цепей, слои (фильтры, легенда, перенос), палитра дефектов, лента, установщик.

## Надёжность: ничего не зависает

У каждой потенциально тяжёлой операции есть верхняя граница — это проверяется тестами, а не обещаниями:

| Место | Граница |
|---|---|
| `MazeRouter.Route` (BFS по сетке) | ≤ 4 000 000 ячеек (`MaxGridCells`); при большем разбросе точек шаг сетки автоматически укрупняется, `RouteResult.GridCoarsened` / `CellUsed` / `GridCells` возвращают фактические значения |
| блокировка препятствий | ≤ 4096 проб на отрезок (`MaxSamplesPerObstacle`) |
| `MazeRouter.Simplify` (string pulling) | ≤ 2 000 000 проверок «отрезок свободен» (`MaxSimplifyTests`), затем оставшиеся точки идут без сглаживания |
| `PolylineBuilder` (обход цепочек) | каждое ребро помечается использованным на входе в цикл → итераций не больше числа рёбер |
| `Revisions.ReadManifest` | ≤ 100 000 записей, чтение потоком |
| `Revisions.PlanRetention` / `OMNI-ARCHIVE` | ≤ 200 файлов за проход (`MaxArchiveBatch`), ≤ 5000 при сканировании папки |
| `Revisions.Archive` | удаляет оригинал только после записи в ZIP и сверки длины |
| `OMNI-SNAP` / `OMNI-RESTORE` | манифест пишется последним; откат не начинается, если страховочный слепок не создан |

Тесты `MazeRouter_HugeSpanIsBounded`, `MazeRouter_ManyObstaclesIsBounded`, `Simplify_LongPathIsBounded`, `Retention_PlanIsFastOnLargeHistory` держат эти потолки: пропадёт граница — упадут тесты, а не AutoCAD.

## Сборка и тесты

```
dotnet build src/El.Core/El.Core.csproj -c Release               # ядро (netstandard2.0 + net45)
dotnet build src/El.Plugin/El.Plugin.csproj -c Release           # 2024 (net48)
dotnet build src/El.Plugin.2014/El.Plugin.2014.csproj -c Release -p:Acad2014Path="C:/refs/2014"   # 2014 (net45)
dotnet run --project tests/El.Core.Tests                         # 72 теста
```

Плагин собирается двумя способами: если AutoCAD установлен — против его DLL (`AcadPath`) с ILRepack в одну сборку; если нет (CI, новый компьютер) — против NuGet-пакета `AutoCAD.NET 24.3.0`, без установленного AutoCAD. Переключается автоматически, вручную — `-p:AcadLocal=true|false`.

Ядро (`El.Core`) — без зависимостей от AutoCAD: граф (SpatialGrid, O(n)), BFS, лабиринтный роутер, парсер AW33, пересечения, полилинии, diff, история ревизий (именование, хеш-цепочка, ZIP), HTML-отчёты.

## Лицензия

Внутренний инструмент. Использование — по согласованию.
