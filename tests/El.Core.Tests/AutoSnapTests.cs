using System;
using System.Collections.Generic;
using El.Core;

namespace El.Core.Tests
{
    /// <summary>Правило авто-слепков: интервал, часовой потолок, устойчивость к переводу часов.</summary>
    public class AutoSnapTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static readonly DateTime Now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        public void Test_AutoSnap_FirstSaveIsAllowed()
        {
            var policy = new AutoSnapPolicy(60, 20);
            Assert(policy.Evaluate(null, 0, Now) == AutoSnapReason.Ok, "первый слепок должен быть разрешён");
        }

        public void Test_AutoSnap_TooSoonIsRejected()
        {
            var policy = new AutoSnapPolicy(60, 20);
            Assert(policy.Evaluate(Now.AddSeconds(-10), 0, Now) == AutoSnapReason.TooSoon, "10 с < 60 с");
            Assert(policy.Evaluate(Now.AddSeconds(-59), 0, Now) == AutoSnapReason.TooSoon, "59 с < 60 с");
        }

        public void Test_AutoSnap_AllowedAfterInterval()
        {
            var policy = new AutoSnapPolicy(60, 20);
            Assert(policy.Evaluate(Now.AddSeconds(-60), 0, Now) == AutoSnapReason.Ok, "ровно 60 с — уже можно");
            Assert(policy.Evaluate(Now.AddHours(-3), 0, Now) == AutoSnapReason.Ok, "три часа назад — можно");
        }

        public void Test_AutoSnap_HourlyQuotaStopsTheStorm()
        {
            var policy = new AutoSnapPolicy(0, 5);
            Assert(policy.Evaluate(Now.AddSeconds(-1), 4, Now) == AutoSnapReason.Ok, "4 из 5 — ещё можно");
            Assert(policy.Evaluate(Now.AddSeconds(-1), 5, Now) == AutoSnapReason.HourlyQuotaReached, "5 из 5 — стоп");
            Assert(policy.Evaluate(Now.AddSeconds(-1), 99, Now) == AutoSnapReason.HourlyQuotaReached, "перебор — стоп");
        }

        public void Test_AutoSnap_ClockWentBackwardsIsSafe()
        {
            var policy = new AutoSnapPolicy(60, 20);
            Assert(policy.Evaluate(Now.AddHours(2), 0, Now) == AutoSnapReason.ClockWentBackwards,
                "метка из будущего не должна приводить к слепку на каждом сохранении");
        }

        public void Test_AutoSnap_CountWithinHour()
        {
            var times = new List<DateTime>
            {
                Now.AddMinutes(-90), // вне часа
                Now.AddMinutes(-30),
                Now.AddMinutes(-5),
                Now              // граница включительно
            };
            Assert(AutoSnapPolicy.CountWithinHour(times, Now) == 3, "три метки попали в час");
            Assert(AutoSnapPolicy.CountWithinHour(null, Now) == 0, "null-список");
        }

        public void Test_AutoSnap_ToggleParsing()
        {
            Assert(AutoSnapPolicy.ParseToggle(null), "пусто — включено");
            Assert(AutoSnapPolicy.ParseToggle("on"), "on");
            Assert(!AutoSnapPolicy.ParseToggle("off"), "off");
            Assert(!AutoSnapPolicy.ParseToggle("  OFF \r\n"), "off с пробелами и CRLF");
            Assert(!AutoSnapPolicy.ParseToggle("0"), "0");
            Assert(!AutoSnapPolicy.ParseToggle("выкл"), "выкл");
        }
    }
}
