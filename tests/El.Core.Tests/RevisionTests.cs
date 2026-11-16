using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using El.Core;

namespace El.Core.Tests
{
    /// <summary>
    /// OMNI revision store: naming (path safety), hash chain, retention planning, ZIP archive.
    /// Every test is self-contained and cleans up after itself.
    /// </summary>
    public class RevisionTests
    {
        private static string TempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "el-rev-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertEqual(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception(message + " (expected '" + expected + "', got '" + actual + "')");
        }

        // ------------------------------------------------------------ naming

        public void Test_SnapshotName_SanitizesPathTraversal()
        {
            string name = Revisions.BuildSnapshotName(
                new DateTime(2025, 1, 31, 14, 25, 30, 123),
                @"DOMAIN\lab",
                @"..\..\evil:name*?""<>|",
                @"C:\Projects\drawing.dwg");

            Assert(!name.Contains("\\"), "backslash survived: " + name);
            Assert(!name.Contains(":"), "colon survived: " + name);
            Assert(!name.Contains(".."), "dot-dot survived: " + name);
            Assert(!name.Contains("*") && !name.Contains("?") && !name.Contains("\""), "wildcard survived: " + name);
            Assert(name.StartsWith("20250131.142530.123_", StringComparison.Ordinal), "stamp lost: " + name);
            Assert(name.EndsWith("_drawing.dwg", StringComparison.Ordinal), "drawing lost: " + name);
            // the name must be a single path segment
            AssertEqual(name, Path.GetFileName(name), "not a single segment");
        }

        public void Test_SnapshotName_RoundTrip()
        {
            var when = new DateTime(2025, 3, 7, 9, 5, 6, 42);
            string name = Revisions.BuildSnapshotName(when, "lab", "добавил кабель АВВГ", "схема_щит.dwg");
            var parsed = Revisions.ParseSnapshotName(name);

            AssertEqual(name, parsed.FileName, "file name");
            AssertEqual("lab", parsed.User, "user");
            AssertEqual("добавил кабель АВВГ", parsed.Message, "message");
            AssertEqual("схема_щит.dwg", parsed.Drawing, "drawing keeps underscores");
            Assert(parsed.When.HasValue && parsed.When.Value == when, "timestamp round-trip");
        }

        public void Test_SnapshotName_EmptyMessageGetsFallback()
        {
            string name = Revisions.BuildSnapshotName(DateTime.UtcNow, "", "   ", "a.dwg");
            Assert(name.Contains("_edit_"), "fallback message missing: " + name);
            Assert(name.Contains("_user_"), "fallback user missing: " + name);
        }

        public void Test_SnapshotName_MessageIsLengthBounded()
        {
            string msg = new string('x', 5000);
            string name = Revisions.BuildSnapshotName(DateTime.UtcNow, "u", msg, "a.dwg");
            Assert(name.Length < 300, "name not bounded: " + name.Length);
            Assert(name.Length <= 300, "unbounded name");
        }

        // ------------------------------------------------------------ chain

        public void Test_Chain_AppendAndVerify()
        {
            string dir = TempDir("chain");
            try
            {
                string manifest = Path.Combine(dir, "history.jsonl");
                for (int i = 0; i < 5; i++)
                {
                    Revisions.AppendManifest(manifest, new RevisionEntry
                    {
                        FileName = "snap" + i + ".dwg",
                        Stamp = DateTime.UtcNow.ToString("o"),
                        User = "lab",
                        Message = "edit " + i,
                        SizeBytes = 100 + i,
                        Sha256 = Revisions.Sha256OfText("snap" + i)
                    });
                }
                var entries = Revisions.ReadManifest(manifest);
                Assert(entries.Count == 5, "expected 5 entries, got " + entries.Count);
                AssertEqual(Revisions.ChainHash(entries[0]), entries[0].Hash, "first hash");
                Assert(entries[0].PrevHash == new string('0', 64), "genesis prev hash");
                AssertEqual(entries[1].PrevHash, entries[0].Hash, "link 0->1");
                AssertEqual(entries[4].PrevHash, entries[3].Hash, "link 3->4");
                Assert(Revisions.VerifyChain(entries) == -1, "chain must verify");
            }
            finally { Cleanup(dir); }
        }

