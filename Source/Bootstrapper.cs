using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace FbmodDecompiler
{
    internal static class Bootstrapper
    {
        private const string AppFolderName = "FbmodDecompiler";

        private static bool _resolverRegistered;

        public static void EnsureRuntimeFiles()
        {
            // Prefer extracting next to the EXE (helps libraries that probe AppDomain.BaseDirectory).
            // Fall back to %LOCALAPPDATA% if the folder isn't writable.
            string baseDir = null;
            string exeDir = null;
            try { exeDir = AppDomain.CurrentDomain.BaseDirectory; } catch { }

            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                try
                {
                    string candidate = Path.Combine(exeDir, "_runtime");
                    Directory.CreateDirectory(candidate);
                    string test = Path.Combine(candidate, ".write_test");
                    File.WriteAllText(test, "ok");
                    File.Delete(test);
                    baseDir = candidate;
                }
                catch
                {
                    baseDir = null;
                }
            }

            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppFolderName,
                    "runtime"
                );
            }

            string profilesDir = Path.Combine(baseDir, "Profiles");
            Directory.CreateDirectory(profilesDir);

            string profilePath = Path.Combine(profilesDir, "StarWarsIISDK.dll");
            string zstdPath = Path.Combine(baseDir, "libzstd.dll");
            string zstdAltPath = Path.Combine(baseDir, "zstd.dll");
            string thirdPartyDir = Path.Combine(baseDir, "ThirdParty");
            Directory.CreateDirectory(thirdPartyDir);

            ExtractIfMissing("Profiles.StarWarsIISDK.dll", profilePath);
            // Frosty typically probes zstd from both the root and a ThirdParty directory.
            ExtractIfMissing("libzstd.dll", zstdPath);
            ExtractIfMissing("libzstd.dll", Path.Combine(thirdPartyDir, "libzstd.dll"));
            ExtractIfMissing("libzstd.0.0.6.dll", Path.Combine(baseDir, "libzstd.0.0.6.dll"));
            ExtractIfMissing("libzstd.1.1.5.dll", Path.Combine(baseDir, "libzstd.1.1.5.dll"));
            ExtractIfMissing("libzstd.1.2.0.dll", Path.Combine(baseDir, "libzstd.1.2.0.dll"));
            ExtractIfMissing("libzstd.1.3.4.dll", Path.Combine(baseDir, "libzstd.1.3.4.dll"));
            ExtractIfMissing("libzstd.1.5.5.dll", Path.Combine(baseDir, "libzstd.1.5.5.dll"));
            ExtractIfMissing("libzstd.0.0.6.dll", Path.Combine(thirdPartyDir, "libzstd.0.0.6.dll"));
            ExtractIfMissing("libzstd.1.1.5.dll", Path.Combine(thirdPartyDir, "libzstd.1.1.5.dll"));
            ExtractIfMissing("libzstd.1.2.0.dll", Path.Combine(thirdPartyDir, "libzstd.1.2.0.dll"));
            ExtractIfMissing("libzstd.1.3.4.dll", Path.Combine(thirdPartyDir, "libzstd.1.3.4.dll"));
            ExtractIfMissing("libzstd.1.5.5.dll", Path.Combine(thirdPartyDir, "libzstd.1.5.5.dll"));
            // Some setups also ship a zstd.dll alias.
            ExtractIfMissing("zstd.dll", zstdAltPath);

            ExtractIfMissing("FrostyHash.dll", Path.Combine(baseDir, "FrostyHash.dll"));
            ExtractIfMissing("FrostySdk.dll", Path.Combine(baseDir, "FrostySdk.dll"));
            ExtractIfMissing("FrostyCore.dll", Path.Combine(baseDir, "FrostyCore.dll"));
            ExtractIfMissing("FrostyControls.dll", Path.Combine(baseDir, "FrostyControls.dll"));
            ExtractIfMissing("FrostyModSupport.dll", Path.Combine(baseDir, "FrostyModSupport.dll"));
            ExtractIfMissing("Newtonsoft.Json.dll", Path.Combine(baseDir, "Newtonsoft.Json.dll"));
            ExtractIfMissing("ZstdSharp.dll", Path.Combine(baseDir, "ZstdSharp.dll"));
            ExtractIfMissing("System.Buffers.dll", Path.Combine(baseDir, "System.Buffers.dll"));
            ExtractIfMissing("System.Memory.dll", Path.Combine(baseDir, "System.Memory.dll"));
            ExtractIfMissing("System.Numerics.Vectors.dll", Path.Combine(baseDir, "System.Numerics.Vectors.dll"));
            ExtractIfMissing("System.Runtime.CompilerServices.Unsafe.dll", Path.Combine(baseDir, "System.Runtime.CompilerServices.Unsafe.dll"));
            ExtractIfMissing("System.Threading.Tasks.Extensions.dll", Path.Combine(baseDir, "System.Threading.Tasks.Extensions.dll"));

            RegisterDiskAssemblyResolver(baseDir);

            try { Environment.CurrentDirectory = baseDir; } catch { }

            try
            {
                var oldPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!oldPath.Split(';').Any(p => string.Equals(p?.Trim(), baseDir, StringComparison.OrdinalIgnoreCase)))
                    Environment.SetEnvironmentVariable("PATH", baseDir + ";" + oldPath);
            }
            catch { }

            try { SetDllDirectory(baseDir); } catch { }

            // Pre-load zstd from the most common names so GetResourceData can decompress.
            TryLoadNative(zstdPath);
            TryLoadNative(Path.Combine(thirdPartyDir, "libzstd.dll"));
            TryLoadNative(Path.Combine(baseDir, "libzstd.1.5.5.dll"));
            TryLoadNative(Path.Combine(thirdPartyDir, "libzstd.1.5.5.dll"));
            TryLoadNative(zstdAltPath);

            // Some components probe Profiles under the application base directory.
            // Mirror the profile there best-effort so both probing styles succeed.
            try
            {
                if (!string.IsNullOrWhiteSpace(exeDir))
                {
                    string exeProfiles = Path.Combine(exeDir, "Profiles");
                    Directory.CreateDirectory(exeProfiles);
                    string exeProfilePath = Path.Combine(exeProfiles, "StarWarsIISDK.dll");
                    if (!File.Exists(exeProfilePath))
                        File.Copy(profilePath, exeProfilePath, overwrite: false);
                }
            }
            catch { }
        }

        private static void TryLoadNative(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return;

            IntPtr h = LoadLibrary(fullPath);
            if (h == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void RegisterDiskAssemblyResolver(string runtimeDir)
        {
            if (_resolverRegistered)
                return;

            _resolverRegistered = true;
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var requested = new AssemblyName(args.Name);

                    string candidate = Path.Combine(runtimeDir, requested.Name + ".dll");
                    if (!File.Exists(candidate))
                        return null;

                    return Assembly.LoadFrom(candidate);
                }
                catch
                {
                    return null;
                }
            };
        }

        private static void ExtractIfMissing(string resourceSuffix, string outPath)
        {
            if (File.Exists(outPath))
                return;

            var asm = Assembly.GetExecutingAssembly();
            string resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (resName == null)
                throw new InvalidOperationException("Missing embedded resource: " + resourceSuffix);

            using Stream s = asm.GetManifestResourceStream(resName);
            if (s == null)
                throw new InvalidOperationException("Failed to open embedded resource stream: " + resName);

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));

            using FileStream fs = File.Create(outPath);
            s.CopyTo(fs);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
