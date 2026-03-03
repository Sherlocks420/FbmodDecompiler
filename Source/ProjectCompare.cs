
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FrostySdk.IO;

namespace FbmodDecompiler
{
    internal static class ProjectCompare
    {
        internal sealed class ProjectSummary
        {
            public string Path = "";
            public long FileSize;
            public ulong Magic;
            public uint Version;
            public string Profile = "";
            public long Created;
            public long Modified;
            public string Title = "";
            public string Author = "";
            public string Category = "";
            public string ProjectVersion = "";
            public int SuperbundleCount;
            public int BundleCount;
            public int AddedEbxCount;
            public int AddedResCount;
            public int AddedChunkCount;
            public int ChunkMarkerCount;
            public int ResMarkerCount;
            public string Sha256Hex = "";

            public List<(string Name, Guid Guid)> AddedEbx = new();
            public List<AddedResEntry> AddedRes = new();
            public List<(Guid Guid, uint Extra)> AddedChunk = new();
        }

        internal sealed class AddedResEntry
        {
            public string Name = "";
            public ulong Rid;
            public uint Type;
            public byte[] Meta = Array.Empty<byte>();
            public uint? MetaLenField;
        }

        public static string GenerateReport(string realPath, string genPath)
        {
            var sb = new StringBuilder();

            if (!File.Exists(realPath))
                return $"ERROR: Real project not found: {realPath}";
            if (!File.Exists(genPath))
                return $"ERROR: Generated project not found: {genPath}";

            var real = ReadSummary(realPath);
            var gen = ReadSummary(genPath);

            sb.AppendLine("FBPROJECT DIFF REPORT");
            sb.AppendLine($"Real      : {real.Path}");
            sb.AppendLine($"Generated : {gen.Path}");
            sb.AppendLine();

            sb.AppendLine("Sizes & hashes");
            sb.AppendLine($"  Real size      : {real.FileSize:n0} bytes");
            sb.AppendLine($"  Generated size : {gen.FileSize:n0} bytes");
            sb.AppendLine($"  Delta          : {(gen.FileSize - real.FileSize):n0} bytes");
            sb.AppendLine($"  Real SHA-256   : {real.Sha256Hex}");
            sb.AppendLine($"  Gen  SHA-256   : {gen.Sha256Hex}");
            sb.AppendLine();

            sb.AppendLine("Header");
            sb.AppendLine($"  magic   : 0x{real.Magic:X16} vs 0x{gen.Magic:X16}");
            sb.AppendLine($"  version : {real.Version} vs {gen.Version}");
            sb.AppendLine($"  profile : '{real.Profile}' vs '{gen.Profile}'");
            sb.AppendLine($"  created : {real.Created} vs {gen.Created}");
            sb.AppendLine($"  modified: {real.Modified} vs {gen.Modified}");
            sb.AppendLine($"  title   : '{real.Title}' vs '{gen.Title}'");
            sb.AppendLine($"  author  : '{real.Author}' vs '{gen.Author}'");
            sb.AppendLine();

            sb.AppendLine("Marker counts (entire file scan)");
            sb.AppendLine($"  'chunk\\0' : {real.ChunkMarkerCount} vs {gen.ChunkMarkerCount}");
            sb.AppendLine($"  'res\\0'   : {real.ResMarkerCount} vs {gen.ResMarkerCount}");
            sb.AppendLine();

            sb.AppendLine("Counts (parsed up to Added tables)");
            sb.AppendLine($"  superbundles : {real.SuperbundleCount} vs {gen.SuperbundleCount}");
            sb.AppendLine($"  bundles      : {real.BundleCount} vs {gen.BundleCount}");
            sb.AppendLine($"  added ebx    : {real.AddedEbxCount} vs {gen.AddedEbxCount}");
            sb.AppendLine($"  added res    : {real.AddedResCount} vs {gen.AddedResCount}");
            sb.AppendLine($"  added chunk  : {real.AddedChunkCount} vs {gen.AddedChunkCount}");
            sb.AppendLine();

            sb.AppendLine("Duplicate checks (Generated)");
            AppendAddedResDupes(sb, gen);
            AppendAddedChunkDupes(sb, gen);
            sb.AppendLine();

            long firstDiff = FindFirstDiffOffset(realPath, genPath);
            sb.AppendLine("Binary diff");
            if (firstDiff < 0)
            {
                sb.AppendLine("  Files are identical byte-for-byte (unexpected).");
            }
            else
            {
                sb.AppendLine($"  First differing offset: 0x{firstDiff:X} ({firstDiff:n0})");
                sb.AppendLine("  Context (hex) around first diff:");
                sb.AppendLine(DumpHexContext(realPath, genPath, firstDiff, 64));
            }

            sb.AppendLine();
            sb.AppendLine("Sample Added RES entries (first 5 each)");
            AppendResSamples(sb, real, gen);

            sb.AppendLine();
            sb.AppendLine("Sample Added CHUNK entries (first 5 each)");
            AppendChunkSamples(sb, real, gen);

            return sb.ToString();
        }

