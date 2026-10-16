using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Write identical complete JSON evidence without a multi-megabyte UTF-16
    // intermediate. Validation and file-write timing remain at the call site.
    internal static class CanonicalJsonEvidence
    {
        internal static void Write(string path, object value)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var text = new StreamWriter(stream, new UTF8Encoding(false, true), 16384);
            using var json = new JsonTextWriter(text);
            JsonSerializer.CreateDefault().Serialize(json, value);
        }
    }
}
