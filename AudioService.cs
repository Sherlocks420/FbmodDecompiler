using System;
using System.IO;
using System.Windows.Media;

namespace FbmodDecompiler
{
    internal sealed class AudioService
    {
        private readonly MediaPlayer _player = new MediaPlayer();
        private bool _initialized;
        private double _volume = 0.35;

        public bool Muted { get; private set; }

        public event Action<bool> MutedChanged;

        public void InitAndPlay()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var mp3Path = EnsureBgmOnDisk();
                if (string.IsNullOrEmpty(mp3Path) || !File.Exists(mp3Path))
                    return;

                _player.Open(new Uri(mp3Path, UriKind.Absolute));
                _player.Volume = Muted ? 0 : _volume;
                _player.MediaEnded += (_, __) =>
                {
                    try
                    {
                        _player.Position = TimeSpan.Zero;
                        _player.Play();
                    }
                    catch { }
                };
                _player.Play();
            }
            catch
            {

            }
        }

        public void ToggleMute()
        {
            SetMuted(!Muted);
        }

        public void SetMuted(bool muted)
        {
            Muted = muted;
            try { _player.Volume = Muted ? 0 : _volume; } catch { }
            try { MutedChanged?.Invoke(Muted); } catch { }
        }

        public string GetMuteIcon() => Muted ? "🔇" : "🔊";

        private static string EnsureBgmOnDisk()
        {
            try
            {

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var direct = Path.Combine(baseDir, "Sleepy Hallow - 2 Fake.mp3");
                if (File.Exists(direct)) return direct;
                var alt = Path.Combine(baseDir, "Assets", "Sleepy Hallow - 2 Fake.mp3");
                if (File.Exists(alt)) return alt;

                var tempDir = Path.Combine(Path.GetTempPath(), "FbmodDecompiler");
                Directory.CreateDirectory(tempDir);
                var outPath = Path.Combine(tempDir, "Sleepy Hallow - 2 Fake.mp3");
                if (File.Exists(outPath)) return outPath;

                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("FbmodDecompiler.Assets.BGM.mp3"))
                {
                    if (s == null) return "";
                    using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                        s.CopyTo(fs);
                }
                return outPath;
            }
            catch
            {
                return "";
            }
        }
    }
}
