using System.Diagnostics;
using System.Text.Json;
using System.Security;
using CapriDesktop;

if (!OperatingSystem.IsMacOS()) { Console.WriteLine("Mac app launcher checks require macOS."); return; }
string scratch = Path.Combine(Path.GetTempPath(), "capri-app-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int checks = 0;
void Check(bool good, string label) { if (!good) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
string Bundle(string name, string display, string id, string folder = null)
{
    string path = Path.Combine(folder ?? scratch, name + ".app");
    Directory.CreateDirectory(Path.Combine(path, "Contents", "MacOS"));
    string E(string text) => SecurityElement.Escape(text);
    File.WriteAllText(Path.Combine(path, "Contents", "Info.plist"), "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>" + E(id) + "</string><key>CFBundleDisplayName</key><string>" + E(display) + "</string><key>CFBundleName</key><string>" + E(name) + "</string><key>CFBundlePackageType</key><string>APPL</string><key>CFBundleExecutable</key><string>Fixture</string></dict></plist>");
    return path;
}
string Result(string status, bool visible = false, bool activated = true) => JsonSerializer.Serialize(new { status, visible, activated, message = status == "failed" ? "Launch rejected" : "" });
string Open(string name) => Hands.Do("open_app", new ToolArguments { Name = name });
var realRun = MacLauncher.Run;
var realRefresh = MacLauncher.Refresh;
try
{
    string nested = Path.Combine(scratch, "Editors");
    string editor = Bundle("Visual Studio Code", "Code Editor", "test.capri.editor", nested);
    Bundle("Hidden Helper", "Internal helper", "test.capri.inner", Path.Combine(editor, "Contents"));
    Bundle("Hidden", "Hidden", "test.capri.hidden", Path.Combine(scratch, ".hidden"));
    string chrome = Bundle("Google Chrome", "Google Chrome", "test.capri.chrome");
    Directory.CreateSymbolicLink(Path.Combine(scratch, "Duplicate.app"), editor);
    var catalog = MacLauncher.Catalog(new[] { scratch, nested, scratch });
    Check(catalog.Count == 2, "native catalog finds nested apps and deduplicates roots and bundle symlinks");
    var code = catalog.Single(app => app.BundleId == "test.capri.editor");
    Check(code.Name == "Code Editor" && code.Aliases.Contains("Visual Studio Code"), "native catalog reads display name, filename aliases and bundle ID");
    Check(!catalog.Any(app => app.Name == "Internal helper" || app.Name == "Hidden"), "catalog does not scan app internals or hidden directories");
    Limbs.Attach(new Brain());
    Hands.OnMac = true;
    Check(Hands.Match(catalog, "VS Code").Single().Id == code.Id, "known aliases resolve to installed app filenames");
    Check(Hands.Match(catalog, "test.capri.editor").Single().Id == code.Id, "exact bundle identifiers resolve independently of display name");
    Check(Hands.Match(catalog, code.Id).Single().Id == code.Id, "exact catalog paths resolve with spaces intact");
    var variants = new List<InstalledApp>(catalog) { new InstalledApp("Google Chrome Beta", "/Applications/Google Chrome Beta.app", "test.capri.beta", "Google Chrome Beta") };
    Check(Hands.Match(variants, "chrome").Single().Id == catalog.Single(app => app.BundleId == "test.capri.chrome").Id, "Chrome alias prefers the stable app over a fuzzy Beta match");
    variants.Add(new InstalledApp("Chrome", "/Applications/Chrome.app", "test.capri.exact", "Chrome"));
    Check(Hands.Match(variants, "chrome").Single().BundleId == "test.capri.exact", "an exact app name takes priority over a configured alias");
    var same = new List<InstalledApp> { code, new InstalledApp(code.Name, "/Applications/Other Code.app", code.BundleId, "Visual Studio Code") };
    Hands.Catalog = () => same; Hands.Forget();
    int launches = 0;
    MacLauncher.Run = arguments => { launches++; return Result("opened", true); };
    Check(Open("Code Editor").StartsWith("Which app") && launches == 0, "equally named installations ask for clarification instead of guessing");
    Hands.Catalog = () => catalog; Hands.Forget();
    Check(Open("VS Code").StartsWith("Opened Code Editor.") && launches == 1, "shared open_app dispatches the resolved Mac app through the verified launcher");
    Check(Hands.Direct("open vscode", out string tool, out ToolArguments values) && tool == "open_app", "ordinary app requests still route to open_app");
    int catalogs = 0;
    Hands.Catalog = () => { catalogs++; return catalogs == 1 ? new List<InstalledApp>() : catalog; }; Hands.Forget();
    Check(Open("VS Code").StartsWith("Opened") && catalogs == 2, "missing app refreshes the cached catalog once before failing");
    catalogs = 0; Hands.Catalog = () => { catalogs++; return new List<InstalledApp>(); }; Hands.Forget();
    Check(Open("nonexistent capri fixture").Contains("No app called") && catalogs == 2, "an absent app gets one bounded refresh and a clear failure");
    MacLauncher.Run = _ => Result("already_running", true);
    Check(MacLauncher.Launch(code).Contains("already running") && !MacLauncher.Launch(code).StartsWith("Opened"), "already-running apps are reported separately from new launches");
    MacLauncher.Run = _ => Result("opened", false);
    Check(MacLauncher.Launch(code).Contains("no visible window was confirmed"), "a running menu-bar or windowless app is not claimed to have a visible window");
    launches = 0; MacLauncher.Run = _ => { launches++; return Result("unconfirmed"); };
    Check(MacLauncher.Launch(code).Contains("haven't retried") && launches == 1, "uncertain startup never retries a potentially successful launch");
    MacLauncher.Run = _ => Result("failed");
    Check(MacLauncher.Launch(code).StartsWith("Couldn't open") && MacLauncher.Launch(code).Contains("Launch rejected"), "Launch Services errors are returned rather than swallowed");
    launches = 0; MacLauncher.Run = _ => { launches++; return "invalid JSON"; };
    Check(MacLauncher.Launch(code).StartsWith("Couldn't confirm") && launches == 1, "malformed helper output cannot claim success or trigger a retry");
    var moved = new InstalledApp(code.Name, "/Applications/Moved Code.app", code.BundleId, "Visual Studio Code");
    List<string> paths = new();
    MacLauncher.Run = arguments => { paths.Add(arguments[1]); return Result(paths.Count == 1 ? "missing" : "opened", true); };
    MacLauncher.Refresh = () => new() { moved };
    Check(MacLauncher.Launch(code).StartsWith("Opened") && paths.SequenceEqual(new[] { code.Id, moved.Id }), "a confirmed missing path is refreshed by bundle ID and retried once at its new path");
    launches = 0; MacLauncher.Run = _ => { launches++; return Result("missing"); }; MacLauncher.Refresh = () => same;
    Check(MacLauncher.Launch(code).StartsWith("Couldn't open") && launches == 1, "a moved app with ambiguous replacements is not retried");
    var info = MacLauncher.StartInfo(new[] { "--launch", "/Applications/Words ' $(touch injected).app" });
    Check(!info.UseShellExecute && info.ArgumentList[1] == "/Applications/Words ' $(touch injected).app", "native app paths are argv data without shell interpolation");
    Check(!info.Environment.ContainsKey("ANTHROPIC_API_KEY") && !info.Environment.ContainsKey("DYLD_INSERT_LIBRARIES"), "native helper receives no API keys or injected library environment");
    Security.Has = _ => true; Security.Pick(Security.Trusted); Security.Set(Security.Open, false);
    Hands.Catalog = () => catalog; Hands.Forget(); launches = 0;
    var brain = new Brain(); Limbs.Attach(brain); MacLauncher.Run = _ => { launches++; return Result("opened"); };
    Check(Security.IsHold(brain.Act("open_app", new ToolArguments { Name = "VS Code" })) && launches == 0, "existing Security gate blocks both engines before the native launcher runs");
    Security.Drop(); Security.Pick(Security.Trusted);
    MacLauncher.Run = realRun;
    using JsonDocument missing = JsonDocument.Parse(realRun(new[] { "--launch", Path.Combine(scratch, "Missing.app") }));
    Check(missing.RootElement.GetProperty("status").GetString() == "missing", "real native helper recognizes a missing app without launching anything");
    using JsonDocument invalid = JsonDocument.Parse(realRun(new[] { "--launch", editor }));
    Check(invalid.RootElement.GetProperty("status").GetString() == "invalid", "real native helper rejects bundles without an executable");
    if (args.Contains("--live"))
    {
        string source = Path.Combine(scratch, "Fixture.swift");
        File.WriteAllText(source, "import AppKit\nlet app = NSApplication.shared\napp.setActivationPolicy(.regular)\nlet window = NSWindow(contentRect: NSRect(x: 120, y: 120, width: 280, height: 100), styleMask: [.titled, .closable], backing: .buffered, defer: false)\nwindow.title = \"Capri launcher test\"\nwindow.makeKeyAndOrderFront(nil)\napp.activate(ignoringOtherApps: true)\nTimer.scheduledTimer(withTimeInterval: 7, repeats: false) { _ in app.terminate(nil) }\napp.run()\n");
        string liveId = "test.capri.launch." + Guid.NewGuid().ToString("N");
        string live = Bundle("Launch Fixture", "Launch Fixture", liveId);
        var compile = new ProcessStartInfo("/usr/bin/xcrun") { UseShellExecute = false };
        foreach (string argument in new[] { "swiftc", source, "-o", Path.Combine(live, "Contents", "MacOS", "Fixture") }) compile.ArgumentList.Add(argument);
        using (var process = Process.Start(compile)) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "isolated launcher test app compiles"); }
        var nativeApp = new InstalledApp("Launch Fixture", live, liveId);
        string started = MacLauncher.Launch(nativeApp);
        Check(started.StartsWith("Opened Launch Fixture."), "real Launch Services starts the exact test bundle and verifies its process");
        Check(MacLauncher.Launch(nativeApp).Contains("already running"), "real Launch Services recognizes the already-running exact test bundle");
        string other = Bundle("Other Installation", "Launch Fixture", liveId);
        File.Copy(Path.Combine(live, "Contents", "MacOS", "Fixture"), Path.Combine(other, "Contents", "MacOS", "Fixture"));
        Check(MacLauncher.Launch(new InstalledApp("Other Installation", other, liveId)).StartsWith("Opened Other Installation."), "an explicitly selected second installation is not substituted with the running copy");
        await Task.Delay(7500);
    }
    Console.WriteLine(checks + " Mac launcher checks passed.");
}
finally
{
    MacLauncher.Run = realRun; MacLauncher.Refresh = realRefresh;
    Directory.Delete(scratch, true);
}