        private static void AppendResSamples(StringBuilder sb, ProjectSummary real, ProjectSummary gen)
        {
            sb.AppendLine("  Real:");
            foreach (var e in real.AddedRes.Take(5))
            {
                sb.AppendLine($"    {e.Name} rid=0x{e.Rid:X16} type=0x{e.Type:X8} metaLen={(e.MetaLenField.HasValue ? e.MetaLenField.Value.ToString() : "fixed")} metaBytes={e.Meta.Length}");
            }
            sb.AppendLine("  Generated:");
            foreach (var e in gen.AddedRes.Take(5))
            {
                sb.AppendLine($"    {e.Name} rid=0x{e.Rid:X16} type=0x{e.Type:X8} metaLen={(e.MetaLenField.HasValue ? e.MetaLenField.Value.ToString() : "fixed")} metaBytes={e.Meta.Length}");
            }
        }

        private static void AppendChunkSamples(StringBuilder sb, ProjectSummary real, ProjectSummary gen)
        {
            sb.AppendLine("  Real:");
            foreach (var e in real.AddedChunk.Take(5))
            {
                sb.AppendLine($"    {e.Guid} extra=0x{e.Extra:X8}");
            }
            sb.AppendLine("  Generated:");
            foreach (var e in gen.AddedChunk.Take(5))
            {
                sb.AppendLine($"    {e.Guid} extra=0x{e.Extra:X8}");
            }
        }

        private static void AppendAddedResDupes(StringBuilder sb, ProjectSummary gen)
        {
            if (gen.AddedRes.Count == 0)
            {
                sb.AppendLine("  Added RES: none parsed.");
                return;
            }

            int dupName = gen.AddedRes.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Count(g => g.Count() > 1);
            int dupRid = gen.AddedRes.GroupBy(r => r.Rid).Count(g => g.Count() > 1);
            int dupRidType = gen.AddedRes.GroupBy(r => (r.Rid, r.Type)).Count(g => g.Count() > 1);

            sb.AppendLine($"  Added RES duplicates by name    : {dupName}");
            sb.AppendLine($"  Added RES duplicates by RID     : {dupRid}");
            sb.AppendLine($"  Added RES duplicates by RID+type: {dupRidType}");

            var worst = gen.AddedRes
                .GroupBy(r => (r.Rid, r.Type))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (worst != null && worst.Count() > 1)
            {
                sb.AppendLine($"  Example duplicate RID+type: rid=0x{worst.Key.Rid:X16} type=0x{worst.Key.Type:X8} count={worst.Count()}");
                foreach (var e in worst.Take(3))
                    sb.AppendLine($"    - {e.Name}");
            }
        }

        private static void AppendAddedChunkDupes(StringBuilder sb, ProjectSummary gen)
        {
            if (gen.AddedChunk.Count == 0)
            {
                sb.AppendLine("  Added CHUNK: none parsed.");
                return;
            }

            int dupGuid = gen.AddedChunk.GroupBy(c => c.Guid).Count(g => g.Count() > 1);
            sb.AppendLine($"  Added CHUNK duplicates by GUID: {dupGuid}");
        }

