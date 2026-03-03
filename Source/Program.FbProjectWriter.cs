using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using Frosty.Core;
using FrostyApp = Frosty.Core.App;

namespace FbmodDecompiler
{
    internal partial class Program
    {
        internal enum FbProjectWriterBackend
        {
            FrostyCore = 0,
            Custom = 1,
        }

        internal static FbProjectWriterBackend SelectedWriterBackend = FbProjectWriterBackend.Custom;

        internal static List<Guid> TryExtractChunkRefsFromOpaqueEbx(byte[] ebxData, List<ChunkResourceData> chunkResources)
        {
            if (ebxData == null || ebxData.Length < 16 || chunkResources == null || chunkResources.Count == 0)
                return null;

            var refs = new List<Guid>();

            foreach (var c in chunkResources)
            {
                if (c == null) continue;

                byte[] patLE = c.Id.ToByteArray();
                if (IndexOfBytes(ebxData, patLE, 0) >= 0)
                {
                    refs.Add(c.Id);
                    continue;
                }

                byte[] patBE = GuidToBigEndianBytes(c.Id);
                if (patBE != null && IndexOfBytes(ebxData, patBE, 0) >= 0)
                    refs.Add(c.Id);
            }

            if (refs.Count > 64)
                refs = refs.Take(64).ToList();

            return refs;
        }

internal static List<Guid> TryExtractChunkRefsFromOpaqueRes(byte[] resMeta, byte[] resData, List<ChunkResourceData> chunkResources)
{
    if (chunkResources == null || chunkResources.Count == 0)
        return null;

    byte[] hayMeta = resMeta ?? Array.Empty<byte>();
    byte[] hayData = resData ?? Array.Empty<byte>();

    var sigMap = new Dictionary<uint, List<(byte[] pat, Guid id)>>(chunkResources.Count * 2);
    foreach (var c in chunkResources)
    {
        if (c == null || c.Id == Guid.Empty) continue;
        var le = c.Id.ToByteArray();
        var be = GuidToBigEndianBytes(c.Id);
        if (le != null && le.Length == 16)
        {
            uint sig = BitConverter.ToUInt32(le, 0);
            if (!sigMap.TryGetValue(sig, out var list)) sigMap[sig] = list = new List<(byte[], Guid)>(2);
            list.Add((le, c.Id));
        }
        if (be != null && be.Length == 16)
        {
            uint sig = BitConverter.ToUInt32(be, 0);
            if (!sigMap.TryGetValue(sig, out var list)) sigMap[sig] = list = new List<(byte[], Guid)>(2);
            list.Add((be, c.Id));
        }
    }

    var refs = new List<(Guid id, int pos)>();

    ScanForGuidRefs(sigMap, hayMeta, 0, hayMeta.Length, 0, refs);
    int dataScanLen = Math.Min(hayData.Length, 256 * 1024);
    ScanForGuidRefs(sigMap, hayData, 0, dataScanLen, 0x10000000, refs);

    if (refs.Count == 0)
    {
        var h32ToGuid = new Dictionary<uint, Guid>();
        foreach (var c in chunkResources)
        {
            if (c == null || c.Id == Guid.Empty) continue;

            uint h32 = unchecked((uint)c.H32);
            if (h32 == 0) continue;
            if (!h32ToGuid.ContainsKey(h32))
                h32ToGuid[h32] = c.Id;
        }

        if (h32ToGuid.Count > 0)
        {

            ScanForH32Refs(h32ToGuid, hayMeta, 0, hayMeta.Length, 0, refs);
            ScanForH32Refs(h32ToGuid, hayData, 0, Math.Min(hayData.Length, 32 * 1024), 0x20000000, refs);
        }
    }

    if (refs.Count == 0) return null;

    return refs
        .OrderBy(t => t.pos)
        .Select(t => t.id)
        .Distinct()
        .Take(128)
        .ToList();
}

private static void ScanForH32Refs(
    Dictionary<uint, Guid> h32ToGuid,
    byte[] hay,
    int start,
    int length,
    int posBias,
    List<(Guid id, int pos)> output)
{
    if (hay == null || hay.Length < 4 || length < 4) return;
    int end = Math.Min(hay.Length, start + length);
    int limit = end - 4;
    for (int i = start; i <= limit; i++)
    {
        uint v = BitConverter.ToUInt32(hay, i);
        if (v != 0 && h32ToGuid.TryGetValue(v, out var id))
            output.Add((id, posBias + i));
    }
}

private static List<Guid> TryGuessChunkRefsByBundle(ResResourceData res, List<ChunkResourceData> allChunks)
{
    if (res == null || allChunks == null || allChunks.Count == 0)
        return null;

    bool plausible = res.ResType == 0x6BDE20BAu
                 || res.ResType == 0xD8F5DAAFu
                 || res.ResType == 0x49B156D4u
                 || (res.ResMeta != null && res.ResMeta.Length == 16);
    if (!plausible)
        return null;

    var refs = new List<Guid>();

    if (res.AddedBundles != null && res.AddedBundles.Count > 0)
    {
        var bset = new HashSet<int>(res.AddedBundles);
        foreach (var ch in allChunks)
        {
            if (ch == null || ch.Id == Guid.Empty)
                continue;
            if (ch.AddedBundles == null || ch.AddedBundles.Count == 0)
                continue;

            bool match = false;
            foreach (int h in ch.AddedBundles)
            {
                if (bset.Contains(h)) { match = true; break; }
            }
            if (!match)
                continue;

            refs.Add(ch.Id);
            if (refs.Count >= 64)
                break;
        }

        if (refs.Count > 0)
            return refs;
    }

    foreach (var ch in allChunks)
    {
        if (ch == null || ch.Id == Guid.Empty) continue;
        if (!ch.AddToChunkBundle) continue;
        refs.Add(ch.Id);
        if (refs.Count >= 8) break;
    }

    return refs.Count > 0 ? refs : null;
}

private static void ScanForGuidRefs(
    Dictionary<uint, List<(byte[] pat, Guid id)>> sigMap,
    byte[] hay,
    int start,
    int length,
    int posBias,
    List<(Guid id, int pos)> output)
{
    if (hay == null || hay.Length < 16 || length < 16) return;
    int end = Math.Min(hay.Length, start + length);
    int limit = end - 16;

    for (int i = start; i <= limit; i++)
    {
        uint sig = BitConverter.ToUInt32(hay, i);
        if (!sigMap.TryGetValue(sig, out var candidates))
            continue;

        foreach (var c in candidates)
        {
            var pat = c.pat;
            bool ok = true;
            for (int j = 0; j < 16; j++)
            {
                if (hay[i + j] != pat[j]) { ok = false; break; }
            }
            if (ok)
                output.Add((c.id, posBias + i));
        }
    }
}

internal static byte[] GuidToBigEndianBytes(Guid g)
        {

            byte[] b = g.ToByteArray();
            if (b == null || b.Length != 16) return null;
            return new byte[]
            {
                b[3], b[2], b[1], b[0],
                b[5], b[4],
                b[7], b[6],
                b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]
            };
        }

        internal static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            if (haystack == null || needle == null || needle.Length == 0) return -1;
            if (start < 0) start = 0;
            int limit = haystack.Length - needle.Length;
            for (int i = start; i <= limit; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }

        internal static int HashStringFNV1a(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;

            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            uint hash = offsetBasis;

            foreach (byte b in Encoding.UTF8.GetBytes(s.ToLowerInvariant()))
            {
                hash ^= b;
                hash *= prime;
            }

            return unchecked((int)hash);
        }

