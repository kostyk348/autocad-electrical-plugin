# Electrical Tools — AutoCAD plugin

.NET plugin for AutoCAD **2024** and **2014**: electrical schematic topology,
audit, wire specifications, BOM, revision comparison — with Ribbon,
context menus and a palette.

> LISP versions: [el-tools](https://github.com/kostyk348/el-tools) · [omni](https://github.com/kostyk348/omni)

## Install

**Download a release** → unzip `El.Plugin.<version>.installer.zip` → in AutoCAD run `NETLOAD` → pick `El.Plugin.dll` → press **Enter** on the auto-load question → restart AutoCAD. The plugin copies itself into `%APPDATA%\Autodesk\ApplicationPlugins\` (bundle).

Alternative: unzip `El.Plugin.<version>.bundle.zip` into `%APPDATA%\Autodesk\ApplicationPlugins\`.

Builds: `El.Plugin.2024.*` (AutoCAD 2024), `El.Plugin.2014.*` (AutoCAD 2014, compiled against the API from the ISO).

## Commands

| Group | Commands |
|---|---|
| **Reports** | `EL-REPORT` (drawing → HTML), `EL-PROJECT-REPORT` (folder of DWGs → summary HTML), `EL-REVISION-DIFF` (current vs OMNI snapshot), `EL-CHECK-REPORT` (defect scan → md/html) |
| **Topology** | `EL-TRACE`, `EL-PATH`, `EL-WHATIF`, `EL-TABLE`, `EL-GRAPH`, `EL-GRAPH-EXPORT` (Graphviz DOT/PNG) |
| **Audit** | `EL-CHECK`, `EL-CROSSING` (X-crossings), `EL-LOOPS`, `EL-BOTTLENECK`, `EL-STATS`, `EL-COLOR-CHAINS` |
| **Specifications** | `AW33` (wires, length × qty), `AW33-HTML`, `AW33-CSV`, `DrawWire`, `WireTable`, `WireNodes`, `WireSegAddr`, `WT` |
| **Automation** | `EL-JOIN` (LINE → polylines), `EL-BOM`, `EL-TITLE` (title block), `EL-SHEET-LIST` (sheet registry), `EL-XREF-LIST`, `EL-AUTOTAG` (chain numbers) |
| **OMNI (revisions)** | `OMNI-SNAP`, `OMNI-LOG`, `OMNI-DIFF`, `OMNI-CLEAR`, `OMNI-TOGGLE`, `OMNI-NOTE` |

UI: Ribbon tab **«Электроавтоматика»** (6 panels), object-aware context menus (LINE/TEXT/empty), palette `EL-PALETTE` (defects with zoom).

## Build & test

```
dotnet build src/El.Plugin/El.Plugin.csproj -c Release                                   # 2024 (net48)
dotnet build src/El.Plugin.2014/El.Plugin.2014.csproj -c Release -p:Acad2014Path="...\refs\2014"   # 2014 (net45)
dotnet run --project tests/El.Core.Tests                                                  # 33 tests
```

Core (`El.Core`) has no AutoCAD dependency: graph (SpatialGrid, O(n)), BFS, AW33 parser, crossings, polyline builder, diff, HTML reports.

## License

Internal tool. Use by agreement.