        private static ProjectSummary ReadSummary(string path)
        {
            var s = new ProjectSummary();
            s.Path = path;
            s.FileSize = new FileInfo(path).Length;
            s.Sha256Hex = ComputeSha256(path);

            (s.ChunkMarkerCount, s.ResMarkerCount) = CountMarkers(path);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new NativeReader(fs);

            s.Magic = reader.ReadULong();
            s.Version = reader.ReadUInt();
            s.Profile = reader.ReadNullTerminatedString();
            s.Created = reader.ReadLong();
            s.Modified = reader.ReadLong();
            reader.ReadUInt();
            s.Title = reader.ReadNullTerminatedString();
            s.Author = reader.ReadNullTerminatedString();
            s.Category = reader.ReadNullTerminatedString();
            s.ProjectVersion = reader.ReadNullTerminatedString();
            _ = reader.ReadNullTerminatedString();

            int iconLen = reader.ReadInt();
            if (iconLen > 0) reader.ReadBytes(iconLen);
            for (int i = 0; i < 4; i++)
            {
                int sl = reader.ReadInt();
                if (sl > 0) reader.ReadBytes(sl);
            }

            s.SuperbundleCount = ReadCountSafe(reader, fs, "SuperbundleCount", max: 100000, minBytesPerEntry: 1);

            s.BundleCount = ReadCountSafe(reader, fs, "BundleCount", max: 200000, minBytesPerEntry: 8);
            for (int i = 0; i < s.BundleCount; i++)
            {
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();
                reader.ReadInt();
                if (!s.Profile.Equals("starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase))
                    reader.ReadBoolean();
            }

            s.AddedEbxCount = ReadCountSafe(reader, fs, "AddedEbxCount", max: 500000, minBytesPerEntry: 20);
            for (int i = 0; i < s.AddedEbxCount; i++)
            {
                string n = reader.ReadNullTerminatedString();
                Guid g = reader.ReadGuid();
                s.AddedEbx.Add((n, g));
            }

            s.AddedResCount = ReadCountSafe(reader, fs, "AddedResCount", max: 500000, minBytesPerEntry: 32);
            for (int i = 0; i < s.AddedResCount; i++)
            {
                var e = new AddedResEntry();
                e.Name = reader.ReadNullTerminatedString();
                e.Rid = reader.ReadULong();
                e.Type = reader.ReadUInt();

                if (s.Profile.Equals("starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase))
                {
                    e.MetaLenField = null;
                    e.Meta = reader.ReadBytes(16);
                }
                else
                {

                    long p = reader.Position;
                    uint maybeLen = reader.ReadUInt();
                    reader.Position = p;

                    if (maybeLen > 0 && maybeLen <= 1024 && (reader.Position + 4 + maybeLen) <= fs.Length)
                    {
                        e.MetaLenField = maybeLen;
                        uint ml = reader.ReadUInt();
                        e.Meta = ml > 0 ? reader.ReadBytes((int)ml) : Array.Empty<byte>();
                    }
                    else
                    {
                        e.MetaLenField = null;
                        e.Meta = reader.ReadBytes(16);
                    }
                }

s.AddedRes.Add(e);
            }

            s.AddedChunkCount = ReadCountSafe(reader, fs, "AddedChunkCount", max: 500000, minBytesPerEntry: 20);
            for (int i = 0; i < s.AddedChunkCount; i++)
            {
                Guid g = reader.ReadGuid();
                uint extra = reader.ReadUInt();
                s.AddedChunk.Add((g, extra));
            }

            return s;
        }

        private static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = sha.ComputeHash(fs);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static (int chunkCount, int resCount) CountMarkers(string path)
        {
            int chunk = 0, res = 0;
            byte[] mChunk = Encoding.ASCII.GetBytes("chunk\0");
            byte[] mRes = Encoding.ASCII.GetBytes("res\0");

            const int bufSize = 1024 * 1024;
            byte[] buffer = new byte[bufSize + 8];
            int overlap = 4;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int read;
            int carry = 0;
            while ((read = fs.Read(buffer, carry, bufSize)) > 0)
            {
                int len = carry + read;
                chunk += CountOccurrences(buffer, len, mChunk);
                res += CountOccurrences(buffer, len, mRes);

                carry = Math.Min(overlap, len);
                Array.Copy(buffer, len - carry, buffer, 0, carry);
            }

            return (chunk, res);
        }

        private static int CountOccurrences(byte[] buffer, int len, byte[] pattern)
        {
            int count = 0;
            for (int i = 0; i <= len - pattern.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j]) { ok = false; break; }
                }
                if (ok) count++;
            }
            return count;
        }

        private static long FindFirstDiffOffset(string a, string b)
        {
            const int buf = 1024 * 1024;
            byte[] ba = new byte[buf];
            byte[] bb = new byte[buf];

            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);

            long offset = 0;
            while (true)
            {
                int ra = fa.Read(ba, 0, buf);
                int rb = fb.Read(bb, 0, buf);
                int r = Math.Min(ra, rb);
                if (r == 0)
                {
                    if (ra == rb) return -1;
                    return offset;
                }

                for (int i = 0; i < r; i++)
                {
                    if (ba[i] != bb[i])
                        return offset + i;
                }

                offset += r;
            }
        }

        private static string DumpHexContext(string a, string b, long offset, int radius)
        {
            long start = Math.Max(0, offset - radius);
            int len = radius * 2;

            byte[] ra = ReadSlice(a, start, len);
            byte[] rb = ReadSlice(b, start, len);

            var sb = new StringBuilder();
            sb.AppendLine($"  Offset base: 0x{start:X}");
            sb.AppendLine("  REAL:");
            sb.AppendLine("  " + BitConverter.ToString(ra).Replace("-", " "));
            sb.AppendLine("  GEN :");
            sb.AppendLine("  " + BitConverter.ToString(rb).Replace("-", " "));
            return sb.ToString();
        }

        private static int ReadCountSafe(NativeReader reader, FileStream fs, string label, int max, int minBytesPerEntry)
        {
            int raw = reader.ReadInt();
            if (raw <= 0) return 0;

            long remaining = fs.Length - reader.Position;
            if (remaining <= 0) return 0;

            long maxByRemaining = minBytesPerEntry <= 0 ? raw : (remaining / minBytesPerEntry);
            long capped = raw;

            if (capped > maxByRemaining) capped = maxByRemaining;
            if (capped > max) capped = max;
            if (capped < 0) capped = 0;

            if (capped != raw)
            {
                try { s_debugNotes.Add($"{label}: raw={raw} clamped={capped} (remaining={remaining})"); } catch { }
            }

            return (int)capped;
        }

        private static readonly List<string> s_debugNotes = new List<string>();

        private static byte[] ReadSlice(string path, long offset, int len)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = offset;
            byte[] buf = new byte[len];
            int r = fs.Read(buf, 0, len);
            if (r < len) Array.Resize(ref buf, r);
            return buf;
        }
    }
}
