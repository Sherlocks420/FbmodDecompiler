using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using Frosty.Core;
using Frosty.Core.Mod;
using Frosty.Core.IO;
using Frosty.Hash;
using FrostyApp = Frosty.Core.App;

namespace FbmodDecompiler
{

    internal partial class Program
    {

	        private static bool _ebxClassesResolverInstalled = false;

        internal const ulong FBPROJECT_MAGIC = 0x00005954534F5246;
        internal const uint FBPROJECT_VERSION = 14;

        internal static readonly List<EbxResourceData> ebxResources = new List<EbxResourceData>();
        internal static readonly List<ResResourceData> resResources = new List<ResResourceData>();
        internal static readonly List<ChunkResourceData> chunkResources = new List<ChunkResourceData>();
        internal static readonly List<BundleResourceData> bundleResources = new List<BundleResourceData>();

        internal static readonly List<LinkedFileResourceData> linkedFileResources = new List<LinkedFileResourceData>();

        internal static string CurrentProfileKey = "";
        internal static uint CurrentGameVersion = 0;
        private static bool _didDumpResApis = false;

        internal static byte[] projectIcon = null;
        internal static readonly List<byte[]> projectScreenshots = new List<byte[]>();

        internal static readonly Dictionary<int, string> bundleHashToName = new Dictionary<int, string>();
        internal static readonly Dictionary<int, string> superBundleIdToName = new Dictionary<int, string>();

        internal static string FrostyDirOverride = null;

        internal sealed class Options
        {
            public string InputFbmod;
            public string Output;
            public string GamePath;
            public string OutputType = "fbproject";
            public bool EbxOnly = false;
            public bool Verbose = false;
            public bool KeepResChunk = true;
            public bool DisableLinked = true;
            public bool EnableGenericLinkedRecords = false;
            public string Writer = "custom";
            public string ChunkLayout = null;
            public string FrostyDirOverride = null;
            public bool FastInit = false;
        }

        internal sealed class EbxResourceData
        {
            public string Name;
            public byte[] Data;
            public Guid Guid;
            public bool IsAdded;
            public bool HasCustomHandler;
            public int HandlerHash;
            public string UserData = "";
            public List<int> AddedBundles = new List<int>();
        }

        internal sealed class ResResourceData
        {
            public string Name;
            public byte[] Data;
            public ulong ResRid;
            public uint ResType;
            public byte[] ResMeta;

            public byte[] Sha1;
            public long OriginalSize;
            public string UserData = "";
            public bool HasCustomHandler;
            public int HandlerHash;
            public bool IsAdded;
            public List<int> AddedBundles = new List<int>();
        }

        internal sealed class ChunkResourceData
        {
            public Guid Id;
            public byte[] Data;
            public int H32;
            public uint RangeStart;
            public uint RangeEnd;
            public uint LogicalOffset;
            public uint LogicalSize;
            public int FirstMip = -1;
            public bool IsAdded;
            public bool AddToChunkBundle = true;
            public string UserData = "";
            public List<int> AddedBundles = new List<int>();
        }

        internal sealed class LinkedFileResourceData
        {

            public string DisplayName;

            public string ResNameLower;

            public byte[] ResData;

            public long ResOriginalSize;

            public byte[] ResMeta;

            public bool IsSwbf2MeshSetDepot;
            public string SecondaryResNameLower;
            public byte[] SecondaryResData;
            public long SecondaryResOriginalSize;
            public byte[] SecondaryResMeta;

            public List<LinkedChunkData> Chunks = new List<LinkedChunkData>();
        }

internal sealed class LinkedChunkData
{
    public Guid Id;
    public byte[] Data;

    public int H32;
    public uint LogicalOffset;
    public uint LogicalSize;
    public uint RangeStart;
    public uint RangeEnd;
    public int FirstMip;
    public bool AddToChunkBundle;
        }

        internal sealed class BundleResourceData
        {
            public string Name;
            public string SuperBundleName;
            public int Type;
            public bool IsAdded;
        }

        
        public static int Run(Options opt)
        {
            try { Bootstrapper.EnsureRuntimeFiles(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to prepare runtime dependencies. See Details.", ex);
            }

            if (opt == null)
                return 1;

            if (string.IsNullOrWhiteSpace(opt.InputFbmod) || !File.Exists(opt.InputFbmod))
                throw new FileNotFoundException("Input .fbmod not found.", opt.InputFbmod ?? "");

            if (string.IsNullOrWhiteSpace(opt.Output))
                throw new ArgumentException("Missing output path.", nameof(opt.Output));

            if (string.IsNullOrWhiteSpace(opt.GamePath) || !Directory.Exists(opt.GamePath))
                throw new DirectoryNotFoundException("Game path not found: " + (opt.GamePath ?? ""));

try
            {
                if (!string.IsNullOrWhiteSpace(opt.InputFbmod) && File.Exists(opt.InputFbmod))
                {

var blocked = false;
if (FrostyModReaderLite.TryReadAuthor(opt.InputFbmod, out var author))
{
    blocked = AuthorProtection.IsBlocked(author);
}
else
{
    blocked = AuthorProtection.ScanFileForBlockedAuthor(opt.InputFbmod);
}

if (blocked)
{
    Console.WriteLine("ERROR: This mod is not allowed to be reversed in this tool.");
    return 2;
}
                }
            }
            catch
            {
            }

            if (string.Equals(opt.Writer, "custom", StringComparison.OrdinalIgnoreCase))
                SelectedWriterBackend = FbProjectWriterBackend.Custom;
            else
                SelectedWriterBackend = FbProjectWriterBackend.FrostyCore;

            if (!string.IsNullOrWhiteSpace(opt.ChunkLayout))
                ChunkLayout = ParseChunkLayout(opt.ChunkLayout);

            if (!string.IsNullOrWhiteSpace(opt.FrostyDirOverride))
                FrostyDirOverride = opt.FrostyDirOverride;

            if (string.IsNullOrWhiteSpace(FrostyDirOverride))
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string p1 = Path.Combine(baseDir, "frosty_dir");
                    string p2 = Path.Combine(baseDir, "frosty_dir.txt");
                    string p = File.Exists(p1) ? p1 : (File.Exists(p2) ? p2 : null);
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        string txt = File.ReadAllText(p)?.Trim();
                        if (!string.IsNullOrWhiteSpace(txt) && Directory.Exists(txt))
                            FrostyDirOverride = txt;
                    }
                }
                catch { }
            }

