using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
const string key = "private_test_test-only-not-a-real-key";
Assembly app = Assembly.Load("Capri");
Type cloud = app.GetType("CapriDesktop.Cloud", true);
Type brain = app.GetType("CapriDesktop.Brain", true);
Type arguments = app.GetType("CapriDesktop.ToolArguments", true);
Type callsType = typeof(List<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(typeof(string), arguments));
string gatewayDefault = (string)brain.GetField("GatewayDefault", members).GetRawConstantValue();
brain.GetField("WorkGateway", members).SetValue(null, "http://127.0.0.1:19001/v1");
brain.GetField("GatewayKeyPrefix", members).SetValue(null, "private_test_");
int checks = 0;

void Check(bool passes, string name)
{
    if (!passes) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    checks++;
}

object Call(Type type, object target, string name, params object[] values)
{
    MethodInfo method = type.GetMethods(members).Where(method => method.Name == name && method.GetParameters().Length >= values.Length && method.GetParameters().Skip(values.Length).All(parameter => parameter.IsOptional)).OrderBy(method => method.GetParameters().Length).First();
    object[] supplied = values.Concat(Enumerable.Repeat(Type.Missing, method.GetParameters().Length - values.Length)).ToArray();
    try { return method.Invoke(target, supplied); }
    catch (TargetInvocationException error) { throw error.InnerException ?? error; }
}

string Kind(string value) { return (string)Call(cloud, null, "KindOf", value); }
string Address(string kind, string path) { return (string)Call(brain, null, "Address", kind, path); }
object Remote(string value, string model) { return Activator.CreateInstance(cloud, members, null, new object[] { value, model }, null); }

Type settings = app.GetType("CapriDesktop.GatewaySettings", true);
string config = Path.Combine(Path.GetTempPath(), "capri-gateway-test-" + Guid.NewGuid().ToString("N") + ".json");
string configWas = Environment.GetEnvironmentVariable("CAPRI_GATEWAY_CONFIG");
try
{
    Environment.SetEnvironmentVariable("CAPRI_GATEWAY_CONFIG", config);
    Check(settings.GetField("BaseUrl", members).GetValue(Call(settings, null, "Load")) == null, "missing local settings disable the private gateway");
    File.WriteAllText(config, "{\"BaseUrl\":\"https://gateway.example.invalid/v1/\",\"KeyPrefix\":\"private_test_\"}");
    object loaded = Call(settings, null, "Load");
    Check((string)settings.GetField("BaseUrl", members).GetValue(loaded) == "https://gateway.example.invalid/v1" && (string)settings.GetField("KeyPrefix", members).GetValue(loaded) == "private_test_", "local JSON supplies endpoint and prefix without compiled employer details");
    foreach (string endpoint in new[] { "http://gateway.example.invalid/v1", "https://user:password@gateway.example.invalid/v1", "https://gateway.example.invalid/v1?token=example" })
    {
        File.WriteAllText(config, JsonSerializer.Serialize(new { BaseUrl = endpoint, KeyPrefix = "private_test_" }));
        Check(settings.GetField("BaseUrl", members).GetValue(Call(settings, null, "Load")) == null, "unsafe gateway URLs are rejected");
    }
    File.WriteAllText(config, "{not json}");
    Check(settings.GetField("BaseUrl", members).GetValue(Call(settings, null, "Load")) == null, "malformed local settings do not fall back to another provider");
}
finally
{
    Environment.SetEnvironmentVariable("CAPRI_GATEWAY_CONFIG", configWas);
    File.Delete(config);
}
Check(((string)Call(brain, null, "Redacted", "error " + key)).Contains("[redacted]"), "configured private key prefix is redacted from error messages");

Check(Kind(key) == "work" && Kind("private_test_") == null && Kind("unsupported_example") == null && Kind(null) == null, "work key recognition rejects empty and unsupported keys");
Check(Kind("sk-ant-example") == "anthropic" && Kind("nvapi-example") == "nvidia" && Kind("sk-proj-example") == "openai", "existing provider classification is unchanged");
Check(Address("work", "/chat/completions") == "http://127.0.0.1:19001/v1/chat/completions", "work keys route to the supplied work gateway");
Check(Address("anthropic", "/messages") == "https://api.anthropic.com/v1/messages" && Address("openai", "/chat/completions") == "https://api.openai.com/v1/chat/completions", "direct provider endpoints are unchanged");
object remote = Remote(key, gatewayDefault);
Check(!(bool)cloud.GetProperty("Claude", members).GetValue(remote) && (string)cloud.GetProperty("Name", members).GetValue(remote) == "Private gateway", "work gateway uses the chat-completions protocol and its own label");
IList models = (IList)Call(brain, null, "CloudModels", key);
Check(models.Count == 1 && (string)models[0] == gatewayDefault, "setup does not depend on the unavailable models endpoint");
brain.GetField("Keeping", members).SetValue(null, false);

