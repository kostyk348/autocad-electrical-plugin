using System;
using System.Collections.Generic;

namespace El.Core
{
    /// <summary>Почему авто-слепок делается или нет — чтобы в командной строке было видно причину.</summary>
    public enum AutoSnapReason
    {
        Ok,
        TooSoon,
        HourlyQuotaReached,
        ClockWentBackwards
    }

    /// <summary>
    /// Правило авто-слепков при сохранении чертежа.
    /// Логика без побочных эффектов и без таймеров: решение принимается по последнему слепку и
    /// счётчику за час, поэтому поведение проверяется тестами, а не «на глаз».
    /// Полностью O(1) — никаких сканов истории на каждом сохранении.
    /// </summary>
    public sealed class AutoSnapPolicy
    {
        /// <summary>Не чаще одного слепка в столько секунд (по умолчанию минута).</summary>
        public int MinIntervalSeconds { get; set; } = 60;

        /// <summary>Потолок слепков в час: защита от «сохранил 200 раз подряд».</summary>
        public int MaxPerHour { get; set; } = 20;

        public AutoSnapPolicy() { }

        public AutoSnapPolicy(int minIntervalSeconds, int maxPerHour)
        {
            MinIntervalSeconds = Math.Max(0, minIntervalSeconds);
            MaxPerHour = Math.Max(0, maxPerHour);
        }

        /// <summary>Слепок сейчас допустим?</summary>
        public AutoSnapReason Evaluate(DateTime? lastSnapUtc, int snapsLastHour, DateTime nowUtc)
        {
            if (snapsLastHour >= MaxPerHour) return AutoSnapReason.HourlyQuotaReached;

            if (lastSnapUtc.HasValue)
            {
                if (nowUtc < lastSnapUtc.Value) return AutoSnapReason.ClockWentBackwards;
                double seconds = (nowUtc - lastSnapUtc.Value).TotalSeconds;
                if (seconds < MinIntervalSeconds) return AutoSnapReason.TooSoon;
            }
            return AutoSnapReason.Ok;
        }

        public bool ShouldSnap(DateTime? lastSnapUtc, int snapsLastHour, DateTime nowUtc)
        {
            return Evaluate(lastSnapUtc, snapsLastHour, nowUtc) == AutoSnapReason.Ok;
        }

        /// <summary>Сколько слепков из списка попадает в последний час — O(n), без сортировок.</summary>
        public static int CountWithinHour(IEnumerable<DateTime> snapshotUtcTimes, DateTime nowUtc)
        {
            if (snapshotUtcTimes == null) return 0;
            int count = 0;
            DateTime from = nowUtc.AddHours(-1);
            foreach (var t in snapshotUtcTimes)
                if (t >= from && t <= nowUtc) count++;
            return count;
        }

        /// <summary>Состояние переключателя из файла: отсутствие/пусто/«on» — включено.</summary>
        public static bool ParseToggle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string value = text.Trim().ToLowerInvariant();
            return value != "off" && value != "0" && value != "false" && value != "выкл";
        }
    }
}
