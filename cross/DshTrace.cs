using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapriDesktop
{
    internal sealed class DshTrace : IDisposable
    {
        private StreamWriter writer;
        private readonly string turn = Guid.NewGuid().ToString("N");
        internal DshTrace(string home, bool enabled)
        {
            if (!enabled) return;
            try
            {
                Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string path = Path.Combine(home, "activity.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                FileStream stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { writer?.Dispose(); writer = null; }
        }

        internal void Write(string kind, object detail)
        {
            if (writer == null) return;
            try { writer.WriteLine(JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, turn, kind, detail = Clean(JsonSerializer.SerializeToElement(detail)) })); }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is UnauthorizedAccessException) { }
        }

        internal static JsonElement Clean(JsonElement value)
        {
            using MemoryStream stream = new MemoryStream();
            using (Utf8JsonWriter json = new Utf8JsonWriter(stream)) Scrub(json, value, 0);
            using JsonDocument result = JsonDocument.Parse(stream.ToArray());
            return result.RootElement.Clone();
        }

        private static void Scrub(Utf8JsonWriter output, JsonElement value, int depth)
        {
            if (depth > 12) { output.WriteStringValue("[depth limit]"); return; }
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String && (type.GetString() == "image" || type.GetString() == "reasoning")) { output.WriteStringValue("[" + type.GetString() + " omitted]"); return; }
                output.WriteStartObject();
                int count = 0;
                foreach (JsonProperty field in value.EnumerateObject())
                {
                    if (++count > 80) break;
                    output.WritePropertyName(field.Name);
                    if (Regex.IsMatch(field.Name, @"^(?:api[_-]?key|authorization|password|secret|token|access[_-]?token|refresh[_-]?token|cookie|set-cookie)$", RegexOptions.IgnoreCase)) output.WriteStringValue("[redacted]");
                    else if (field.Name == "base64" || field.Name == "data" || field.Name == "bytes") output.WriteStringValue("[payload omitted]");
                    else Scrub(output, field.Value, depth + 1);
                }
                output.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                output.WriteStartArray(); int count = 0;
                foreach (JsonElement item in value.EnumerateArray()) { if (++count > 40) { output.WriteStringValue("[remaining items omitted]"); break; } Scrub(output, item, depth + 1); }
                output.WriteEndArray();
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString();
                if (text.Length < 65536 && (text.TrimStart().StartsWith("{") || text.TrimStart().StartsWith("[")))
                {
                    try { using JsonDocument parsed = JsonDocument.Parse(text); Scrub(output, parsed.RootElement, depth + 1); return; } catch (JsonException) { }
                }
                text = Brain.Redacted(text);
                output.WriteStringValue(text.Length > 6000 ? text.Substring(0, 6000) + " [truncated]" : text);
            }
            else value.WriteTo(output);
        }

        public void Dispose() { writer?.Dispose(); writer = null; }
    }
}