        public void Test_Chain_DetectsTampering()
        {
            var entries = new List<RevisionEntry>();
            for (int i = 0; i < 3; i++)
            {
                var e = new RevisionEntry
                {
                    FileName = "s" + i + ".dwg",
                    Stamp = "2025-01-01T00:00:00Z",
                    User = "lab",
                    Message = "m" + i,
                    SizeBytes = i,
                    Sha256 = "abc" + i,
                    PrevHash = i == 0 ? new string('0', 64) : entries[i - 1].Hash
                };
                e.Hash = Revisions.ChainHash(e);
                entries.Add(e);
            }
            Assert(Revisions.VerifyChain(entries) == -1, "clean chain");

            entries[1].Message = "подменённое сообщение";
            Assert(Revisions.VerifyChain(entries) == 1, "tampering must be detected at index 1");

            entries[1].Message = "m1";
            Assert(Revisions.VerifyChain(entries) == -1, "restored chain");

            entries[2].PrevHash = entries[0].Hash;
            Assert(Revisions.VerifyChain(entries) == 2, "broken link must be detected");
        }

        public void Test_Chain_MissingManifestIsEmpty()
        {
            string dir = TempDir("miss");
            try
            {
                var entries = Revisions.ReadManifest(Path.Combine(dir, "nope.jsonl"));
                Assert(entries.Count == 0, "missing manifest must read as empty");
                Assert(Revisions.VerifyChain(entries) == -1, "empty chain is valid");
            }
            finally { Cleanup(dir); }
        }

        public void Test_Chain_RobustAgainstGarbageLines()
        {
            string dir = TempDir("garbage");
            try
            {
                string manifest = Path.Combine(dir, "history.jsonl");
                File.WriteAllText(manifest, "not json\n{\"file\":\"a\"}\n\n" + RevisionEntryLine("ok.dwg"));
                var entries = Revisions.ReadManifest(manifest);
                Assert(entries.Count == 2, "garbage must be skipped, not thrown on: " + entries.Count);
            }
            finally { Cleanup(dir); }
        }

        private static string RevisionEntryLine(string file)
        {
            var e = new RevisionEntry
            {
                FileName = file, Stamp = "t", User = "u", Message = "m", SizeBytes = 1,
                Sha256 = "s", PrevHash = new string('0', 64)
            };
            e.Hash = Revisions.ChainHash(e);
            return e.ToJsonLine();
        }

        public void Test_Entry_JsonEscapingRoundTrip()
        {
            var e = new RevisionEntry
            {
                FileName = "a.dwg", Stamp = "t", User = "lab",
                Message = "кавычка \" слэш \\ таб\t перевод\nстроки",
                SizeBytes = 42, Sha256 = "ff", PrevHash = new string('0', 64)
            };
            e.Hash = Revisions.ChainHash(e);
            var back = RevisionEntry.TryParse(e.ToJsonLine());
            Assert(back != null, "line did not parse");
            AssertEqual(e.Message, back.Message, "message escaped incorrectly");
            AssertEqual(e.FileName, back.FileName, "file");
            Assert(back.SizeBytes == 42, "size");
        }

        // ------------------------------------------------------------ retention

