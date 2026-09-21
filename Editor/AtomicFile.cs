using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WasimDevelopment.UnityMcpBridge
{
    // Every IPC reader must permit deletion so an atomic rename can replace its open file.
    internal static class AtomicFile
    {
        public static string ReadText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true)) return reader.ReadToEnd();
        }

        public static JObject ReadObject(string path)
        {
            using (var reader = new JsonTextReader(new StringReader(ReadText(path))))
            {
                // Keep ISO timestamps as strings; do not round-trip through a locale-formatted DateTime.
                reader.DateParseHandling = DateParseHandling.None;
                return JObject.Load(reader);
            }
        }

        public static bool TryUtc(string text, out DateTime value) => DateTime.TryParse(text,
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

        public static void WriteText(string path, string content) => WriteBytes(path, new UTF8Encoding(false).GetBytes(content ?? ""));

        public static void WriteBytes(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, bytes);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporary, path, null);
                        else File.Move(temporary, path);
                        return;
                    }
                    catch (IOException) when (attempt < 3) { Thread.Sleep(5 * (attempt + 1)); }
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
