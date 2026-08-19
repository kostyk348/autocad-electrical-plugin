using System;
using System.Collections.Generic;
using System.Linq;

namespace El.Core
{
    /// <summary>Сравнение спецификаций (AW33) и BOM между версиями.</summary>
    public static class SpecDiff
    {
        public sealed class WireChange
        {
            public string Color;
            public string Size;
            public int QtyOld, QtyNew;
            public double LenCmOld, LenCmNew;
            /// <summary>added | removed | changed | unchanged</summary>
            public string Kind;
        }

        public sealed class BomChange
        {
            public string Block;
            public int CountOld, CountNew;
            public string Kind; // added | removed | changed
        }

        public sealed class DiffResult
        {
            public List<WireChange> Wires = new List<WireChange>();
            public List<BomChange> Bom = new List<BomChange>();
            public List<string> TopologyAdded = new List<string>();
            public List<string> TopologyRemoved = new List<string>();
            public int WiresAdded => Wires.Count(w => w.Kind == "added");
            public int WiresRemoved => Wires.Count(w => w.Kind == "removed");
            public int WiresChanged => Wires.Count(w => w.Kind == "changed");
            public int BomAdded => Bom.Count(b => b.Kind == "added");
            public int BomRemoved => Bom.Count(b => b.Kind == "removed");
            public int BomChanged => Bom.Count(b => b.Kind == "changed");
        }

        /// <summary>Сравнение спецификаций проводов по ключу (цвет+сечение+кол-во).</summary>
        public static List<WireChange> CompareWires(Aw33PageResult oldSpec, Aw33PageResult newSpec)
        {
            var res = new List<WireChange>();
            var oldMap = oldSpec.Wires.ToDictionary(w => w.Key);
            var newMap = newSpec.Wires.ToDictionary(w => w.Key);
            foreach (var key in oldMap.Keys.Union(newMap.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                oldMap.TryGetValue(key, out var a);
                newMap.TryGetValue(key, out var b);
                if (a == null)
                {
                    res.Add(new WireChange { Color = b.Color, Size = b.Size, QtyOld = 0, QtyNew = b.Qty, LenCmNew = b.LengthCm, Kind = "added" });
                }
                else if (b == null)
                {
                    res.Add(new WireChange { Color = a.Color, Size = a.Size, QtyOld = a.Qty, QtyNew = 0, LenCmOld = a.LengthCm, Kind = "removed" });
                }
                else
                {
                    bool sameQty = a.Qty == b.Qty;
                    bool sameLen = Math.Abs(a.LengthCm - b.LengthCm) < 1.0;
                    res.Add(new WireChange
                    {
                        Color = b.Color, Size = b.Size,
                        QtyOld = a.Qty, QtyNew = b.Qty,
                        LenCmOld = a.LengthCm, LenCmNew = b.LengthCm,
                        Kind = (sameQty && sameLen) ? "unchanged" : "changed"
                    });
                }
            }
            return res;
        }

        /// <summary>Сравнение BOM (счёт блоков).</summary>
        public static List<BomChange> CompareBom(Dictionary<string, int> oldBom, Dictionary<string, int> newBom)
        {
            var res = new List<BomChange>();
            foreach (var key in oldBom.Keys.Union(newBom.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                oldBom.TryGetValue(key, out int a);
                newBom.TryGetValue(key, out int b);
                if (a == 0)
                    res.Add(new BomChange { Block = key, CountNew = b, Kind = "added" });
                else if (b == 0)
                    res.Add(new BomChange { Block = key, CountOld = a, Kind = "removed" });
                else if (a != b)
                    res.Add(new BomChange { Block = key, CountOld = a, CountNew = b, Kind = "changed" });
            }
            return res;
        }

        public sealed class TopologyChanges
        {
            public List<string> Added = new List<string>();
            public List<string> Removed = new List<string>();
        }

        /// <summary>Топология: цепи как сигнатуры текстов — добавленные/удалённые.</summary>
        public static TopologyChanges CompareTopology(
            IReadOnlyList<List<string>> oldChains, IReadOnlyList<List<string>> newChains)
        {
            var res = new TopologyChanges();
            foreach (var c in newChains)
            {
                if (!oldChains.Any(o => TopologyDiff.MatchScore(o, c) >= 0.5))
                    res.Added.Add(string.Join(", ", c));
            }
            foreach (var c in oldChains)
            {
                if (!newChains.Any(n => TopologyDiff.MatchScore(c, n) >= 0.5))
                    res.Removed.Add(string.Join(", ", c));
            }
            return res;
        }
    }
}
