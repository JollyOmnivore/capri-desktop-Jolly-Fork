using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CapriDesktop
{
    internal sealed partial class Pet
    {
        private MicrophoneInput[] microphones = Array.Empty<MicrophoneInput>();
        private int microphoneRequest;
        internal Func<Task<MicrophoneInput[]>> MicrophoneList = MicrophoneInputs.ReadAsync;
        internal bool MicrophoneLoading { get; private set; }
        internal string MicrophoneProblem { get; private set; }
        internal string MicrophoneId { get { return Saved.MicrophoneInput ?? ""; } }
        internal MicrophoneInput[] MicrophoneChoices
        {
            get
            {
                string name = "System default";
                foreach (var input in microphones) if (input.Default) name += " · " + input.Name;
                var choices = new List<MicrophoneInput> { new MicrophoneInput("", name) };
                choices.AddRange(microphones);
                if (MicrophoneId.Length > 0 && !Array.Exists(microphones, input => input.Id == MicrophoneId))
                    choices.Add(new MicrophoneInput(MicrophoneId, "Selected microphone (not connected)"));
                return choices.ToArray();
            }
        }
        internal void SetMicrophoneInput(string id)
        {
            id = id ?? "";
            if (id.Length > 0 && !MicrophoneInputs.Valid(id)) return;
            if (MicrophoneId != id) CancelPushToTalk();
            Saved.MicrophoneInput = id;
            MicrophoneProblem = null;
            Save();
        }
        internal async void RefreshMicrophones()
        {
            int ticket = ++microphoneRequest;
            MicrophoneLoading = true;
            MicrophoneProblem = null;
            NotifySettings();
            MicrophoneInput[] found = null;
            string problem = null;
            try { found = await MicrophoneList(); }
            catch (Exception) { problem = "Couldn't list microphones. Reconnect your input and try Refresh devices."; }
            Dispatcher.UIThread.Post(() =>
            {
                if (ticket != microphoneRequest || quitting) return;
                MicrophoneLoading = false;
                microphones = found ?? Array.Empty<MicrophoneInput>();
                MicrophoneProblem = problem ?? (microphones.Length == 0 ? "No microphone input is connected." : null);
                NotifySettings();
            });
        }
        internal void OpenMicrophone()
        {
            OpenMainframe();
            mainframe.OpenMicrophone();
        }
    }
}
