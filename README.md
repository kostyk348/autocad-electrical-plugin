# AutoCAD Electrical Plugin (.NET)

C#-плагин для AutoCAD 2024: топология электрических схем, аудит, спецификации
проводов, версии чертежей — с полным UI (Ribbon, контекстные меню, палитра).

Заменяет и расширяет AutoLISP-инструменты из
[el-tools](https://github.com/kostyk348/el-tools) и
[omni](https://github.com/kostyk348/omni): тот же функционал, но
**в 10–100 раз быстрее** (пространственный grid, HashSet вместо alist)
и с графическим интерфейсом.

## Установка

### Способ 1 — прямо из AutoCAD (рекомендуется)

1. Скачайте релиз `El.Plugin.2024.installer.zip`, распакуйте в любую папку
   (нужны `El.Plugin.dll` + `El.Core.dll` рядом).
2. В AutoCAD 2024: `NETLOAD` → выберите `El.Plugin.dll`.
3. Плагин спросит «Установить для автозагрузки?» — нажмите Enter,
   либо выполните команду `EL-INSTALL`.
4. Плагин сам скопирует себя в
   `%APPDATA%\Autodesk\ApplicationPlugins\El.Plugin.2024.bundle\`
   и создаст `PackageContents.xml`.
5. Перезапустите AutoCAD — автозагрузка.

### Способ 2 — bundle вручную

1. Скачайте `El.Plugin.2024.bundle.zip`, распакуйте — папка `El.Plugin.2024.bundle/`.
2. Скопируйте её в `%APPDATA%\Autodesk\ApplicationPlugins\`.
3. Запустите AutoCAD 2024 — плагин загрузится сам.

### Способ 3 — вручную (NETLOAD)

`NETLOAD` → `El.Plugin.dll` (нужен `El.Core.dll` рядом). Автозагрузки нет,
но всё работает в текущей сессии.

### AutoCAD 2014 — готовые сборки

Плагин **собран против официального API AutoCAD 2014** (R19.1, net45;
DLL API извлечены из вашего ISO `Autodesk.AutoCAD.2014.SP1.ru-en.x86-x64.iso`).
В релизе: `El.Plugin.2014.installer.zip` и `El.Plugin.2014.bundle.zip` —
установка так же через NETLOAD → Enter (EL-INSTALL) → перезапуск.

Поддержка 2014 в коде:
- единый исходник, `El.Plugin.2014` (net45) vs `El.Plugin` (net48);
- `#if NET45` в Installer: имя bundle `El.Plugin.2014.bundle`, `SeriesMin/Max=R19.1`;
- `El.Core` мультитаргет: `netstandard2.0` (2024) + `net45` (2014);
- зум через `ViewTableRecord` (в 2014 нет `Editor.Command`);
- без кортежей C# 7 (net45 не имеет ValueTuple).

Пересборка 2014 без установленного AutoCAD:
```
.\scripts\extract-2014.ps1 -Iso "C:\...\Autodesk.AutoCAD.2014.SP1.ru-en.x86-x64.iso"
dotnet build src/El.Plugin.2014/El.Plugin.2014.csproj -c Release -p:Acad2014Path="...\refs\2014"
```

LISP-инструменты для 2014 — CP1251-версии в релизах el-tools/omni.

## UI

| Элемент | Что даёт |
|---|---|
| **Ribbon «Электроавтоматика»** | 4 панели: «Цепи и Граф», «Аудит и Дефекты», «Спецификации», «OMNI (версии)» |
| **Контекстное меню** (ПКМ) | по **LINE**: трассировать/разрыв/адреса/выноска; по **TEXT**: спецификация AW33; по пустому месту: дефектоскоп, таблица, OMNI |
| **Палитра** (`EL-PALETTE`) | список цепей после EL-CHECK, двойной клик — зум к дефекту |
| `AW33-CSV` | экспорт спецификации в CSV (для Excel и web-генератора wire-table) |

## Команды

### Топология
| Команда | Описание |
|---|---|
| `EL-TRACE` | клик по LINE → вся цепь, подсветка, тексты, длина |
| `EL-WHATIF` | симуляция разрыва: на какие части распадётся цепь |
| `EL-TABLE` | таблица соединений «Откуда → Куда» (AutoCAD Table) |
| `EL-GRAPH` | информация о графе (число линий/цепей) |

### Аудит
| Команда | Описание |
|---|---|
| `EL-CHECK` | дефектоскоп: изолированные, near-miss разрывы, дубликаты текста, цепи без подписей |
| `EL-LOOPS` | поиск колец (цепь без терминалов) |
| `EL-BOTTLENECK` | топ-10 линий через несколько цепей |
| `EL-STATS` | статистика чертежа |
| `EL-COLOR-CHAINS` | раскраска цепей в разные цвета |

### Спецификации и провода
| Команда | Описание |
|---|---|
| `AW33` | постраничная спецификация: цвет/сечение/**кол-во**/длина; **длина × кол-во**; таблицы + сводная |
| `AW33-CSV` | та же спецификация в CSV |
| `DrawWire` | провод: полилиния + XData + номер в кружке |
| `WireTable` | таблица проводов из XData |
| `WireNodes` | узлы-точки по вершинам проводов |
| `WireSegAddr` | адреса точек разбивки вдоль линии |
| `WT` | выноска «стрелка + номер» с двух концов |

### OMNI (версии чертежа)
| Команда | Описание |
|---|---|
| `OMNI-SNAP` | слепок DWG в `_OMNI_HISTORY` |
| `OMNI-LOG` | список слепков, открытие |
| `OMNI-DIFF` | наложение ревизии (XREF) + обесцвечивание |
| `OMNI-CLEAR` | снятие только своих XREF |
| `OMNI-TOGGLE` | показ/скрытие слоёв наложения |
| `OMNI-NOTE` | круглая заметка |

## Сборка

```
dotnet build src/El.Plugin/El.Plugin.csproj -c Release
# результат: bin/plugin/El.Plugin.dll + El.Core.dll
```

Требования: .NET SDK 6+ (пакет `Microsoft.NETFramework.ReferenceAssemblies`
подтянется из nuget), AutoCAD 2024 (путь к DLL можно переопределить:
`dotnet build -p:AcadPath="C:\Program Files\Autodesk\AutoCAD 2024"`).

## Архитектура

```
src/El.Core/    netstandard2.0, БЕЗ зависимостей от AutoCAD
                - GraphBuilder: граф смежности через SpatialGrid (O(n) вместо O(n²))
                - WireGraph: BFS, all-chains, терминалы, remove-edge
                - Aw33Parser: спецификация с qty («N шт», «Nx<сечение>»), м/см
                - TopologyDiff/SnapshotSerializer: дифф версий
src/El.Plugin/  net48 + AutoCAD 2024 API
                - Commands.cs / WireCommands.cs / OmniCommands.cs
                - Ui/: Ribbon, ContextMenu, Palette
tests/          самописный раннер (15 тестов: граф, парсер AW33, дифф)
```

## Тесты

```
dotnet run --project tests/El.Core.Tests
# === 15 passed, 0 failed ===
```

## Дорожная карта

- [ ] EL-CROSSREF-ALL: полистовой анализ папки DWG (открытие через Database)
- [ ] экспорт спецификации в HTML (web-генератор wire-table)
- [ ] автозагрузка плагина через PackageContents.xml (Bundle)
- [ ] поддержка AutoCAD 2014 (отдельная сборка с другим API)
