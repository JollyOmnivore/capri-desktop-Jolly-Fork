using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CapriDesktop
{
    internal static class MacLauncher
    {
        internal sealed class Entry
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public string BundleId { get; set; }
            public string[] Aliases { get; set; }
        }

        internal sealed class Result
        {
            public string Status { get; set; }
            public bool Visible { get; set; }
            public bool Activated { get; set; }
            public string Message { get; set; }
        }

        internal static Func<string[], string> Run = Execute;
        internal static Func<List<InstalledApp>> Refresh = () => Catalog(Limbs.MacFolders());
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "vscode", "Visual Studio Code" }, { "vs code", "Visual Studio Code" }, { "vsc", "Visual Studio Code" },
            { "chrome", "Google Chrome" }, { "edge", "Microsoft Edge" }, { "teams", "Microsoft Teams" },
            { "word", "Microsoft Word" }, { "excel", "Microsoft Excel" }, { "powerpoint", "Microsoft PowerPoint" },
            { "ppt", "Microsoft PowerPoint" }, { "outlook", "Microsoft Outlook" }
        };

        internal static string Alias(string name) { return Aliases.TryGetValue(name, out string full) ? full : null; }

        internal static List<InstalledApp> Catalog(IEnumerable<string> folders)
        {
            string[] arguments = new[] { "--catalog" }.Concat(folders).ToArray();
            List<Entry> entries = JsonSerializer.Deserialize<List<Entry>>(Run(arguments), Json) ?? throw new InvalidDataException("The Mac app catalog was empty.");
            return entries.Where(app => !string.IsNullOrWhiteSpace(app.Name) && System.IO.Path.IsPathFullyQualified(app.Path ?? "") && app.Path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                .GroupBy(app => app.Path, StringComparer.Ordinal).Select(group => group.First()).Select(app => new InstalledApp(app.Name, app.Path, app.BundleId ?? "", app.Aliases ?? Array.Empty<string>())).ToList();
        }

        internal static string Launch(InstalledApp app)
        {
            try
            {
                Result result = Read(app);
                if (result.Status == "missing")
                {
                    Hands.Forget();
                    List<InstalledApp> fresh = Refresh();
                    List<InstalledApp> matches = app.BundleId?.Length > 0 ? fresh.FindAll(other => other.BundleId == app.BundleId) : Hands.Match(fresh, app.Name);
                    if (matches.Count != 1) return "Couldn't open " + app.Name + ": it moved or was removed. Refresh the app list and give its exact name or path.";
                    app = matches[0];
                    result = Read(app);
                }
                if (result.Status == "opened" || result.Status == "already_running")
                {
                    string status = result.Status == "opened" ? "Opened " + app.Name + "." : app.Name + " was already running.";
                    return status + (result.Visible ? " A window is visible." : " It's running, but no visible window was confirmed.") + (result.Activated ? "" : " Bringing it to the foreground wasn't confirmed.");
                }
                if (result.Status == "unconfirmed") return "Couldn't confirm whether " + app.Name + " opened. It may still start; I haven't retried it.";
                return "Couldn't open " + app.Name + ": " + (string.IsNullOrWhiteSpace(result.Message) ? "the launcher rejected the request." : result.Message);
            }
            catch (Exception) { return "Couldn't confirm whether " + app.Name + " opened: the Mac launcher didn't answer correctly. I haven't retried it."; }
        }

        private static Result Read(InstalledApp app)
        {
            if (!System.IO.Path.IsPathFullyQualified(app.Id ?? "") || !app.Id.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid app path.");
            return JsonSerializer.Deserialize<Result>(Run(new[] { "--launch", app.Id }), Json) ?? throw new InvalidDataException("Empty launch result.");
        }

        internal static ProcessStartInfo StartInfo(string[] arguments)
        {
            string helper = System.IO.Path.Combine(AppContext.BaseDirectory, "capri-apps");
            ProcessStartInfo info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            info.Environment.Clear();
            foreach (string key in new[] { "HOME", "TMPDIR", "LANG", "LC_ALL" })
            {
                string value = Environment.GetEnvironmentVariable(key);
                if (value != null) info.Environment[key] = value;
            }
            return info;
        }

        private static string Execute(string[] arguments)
        {
            using Process process = Process.Start(StartInfo(arguments));
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(true); throw new TimeoutException("Mac app helper timed out."); }
            string text = output.GetAwaiter().GetResult();
            errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Mac app helper failed.");
            return text;
        }
    }
}
