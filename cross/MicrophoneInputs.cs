using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CapriDesktop
{
    internal sealed class MicrophoneInput
    {
        internal readonly string Id, Name;
        internal readonly bool Default;
        internal MicrophoneInput(string id, string name, bool isDefault = false) { Id = id; Name = name; Default = isDefault; }
    }

    internal static class MicrophoneInputs
    {
        internal static Func<ProcessStartInfo, Process> Spawn = Process.Start;
        internal static MicrophoneInput[] Parse(string text)
        {
            if (text == null || text.Length > 65536) throw new IOException("The microphone list was too large.");
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("Couldn't read the microphone list.");
            var inputs = new List<MicrophoneInput>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                string uid = id.GetString(), label = name.GetString();
                if (!Valid(uid) || string.IsNullOrWhiteSpace(label) || label.Length > 256 || label.Any(char.IsControl) || !seen.Add(uid)) continue;
                inputs.Add(new MicrophoneInput(uid, label, item.TryGetProperty("isDefault", out var primary) && primary.ValueKind == JsonValueKind.True));
            }
            return inputs.ToArray();
        }
        internal static bool Valid(string id) { return !string.IsNullOrEmpty(id) && id.Length <= 1024 && !id.Any(char.IsControl); }
        internal static async Task<MicrophoneInput[]> ReadAsync()
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "capri-mic")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--list");
            using var process = Spawn(start);
            if (process == null) throw new IOException("Couldn't start the microphone device list.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token), error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0) throw new IOException("Couldn't list microphones. Try Refresh devices.");
                await error;
                return Parse(await output);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            }
        }
    }
}
