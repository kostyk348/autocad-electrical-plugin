# AutoCAD Electrical Plugin (.NET)

C#-плагин для AutoCAD 2024: топология электрических схем, аудит, спецификации
проводов, версии чертежей — с полным UI (Ribbon, контекстные меню, палитра).

Заменяет и расширяет AutoLISP-инструменты из
[el-tools](https://github.com/kostyk348/el-tools) и
[omni](https://github.com/kostyk348/omni): тот же функционал, но
**в 10–100 раз быстрее** (пространственный grid, HashSet вместо alist)
и с графическим интерфейсом.

## Установка

### Способ 1 — автозагрузка (bundle, рекомендуется)

1. Скачайте релиз `El.Plugin.2024.bundle.zip` со страницы Releases.
2. Распакуйте — получится папка `El.Plugin.2024.bundle/`.
3. Скопируйте её в `%APPDATA%\Autodesk\ApplicationPlugins\`.
4. Запустите AutoCAD 2024 — плагин загрузится сам (вкладка
   «Электроавтоматика», контекстные меню, палитра).

### Способ 2 — вручную (NETLOAD)

1. Соберите Release (см. «Сборка») или возьмите `bin/plugin/`
   из релиза (`El.Plugin.dll` + `El.Core.dll` — обе обязательны).
2. В AutoCAD 2024: команда `NETLOAD` → выберите `El.Plugin.dll`.

### AutoCAD 2014

LISP-инструменты (el-tools/omni) полностью работают в 2014 — берите
CP1251-версии из их релизов. **C#-плагин под 2014** собирается из
исходников после установки AutoCAD 2014 (managed API DLL берутся из его
папки, отдельно не распространяются):

```
dotnet build src/El.Plugin.2014/El.Plugin.2014.csproj -c Release ^
    -p:Acad2014Path="C:\Program Files\Autodesk\AutoCAD 2014"
```

Результат — `bin/plugin-2014/El.Plugin.2014.dll` (net45, единый код с 2024).

> Требуется El.Core.dll рядом с El.Plugin.dll (копируется в bin/plugin).

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
