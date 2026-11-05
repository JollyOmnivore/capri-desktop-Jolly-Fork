using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CapriDesktop
{
    internal sealed class DshOptions
    {
        public string Node { get; set; } = "/usr/local/bin/node";
        public string Runtime { get; set; } = "";
        public string Workspace { get; set; } = "";
        public int Port { get; set; } = 11434;
        public bool Computer { get; set; }

        internal static DshOptions Read(string folder)
        {
            string path = Path.Combine(folder, "dsh.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<DshOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException("DSH settings are empty.");
            string runtime = "";
            foreach (string candidate in new[] { Path.Combine(AppContext.BaseDirectory, "..", "dsh-runtime"), Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dsh-runtime"), Path.Combine(Environment.CurrentDirectory, "out", "dsh-runtime") })
                if (File.Exists(Path.Combine(candidate, "node_modules", "@deepseek-ai", "dsh", "package.json"))) { runtime = Path.GetFullPath(candidate); break; }
            return new DshOptions { Workspace = Path.Combine(folder, "dsh-workspace"), Runtime = runtime };
        }
    }

    internal sealed class DshBrain
    {
        private readonly object gate = new object();
        private static readonly SemaphoreSlim desktopLease = new SemaphoreSlim(1, 1);
        private CancellationTokenSource active;

        internal void Cancel()
        {
            lock (gate) active?.Cancel();
        }

        internal static ProcessStartInfo StartInfo(DshOptions options, string bridge, string home)
        {
            if (!OperatingSystem.IsMacOS()) throw new InvalidOperationException("The experimental DSH backend is currently enabled on Mac only.");
            if (options.Port < 1 || options.Port > 65535) throw new InvalidDataException("Invalid local Ollama port.");
            foreach (string path in new[] { options.Node, options.Runtime, options.Workspace, bridge, home })
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("DSH paths must be absolute. Check dsh.json.");
            if (!File.Exists(options.Node) || !File.Exists(Path.Combine(options.Runtime, "node_modules", "@deepseek-ai", "dsh", "package.json")) || !File.Exists(bridge)) throw new FileNotFoundException("Set up the pinned DSH runtime and its absolute path in dsh.json first.");
            Directory.CreateDirectory(options.Workspace);
            Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ProcessStartInfo info = new ProcessStartInfo(options.Node) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = options.Workspace };
            info.ArgumentList.Add(bridge);
            info.ArgumentList.Add(options.Runtime);
            info.Environment.Clear();
            foreach (string name in new[] { "HOME", "PATH", "TMPDIR", "LANG", "LC_ALL" })
            {
                string value = Environment.GetEnvironmentVariable(name);
                if (value != null) info.Environment[name] = value;
            }
            info.Environment["DSH_HOME"] = home;
            info.Environment["DSH_TELEMETRY_DISABLED"] = "1";
            info.Environment["DO_NOT_TRACK"] = "1";
            info.Environment["CAPRI_LOCAL_MODEL_KEY"] = "ollama";
            return info;
        }

        internal string Ask(DshOptions options, string bridge, string home, string model, string persona, string text, List<ChatMessage> history, Func<string, string, string, CancellationToken, Task<bool>> approve, Action<string, string> activity, Func<bool> current = null, DshTools tools = null, DshComputer computer = null)
        {
            using CancellationTokenSource stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            lock (gate) { if (current != null && !current()) throw new OperationCanceledException(); active?.Cancel(); active = stop; }
            bool leased = false;
            try
            {
                if (options.Computer) { desktopLease.Wait(stop.Token); leased = true; }
                return Run(options, bridge, home, model, persona, text, history, approve, activity, stop.Token, tools, computer).GetAwaiter().GetResult();
            }
            finally
            {
                if (leased) desktopLease.Release();
                lock (gate) if (active == stop) active = null;
            }
        }

        private static async Task<string> Run(DshOptions options, string bridge, string home, string model, string persona, string text, List<ChatMessage> history, Func<string, string, string, CancellationToken, Task<bool>> approve, Action<string, string> activity, CancellationToken stop, DshTools tools, DshComputer computer)
        {
            string runHome = Path.Combine(home, "runs", Guid.NewGuid().ToString("N"));
            using Process process = new Process { StartInfo = StartInfo(options, bridge, runHome) };
            stop.ThrowIfCancellationRequested();
            process.Start();
            Task errors = Drain(process.StandardError);
            string session = Guid.NewGuid().ToString("N"), reply = null, turnProblem = null, lastFailure = null;
            if (computer != null && approve != null)
            {
                var approval = approve;
                approve = async (tool, arguments, cwd, token) =>
                {
                    await computer.Wait(token);
                    bool allowed = await approval(tool, arguments, cwd, token);
                    if (allowed) await computer.Wait(token);
                    return allowed;
                };
            }
            int counter = 0;
            Dictionary<string, string> calls = new Dictionary<string, string>();
            Dictionary<string, string> outcomes = new Dictionary<string, string>();
            async Task Send(object message) { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)); await process.StandardInput.FlushAsync(); }
            async Task<JsonElement> Request(string method, object parameters)
            {
                int id = ++counter;
                await Send(new { jsonrpc = "2.0", id, method, @params = parameters });
                while (true)
                {
                    string line = await process.StandardOutput.ReadLineAsync(stop);
                    if (line == null) throw new InvalidOperationException("DSH stopped unexpectedly. No action was retried.");
                    if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("DSH returned an oversized frame.");
                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement frame = document.RootElement;
                    if (frame.TryGetProperty("method", out JsonElement named))
                    {
                        string incoming = named.GetString();
                        if (frame.TryGetProperty("id", out JsonElement request))
                        {
                            if (incoming == "capri/tool")
                            {
                                string result;
                                try
                                {
                                    JsonElement args = frame.GetProperty("params");
                                    result = tools == null ? "Couldn't run that tool: Capri bridge is unavailable." : await tools.Invoke(args.GetProperty("tool").GetString(), args.GetProperty("arguments").GetRawText(), options.Workspace, approve, stop);
                                    if (args.TryGetProperty("callId", out JsonElement call) && call.ValueKind == JsonValueKind.String) outcomes[call.GetString()] = result;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception) { result = "Couldn't finish that Capri action. It was not retried."; }
                                stop.ThrowIfCancellationRequested();
                                await Send(new { jsonrpc = "2.0", id = request.Clone(), result = new { text = result, isError = Hands.Failed(result) || Security.IsHold(result) } });
                                continue;
                            }
                            if (incoming == "capri/computer")
                            {
                                if (!options.Computer || computer == null) throw new InvalidOperationException("Computer use is disabled.");
                                await computer.Update(frame.GetProperty("params"), stop);
                                await Send(new { jsonrpc = "2.0", id = request.Clone(), result = new { ready = true } });
                                continue;
                            }
                            bool allowed = false;
                            if (incoming == "capri/approval" && approve != null)
                            {
                                JsonElement args = frame.GetProperty("params");
                                if (args.GetProperty("tool").GetString().StartsWith("computer_", StringComparison.Ordinal))
                                {
                                    if (!options.Computer || computer == null) throw new InvalidOperationException("Computer use is disabled.");
                                    await computer.Announce(args.GetProperty("tool").GetString(), stop);
                                }
                                allowed = await approve(args.GetProperty("tool").GetString(), args.GetProperty("arguments").GetRawText(), args.GetProperty("cwd").GetString(), stop);
                            }
                            stop.ThrowIfCancellationRequested();
                            await Send(new { jsonrpc = "2.0", id = request.Clone(), result = new { allowed } });
                        }
                        else if (incoming == "session.event")
                        {
                            JsonElement data = frame.GetProperty("params");
                            if (data.GetProperty("sessionId").GetString() != session) continue;
                            JsonElement entry = data.GetProperty("event"), value = entry.GetProperty("data");
                            string kind = entry.GetProperty("type").GetString();
                            if (kind == "assistant/message") reply = Text(value.GetProperty("message").GetProperty("content"));
                            else if (kind == "turn/end") turnProblem = TurnProblem(value.GetProperty("reason"));
                            else if (kind == "tool/call")
                            {
                                string tool = value.GetProperty("name").GetString();
                                calls[value.GetProperty("callId").GetString()] = tool;
                                activity?.Invoke(tool, null);
                            }
                            else if (kind == "tool/result")
                            {
                                JsonElement message = value.GetProperty("message");
                                string call = message.GetProperty("toolCallId").GetString();
                                bool failed = message.TryGetProperty("isError", out JsonElement bad) && bad.GetBoolean();
                                string detail = failed ? "Couldn't finish " + (calls.TryGetValue(call, out string called) ? called : "that action") + ": " + SafeProblem(Text(message.GetProperty("content"))) : "Finished.";
                                if (failed) lastFailure = detail;
                                if (calls.TryGetValue(call, out string tool)) activity?.Invoke(tool, outcomes.TryGetValue(call, out string outcome) ? outcome : detail);
                            }
                        }
                    }
                    else if (frame.TryGetProperty("id", out JsonElement response) && response.ValueKind == JsonValueKind.Number && response.GetInt32() == id)
                    {
                        if (frame.TryGetProperty("error", out JsonElement error)) throw new InvalidOperationException("DSH couldn't finish this turn. No action was retried. " + error.GetProperty("message").GetString());
                        return frame.GetProperty("result").Clone();
                    }
                }
            }
            try
            {
                JsonElement hello = await Request("capri/initialize", new { cwd = options.Workspace, port = options.Port, model, persona, tools = tools?.Schemas ?? Array.Empty<JsonElement>(), toolMode = tools?.Mode ?? "all", computer = options.Computer && computer != null, hostPid = Environment.ProcessId });
                if (hello.GetProperty("serverInfo").GetProperty("name").GetString() != "deepseek-harness-sdk-runtime") throw new InvalidDataException("Unexpected DSH runtime.");
                StringBuilder prompt = new StringBuilder();
                foreach (ChatMessage message in history.TakeLast(12))
                    if (message.Role == "user" || message.Role == "assistant") prompt.Append(message.Role).Append(": ").Append(message.Content).Append('\n');
                string context = prompt.Length == 0 ? "" : "Previous conversation for context; do not repeat previous actions:\n" + prompt + "\nCurrent request:\n";
                await Request("session/prompt", new { sessionId = session, contentBlocks = new[] { new { type = "text", text = context + text } } });
                await Request("session/wait", new { sessionId = session });
                if (turnProblem != null) throw new InvalidOperationException(turnProblem + (lastFailure == null ? "" : " Last tool error: " + lastFailure));
                tools?.VerifyCompletion();
                if (string.IsNullOrWhiteSpace(reply)) throw new InvalidOperationException(lastFailure ?? "The local model ended without a visible reply. No action was retried.");
                return reply;
            }
            finally
            {
                try
                {
                    await Send(new { jsonrpc = "2.0", id = ++counter, method = "shutdown" });
                    process.StandardInput.Close();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch { try { if (!process.HasExited) process.Kill(true); } catch (Exception) { } }
                try { await errors.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
                if (options.Computer) try { Directory.Delete(runHome, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task Drain(StreamReader reader)
        {
            char[] buffer = new char[4096];
            while (await reader.ReadAsync(buffer, 0, buffer.Length) > 0) { }
        }

        internal static string TurnProblem(JsonElement reason)
        {
            string kind = reason.GetProperty("kind").GetString();
            if (kind == "completed") return null;
            if (kind == "max-tokens") return "The local model reached its output-token limit before finishing. Inspect any completed actions before retrying.";
            if (kind == "error" && reason.TryGetProperty("error", out JsonElement error) && error.TryGetProperty("message", out JsonElement message)) return SafeProblem(message.GetString());
            return "DSH turn ended as " + kind + ". Inspect any completed actions before retrying.";
        }

        private static string SafeProblem(string text)
        {
            string clean = Brain.Redacted(text ?? "Unknown tool error");
            return clean.Length > 1200 ? clean.Substring(0, 1200) + "…" : clean;
        }

        private static string Text(JsonElement blocks)
        {
            StringBuilder text = new StringBuilder();
            foreach (JsonElement block in blocks.EnumerateArray())
                if (block.GetProperty("type").GetString() == "text") text.Append(block.GetProperty("text").GetString());
            return text.ToString();
        }
    }
}
