using System.Diagnostics;
using System.Reflection;

if (args.Length == 4 && args[0] == "--fake-pocket")
{
    string text = Console.In.ReadToEnd();
    if (text != "Hello, chewm.") Environment.Exit(2);
    if (args[3] == "slow") Thread.Sleep(30000);
    File.Copy(args[2], args[1]);
    return;
}

Assembly app = Assembly.Load("Capri");
Type pocket = app.GetType("CapriDesktop.PocketVoice", true);
Type voice = app.GetType("CapriDesktop.Voice", true);
const BindingFlags members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
string home = Path.Combine(Path.GetTempPath(), "capri-pocket-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(home);
int checks = 0;
object Call(object target, string method, params object[] values)
{
    try { return pocket.GetMethod(method, members).Invoke(target, values); }
    catch (TargetInvocationException error) { throw error.InnerException ?? error; }
}
object Session(string path) { return Activator.CreateInstance(pocket, members, null, new object[] { path }, null); }
string Property(object target, string name) { return (string)pocket.GetProperty(name, members).GetValue(target); }
void Check(bool result, string message)
{
    if (!result) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}
void Reject(byte[] bytes, string message)
{
    try { Call(null, "Normalize", bytes); }
    catch (Exception error) when (error.GetType().Name == "VoiceException") { Check(true, message); return; }
    throw new Exception(message);
}

try
{
    short[] samples = Enumerable.Range(0, 24000).Select(i => (short)(Math.Sin(i * 0.05) * 3000)).ToArray();
    byte[] wave = (byte[])voice.GetMethod("Wave", members).Invoke(null, new object[] { samples, 24000 });
    string sample = Path.Combine(home, "sample.wav");
    File.WriteAllBytes(sample, wave);
    using IDisposable session = (IDisposable)Session(home);
    string binary = Property(session, "Binary");
    Directory.CreateDirectory(Path.GetDirectoryName(binary));
    File.WriteAllText(binary, "test fixture");
    Check((string)Call(null, "Speaker", "../outside") == "alba", "unrecognized speaker cannot become a path or download URL");
    string tokenWas = Environment.GetEnvironmentVariable("HF_TOKEN");
    Environment.SetEnvironmentVariable("HF_TOKEN", "fake-secret-for-test");
    ProcessStartInfo info;
    try { info = (ProcessStartInfo)Call(session, "StartInfo", "cosette", Path.Combine(home, "with spaces.wav")); }
    finally { Environment.SetEnvironmentVariable("HF_TOKEN", tokenWas); }
    Check(!info.UseShellExecute && info.RedirectStandardInput && info.ArgumentList.Contains("-") && !info.ArgumentList.Contains("Hello, choom."), "speech is sent over stdin without a shell or command-line text");
    Check(!info.Environment.ContainsKey("HF_TOKEN") && info.Environment["HF_HOME"] == Property(session, "Cache") && info.Environment["HF_HUB_DISABLE_TELEMETRY"] == "1", "runtime uses its own model cache without inherited Hugging Face credentials");
    Check(info.ArgumentList.Contains("english_2026-01") && info.ArgumentList.Contains("cosette"), "runtime receives the verified model version and selected voice");
    byte[] normalized = (byte[])Call(null, "Normalize", wave);
    Check(normalized.SequenceEqual(wave), "native PCM16 audio survives normalization unchanged");
    byte[] halfRate = (byte[])voice.GetMethod("Wave", members).Invoke(null, new object[] { samples, 12000 });
    byte[] resampled = (byte[])Call(null, "Normalize", halfRate);
    Check(BitConverter.ToInt32(resampled, 24) == 24000 && Math.Abs(resampled.Length - (44 + samples.Length * 4)) < 10, "other PCM sample rates are normalized for the shared effects pipeline");
    Reject(new byte[44], "invalid WAV is rejected");
    byte[] floatWave = (byte[])wave.Clone();
    floatWave[20] = 3;
    Reject(floatWave, "unexpected floating-point audio is rejected instead of misplayed");
    Reject(wave.Take(60).ToArray(), "truncated audio is rejected");
    string output = null, mode = "normal";
    TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    pocket.GetField("Spawn", members).SetValue(null, new Func<ProcessStartInfo, Process>(start =>
    {
        int index = start.ArgumentList.IndexOf("--output-path");
        output = start.ArgumentList[index + 1];
        ProcessStartInfo child = new ProcessStartInfo(Environment.ProcessPath) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { "--fake-pocket", output, sample, mode }) child.ArgumentList.Add(argument);
        Process process = Process.Start(child);
        started.TrySetResult();
        return process;
    }));
    byte[] spoken = (byte[])Call(session, "Speak", "**Hello, choom.**", "alba");
    Check(spoken.SequenceEqual(wave), "renderer sends cleaned spoken text and reads the generated audio");
    Check(!Directory.Exists(Path.GetDirectoryName(output)), "temporary speech audio is deleted after generation");
    mode = "slow";
    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task waiting = Task.Run(() => Call(session, "Speak", "Hello, choom.", "alba"));
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Call(session, "Stop");
    bool stopped = false;
    try { await waiting.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) { stopped = true; }
    Check(stopped && !Directory.Exists(Path.GetDirectoryName(output)), "stopping speech terminates generation and removes temporary audio");
    pocket.GetField("Spawn", members).SetValue(null, new Func<ProcessStartInfo, Process>(Process.Start));
    if (args.Length == 2 && args[0] == "--live")
    {
        using IDisposable local = (IDisposable)Session(Path.GetFullPath(args[1]));
        byte[] actual = (byte[])Call(local, "Speak", "Hello, choom. This voice runs locally in Capri.", "alba");
        double seconds = (double)voice.GetMethod("Seconds", members).Invoke(null, new object[] { actual });
        Check(seconds > 1 && BitConverter.ToInt32(actual, 24) == 24000, "official runtime installs and generates real local speech through Capri");
        File.WriteAllBytes(Path.Combine(Path.GetFullPath(args[1]), "sample.wav"), actual);
    }
    Console.WriteLine(checks + " Pocket TTS checks passed.");
}
finally
{
    pocket.GetField("Spawn", members).SetValue(null, new Func<ProcessStartInfo, Process>(Process.Start));
    Directory.Delete(home, true);
}
