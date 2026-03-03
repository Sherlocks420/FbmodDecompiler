using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FbmodDecompiler
{
    internal static class AuthorProtection
    {
        private static readonly HashSet<string> BlockedAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sherlocks",
            "clayton",
            "sherlocks420",
            "ryvel",
            "ryvell",
            "redlanes777",
            "stalin_97"
        };

        internal static bool IsBlocked(string author)
        {
            if (string.IsNullOrWhiteSpace(author))
                return false;

            string a = author.Trim();
            if (BlockedAuthors.Contains(a))
                return true;

            string[] tokens = a.Split(new[] { ',', ';', '|', '/', '\\', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string t in tokens)
            {
                string tt = t.Trim();
                if (tt.Length == 0) continue;
                if (BlockedAuthors.Contains(tt))
                    return true;
            }

            foreach (string b in BlockedAuthors)
            {
                if (a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        internal static bool ScanFileForBlockedAuthor(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                const int MaxBytes = 64 * 1024 * 1024;
                byte[] data;

                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length <= 0) return false;
                    int len = (int)Math.Min(fs.Length, MaxBytes);
                    data = new byte[len];

                    int read = 0;
                    while (read < len)
                    {
                        int r = fs.Read(data, read, len - read);
                        if (r <= 0) break;
                        read += r;
                    }

                    if (read != len)
                        Array.Resize(ref data, read);
                }

                string utf8 = Encoding.UTF8.GetString(data);
                if (ScanString(utf8))
                    return true;

                string utf16 = Encoding.Unicode.GetString(data);
                return ScanString(utf16);
            }
            catch
            {
                return false;
            }
        }

        private static bool ScanString(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            foreach (string b in BlockedAuthors)
            {
                if (text.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }
    }
}
