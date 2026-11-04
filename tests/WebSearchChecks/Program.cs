using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

const BindingFlags members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
Assembly app = Assembly.Load("Capri");
Type web = app.GetType("CapriDesktop.WebSearch", true);
Type hands = app.GetType("CapriDesktop.Hands", true);
Type arguments = app.GetType("CapriDesktop.ToolArguments", true);
Type security = app.GetType("CapriDesktop.Security", true);
Type brain = app.GetType("CapriDesktop.Brain", true);
int checks = 0, browsers = 0;
object Call(Type type, object target, string name, params object[] values)
{
    MethodInfo method = type.GetMethods(members).Where(method => method.Name == name && method.GetParameters().Length >= values.Length && method.GetParameters().Skip(values.Length).All(parameter => parameter.IsOptional)).OrderBy(method => method.GetParameters().Length).First();
    object[] supplied = values.Concat(Enumerable.Repeat(Type.Missing, method.GetParameters().Length - values.Length)).ToArray();
    try { return method.Invoke(target, supplied); }
    catch (TargetInvocationException error) { throw error.InnerException ?? error; }
}
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    checks++;
}
string Search(string query, int count = 3) { return (string)Call(web, null, "Search", query, count); }
string Results(string json, int count = 3) { return (string)Call(web, null, "Results", json, count); }
string Fixture(params object[] results) { return JsonSerializer.Serialize(new { results }); }
string valid = Fixture(new { title = "<b>Reliable</b> source", url = "https://example.com/source", content = "A readable &amp; useful excerpt." });
hands.GetField("Run", members).SetValue(null, new Action<string>(url => browsers++));
hands.GetField("SearchWeb", members).SetValue(null, new Func<string, int, string>((query, count) => Search(query, count)));
brain.GetField("Keeping", members).SetValue(null, false);

foreach (string address in new[] { "https://external.example/v1", "file:///etc/passwd", "http://user:password@localhost:8080", "http://localhost:8080?token=example" })
{
    bool rejected = false;
    try { Call(web, null, "Endpoint", address); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "search endpoint rejects nonlocal addresses or embedded credentials");
}
Check(((Uri)Call(web, null, "Endpoint", "http://127.0.0.1:8080/")).AbsoluteUri == "http://127.0.0.1:8080/search", "loopback endpoint gets the search route");
string readable = Results(valid);
Check(readable.Contains("https://example.com/source") && readable.Contains("A readable & useful excerpt.") && !readable.Contains("<b>"), "results contain readable text, title and source URL");
Check(readable.Contains("untrusted") && readable.Contains("full pages have not been read"), "tool result marks excerpts as untrusted and incomplete");
string filtered = Results(Fixture(new { title = "Bad", url = "javascript:alert(1)", content = "Bad" }, new { title = "Local", url = "http://127.0.0.1/admin", content = "Private" }, new { title = "Empty", url = "https://example.com/empty", content = "" }));
Check(filtered.StartsWith("Couldn't"), "unsafe links and empty excerpts are excluded");
Check(Results("[]").StartsWith("Couldn't") && Results("{\"results\":[]}").StartsWith("Couldn't"), "missing results produce a plain failure instead of invented information");
string many = Fixture(Enumerable.Range(0, 20).Select(i => (object)new { title = "Title", url = "https://example.com/" + i, content = new string('x', 30000) }).ToArray());
Check(Results(many, 50).Length < 6500 && !Results(many, 50).Contains("https://example.com/5\n"), "result count and excerpt size are bounded for the local model");
Check(Search("").StartsWith("Couldn't") && Search(new string('x', 501)).StartsWith("Couldn't"), "empty and excessive queries are rejected before network access");
object[] direct = { "Search the web for Kyutai Pocket TTS", null, null };
Check((bool)hands.GetMethod("Direct", members).Invoke(null, direct) && (string)direct[1] == "web_search", "explicit web searches choose the readable search tool");
object[] youtube = { "search youtube for cats", null, null };
Check((bool)hands.GetMethod("Direct", members).Invoke(null, youtube) && (string)youtube[1] == "open_website", "explicit site searches retain browser behavior");
object toolArgs = Activator.CreateInstance(arguments, true);
arguments.GetField("Query", members).SetValue(toolArgs, "capri & local search");
int open = (int)security.GetField("Open", members).GetRawConstantValue();
Check((int)Call(security, null, "Needs", "web_search", toolArgs) == open, "web search respects the existing website permission gate");

