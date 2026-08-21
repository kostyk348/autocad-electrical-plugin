using System;
using System.Collections.Generic;
using System.Linq;

namespace El.Core
{
    /// <summary>Данные провода (XData-совместимо: 9 полей).</summary>
    public sealed class WireRecord
    {
        public int Num;
        public string Dev1 = "", Term1 = "", Tip1 = "";
        public string Dev2 = "", Term2 = "", Tip2 = "";
        public string Color = "";
        public int Qty = 1;
        /// <summary>true — провод-переход (стрелки, линии нет): длина 0, в таблице помечается.</summary>
        public bool IsJump;

        public string[] ToXData()
        {
            // первые 5 полей — старый формат (совместимость с DrawWire v2.1),
            // затем tip1, tip2, color, qty, jump-флаг
            return new[] { Num.ToString(), Dev1, Term1, Dev2, Term2, Tip1, Tip2, Color, Qty.ToString(), IsJump ? "1" : "" };
        }

        /// <summary>Из XData (5, 9 или 10 полей — старые провода поддерживаются).</summary>
        public static WireRecord FromXData(IReadOnlyList<string> vals)
        {
            var w = new WireRecord();
            if (vals == null || vals.Count == 0) return w;
            int.TryParse(vals[0], out w.Num);
            if (vals.Count > 1) w.Dev1 = vals[1];
            if (vals.Count > 2) w.Term1 = vals[2];
            if (vals.Count > 3) w.Dev2 = vals[3];
            if (vals.Count > 4) w.Term2 = vals[4];
            if (vals.Count > 5) w.Tip1 = vals[5];
            if (vals.Count > 6) w.Tip2 = vals[6];
            if (vals.Count > 7) w.Color = vals[7];
            if (vals.Count > 8) { int.TryParse(vals[8], out int q); w.Qty = q > 0 ? q : 1; }
            if (vals.Count > 9) w.IsJump = vals[9] == "1";
            return w;
        }
    }
}
