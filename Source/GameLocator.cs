using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FbmodDecompiler
{
    internal static class GameLocator
    {
        public static string TryFindSwbf2InstallDir()
        {
            foreach (var p in GetCommonCandidates())
            {
                if (IsValidGameDir(p))
                    return p;
            }

            var steam = TryGetSteamInstallPath();
            if (!string.IsNullOrWhiteSpace(steam))
            {
                foreach (var p in GetSteamLibraryCandidates(steam))
                {
                    if (IsValidGameDir(p))
                        return p;
                }
            }

            foreach (var p in GetRegistryCandidates())
            {
                if (IsValidGameDir(p))
                    return p;
            }

            return null;
        }

        private static bool IsValidGameDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return false;

            var exe1 = Path.Combine(dir, "starwarsbattlefrontii.exe");
            var exe2 = Path.Combine(dir, "StarWarsBattlefrontII.exe");
            return File.Exists(exe1) || File.Exists(exe2);
        }

        private static IEnumerable<string> GetCommonCandidates()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            yield return Path.Combine(pfx86, "Origin Games", "STAR WARS Battlefront II");
            yield return Path.Combine(pf, "EA Games", "STAR WARS Battlefront II");

            yield return Path.Combine(pfx86, "Steam", "steamapps", "common", "STAR WARS Battlefront II");
            yield return Path.Combine(pfx86, "Steam", "steamapps", "common", "STAR WARS Battlefront II Celebration Edition");

            yield return Path.Combine(pf, "Epic Games", "STAR WARS Battlefront II");
        }

        private static string TryGetSteamInstallPath()
        {
            try
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
                    var install = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(install) && Directory.Exists(install))
                        return install;
                }
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> GetSteamLibraryCandidates(string steamInstall)
        {
            var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                steamInstall
            };

            try
            {
                var vdf = Path.Combine(steamInstall, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    var lines = File.ReadAllLines(vdf);
                    foreach (var ln in lines)
                    {
                        if (ln.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var parts = ln.Split('"').Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                            var idx = Array.FindIndex(parts, p => string.Equals(p, "path", StringComparison.OrdinalIgnoreCase));
                            if (idx >= 0 && idx + 1 < parts.Length)
                            {
                                var path = parts[idx + 1].Replace(@"\\", @"\").Trim();
                                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                                    libs.Add(path);
                            }
                        }
                    }
                }
            }
            catch { }

            foreach (var lib in libs)
            {
                yield return Path.Combine(lib, "steamapps", "common", "STAR WARS Battlefront II");
                yield return Path.Combine(lib, "steamapps", "common", "STAR WARS Battlefront II Celebration Edition");
            }
        }

        private static IEnumerable<string> GetRegistryCandidates()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);

                    foreach (var k in new[]
                    {
                        @"SOFTWARE\EA Games\STAR WARS Battlefront II",
                        @"SOFTWARE\WOW6432Node\EA Games\STAR WARS Battlefront II",
                        @"SOFTWARE\Origin Games\STAR WARS Battlefront II",
                        @"SOFTWARE\WOW6432Node\Origin Games\STAR WARS Battlefront II",
                    })
                    {
                        string p = TryReadInstallPath(baseKey, k);
                        if (!string.IsNullOrWhiteSpace(p)) yield return p;
                    }

                    foreach (var k in new[]
                    {
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                    })
                    {
                        foreach (var p in EnumerateUninstallInstallLocations(baseKey, k))
                            yield return p;
                    }
                }
            }
        }

        private static string TryReadInstallPath(RegistryKey baseKey, string subKeyPath)
        {
            try
            {
                using var k = baseKey.OpenSubKey(subKeyPath);
                var v = (k?.GetValue("Install Dir") as string)
                        ?? (k?.GetValue("InstallDir") as string)
                        ?? (k?.GetValue("InstallLocation") as string)
                        ?? (k?.GetValue("Path") as string);
                if (!string.IsNullOrWhiteSpace(v) && Directory.Exists(v))
                    return v;
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> EnumerateUninstallInstallLocations(RegistryKey baseKey, string uninstallKeyPath)
        {
            var results = new List<string>();
            try
            {
                using var k = baseKey.OpenSubKey(uninstallKeyPath);
                if (k == null)
                    return results;

                foreach (var name in k.GetSubKeyNames())
                {
                    using var app = k.OpenSubKey(name);
                    var display = app?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(display)) continue;

                    if (display.IndexOf("Battlefront", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (display.IndexOf("Star Wars", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var loc = app.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                        results.Add(loc);
                }
            }
            catch { }

            return results;
        }
    }
}
