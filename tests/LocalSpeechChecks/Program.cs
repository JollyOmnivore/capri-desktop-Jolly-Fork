using System.Diagnostics;
using System.Reflection;

if (args.Length == 3 && args[0] == "--fake-recorder")
{
    Console.WriteLine("READY");
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
    ProcessStartInfo child = new ProcessStartInfo(Environment.ProcessPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    child.ArgumentList.Add("--fake-recorder");
    child.ArgumentList.Add(recording);
    child.ArgumentList.Add(sample);
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
        TaskCompletionSource ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> capture = Capture(session, text => { if (text.StartsWith("Listening")) ready.TrySetResult(); });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Call(session, "Release");
        string text = await capture.WaitAsync(TimeSpan.FromSeconds(60));
        Check(text.Contains("country", StringComparison.OrdinalIgnoreCase) && spawned == 1, "release stops the fake recorder and transcribes its local fixture");
        Check(!Directory.Exists(Path.GetDirectoryName(recording)), "recording and private temporary folder are deleted after success");
    }
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
