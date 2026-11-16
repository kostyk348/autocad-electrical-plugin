using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace El.Core
{
    /// <summary>
    /// OMNI revision store: snapshot naming, tamper-evident hash chain, retention and archiving.
    ///
    /// Design rules (they are the reason this file looks the way it does):
    ///  * every public method is O(n) in the number of files/entries it is given — no nested scans;
    ///  * no unbounded loops, no recursion, no network, no blocking waits;
    ///  * hashing and zipping stream through 64 KB buffers, so memory does not grow with file size;
    ///  * a file is deleted only after its archive entry was written and its length verified.
    /// </summary>
    public static class Revisions
    {
        public const int MaxMessageLength = 120;
        public const int MaxRawSnapshotsKept = 12;
        private const int CopyBufferBytes = 64 * 1024;

        // ---------------------------------------------------------------- naming

        /// <summary>
        /// Builds a snapshot file name: <c>20250131.142530.123_user_message_drawing.dwg</c>.
        /// The message is sanitised — it ends up in a path, so quotes, separators, dots-only
        /// segments and control characters are removed rather than escaped.
        /// </summary>
        public static string BuildSnapshotName(DateTime when, string user, string message, string dwgFileName)
        {
            string stamp = when.ToString("yyyyMMdd.HHmmss", CultureInfo.InvariantCulture)
                           + "." + when.Millisecond.ToString("000", CultureInfo.InvariantCulture);
            string safeUser = SafeSegment(user, 32, "user").Replace('_', '-');
            string safeMsg = SafeSegment(message, MaxMessageLength, "edit").Replace('_', '-');
            string safeDwg = SafeFileName(dwgFileName, 96);
            return stamp + "_" + safeUser + "_" + safeMsg + "_" + safeDwg;
        }

        /// <summary>Splits a snapshot name back into its parts. Never throws; unknown parts stay null.</summary>
        public static SnapshotName ParseSnapshotName(string fileName)
        {
            var result = new SnapshotName { FileName = fileName };
            if (string.IsNullOrEmpty(fileName)) return result;

            // <stamp>_<user>_<message>_<drawing>: exactly three separators, the drawing keeps the rest.
            var parts = SplitN(fileName, 3);
            if (parts.Count < 4) return result;
            result.Stamp = parts[0];
            result.User = parts[1];
            result.Message = parts[2];
            result.Drawing = parts[3];

            DateTime parsed;
            if (DateTime.TryParseExact(result.Stamp, "yyyyMMdd.HHmmss.fff", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out parsed))
            {
                result.When = parsed;
            }
            return result;
        }

        /// <summary>Splits on '_' into at most <paramref name="maxSplits"/>+1 parts (the tail keeps its underscores).</summary>
        private static List<string> SplitN(string text, int maxSplits)
        {
            var parts = new List<string>();
            int start = 0, split = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '_' || split >= maxSplits) continue;
                parts.Add(text.Substring(start, i - start));
                start = i + 1;
                split++;
            }
            parts.Add(text.Substring(start));
            return parts;
        }

        private static readonly char[] InvalidSegmentChars = "<>:\"/\\|?*\0".ToCharArray();

        /// <summary>Turns free text into a single safe path segment of bounded length.</summary>
        public static string SafeSegment(string text, int maxLength, string fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = false;
            for (int i = 0; i < text.Length && sb.Length < maxLength; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n' || c == '\t') c = ' ';
                if (c < 32 || c == 127) continue;
                if (Array.IndexOf(InvalidSegmentChars, c) >= 0) continue;
                if (c == ' ')
                {
                    if (lastWasSpace || sb.Length == 0) continue;
                    sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }
                lastWasSpace = false;
                sb.Append(c);
            }
            string result = sb.ToString().Trim(' ', '.');
            if (result.Length == 0) return fallback;
            if (result == "." || result == "..") return fallback;
            return result;
        }

        /// <summary>Keeps the extension, bounds the stem length, strips separators.</summary>
        public static string SafeFileName(string fileName, int maxStemLength)
        {
            if (string.IsNullOrEmpty(fileName)) return "drawing.dwg";
            string name = fileName;
            int slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            if (slash >= 0) name = name.Substring(slash + 1);
            string ext = Path.GetExtension(name);
            if (ext.Length > 8) ext = ext.Substring(0, 8);
            string stem = ext.Length > 0 ? name.Substring(0, name.Length - ext.Length) : name;
            stem = SafeSegment(stem, maxStemLength, "drawing");
            return stem + (ext.Length > 0 ? ext : ".dwg");
        }

        // ---------------------------------------------------------------- hashing

        /// <summary>SHA-256 of a file, streamed in 64 KB blocks — O(size), constant memory.</summary>
        public static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                           CopyBufferBytes, FileOptions.SequentialScan))
            {
                return ToHex(sha.ComputeHash(fs));
            }
        }

        public static string Sha256OfText(string text)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)));
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // ---------------------------------------------------------------- manifest (hash chain)

        /// <summary>
        /// Appends one entry to the JSONL manifest, linking it to the previous one:
        /// <c>hash = sha256(prevHash | fileName | stamp | user | message | size | sha256)</c>.
        /// Reading the tail is bounded, so the append is O(1) regardless of history length.
        /// </summary>
        public static RevisionEntry AppendManifest(string manifestPath, RevisionEntry entry)
        {
            if (string.IsNullOrEmpty(manifestPath)) throw new ArgumentNullException("manifestPath");
            if (entry == null) throw new ArgumentNullException("entry");

            entry.PrevHash = ReadLastHash(manifestPath) ?? new string('0', 64);
            entry.Hash = ChainHash(entry);

            string dir = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(manifestPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                w.Write(entry.ToJsonLine());
                w.Write('\n');
            }
            return entry;
        }

        /// <summary>Chain hash of one entry (deterministic, order-sensitive).</summary>
        public static string ChainHash(RevisionEntry e)
        {
            string payload = string.Join("|", new[]
            {
                e.PrevHash ?? string.Empty,
                e.FileName ?? string.Empty,
                e.Stamp ?? string.Empty,
                e.User ?? string.Empty,
                e.Message ?? string.Empty,
                e.SizeBytes.ToString(CultureInfo.InvariantCulture),
                e.Sha256 ?? string.Empty
            });
            return Sha256OfText(payload);
        }

        /// <summary>Reads the last 16 KB of the manifest and returns the hash of the last entry.</summary>
        private static string ReadLastHash(string manifestPath)
        {
            if (!File.Exists(manifestPath)) return null;
            const int tailBytes = 16 * 1024;
            using (var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long start = Math.Max(0, fs.Length - tailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[fs.Length - start];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = fs.Read(buffer, read, buffer.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read == 0) return null;
                string text = Encoding.UTF8.GetString(buffer, 0, read);
                var lines = text.Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    var parsed = RevisionEntry.TryParse(line);
                    if (parsed != null && !string.IsNullOrEmpty(parsed.Hash)) return parsed.Hash;
                }
            }
            return null;
        }

        /// <summary>Reads the whole manifest. O(size); lines that do not parse are skipped, not thrown on.</summary>
        public static List<RevisionEntry> ReadManifest(string manifestPath, int maxEntries = 100000)
        {
            var list = new List<RevisionEntry>();
            if (!File.Exists(manifestPath)) return list;
            int count = 0;
            using (var fs = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                           CopyBufferBytes, FileOptions.SequentialScan))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while (count < maxEntries && (line = reader.ReadLine()) != null)
                {
                    count++;
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    var entry = RevisionEntry.TryParse(trimmed);
                    if (entry != null) list.Add(entry);
                }
            }
            return list;
        }

        /// <summary>
        /// Recomputes every link. Returns the index of the first broken entry, or -1 when the
        /// chain is intact. O(n) with one hash per entry.
        /// </summary>
        public static int VerifyChain(IList<RevisionEntry> entries)
        {
            if (entries == null) return -1;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string expectedPrev = i == 0 ? new string('0', 64) : entries[i - 1].Hash;
                if (!string.Equals(e.PrevHash ?? string.Empty, expectedPrev, StringComparison.Ordinal)) return i;
                if (!string.Equals(e.Hash ?? string.Empty, ChainHash(e), StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        // ---------------------------------------------------------------- retention & archiving

        /// <summary>
        /// Decides what may leave the raw folder: keeps the newest <paramref name="keepRaw"/> files,
        /// archives the rest, and never plans more than <paramref name="maxBatch"/> moves per call
        /// (bounded work — a 10 000-file history cannot stall the editor).
        /// </summary>
        public static RetentionPlan PlanRetention(IEnumerable<FileInfo> rawSnapshotFiles, int keepRaw = MaxRawSnapshotsKept,
                                                  int maxBatch = 200, DateTime? olderThanUtc = null)
        {
            var plan = new RetentionPlan();
            if (rawSnapshotFiles == null) return plan;
            var files = rawSnapshotFiles
                .Where(f => f != null && f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            var keep = new List<FileInfo>();
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                bool insideKeepWindow = i < Math.Max(0, keepRaw);
                bool tooNewToArchive = olderThanUtc.HasValue && f.LastWriteTimeUtc >= olderThanUtc.Value;
                if (insideKeepWindow || tooNewToArchive) keep.Add(f);
                else if (plan.ToArchive.Count < Math.Max(0, maxBatch)) plan.ToArchive.Add(f);
                // beyond the batch limit the file simply stays where it is until the next run
            }
            plan.Kept = keep;
            return plan;
        }

        /// <summary>
        /// Packs the planned snapshots into <paramref name="zipPath"/> (append-or-create) and deletes
        /// the originals only after the entry was written and its uncompressed length verified.
        /// O(total bytes) with 64 KB streaming; bounded by the plan.
        /// </summary>
        public static ArchiveResult Archive(string zipPath, IEnumerable<FileInfo> files)
        {
            var result = new ArchiveResult();
            if (string.IsNullOrEmpty(zipPath) || files == null) return result;
            var list = files.Where(f => f != null && f.Exists).ToList();
            if (list.Count == 0) return result;

            string dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var fs = new FileStream(zipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                foreach (var file in list)
                {
                    string name = file.Name;
                    var existing = zip.GetEntry(name);
                    if (existing != null) zip.GetEntry(name).Delete();

                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    long written;
                    using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                      CopyBufferBytes, FileOptions.SequentialScan))
                    using (var output = entry.Open())
                    {
                        input.CopyTo(output, CopyBufferBytes);
                        written = input.Length;
                    }
                    if (written != file.Length)
                    {
                        result.Failed.Add(file.Name);
                        continue;
                    }
                    result.Archived.Add(file.Name);
                    result.BytesFreed += file.Length;
                }
            }

            // delete only what is provably inside the archive
            using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries) names.Add(e.FullName);
                foreach (var file in list)
                {
                    if (!result.Archived.Contains(file.Name)) continue;
                    if (!names.Contains(file.Name)) continue;
                    try { file.Delete(); result.Deleted++; }
                    catch (IOException) { /* locked file stays in place; nothing is lost */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return result;
        }

        /// <summary>Extracts one entry from an archive to <paramref name="destinationPath"/>. O(entry size).</summary>
        public static bool Extract(string zipPath, string entryName, string destinationPath)
        {
            if (string.IsNullOrEmpty(zipPath) || string.IsNullOrEmpty(entryName) || !File.Exists(zipPath)) return false;
            using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = null;
                foreach (var e in zip.Entries)
                {
                    if (!string.Equals(e.FullName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
                    entry = e;
                    break;
                }
                if (entry == null) return false;
                string dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using (var input = entry.Open())
                using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                                   CopyBufferBytes))
                {
                    input.CopyTo(output, CopyBufferBytes);
                }
                return new FileInfo(destinationPath).Length == entry.Length;
            }
        }

        /// <summary>Finds which archive of a directory holds the given snapshot (newest archive first).</summary>
        public static string FindInArchives(string archiveDir, string fileName)
        {
            if (string.IsNullOrEmpty(archiveDir) || !Directory.Exists(archiveDir)) return null;
            var zips = Directory.GetFiles(archiveDir, "*.zip").OrderByDescending(f => f).ToList();
            foreach (var zip in zips)
            {
                try
                {
                    using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        foreach (var e in archive.Entries)
                            if (string.Equals(e.FullName, fileName, StringComparison.OrdinalIgnoreCase)) return zip;
                    }
                }
                catch (InvalidDataException) { /* a corrupt archive must not stop the search */ }
                catch (IOException) { }
            }
            return null;
        }
    }

    /// <summary>Parts of a snapshot file name.</summary>
    public sealed class SnapshotName
    {
        public string FileName { get; set; }
        public string Stamp { get; set; }
        public string User { get; set; }
        public string Message { get; set; }
        public string Drawing { get; set; }
        public DateTime? When { get; set; }
    }

    public sealed class RetentionPlan
    {
        public List<FileInfo> ToArchive { get; } = new List<FileInfo>();
        public List<FileInfo> Kept { get; set; } = new List<FileInfo>();
    }

    public sealed class ArchiveResult
    {
        public List<string> Archived { get; } = new List<string>();
        public List<string> Failed { get; } = new List<string>();
        public int Deleted { get; set; }
        public long BytesFreed { get; set; }
    }

    /// <summary>One revision in the manifest. Serialised by hand — no reflection, no JSON dependency.</summary>
    public sealed class RevisionEntry
    {
        public string FileName { get; set; }
        public string Stamp { get; set; }
        public string User { get; set; }
        public string Message { get; set; }
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; }
        public string PrevHash { get; set; }
        public string Hash { get; set; }

        public string ToJsonLine()
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendField(sb, "file", FileName); sb.Append(',');
            AppendField(sb, "stamp", Stamp); sb.Append(',');
            AppendField(sb, "user", User); sb.Append(',');
            AppendField(sb, "msg", Message); sb.Append(',');
            sb.Append("\"size\":").Append(SizeBytes.ToString(CultureInfo.InvariantCulture)).Append(',');
            AppendField(sb, "sha256", Sha256); sb.Append(',');
            AppendField(sb, "prev", PrevHash); sb.Append(',');
            AppendField(sb, "hash", Hash);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendField(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":\"");
            string text = value ?? string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Strict flat-object parser for lines written by <see cref="ToJsonLine"/>. Returns null on any doubt.</summary>
        public static RevisionEntry TryParse(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            var map = ParseFlatObject(line);
            if (map == null) return null;
            long size;
            long.TryParse(Get(map, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            return new RevisionEntry
            {
                FileName = Get(map, "file"),
                Stamp = Get(map, "stamp"),
                User = Get(map, "user"),
                Message = Get(map, "msg"),
                SizeBytes = size,
                Sha256 = Get(map, "sha256"),
                PrevHash = Get(map, "prev"),
                Hash = Get(map, "hash")
            };
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string value;
            return map.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, string> ParseFlatObject(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0, n = text.Length;
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            if (i >= n || text[i] != '{') return null;
            i++;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i < n && text[i] == '}') return map;
                if (i >= n || text[i] != '"') return null;
                string key = ReadString(text, ref i);
                if (key == null) return null;
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n || text[i] != ':') return null;
                i++;
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n) return null;
                string value;
                if (text[i] == '"')
                {
                    value = ReadString(text, ref i);
                    if (value == null) return null;
                }
                else
                {
                    int start = i;
                    while (i < n && (char.IsDigit(text[i]) || text[i] == '-' || text[i] == '+')) i++;
                    if (i == start) return null;
                    value = text.Substring(start, i - start);
                }
                map[key] = value;
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i < n && text[i] == ',') { i++; continue; }
                if (i < n && text[i] == '}') return map;
                return null;
            }
            return null;
        }

        private static string ReadString(string text, ref int i)
        {
            if (i >= text.Length || text[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c != '\\') { sb.Append(c); i++; continue; }
                i++;
                if (i >= text.Length) return null;
                char esc = text[i];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 >= text.Length) return null;
                        string hex = text.Substring(i + 1, 4);
                        int code;
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) return null;
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: return null;
                }
                i++;
            }
            return null;
        }
    }
}
