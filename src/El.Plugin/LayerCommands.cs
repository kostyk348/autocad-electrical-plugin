using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using El.Core;

namespace El.Plugin
{
    /// <summary>
    /// Управление слоями: фильтр анализа, создание, покраска, перенос,
    /// статистика, кластеризация цепей по слоям.
    /// </summary>
    public static class LayerCommands
    {
        private static Editor Ed => DwgAccess.Ed;

        // ============================================================
        // EL-LAYER-FILTER — какие слои участвуют в анализе топологии
        // ============================================================
        [CommandMethod("EL-LAYER-FILTER")]
        public static void ElLayerFilter()
        {
            try
            {
                string cur = CommandState.LayerFilter.Count == 0
                    ? "* (все)"
                    : string.Join(", ", CommandState.LayerFilter);
                Ed.WriteMessage($"\n=== EL-LAYER-FILTER: текущий фильтр: {cur} ===");
                var s = Ed.GetString(new PromptStringOptions(
                    "\nСлои для анализа (через запятую, * — все): ") { AllowSpaces = true }).StringResult;
                if (s == null) return;
                s = s.Trim();
                if (s == "" || s == "*")
                    CommandState.LayerFilter = new List<string>();
                else
                    CommandState.LayerFilter = new List<string>(
                        s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.Trim()));
                CommandState.SaveLayerFilter();
                Ed.WriteMessage($"\n; Фильтр: {(CommandState.LayerFilter.Count == 0 ? "* (все)" : string.Join(", ", CommandState.LayerFilter))}");
                Ed.WriteMessage("\n; Применяется к EL-TRACE / EL-CHECK / EL-TABLE / EL-PATH / EL-CROSSING и др.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-LAYER-FILTER: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-LAYER-NEW — создать слой
        // ============================================================
        [CommandMethod("EL-LAYER-NEW")]
        public static void ElLayerNew()
        {
            try
            {
                var n = Ed.GetString(new PromptStringOptions("\nИмя слоя: ") { AllowSpaces = true }).StringResult;
                if (string.IsNullOrEmpty(n)) return;
                var c = Ed.GetInteger(new PromptIntegerOptions("\nЦвет (ACI 1-255, Enter — 7): ") { AllowNegative = false });
                short color = (short)(c.Status == PromptStatus.OK ? c.Value : 7);
                color = Math.Max((short)1, Math.Min((short)255, color));
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    DwgAccess.EnsureLayer(tr, n, color);
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Слой \"{n}\" создан (цвет {color}).");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-LAYER-NEW: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-LAYER-COLOR — покрасить слой
        // ============================================================
        [CommandMethod("EL-LAYER-COLOR")]
        public static void ElLayerColor()
        {
            try
            {
                var names = ListLayers(out var ids);
                if (names.Count == 0) { Ed.WriteMessage("\n! Слоёв нет."); return; }
                for (int i = 0; i < names.Count; i++)
                    Ed.WriteMessage($"\n[{i + 1}] {names[i]}");
                var pi = Ed.GetInteger(new PromptIntegerOptions($"\nНомер слоя (1-{names.Count}, 0 — отмена): ") { AllowNegative = false });
                if (pi.Status != PromptStatus.OK || pi.Value <= 0 || pi.Value > names.Count) return;
                string target = names[pi.Value - 1];
                var ci = Ed.GetInteger(new PromptIntegerOptions("\nЦвет (ACI 1-255): ") { AllowNegative = false });
                if (ci.Status != PromptStatus.OK) return;
                short color = (short)Math.Max(1, Math.Min(255, ci.Value));
                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(DwgAccess.Doc.Database.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(target))
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lt[target], OpenMode.ForWrite);
                        ltr.Color = Color.FromColorIndex(ColorMethod.ByAci, color);
                    }
                    tr.Commit();
                }
                Ed.WriteMessage($"\n; Слой \"{target}\" покрашен в {color}.");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-LAYER-COLOR: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-LAYER-MOVE — перенести объекты на слой
        // ============================================================
        [CommandMethod("EL-LAYER-MOVE")]
        public static void ElLayerMove()
        {
            try
            {
                Ed.WriteMessage("\n=== EL-LAYER-MOVE: выбери объекты для переноса ===");
                var sr = Ed.GetSelection(new PromptSelectionOptions());
                if (sr.Status != PromptStatus.OK) return;

                string target = null;
                var names = ListLayers(out _);
                Ed.WriteMessage("\nСуществующие слои:");
                for (int i = 0; i < names.Count; i++)
                    Ed.WriteMessage($"\n[{i + 1}] {names[i]}");
                var n = Ed.GetString(new PromptStringOptions("\nИмя слоя (или номер из списка, Enter — отмена): ") { AllowSpaces = true }).StringResult;
                if (string.IsNullOrEmpty(n)) return;
                n = n.Trim();
                if (int.TryParse(n, out int idx) && idx >= 1 && idx <= names.Count)
                    target = names[idx - 1];
                else
                    target = n;

                using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
                {
                    // слой существует? если нет — создаём (цвет 7)
                    var lt = (LayerTable)tr.GetObject(DwgAccess.Doc.Database.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(target))
                    {
                        DwgAccess.EnsureLayer(tr, target, 7);
                    }
                    int moved = 0;
                    foreach (var id in sr.Value.GetObjectIds())
                    {
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite, true);
                        if (ent == null || ent.IsErased) continue;
                        ent.Layer = target;
                        moved++;
                    }
                    tr.Commit();
                    Ed.WriteMessage($"\n; Перенесено объектов: {moved} на слой \"{target}\".");
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-LAYER-MOVE: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-LAYER-STATS — статистика по слоям
        // ============================================================
        [CommandMethod("EL-LAYER-STATS")]
        public static void ElLayerStats()
        {
            try
            {
                var doc = DwgAccess.Doc;
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var linesPerLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                        string layer = ent.Layer ?? "";
                        counts.TryGetValue(layer, out int c);
                        counts[layer] = c + 1;
                        if (ent is Line) { linesPerLayer.TryGetValue(layer, out int lc); linesPerLayer[layer] = lc + 1; }
                    }
                    tr.Commit();
                }
                Ed.WriteMessage("\n=== EL-LAYER-STATS ===");
                Ed.WriteMessage($"\n{"Слой",-24} {"Объектов",8} {"LINE",6}");
                foreach (var kv in counts.OrderBy(kv => kv.Key))
                {
                    linesPerLayer.TryGetValue(kv.Key, out int lc);
                    Ed.WriteMessage($"\n{kv.Key,-24} {kv.Value,8} {lc,6}");
                }
                // пустые слои
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
                    var empty = new List<string>();
                    foreach (ObjectId lid in lt)
                    {
                        var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        if (!counts.ContainsKey(ltr.Name)) empty.Add(ltr.Name);
                    }
                    tr.Commit();
                    if (empty.Count > 0)
                        Ed.WriteMessage($"\n\nПустые слои ({empty.Count}): {string.Join(", ", empty.Take(15))}");
                }
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-LAYER-STATS: " + ex.Message); Plugin.Log(ex); }
        }

        // ============================================================
        // EL-LAYER-CHAINS — кластеризация: цепи в разрезе слоёв
        // ============================================================
        [CommandMethod("EL-LAYER-CHAINS")]
        public static void ElLayerChains()
        {
            try
            {
                CommandState.Refresh();
                var clusters = LayerClusters.Cluster(CommandState.Lines);
                if (clusters.Count == 0) { Ed.WriteMessage("\n! Линий нет."); return; }

                Ed.WriteMessage("\n=== EL-LAYER-CHAINS: цепи по слоям ===");
                int totalChains = 0;
                foreach (var kv in clusters.OrderBy(kv => kv.Key))
                {
                    if (kv.Value.Count < 2) continue;
                    var g = GraphBuilder.Build(kv.Value, DwgAccess.DefaultTolerance);
                    var chains = g.AllChains();
                    int textless = 0;
                    foreach (var ch in chains)
                        if (ChainTexts.NearEnds(g, ch, CommandState.Texts, DwgAccess.DefaultTextRadius).Count == 0) textless++;
                    totalChains += chains.Count;
                    Ed.WriteMessage($"\nСлой \"{kv.Key}\": {kv.Value.Count} линий, {chains.Count} цепей, без подписей: {textless}");
                }
                Ed.WriteMessage($"\nИтого цепей (по слоям): {totalChains}");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n! EL-LAYER-CHAINS: " + ex.Message); Plugin.Log(ex); }
        }

        // ---------- помощники ----------

        private static List<string> ListLayers(out Dictionary<string, ObjectId> ids)
        {
            ids = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            using (var tr = DwgAccess.Doc.Database.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(DwgAccess.Doc.Database.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    ids[ltr.Name] = lid;
                    names.Add(ltr.Name);
                }
                tr.Commit();
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
    }
}
