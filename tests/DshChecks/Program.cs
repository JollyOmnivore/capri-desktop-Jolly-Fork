using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CapriDesktop;

string root = Path.GetFullPath(args.Length == 0 ? "out/dsh-runtime" : args[0]);
string bridge = Path.GetFullPath("integrations/dsh/bridge.mjs");
string scratch = Path.Combine(Path.GetTempPath(), "capri-dsh-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS " + label);
    checks++;
}
TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start();
int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using HttpListener listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
listener.Start();
List<string> requests = new List<string>();
int step = 0;
string mode = "tools";
string deniedFolder = Path.Combine(Environment.CurrentDirectory, ".local", "dsh-sandbox-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(deniedFolder);
string blockedFile = Path.Combine(deniedFolder, "blocked.txt");
TaskCompletionSource reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Task serving = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); } catch { break; }
        using StreamReader reader = new StreamReader(context.Request.InputStream);
        string request = await reader.ReadToEndAsync();
        requests.Add(request);
        reached.TrySetResult();
        if (mode == "stall") continue;
        object delta;
        string finish = "tool_calls";
        if (mode == "tools" && step < 3)
        {
            string tool = step == 0 ? "read" : step == 1 ? "write" : "bash";
            object values = step == 0 ? new { file_path = "sample.txt" } : step == 1 ? (object)new { file_path = "created.txt", content = "written by DSH" } : new { command = "printf 'shell works' > shell.txt", description = "Write a harmless shell test file" };
            delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call" + step, type = "function", function = new { name = tool, arguments = JsonSerializer.Serialize(values) } } } };
        }
        else if (mode == "outside" && step == 0)
            delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "outside", type = "function", function = new { name = "write", arguments = JsonSerializer.Serialize(new { file_path = "../escape.txt", content = "escape" }) } } } };
        else if (mode == "denied" && step == 0)
            delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "denied", type = "function", function = new { name = "write", arguments = JsonSerializer.Serialize(new { file_path = "denied.txt", content = "must not exist" }) } } } };
        else if (mode == "shell-escape" && step == 0)
            delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "escape", type = "function", function = new { name = "bash", arguments = JsonSerializer.Serialize(new { command = "printf blocked > '" + blockedFile.Replace("'", "'\\''") + "'", description = "Attempt a test write outside the workspace" }) } } } };
        else if (mode == "symlink" && step == 0)
            delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "symlink", type = "function", function = new { name = "read", arguments = JsonSerializer.Serialize(new { file_path = "outside/secret.txt" }) } } } };
        else if (mode == "escalation" && step == 0)
            delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "escalation", type = "function", function = new { name = "bash", arguments = JsonSerializer.Serialize(new { command = "printf blocked > escalation.txt", description = "Request unsupported escalation", sandbox_permissions = "danger-full-access", justification = "Test only" }) } } } };
        else { delta = new { role = "assistant", content = "Finished the test." }; finish = "stop"; }
        step++;
        string Frame(object change, string reason) => "data: " + JsonSerializer.Serialize(new { id = "test", @object = "chat.completion.chunk", created = 1, model = "qwen3.5:9b", choices = new[] { new { index = 0, delta = change, finish_reason = reason } } }) + "\n\n";
        byte[] bytes = Encoding.UTF8.GetBytes(Frame(delta, null) + Frame(new { }, finish) + "data: [DONE]\n\n");
        context.Response.ContentType = "text/event-stream";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
});