        public void Test_Retention_KeepsNewestRawFiles()
        {
            string dir = TempDir("ret");
            try
            {
                var files = new List<FileInfo>();
                for (int i = 0; i < 20; i++)
                {
                    string p = Path.Combine(dir, "snap" + i + ".dwg");
                    File.WriteAllText(p, "x" + i);
                    var fi = new FileInfo(p);
                    fi.LastWriteTimeUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i);
                    files.Add(fi);
                }
                var plan = Revisions.PlanRetention(files, keepRaw: 5, maxBatch: 100);
                Assert(plan.Kept.Count == 5, "kept " + plan.Kept.Count);
                Assert(plan.ToArchive.Count == 15, "archive " + plan.ToArchive.Count);
                Assert(plan.Kept.All(f => f.Name != plan.ToArchive[0].Name), "a file cannot be both kept and archived");
                Assert(plan.Kept.Select(f => f.Name).Contains("snap19.dwg"), "newest must be kept");
            }
            finally { Cleanup(dir); }
        }

        public void Test_Retention_BatchIsBounded()
        {
            string dir = TempDir("ret2");
            try
            {
                var files = new List<FileInfo>();
                for (int i = 0; i < 500; i++)
                {
                    string p = Path.Combine(dir, "s" + i + ".dwg");
                    File.WriteAllText(p, "x");
                    files.Add(new FileInfo(p));
                }
                var plan = Revisions.PlanRetention(files, keepRaw: 10, maxBatch: 7);
                Assert(plan.ToArchive.Count == 7, "batch cap violated: " + plan.ToArchive.Count);
            }
            finally { Cleanup(dir); }
        }

        public void Test_Retention_PlanIsFastOnLargeHistory()
        {
            string dir = TempDir("ret3");
            try
            {
                var files = new List<FileInfo>();
                for (int i = 0; i < 3000; i++)
                    files.Add(new FileInfo(Path.Combine(dir, "s" + i + ".dwg")));
                var sw = Stopwatch.StartNew();
                var plan = Revisions.PlanRetention(files, keepRaw: 12, maxBatch: 50);
                sw.Stop();
                Assert(plan.ToArchive.Count == 0, "non-existent files must not be planned");
                Assert(sw.ElapsedMilliseconds < 1500, "retention scan too slow: " + sw.ElapsedMilliseconds + " ms");
            }
            finally { Cleanup(dir); }
        }

        // ------------------------------------------------------------ archive

        public void Test_Archive_RoundTripAndDelete()
        {
            string dir = TempDir("zip");
            try
            {
                string hist = Path.Combine(dir, "history");
                Directory.CreateDirectory(hist);
                // DWG-like payload: mostly repetitive, a little entropy
                var payload = new byte[200 * 1024];
                for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
                var noise = new byte[2048];
                new Random(7).NextBytes(noise);
                Array.Copy(noise, 0, payload, 4096, noise.Length);
                var files = new List<FileInfo>();
                for (int i = 0; i < 3; i++)
                {
                    string p = Path.Combine(hist, "snap" + i + ".dwg");
                    File.WriteAllBytes(p, payload);
                    files.Add(new FileInfo(p));
                }
                string zip = Path.Combine(dir, "archive", "2025-01.zip");
                var result = Revisions.Archive(zip, files);

                Assert(result.Archived.Count == 3, "archived " + result.Archived.Count);
                Assert(result.Deleted == 3, "deleted " + result.Deleted);
                Assert(files.All(f => !f.Exists), "originals must be gone");
                Assert(result.BytesFreed == 3L * payload.Length, "bytes freed");
                Assert(new FileInfo(zip).Length < result.BytesFreed / 10, "random payload should compress well");

                string restored = Path.Combine(dir, "restored.dwg");
                Assert(Revisions.Extract(zip, "snap1.dwg", restored), "extract failed");
                var back = File.ReadAllBytes(restored);
                Assert(back.Length == payload.Length, "length after extract");
                for (int i = 0; i < back.Length; i++)
                    if (back[i] != payload[i]) throw new Exception("byte mismatch at " + i);
            }
            finally { Cleanup(dir); }
        }

        public void Test_Archive_KeepsFileWhenArchiveEntryMissing()
        {
            string dir = TempDir("zip2");
            try
            {
                string p = Path.Combine(dir, "keep.dwg");
                File.WriteAllText(p, "data");
                var ghost = new FileInfo(Path.Combine(dir, "ghost.dwg"));
                var result = Revisions.Archive(Path.Combine(dir, "a.zip"), new[] { new FileInfo(p), ghost });
                Assert(result.Archived.Count == 1, "only the real file is archived");
                Assert(File.Exists(p) == false, "archived file is removed");
                Assert(result.Failed.Count == 0, "missing source is not a failure");
            }
            finally { Cleanup(dir); }
        }

        public void Test_Archive_FindInArchives()
        {
            string dir = TempDir("zip3");
            try
            {
                string hist = Path.Combine(dir, "history");
                Directory.CreateDirectory(hist);
                string older = Path.Combine(dir, "2024-12.zip");
                string newer = Path.Combine(dir, "2025-01.zip");
                File.WriteAllText(Path.Combine(hist, "a.dwg"), "a");
                Revisions.Archive(older, new[] { new FileInfo(Path.Combine(hist, "a.dwg")) });
                File.WriteAllText(Path.Combine(hist, "b.dwg"), "b");
                Revisions.Archive(newer, new[] { new FileInfo(Path.Combine(hist, "b.dwg")) });

                AssertEqual(newer, Revisions.FindInArchives(dir, "b.dwg"), "b must be in the newer archive");
                AssertEqual(older, Revisions.FindInArchives(dir, "a.dwg"), "a must be in the older archive");
                Assert(Revisions.FindInArchives(dir, "zzz.dwg") == null, "unknown entry must return null");
            }
            finally { Cleanup(dir); }
        }

        public void Test_Sha256_MatchesKnownVector()
        {
            AssertEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                        Revisions.Sha256OfText(""), "sha256 of empty string");
            AssertEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                        Revisions.Sha256OfText("abc"), "sha256 of 'abc'");
        }
    }
}
