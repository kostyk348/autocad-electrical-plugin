using System;
using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using El.Core;

namespace El.Plugin
{
    /// <summary>
    /// Автоматические слепки при сохранении чертежа (QSAVE/SAVEAS).
    ///
    /// Правила — в <see cref="AutoSnapPolicy"/> (чистая логика, покрыта тестами): не чаще раза в
    /// минуту и не больше 20 слепков в час. Здесь только обвязка: событие команды, чтение/запись
    /// маленьких файлов состояния (десятки байт), копия файла и запись в манифест.
    /// Никаких таймеров и опроса — событие приходит от AutoCAD, работа O(1) на команду.
    /// </summary>
    public static class OmniAutoSnap
    {
        private static readonly AutoSnapPolicy Policy = new AutoSnapPolicy();
        private static bool _installed;
        private static Document _hooked;

        internal static string StateDir => OmniCommands.HistoryDir();
        internal static string ToggleFile => Path.Combine(StateDir, "autosnap.state");
        internal static string LastFile => Path.Combine(StateDir, "autosnap.last");
        internal static string HourFile => Path.Combine(StateDir, "autosnap.hour");

        // ---------------------------------------------------------------- install

        /// <summary>Подписка на события AutoCAD. Вызывается один раз из Plugin.Initialize().</summary>
        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            Application.DocumentManager.DocumentCreated += OnDocumentCreated;
            Application.DocumentManager.DocumentActivated += OnDocumentCreated;
            HookActive();
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            _installed = false;
            try { Application.DocumentManager.DocumentCreated -= OnDocumentCreated; } catch { }
            try { Application.DocumentManager.DocumentActivated -= OnDocumentCreated; } catch { }
            Unhook();
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            try { HookActive(); } catch (System.Exception ex) { Plugin.Log(ex); }
        }