try
{
    string workspace = Path.Combine(scratch, "workspace");
    Directory.CreateDirectory(workspace);
    File.WriteAllText(Path.Combine(workspace, "sample.txt"), "test file content");
    DshOptions options = new DshOptions { Node = "/usr/local/bin/node", Runtime = root, Workspace = workspace, Port = port };
    string old = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "fake-private-key");
    var info = DshBrain.StartInfo(options, bridge, Path.Combine(scratch, "home"));
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", old);
    Check(!info.Environment.ContainsKey("ANTHROPIC_API_KEY") && !info.Environment.ContainsKey("NODE_OPTIONS") && info.Environment["DSH_TELEMETRY_DISABLED"] == "1", "runtime strips inherited credentials and Node injection options");
    Check(!info.UseShellExecute && info.ArgumentList[0] == bridge, "runtime launches without shell interpolation");
    DshBrain brain = new DshBrain();
    List<string> approvals = new List<string>(), activity = new List<string>();
    string Ask(Func<string, string, string, CancellationToken, Task<bool>> approve) => brain.Ask(options, bridge, Path.Combine(scratch, "home"), "qwen3.5:9b", "You are Capri. Keep the custom personality.", "Run the test.", new List<ChatMessage>(), approve, (tool, result) => activity.Add(tool + (result == null ? ":start" : ":end")));
    string answer = Ask((tool, arguments, cwd, stop) => { approvals.Add(tool); return Task.FromResult(true); });
    Check(answer == "Finished the test.", "waits for the final assistant answer after chained tool calls");
    Check(approvals.SequenceEqual(new[] { "read", "write", "bash" }), "every actual DSH tool asks for approval before execution");
    Check(File.ReadAllText(Path.Combine(workspace, "created.txt")) == "written by DSH", "approved DSH write creates a workspace file");
    Check(File.ReadAllText(Path.Combine(workspace, "shell.txt")) == "shell works", "approved DSH bash runs in the workspace");
    Check(activity.SequenceEqual(new[] { "read:start", "read:end", "write:start", "write:end", "bash:start", "bash:end" }), "actual tool events reach Capri activity callbacks in order");
    Check(requests[0].Contains("Keep the custom personality.") && requests.Any(x => x.Contains("test file content")), "custom personality and real read result reach the local model");
    Check(requests.All(x => !x.Contains("fake-private-key")), "private inherited credentials never enter model requests");
    mode = "denied"; step = 0;
    Ask((tool, arguments, cwd, stop) => Task.FromResult(false));
    Check(!File.Exists(Path.Combine(workspace, "denied.txt")), "denied write has no file effect");
    mode = "outside"; step = 0;
    int outsideApprovals = 0;
    Ask((tool, arguments, cwd, stop) => { outsideApprovals++; return Task.FromResult(true); });
    Check(outsideApprovals == 0 && !File.Exists(Path.Combine(scratch, "escape.txt")), "file traversal outside the workspace is rejected before approval");
    mode = "shell-escape"; step = 0;
    Ask((tool, arguments, cwd, stop) => Task.FromResult(true));
    Check(!File.Exists(blockedFile) && requests.Last().Contains("Operation not permitted"), "Mac sandbox blocks an approved shell write outside workspace and temp roots");
    Directory.CreateDirectory(Path.Combine(scratch, "private"));
    File.WriteAllText(Path.Combine(scratch, "private", "secret.txt"), "mock private data");
    Directory.CreateSymbolicLink(Path.Combine(workspace, "outside"), Path.Combine(scratch, "private"));
    mode = "symlink"; step = 0;
    int symlinkApprovals = 0;
    Ask((tool, arguments, cwd, stop) => { symlinkApprovals++; return Task.FromResult(true); });
    Check(symlinkApprovals == 0 && !requests.Last().Contains("mock private data"), "symlink reads outside the selected workspace are rejected");
    mode = "escalation"; step = 0;
    int escalationApprovals = 0;
    Ask((tool, arguments, cwd, stop) => { escalationApprovals++; return Task.FromResult(true); });
    Check(escalationApprovals == 0 && !File.Exists(Path.Combine(workspace, "escalation.txt")), "model cannot switch to unrestricted sandbox permissions");
    try { brain.Ask(new DshOptions(), bridge, scratch, "qwen3.5:9b", "", "", new(), null, null, () => false); throw new Exception("Expected stale turn rejection"); }
    catch (OperationCanceledException) { Check(true, "stale queued turns cannot start a runtime or cancel a newer turn"); }
    mode = "stall"; step = 0;
    reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task canceled = Task.Run(() => Ask((tool, arguments, cwd, stop) => Task.FromResult(false)));
    await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    brain.Cancel();
    try { await canceled.WaitAsync(TimeSpan.FromSeconds(6)); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { Check(true, "cancellation stops a stalled runtime promptly"); }
    Check(Pet.Newest(new Settings { Dsh = true }).Dsh && Pet.Kept(new Settings(), new Pet.NewSettings { Dsh = true }).Dsh, "experimental engine selection survives settings migration");
    if (args.Contains("--live"))
    {
        File.WriteAllText(Path.Combine(workspace, "live-check.txt"), "The test token is CAPRI_DSH_47219.");
        options.Port = 11434;
        int reads = 0;
        string live = brain.Ask(options, bridge, Path.Combine(scratch, "live-home"), "qwen3.5:9b", "You are Capri. Answer concisely and use tools when asked.", "Use the read tool to read live-check.txt in the workspace. Reply with the exact test token from that file. Do not run commands or modify any files.", new(),
            (tool, arguments, cwd, stop) => { using var values = JsonDocument.Parse(arguments); bool allowed = tool == "read" && values.RootElement.GetProperty("file_path").GetString() == "live-check.txt"; if (allowed) reads++; return Task.FromResult(allowed); }, null);
        Check(reads > 0 && live.Contains("CAPRI_DSH_47219"), "local Qwen uses the real DSH read tool and answers from its result");
    }
    Console.WriteLine(checks + " DSH checks passed.");
}
finally
{
    listener.Stop();
    await serving;
    Directory.Delete(scratch, true);
    Directory.Delete(deniedFolder, true);
}
