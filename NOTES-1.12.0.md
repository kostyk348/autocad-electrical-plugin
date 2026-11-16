# v1.12.0 — сборка для AutoCAD 2027 (.NET 10) + авто-слепки

Две вещи: плагин теперь собирается под три линейки AutoCAD из одних исходников, и история
ревизий ведётся сама при сохранении.

## Поддержка версий AutoCAD

| Версия AutoCAD | Серия | Платформа | Пакет в этом релизе |
|---|---|---|---|
| 2021–2024 | R24.0–R24.3 | .NET Framework 4.8 | `El.Plugin.2024.bundle.zip`, `El.Plugin.2024.installer.zip` |
| 2027 | R26.0 | .NET 10 | `El.Plugin.2027.bundle.zip`, `El.Plugin.2027.installer.zip` |
| 2025–2026 | R25.0–R25.1 | .NET 8 | **нет пакета**: managed-API `AutoCAD.NET 25.0.0` зависит от `AutoCAD.NET.Core 25.0.0-V058`, которого нет на nuget.org — собирается на машине с AutoCAD 2025+ (проект `src/El.Plugin.2025`) |

Соответствие серий взято из документации Autodesk (2021–2024 = R24.x, 2025–2026 = R25.x, 2027 = R26.0).

## Новое: `OMNI-AUTOSNAP`

Авто-слепки при каждом `QSAVE`/`SAVEAS`:

| Параметр | Значение |
|---|---|
| Минимальный интервал | 60 с |
| Потолок | 20 слепков в час |
| Состояние | `_OMNI_HISTORY/autosnap.state` (по умолчанию включено) |
| Дополнительно | защита от переведённых назад часов; ошибки внутри обработчика пишутся в лог, сохранение из-за плагина не падает |

Решение принимает чистая функция `AutoSnapPolicy` (покрыта тестами), состояние — три маленьких
файла: на каждое сохранение O(1), без чтения всей истории.

## Всё, что уже есть

- **История ревизий:** `OMNI-SNAP` · `OMNI-AUTOSNAP` · `OMNI-HISTORY` · `OMNI-ARCHIVE` ·
  `OMNI-RESTORE` · `OMNI-LOG` · `OMNI-DIFF` · `OMNI-CLEAR` · `OMNI-TOGGLE` · `OMNI-NOTE`.
  Манифест `history.jsonl` с hash-chain, старые слепки пакуются в ZIP, откат — с обязательным
  страховочным слепком.
- **Инженерия схем:** топология (`EL-TRACE`, `EL-PATH`, `EL-WHATIF`), аудит (`EL-CHECK`,
  `EL-LOOPS`, `EL-CROSSING`, `EL-BOTTLENECK`, `EL-STATS`), спецификации (`AW33`, `AW33-CSV`,
  `AW33-HTML`), провода (`DrawWire`, `WireTable`), тепловая карта (`EL-HOTSPOTS`),
  ссылки на листы (`EL-CROSSREF`), отчёты HTML/md, BOM, штамп, реестр листов.
- **Границы вместо зависаний:** бюджет 4 млн ячеек в лабиринтном роутере, 4096 проб на препятствие,
  2 млн проверок при сглаживании, 200 файлов за проход архивации — всё закреплено тестами.

## Установка

```
AutoCAD 2024:  El.Plugin.2024.installer.zip  -> NETLOAD -> El.Plugin.dll
AutoCAD 2027:  El.Plugin.2027.installer.zip  -> NETLOAD -> El.Plugin.dll
Или: распаковать *.bundle.zip в %APPDATA%\Autodesk\ApplicationPlugins\
```

Проверка загрузки:

```
sha256sum -c SHA256SUMS.txt
Get-FileHash -Algorithm SHA256 El.Plugin.2027.installer.zip     # Windows
```

## Проверено

- 79 тестов ядра, 0 падений;
- локальная сборка 2024 против установленного AutoCAD 2024 (одна сборка `El.Plugin.dll`, 223 744 б);
- CI-прогон `43862551933` (коммит `611bc53`): ядро+тесты, сборка 2024 против
  `AutoCAD.NET 24.3.0`, сборка 2027 против `AutoCAD.NET 26.0.0` на .NET 10 — все три зелёные;
- содержимое собранных DLL проверяется в CI: все команды (`OMNI-SNAP`, `OMNI-AUTOSNAP`,
  `OMNI-HISTORY`, `OMNI-RESTORE`, `EL-HOTSPOTS`, `EL-CROSSREF`, `AW33`) обязаны присутствовать.
