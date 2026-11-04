using System;

namespace CapriDesktop
{
    internal sealed class ReplyActivity
    {
        internal string Label { get; private set; } = "Reply complete";
        internal bool Active { get; private set; }
        internal int Tools { get; private set; }
        private string outcome;
        private bool voicePending;
        internal void Begin() { Tools = 0; outcome = null; voicePending = false; Label = "Thinking"; Active = true; }
        internal void Tool(string tool, string result)
        {
            if (result == null)
            {
                Tools++; Active = true;
                Label = tool == "bash" ? "Running a command" : tool == "read" ? "Reading a file" : tool == "write" || tool == "edit" ? "Updating a file" : tool == "web_search" ? "Searching the web" : tool == "open_website" ? "Opening a page" : tool == "look_at_screen" ? "Looking at your screen" : tool == "open_app" ? "Opening an app" : tool == "mini_game" ? "Updating the game" : tool == "capri_settings" ? "Updating settings" : "Running an action";
            }
            else
            {
                if (Security.IsHold(result)) outcome = "Waiting for permission";
                else if (Hands.Failed(result)) outcome = "Action failed";
                Label = outcome ?? "Action finished · writing reply";
                Active = outcome == null;
            }
        }
        internal void Voice() { voicePending = true; Label = "Preparing voice"; Active = true; }
        internal void AwaitApproval() { Label = "Waiting for permission"; Active = true; }
        internal void ResumeTool() { Label = "Running an action"; Active = true; }
        internal void VoiceReady() { voicePending = false; }
        internal void Replying() { Label = outcome ?? (voicePending ? "Replying · preparing voice" : "Replying"); Active = outcome == null; }
        internal void Complete() { Label = outcome ?? (voicePending ? "Preparing voice" : Tools > 0 ? "Action finished" : "Reply complete"); Active = outcome == null && voicePending; }
        internal void Fail() { outcome = Label = "Couldn't finish"; Active = false; }
        internal void NoAction() { outcome = Label = "No action started"; Active = false; }
        internal void Cancel() { voicePending = false; outcome = Label = "Canceled"; Active = false; }
    }
}