        private static List<string> ResolveBundleNamesForWrite(List<int> bundleHashes)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (bundleHashes != null)
            {
                foreach (int h in bundleHashes)
                {
                    if (bundleHashToName.TryGetValue(h, out string bn) && !string.IsNullOrWhiteSpace(bn) && seen.Add(bn))
                        names.Add(bn);
                }
            }
            return names;
        }

        private static bool IsBundleAnchorEbx(string ebxName, string bundleName)
        {
            if (string.IsNullOrWhiteSpace(ebxName) || string.IsNullOrWhiteSpace(bundleName))
                return false;

            string b = bundleName.Trim();
            if (b.StartsWith("win32/", StringComparison.OrdinalIgnoreCase))
                b = b.Substring("win32/".Length);

            string e = (ebxName ?? string.Empty).Trim();
            e = e.Replace('\\', '/');

            return string.Equals(e, b, StringComparison.OrdinalIgnoreCase);
        }
internal enum ChunkRecordLayout
        {

            Legacy = 0,

            WithLinked = 1,

            WithLinkedAndSizes = 2,

            WithLinkedAndSizesAndSecondBundleCount = 3,
        }

        internal static ChunkRecordLayout ChunkLayout = ChunkRecordLayout.WithLinked;

        internal static ChunkRecordLayout ParseChunkLayout(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ChunkLayout;

            if (int.TryParse(value, out int n))
            {
                if (Enum.IsDefined(typeof(ChunkRecordLayout), n))
                    return (ChunkRecordLayout)n;
                return ChunkLayout;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "legacy":
                case "v0":
                case "0":
                    return ChunkRecordLayout.Legacy;

                case "linked":
                case "withlinked":
                case "v1":
                case "1":
                    return ChunkRecordLayout.WithLinked;

                case "linked+s":
                case "linked+sizes":
                case "withlinkedandsizes":
                case "v2":
                case "2":
                    return ChunkRecordLayout.WithLinkedAndSizes;

                case "linked+s+bc2":
                case "linked+sizes+bundlecount2":
                case "withlinkedandsizesandsecondbundlecount":
                case "v3":
                case "3":
                    return ChunkRecordLayout.WithLinkedAndSizesAndSecondBundleCount;

                default:
                    return ChunkLayout;
            }
        }

        internal static bool TryWriteFbprojectViaFrostyCore(
            string outputPath,
            string title,
            string author,
            string category,
            string version,
            string description)
        {

            return false;
        }

        internal static void WriteFbproject(string outputPath, string title, string author, string category, string version, string description, string link)
        {
            string profileName = Program.CurrentProfileKey ?? "";
            long now = DateTime.UtcNow.Ticks;

            uint gameVersion = 0;
            try { gameVersion = Program.CurrentGameVersion; } catch { }

            static string NormName(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                return s.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            }

            static string DeriveEbxFromMeshResNorm(string resNorm)
            {
                if (string.IsNullOrWhiteSpace(resNorm))
                    return null;

                const string s1 = "_mesh_mesh/blocks";
                const string s2 = "_mesh/blocks";
                const string s3 = "_mesh";

                // resNorm is already normalized lowercase
                if (resNorm.EndsWith(s1, StringComparison.Ordinal))
                    return resNorm.Substring(0, resNorm.Length - s1.Length);
                if (resNorm.EndsWith(s2, StringComparison.Ordinal))
                    return resNorm.Substring(0, resNorm.Length - s2.Length);
                if (resNorm.EndsWith(s3, StringComparison.Ordinal))
                    return resNorm.Substring(0, resNorm.Length - s3.Length);
                return null;
            }

            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new NativeWriter(fs))
            {

                writer.Write(FBPROJECT_MAGIC);
                writer.Write(FBPROJECT_VERSION);
                writer.WriteNullTerminatedString(profileName);
                writer.Write(now);
                writer.Write(now);
                writer.Write(gameVersion);

                writer.WriteNullTerminatedString(title ?? "");
                writer.WriteNullTerminatedString(author ?? "");
                writer.WriteNullTerminatedString(category ?? "");
                writer.WriteNullTerminatedString(version ?? "");
                writer.WriteNullTerminatedString(description ?? "");

                if (projectIcon != null && projectIcon.Length > 0)
                {
                    writer.Write(projectIcon.Length);
                    writer.Write(projectIcon);
                }
                else
                {
                    writer.Write(0);
                }

                for (int i = 0; i < 4; i++)
                {
                    byte[] buf = null;
                    try
                    {
                        if (projectScreenshots != null && i < projectScreenshots.Count)
                            buf = projectScreenshots[i];
                    }
                    catch { }

                    if (buf != null && buf.Length > 0)
                    {
                        writer.Write(buf.Length);
                        writer.Write(buf);
                    }
                    else
                    {
                        writer.Write(0);
                    }
                }

                writer.Write(0);

                writer.Write(bundleResources?.Count ?? 0);
                if (bundleResources != null)
                {
                    foreach (var b in bundleResources)
                    {
                        writer.WriteNullTerminatedString(b?.Name ?? "");
                        writer.WriteNullTerminatedString(b?.SuperBundleName ?? "");
                        writer.Write(b != null ? b.Type : 0);
                    }
                }

                var addedEbx = (ebxResources ?? new List<EbxResourceData>()).Where(e => e != null && e.IsAdded).ToList();
                writer.Write(addedEbx.Count);
                foreach (var e in addedEbx)
                {
                    writer.WriteNullTerminatedString(e.Name ?? "");
                    Guid g = e.Guid;
                    if (g == Guid.Empty)
                        g = DeterministicGuidFromName(e.Name ?? "");
                    writer.Write(g);
                }

                var addedRes = (resResources ?? new List<ResResourceData>()).Where(r => r != null && r.IsAdded).ToList();
                writer.Write(addedRes.Count);
                foreach (var r in addedRes)
                {
                    writer.WriteNullTerminatedString(r.Name ?? "");
                    writer.Write(r.ResRid);
                    writer.Write(r.ResType);

                    byte[] meta = r.ResMeta ?? Array.Empty<byte>();
                    if (meta.Length != 16)
                    {
                        var tmp = new byte[16];
                        Buffer.BlockCopy(meta, 0, tmp, 0, Math.Min(16, meta.Length));
                        meta = tmp;
                    }
                    writer.Write(meta);
                }

                var addedChunks = (chunkResources ?? new List<ChunkResourceData>()).Where(c => c != null && c.IsAdded).ToList();
                writer.Write(addedChunks.Count);
                foreach (var c in addedChunks)
                {
                    writer.Write(c.Id);
                    writer.Write(c.H32);
                }

                var resByNorm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (resResources != null)
                {
                    foreach (var rr in resResources)
                    {
                        if (rr == null || string.IsNullOrWhiteSpace(rr.Name)) continue;
                        string k = NormName(rr.Name);
                        if (!resByNorm.ContainsKey(k))
                            resByNorm[k] = rr.Name;
                    }
                }

                var ebxBundleAnchorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (bundleResources != null && bundleResources.Count > 0 && ebxResources != null && ebxResources.Count > 0)
                {
                    foreach (var b in bundleResources)
                    {
                        if (b == null || string.IsNullOrWhiteSpace(b.Name))
                            continue;
                        string anchor = b.Name.Trim();
                        if (anchor.StartsWith("win32/", StringComparison.OrdinalIgnoreCase))
                            anchor = anchor.Substring("win32/".Length);

                        EbxResourceData match = null;
                        foreach (var e in ebxResources)
                        {
                            if (e == null || string.IsNullOrWhiteSpace(e.Name))
                                continue;
                            if (string.Equals(e.Name.Replace('\\', '/').Trim(), anchor, StringComparison.OrdinalIgnoreCase))
                            {
                                match = e;
                                break;
                            }
                        }

                        if (match != null)
                        {
                            if (!ebxBundleAnchorMap.ContainsKey(match.Name))
                                ebxBundleAnchorMap[match.Name] = b.Name;
                        }
                        else
                        {
                            var first = ebxResources[0];
                            if (first != null && !string.IsNullOrWhiteSpace(first.Name) && !ebxBundleAnchorMap.ContainsKey(first.Name))
                                ebxBundleAnchorMap[first.Name] = b.Name;
                        }
                    }
                }

                // Some skin/mesh mods only modify RES/CHUNK payloads. In Frosty, those meshes can still be previewed,
                // but the EBX entries may not appear as modified unless they are linked to the modified RES.
                // Create lightweight EBX "link-only" records for likely mesh parents so Data Explorer can show them as indirectly modified.
                var ebxWriteList = new List<EbxResourceData>();
                if (ebxResources != null && ebxResources.Count > 0)
                    ebxWriteList.AddRange(ebxResources);

                try
                {
                    var am = FrostyApp.AssetManager;
                    if (am != null && resResources != null && resResources.Count > 0)
                    {
                        var existing = new HashSet<string>(
                            ebxWriteList
                                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name))
                                .Select(x => NormName(x.Name)),
                            StringComparer.OrdinalIgnoreCase);

                        foreach (var rr in resResources)
                        {
                            if (rr == null || string.IsNullOrWhiteSpace(rr.Name) || rr.Data == null || rr.Data.Length == 0)
                                continue;

                            string rn = NormName(rr.Name);
                            string cand = DeriveEbxFromMeshResNorm(rn);
                            if (string.IsNullOrWhiteSpace(cand))
                                continue;

                            if (existing.Contains(cand))
                                continue;

                            // only add if this EBX exists in the base game database
                            var be = am.GetEbxEntry(cand);
                            if (be == null)
                                continue;

                            ebxWriteList.Add(new EbxResourceData
                            {
                                Name = cand,
                                Data = null,
                                Guid = Guid.Empty,
                                IsAdded = false,
                                HasCustomHandler = false,
                                HandlerHash = 0,
                                UserData = "",
                                AddedBundles = new List<int>()
                            });

                            existing.Add(cand);
                        }
                    }
                }
                catch { }

                writer.Write(ebxWriteList.Count);
                if (ebxWriteList != null)
                {
                    foreach (var e in ebxWriteList)
                    {
                        writer.WriteNullTerminatedString(e?.Name ?? "");

                        var links = new List<(string type, Guid gid, string name)>();

                        bool hasData = e?.Data != null && e.Data.Length > 0;

                        if (e != null)
                        {
                            string k = NormName(e.Name ?? "");
                            if (resByNorm.TryGetValue(k, out string resName) && !string.IsNullOrWhiteSpace(resName))
                                links.Add(("res", Guid.Empty, resName));

                            // Heuristic: link EBX to mesh-like RES that share the same base name.
                            // This helps Frosty mark the EBX entry as indirectly modified.
                            if (!string.IsNullOrWhiteSpace(k) && resResources != null && resResources.Count > 0)
                            {
                                var seenRes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var rr in resResources)
                                {
                                    if (rr == null || string.IsNullOrWhiteSpace(rr.Name))
                                        continue;

                                    string rrNorm = NormName(rr.Name);
                                    if (!rrNorm.Contains("_mesh"))
                                        continue;

                                    if (rrNorm.StartsWith(k + "_", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (seenRes.Add(rr.Name))
                                            links.Add(("res", Guid.Empty, rr.Name));

                                        if (links.Count >= 64)
                                            break;
                                    }
                                }
                            }
                        }

                        if (hasData && e != null && !e.HasCustomHandler)
                        {
                            var chunkRefs = TryExtractChunkRefsFromOpaqueEbx(e.Data, chunkResources);
                            if (chunkRefs != null)
                            {
                                foreach (var g in chunkRefs)
                                    links.Add(("chunk", g, null));
                            }
                        }

                        writer.Write(links.Count);
                        foreach (var l in links)
                        {
                            writer.WriteNullTerminatedString(l.type);
                            if (l.type == "chunk")
                                writer.Write(l.gid);
                            else
                                writer.WriteNullTerminatedString(l.name ?? "");
                        }

                        var ebxBundleNames = new List<string>();
                        if (e != null && ebxBundleAnchorMap != null && ebxBundleAnchorMap.Count > 0)
                        {
                            if (ebxBundleAnchorMap.TryGetValue(e.Name ?? string.Empty, out var bn) && !string.IsNullOrWhiteSpace(bn))
                                ebxBundleNames.Add(bn);
                        }
                        writer.Write(ebxBundleNames.Count);
                        foreach (var bn in ebxBundleNames)
                            writer.WriteNullTerminatedString(bn);

                        writer.Write(hasData);
                        if (hasData)
                        {
                            writer.Write(false);
                            writer.WriteNullTerminatedString(e?.UserData ?? "");
                            writer.Write(e != null && e.HasCustomHandler);
                            writer.Write(e.Data.Length);
                            writer.Write(e.Data);
                        }
                    }
                }

                writer.Write(resResources?.Count ?? 0);
                if (resResources != null)
                {
                    foreach (var r in resResources)
                    {
                        writer.WriteNullTerminatedString(r?.Name ?? "");

                        bool hasData = r?.Data != null && r.Data.Length > 0;

                        List<Guid> chunkRefs = null;

                        try
                        {
                            if (r?.ResMeta != null && r.ResMeta.Length == 16)
                            {
                                var g = new Guid(r.ResMeta);
                                if (g != Guid.Empty && chunkResources != null && chunkResources.Any(c => c != null && c.Id == g))
                                    chunkRefs = new List<Guid> { g };
                            }
                        }
                        catch { }

                        if (chunkRefs == null)
                        {
                            chunkRefs = (hasData && r != null)
                                ? TryExtractChunkRefsFromOpaqueRes(r.ResMeta, r.Data, chunkResources)
                                : null;
                        }

                        if ((chunkRefs == null || chunkRefs.Count == 0) && r != null)
                            chunkRefs = TryGuessChunkRefsByBundle(r, chunkResources);

                        var resRefs = new List<string>();
                        if (r != null)
                        {
                            string baseName = (r.Name ?? "").Replace('\\', '/').TrimStart('/');
                            if (!string.IsNullOrWhiteSpace(baseName))
                            {
                                string blocksCandidate = baseName + "_mesh/blocks";
                                bool exists = false;
                                foreach (var rr in resResources)
                                {
                                    if (rr == null || string.IsNullOrWhiteSpace(rr.Name)) continue;
                                    string rrNorm = rr.Name.Replace('\\', '/').TrimStart('/');
                                    if (string.Equals(rrNorm, blocksCandidate, StringComparison.OrdinalIgnoreCase))
                                    {
                                        exists = true;
                                        break;
                                    }
                                }
                                if (exists)
                                    resRefs.Add(blocksCandidate);
                            }
                        }

                        int linkCount = (chunkRefs != null ? chunkRefs.Count : 0) + resRefs.Count;
                        writer.Write(linkCount);

                        if (chunkRefs != null)
                        {
                            foreach (var g in chunkRefs)
                            {
                                writer.WriteNullTerminatedString("chunk");
                                writer.Write(g);
                            }
                        }

                        foreach (var rn in resRefs)
                        {
                            writer.WriteNullTerminatedString("res");
                            writer.WriteNullTerminatedString(rn);
                        }

                        writer.Write(0);

                        writer.Write(hasData);
                        if (hasData)
                        {
                            // IMPORTANT: handler-based RES (eg *_mesh_mesh/blocks) must be stored as ModifiedResource
                            // in the project. FrostyProject.InternalLoad uses Sha1 == Sha1.Zero to decide that.
                            if (r != null && r.HasCustomHandler)
                            {
                                writer.Write(new byte[20]);
                            }
                            else
                            {
                                byte[] sha1 = r.Sha1;
                                if (sha1 == null || sha1.Length != 20)
                                    sha1 = ComputeSha1(r.Data);
                                writer.Write(sha1);
                            }

                            long origSize = r.OriginalSize > 0 ? r.OriginalSize : (r.Data?.LongLength ?? 0);
                            writer.Write(origSize);

                            byte[] meta = r.ResMeta ?? Array.Empty<byte>();
                            writer.Write(meta.Length);
                            if (meta.Length > 0)
                                writer.Write(meta);

                            writer.WriteNullTerminatedString(r?.UserData ?? "");
                            writer.Write(r.Data.Length);
                            writer.Write(r.Data);
                        }
                    }
                }

                // CHUNKS (FrostyProject v14 binary layout)
                var chunkWriteList = (chunkResources ?? new List<ChunkResourceData>()).Where(c => c != null && c.Id != Guid.Empty).ToList();
                writer.Write(chunkWriteList.Count);
                foreach (var c in chunkWriteList)
                {
                    writer.Write(c.Id);

                    // Bundles the chunk has been added to (usually empty for base modifications)
                    var chunkBundleNames = ResolveBundleNamesForWrite(c.AddedBundles);
                    writer.Write(chunkBundleNames.Count);
                    foreach (string bName in chunkBundleNames)
                        writer.WriteNullTerminatedString(bName);

                    // v14 stores FirstMip and H32 even when only added-to-bundles
                    writer.Write(c.FirstMip);
                    writer.Write(c.H32);

                    bool hasModifiedData = c.Data != null && c.Data.Length > 0;
                    writer.Write(hasModifiedData);
                    if (!hasModifiedData)
                        continue;

                    writer.Write(ComputeSha1(c.Data));

                    // Chunk meta
                    writer.Write(c.LogicalOffset);
                    uint logicalSize = c.LogicalSize != 0 ? c.LogicalSize : (uint)c.Data.Length;
                    writer.Write(logicalSize);
                    writer.Write(c.RangeStart);
                    writer.Write(c.RangeEnd);

                    // Flags + userdata
                    writer.Write(c.AddToChunkBundle);
                    writer.WriteNullTerminatedString(c.UserData ?? "");

                    writer.Write(c.Data.Length);
                    writer.Write(c.Data);
                }

                writer.Write(1);
                writer.WriteNullTerminatedString("legacy");
                writer.Write(0);
            }
        }

        
        internal static void WriteFbprojectBestEffort(string outputPath, string title, string author, string category, string version, string description, string link)
        {
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            string tmpPath = outputPath + ".tmp";
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }

            bool wrote = false;
            try
            {
                if (SelectedWriterBackend == FbProjectWriterBackend.Custom)
                {
                    WriteFbproject(tmpPath, title, author, category, version, description, link);
                    wrote = true;
                }
                else if (linkedFileResources != null && linkedFileResources.Count > 0)
                {
                    WriteFbproject(tmpPath, title, author, category, version, description, link);
                    wrote = true;
                }
                else
                {
                    try
                    {
                        if (TryWriteFbprojectViaFrostyCore(tmpPath, title, author, category, version, description))
                        {
                            try
                            {
                                if (string.Equals(Program.CurrentProfileKey, "starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase))
                                    PatchSwbf2EbxSectionPreserveTail(tmpPath);
                            }
                            catch (Exception pex)
                            {
                                Console.WriteLine($"[warn] EBX post-process failed (project still written): {pex.GetType().Name}: {pex.Message}");
                            }
                            wrote = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[warn] FrostyCore writer failed, falling back to custom writer: {ex.GetType().Name}: {ex.Message}");
                    }

                    if (!wrote)
                    {
                        WriteFbproject(tmpPath, title, author, category, version, description, link);
                        wrote = true;
                    }
                }

                if (!wrote || !File.Exists(tmpPath) || new FileInfo(tmpPath).Length < 16)
                    throw new InvalidOperationException("Failed to generate a valid .fbproject. See Details.");

                try
                {
                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                }
                catch { }

                File.Move(tmpPath, outputPath);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        private static void PatchSwbf2EbxSectionPreserveTail(string fbprojectPath)
        {
            byte[] data = File.ReadAllBytes(fbprojectPath);

            int off = 0;
            off += 8;
            off += 4;

            ReadCStr(data, ref off);
            off += 8;
            off += 8;
            off += 4;

            ReadCStr(data, ref off);
            ReadCStr(data, ref off);
            ReadCStr(data, ref off);
            ReadCStr(data, ref off);
            ReadCStr(data, ref off);

            int iconLen = ReadInt32LE(data, ref off);
            off += iconLen;

            for (int i = 0; i < 4; i++)
            {
                int sl = ReadInt32LE(data, ref off);
                off += sl;
            }

            ReadInt32LE(data, ref off);

            int bundleCount = ReadInt32LE(data, ref off);
            for (int i = 0; i < bundleCount; i++)
            {
                ReadCStr(data, ref off);
                ReadCStr(data, ref off);
                off += 4;
            }

            int addedEbx = ReadInt32LE(data, ref off);
            for (int i = 0; i < addedEbx; i++)
            {
                ReadCStr(data, ref off);
                off += 16;
            }

            int addedRes = ReadInt32LE(data, ref off);
            for (int i = 0; i < addedRes; i++)
            {
                ReadCStr(data, ref off);
                off += 8;
                off += 4;
                off += 16;
            }

            int addedChunks = ReadInt32LE(data, ref off);
            for (int i = 0; i < addedChunks; i++)
            {
                off += 16;
                off += 4;
            }

            int ebxCountOffset = off;
            int ebxCount = ReadInt32LE(data, ref off);
            int ebxStartOffset = off;

            for (int i = 0; i < ebxCount; i++)
            {
                ReadCStr(data, ref off);
                int linked = ReadInt32LE(data, ref off);
                if (linked != 0)
                {

                    return;
                }

                int bc = ReadInt32LE(data, ref off);
                for (int b = 0; b < bc; b++) ReadCStr(data, ref off);

                bool hasData = ReadBool(data, ref off);
                if (hasData)
                {
                    ReadBool(data, ref off);
                    ReadCStr(data, ref off);
                    ReadBool(data, ref off);
                    int len = ReadInt32LE(data, ref off);
                    off += len;
                }
            }

            int resSectionOffset = off;

            using (var ms = new MemoryStream())
            using (var w = new NativeWriter(ms))
            {

                w.Write(data, 0, ebxCountOffset);
                w.Write(ebxCount);

                var resNameLower = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rr in resResources)
                {
                    if (rr == null || string.IsNullOrWhiteSpace(rr.Name)) continue;
                    resNameLower.Add(rr.Name.Replace('\\', '/').TrimStart('/').ToLowerInvariant());
                }

                if (ebxResources == null || ebxResources.Count != ebxCount)
                    return;

                foreach (var ebx in ebxResources)
                {
                    w.WriteNullTerminatedString(ebx.Name ?? "");
                    bool hasData = ebx.Data != null && ebx.Data.Length > 0;

                    var chunkRefs = (hasData && !ebx.HasCustomHandler)
                        ? TryExtractChunkRefsFromOpaqueEbx(ebx.Data, chunkResources)
                        : null;

                    if (chunkRefs != null && chunkRefs.Count > 0)
                    {
                        w.Write(4);
                        foreach (var g in chunkRefs)
                        {
                            w.WriteNullTerminatedString("chunk");
                            w.Write(g);
                        }
                        w.Write(0);
                        w.Write(1);
                        w.Write(ebx.Data.Length);
                        w.Write(ebx.Data);
                        continue;
                    }

                    if (hasData && !ebx.HasCustomHandler)
                    {
                        string ebxLower = (ebx.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
                        if (resNameLower.Contains(ebxLower))
                        {
                            w.Write(1);
                            w.WriteNullTerminatedString("res");
                            w.WriteNullTerminatedString(ebxLower);
                            w.Write(0);
                            w.Write(1);
                            w.Write(ebx.Data.Length);
                            w.Write(ebx.Data);
                            continue;
                        }
                    }

                    w.Write(0);
                    var ebxBundleNames = new List<string>();
                    if (bundleResources != null && bundleResources.Count > 0)
                    {
                        foreach (var b in bundleResources)
                        {
                            if (b == null || string.IsNullOrWhiteSpace(b.Name))
                                continue;
                            if (IsBundleAnchorEbx(ebx.Name, b.Name))
                            {
                                ebxBundleNames.Add(b.Name);
                                break;
                            }
                        }
                    }
                    w.Write(ebxBundleNames.Count);
                    foreach (string bName in ebxBundleNames)
                        w.WriteNullTerminatedString(bName);
                    w.Write(hasData);
                    if (hasData)
                    {
                        w.Write(false);
                        w.WriteNullTerminatedString("");
                        w.Write(ebx.HasCustomHandler);
                        w.Write(ebx.Data.Length);
                        w.Write(ebx.Data);
                    }
                }

                w.Write(data, resSectionOffset, data.Length - resSectionOffset);

                File.WriteAllBytes(fbprojectPath, ms.ToArray());
            }
        }

        private static int ReadInt32LE(byte[] data, ref int off) { int v = BitConverter.ToInt32(data, off); off += 4; return v; }
        private static bool ReadBool(byte[] data, ref int off) { bool v = data[off] != 0; off += 1; return v; }
        private static string ReadCStr(byte[] data, ref int off)
        {
            int start = off;
            while (off < data.Length && data[off] != 0) off++;
            string s = Encoding.UTF8.GetString(data, start, off - start);
            off++;
            return s;
        }

private static void WriteLinkedFileRecord(NativeWriter writer, LinkedFileResourceData lf)
        {
            if (lf != null && lf.IsSwbf2MeshSetDepot)
            {
                WriteSwbf2MeshSetDepotLinkedFileRecord(writer, lf);
                return;
            }

            string resLower = (lf.ResNameLower ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(resLower))
                resLower = "linked";

            byte[] resData = lf.ResData ?? Array.Empty<byte>();
            var chunks = lf.Chunks ?? new List<LinkedChunkData>();

            writer.WriteNullTerminatedString("res");
            writer.WriteNullTerminatedString(resLower);

            foreach (var ch in chunks)
            {
                writer.WriteNullTerminatedString("chunk");
                writer.Write(ch.Id);
            }

            writer.Write(0);
            writer.Write(true);

            writer.Write(ComputeSha1(resData));

            long resOrigSize = lf.ResOriginalSize > 0 ? lf.ResOriginalSize : resData.LongLength;
            writer.Write(resOrigSize);

            writer.Write((byte)0);
            writer.Write(0);
            writer.Write(resData.Length);
            writer.Write(resData);

            writer.Write(chunks.Count);
            foreach (var ch in chunks)
            {
                writer.Write(ch.Id);
                writer.Write((long)0);
                writer.Write(ch.H32);
                writer.Write(ch.AddToChunkBundle);
            }

            foreach (var ch in chunks)
            {
                byte[] chunkData = ch.Data ?? Array.Empty<byte>();
                writer.Write(ComputeSha1(chunkData));

                writer.Write((uint)ch.LogicalOffset);
                writer.Write((uint)ch.LogicalSize);
                writer.Write((uint)ch.RangeStart);
                writer.Write((uint)ch.RangeEnd);

                byte firstMip = 1;
                if (ch.FirstMip > 0 && ch.FirstMip < 256)
                    firstMip = (byte)ch.FirstMip;

                writer.Write(firstMip);
                writer.Write((byte)0);

                writer.Write(chunkData.Length);
                writer.Write(chunkData);
            }
        }

        private static void WriteSwbf2MeshSetDepotLinkedFileRecord(NativeWriter writer, LinkedFileResourceData lf)
        {

            string depotLower = (lf.ResNameLower ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            string blocksLower = (lf.SecondaryResNameLower ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();

            byte[] depotData = lf.ResData ?? Array.Empty<byte>();
            byte[] blocksData = lf.SecondaryResData ?? Array.Empty<byte>();

            byte[] depotMeta = EnsureMeta16(lf.ResMeta);
            byte[] blocksMeta = EnsureMeta16(lf.SecondaryResMeta);

            writer.WriteNullTerminatedString("res");
            writer.WriteNullTerminatedString(depotLower);
            writer.Write(0);
            writer.Write((byte)0);
            writer.Write(2);
            writer.WriteNullTerminatedString(blocksLower);

            writer.Write((long)0);
            writer.Write(1);
            writer.Write(new byte[20]);
            writer.Write(0);
            writer.Write((byte)0);
            writer.Write(16);
            writer.Write(depotMeta);
            writer.WriteNullTerminatedString("res");
            writer.Write(depotData.Length);
            writer.Write(depotData);

            int chunkCount = lf.Chunks != null ? lf.Chunks.Count : 0;

            writer.WriteNullTerminatedString(depotLower);
            writer.Write(1 + chunkCount);
            writer.WriteNullTerminatedString("res");
            writer.WriteNullTerminatedString(blocksLower);

            if (lf.Chunks != null && lf.Chunks.Count > 0)
            {
                var ordered = lf.Chunks
                    .OrderByDescending(c =>
                    {
                        long ls = (long)(c.LogicalSize != 0 ? c.LogicalSize : (uint)(c.Data != null ? c.Data.Length : 0));
                        return ls;
                    })
                    .ToList();

                foreach (var ch in ordered)
                {
                    writer.WriteNullTerminatedString("chunk");
                    writer.Write(ch.Id);
                }
            }

            writer.Write(0);
            writer.Write(true);

            writer.Write(ComputeSha1(blocksData));

            long blocksOrigSize = lf.SecondaryResOriginalSize > 0 ? lf.SecondaryResOriginalSize : blocksData.LongLength;
            if (blocksData.Length >= 4)
            {
                uint be = (uint)((blocksData[0] << 24) | (blocksData[1] << 16) | (blocksData[2] << 8) | blocksData[3]);
                if (be > 0 && be <= 512u * 1024u * 1024u)
                    blocksOrigSize = be;
            }

            writer.Write(blocksOrigSize);
            writer.Write(16);
            writer.Write(blocksMeta);
            writer.WriteNullTerminatedString("res");
            writer.Write(blocksData.Length);
            writer.Write(blocksData);

            writer.Write(chunkCount);

            const int MeshSetDepotH32 = unchecked((int)0xDB4D27ED);

            if (lf.Chunks != null)
            {
                foreach (var ch in lf.Chunks)
                {
                    byte[] chunkData = ch.Data ?? Array.Empty<byte>();

                    uint logicalSize = ch.LogicalSize != 0 ? ch.LogicalSize : (uint)chunkData.Length;

                    writer.Write(ch.Id);
                    writer.Write(0);
                    writer.Write(-1);
                    writer.Write(MeshSetDepotH32);
                    writer.Write((byte)1);

                    writer.Write(ComputeSha1(chunkData));

                    writer.Write(0);
                    writer.Write(logicalSize);
                    writer.Write((long)0);
                    writer.Write((short)1);

                    writer.Write(chunkData.Length);
                    writer.Write(chunkData);
                }
            }
        }

        private static byte[] EnsureMeta16(byte[] meta)
        {
            if (meta != null && meta.Length == 16)
                return meta;

            byte[] b = new byte[16];
            if (meta != null && meta.Length > 0)
                Buffer.BlockCopy(meta, 0, b, 0, Math.Min(16, meta.Length));
            return b;
        }

        private static long GuessSwbf2ResOriginalSize(long current, byte[] data)
        {
            if (data == null || data.Length < 8) return current;

            uint beSize = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
            bool hasZstd = false;
            int scanLen = Math.Min(64, data.Length - 4);
            for (int i = 0; i <= scanLen; i++)
            {
                if (data[i] == 0x28 && data[i + 1] == 0xB5 && data[i + 2] == 0x2F && data[i + 3] == 0xFD)
                {
                    hasZstd = true;
                    break;
                }
            }
            if (hasZstd && beSize >= data.Length && beSize <= 64 * 1024 * 1024)
                return beSize;
            return current;
        }

        private static List<Guid> TryExtractChunkRefsFromRes(ResResourceData res, List<ChunkResourceData> allChunks)
        {
            var refs = new List<Guid>();
            if (res == null || allChunks == null || allChunks.Count == 0) return refs;

            byte[] meta = res.ResMeta ?? Array.Empty<byte>();
            byte[] data = res.Data ?? Array.Empty<byte>();

            int dataWin = Math.Min(512, data.Length);
            byte[] window = new byte[meta.Length + dataWin];
            if (meta.Length > 0) Buffer.BlockCopy(meta, 0, window, 0, meta.Length);
            if (dataWin > 0) Buffer.BlockCopy(data, 0, window, meta.Length, dataWin);

            foreach (var ch in allChunks)
            {
                if (ch == null) continue;
                byte[] g = ch.Id.ToByteArray();
                if (IndexOf(window, g) >= 0)
                    refs.Add(ch.Id);
            }

            return refs;
        }

private static void WriteChunkRecord(NativeWriter writer, ChunkResourceData chunk)
        {
            writer.Write(chunk.Id);

            if (ChunkLayout != ChunkRecordLayout.Legacy)
                writer.Write(0);

            var chunkBundleNames = ResolveBundleNamesForWrite(chunk.AddedBundles);
            writer.Write(chunkBundleNames.Count);
            foreach (string bName in chunkBundleNames)
                writer.WriteNullTerminatedString(bName);

            if (ChunkLayout == ChunkRecordLayout.WithLinkedAndSizesAndSecondBundleCount)
                writer.Write(0);

            bool hasData = chunk.Data != null && chunk.Data.Length > 0;
            writer.Write(hasData);
            if (!hasData)
                return;

            writer.Write(ComputeSha1(chunk.Data));

            if (ChunkLayout == ChunkRecordLayout.WithLinkedAndSizes || ChunkLayout == ChunkRecordLayout.WithLinkedAndSizesAndSecondBundleCount)
            {

                long s = chunk.Data.Length;
                writer.Write(s);
                writer.Write(s);
            }

            writer.Write(chunk.LogicalOffset);
            writer.Write(chunk.LogicalSize);
            writer.Write(chunk.RangeStart);
            writer.Write(chunk.RangeEnd);
            writer.Write(chunk.FirstMip);
            writer.Write(chunk.H32);
            writer.Write(chunk.AddToChunkBundle);

            writer.WriteNullTerminatedString("res");
            writer.Write(chunk.Data.Length);
            writer.Write(chunk.Data);
        }

        public static void InspectProject(string projectPath)
        {
            Console.WriteLine("Inspecting .fbproject ...");
            using (NativeReader reader = new NativeReader(new FileStream(projectPath, FileMode.Open, FileAccess.Read)))
            {
                static string ReadCStringSafe(NativeReader r, int maxLen = 4096)
                {

                    var bytes = new List<byte>(64);
                    for (int i = 0; i < maxLen; i++)
                    {
                        if (r.Position >= r.Length)
                            break;
                        byte b = r.ReadByte();
                        if (b == 0)
                            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                        bytes.Add(b);
                    }
                    throw new InvalidDataException($"CString exceeded {maxLen} bytes at 0x{r.Position:X}");
                }

                string stage = "header";
                try
                {
                    ulong magic = reader.ReadULong();
                    uint version = reader.ReadUInt();
                    string profileName = ReadCStringSafe(reader);
                    bool isSwbf2 = string.Equals(profileName, "starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase);
                long created = reader.ReadLong();
                long modified = reader.ReadLong();
                reader.ReadUInt();
                string title = ReadCStringSafe(reader);
                string author = ReadCStringSafe(reader);
                string category = ReadCStringSafe(reader);
                string projVer = ReadCStringSafe(reader);
                string desc = ReadCStringSafe(reader);

                int iconLen = reader.ReadInt();
                if (iconLen > 0) reader.ReadBytes(iconLen);
                for (int i = 0; i < 4; i++)
                {
                    int sLen = reader.ReadInt();
                    if (sLen > 0) reader.ReadBytes(sLen);
                }

                    stage = "superbundles";
                    int superbundles = reader.ReadInt();

                    stage = "bundles";
                    int bundleCount = reader.ReadInt();
                for (int i = 0; i < bundleCount; i++)
                {
                    ReadCStringSafe(reader);
                    ReadCStringSafe(reader);
                    reader.ReadInt();

                    if (!profileName.Equals("starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase))
                        reader.ReadBoolean();
                }

                    stage = "added-ebx";
                    int addedEbxCount = reader.ReadInt();
                for (int i = 0; i < addedEbxCount; i++)
                {
                    ReadCStringSafe(reader);
                    reader.ReadGuid();
                }

                int addedResCount = reader.ReadInt();
                for (int i = 0; i < addedResCount; i++)
                {
                    ReadCStringSafe(reader);
                    if (isSwbf2)
                    {

                        reader.ReadULong();
                        reader.ReadUInt();
                        reader.ReadBytes(16);
                    }
                    else
                    {

                        reader.ReadULong();
                        reader.ReadUInt();
                        reader.ReadBytes(16);
                    }
                }

                int addedChunkCount = reader.ReadInt();
                for (int i = 0; i < addedChunkCount; i++)
                {
                    reader.ReadGuid();
                    reader.ReadInt();
                }

                int ebxCount = reader.ReadInt();
                for (int i = 0; i < ebxCount; i++)
                {
                    string name = ReadCStringSafe(reader);
                    int linked = reader.ReadInt();

                    if (linked == 4)
                    {

                        while (true)
                        {
                            long p0 = reader.Position;
                            string mk = ReadCStringSafe(reader, 64);
                            if (!string.Equals(mk, "chunk", StringComparison.OrdinalIgnoreCase))
                            {
                                reader.Position = p0;
                                break;
                            }
                            reader.ReadGuid();
                        }

                        reader.ReadInt();
                        reader.ReadInt();
                        int len = reader.ReadInt();
                        if (len > 0) reader.ReadBytes(len);
                        continue;
                    }

if (linked == 1)
{

    long linkedStart = reader.Position;

    string marker1 = ReadCStringSafe(reader, 64);
    string resLower = ReadCStringSafe(reader);

    long probeInline = reader.Position;
    try
    {
        int z0 = reader.ReadInt();
        int z1 = reader.ReadInt();
        if (z0 == 0 && z1 == 1)
        {
            int lenInline = reader.ReadInt();
            if (lenInline > 0) reader.ReadBytes(lenInline);
            continue;
        }
    }
    catch
    {

    }

    reader.Position = probeInline;

    {
        long probePos = reader.Position;
        try
        {
            int a0 = reader.ReadInt();
            byte b0 = reader.ReadByte();
            int a2 = reader.ReadInt();
            string blocksLower = ReadCStringSafe(reader);

            if (a0 == 0 && b0 == 0 && a2 == 2 && blocksLower != null && blocksLower.EndsWith("/blocks", StringComparison.OrdinalIgnoreCase))
            {

                reader.ReadLong();
                reader.ReadInt();
                reader.ReadBytes(20);
                reader.ReadInt();
                reader.ReadByte();
                int metaLen0 = reader.ReadInt();
                if (metaLen0 > 0) reader.ReadBytes(metaLen0);
                ReadCStringSafe(reader);
                int depotLen = reader.ReadInt();
                if (depotLen > 0) reader.ReadBytes(depotLen);

                ReadCStringSafe(reader);
                int linkedCount = reader.ReadInt();
                ReadCStringSafe(reader, 64);
                ReadCStringSafe(reader);

                int chunkRefCount = Math.Max(0, linkedCount - 1);
                for (int cc = 0; cc < chunkRefCount; cc++)
                {
                    string mk = ReadCStringSafe(reader, 64);
                    reader.ReadGuid();
                }

                reader.ReadInt();
                reader.ReadBoolean();
                reader.ReadBytes(20);
                reader.ReadLong();
                int metaLen1 = reader.ReadInt();
                if (metaLen1 > 0) reader.ReadBytes(metaLen1);
                ReadCStringSafe(reader);
                int blocksLen = reader.ReadInt();
                if (blocksLen > 0) reader.ReadBytes(blocksLen);

                int embeddedChunkCount = reader.ReadInt();
                for (int cc = 0; cc < embeddedChunkCount; cc++)
                {
                    reader.ReadGuid();
                    reader.ReadInt();
                    reader.ReadInt();
                    reader.ReadInt();
                    reader.ReadByte();
                    reader.ReadBytes(20);
                    reader.ReadInt();
                    reader.ReadUInt();
                    reader.ReadULong();
                    reader.ReadUShort();
                    int dataLen = reader.ReadInt();
                    if (dataLen > 0) reader.ReadBytes(dataLen);
                }

                Console.WriteLine($"linked[{i}] name='{name}' (MeshSetDepot) res='{resLower}' blocks='{blocksLower}' chunks={embeddedChunkCount}");
                continue;
            }
        }
        catch
        {

        }
        finally
        {
            reader.Position = probePos;
        }
    }

    byte extra0 = reader.ReadByte();

    int unk0 = reader.ReadInt();
    int resNameCount = reader.ReadInt();

    if (resNameCount >= 0 && resNameCount <= 256)
    {
        for (int r = 0; r < resNameCount; r++)
            reader.ReadNullTerminatedString();

        int chunkCountInner = reader.ReadInt();

        Guid firstChunk = Guid.Empty;
        string marker2 = "";
        if (chunkCountInner > 0)
        {

            for (int c = 0; c < chunkCountInner; c++)
            {
                marker2 = reader.ReadNullTerminatedString();
                firstChunk = reader.ReadGuid();
            }
        }

        try
        {
            int unk1 = reader.ReadInt();
            bool flag = reader.ReadBoolean();

            reader.ReadBytes(20);
            long resOrigSize = reader.ReadLong();
            reader.ReadByte();
            reader.ReadInt();
            int resLen = reader.ReadInt();
            if (resLen > 0) reader.ReadBytes(resLen);

            int chunkTableCount = reader.ReadInt();
            for (int c = 0; c < chunkTableCount; c++)
            {
                reader.ReadGuid();
                reader.ReadLong();
                reader.ReadInt();
                reader.ReadBoolean();
            }

            for (int c = 0; c < chunkTableCount; c++)
            {
                reader.ReadBytes(20);
                reader.ReadUInt();
                reader.ReadUInt();
                reader.ReadUInt();
                reader.ReadUInt();
                reader.ReadByte();
                reader.ReadByte();
                int chunkLen = reader.ReadInt();
                if (chunkLen > 0) reader.ReadBytes(chunkLen);
            }

            Console.WriteLine($"linked[{i}] name='{name}' res='{resLower}' chunks={chunkCountInner} resLen={resLen} resOrigSize={resOrigSize} marker1='{marker1}'");
        }
        catch
        {
            Console.WriteLine($"linked[{i}] name='{name}' res='{resLower}' chunks={chunkCountInner} marker1='{marker1}' (tail parse skipped)");
        }

        continue;
    }

    reader.Position = linkedStart;
    Console.WriteLine($"linked[{i}] name='{name}' (unrecognized linked layout)");
    continue;
}

                    int bc = reader.ReadInt();
                    for (int j = 0; j < bc; j++) ReadCStringSafe(reader);
                    bool hasData = reader.ReadBoolean();
                    if (hasData)
                    {
                        reader.ReadBoolean();
                        ReadCStringSafe(reader);
                        reader.ReadBoolean();
                        int dLen = reader.ReadInt();
                        if (dLen > 0) reader.ReadBytes(dLen);
                    }
                }

                long posBeforeRes = reader.Position;
                int maybeResCount = reader.ReadInt();

                int resCount = 0;
                int chunkCount = 0;

                if (maybeResCount == 1)
                {
                    long posAfterInt = reader.Position;
                    string maybeTag = ReadCStringSafe(reader, 64);
                    if (maybeTag.Equals("legacy", StringComparison.OrdinalIgnoreCase) || maybeTag.Equals("link", StringComparison.OrdinalIgnoreCase))
                    {
                        int linkEnd = reader.ReadInt();
                        Console.WriteLine($"magic=0x{magic:X16} version={version} profile={profileName} bundles={bundleCount} ebx={ebxCount} res=0 chunks=0 (omitted sections)");
                        Console.WriteLine($"added: ebx={addedEbxCount} res={addedResCount} chunk={addedChunkCount} superbundles={superbundles}");
                        Console.WriteLine($"meta: title='{title}' author='{author}' category='{category}' v='{projVer}'");
                        Console.WriteLine($"eof=1 link='{maybeTag}' linkEnd={linkEnd}");
                        return;
                    }

                    reader.Position = posBeforeRes;
                    resCount = reader.ReadInt();
                }
                else
                {
                    resCount = maybeResCount;
                }

                    stage = "res-section";
int linkedRes = 0;
int resHasData = 0;
for (int i = 0; i < resCount; i++)
{

    string rname = ReadCStringSafe(reader);
    int linked = reader.ReadInt();

    if (linked > 0)
    {
        linkedRes++;

        int linkCount = linked;
        for (int k = 0; k < linkCount; k++)
        {
            string mk = ReadCStringSafe(reader, 64);
            if (string.Equals(mk, "chunk", StringComparison.OrdinalIgnoreCase))
            {
                reader.ReadGuid();
            }
            else if (string.Equals(mk, "res", StringComparison.OrdinalIgnoreCase))
            {
                ReadCStringSafe(reader);
            }
            else
            {

                throw new InvalidDataException($"Invalid linked RES tag '{mk}' at 0x{reader.Position:X} name='{rname}'");
            }
        }

        int term = reader.ReadInt();
        if (term != 0)
            throw new InvalidDataException($"Missing linked RES terminator at 0x{reader.Position:X} name='{rname}' (got {term})");

        bool hasDataLinked = reader.ReadBoolean();
        if (hasDataLinked)
        {
            resHasData++;
            reader.ReadBytes(20);
            reader.ReadLong();
            int metaLen = reader.ReadInt();
            if (metaLen < 0 || metaLen > 1024 * 1024)
                throw new InvalidDataException($"Invalid metaLen={metaLen} in linked RES at 0x{reader.Position:X} name='{rname}'");
            if (metaLen > 0) reader.ReadBytes(metaLen);
            ReadCStringSafe(reader);
            int dLen = reader.ReadInt();
            if (dLen < 0 || dLen > 1024 * 1024 * 1024)
                throw new InvalidDataException($"Invalid dataLen={dLen} in linked RES at 0x{reader.Position:X} name='{rname}'");
            if (dLen > 0) reader.ReadBytes(dLen);
        }
        continue;
    }

    int bc = reader.ReadInt();
    for (int j = 0; j < bc; j++) ReadCStringSafe(reader);
    bool hasData = reader.ReadBoolean();
    if (hasData)
    {
        resHasData++;
        reader.ReadBytes(20);
        reader.ReadLong();
        int metaLen = reader.ReadInt();
        if (metaLen > 0) reader.ReadBytes(metaLen);
        ReadCStringSafe(reader);
int dLen = reader.ReadInt();
        if (dLen > 0) reader.ReadBytes(dLen);
    }
}

Console.WriteLine($"resSummary: total={resCount} linked={linkedRes} hasData={resHasData}");

chunkCount = reader.ReadInt();

                Console.WriteLine($"magic=0x{magic:X16} version={version} profile={profileName} bundles={bundleCount} ebx={ebxCount} res={resCount} chunks={chunkCount}");
                Console.WriteLine($"added: ebx={addedEbxCount} res={addedResCount} chunk={addedChunkCount} superbundles={superbundles}");
                Console.WriteLine($"meta: title='{title}' author='{author}' category='{category}' v='{projVer}'");

                for (int i = 0; i < chunkCount; i++)
                {
                    Guid id = reader.ReadGuid();

                    int bc = reader.ReadInt();
                    for (int j = 0; j < bc; j++)
                        ReadCStringSafe(reader);

                    int firstMip = reader.ReadInt();
                    int h32 = reader.ReadInt();

                    bool hasData = reader.ReadBoolean();
                    if (!hasData)
                    {
                        Console.WriteLine($"chunk[{i}] {id} hasData=False bundles={bc} firstMip={firstMip} h32=0x{(uint)h32:X8}");
                        continue;
                    }

                    reader.ReadBytes(20);

                    uint logicalOffset = reader.ReadUInt();
                    uint logicalSize = reader.ReadUInt();
                    uint rangeStart = reader.ReadUInt();
                    uint rangeEnd = reader.ReadUInt();
                    bool addToChunkBundle = reader.ReadBoolean();
                    string userdata = ReadCStringSafe(reader);
                    int dataLen = reader.ReadInt();

                    if (dataLen > 0)
                        reader.ReadBytes(dataLen);

                    Console.WriteLine($"chunk[{i}] {id} hasData=True dataLen={dataLen} logicalSize={logicalSize} range=[{rangeStart},{rangeEnd}] firstMip={firstMip} h32=0x{(uint)h32:X8} addToChunkBundle={addToChunkBundle}");
                }

                int eof = reader.ReadInt();
                Console.WriteLine($"eof={eof}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"inspect failed at stage='{stage}' offset=0x{reader.Position:X} ({reader.Position})");
                    Console.WriteLine(ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        static void CompileProject(string projectPath, string fbmodPath)
        {
            Console.WriteLine("Initializing Compiler SDK...");
            TypeLibrary.Initialize();
            FrostyApp.AssetManager = new AssetManager(FrostyApp.FileSystem, FrostyApp.ResourceManager);
            FrostyApp.AssetManager.Initialize();

            using (NativeReader reader = new NativeReader(new FileStream(projectPath, FileMode.Open)))
            {
                if (reader.ReadULong() != FBPROJECT_MAGIC) throw new Exception("Invalid magic");
                reader.ReadUInt();
                reader.ReadNullTerminatedString();
                reader.ReadLong();
                reader.ReadLong();
                reader.ReadUInt();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();
                reader.ReadNullTerminatedString();

                int iconLen = reader.ReadInt();
                if (iconLen > 0) reader.ReadBytes(iconLen);
                for (int i = 0; i < 4; i++)
                {
                    int sLen = reader.ReadInt();
                    if (sLen > 0) reader.ReadBytes(sLen);
                }

                reader.ReadInt();

                int bundleCount = reader.ReadInt();
                for (int i = 0; i < bundleCount; i++)
                {
                    string bName = reader.ReadNullTerminatedString();
                    string super = reader.ReadNullTerminatedString();
                    int type = reader.ReadInt();
                    bool isAdded = reader.ReadBoolean();
                    int sbId = FrostyApp.AssetManager.GetSuperBundleId(super);
                    FrostyApp.AssetManager.AddBundle(bName, (BundleType)type, sbId);
                }

                int addedEbxCount = reader.ReadInt();
                for (int i = 0; i < addedEbxCount; i++)
                {
                    string name = reader.ReadNullTerminatedString();
                    Guid guid = reader.ReadGuid();

                    try { FrostyApp.AssetManager.AddEbx(new EbxAssetEntry { Name = name, Guid = guid, IsAdded = true }); }
                    catch { }
                }

                int addedResCount = reader.ReadInt();
                for (int i = 0; i < addedResCount; i++)
                {
                    string name = reader.ReadNullTerminatedString();
                    if (string.Equals(ProfilesLibrary.ProfileName, "starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase))
                    {

                        Guid guid = reader.ReadGuid();
                        int type = reader.ReadInt();
                        reader.ReadInt(); reader.ReadInt(); reader.ReadInt();
                        try { FrostyApp.AssetManager.AddRes(new ResAssetEntry { Name = name, ResRid = 0UL, ResType = (uint)type, ResMeta = new byte[16], IsAdded = true }); } catch { }
                    }
                    else
                    {
                        ulong rid = reader.ReadULong();
                        uint type = reader.ReadUInt();
                        byte[] meta = reader.ReadBytes(0x10);
                        try { FrostyApp.AssetManager.AddRes(new ResAssetEntry { Name = name, ResRid = rid, ResType = type, ResMeta = meta, IsAdded = true }); } catch { }
                    }
                }

                int addedChunkCount = reader.ReadInt();
                for (int i = 0; i < addedChunkCount; i++)
                {
                    Guid id = reader.ReadGuid();
                    int h32 = reader.ReadInt();
                    try { FrostyApp.AssetManager.AddChunk(new ChunkAssetEntry { Id = id, H32 = h32, IsAdded = true }); }
                    catch { }
                }

                int ebxs = reader.ReadInt();
                for (int i = 0; i < ebxs; i++)
                {
                    string name = reader.ReadNullTerminatedString();
                    reader.ReadInt();
                    int abCount = reader.ReadInt();
                    for (int j = 0; j < abCount; j++)
                    {
                        string bName = reader.ReadNullTerminatedString();
                        if (FrostyApp.AssetManager.GetBundleId(bName) == -1)
                            Console.WriteLine($"CRASH RISK: Invalid Bundle Link '{bName}' for {name}");
                    }
                    if (reader.ReadBoolean())
                    {
                        reader.ReadBoolean();
                        reader.ReadNullTerminatedString();
                        reader.ReadBoolean();
                        int dLen = reader.ReadInt();
                        reader.ReadBytes(dLen);
                    }
                }

                int ress = reader.ReadInt();
                for (int i = 0; i < ress; i++)
                {
                    string name = reader.ReadNullTerminatedString();
                    reader.ReadInt();
                    int abCount = reader.ReadInt();
                    for (int j = 0; j < abCount; j++) reader.ReadNullTerminatedString();
                    if (reader.ReadBoolean())
                    {
                        reader.ReadBytes(20);
                        reader.ReadLong();
                        int metaLen = reader.ReadInt();
                        if (metaLen > 0) reader.ReadBytes(metaLen);
                        reader.ReadNullTerminatedString();
                        int dLen = reader.ReadInt();
                        if (dLen > 0) reader.ReadBytes(dLen);
                    }
                }

                int chunks = reader.ReadInt();
                for (int i = 0; i < chunks; i++)
                {
                    Guid id = reader.ReadGuid();
                    if (ChunkLayout != ChunkRecordLayout.Legacy) reader.ReadInt();

                    int abCount = reader.ReadInt();
                    for (int j = 0; j < abCount; j++) reader.ReadNullTerminatedString();

                    if (ChunkLayout == ChunkRecordLayout.WithLinkedAndSizesAndSecondBundleCount)
                        reader.ReadInt();

                    bool hasData = reader.ReadBoolean();
                    if (!hasData) continue;

                    reader.ReadBytes(20);

                    if (ChunkLayout == ChunkRecordLayout.WithLinkedAndSizes || ChunkLayout == ChunkRecordLayout.WithLinkedAndSizesAndSecondBundleCount)
                    {
                        reader.ReadLong();
                        reader.ReadLong();
                    }

                    uint logicalOffset = reader.ReadUInt();
                    uint logicalSize = reader.ReadUInt();
                    uint rangeStart = reader.ReadUInt();
                    uint rangeEnd = reader.ReadUInt();
                    int firstMip = reader.ReadInt();
                    int h32 = reader.ReadInt();
                    bool addToChunkBundle = reader.ReadBoolean();
                    reader.ReadNullTerminatedString();
                    int dLen = reader.ReadInt();
                    if (dLen > 0) reader.ReadBytes(dLen);

                    Console.WriteLine($"[chunk] {id} dataLen={dLen} logicalSize={logicalSize} range=[{rangeStart},{rangeEnd}] firstMip={firstMip} h32={h32} addToChunkBundle={addToChunkBundle}");
                }
            }

            Console.WriteLine("Project Check Completed.");
        }
    }
}
