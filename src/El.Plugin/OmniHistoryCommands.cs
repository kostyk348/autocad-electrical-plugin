using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using El.Core;

namespace El.Plugin
{
    /// <summary>
    /// OMNI: история ревизий поверх слепков DWG.
    ///
    ///  * <c>OMNI-HISTORY</c> — цепочка ревизий с проверкой целостности (SHA-256 prev→next);
    ///  * <c>OMNI-ARCHIVE</c> — сжатие старых слепков в ZIP, чтобы история не съедала диск;
    ///  * <c>OMNI-RESTORE</c> — откат к выбранной ревизии (с обязательным страховочным слепком).
    ///
    /// Все операции линейны по числу файлов и не содержат циклов без верхней границы:
    /// список файлов ограничен <see cref="MaxScanFiles"/>, пакет архивации — <see cref="MaxArchiveBatch"/>,
    /// поэтому команда не может «повиснуть» на большой истории.
    /// </summary>
    public static class OmniHistoryCommands
    {
        private const int MaxScanFiles = 5000;
        private const int MaxArchiveBatch = 200;
        private const int MaxDisplayEntries = 25;

        private static Editor Ed => DwgAccess.Ed;

        internal static string ManifestPath()
        {
            return Path.Combine(OmniCommands.HistoryDir(), "history.jsonl");
        }

