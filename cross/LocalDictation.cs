using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace CapriDesktop
{
    internal sealed class LocalDictation : IDisposable
    {
        internal const long ModelSize = 147951465;
        internal const string ModelSha = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";
        internal const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-base.bin";
        internal static Func<ProcessStartInfo, Process> Spawn = Process.Start;
        private readonly string folder;
        private readonly object gate = new object();
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private Process recorder;
        private bool released;
        internal string InputDevice;

        internal static ProcessStartInfo Recorder(string helper, string audio, string device)
        {
            ProcessStartInfo start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(audio);
            if (!string.IsNullOrEmpty(device))
            {
                if (!MicrophoneInputs.Valid(device)) throw new IOException("Choose a valid microphone input in Capri Mainframe > Settings > Microphone input.");
                start.ArgumentList.Add("--device");
                start.ArgumentList.Add(device);
            }
            return start;
        }

        internal static string SpeechLanguage(string language)
        {
            return Regex.IsMatch(language ?? "", @"\A[a-z]{2,3}\z", RegexOptions.IgnoreCase) ? language.ToLowerInvariant() : Brain.LanguageCode(language);
        }

        internal LocalDictation(string data) { folder = Path.Combine(data, "speech"); }
        internal string Model { get { return Path.Combine(folder, "ggml-base.bin"); } }
        internal bool Installed { get { return File.Exists(Model) && new FileInfo(Model).Length == ModelSize; } }

        internal Task PrepareAsync(Action<string> status)
        {
            return Task.Run(delegate
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (Installed && Engine.Hash(Model) == ModelSha) return;
                Directory.CreateDirectory(folder);
                Engine.Fetch(ModelUrl, Model, ModelSize, ModelSha, "local speech", delegate(string text, long done, long total)
                {
                    if (status != null) status(text + " (" + Engine.Amount(done) + " of " + Engine.Amount(total) + ")");
                }, delegate { return cancel.IsCancellationRequested; });
                cancel.Token.ThrowIfCancellationRequested();
            }, cancel.Token);
        }

        internal void Release()
        {
            lock (gate)
            {
                released = true;
                if (recorder == null) return;
                try { recorder.StandardInput.WriteLine("stop"); recorder.StandardInput.Flush(); }
                catch (IOException) { }
                catch (InvalidOperationException) { }
            }
        }

        internal async Task<string> CaptureAsync(string language, Action<string> status)
        {
            cancel.Token.ThrowIfCancellationRequested();
            lock (gate) if (released) return "";
            await PrepareAsync(status);
            lock (gate) if (released) return "";
            string temporary = Path.Combine(Path.GetTempPath(), "capri-mic-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string audio = Path.Combine(temporary, "speech.wav");
            Process process = null;
            bool listening = false;
            try
            {
                string helper = Path.Combine(AppContext.BaseDirectory, "capri-mic");
                ProcessStartInfo start = Recorder(helper, audio, InputDevice);
                lock (gate)
                {
                    if (released) return "";
                    process = Spawn(start);
                    if (process == null) throw new IOException("Couldn't start the microphone.");
                    recorder = process;
                }
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(45));
                    Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
                    string ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    listening = ready != null && (ready == "READY" || ready.StartsWith("READY\t", StringComparison.Ordinal));
                    if (listening && status != null)
                        status("Listening on " + (ready.Length > 6 ? ready.Substring(6) : "this Mac") + ". Release TALK when done (30 seconds maximum).");
                    await process.WaitForExitAsync(timeout.Token);
                    string problem = (await error).Trim();
                    if (process.ExitCode != 0) throw new IOException(problem.Length > 0 ? problem : "Couldn't record. Check Microphone access for Capri in System Settings.");
                }
                lock (gate) recorder = null;
                cancel.Token.ThrowIfCancellationRequested();
                if (!listening) throw new IOException("Recording ended before the microphone was ready. Hold TALK until Listening appears, speak, then release.");
                if (!File.Exists(audio)) throw new IOException("No audio arrived from the microphone. Check Microphone input in Capri Mainframe > Settings.");
                int sampleRate;
                short[] captured = Voice.Samples(await File.ReadAllBytesAsync(audio, cancel.Token), out sampleRate);
                if (captured.Length == 0) throw new IOException("No audio arrived from the microphone. Reconnect it or choose another Microphone input in Capri Mainframe > Settings.");
                if (captured.Length < 16000 / 3) throw new IOException("The recording was too short. Hold TALK until Listening appears, speak, then release.");
                if (!Audible(captured)) throw new IOException("No microphone signal was detected. Choose your input in Capri Mainframe > Settings > Microphone input and check its input level in macOS Sound settings.");
                if (status != null) status("Transcribing on this Mac...");
                return await TranscribeAsync(audio, language, cancel.Token);
            }
            finally
            {
                lock (gate) recorder = null;
                if (process != null)
                {
                    try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                    catch (InvalidOperationException) { }
                    process.Dispose();
                }
                try { Directory.Delete(temporary, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal async Task<string> TranscribeAsync(string audio, string language, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int rate;
            short[] samples = Voice.Samples(await File.ReadAllBytesAsync(audio, token), out rate);
            if (rate != 16000) throw new IOException("Local speech needs a 16 kHz microphone recording.");
            if (samples.Length < rate / 3 || !Audible(samples)) return "";
            using (WhisperFactory factory = WhisperFactory.FromPath(Model))
            using (WhisperProcessor processor = factory.CreateBuilder().WithLanguage(SpeechLanguage(language)).Build())
            using (FileStream stream = File.OpenRead(audio))
            {
                StringBuilder text = new StringBuilder();
                await foreach (SegmentData segment in processor.ProcessAsync(stream).WithCancellation(token))
                {
                    if (segment.NoSpeechProbability < 0.6f) text.Append(segment.Text).Append(' ');
                }
                token.ThrowIfCancellationRequested();
                return Regex.Replace(Regex.Replace(text.ToString(), @"\[(BLANK_AUDIO|SILENCE|NO_SPEECH)\]", "", RegexOptions.IgnoreCase), @"\s+", " ").Trim();
            }
        }

        private static bool Audible(short[] samples)
        {
            double energy = 0;
            foreach (short sample in samples) energy += (double)sample * sample;
            return energy / samples.Length >= 6400;
        }

        internal void Cancel() { cancel.Cancel(); Release(); }
        public void Dispose() { Cancel(); cancel.Dispose(); }
    }
}
