using System;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CapriDesktop
{
    internal sealed class PocketVoice : IDisposable, ISpeaker
    {
        internal const string Build = "4.0.1";
        internal const string Language = "english_2026-01";
        internal static readonly string[] Voices = { "alba", "cosette", "fantine", "azelma", "eponine", "jean", "javert", "marius" };
        internal static Func<ProcessStartInfo, Process> Spawn = Process.Start;
        internal string Selected;
        public string Name { get { return "Pocket TTS"; } }
        public bool Ready { get { return true; } }
        byte[] ISpeaker.Speak(string text) { return Speak(text, Selected); }
        private readonly string folder;
        private readonly object gate = new object();
        private readonly object rendering = new object();
        private Process active;
        private int generation;

        internal PocketVoice(string data) { folder = Path.Combine(data, "pocket-tts"); }
        internal string Binary { get { return Path.Combine(folder, "runtime-" + Build, "pocket-tts"); } }
        internal string Cache { get { return Path.Combine(folder, "cache"); } }
        internal bool Installed { get { return File.Exists(Binary); } }
        internal static string Speaker(string name) { return Array.IndexOf(Voices, name) >= 0 ? name : "alba"; }

        private void Install(int ticket)
        {
            if (Installed) return;
            bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            string target, sha;
            long size;
            if (OperatingSystem.IsMacOS())
            {
                target = arm ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
                sha = arm ? "ee9ca6c7c000ee72c5846e3a8ba01d08f56636d94a614d2259c3f12b8fd69afa" : "f04baa94d4fc47a689cc9f11ca2b0ba869dc002f9e78e15d387c6ecaeb1d654b";
                size = arm ? 7696220 : 8376592;
            }
            else if (OperatingSystem.IsLinux())
            {
                target = arm ? "aarch64-unknown-linux-gnu" : "x86_64-unknown-linux-gnu";
                sha = arm ? "2b9a727bffd86ea6d73370d675cbd91a7db118f11b7f5a60343c2d5da12b187a" : "9abff0aa138a804e44052952d8c3bdaae247fb267ca8f4b83fe178897d1a0559";
                size = arm ? 8172287 : 8641335;
            }
            else throw new VoiceException("Pocket TTS is available in this Mac and Linux build.");
            string inside = "pocket-tts-3.3.0-" + target;
            Directory.CreateDirectory(Path.GetDirectoryName(Binary));
            string archive = Path.Combine(folder, "runtime-" + Build + ".tar.gz");
            Engine.Fetch("https://github.com/kyutai-labs/pocket-tts/releases/download/v" + Build + "/" + inside + ".tar.gz", archive, size, sha, "Pocket TTS", delegate { }, delegate { return ticket != Volatile.Read(ref generation); });
            string partial = Binary + ".partial";
            try
            {
                using (FileStream input = File.OpenRead(archive))
                using (GZipStream gzip = new GZipStream(input, CompressionMode.Decompress))
                using (TarReader tar = new TarReader(gzip))
                {
                    TarEntry entry;
                    while ((entry = tar.GetNextEntry()) != null)
                    {
                        if (entry.Name != inside + "/pocket-tts" && entry.Name != inside + "/LICENSE") continue;
                        if (entry.DataStream == null || entry.Length > 64L * 1024 * 1024) throw new VoiceException("The Pocket TTS download is invalid.");
                        string output = entry.Name.EndsWith("/LICENSE", StringComparison.Ordinal) ? Path.Combine(Path.GetDirectoryName(Binary), "LICENSE.txt") : partial;
                        using (FileStream file = File.Create(output)) entry.DataStream.CopyTo(file);
                    }
                }
                if (!File.Exists(partial)) throw new VoiceException("The Pocket TTS download is missing its program.");
                if (ticket != Volatile.Read(ref generation)) throw new OperationCanceledException();
                File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.Move(partial, Binary, true);
                File.Delete(archive);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }

        internal ProcessStartInfo StartInfo(string speaker, string output)
        {
            ProcessStartInfo start = new ProcessStartInfo(Binary) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "generate", "--text", "-", "--voice", Speaker(speaker), "--language", Language, "--output-path", output, "--quiet" }) start.ArgumentList.Add(argument);
            start.Environment["HF_HOME"] = Cache;
            start.Environment["HF_ENDPOINT"] = "https://huggingface.co";
            start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            start.Environment["DO_NOT_TRACK"] = "1";
            foreach (string key in new[] { "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "HUGGINGFACE_HUB_TOKEN" }) start.Environment.Remove(key);
            return start;
        }

        internal byte[] Speak(string text, string speaker)
        {
            int ticket = Volatile.Read(ref generation);
            lock (rendering)
            {
                if (ticket != Volatile.Read(ref generation)) throw new OperationCanceledException();
                Install(ticket);
                string spoken = Voice.Sayable(Voice.Spoken(text));
                if (string.IsNullOrWhiteSpace(spoken)) throw new VoiceException("That reply had nothing to say out loud.");
                string temporary = Path.Combine(Path.GetTempPath(), "capri-pocket-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string output = Path.Combine(temporary, "speech.wav");
                Process process = null;
                try
                {
                    lock (gate)
                    {
                        if (ticket != generation) throw new OperationCanceledException();
                        process = Spawn(StartInfo(speaker, output));
                        if (process == null) throw new VoiceException("Couldn't start Pocket TTS.");
                        active = process;
                    }
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    Task<string> logs = process.StandardOutput.ReadToEndAsync();
                    process.StandardInput.Write(spoken);
                    process.StandardInput.Close();
                    if (!process.WaitForExit(180000)) throw new VoiceException("Pocket TTS took too long. Try a shorter reply.");
                    if (ticket != Volatile.Read(ref generation)) throw new OperationCanceledException();
                    Task.WaitAll(errors, logs);
                    if (process.ExitCode != 0 || !File.Exists(output)) throw new VoiceException("Pocket TTS couldn't speak. " + Voice.Short(errors.Result.Trim()));
                    if (new FileInfo(output).Length > 64L * 1024 * 1024) throw new VoiceException("Pocket TTS returned too much audio.");
                    return Normalize(File.ReadAllBytes(output));
                }
                catch (Exception) when (ticket != Volatile.Read(ref generation)) { throw new OperationCanceledException(); }
                finally
                {
                    lock (gate) active = null;
                    if (process != null)
                    {
                        try { if (!process.HasExited) process.Kill(true); }
                        catch (InvalidOperationException) { }
                        process.Dispose();
                    }
                    try { Directory.Delete(temporary, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        internal static byte[] Normalize(byte[] audio)
        {
            if (audio == null || audio.Length < 44 || Encoding.ASCII.GetString(audio, 0, 4) != "RIFF" || Encoding.ASCII.GetString(audio, 8, 4) != "WAVE") throw new VoiceException("Pocket TTS returned unreadable audio.");
            bool pcm16 = false;
            for (int at = 12; at + 8 <= audio.Length;)
            {
                int size = BitConverter.ToInt32(audio, at + 4);
                long next = (long)at + 8 + size + (size & 1);
                if (size < 0 || next > audio.Length) throw new VoiceException("Pocket TTS returned incomplete audio.");
                string chunk = Encoding.ASCII.GetString(audio, at, 4);
                if (chunk == "fmt " && size >= 16) pcm16 = BitConverter.ToUInt16(audio, at + 8) == 1 && BitConverter.ToUInt16(audio, at + 10) == 1 && BitConverter.ToUInt16(audio, at + 22) == 16;
                if (chunk == "data") break;
                at = (int)next;
            }
            if (!pcm16) throw new VoiceException("Pocket TTS must return mono PCM16 audio.");
            int rate;
            short[] pcm = Voice.Samples(audio, out rate);
            if (pcm.Length == 0 || rate < 4000 || rate > 192000) throw new VoiceException("Pocket TTS returned unreadable audio.");
            if (rate != 24000) pcm = VoiceFx.Resample(pcm, rate / 24000.0);
            return Voice.Wave(pcm, 24000);
        }

        internal void Stop()
        {
            lock (gate)
            {
                generation++;
                try { if (active != null && !active.HasExited) active.Kill(true); }
                catch (InvalidOperationException) { }
            }
        }

        public void Dispose() { Stop(); }
    }
}