        internal static string ArchiveDir()
        {
            string dir = Path.Combine(OmniCommands.HistoryDir(), "archive");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Raw snapshots in the history folder, bounded and newest-last.</summary>
        internal static List<FileInfo> RawSnapshots()
        {
            string dir = OmniCommands.HistoryDir();
            if (!Directory.Exists(dir)) return new List<FileInfo>();
            var files = Directory.GetFiles(dir, "*.dwg");
            int take = Math.Min(files.Length, MaxScanFiles);
            var infos = new List<FileInfo>(take);
            for (int i = 0; i < take; i++)
            {
                var info = new FileInfo(files[i]);
                if (info.Exists) infos.Add(info);
            }
            infos.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            return infos;
        }

        /// <summary>Records a snapshot in the tamper-evident manifest. Returns null on failure.</summary>
        internal static RevisionEntry RecordSnapshot(string snapshotPath, string user, string message, DateTime when)
        {
            try
            {
                var info = new FileInfo(snapshotPath);
                if (!info.Exists) return null;
                var entry = new RevisionEntry
                {
                    FileName = info.Name,
                    Stamp = when.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
                    User = user,
                    Message = message ?? string.Empty,
                    SizeBytes = info.Length,
                    Sha256 = Revisions.Sha256OfFile(snapshotPath)
                };
                return Revisions.AppendManifest(ManifestPath(), entry);
            }
            catch (System.Exception ex)
            {
                Ed.WriteMessage("\n[OMNI] Манифест не записан: " + ex.Message);
                Plugin.Log(ex);
                return null;
            }
        }

        /// <summary>
        /// Keeps the newest <paramref name="keepRaw"/> raw snapshots and packs the older ones into
        /// archive\YYYY-MM.zip. Bounded work, returns a human-readable summary.
        /// </summary>
        internal static string Compact(int keepRaw, out int archived, out long bytesFreed)
        {
            archived = 0;
            bytesFreed = 0;
            var raw = RawSnapshots();
            if (raw.Count == 0) return "слепков нет";

            var plan = Revisions.PlanRetention(raw, keepRaw, MaxArchiveBatch);
            if (plan.ToArchive.Count == 0) return "нечего сжимать (слепков: " + raw.Count + ")";

            var groups = plan.ToArchive
                .GroupBy(f => f.LastWriteTimeUtc.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .ToList();
            foreach (var group in groups)
            {
                string zip = Path.Combine(ArchiveDir(), group.Key + ".zip");
                var result = Revisions.Archive(zip, group);
                archived += result.Deleted;
                bytesFreed += result.BytesFreed;
                if (result.Failed.Count > 0)
                    Ed.WriteMessage("\n[OMNI] Не упакованы: " + string.Join(", ", result.Failed.Take(5)));
            }
            return "сжато " + archived + " слепков, освобождено " + Mb(bytesFreed);
        }

        private static string Mb(long bytes)
        {
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " МБ";
        }

        // ---------- OMNI-HISTORY ----------

        [CommandMethod("OMNI-HISTORY")]
        public static void OmniHistory()
        {
            try
            {
                var entries = Revisions.ReadManifest(ManifestPath());
                var raw = RawSnapshots();
                int archived = Directory.GetFiles(ArchiveDir(), "*.zip").Length;

                if (entries.Count == 0)
                {
                    Ed.WriteMessage("\n[OMNI] Манифест пуст. Слепки: " + raw.Count +
                                    " (в архивах: " + archived + "). Делай OMNI-SNAP — история начнёт вестись.");
                    return;
                }

                int broken = Revisions.VerifyChain(entries);
                Ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                    "\n[OMNI] === История: {0} ревизий, слепков на диске {1}, архивов {2} ===",
                    entries.Count, raw.Count, archived));
                Ed.WriteMessage(broken < 0
                    ? "\n[OMNI] Цепочка целостна (SHA-256 prev→next совпадает)."
                    : "\n[OMNI] ВНИМАНИЕ: цепочка нарушена на записи #" + broken + " — история правила вручную.");

                int from = Math.Max(0, entries.Count - MaxDisplayEntries);
                for (int i = from; i < entries.Count; i++)
                {
                    var e = entries[i];
                    Ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                        "\n  {0,3}. {1}  {2,-12}  {3,8}  {4}",
                        i + 1, e.Stamp, Truncate(e.User, 12),
                        SizeText(e.SizeBytes), Truncate(e.Message, 48)));
                }
                if (entries.Count > MaxDisplayEntries)
                    Ed.WriteMessage("\n  ... показаны последние " + MaxDisplayEntries + " из " + entries.Count);
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] HISTORY: " + ex.Message); Plugin.Log(ex); }
        }

        // ---------- OMNI-ARCHIVE ----------

        [CommandMethod("OMNI-ARCHIVE")]
        public static void OmniArchive()
        {
            try
            {
                var raw = RawSnapshots();
                if (raw.Count == 0) { Ed.WriteMessage("\n[OMNI] Слепков нет."); return; }

                var pio = new PromptIntegerOptions("\n[OMNI] Сколько последних слепков оставить распакованными")
                {
                    DefaultValue = Revisions.MaxRawSnapshotsKept,
                    LowerLimit = 1,
                    UpperLimit = 1000
                };
                var res = Ed.GetInteger(pio);
                int keep = res.Status == PromptStatus.OK ? res.Value : Revisions.MaxRawSnapshotsKept;

                int archived;
                long freed;
                string summary = Compact(keep, out archived, out freed);
                Ed.WriteMessage("\n[OMNI] " + summary + ". Открыть историю — OMNI-HISTORY, откатиться — OMNI-RESTORE.");
                Plugin.LogText("OMNI-ARCHIVE keep=" + keep + " " + summary);
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] ARCHIVE: " + ex.Message); Plugin.Log(ex); }
        }

        // ---------- OMNI-RESTORE ----------

        [CommandMethod("OMNI-RESTORE")]
        public static void OmniRestore()
        {
            try
            {
                var doc = DwgAccess.Doc;
                string current = doc.Database.Filename;
                if (string.IsNullOrEmpty(current) || !File.Exists(current))
                {
                    Ed.WriteMessage("\n[OMNI] Файл не сохранён — откат невозможен. Сначала QSAVE.");
                    return;
                }

                var entries = Revisions.ReadManifest(ManifestPath());
                var raw = RawSnapshots();
                // 1) ревизии из манифеста, 2) «сироты» — слепки без записи в манифесте
                var labelled = new List<string>();
                var payload = new List<KeyValuePair<string, string>>(); // label -> file name
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    known.Add(e.FileName);
                    string label = (i + 1) + ". " + e.Stamp + "  " + Truncate(e.User, 12) + "  " + Truncate(e.Message, 40);
                    labelled.Add(label);
                    payload.Add(new KeyValuePair<string, string>(label, e.FileName));
                }
                foreach (var file in raw)
                {
                    if (known.Contains(file.Name)) continue;
                    string label = "•  " + file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                                   "  " + Truncate(file.Name, 60);
                    labelled.Add(label);
                    payload.Add(new KeyValuePair<string, string>(label, file.Name));
                }
                if (labelled.Count == 0) { Ed.WriteMessage("\n[OMNI] Ревизий нет."); return; }

                // newest first in the dialog
                labelled.Reverse();
                payload.Reverse();

                var dlg = new El.Plugin.Ui.ListPickDialog("OMNI — откат к ревизии",
                    "Выберите ревизию (сначала новые):", labelled);
                if (dlg.Show() != System.Windows.Forms.DialogResult.OK || dlg.Selected == null) return;

                string fileName = null;
                foreach (var pair in payload)
                {
                    if (string.Equals(pair.Key, dlg.Selected, StringComparison.Ordinal)) { fileName = pair.Value; break; }
                }
                if (string.IsNullOrEmpty(fileName)) return;

                // locate: raw snapshot first, then archive
                string sourcePath = Path.Combine(OmniCommands.HistoryDir(), fileName);
                string temp = null;
                if (!File.Exists(sourcePath))
                {
                    string zip = Revisions.FindInArchives(ArchiveDir(), fileName);
                    if (zip == null)
                    {
                        Ed.WriteMessage("\n[OMNI] Ревизия не найдена ни на диске, ни в архиве: " + fileName);
                        return;
                    }
                    temp = Path.Combine(Path.GetTempPath(), "omni-restore-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dwg");
                    if (!Revisions.Extract(zip, fileName, temp))
                    {
                        Ed.WriteMessage("\n[OMNI] Не удалось распаковать ревизию из " + Path.GetFileName(zip));
                        return;
                    }
                    sourcePath = temp;
                }

                // 2) страховочный слепок текущего состояния — история не должна теряться при откате
                var when = DateTime.Now;
                string safety = Path.Combine(OmniCommands.HistoryDir(),
                    Revisions.BuildSnapshotName(when, Environment.UserName, "before-restore", Path.GetFileName(current)));
                try { File.Copy(current, safety, true); }
                catch (System.Exception copyEx)
                {
                    Ed.WriteMessage("\n[OMNI] Откат отменён: не удалось сделать страховочный слепок (" + copyEx.Message + ")");
                    Plugin.Log(copyEx);
                    if (temp != null) TryDelete(temp);
                    return;
                }
                RecordSnapshot(safety, Environment.UserName, "before-restore", when);

                // 3) сам откат
                try
                {
                    doc.CloseAndDiscard();
                }
                catch (System.Exception closeEx)
                {
                    Ed.WriteMessage("\n[OMNI] Не удалось закрыть текущий документ: " + closeEx.Message);
                    Plugin.Log(closeEx);
                    if (temp != null) TryDelete(temp);
                    return;
                }

                File.Copy(sourcePath, current, true);
                if (temp != null) TryDelete(temp);

                Application.DocumentManager.Open(current);
                Ed.WriteMessage("\n[OMNI] Откат выполнен: " + fileName + ". Предыдущее состояние сохранено как " +
                                Path.GetFileName(safety) + ".");
            }
            catch (System.Exception ex) { Ed.WriteMessage("\n[OMNI] RESTORE: " + ex.Message); Plugin.Log(ex); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        private static string SizeText(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