async Task Request(string body, int status, Action<string> inspect)
{
    using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    web.GetField("Url", members).SetValue(null, "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port);
    using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    Task server = Task.Run(async () =>
    {
        using TcpClient socket = await listener.AcceptTcpClientAsync(timeout.Token);
        using NetworkStream stream = socket.GetStream();
        List<byte> bytes = new List<byte>();
        while (bytes.Count < 65536)
        {
            int next = stream.ReadByte();
            if (next < 0) throw new Exception("Incomplete request");
            bytes.Add((byte)next);
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
        }
        string headers = Encoding.ASCII.GetString(bytes.ToArray());
        Check(headers.StartsWith("GET /search?q=capri%20%26%20local%20search&format=json&safesearch=1 HTTP/1.1"), "search query is encoded and JSON is requested from the local service");
        Check(!headers.Contains("Authorization", StringComparison.OrdinalIgnoreCase) && !headers.Contains("Cookie:", StringComparison.OrdinalIgnoreCase), "search requests contain no provider keys or browser cookies");
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string location = status == 302 ? "Location: https://example.invalid/never-follow\r\n" : "";
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Type: application/json\r\n" + location + "Content-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, timeout.Token);
        try { await stream.WriteAsync(payload, timeout.Token); }
        catch (IOException) { }
    });
    string answer = (string)Call(hands, null, "Do", "web_search", toolArgs);
    await server.WaitAsync(timeout.Token);
    inspect(answer);
}
await Request(valid, 200, answer => Check(answer.Contains("https://example.com/source"), "actual tool call returns excerpts without launching a browser"));
await Request("{broken", 200, answer => Check(answer.Contains("unreadable JSON"), "invalid service JSON gives a useful error"));
await Request("{}", 403, answer => Check(answer.Contains("enable JSON"), "disabled JSON output gives setup guidance"));
await Request("{}", 302, answer => Check(answer.Contains("HTTP 302"), "redirects are rejected instead of contacting another service"));
await Request(new string('x', 1100000), 200, answer => Check(answer.Contains("too large"), "oversized service responses are rejected"));
Check(browsers == 0, "none of the search tool calls opened a browser");
int chained = await Chains.Run();

if (args.Length >= 2 && args[0] == "--live")
{
    web.GetField("Url", members).SetValue(null, args[1]);
    string actual = Search("Kyutai Pocket TTS");
    Check(actual.Contains("https://") && actual.Contains("Web search excerpts"), "existing SearXNG service returns real readable public search results");
    if (args.Length == 3)
    {
        int searches = 0;
        hands.GetField("SearchWeb", members).SetValue(null, new Func<string, int, string>((query, count) => { searches++; return Search(query, count); }));
        object model = Activator.CreateInstance(brain, true);
        brain.GetField("Model", members).SetValue(model, args[2]);
        FieldInfo act = brain.GetField("Act", members);
        act.SetValue(model, hands.GetMethod("Do", members).CreateDelegate(act.FieldType));
        string reply = (string)Call(brain, model, "Ask", "Search the web for who makes Pocket TTS and answer from the search results with a source URL.");
        Console.WriteLine("Local model public test: " + searches + " searches; " + reply);
        Check(searches > 0 && reply.Contains("http") && reply.Contains("Kyutai", StringComparison.OrdinalIgnoreCase), "local model searches, reads the excerpts and answers with a source");
        List<string> opened = new List<string>();
        hands.GetField("Run", members).SetValue(null, new Action<string>(url => opened.Add(url)));
        object next = Activator.CreateInstance(brain, true);
        brain.GetField("Model", members).SetValue(next, args[2]);
        act.SetValue(next, hands.GetMethod("Do", members).CreateDelegate(act.FieldType));
        string followed = (string)Call(brain, next, "Ask", "Find the official Pocket TTS GitHub repo and open it.");
        Console.WriteLine("Local model chained answer: " + followed);
        Check(opened.Count == 1 && opened[0].TrimEnd('/') == "https://github.com/kyutai-labs/pocket-tts", "local model searches then selects and opens the actual official result without a real browser launch");
    }
}
Console.WriteLine((checks + chained) + " web search and chain checks passed.");