async Task Request(string model, string operation, int status, string response, Action<JsonElement> inspect, string expectedError = null)
{
    using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    string local = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    brain.GetField("WorkGateway", members).SetValue(null, local + "/v1");
    foreach (string field in new[] { "OpenAi", "Anthropic", "Nvidia", "DeepSeek" }) brain.GetField(field, members).SetValue(null, local + "/wrong-provider");
    using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    Task server = Task.Run(async () =>
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
        using NetworkStream stream = client.GetStream();
        stream.ReadTimeout = 10000;
        List<byte> headers = new List<byte>();
        while (true)
        {
            int next = stream.ReadByte();
            if (next < 0 || headers.Count > 65536) throw new Exception("Invalid HTTP request");
            headers.Add((byte)next);
            int n = headers.Count;
            if (n >= 4 && headers[n - 4] == 13 && headers[n - 3] == 10 && headers[n - 2] == 13 && headers[n - 1] == 10) break;
        }
        string[] lines = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n");
        Dictionary<string, string> fields = lines.Skip(1).Where(line => line.Contains(':')).ToDictionary(line => line.Substring(0, line.IndexOf(':')), line => line.Substring(line.IndexOf(':') + 1).Trim(), StringComparer.OrdinalIgnoreCase);
        Check(lines[0] == "POST /v1/chat/completions HTTP/1.1", operation + " stays on the gateway route");
        Check(fields["Authorization"] == "Bearer " + key && !fields.ContainsKey("x-api-key") && !fields.ContainsKey("anthropic-version"), operation + " uses gateway Bearer authentication");
        byte[] body = new byte[int.Parse(fields["Content-Length"])];
        await stream.ReadExactlyAsync(body, timeout.Token);
        using JsonDocument json = JsonDocument.Parse(body);
        inspect(json.RootElement);
        byte[] reply = Encoding.UTF8.GetBytes(response);
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Type: application/json\r\nContent-Length: " + reply.Length + "\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, timeout.Token);
        await stream.WriteAsync(reply, timeout.Token);
    });
    object instance = Activator.CreateInstance(brain, true);
    brain.GetField("Remote", members).SetValue(instance, Remote(key, model));
    brain.GetField("Model", members).SetValue(instance, model);
    Exception failure = null;
    try
    {
        if (operation == "vision") Call(brain, instance, "See", new byte[] { 1, 2, 3 }, "Describe this test image.", "What is shown?", 100);
        else if (operation == "tools")
        {
            IList calls = (IList)Activator.CreateInstance(callsType);
            Call(brain, instance, "Step", "Choose a tool.", "Open Calculator.", null, null, "", calls);
            Check(calls.Count == 1, "gateway tool responses are parsed without executing actions");
        }
        else Call(brain, instance, "Ask", "Hello.");
    }
    catch (Exception error) { failure = error; }
    await server.WaitAsync(timeout.Token);
    if (expectedError == null && failure != null) throw failure;
    if (expectedError != null) Check(failure != null && failure.Message.Contains(expectedError) && !failure.Message.Contains(key), operation + " gives a useful error without exposing the key");
}

const string answer = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hello.\"},\"finish_reason\":\"stop\"}]}";
await Request(gatewayDefault, "default model", 200, answer, body =>
{
    Check(!body.TryGetProperty("model", out _), "gateway default omits the model instead of guessing an ID");
    Check(body.GetProperty("max_tokens").GetInt32() == 2000 && !body.TryGetProperty("reasoning_effort", out _) && !body.TryGetProperty("thinking", out _), "gateway receives standard chat parameters");
});
await Request("work/claude-model", "custom model", 200, answer, body => Check(body.GetProperty("model").GetString() == "work/claude-model", "explicit model IDs are preserved"));
await Request("work/claude-model", "vision", 200, answer, body => Check(body.GetProperty("messages")[1].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString() == "data:image/jpeg;base64,AQID", "vision retains the OpenAI-compatible image format"));
await Request("work/claude-model", "tools", 200, "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"test\",\"type\":\"function\",\"function\":{\"name\":\"open_app\",\"arguments\":\"{\\\"name\\\":\\\"Calculator\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}", body => Check(body.GetProperty("tools").GetArrayLength() > 0, "gateway requests retain tool definitions"));
await Request(gatewayDefault, "expired key", 401, "{\"error\":{\"message\":\"Expired\"}}", body => { }, "Check its expiry");
await Request(gatewayDefault, "usage limit", 429, "{\"error\":{\"message\":\"Limit\"}}", body => { }, "rate or usage limit");
await Request(gatewayDefault, "missing model", 400, "{\"error\":{\"message\":\"model is required\"}}", body => { }, "model is required");
Console.WriteLine(checks + " gateway checks passed. No real credentials or external API calls were used.");