            try
            {

                ResetState();

                try
                {
                    if (string.IsNullOrWhiteSpace(opt.GamePath))
                    {
                        var auto = GameLocator.TryFindSwbf2InstallDir();
                        if (!string.IsNullOrWhiteSpace(auto))
                            opt.GamePath = auto;
                    }
                }
                catch { }

                
                // For full-fidelity fbproject output (especially CHUNK/RES), we need AssetManager caches.
                if (!opt.EbxOnly && string.Equals(opt.OutputType, "fbproject", StringComparison.OrdinalIgnoreCase) && opt.FastInit)
                {
                    Console.WriteLine("[info] FastInit overridden to false for fbproject output (bundle/chunk correctness).");
                    opt.FastInit = false;
                }

if (!InitializeFrosty(opt.GamePath, opt.Verbose, opt.FastInit, out var assetManager, out var profileKey))
                    return 1;

                ReadModAndCollectResources(opt, assetManager, profileKey);

                int totalCollected = ebxResources.Count + resResources.Count + chunkResources.Count + bundleResources.Count;
                if (totalCollected == 0)
                    throw new InvalidOperationException(
                        "No mod resources were collected. Missing dependencies or mod could not be parsed. See Details.",
                        _firstResourceDataError
                    );

                RepairResourceMetadata(assetManager, opt.Verbose);

                if (!opt.EbxOnly && chunkResources.Count > 0 && ChunkLayout == ChunkRecordLayout.Legacy)
                {
                    Console.WriteLine("[warn] CHUNK layout is set to 'legacy' but this mod contains CHUNK entries. Switching to 'linked' to avoid Frosty loading hangs.");
                    ChunkLayout = ChunkRecordLayout.WithLinked;
                }

                if (!opt.DisableLinked && !opt.EbxOnly)
                {
                    bool keepResChunk = opt.KeepResChunk;
                    bool enableGeneric = opt.EnableGenericLinkedRecords;

                    if (SelectedWriterBackend == FbProjectWriterBackend.Custom)
                        keepResChunk = true;

                    BuildLinkedFileResources(assetManager, opt.Verbose, keepResChunk, enableGeneric);

                    if (SelectedWriterBackend == FbProjectWriterBackend.Custom)
                        linkedFileResources.Clear();
                }

                if (!opt.EbxOnly)
                    FillMissingResFromBase(assetManager, opt.Verbose);

                SanitizeAddedFlags(assetManager, opt.Verbose);

                if (string.Equals(opt.OutputType, "dump", StringComparison.OrdinalIgnoreCase))
                {
                    DumpEbx(opt.Output, opt.EbxOnly);
                    return 0;
                }

                Console.WriteLine("Writing .fbproject ...");
                WriteFbprojectBestEffort(
                    opt.Output,
                    title: _modTitle,
                    author: _modAuthor,
                    category: _modCategory,
                    version: _modVersion,
                    description: _modDescription,
                    link: "legacy"
                );

                Console.WriteLine("Done: " + opt.Output);
                Console.WriteLine($"EBX={ebxResources.Count} RES={resResources.Count} CHUNK={chunkResources.Count} BUNDLE={bundleResources.Count}");
                Console.WriteLine("Mesh-Assets may not show as modified although they contain the modified Data/Model");
                if (_failedEbxCount > 0)
                    Console.WriteLine($"EBX parse failures: {_failedEbxCount} (bundle references preserved where possible)");

                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: " + ex);
                return 1;
            }
        
        }


        public static void InspectMod(string fbmodPath, string gamePath, string frostyDir = null, bool verbose = false)
        {
            if (string.IsNullOrWhiteSpace(fbmodPath) || !File.Exists(fbmodPath))
                throw new FileNotFoundException("Input .fbmod not found", fbmodPath);

            if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
                throw new DirectoryNotFoundException("Game folder not found: " + gamePath);

            if (!string.IsNullOrWhiteSpace(frostyDir))
                FrostyDirOverride = frostyDir;

            ResetState();

            if (!InitializeFrosty(gamePath, verbose, true, out var assetManager, out var profileKey))
                throw new InvalidOperationException("Failed to initialize Frosty SDK.");

            var opt = new Options
            {
                InputFbmod = fbmodPath,
                OutputType = "fbproject",
                EbxOnly = false,
                Verbose = verbose,
                Writer = "custom"
            };

            ReadModAndCollectResources(opt, assetManager, profileKey);

            Console.WriteLine("---- MOD DETAILS ----");
            Console.WriteLine($"Title: {_modTitle}");
            Console.WriteLine($"Author: {_modAuthor}");
            if (!string.IsNullOrWhiteSpace(_modCategory)) Console.WriteLine($"Category: {_modCategory}");
            Console.WriteLine($"Version: {_modVersion}");
            if (!string.IsNullOrWhiteSpace(_modDescription)) Console.WriteLine($"Description: {_modDescription}");
            Console.WriteLine();
            Console.WriteLine($"Counts: EBX={ebxResources.Count} RES={resResources.Count} CHUNK={chunkResources.Count} BUNDLE={bundleResources.Count}");

            BuildLinkedFileResources(assetManager, verbose, keepResChunk: true, enableGenericLinkedRecords: false);

            Console.WriteLine($"Linked pairs detected: {linkedFileResources.Count}");
            int show = Math.Min(25, linkedFileResources.Count);
            for (int i = 0; i < show; i++)
            {
                var l = linkedFileResources[i];
                Console.WriteLine($"  [{i}] {l.DisplayName}");
                Console.WriteLine($"       res='{l.ResNameLower}' resLen={l.ResData?.Length ?? 0} resOrigSize={l.ResOriginalSize}");
                Console.WriteLine($"       chunks={l.Chunks?.Count ?? 0}");
            }
            if (linkedFileResources.Count > show)
                Console.WriteLine($"  ... ({linkedFileResources.Count - show} more)");
        }

        public static void RunDiagnostics(Options opt)
        {
            if (opt == null)
                throw new ArgumentNullException(nameof(opt));

            try { Bootstrapper.EnsureRuntimeFiles(); } catch { }

            Console.WriteLine("================ DIAGNOSTICS ================");
            Console.WriteLine($"Input: {opt.InputFbmod}");
            Console.WriteLine($"Game:  {opt.GamePath}");
            Console.WriteLine($"Writer: {opt.Writer} | ChunkLayout: {opt.ChunkLayout ?? "(default)"}");
            Console.WriteLine($"Linked fix: {(opt.DisableLinked ? "disabled" : "enabled")} | Keep RES/CHUNK: {(opt.KeepResChunk ? "yes" : "no")}");
            Console.WriteLine($"FastInit: {opt.FastInit} (diagnostics forces full init)");
            if (!string.IsNullOrWhiteSpace(opt.FrostyDirOverride))
                Console.WriteLine($"FrostyDir: {opt.FrostyDirOverride}");
            Console.WriteLine();

            if (string.IsNullOrWhiteSpace(opt.InputFbmod) || !File.Exists(opt.InputFbmod))
                throw new FileNotFoundException("Input .fbmod not found.", opt.InputFbmod ?? "");
            if (string.IsNullOrWhiteSpace(opt.GamePath) || !Directory.Exists(opt.GamePath))
                throw new DirectoryNotFoundException("Game path not found: " + (opt.GamePath ?? ""));

            if (!string.IsNullOrWhiteSpace(opt.FrostyDirOverride))
                FrostyDirOverride = opt.FrostyDirOverride;

            ResetState();

            // Full init so base lookups are meaningful.
            if (!InitializeFrosty(opt.GamePath, verbose: true, fastInit: false, out var am, out var profileKey))
                throw new InvalidOperationException("Failed to initialize Frosty SDK.");

            try { FrostyApp.AssetManager = am; } catch { }

            Console.WriteLine($"Profile: {ProfilesLibrary.ProfileName} | key={profileKey}");
            try { Console.WriteLine($"GameVersion (Head): 0x{CurrentGameVersion:X8}"); } catch { }

            // Base DB sanity
            try
            {
                int sbCount = TryCountEnumerableMethod(am, "EnumerateSuperBundles", new object[] { false });
                int bCount = TryCountEnumerableMethod(am, "EnumerateBundles", new object[] { -1, false });
                if (bCount <= 0)
                {
                    // Some forks use (BundleType type, bool modifiedOnly)
                    bCount = TryCountEnumerableMethod(am, "EnumerateBundles", new object[] { (BundleType)(-1), false });
                }
                Console.WriteLine($"Base DB: superbundles={sbCount} bundles={bCount}");
            }
            catch { }
            Console.WriteLine();

            // Read mod via FrostyCore so we can see authoritative flags (IsAdded/HasHandler/etc)
            FrostyMod mod = null;
            try
            {
                mod = new FrostyMod(opt.InputFbmod);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR: FrostyMod failed to open the fbmod: " + ex.Message);
                throw;
            }

            try
            {
                var md = mod.ModDetails;
                if (md != null)
                {
                    Console.WriteLine("---- MOD DETAILS ----");
                    Console.WriteLine($"Title: {md.Title}");
                    Console.WriteLine($"Author: {md.Author}");
                    Console.WriteLine($"Category: {md.Category}");
                    Console.WriteLine($"Version: {md.Version}");
                    if (!string.IsNullOrWhiteSpace(md.Description))
                        Console.WriteLine($"Description: {md.Description}");
                    Console.WriteLine();
                }
            }
            catch { }

            var all = (mod.Resources ?? Enumerable.Empty<BaseModResource>()).ToList();
            Console.WriteLine($"Resources total: {all.Count}");
            foreach (var g in all.GroupBy(r => r.Type).OrderBy(g => g.Key.ToString()))
            {
                int added = g.Count(r => SafeIsAdded(r));
                int handler = g.Count(r => SafeHasHandler(r));
                Console.WriteLine($"  {g.Key,-10} count={g.Count(),-5} IsAdded={added,-5} HasHandler={handler,-5}");
            }
            Console.WriteLine();

            // Decode sanity: sample a few RES/CHUNK payloads
            int resDecodeOk = 0, resDecodeFail = 0;
            int chunkDecodeOk = 0, chunkDecodeFail = 0;
            long resBytes = 0, chunkBytes = 0;

            int resShown = 0;
            Console.WriteLine("---- RES SAMPLE (decision trace) ----");
            foreach (var r in all.Where(r => r.Type == ModResourceType.Res).Take(40))
            {
                var tmp = new ResAssetEntry();
                try { r.FillAssetEntry(tmp); } catch { }

                bool baseByRid = false;
                bool baseByName = false;
                try { baseByRid = (am.GetResEntry(tmp.ResRid) != null); } catch { }
                try { baseByName = (!string.IsNullOrWhiteSpace(r.Name) && am.GetResEntry(r.Name) != null); } catch { }

                int dataLen = 0;
                try
                {
                    var bytes = mod.GetResourceData(r);
                    if (bytes != null && bytes.Length > 0) { resDecodeOk++; resBytes += bytes.LongLength; dataLen = bytes.Length; }
                    else { resDecodeFail++; }
                }
                catch { resDecodeFail++; }

                Console.WriteLine($"[{resShown:00}] isAdded(mod)={SafeIsAdded(r)} baseRid={baseByRid} baseName={baseByName} rid=0x{tmp.ResRid:X16} type=0x{tmp.ResType:X8} size={r.Size} dataLen={dataLen} name='{r.Name}'");
                resShown++;
            }
            if (resShown == 0) Console.WriteLine("(no RES resources)");
            Console.WriteLine();

            int chunkShown = 0;
            Console.WriteLine("---- CHUNK SAMPLE (decision trace) ----");
            foreach (var r in all.Where(r => r.Type == ModResourceType.Chunk).Take(40))
            {
                var tmp = new ChunkAssetEntry();
                try { r.FillAssetEntry(tmp); } catch { }

                bool baseById = false;
                try { baseById = (am.GetChunkEntry(tmp.Id) != null); } catch { }

                int dataLen = 0;
                try
                {
                    var bytes = mod.GetResourceData(r);
                    if (bytes != null && bytes.Length > 0) { chunkDecodeOk++; chunkBytes += bytes.LongLength; dataLen = bytes.Length; }
                    else { chunkDecodeFail++; }
                }
                catch { chunkDecodeFail++; }

                Console.WriteLine($"[{chunkShown:00}] isAdded(mod)={SafeIsAdded(r)} baseId={baseById} id={tmp.Id} size={r.Size} dataLen={dataLen}");
                chunkShown++;
            }
            if (chunkShown == 0) Console.WriteLine("(no CHUNK resources)");
            Console.WriteLine();

            Console.WriteLine($"Decode summary: RES ok={resDecodeOk} fail={resDecodeFail} bytes={resBytes} | CHUNK ok={chunkDecodeOk} fail={chunkDecodeFail} bytes={chunkBytes}");
            Console.WriteLine();

            // Now run the tool's own pipeline to show what it will write (counts + Added tables)
            Console.WriteLine("---- TOOL PIPELINE PREVIEW ----");
            ResetState();

            // Force the important safe defaults for preview.
            var previewOpt = new Options
            {
                InputFbmod = opt.InputFbmod,
                Output = opt.Output,
                GamePath = opt.GamePath,
                OutputType = "fbproject",
                EbxOnly = opt.EbxOnly,
                Verbose = true,
                KeepResChunk = opt.KeepResChunk,
                DisableLinked = opt.DisableLinked,
                EnableGenericLinkedRecords = opt.EnableGenericLinkedRecords,
                Writer = opt.Writer,
                ChunkLayout = opt.ChunkLayout,
                FrostyDirOverride = opt.FrostyDirOverride,
                FastInit = false
            };

            ReadModAndCollectResources(previewOpt, am, profileKey);

            int addedRes = resResources.Count(x => x != null && x.IsAdded);
            int addedChunks = chunkResources.Count(x => x != null && x.IsAdded);
            int addedEbx = ebxResources.Count(x => x != null && x.IsAdded);
            Console.WriteLine($"Collected: EBX={ebxResources.Count} RES={resResources.Count} CHUNK={chunkResources.Count} BUNDLE={bundleResources.Count}");
            Console.WriteLine($"Added flags (tool): EBX={addedEbx} RES={addedRes} CHUNK={addedChunks}");

            int beforeRes = resResources.Count;
            int beforeChunk = chunkResources.Count;
            if (!previewOpt.DisableLinked)
            {
                BuildLinkedFileResources(am, verbose: true, keepResChunk: previewOpt.KeepResChunk, enableGenericLinkedRecords: previewOpt.EnableGenericLinkedRecords);
                Console.WriteLine($"Linked records: {linkedFileResources.Count}");
                Console.WriteLine($"After linked: RES={resResources.Count} (delta {resResources.Count - beforeRes}), CHUNK={chunkResources.Count} (delta {chunkResources.Count - beforeChunk})");

                if (!previewOpt.KeepResChunk && linkedFileResources.Count > 0)
                    Console.WriteLine("NOTE: Linked fix enabled AND Keep RES/CHUNK is OFF. This will shrink output size and can result in CHUNK=0.");
            }
            else
            {
                Console.WriteLine("Linked fix disabled.");
            }

            Console.WriteLine("================ END DIAGNOSTICS ================");
        }

        private static bool SafeIsAdded(BaseModResource r)
        {
            try { return r != null && r.IsAdded; } catch { return false; }
        }
        private static bool SafeHasHandler(BaseModResource r)
        {
            try { return r != null && r.HasHandler; } catch { return false; }
        }

        private static int TryCountEnumerableMethod(object target, string methodName, object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
                return 0;

            try
            {
                var t = target.GetType();
                var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var m in methods)
                {
                    try
                    {
                        var pars = m.GetParameters();
                        if (pars.Length != (args?.Length ?? 0))
                            continue;

                        bool ok = true;
                        object[] coerced = new object[pars.Length];
                        for (int i = 0; i < pars.Length; i++)
                        {
                            object a = args[i];
                            var pt = pars[i].ParameterType;
                            if (a == null)
                            {
                                coerced[i] = null;
                                continue;
                            }

                            if (pt.IsInstanceOfType(a))
                            {
                                coerced[i] = a;
                                continue;
                            }

                            try
                            {
                                // allow enum/int coercions
                                if (pt.IsEnum && a is int ai)
                                {
                                    coerced[i] = Enum.ToObject(pt, ai);
                                }
                                else if (pt == typeof(int) && a is Enum ae)
                                {
                                    coerced[i] = Convert.ToInt32(ae);
                                }
                                else
                                {
                                    coerced[i] = Convert.ChangeType(a, pt);
                                }
                            }
                            catch
                            {
                                ok = false;
                                break;
                            }
                        }

                        if (!ok)
                            continue;

                        var enumerable = m.Invoke(target, coerced) as System.Collections.IEnumerable;
                        if (enumerable == null)
                            return 0;

                        int count = 0;
                        foreach (var _ in enumerable)
                            count++;
                        return count;
                    }
                    catch { }
                }
            }
            catch { }
            return 0;
        }

        private static void ResetState()
        {
            ebxResources.Clear();
            resResources.Clear();
            chunkResources.Clear();
            bundleResources.Clear();
            linkedFileResources.Clear();
            projectScreenshots.Clear();
            projectIcon = null;
            bundleHashToName.Clear();
            superBundleIdToName.Clear();

            _defaultSuperBundleName = null;

            _failedEbxCount = 0;
            _modTitle = "Unknown";
            _modAuthor = "Unknown";
            _modCategory = "";
            _modVersion = "1.0.0";
            _modDescription = "";
        }

        


        private static string _modTitle = "Unknown";
        private static string _modAuthor = "Unknown";
        private static string _modCategory = "";
        private static string _modVersion = "1.0.0";
        private static string _modDescription = "";
        private static int _failedEbxCount = 0;

        private static bool InitializeFrosty(string gamePath, bool verbose, bool fastInit, out AssetManager assetManager, out string profileKey)
        {
            Console.WriteLine("Initializing Frosty SDK...");

	        // Ensure the out parameter is definitely assigned even if initialization fails early.
	        assetManager = null;

            string gameExe = FindGameExecutable(gamePath);

            // Normalize to the actual game root (folder containing the .exe).
            string gameRoot = null;
            try { gameRoot = Path.GetDirectoryName(gameExe); } catch { gameRoot = null; }
            if (!string.IsNullOrWhiteSpace(gameRoot))
                gamePath = gameRoot;

            profileKey = Path.GetFileNameWithoutExtension(gameExe);

            CurrentProfileKey = profileKey ?? "";

            if (verbose)
                Console.WriteLine("Profile key: " + profileKey);

	            ProfilesLibrary.Initialize(new List<Profile>());
	            ProfilesLibrary.Initialize(profileKey);

	            InstallEbxClassesResolver(profileKey, verbose, FrostyDirOverride);
	            TypeLibrary.Initialize();
            // IMPORTANT: Frosty FileSystem must be initialized with the correct sources before AssetManager can
            // resolve base-game entries (GetResEntry/GetChunkEntry). Without this, projects may mark everything as
            // "Added" and FrostyProject.Load will crash on duplicates.
            FrostyApp.FileSystem = new FileSystem(gamePath);

            // Guard: if the path is wrong, FileSystem.Initialize can take an extremely long time
            // while probing/scanning for expected layouts.
            if (!Directory.Exists(Path.Combine(gamePath, "Data")))
                throw new DirectoryNotFoundException("Game path does not look like a valid SWBF2 install (missing Data folder). Please select the folder containing the game .exe.");

            Console.WriteLine("Setting up FileSystem sources...");
            try
            {
                FrostyApp.FileSystem.AddSource("Patch");
                FrostyApp.FileSystem.AddSource("Data");
                // Adding every installpackage as a FileSystem source can make Initialize() extremely slow
                // on some installations/drives. Data + Patch are sufficient for base lookups and mod extraction.
                // If you ever need installpackage sources, re-enable this behind an explicit setting.
                // TryAddInstallPackageSourcesFast(FrostyApp.FileSystem, verbose);
            }
            catch { }

            Console.WriteLine("Initializing FileSystem...");
            FileSystemInitWithProgress(FrostyApp.FileSystem);
            try { CurrentGameVersion = FrostyApp.FileSystem.Head; } catch { CurrentGameVersion = 0; }

            Console.WriteLine("Initializing ResourceManager...");
            FrostyApp.ResourceManager = new ResourceManager(FrostyApp.FileSystem);
            RunWithWatchdog(() => FrostyApp.ResourceManager.Initialize(), "ResourceManager.Initialize");

	            var am = new AssetManager(FrostyApp.FileSystem, FrostyApp.ResourceManager);
	            if (!fastInit)
	            {
	                Console.WriteLine("Initializing AssetManager...");
                    Console.WriteLine("This Step may take up to 10 minutes depending on the Mod");
	                RunWithWatchdog(() => am.Initialize(), "AssetManager.Generating Cache");
	
	                Console.WriteLine("Building name caches...");
	                RunWithWatchdog(() => BuildNameCaches(am, verbose), "BuildNameCaches");
	
	                assetManager = am;
	            }
	            else
	            {
	                // Fast init mode: skip heavy cache builds. We will treat assets as "modified" (not "added")
	                // and avoid base-game lookups/fills that require AssetManager caches.
	                Console.WriteLine("Fast init enabled: skipping AssetManager cache build");
	                assetManager = null;
	            }

            FrostyApp.AssetManager = null;

            return true;
        }

        private static void RunWithWatchdog(Action action, string label)
        {
            if (action == null)
                return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var secs = (int)sw.Elapsed.TotalSeconds;
                    if (secs > 0 && secs % 2 == 0)
                        Console.WriteLine($"{label}... ({secs}s)");
                }
                catch { }
            }, null, 2000, 2000))
            {
                action();
            }
            sw.Stop();
            Console.WriteLine($"{label} completed in {sw.Elapsed.TotalSeconds:0.0}s");
        }

        private static void TryAddInstallPackageSourcesFast(FileSystem fs, bool verbose)
        {
            try
            {
                if (fs == null)
                    return;

                string baseLayoutPath = fs.ResolvePath("native_data/layout.toc");
                if (string.IsNullOrWhiteSpace(baseLayoutPath) || !File.Exists(baseLayoutPath))
                    return;

                string patchLayoutPath = fs.ResolvePath("native_patch/layout.toc");

                DbObject baseLayout;
                using (DbReader reader = new DbReader(new FileStream(baseLayoutPath, FileMode.Open, FileAccess.Read), fs.CreateDeobfuscator()))
                    baseLayout = reader.ReadDbObject();

                DbObject layout = baseLayout;
                if (!string.IsNullOrWhiteSpace(patchLayoutPath) && File.Exists(patchLayoutPath))
                {
                    try
                    {
                        using (DbReader reader = new DbReader(new FileStream(patchLayoutPath, FileMode.Open, FileAccess.Read), fs.CreateDeobfuscator()))
                            layout = reader.ReadDbObject();
                    }
                    catch
                    {
                        layout = baseLayout;
                    }
                }

                DbObject installManifest = null;
                try { installManifest = layout.GetValue<DbObject>("installManifest"); } catch { installManifest = null; }
                if (installManifest == null)
                    return;

                DbObject installChunks = null;
                try { installChunks = installManifest.GetValue<DbObject>("installChunks"); } catch { installChunks = null; }
                if (installChunks == null)
                    return;

                int added = 0;

                foreach (DbObject installChunk in installChunks)
                {
                    if (installChunk == null)
                        continue;

                    try
                    {
                        if (installChunk.GetValue<bool>("testDLC"))
                            continue;
                    }
                    catch { }

                    string name = null;
                    try { name = installChunk.GetValue<string>("name"); } catch { name = null; }
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    // Frosty expects installpackage sources as additional roots under Data\\Win32\\<name>
                    // Use the public AddSource API so FileSystem internal invariants stay valid.
                    string relDir = Path.Combine("Data", "Win32", name.Replace('/', '\\'));
                    string fullDir = Path.Combine(fs.BasePath, relDir);
                    if (!Directory.Exists(fullDir))
                        continue;

                    string mft = Path.Combine(fullDir, "package.mft");
                    if (!File.Exists(mft))
                        continue;

                    try
                    {
                        fs.AddSource(relDir);
                        added++;
                    }
                    catch { }
                }

                if (verbose)
                    Console.WriteLine("[fs] installpackage sources added: " + added);
            }
            catch { }
        }

	        private static void InstallEbxClassesResolver(string profileKey, bool verbose, string? frostyDir)
	        {
	            if (_ebxClassesResolverInstalled)
	                return;
	            _ebxClassesResolverInstalled = true;

	            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
	            var roots = new List<string>();
	            void AddRoot(string p)
	            {
	                if (string.IsNullOrWhiteSpace(p)) return;
	                try
	                {
	                    string full = Path.GetFullPath(p);
	                    if (Directory.Exists(full) && !roots.Contains(full, StringComparer.OrdinalIgnoreCase))
	                        roots.Add(full);
	                }
	                catch { }
	            }

	            AddRoot(frostyDir);
	            AddRoot(FrostyDirOverride);
	            AddRoot(exeDir);
	            AddRoot(Path.Combine(exeDir, "Profiles"));
	            AddRoot(Path.Combine(exeDir, "Profiles", profileKey));
	            if (!string.IsNullOrWhiteSpace(frostyDir))
	            {
	                AddRoot(Path.Combine(frostyDir, "Profiles"));
	                AddRoot(Path.Combine(frostyDir, "Profiles", profileKey));
	            }
	            if (!string.IsNullOrWhiteSpace(FrostyDirOverride))
	            {
	                AddRoot(Path.Combine(FrostyDirOverride, "Profiles"));
	                AddRoot(Path.Combine(FrostyDirOverride, "Profiles", profileKey));
	            }

	            Assembly ResolveEbxClasses()
	            {

	                string[] commonFiles =
	                {
	                    Path.Combine(exeDir, "EbxClasses.dll"),
	                    Path.Combine(exeDir, "Profiles", profileKey, "EbxClasses.dll"),
	                    Path.Combine(exeDir, "Profiles", "EbxClasses.dll"),
	                };

	                foreach (var f in commonFiles)
	                {
	                    try
	                    {
	                        if (!File.Exists(f))
	                            continue;
	                        var an = AssemblyName.GetAssemblyName(f);
	                        if (an != null && an.Name.Equals("EbxClasses", StringComparison.OrdinalIgnoreCase))
	                            return Assembly.LoadFrom(f);
	                    }
	                    catch { }
	                }

                try
                {
                    if (profileKey.Equals("starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] rootsTry =
                        {
                            frostyDir,
                            FrostyDirOverride,
                            Path.Combine(frostyDir ?? "", "Profiles"),
                            Path.Combine(FrostyDirOverride ?? "", "Profiles"),
                        };

                        foreach (var r0 in rootsTry)
                        {
                            if (string.IsNullOrWhiteSpace(r0)) continue;
                            string dll = Path.Combine(r0, "StarWarsIISDK.dll");
                            if (!File.Exists(dll)) continue;
                            var an = AssemblyName.GetAssemblyName(dll);
                            if (an != null && an.Name.Equals("EbxClasses", StringComparison.OrdinalIgnoreCase))
                            {
                                if (verbose)
                                    Console.WriteLine("Resolved EbxClasses from: " + dll);
                                return Assembly.LoadFrom(dll);
                            }
                        }
                    }
                }
                catch { }

                int ScoreSdk(string pk, string dllPath)
                {
                    string fn = Path.GetFileNameWithoutExtension(dllPath).ToLowerInvariant();
                    string p = pk.ToLowerInvariant();
                    int score = 0;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(FrostyDirOverride))
                        {
                            string root = Path.GetFullPath(FrostyDirOverride).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            string full = Path.GetFullPath(dllPath);
                            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                                score += 100000;
                        }
                        if (!string.IsNullOrWhiteSpace(frostyDir))
                        {
                            string root2 = Path.GetFullPath(frostyDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            string full2 = Path.GetFullPath(dllPath);
                            if (full2.StartsWith(root2, StringComparison.OrdinalIgnoreCase))
                                score += 100000;
                        }
                    }
                    catch { }

                    if (fn.Contains("sdk")) score += 10;

                    if (p == "starwarsbattlefrontii")
                    {
                        if (fn.Contains("swbf2")) score += 260;
                        if (fn.Contains("starwars")) score += 220;
                        if (fn.Contains("battlefront")) score += 200;
                        if (fn.Contains("ii")) score += 120;
                        if (fn.Contains("anthem")) score -= 500;
                    }

                    if (fn.Contains("anthem")) score -= 150;
                    if (fn.Contains("fifa")) score -= 80;
                    if (fn.Contains("madden")) score -= 80;

                    score -= fn.Length;
                    return score;
                }

                var candidates = new List<string>();
                foreach (var r in roots)
                {
                    try
                    {
                        if (!Directory.Exists(r))
                            continue;

                        foreach (var dll in Directory.EnumerateFiles(r, "*SDK.dll", SearchOption.AllDirectories))
                            candidates.Add(dll);
                    }
                    catch { }
                }

                candidates = candidates
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(p => ScoreSdk(profileKey, p))
                    .ToList();

                foreach (var dll in candidates)
                {
                    try
                    {
                        var an = AssemblyName.GetAssemblyName(dll);
                        if (an != null && an.Name.Equals("EbxClasses", StringComparison.OrdinalIgnoreCase))
                        {
                            if (verbose)
                                Console.WriteLine("Resolved EbxClasses from: " + dll);
                            return Assembly.LoadFrom(dll);
                        }
                    }
                    catch { }
                }

                var anyDlls = new List<string>();
                foreach (var r in roots)
                {
                    try
                    {
                        if (!Directory.Exists(r))
                            continue;

                        foreach (var dll in Directory.EnumerateFiles(r, "*.dll", SearchOption.AllDirectories))
                            anyDlls.Add(dll);
                    }
                    catch { }
                }

                foreach (var dll in anyDlls
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(p => ScoreSdk(profileKey, p)))
                {
                    try
                    {
                        var an = AssemblyName.GetAssemblyName(dll);
                        if (an != null && an.Name.Equals("EbxClasses", StringComparison.OrdinalIgnoreCase))
                        {
                            if (verbose)
                                Console.WriteLine("Resolved EbxClasses from: " + dll);
                            return Assembly.LoadFrom(dll);
                        }
                    }
                    catch { }
                }

                return null;
}

	            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
	            {
	                try
	                {
	                    string req = new AssemblyName(e.Name).Name;
	                    if (!req.Equals("EbxClasses", StringComparison.OrdinalIgnoreCase))
	                        return null;
	                    return ResolveEbxClasses();
	                }
	                catch
	                {
	                    return null;
	                }
	            };
	        }

        private static void BuildNameCaches(AssetManager am, bool verbose)
        {
            void AddToCache(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;

                string nameNoWin32 = name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase)
                    ? name.Substring(6)
                    : name;

	                string normalized = name.Replace('\\', '/').TrimEnd('/');
	                int lastSlash = normalized.LastIndexOf('/');
	                string baseName = (lastSlash >= 0 && lastSlash + 1 < normalized.Length)
	                    ? normalized.Substring(lastSlash + 1)
	                    : normalized;

                string[] variations =
                {
                    name,
                    name.ToLowerInvariant(),
                    name.Replace("\\", "/"),
                    name.Replace("\\", "/").ToLowerInvariant(),
                    nameNoWin32,
                    nameNoWin32.ToLowerInvariant(),
	                    baseName,
	                    baseName.ToLowerInvariant(),
                };

                foreach (var v in variations)
                {
                    int h = Fnv1.HashString(v);
                    if (!bundleHashToName.ContainsKey(h))
                        bundleHashToName[h] = name;

                    unchecked
                    {
                        uint h1a = 0x811c9dc5;
                        foreach (char c in v)
                            h1a = (h1a ^ (byte)c) * 0x01000193;
                        int hi = (int)h1a;
                        if (!bundleHashToName.ContainsKey(hi))
                            bundleHashToName[hi] = name;
                    }
                }
            }

            Console.WriteLine("Building bundle hash cache...");
            foreach (var b in am.EnumerateBundles())
                AddToCache(b.Name);

            Console.WriteLine("Building superbundle cache...");
            int i = 0;
            string firstSb = null;
            foreach (var sb in am.EnumerateSuperBundles())
            {

                int sbId = i;
                try
                {
                    var p = sb.GetType().GetProperty("Id")
                            ?? sb.GetType().GetProperty("SuperBundleId")
                            ?? sb.GetType().GetProperty("Index");
                    if (p != null)
                        sbId = Convert.ToInt32(p.GetValue(sb));
                }
                catch { }

                if (!superBundleIdToName.ContainsKey(sbId))
                    superBundleIdToName.Add(sbId, sb.Name);
                if (firstSb == null && !string.IsNullOrWhiteSpace(sb.Name))
                    firstSb = sb.Name;

                AddToCache(sb.Name);

                if (verbose)
                    Console.WriteLine($"  SB[{sbId}] {sb.Name}");

                i++;
            }

            _defaultSuperBundleName = firstSb;
            if (verbose)
                Console.WriteLine("Default superbundle: " + (_defaultSuperBundleName ?? "(none)"));

            try
            {
                DumpAssetManagerResApis(am);
                Console.Out.Flush();
            }
            catch { }
        }

        private static string _defaultSuperBundleName = null;
        private static Exception _firstResourceDataError = null;

        private static void SanitizeAddedFlags(AssetManager am, bool verbose)
        {
            // Prevent FrostyProject load crashes: never emit Added RES/CHUNK entries
            // that already exist in the base game, and remove duplicates from Added tables.

            if (resResources != null && resResources.Count > 0)
            {
                var seenRidType = new HashSet<(ulong rid, uint type)>();
                var seenName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var r in resResources)
                {
                    if (r == null)
                        continue;

                    // If there is no payload, it cannot be a new (added) resource.
                    if (r.Data == null || r.Data.Length == 0)
                        r.IsAdded = false;

                    // Base-game presence check: if it exists, it must not be treated as Added.
                    try
                    {
                        if (am != null)
                        {
                            ResAssetEntry baseEntry = null;
                            try { baseEntry = am.GetResEntry(r.ResRid); } catch { baseEntry = null; }
                            if (baseEntry == null && !string.IsNullOrWhiteSpace(r.Name))
                            {
                                try { baseEntry = am.GetResEntry(r.Name); } catch { baseEntry = null; }
                            }

                            if (baseEntry != null)
                                r.IsAdded = false;
                        }
                    }
                    catch { }

                    if (r.IsAdded)
                    {
                        var key = (r.ResRid, r.ResType);
                        if (!seenRidType.Add(key))
                        {
                            r.IsAdded = false;
                            if (verbose)
                                Console.WriteLine($"[dedupe] dropping Added RES duplicate rid=0x{r.ResRid:X16} type=0x{r.ResType:X8}");
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(r.Name) && !seenName.Add(r.Name))
                        {
                            r.IsAdded = false;
                            if (verbose)
                                Console.WriteLine($"[dedupe] dropping Added RES duplicate name={r.Name}");
                        }
                    }
                }
            }

            if (chunkResources != null && chunkResources.Count > 0)
            {
                var seen = new HashSet<Guid>();
                var mGet = am != null ? am.GetType().GetMethod("GetChunkEntry", new[] { typeof(Guid) }) : null;

                foreach (var c in chunkResources)
                {
                    if (c == null)
                        continue;

                    // If there is no payload, it cannot be a new (added) chunk.
                    if (c.Data == null || c.Data.Length == 0)
                        c.IsAdded = false;

                    // Base-game presence check.
                    try
                    {
                        if (mGet != null)
                        {
                            var existing = mGet.Invoke(am, new object[] { c.Id });
                            if (existing != null)
                                c.IsAdded = false;
                        }
                    }
                    catch { }

                    if (c.IsAdded)
                    {
                        if (!seen.Add(c.Id))
                        {
                            c.IsAdded = false;
                            if (verbose)
                                Console.WriteLine($"[dedupe] dropping Added CHUNK duplicate {c.Id}");
                        }
                    }
                }
            }
        }


        private static void ReadModAndCollectResources(Options opt, AssetManager am, string profileKey)
        {
            Console.WriteLine("Reading .fbmod ...");

            _firstResourceDataError = null;

            using (var fs = new FileStream(opt.InputFbmod, FileMode.Open, FileAccess.Read))
            using (var modReader = new FrostyModReader(fs))
            {
                if (!modReader.IsValid)
                    throw new InvalidOperationException("Invalid .fbmod (FrostyModReader.IsValid == false)");

                var details = modReader.ReadModDetails();
                _modTitle = details.Title ?? "Unknown";
                _modAuthor = details.Author ?? "Unknown";
                _modCategory = details.Category ?? "";
                _modVersion = details.Version ?? "1.0.0";
                _modDescription = details.Description ?? "";

                var resources = modReader.ReadResources();
                Console.WriteLine($"Resources: {resources?.Length ?? 0}");

                if (resources != null)
                {
                    foreach (var r in resources)
                    {
                        if (r.Type == ModResourceType.Bundle)
                        {
                            if (!string.IsNullOrWhiteSpace(r.Name))
                            {
                                int h = Fnv1.HashString(r.Name);
                                if (!bundleHashToName.ContainsKey(h))
                                    bundleHashToName[h] = r.Name;
                            }
                        }
                    }

                                        var newBundleAnchorEbx = new Dictionary<int, string>();
                    if (bundleHashToName.Count > 0)
                    {
                        foreach (var kv in bundleHashToName)
                        {
                            var bn = (kv.Value ?? string.Empty).Trim();
                            if (bn.StartsWith("win32/", StringComparison.OrdinalIgnoreCase))
                                bn = bn.Substring("win32/".Length);
                            newBundleAnchorEbx[kv.Key] = bn;
                        }
                    }

foreach (var r in resources)
                    {
                        byte[] data = null;
                        try { data = modReader.GetResourceData(r); }
                        catch (Exception ex)
                        {
                            if (_firstResourceDataError == null)
                                _firstResourceDataError = ex;
                            if (opt.Verbose)
                                Console.WriteLine($"[warn] GetResourceData failed for {r.Type} '{r.Name}': {ex.GetType().Name}: {ex.Message}");
                        }

                        var bundles = r.AddedBundles?.ToList() ?? new List<int>();

                        if (bundles.Count > 0 && newBundleAnchorEbx.Count > 0)
                        {
                            if (r.Type == ModResourceType.Ebx)
                            {
                                var filtered = new List<int>(bundles.Count);
                                foreach (var bh in bundles)
                                {
                                    if (!newBundleAnchorEbx.TryGetValue(bh, out var anchor) || string.Equals(r.Name, anchor, StringComparison.OrdinalIgnoreCase))
                                        filtered.Add(bh);
                                }
                                bundles = filtered;
                            }
                            else if (r.Type == ModResourceType.Res || r.Type == ModResourceType.Chunk)
                            {
                                var filtered = new List<int>(bundles.Count);
                                foreach (var bh in bundles)
                                {
                                    if (!newBundleAnchorEbx.ContainsKey(bh))
                                        filtered.Add(bh);
                                }
                                bundles = filtered;
                            }
                        }

                        bool hasData = data != null && data.Length > 0;
                        bool hasBundles = bundles.Count > 0;

                        if (!hasData && !hasBundles && r.Type != ModResourceType.Bundle && r.Type != ModResourceType.Embedded)
                            continue;

                        switch (r.Type)
                        {
                            case ModResourceType.Ebx:
                                ProcessEbx(r, data, bundles);
                                break;
                            case ModResourceType.Res:
                                if (!opt.EbxOnly)
                                    ProcessRes(am, r, data, bundles, opt.FastInit);
                                break;
                            case ModResourceType.Chunk:
                                if (!opt.EbxOnly)
                                    ProcessChunk(am, r, data, bundles, opt.FastInit);
                                break;
                            case ModResourceType.Bundle:
                                if (!opt.EbxOnly)
                                    ProcessBundle(r);
                                break;
                            case ModResourceType.Embedded:
                                ProcessEmbedded(r, data);
                                break;
                        }
                    }
                }
            }
        }

        private static void FillMissingResFromBase(AssetManager am, bool verbose)
        {
            if (am == null || resResources.Count == 0)
            {
                Console.WriteLine("[res] base RES fill: skipped (no AssetManager or no RES)");
                return;
            }

            int missing = 0;
            int tried = 0;
            int filled = 0;
            int noEntry = 0;
            int streamNull = 0;
            int emptyStream = 0;
            int errors = 0;
            int logged = 0;

            foreach (var rr in resResources)
            {
                if (rr == null)
                    continue;
                if (rr.Data != null && rr.Data.Length > 0)
                    continue;

                missing++;
                tried++;

                ResAssetEntry be = null;
                try
                {
                    be = am.GetResEntry(rr.ResRid);
                }
                catch (Exception ex)
                {
                    errors++;
                    if (verbose && logged++ < 10)
                        Console.WriteLine($"[res] GetResEntry exception rid=0x{rr.ResRid:X16} {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                if (be == null)
                {
                    noEntry++;
                    if (verbose && logged++ < 10)
                        Console.WriteLine($"[res] GetResEntry null rid=0x{rr.ResRid:X16} name={rr.Name}");
                    continue;
                }

                try
                {
                    using (var st = am.GetRes(be))
                    {
                        if (st == null)
                        {
                            streamNull++;
                            if (verbose && logged++ < 10)
                                Console.WriteLine($"[res] GetRes stream null rid=0x{rr.ResRid:X16} name={rr.Name}");
                            continue;
                        }

                        if (st.CanSeek)
                            st.Position = 0;

                        byte[] bytes;
                        using (var ms = new MemoryStream())
                        {
                            try
                            {
                                st.CopyTo(ms);
                            }
                            catch
                            {

                                ms.SetLength(0);
                                var buf = new byte[256 * 1024];
                                int n;
                                while ((n = st.Read(buf, 0, buf.Length)) > 0)
                                    ms.Write(buf, 0, n);
                            }
                            bytes = ms.ToArray();
                        }

                        if (bytes != null && bytes.Length > 0)
                        {
                            rr.Data = bytes;
                            filled++;

                            try
                            {
                                var pSha1 = be.GetType().GetProperty("Sha1") ?? be.GetType().GetProperty("SHA1") ?? be.GetType().GetProperty("Hash");
                                var v = pSha1?.GetValue(be);
                                if (rr.Sha1 == null && v is byte[] b && b.Length == 20)
                                    rr.Sha1 = b;
                            }
                            catch { }

                            if (verbose && logged++ < 10)
                                Console.WriteLine($"[res] base filled rid=0x{rr.ResRid:X16} bytes={bytes.Length} name={rr.Name}");
                        }
                        else
                        {
                            emptyStream++;
                            if (verbose && logged++ < 10)
                                Console.WriteLine($"[res] base stream empty rid=0x{rr.ResRid:X16} name={rr.Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    if (verbose && logged++ < 10)
                        Console.WriteLine($"[res] GetRes exception rid=0x{rr.ResRid:X16} {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"[res] base RES fill: missing={missing} tried={tried} filled={filled} noEntry={noEntry} streamNull={streamNull} emptyStream={emptyStream} errors={errors}");
        }

        private static bool TryGetSwbf2ResOriginalSize(byte[]? resData, out long originalSize)
        {
            originalSize = 0;
            if (resData == null || resData.Length < 8)
                return false;

            uint size = (uint)((resData[0] << 24) | (resData[1] << 16) | (resData[2] << 8) | resData[3]);
            if (size < 16 || size > 64 * 1024 * 1024)
                return false;

            bool hasZstdMagic = false;
            int scanLen = Math.Min(64, resData.Length - 4);
            for (int i = 0; i <= scanLen; i++)
            {
                if (resData[i] == 0x28 && resData[i + 1] == 0xB5 && resData[i + 2] == 0x2F && resData[i + 3] == 0xFD)
                {
                    hasZstdMagic = true;
                    break;
                }
            }

            if (!hasZstdMagic)
                return false;

            originalSize = (long)size;
            return true;
        }

        private static void BuildLinkedFileResources(AssetManager am, bool verbose, bool keepResChunk, bool enableGenericLinkedRecords)
        {

            linkedFileResources.Clear();

            if (resResources.Count == 0)
                return;

            var usedRes = new HashSet<ResResourceData>();
            var usedChunks = new HashSet<Guid>();

            TryBuildSwbf2MeshSetDepotLinkedRecord(am, verbose, usedRes, usedChunks);

            if (!enableGenericLinkedRecords)
            {
                if (linkedFileResources.Count > 0 && !keepResChunk)
                {
                    resResources.RemoveAll(r => usedRes.Contains(r));
                    chunkResources.RemoveAll(c => usedChunks.Contains(c.Id));
                }
                return;
            }

            Func<List<int>, string> makeBundleKey = (bundles) =>
            {
                if (bundles == null || bundles.Count == 0)
                    return "";
                var arr = bundles.ToArray();
                Array.Sort(arr);
                return string.Join(",", arr.Select(x => x.ToString("X8")));
            };

            var resGroups = new Dictionary<string, List<ResResourceData>>();
            foreach (var r in resResources)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.Name) || r.Data == null || r.Data.Length == 0)
                    continue;
                if (usedRes.Contains(r))
                    continue;

                string key = makeBundleKey(r.AddedBundles);
                if (!resGroups.TryGetValue(key, out var list))
                {
                    list = new List<ResResourceData>();
                    resGroups[key] = list;
                }
                list.Add(r);
            }

            if (resGroups.Count == 0)
            {

                if (linkedFileResources.Count > 0 && !keepResChunk)
                {
                    resResources.RemoveAll(r => usedRes.Contains(r));
                    chunkResources.RemoveAll(c => usedChunks.Contains(c.Id));
                }
                return;
            }

            var chunkByKey = new Dictionary<string, List<ChunkResourceData>>();
            foreach (var c in chunkResources)
            {
                if (c == null || c.Id == Guid.Empty || c.Data == null || c.Data.Length == 0)
                    continue;
                if (usedChunks.Contains(c.Id))
                    continue;

                string key = makeBundleKey(c.AddedBundles);
                if (!chunkByKey.TryGetValue(key, out var list))
                {
                    list = new List<ChunkResourceData>();
                    chunkByKey[key] = list;
                }
                list.Add(c);
            }

            foreach (var kv in resGroups)
            {
                string key = kv.Key;
                var groupRes = kv.Value;

                var primaryRes = groupRes
                    .OrderByDescending(r => (r.Name ?? "").Count(ch => ch == '/' || ch == '\\'))
                    .ThenByDescending(r => (r.Name ?? "").Length)
                    .FirstOrDefault();

                chunkByKey.TryGetValue(key, out var groupChunks);
                groupChunks = groupChunks ?? new List<ChunkResourceData>();

                foreach (var res in groupRes)
                {
                    string resLower = (res.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
                    string displayFallback = (res.Name ?? resLower).Replace('\\', '/').TrimStart('/');

                    string displayName = TryResolveResDisplayName(am, res.ResRid, displayFallback);

                    long origSize = TryResolveResOriginalSize(am, res.ResRid, res.OriginalSize);
                    if (TryGetSwbf2ResOriginalSize(res.Data, out long swbf2OrigSize))
                        origSize = swbf2OrigSize;
                    if (origSize <= 0)
                        origSize = res.Data?.LongLength ?? 0;

                    var lf = new LinkedFileResourceData
                    {
                        DisplayName = displayName,
                        ResNameLower = resLower,
                        ResData = res.Data,
                        ResOriginalSize = origSize,
                        ResMeta = res.ResMeta
                    };

                    if (res == primaryRes && groupChunks.Count > 0)
                    {
                        foreach (var ch in groupChunks)
                        {
                            int firstMip = ch.FirstMip;
                            bool addToChunkBundle = ch.AddToChunkBundle;
                            TryResolveChunkMeta(am, ch.Id, ref firstMip, ref addToChunkBundle);

                            lf.Chunks.Add(new LinkedChunkData
                            {
                                Id = ch.Id,
                                Data = ch.Data,
                                H32 = ch.H32,
                                LogicalOffset = ch.LogicalOffset,
                                LogicalSize = ch.LogicalSize,
                                RangeStart = ch.RangeStart,
                                RangeEnd = ch.RangeEnd,
                                FirstMip = firstMip,
                                AddToChunkBundle = addToChunkBundle
                            });

                            usedChunks.Add(ch.Id);
                        }
                    }

                    linkedFileResources.Add(lf);
                    usedRes.Add(res);

                    if (verbose)
                        Console.WriteLine($"[linked] {displayName} <- {resLower} (chunks={lf.Chunks.Count})");
                }
            }

            bool hasMeshSetDepotLinked = linkedFileResources.Any(l => l != null && l.IsSwbf2MeshSetDepot);

            if (hasMeshSetDepotLinked && keepResChunk)
            {
                if (verbose)
                    Console.WriteLine("[linked][meshset-depot] preserving RES/CHUNK and disabling linked record (stability mode).");

                linkedFileResources.RemoveAll(l => l != null && l.IsSwbf2MeshSetDepot);
            }

            if (linkedFileResources.Count > 0 && !keepResChunk)
            {

                resResources.RemoveAll(r => usedRes.Contains(r));
                chunkResources.RemoveAll(c => usedChunks.Contains(c.Id));
            }
        }

        private static bool TryBuildSwbf2MeshSetDepotLinkedRecord(AssetManager am, bool verbose, HashSet<ResResourceData> usedRes, HashSet<Guid> usedChunks)
        {

const string markerFull = "MeshSetPlugin.Resources.ModifiedShaderBlockDepot";
const string markerShort = "ModifiedShaderBlockDepot";

var blocksCandidates = new List<ResResourceData>();
foreach (var r in resResources)
{
    if (r == null || r.Data == null || r.Data.Length == 0 || string.IsNullOrWhiteSpace(r.Name))
        continue;

    string nameLower = (r.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    if (nameLower.EndsWith("/blocks"))
        blocksCandidates.Add(r);
}

var resByLower = new Dictionary<string, ResResourceData>();
foreach (var r in resResources)
{
    if (r == null || string.IsNullOrWhiteSpace(r.Name))
        continue;
    string nl = (r.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    if (!resByLower.ContainsKey(nl))
        resByLower[nl] = r;
}

ResResourceData depotRes = null;
ResResourceData blocksRes = null;

ResResourceData markerRes = null;
foreach (var r in resResources)
{
    if (r == null || r.Data == null || r.Data.Length == 0 || string.IsNullOrWhiteSpace(r.Name))
        continue;

    if (IndexOfAscii(r.Data, markerFull) >= 0 || IndexOfAscii(r.Data, markerShort) >= 0 ||
        IndexOfUtf16LE(r.Data, markerFull) >= 0 || IndexOfUtf16LE(r.Data, markerShort) >= 0)
    {
        markerRes = r;
        break;
    }
}

if (markerRes == null)
    return false;

if (markerRes != null)
{
    string markerLower = (markerRes.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    if (markerLower.EndsWith("/blocks"))
    {

        blocksRes = markerRes;

        string parent = markerLower.Substring(0, markerLower.Length - "/blocks".Length);
        string meshLower = parent;

        if (meshLower.EndsWith("_mesh_mesh"))
            meshLower = meshLower.Substring(0, meshLower.Length - "_mesh_mesh".Length) + "_mesh";

        if (!resByLower.TryGetValue(meshLower, out depotRes))
        {

            if (meshLower.EndsWith("_mesh"))
            {
                string alt = meshLower.Substring(0, meshLower.Length - "_mesh".Length);
                resByLower.TryGetValue(alt, out depotRes);
            }
        }
    }
    else
    {

        depotRes = markerRes;

        if (blocksCandidates.Count > 0)
        {
            string depotLowerProbe = (depotRes.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();

            int bestPrefix = -1;
            foreach (var r in blocksCandidates)
            {
                if (r == null || r == depotRes)
                    continue;

                string nameLower = (r.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
                int common = CommonPrefixLength(depotLowerProbe, nameLower);
                if (common > bestPrefix)
                {
                    bestPrefix = common;
                    blocksRes = r;
                }
            }
        }
    }
}

if (depotRes != null && blocksRes != null)
{

    string depotLowerProbe = (depotRes.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    if (depotLowerProbe.EndsWith("/blocks"))
    {
        depotRes = null;
        blocksRes = null;
    }
}

if (depotRes == null || blocksRes == null)
{
    if (blocksCandidates.Count == 0)
        return false;

    blocksRes = blocksCandidates
        .OrderByDescending(r => (r.Name ?? "").Count(ch => ch == '/' || ch == '\\'))
        .ThenByDescending(r => (r.Name ?? "").Length)
        .FirstOrDefault();

    if (blocksRes == null)
        return false;

    string blocksLowerProbe = (blocksRes.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    string blocksParent = blocksLowerProbe.EndsWith("/blocks")
        ? blocksLowerProbe.Substring(0, blocksLowerProbe.Length - "/blocks".Length)
        : blocksLowerProbe;

    var depotCandidates = new List<string>();
    depotCandidates.Add(blocksParent);
    if (blocksParent.EndsWith("_mesh_mesh"))
        depotCandidates.Add(blocksParent.Substring(0, blocksParent.Length - "_mesh_mesh".Length) + "_mesh");

    foreach (var r in resResources)
    {
        if (r == null || r == blocksRes || r.Data == null || r.Data.Length == 0 || string.IsNullOrWhiteSpace(r.Name))
            continue;

        string nameLower = (r.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        if (nameLower.EndsWith("/blocks"))
            continue;

        if (depotCandidates.Contains(nameLower))
        {
            depotRes = r;
            break;
        }
    }

    if (depotRes == null)
    {
        int bestScore = -1;
        ResResourceData best = null;

        foreach (var r in resResources)
        {
            if (r == null || r == blocksRes || r.Data == null || r.Data.Length == 0 || string.IsNullOrWhiteSpace(r.Name))
                continue;

            string nameLower = (r.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            if (nameLower.EndsWith("/blocks"))
                continue;

            int common = CommonPrefixLength(blocksParent, nameLower);
            int bonus = nameLower.EndsWith("_mesh") ? 1000 : (nameLower.Contains("_mesh") ? 200 : 0);
            int score = common + bonus;

            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        depotRes = best;
    }

    if (depotRes == null)
        return false;
}

string depotLower = (depotRes.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();

            var referencedChunkIds = new HashSet<Guid>();
            AddReferencedChunksFromBytes(depotRes.Data, referencedChunkIds);
            AddReferencedChunksFromBytes(blocksRes.Data, referencedChunkIds);

            var candidateChunks = new List<ChunkResourceData>();

            if (referencedChunkIds.Count > 0)
            {
                foreach (var ch in chunkResources)
                {
                    if (ch == null || ch.Id == Guid.Empty || ch.Data == null || ch.Data.Length == 0)
                        continue;
                    if (referencedChunkIds.Contains(ch.Id))
                        candidateChunks.Add(ch);
                }
            }
            else
            {

                var bundleSet = new HashSet<int>();
                if (depotRes.AddedBundles != null) foreach (int h in depotRes.AddedBundles) bundleSet.Add(h);
                if (blocksRes.AddedBundles != null) foreach (int h in blocksRes.AddedBundles) bundleSet.Add(h);

                foreach (var ch in chunkResources)
                {
                    if (ch == null || ch.Id == Guid.Empty || ch.Data == null || ch.Data.Length == 0)
                        continue;

                    bool bundleMatch = false;
                    if (bundleSet.Count > 0 && ch.AddedBundles != null && ch.AddedBundles.Count > 0)
                    {
                        foreach (int h in ch.AddedBundles)
                        {
                            if (bundleSet.Contains(h))
                            {
                                bundleMatch = true;
                                break;
                            }
                        }
                    }
                    else
                    {
                        bundleMatch = true;
                    }

                    if (bundleMatch)
                        candidateChunks.Add(ch);
                }

                if (candidateChunks.Count == 0 && chunkResources.Count > 0)
                    candidateChunks.AddRange(chunkResources);
            }

string blocksLower = (blocksRes.Name ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();
            string displayName = (depotRes.Name ?? depotLower).Replace('\\', '/').TrimStart('/');

            long depotOrigSize = TryResolveResOriginalSize(am, depotRes.ResRid, depotRes.OriginalSize);
            if (depotOrigSize <= 0) depotOrigSize = depotRes.Data?.LongLength ?? 0;

            long blocksOrigSize = TryResolveResOriginalSize(am, blocksRes.ResRid, blocksRes.OriginalSize);
            if (TryGetSwbf2ResOriginalSize(blocksRes.Data, out long swbf2BlocksSize))
                blocksOrigSize = swbf2BlocksSize;
            if (blocksOrigSize <= 0) blocksOrigSize = blocksRes.Data?.LongLength ?? 0;

            var lf = new LinkedFileResourceData
            {
                DisplayName = displayName,
                ResNameLower = depotLower,

                ResData = blocksRes.Data,
                ResOriginalSize = blocksOrigSize,
                ResMeta = blocksRes.ResMeta,

                IsSwbf2MeshSetDepot = true,
                SecondaryResNameLower = blocksLower,
                SecondaryResData = depotRes.Data,
                SecondaryResOriginalSize = depotOrigSize,
                SecondaryResMeta = depotRes.ResMeta
            };

            foreach (var ch in candidateChunks)
            {
                int firstMip = ch.FirstMip;
                bool addToChunkBundle = ch.AddToChunkBundle;
                TryResolveChunkMeta(am, ch.Id, ref firstMip, ref addToChunkBundle);

                lf.Chunks.Add(new LinkedChunkData
                {
                    Id = ch.Id,
                    Data = ch.Data,
                    H32 = ch.H32,
                    LogicalOffset = ch.LogicalOffset,
                    LogicalSize = ch.LogicalSize,
                    RangeStart = ch.RangeStart,
                    RangeEnd = ch.RangeEnd,
                    FirstMip = firstMip,
                    AddToChunkBundle = addToChunkBundle
                });

                usedChunks.Add(ch.Id);
            }

            linkedFileResources.Add(lf);

            usedRes.Add(depotRes);
            usedRes.Add(blocksRes);

            if (verbose)
                Console.WriteLine($"[linked][meshset-depot] {displayName} <- {depotLower} + {blocksLower} (chunks={lf.Chunks.Count})");

            return true;
        }

        private static int IndexOfAscii(byte[] haystack, string asciiNeedle)
        {
            if (haystack == null || haystack.Length == 0 || string.IsNullOrEmpty(asciiNeedle))
                return -1;

            byte[] needle = System.Text.Encoding.ASCII.GetBytes(asciiNeedle);
            if (needle.Length == 0 || needle.Length > haystack.Length)
                return -1;

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        private static int IndexOfUtf16LE(byte[] haystack, string needleStr)
        {
            if (haystack == null || haystack.Length == 0 || string.IsNullOrEmpty(needleStr))
                return -1;

            byte[] needle = System.Text.Encoding.Unicode.GetBytes(needleStr);
            if (needle.Length == 0 || needle.Length > haystack.Length)
                return -1;

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        private static void AddReferencedChunksFromBytes(byte[] data, HashSet<Guid> outIds)
        {
            if (outIds == null || data == null || data.Length < 16)
                return;

            foreach (var ch in chunkResources)
            {
                if (ch == null || ch.Id == Guid.Empty)
                    continue;

                byte[] gb = ch.Id.ToByteArray();
                if (IndexOfBytes(data, gb) >= 0)
                    outIds.Add(ch.Id);
            }
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length)
                return -1;

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            if (a == null || b == null) return 0;
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            for (; i < n; i++)
            {
                if (a[i] != b[i])
                    break;
            }
            return i;
        }

        private static string TryResolveResDisplayName(AssetManager am, ulong resRid, string fallback)
        {
            try
            {

                var m = am.GetType().GetMethod("GetResEntry", new[] { typeof(ulong) })
                        ?? am.GetType().GetMethod("GetResEntry", new[] { typeof(long) });

                if (m != null)
                {
                    object entryObj = m.GetParameters()[0].ParameterType == typeof(long)
                        ? m.Invoke(am, new object[] { (long)resRid })
                        : m.Invoke(am, new object[] { resRid });

                    var entry = entryObj as ResAssetEntry;
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.Name))
                        return entry.Name;
                }
            }
            catch { }

            return fallback;
        }

        private static long TryResolveResOriginalSize(AssetManager am, ulong resRid, long fallback)
        {
            long size = 0;
            try
            {
                var m = am.GetType().GetMethod("GetResEntry", new[] { typeof(ulong) })
                        ?? am.GetType().GetMethod("GetResEntry", new[] { typeof(long) });

                if (m != null)
                {
                    object entryObj = m.GetParameters()[0].ParameterType == typeof(long)
                        ? m.Invoke(am, new object[] { (long)resRid })
                        : m.Invoke(am, new object[] { resRid });

                    if (entryObj != null)
                    {
                        // AssetEntry.OriginalSize/Size are fields in FrostySdk, not properties.
                        var t = entryObj.GetType();
                        var f = t.GetField("OriginalSize") ?? t.GetField("Size") ?? t.GetField("ResourceSize") ?? t.GetField("ResSize");
                        if (f != null)
                            size = Convert.ToInt64(f.GetValue(entryObj));
                        else
                        {
                            var p = t.GetProperty("OriginalSize") ?? t.GetProperty("Size") ?? t.GetProperty("ResourceSize") ?? t.GetProperty("ResSize");
                            if (p != null)
                                size = Convert.ToInt64(p.GetValue(entryObj));
                        }
                    }
                }
            }
            catch { }

            if (size <= 0)
                size = fallback;

            return size;
        }

        private struct ChunkMetaResolved
        {
            public int H32;
            public uint RangeStart;
            public uint RangeEnd;
            public uint LogicalOffset;
            public uint LogicalSize;
            public int FirstMip;
            public bool AddToChunkBundle;
        }

        private static void RepairResourceMetadata(AssetManager am, bool verbose)
        {
            int fixedChunks = 0;

            foreach (var ch in chunkResources)
            {
                if (ch == null)
                    continue;

                bool changed = false;

                if (TryResolveChunkMetaFull(am, ch.Id, out var baseMeta))
                {
                    if (ch.H32 == 0 && baseMeta.H32 != 0) { ch.H32 = baseMeta.H32; changed = true; }
                    if (ch.LogicalOffset == 0 && baseMeta.LogicalOffset != 0) { ch.LogicalOffset = baseMeta.LogicalOffset; changed = true; }
                    if (ch.LogicalSize == 0 && baseMeta.LogicalSize != 0) { ch.LogicalSize = baseMeta.LogicalSize; changed = true; }
                    if (ch.RangeStart == 0 && baseMeta.RangeStart != 0) { ch.RangeStart = baseMeta.RangeStart; changed = true; }
                    if (ch.RangeEnd == 0 && baseMeta.RangeEnd != 0) { ch.RangeEnd = baseMeta.RangeEnd; changed = true; }
                    if (ch.FirstMip == 0 && baseMeta.FirstMip != 0) { ch.FirstMip = baseMeta.FirstMip; changed = true; }
                    if (ch.FirstMip <= 0 && baseMeta.FirstMip > 0) { ch.FirstMip = baseMeta.FirstMip; changed = true; }
                    if (!ch.AddToChunkBundle && baseMeta.AddToChunkBundle) { ch.AddToChunkBundle = true; changed = true; }
                }

                if (LooksLikeTextureChunk(ch.Data))
                {
                    if (ch.FirstMip <= 0) { ch.FirstMip = 1; changed = true; }
                    if (!ch.AddToChunkBundle) { ch.AddToChunkBundle = true; changed = true; }
                    if (ch.LogicalSize == 0 && ch.Data != null) { ch.LogicalSize = (uint)ch.Data.Length; changed = true; }
                    if (ch.RangeEnd == 0 && ch.Data != null) { ch.RangeEnd = (uint)ch.Data.Length; changed = true; }
                }

                if (ch.Data != null && ch.Data.Length > 0)
                {
                    if (ch.RangeEnd == 0)
                    {
                        ch.RangeStart = 0;
                        ch.RangeEnd = (uint)ch.Data.Length;
                        changed = true;
                    }

                    if (ch.LogicalSize == 0)
                    {
                        ch.LogicalSize = (uint)Math.Max(ch.Data.Length, (int)ch.RangeEnd);
                        changed = true;
                    }

                    if (ch.RangeEnd != 0 && ch.LogicalSize != 0 && ch.LogicalSize < ch.RangeEnd && ch.RangeStart == 0)
                    {
                        ch.LogicalSize = ch.RangeEnd;
                        changed = true;
                    }

                    if (ch.RangeEnd != 0 && ch.RangeStart > ch.RangeEnd)
                    {
                        ch.RangeStart = 0;
                        changed = true;
                    }
                }

                if (changed)
                    fixedChunks++;
            }

            if (verbose)
                Console.WriteLine($"[meta] repaired chunk metadata: {fixedChunks} entries");
        }

        private static bool LooksLikeTextureChunk(byte[] data)
        {

            return data != null && data.Length >= 4 && data[0] == 0x00 && data[1] == 0x01 && data[2] == 0x00 && data[3] == 0x00;
        }

        private static bool TryResolveChunkMetaFull(AssetManager am, Guid chunkId, out ChunkMetaResolved meta)
        {
            meta = default;

            try
            {
                object entryObj = null;

                var m = am.GetType().GetMethod("GetChunkEntry", new[] { typeof(Guid) })
                        ?? am.GetType().GetMethod("GetChunk", new[] { typeof(Guid) });

                if (m != null)
                    entryObj = m.Invoke(am, new object[] { chunkId });

                if (entryObj == null)
                {
                    var em = am.GetType().GetMethod("EnumerateChunks", Type.EmptyTypes);
                    if (em != null)
                    {
                        var enumerable = em.Invoke(am, null) as System.Collections.IEnumerable;
                        if (enumerable != null)
                        {
                            foreach (var it in enumerable)
                            {
                                try
                                {
                                    var pid = it.GetType().GetProperty("Id");
                                    if (pid == null) continue;
                                    var idVal = (Guid)pid.GetValue(it);
                                    if (idVal == chunkId)
                                    {
                                        entryObj = it;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }

                if (entryObj == null)
                    return false;

                try { var p = entryObj.GetType().GetProperty("H32"); if (p != null) meta.H32 = Convert.ToInt32(p.GetValue(entryObj)); } catch { }
                try { var p = entryObj.GetType().GetProperty("RangeStart"); if (p != null) meta.RangeStart = Convert.ToUInt32(p.GetValue(entryObj)); } catch { }
                try { var p = entryObj.GetType().GetProperty("RangeEnd"); if (p != null) meta.RangeEnd = Convert.ToUInt32(p.GetValue(entryObj)); } catch { }
                try { var p = entryObj.GetType().GetProperty("LogicalOffset"); if (p != null) meta.LogicalOffset = Convert.ToUInt32(p.GetValue(entryObj)); } catch { }
                try { var p = entryObj.GetType().GetProperty("LogicalSize"); if (p != null) meta.LogicalSize = Convert.ToUInt32(p.GetValue(entryObj)); } catch { }
                try { var p = entryObj.GetType().GetProperty("FirstMip"); if (p != null) meta.FirstMip = Convert.ToInt32(p.GetValue(entryObj)); } catch { }
                try { var p = entryObj.GetType().GetProperty("AddToChunkBundle"); if (p != null && p.PropertyType == typeof(bool)) meta.AddToChunkBundle = (bool)p.GetValue(entryObj); } catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TryResolveChunkMeta(AssetManager am, Guid chunkId, ref int firstMip, ref bool addToChunkBundle)
        {
            try
            {
                object entryObj = null;

                var m = am.GetType().GetMethod("GetChunkEntry", new[] { typeof(Guid) })
                        ?? am.GetType().GetMethod("GetChunk", new[] { typeof(Guid) });

                if (m != null)
                    entryObj = m.Invoke(am, new object[] { chunkId });

                if (entryObj == null)
                {
                    var em = am.GetType().GetMethod("EnumerateChunks", Type.EmptyTypes);
                    if (em != null)
                    {
                        var enumerable = em.Invoke(am, null) as System.Collections.IEnumerable;
                        if (enumerable != null)
                        {
                            foreach (var it in enumerable)
                            {
                                try
                                {
                                    var pid = it.GetType().GetProperty("Id");
                                    if (pid == null) continue;
                                    var idVal = (Guid)pid.GetValue(it);
                                    if (idVal == chunkId)
                                    {
                                        entryObj = it;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }

                if (entryObj != null)
                {
                    try
                    {
                        var pFirstMip = entryObj.GetType().GetProperty("FirstMip");
                        if (pFirstMip != null)
                        {
                            int fm = Convert.ToInt32(pFirstMip.GetValue(entryObj));
                            if (fm > 0)
                                firstMip = fm;
                        }
                    }
                    catch { }

                    try
                    {
                        var pAdd = entryObj.GetType().GetProperty("AddToChunkBundle");
                        if (pAdd != null && pAdd.PropertyType == typeof(bool))
                            addToChunkBundle = (bool)pAdd.GetValue(entryObj);
                    }
                    catch { }
                }
            }
            catch { }

            if (firstMip <= 0) firstMip = 1;
            if (!addToChunkBundle) addToChunkBundle = true;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length)
                return -1;

            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }

            return -1;
        }

        private static Guid DeterministicGuidFromName(string name)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                    return Guid.Empty;
                using (var sha1 = SHA1.Create())
                {
                    byte[] h = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(name));
                    byte[] g = new byte[16];
                    Array.Copy(h, 0, g, 0, 16);
                    return new Guid(g);
                }
            }
            catch
            {
                return Guid.Empty;
            }
        }

        private static Guid TryGetGuidFromModResource(BaseModResource r)
        {
            if (r == null) return Guid.Empty;
            try
            {
                var t = r.GetType();
                var p = t.GetProperty("Guid") ?? t.GetProperty("FileGuid") ?? t.GetProperty("AssetGuid") ?? t.GetProperty("EbxGuid");
                if (p != null && p.PropertyType == typeof(Guid))
                    return (Guid)p.GetValue(r);
            }
            catch { }
            return Guid.Empty;
        }

        private static Guid TryReadEbxFileGuidFromBytes(byte[] ebx)
        {
            try
            {
                if (ebx == null || ebx.Length < 56)
                    return Guid.Empty;
                uint magic = BitConverter.ToUInt32(ebx, 0);
                if (magic != 0x0FB2D1CE && magic != 0x0FB4D1CE)
                    return Guid.Empty;
                int off = 4 + (4 * 3) + (2 * 6) + (4 * 3);
                if (off + 16 > ebx.Length)
                    return Guid.Empty;
                byte[] g = new byte[16];
                Buffer.BlockCopy(ebx, off, g, 0, 16);
                return new Guid(g);
            }
            catch
            {
                return Guid.Empty;
            }
        }
private static void ProcessEbx(BaseModResource r, byte[] data, List<int> bundles)
        {

            Guid guidFromResource = TryGetGuidFromModResource(r);

            byte[] processed = data;
            bool isProjectEbx = false;

            if (data != null && data.Length >= 4)
            {
                uint magic = BitConverter.ToUInt32(data, 0);
                isProjectEbx = (magic == 0x0FB2D1CE || magic == 0x0FB4D1CE);

                if (!isProjectEbx)
                {
                    try
                    {
                        using (var ms = new MemoryStream(data))
                        using (var cas = new CasReader(ms))
                            processed = cas.Read();
                    }
                    catch
                    {
                        processed = data;
                    }

                    if (processed != null && processed.Length >= 4)
                    {
                        uint m2 = BitConverter.ToUInt32(processed, 0);
                        isProjectEbx = (m2 == 0x0FB2D1CE || m2 == 0x0FB4D1CE);
                    }
                }
            }

            if (guidFromResource == Guid.Empty)
            {
                Guid gfb = TryReadEbxFileGuidFromBytes(processed ?? data);
                if (gfb != Guid.Empty)
                    guidFromResource = gfb;
            }

            bool isFsLo = false;
            try
            {
                byte[] probe = processed ?? data;
                if (probe != null && probe.Length >= 4 && probe[0] == (byte)'F' && probe[1] == (byte)'s' && probe[2] == (byte)'L' && probe[3] == (byte)'o')
                    isFsLo = true;
            }
            catch { }

            if (isFsLo)
            {
                ebxResources.Add(new EbxResourceData
                {
                    Name = r.Name,
                    Data = processed ?? data,
                    Guid = guidFromResource,
                    IsAdded = r.IsAdded,
                    AddedBundles = bundles,
                    HasCustomHandler = true,
                    HandlerHash = 0,
                    UserData = r.UserData ?? ""
                });
                return;
            }

            if (isProjectEbx && processed != null && processed.Length > 0)
            {
                Guid g = guidFromResource;
                if (g == Guid.Empty)
                    g = TryReadEbxFileGuidFromBytes(processed);
                if (g == Guid.Empty && r.IsAdded)
                    g = DeterministicGuidFromName(r.Name);

                ebxResources.Add(new EbxResourceData
                {
                    Name = r.Name,
                    Data = processed,
                    Guid = g,
                    IsAdded = r.IsAdded,
                    AddedBundles = bundles,
                    HasCustomHandler = false,
                    HandlerHash = 0,
                    UserData = r.UserData ?? ""
                });
                return;
            }

            bool success = false;
            Guid guid = guidFromResource;
            byte[] projectEbxData = null;

            if (processed != null && processed.Length > 0)
            {
                try
                {
                    using (var ms = new MemoryStream(processed))
                    using (var reader = EbxReader.CreateReader(ms))
                    {
                        if (reader != null && reader.IsValid)
                        {
                            var asset = reader.ReadAsset<EbxAsset>();
                            if (asset != null && asset.IsValid)
                            {
                                if (guid == Guid.Empty)
                                    guid = asset.FileGuid;

                                using (var outMs = new MemoryStream())
                                using (var writer = EbxBaseWriter.CreateProjectWriter(outMs, EbxWriteFlags.IncludeTransient))
                                {
                                    writer.WriteAsset(asset);
                                    projectEbxData = outMs.ToArray();
                                    success = true;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    success = false;
                }
            }

            if (!success)
            {
                _failedEbxCount++;

                if (bundles.Count == 0)
                {
                    string n = r.Name ?? string.Empty;
                    if (n.IndexOf("WSLocalization", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("Localization/", StringComparison.OrdinalIgnoreCase) < 0)
                        return;
                }

                if (guid == Guid.Empty)
                    guid = TryReadEbxFileGuidFromBytes(processed ?? data);
                if (guid == Guid.Empty && r.IsAdded)
                    guid = DeterministicGuidFromName(r.Name);

                ebxResources.Add(new EbxResourceData
                {
                    Name = r.Name,

                    Data = processed ?? data,
                    Guid = guid,
                    IsAdded = r.IsAdded,
                    AddedBundles = bundles,

                    HasCustomHandler = isFsLo,
                    HandlerHash = 0,
                    UserData = r.UserData ?? ""
                });
                return;
            }

            if (guid == Guid.Empty)
                guid = TryReadEbxFileGuidFromBytes(projectEbxData ?? processed ?? data);
            if (guid == Guid.Empty && r.IsAdded)
                guid = DeterministicGuidFromName(r.Name);

            ebxResources.Add(new EbxResourceData
            {
                Name = r.Name,
                Data = projectEbxData,
                Guid = guid,
                IsAdded = r.IsAdded,
                AddedBundles = bundles,
                HasCustomHandler = false,
                HandlerHash = 0,
                UserData = r.UserData ?? ""
            });
        }

        private static void ProcessRes(AssetManager am, BaseModResource r, byte[] data, List<int> bundles, bool fastInit)
        {
            var entry = new ResAssetEntry();
            r.FillAssetEntry(entry);

            // IMPORTANT: Trust the fbmod's IsAdded flag.
            // Many SWBF2 skin mods are base replacements (IsAdded=false). Inferring "added" based on base lookups
            // is brittle and can cause FrostyProject.Load to crash (duplicate AddRes by ResRid).
            bool isAdded = r.IsAdded;


            byte[] sha1 = null;
            try
            {
                var pSha1 = entry.GetType().GetProperty("Sha1") ?? entry.GetType().GetProperty("SHA1") ?? entry.GetType().GetProperty("Hash");
                if (pSha1 != null)
                {
                    var v = pSha1.GetValue(entry);
                    if (v is byte[] b && b.Length == 20)
                        sha1 = b;
                }
            }
            catch { }

            if (data == null || data.Length == 0)
            {
                try { Console.WriteLine($"[res] missing mod RES bytes: rid=0x{entry.ResRid:X16} name={r.Name}"); } catch { }
                if (!fastInit && TryLoadBaseResData(am, entry, out var baseBytes, out var baseSha1))
                {
                    if (baseBytes != null && baseBytes.Length > 0)
                        data = baseBytes;
                    if (sha1 == null && baseSha1 != null && baseSha1.Length == 20)
                        sha1 = baseSha1;
                }
            }

            // Use the fbmod resource's stored uncompressed size. In FrostySdk, AssetEntry.OriginalSize is a field,
            // not a property, so reflection here would silently fail and fall back to data.Length (compressed size).
            long originalSize = r.Size;
            if (originalSize <= 0 && data != null)
                originalSize = data.LongLength;

            resResources.Add(new ResResourceData
            {
                Name = r.Name,
                Data = data,
                ResRid = entry.ResRid,
                ResType = entry.ResType,
                ResMeta = entry.ResMeta ?? new byte[0x10],
                Sha1 = sha1,
                OriginalSize = originalSize,
                HasCustomHandler = r.HasHandler,
                HandlerHash = r.Handler,
                IsAdded = isAdded,
                AddedBundles = bundles
            });
        }

                private static bool TryLoadBaseResData(AssetManager am, ResAssetEntry entry, out byte[] data, out byte[] sha1)
        {
            data = null;
            sha1 = null;
            if (am == null || entry == null)
                return false;

            DumpAssetManagerResApis(am);

            try
            {
                Console.WriteLine($"[res] try base RES rid=0x{entry.ResRid:X16} name={entry.Name}");

                object trackedEntry = null;
                try
                {
                    var tAm = am.GetType();
                    var mGetEntry =
                        tAm.GetMethod("GetResEntry", new[] { typeof(ulong) }) ??
                        tAm.GetMethod("GetResEntry", new[] { typeof(long) });

                    if (mGetEntry != null)
                    {
                        trackedEntry = (mGetEntry.GetParameters()[0].ParameterType == typeof(long))
                            ? mGetEntry.Invoke(am, new object[] { unchecked((long)entry.ResRid) })
                            : mGetEntry.Invoke(am, new object[] { entry.ResRid });
                    }
                }
                catch { }

                if (trackedEntry != null)
                {
                    try
                    {
                        var t = trackedEntry.GetType();
                        var pSha1 = t.GetProperty("Sha1") ?? t.GetProperty("SHA1") ?? t.GetProperty("Hash");
                        if (pSha1 != null)
                        {
                            var v = pSha1.GetValue(trackedEntry);
                            if (v is byte[] b && b.Length == 20)
                                sha1 = b;
                        }
                    }
                    catch { }
                }

                object resObj = null;

                {
                    var argObj = trackedEntry ?? (object)entry;
                    var argType = argObj.GetType();
                    var tAm = am.GetType();

                    var mi = tAm.GetMethod("GetRes", new[] { argType });
                    if (mi != null)
                    {
                        resObj = mi.Invoke(am, new[] { argObj });
                    }
                    else
                    {

                        foreach (var mth in tAm.GetMethods())
                        {
                            if (!string.Equals(mth.Name, "GetRes", StringComparison.OrdinalIgnoreCase))
                                continue;
                            var ps = mth.GetParameters();
                            if (ps.Length != 1) continue;

                            if (!ps[0].ParameterType.IsAssignableFrom(argType) && !argType.IsAssignableFrom(ps[0].ParameterType))
                                continue;

                            resObj = mth.Invoke(am, new[] { argObj });
                            if (resObj != null) break;
                        }
                    }
                }

                if (resObj == null)
                {
                    ulong rid = entry.ResRid;
                    long ridL = unchecked((long)rid);
                    foreach (var mth in am.GetType().GetMethods())
                    {
                        var name = mth.Name;
                        if (name.IndexOf("Res", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("Resource", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        var ps = mth.GetParameters();
                        if (ps.Length != 1) continue;

                        object arg = null;
                        if (ps[0].ParameterType == typeof(ulong)) arg = rid;
                        else if (ps[0].ParameterType == typeof(long)) arg = ridL;
                        else continue;

                        if (mth.ReturnType == typeof(void)) continue;

                        try
                        {
                            var candidate = mth.Invoke(am, new[] { arg });
                            Console.WriteLine($"[res] invoked {mth.Name}({ps[0].ParameterType.Name}) => {(candidate == null ? "null" : candidate.GetType().FullName)}");
                            if (candidate == null) continue;

                            resObj = candidate;
                            break;
                        }
                        catch { }
                    }
                }

                if (resObj == null)
                {
                    Console.WriteLine($"[res] base lookup failed for rid=0x{entry.ResRid:X16} (no candidate returned)");
                    return false;
                }

                if (resObj is byte[] bytes && bytes.Length > 0)
                {
                    data = bytes;
                    return true;
                }

                if (resObj is System.IO.Stream st)
                {
                    try
                    {
                        if (st.CanSeek)
                            st.Position = 0;
                    }
                    catch { }

                    using (st)
                    using (var ms = new System.IO.MemoryStream())
                    {
                        try
                        {
                            st.CopyTo(ms);
                        }
                        catch
                        {

                            var buf = new byte[81920];
                            int read;
                            while ((read = st.Read(buf, 0, buf.Length)) > 0)
                                ms.Write(buf, 0, read);
                        }
                        data = ms.ToArray();
                        Console.WriteLine($"[res] GetRes stream => {data?.Length ?? 0} bytes for rid=0x{entry.ResRid:X16}");
                        return data != null && data.Length > 0;
                    }
                }

                var pData = resObj.GetType().GetProperty("Data");
                if (pData != null && typeof(byte[]).IsAssignableFrom(pData.PropertyType))
                {
                    data = (byte[])pData.GetValue(resObj);
                    return data != null && data.Length > 0;
                }

                var mGetData = resObj.GetType().GetMethod("GetData", Type.EmptyTypes);
                if (mGetData != null && typeof(byte[]).IsAssignableFrom(mGetData.ReturnType))
                {
                    data = (byte[])mGetData.Invoke(resObj, null);
                    return data != null && data.Length > 0;
                }

                var mGetStream = resObj.GetType().GetMethod("GetStream", Type.EmptyTypes);
                if (mGetStream != null)
                {
                    var s = mGetStream.Invoke(resObj, null) as System.IO.Stream;
                    if (s != null)
                    {
                        using (s)
                        using (var ms = new System.IO.MemoryStream())
                        {
                            s.CopyTo(ms);
                            data = ms.ToArray();
                            return data != null && data.Length > 0;
                        }
                    }
                }
            }
            catch
            {

            }

            return false;
        }

private static void DumpAssetManagerResApis(AssetManager am)
{

    if (_didDumpResApis || am == null) return;
    _didDumpResApis = true;

    try
    {
        Console.WriteLine("[res] AssetManager type: " + am.GetType().FullName);
        foreach (var m in am.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            var name = m.Name;
            if (name.IndexOf("Res", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("Resource", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var ps = m.GetParameters();
            var sig = string.Join(", ", Array.ConvertAll(ps, p => p.ParameterType.Name + " " + p.Name));
            Console.WriteLine($"[res] {m.ReturnType.Name} {name}({sig})");
        }
    }
    catch { }
}

        private static void ProcessChunk(AssetManager am, BaseModResource r, byte[] data, List<int> bundles, bool fastInit)
        {
            var entry = new ChunkAssetEntry();
            r.FillAssetEntry(entry);

            // IMPORTANT: Trust the fbmod's IsAdded flag (see ProcessRes comment).
            bool isAdded = r.IsAdded;


            // Preserve the fbmod flag (bit 0x02). In Frosty mod format, this bit is used to control whether a modified
            // chunk should be added to the special chunks bundle during apply. ChunkAssetEntry.AddToChunkBundle does not exist
            // (it's stored on ModifiedEntry in the editor), so reflection would always fail and default to false.
            bool addToChunkBundle = r.IsTocChunk;

            // Heuristic: texture chunks should always be added to the chunks bundle.
            if (!addToChunkBundle && LooksLikeTextureChunk(data))
                addToChunkBundle = true;

            chunkResources.Add(new ChunkResourceData
            {
                Id = entry.Id,
                Data = data,
                H32 = entry.H32,
                RangeStart = entry.RangeStart,
                RangeEnd = entry.RangeEnd,
                LogicalOffset = entry.LogicalOffset,
                LogicalSize = entry.LogicalSize,
                FirstMip = entry.FirstMip,
                IsAdded = isAdded,
                AddToChunkBundle = addToChunkBundle,
                UserData = r.UserData ?? "",
                AddedBundles = bundles
            });
        }

        private static void ProcessBundle(BaseModResource r)
        {
            var entry = new BundleEntry();
            r.FillAssetEntry(entry);

            string sbName = _defaultSuperBundleName ?? "win32/superbundles/base";
            if (superBundleIdToName.TryGetValue(entry.SuperBundleId, out var found) && !string.IsNullOrWhiteSpace(found))
                sbName = found;

            int type = 0;
            try
            {
                var p = entry.GetType().GetProperty("Type");
                if (p != null)
                    type = Convert.ToInt32(p.GetValue(entry));
            }
            catch { }

            if (type == 0 && string.Equals(ProfilesLibrary.ProfileName, "starwarsbattlefrontii", StringComparison.OrdinalIgnoreCase))
                type = 1;

            bundleResources.Add(new BundleResourceData
            {
                Name = entry.Name,
                SuperBundleName = sbName,
                Type = type,
                IsAdded = true
            });

            try
            {
                int h = Fnv1.HashString(entry.Name);
                if (!bundleHashToName.ContainsKey(h))
                    bundleHashToName[h] = entry.Name;
            }
            catch { }
        }

        private static void ProcessEmbedded(BaseModResource r, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            if (r.Name.Equals("Icon", StringComparison.OrdinalIgnoreCase))
                projectIcon = data;
            else if (r.Name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase))
                projectScreenshots.Add(data);
        }

        private static void DumpEbx(string folder, bool ebxOnly)
        {
            Directory.CreateDirectory(folder);

            int i = 0;
            foreach (var ebx in ebxResources)
            {

                if (ebx.Data == null || ebx.Data.Length == 0)
                    continue;

                string safeName = MakeSafePath(ebx.Name);
                string outPath = Path.Combine(folder, safeName + ".ebx");
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllBytes(outPath, ebx.Data);
                i++;
            }

            Console.WriteLine("EBX dumped: " + i);
        }

        private static string MakeSafePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "unknown";

            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Replace(':', '_');

            name = name.Replace('\\', '/');
            while (name.Contains("//")) name = name.Replace("//", "/");
            return name.TrimStart('/');
        }

        internal static byte[] ComputeSha1(byte[] data)
        {
            if (data == null) return new byte[20];
            using (var sha1 = SHA1.Create())
                return sha1.ComputeHash(data);
        }


        private static void FileSystemInitWithProgress(FileSystem fs)
        {
            if (fs == null) throw new ArgumentNullException(nameof(fs));

            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            // Watchdog so the UI/logs do not appear frozen during heavy disk I/O
            var cts = new System.Threading.CancellationTokenSource();
            var token = cts.Token;
            var watchdog = System.Threading.Tasks.Task.Run(async () =>
            {
                int seconds = 0;
                while (!token.IsCancellationRequested)
                {
                    await System.Threading.Tasks.Task.Delay(2000, token).ConfigureAwait(false);
                    seconds += 2;
                    try { Console.WriteLine($"Initializing FileSystem... ({seconds}s)"); } catch { }
                }
            }, token);

            try
            {
                // Call the internal stages separately so we can see where it hangs.
                InvokeNonPublic(fs, "ProcessLayouts");
                Console.WriteLine("FileSystem: layouts processed");

                // LoadInitfs(byte[] key, bool patched = true)
                var mi = fs.GetType().GetMethod("LoadInitfs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (mi != null)
                {
                    mi.Invoke(fs, new object[] { null, true });
                    Console.WriteLine("FileSystem: initfs loaded");
                }
                else
                {
                    // Fallback to public Initialize() if method name changes
                    fs.Initialize();
                    Console.WriteLine("FileSystem: initialized");
                }

                swTotal.Stop();
                Console.WriteLine($"FileSystem.Initialize completed in {swTotal.Elapsed.TotalSeconds:F1}s");
            }
            finally
            {
                cts.Cancel();
                try { watchdog.Wait(250); } catch { }
                cts.Dispose();
            }
        }

        private static void InvokeNonPublic(object instance, string methodName)
        {
            var t = instance.GetType();
            var mi = t.GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi == null)
                throw new MissingMethodException(t.FullName, methodName);
            mi.Invoke(instance, null);
        }

        private static string FindGameExecutable(string gamePath)
        {
            if (string.IsNullOrWhiteSpace(gamePath))
                throw new ArgumentException("Game path is empty.");

            gamePath = gamePath.Trim().Trim('"');

            // Allow passing the exe directly.
            if (File.Exists(gamePath) && gamePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return gamePath;

            string dir = gamePath;

            // If the user selected Data/Patch/Win32, climb up a few levels.
            for (int i = 0; i < 4; i++)
            {
                string leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (leaf.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("Patch", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("Win32", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Directory.GetParent(dir);
                    if (parent == null) break;
                    dir = parent.FullName;
                    continue;
                }
                break;
            }

            string[] candidates =
            {
                "starwarsbattlefrontii.exe",
                "StarWarsBattlefrontII.exe",
                "starwarsbattlefrontii_trial.exe",
                "SWBFII.exe"
            };

            foreach (var c in candidates)
            {
                string p = Path.Combine(dir, c);
                if (File.Exists(p))
                    return p;
            }

            // Last resort: top-level only (no recursive scan).
            try
            {
                var exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(p => !p.EndsWith("CrashReporter.exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var bf = exes.FirstOrDefault(p =>
                    Path.GetFileName(p).IndexOf("battlefront", StringComparison.OrdinalIgnoreCase) >= 0);

                if (!string.IsNullOrWhiteSpace(bf))
                    return bf;

                if (exes.Count > 0)
                    return exes[0];
            }
            catch { }

            throw new FileNotFoundException("Could not find a game .exe inside: " + dir);
        }
	    }
    }
