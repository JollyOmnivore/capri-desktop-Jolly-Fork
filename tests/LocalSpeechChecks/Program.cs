using System.Diagnostics;
using System.Reflection;

if (args.Length == 3 && args[0] == "--fake-recorder")
{
    Console.WriteLine("READY\tFixture USB microphone");
    Console.Out.Flush();
    Console.ReadLine();
    File.Copy(args[2], args[1]);
    return;
}
if (args.Length != 2) throw new Exception("Supply the pinned ggml-base.bin and Whisper's public jfk.wav sample.");
string model = Path.GetFullPath(args[0]);
string sample = Path.GetFullPath(args[1]);
Assembly app = Assembly.Load("Capri");
Type speech = app.GetType("CapriDesktop.LocalDictation", true);
Type voice = app.GetType("CapriDesktop.Voice", true);
const BindingFlags members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
int checks = 0, spawned = 0;
string recording = null;
string expectedDevice = null, fixture = sample;
string home = Path.Combine(Path.GetTempPath(), "capri-speech-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(home, "speech"));
File.Copy(model, Path.Combine(home, "speech", "ggml-base.bin"));

void Check(bool passes, string name)
{
    if (!passes) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
object Call(object target, string name, params object[] values)
{
    try { return speech.GetMethod(name, members).Invoke(target, values); }
    catch (TargetInvocationException error) { throw error.InnerException ?? error; }
}
object Session() { return Activator.CreateInstance(speech, members, null, new object[] { home }, null); }
Task<string> Capture(object session, Action<string> status) { return (Task<string>)Call(session, "CaptureAsync", "en", status); }
Task<string> Transcribe(object session, string path) { return (Task<string>)Call(session, "TranscribeAsync", path, "en", CancellationToken.None); }

speech.GetField("Spawn", members).SetValue(null, new Func<ProcessStartInfo, Process>(info =>
{
    spawned++;
    recording = info.ArgumentList[0];
    Check(expectedDevice == null ? info.ArgumentList.Count == 1 : info.ArgumentList.Count == 3 && info.ArgumentList[1] == "--device" && info.ArgumentList[2] == expectedDevice, "recording passes the selected device as one argument without changing the system default");
    ProcessStartInfo child = new ProcessStartInfo(Environment.ProcessPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    child.ArgumentList.Add("--fake-recorder");
    child.ArgumentList.Add(recording);
    child.ArgumentList.Add(fixture);
    return Process.Start(child);
}));

try
{
    Check(new FileInfo(model).Length == (long)speech.GetField("ModelSize", members).GetRawConstantValue(), "model has the pinned expected size");
    using (System.Security.Cryptography.SHA256 hash = System.Security.Cryptography.SHA256.Create())
    using (FileStream stream = File.OpenRead(model)) Check(Convert.ToHexString(hash.ComputeHash(stream)).Equals((string)speech.GetField("ModelSha", members).GetRawConstantValue(), StringComparison.OrdinalIgnoreCase), "model matches the pinned SHA-256");
    string helper = Path.Combine(AppContext.BaseDirectory, "capri-mic");
    using (Process probe = Process.Start(new ProcessStartInfo(helper, "--check") { RedirectStandardOutput = true, UseShellExecute = false }))
    {
        string output = await probe.StandardOutput.ReadToEndAsync();
        await probe.WaitForExitAsync();
        Check(probe.ExitCode == 0 && output.Contains("capri-mic ready"), "packaged microphone helper starts without recording");
    }
    using (Process probe = Process.Start(new ProcessStartInfo(helper, "--self-test") { RedirectStandardOutput = true, UseShellExecute = false }))
    {
        string output = await probe.StandardOutput.ReadToEndAsync();
        await probe.WaitForExitAsync();
        Check(probe.ExitCode == 0 && output.Contains("PCM ready"), "device recorder writes valid 16 kHz mono PCM WAV without opening a microphone");
    }
    Type inputs = app.GetType("CapriDesktop.MicrophoneInputs", true);
    Array parsed = (Array)inputs.GetMethod("Parse", members).Invoke(null, new object[] { "[{\"id\":\"usb mic\",\"name\":\"USB Headset\",\"isDefault\":true},{\"id\":\"usb mic\",\"name\":\"Duplicate\"},{\"id\":\"\",\"name\":\"Invalid\"},{\"id\":\"bad\\nuid\",\"name\":\"Invalid\"}]" });
    Check(parsed.Length == 1, "device enumeration rejects duplicate and invalid device IDs");
    ProcessStartInfo command = (ProcessStartInfo)Call(null, "Recorder", "helper", "private.wav", "usb ' microphone ; $(not-a-command)");
    Check(!command.UseShellExecute && command.ArgumentList.Count == 3 && command.ArgumentList[2] == "usb ' microphone ; $(not-a-command)", "unusual device UIDs remain data instead of shell commands");
    Check((string)Call(null, "SpeechLanguage", "Spanish") == "es" && (string)Call(null, "SpeechLanguage", "fr") == "fr" && (string)Call(null, "SpeechLanguage", "English") == "en", "Capri language names and language codes both reach Whisper correctly");
    using (IDisposable session = (IDisposable)Session())
    {
        string text = await Transcribe(session, sample);
        Check(text.Contains("country", StringComparison.OrdinalIgnoreCase) && text.Contains("ask", StringComparison.OrdinalIgnoreCase), "real Whisper inference recognizes the public speech sample locally");
        string silence = Path.Combine(home, "silence.wav");
        File.WriteAllBytes(silence, (byte[])voice.GetMethod("Wave", members, null, new[] { typeof(short[]), typeof(int) }, null).Invoke(null, new object[] { new short[16000], 16000 }));
        Check(await Transcribe(session, silence) == "", "silent audio produces no hallucinated message or action");
    }
    using (IDisposable session = (IDisposable)Session())
    {
        Call(session, "Release");
        Check(await Capture(session, null) == "" && spawned == 0, "release before readiness never starts the microphone");
    }
    using (IDisposable session = (IDisposable)Session())
    {
        Call(session, "Cancel");
        bool cancelled = false;
        try { await Capture(session, null); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && spawned == 0, "cancel before readiness never starts the microphone");
    }
    using (IDisposable session = (IDisposable)Session())
    {
        expectedDevice = "usb fixture microphone";
        speech.GetField("InputDevice", members).SetValue(session, expectedDevice);
        bool named = false;
        TaskCompletionSource ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> capture = Capture(session, text => { if (text.StartsWith("Listening")) { named = text.Contains("Fixture USB microphone"); ready.TrySetResult(); } });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Call(session, "Release");
        string text = await capture.WaitAsync(TimeSpan.FromSeconds(60));
        Check(text.Contains("country", StringComparison.OrdinalIgnoreCase) && spawned == 1, "release stops the fake recorder and transcribes its local fixture");
        Check(named, "Listening status names the input that the native recorder opened");
        Check(!Directory.Exists(Path.GetDirectoryName(recording)), "recording and private temporary folder are deleted after success");
    }
    expectedDevice = null;
    using (IDisposable session = (IDisposable)Session())
    {
        fixture = Path.Combine(home, "quiet.wav");
        File.WriteAllBytes(fixture, (byte[])voice.GetMethod("Wave", members, null, new[] { typeof(short[]), typeof(int) }, null).Invoke(null, new object[] { new short[16000], 16000 }));
        TaskCompletionSource ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> capture = Capture(session, text => { if (text.StartsWith("Listening")) ready.TrySetResult(); });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Call(session, "Release");
        bool silent = false;
        try { await capture; } catch (IOException e) { silent = e.Message.Contains("No microphone signal"); }
        Check(silent && !Directory.Exists(Path.GetDirectoryName(recording)), "silent input gives a device-selection diagnostic and deletes its recording");
    }
    fixture = sample;
    using (IDisposable session = (IDisposable)Session())
    {
        TaskCompletionSource ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> capture = Capture(session, text => { if (text.StartsWith("Listening")) ready.TrySetResult(); });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Call(session, "Cancel");
        bool cancelled = false;
        try { await capture.WaitAsync(TimeSpan.FromSeconds(15)); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !Directory.Exists(Path.GetDirectoryName(recording)), "cancellation stops the fake recorder and deletes its recording");
    }
    Console.WriteLine(checks + " local speech checks passed. No microphone audio was captured or uploaded.");
}
finally { Directory.Delete(home, true); }
