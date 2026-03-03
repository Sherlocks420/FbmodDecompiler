using System;
using System.IO;
using FrostySdk;
using FrostySdk.IO;

namespace FbmodDecompiler
{

    internal sealed class FrostyModReaderLite : NativeReader
    {

        private const ulong FrostyModMagic = 0x01005954534F5246;
        private const uint MaxSupportedVersion = 5;

        public bool IsValid { get; private set; }
        public uint Version { get; private set; }
        public int GameVersion { get; private set; }

        public FrostyModReaderLite(Stream inStream) : base(inStream)
        {
            try
            {
                ulong magic = ReadULong();
                if (magic != FrostyModMagic)
                    return;

                Version = ReadUInt();
                if (Version > MaxSupportedVersion)
                    return;

                ReadLong();
                ReadInt();

                string profileName = ReadSizedString(ReadByte());
                if (!profileName.Equals(ProfilesLibrary.ProfileName, StringComparison.OrdinalIgnoreCase))
                    return;

                GameVersion = ReadInt();
                IsValid = true;
            }
            catch
            {
                IsValid = false;
            }
        }

        public bool TryReadAuthor(out string author)
        {
            author = "";
            if (!IsValid) return false;

            try
            {

                _ = ReadNullTerminatedString();
                author = ReadNullTerminatedString();
                return true;
            }
            catch
            {
                author = "";
                return false;
            }
        }

        public static bool TryReadAuthor(string fbmodPath, out string author)
        {
            author = "";
            if (string.IsNullOrWhiteSpace(fbmodPath) || !File.Exists(fbmodPath))
                return false;

            try
            {
                using (var fs = new FileStream(fbmodPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var r = new FrostyModReaderLite(fs))
                {
                    if (!r.IsValid)
                        return false;
                    return r.TryReadAuthor(out author);
                }
            }
            catch
            {
                author = "";
                return false;
            }
        }
    }
}
