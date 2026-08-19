using System;
using System.Collections.Generic;
using System.Linq;

namespace El.Core
{
    /// <summary>Снимок топологии: список цепей, каждая — набор подписей.</summary>
    public sealed class TopologySnapshot
    {
        public List<List<string>> Chains { get; } = new List<List<string>>();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string SourceFile { get; set; } = "";
        public int LineCount { get; set; }
    }

    public static class SnapshotSerializer
    {
        /// <summary>Текстовый формат, совместимый с LISP-версией (CHAIN: "txt" "txt").</summary>
        public static string Serialize(TopologySnapshot snap)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(";; EL-DIFF snapshot");
            sb.AppendLine(";; File: " + snap.SourceFile);
            sb.AppendLine(";; Date: " + snap.CreatedAt.ToString("yyyy.MM.dd HH:mm:ss"));
            sb.AppendLine(";; Lines: " + snap.LineCount);
            sb.AppendLine(";; Chains: " + snap.Chains.Count);
            foreach (var ch in snap.Chains)
            {
                sb.Append("CHAIN: ");
                foreach (var t in ch)
                {
                    var esc = t.Replace("\"", "\"\"");
                    sb.Append('"').Append(esc).Append("\" ");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static TopologySnapshot Deserialize(string text)
        {
            var snap = new TopologySnapshot();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith(";; Lines:"))
                {
                    int.TryParse(line.Substring(";; Lines:".Length).Trim(), out int n);
                    snap.LineCount = n;
                    continue;
                }
                if (line.StartsWith(";;")) continue;
                if (line.StartsWith("CHAIN:"))
                {
                    var body = line.Substring("CHAIN:".Length);
                    var texts = new List<string>();
                    int i = 0;
                    while (i < body.Length)
                    {
                        if (body[i] != '"') { i++; continue; }
                        int j = i + 1;
                        var sb = new System.Text.StringBuilder();
                        while (j < body.Length)
                        {
                            if (body[j] == '"')
                            {
                                if (j + 1 < body.Length && body[j + 1] == '"') { sb.Append('"'); j += 2; continue; }
                                break;
                            }
                            sb.Append(body[j]);
                            j++;
                        }
                        texts.Add(sb.ToString());
                        i = j + 1;
                    }
                    snap.Chains.Add(texts);
                }
                else if (line.StartsWith(";; Lines:"))
                {
                    int.TryParse(line.Substring(";; Lines:".Length).Trim(), out int n);
                    snap.LineCount = n;
                }
            }
            return snap;
        }
    }

    public static class TopologyDiff
    {
        /// <summary>Оценка совпадения цепей по множеству подписей (0..1).</summary>
        public static double MatchScore(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            int max = Math.Max(a.Count, b.Count);
            if (max == 0) return 0;
            int common = a.Intersect(b).Count();
            return (double)common / max;
        }

        public static List<string> Signature(IReadOnlyList<string> texts)
        {
            var s = new List<string>(texts);
            s.Sort(StringComparer.Ordinal);
            return s;
        }

        public sealed class DiffResult
        {
            public List<List<string>> Added { get; } = new List<List<string>>();
            public List<List<string>> Removed { get; } = new List<List<string>>();
            public List<Tuple<List<string>, List<string>>> Changed { get; } = new List<Tuple<List<string>, List<string>>>();
        }

        public static DiffResult Compare(TopologySnapshot snap, TopologySnapshot current)
        {
            var res = new DiffResult();
            const double threshold = 0.5;

            foreach (var sch in snap.Chains)
            {
                List<string> match = null;
                foreach (var cch in current.Chains)
                {
                    if (MatchScore(Signature(sch), Signature(cch)) >= threshold)
                        match = cch;
                }
                if (match == null)
                {
                    res.Removed.Add(sch);
                }
                else
                {
                    var s1 = Signature(sch);
                    var s2 = Signature(match);
                    if (!s1.SequenceEqual(s2))
                        res.Changed.Add(Tuple.Create(sch, match));
                }
            }

            foreach (var cch in current.Chains)
            {
                bool found = false;
                foreach (var sch in snap.Chains)
                {
                    if (MatchScore(Signature(sch), Signature(cch)) >= threshold) { found = true; break; }
                }
                if (!found) res.Added.Add(cch);
            }
            return res;
        }
    }
}