        private static void HookActive()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null || ReferenceEquals(doc, _hooked)) return;
            Unhook();
            doc.CommandEnded += OnCommandEnded;
            _hooked = doc;
        }

        private static void Unhook()
        {
            if (_hooked == null) return;
            try { _hooked.CommandEnded -= OnCommandEnded; } catch { }
            _hooked = null;
        }

        // ---------------------------------------------------------------- event

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            try
            {
                if (!IsSaveCommand(e.GlobalCommandName)) return;
                if (!IsEnabled()) return;
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                TrySnapshot(doc, "auto");
            }
            catch (System.Exception ex)
            {
                // событие не должно ломать сохранение чертежа — только запись в лог
                Plugin.Log(ex);
            }
        }

        internal static bool IsSaveCommand(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.Trim().TrimStart('_', '.').ToUpperInvariant();
            return n == "QSAVE" || n == "SAVEAS" || n == "SAVE";
        }

        // ---------------------------------------------------------------- state (bounded read/write)

        internal static bool IsEnabled()
        {
            try
            {
                if (!File.Exists(ToggleFile)) return true; // по умолчанию включено
                return AutoSnapPolicy.ParseToggle(ReadSmall(ToggleFile, 64));
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        internal static void SetEnabled(bool enabled)
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(ToggleFile, enabled ? "on" : "off");
        }

        private static string ReadSmall(string path, int maxBytes)
        {
            if (!File.Exists(path)) return null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int length = (int)Math.Min(maxBytes, fs.Length);
                var buffer = new byte[length];
                int read = fs.Read(buffer, 0, length);
                return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        private static DateTime? ReadLastSnapUtc()
        {
            string text = ReadSmall(LastFile, 32);
            long ticks;
            if (string.IsNullOrEmpty(text)) return null;
            if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return null;
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>Счётчик за час: «начало окна|количество». O(1) вместо скана истории.</summary>
        private static int ReadHourCount(DateTime nowUtc, out DateTime windowStartUtc)
        {
            windowStartUtc = nowUtc;
            string text = ReadSmall(HourFile, 64);
            if (string.IsNullOrEmpty(text)) return 0;
            var parts = text.Trim().Split('|');
            long ticks;
            int count;
            if (parts.Length != 2) return 0;
            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) return 0;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count)) return 0;
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return 0;
            var start = new DateTime(ticks, DateTimeKind.Utc);
            if ((nowUtc - start).TotalHours >= 1) return 0; // окно истекло — начинаем заново
            windowStartUtc = start;
            return Math.Max(0, count);
        }

        private static void WriteHourState(DateTime windowStartUtc, int count)
        {
            try
            {
                Directory.CreateDirectory(StateDir);
                File.WriteAllText(HourFile,
                    windowStartUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" +
                    count.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------------------------------------------------------------- snapshot

        /// <summary>Делает слепок, если это разрешено политикой. Возвращает true, если сделал.</summary>
        internal static bool TrySnapshot(Document doc, string message)
        {
            if (doc == null) return false;
            string source = doc.Database.Filename;
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return false;

            var now = DateTime.UtcNow;
            var last = ReadLastSnapUtc();
            DateTime windowStart;
            int count = ReadHourCount(now, out windowStart);
            if (!Policy.ShouldSnap(last, count, now)) return false;

            var when = DateTime.Now;
            string name = Revisions.BuildSnapshotName(when, Environment.UserName, message, Path.GetFileName(source));
            string target = Path.Combine(StateDir, name);
            try
            {
                File.Copy(source, target, false);
            }
            catch (IOException) { return false; } // файл занят — следующий раз
            catch (UnauthorizedAccessException) { return false; }

            OmniHistoryCommands.RecordSnapshot(target, Environment.UserName, message, when);

            try
            {
                File.WriteAllText(LastFile, now.Ticks.ToString(CultureInfo.InvariantCulture));
                WriteHourState(count == 0 ? now : windowStart, count + 1);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // авто-сжатие тем же ограниченным путём, что и в OMNI-SNAP
            try
            {
                var raw = OmniHistoryCommands.RawSnapshots();
                if (raw.Count > Revisions.MaxRawSnapshotsKept)
                {
                    int archived;
                    long freed;
                    OmniHistoryCommands.Compact(Revisions.MaxRawSnapshotsKept, out archived, out freed);
                }
            }
            catch (System.Exception ex) { Plugin.Log(ex); }

            var ed = doc.Editor;
            if (ed != null)
                ed.WriteMessage("\n[OMNI] Авто-слепок: " + name);
            return true;
        }

        // ---------------------------------------------------------------- command

        [CommandMethod("OMNI-AUTOSNAP")]
        public static void OmniAutoSnapCommand()
        {
            try
            {
                bool enabled = IsEnabled();
                var ed = DwgAccess.Ed;
                var pko = new PromptKeywordOptions("\n[OMNI] Авто-слепки при сохранении сейчас "
                                                   + (enabled ? "ВКЛ" : "ВЫКЛ") + " [Вкл/Выкл/Состояние]")
                {
                    AllowNone = true
                };
                pko.Keywords.Add("Вкл");
                pko.Keywords.Add("Выкл");
                pko.Keywords.Add("Состояние");
                var res = ed.GetKeywords(pko);

                string choice = res.Status == PromptStatus.OK ? res.StringResult : "";
                if (choice == "Вкл") SetEnabled(true);
                else if (choice == "Выкл") SetEnabled(false);
                else if (choice != "Состояние" && res.Status == PromptStatus.OK) return;

                ed.WriteMessage("\n[OMNI] Авто-слепки: " + (IsEnabled() ? "включены" : "выключены") +
                                " (не чаще " + Policy.MinIntervalSeconds + " с, максимум " +
                                Policy.MaxPerHour + " в час).");
                var last = ReadLastSnapUtc();
                if (last.HasValue)
                    ed.WriteMessage("\n[OMNI] Последний авто-слепок: " +
                                    last.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }
            catch (System.Exception ex) { DwgAccess.Ed.WriteMessage("\n[OMNI] AUTOSNAP: " + ex.Message); Plugin.Log(ex); }
        }
    }
}
