using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CapriDesktop
{
    internal sealed class DshComputer : IDisposable
    {
        private readonly object gate = new object();
        private readonly Action stop;
        private TaskCompletionSource resumed;
        private bool ended;
        private Window strip, target;
        private HerCursor cursor;
        private TextBlock status, detail;
        private Button pause;
        private string phase = "Preparing desktop tools";
        internal bool Paused { get { lock (gate) return resumed != null; } }

        internal DshComputer(Action stop) { this.stop = stop; }

        internal void TogglePause()
        {
            lock (gate)
            {
                if (ended) return;
                if (resumed == null) resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                else { resumed.TrySetResult(); resumed = null; }
            }
            Dispatcher.UIThread.Post(() => { if (status != null) { status.Text = Paused ? "Paused · no new input" : phase; pause.Content = Paused ? "Resume" : "Pause"; } });
        }

        internal async Task Wait(CancellationToken token)
        {
            while (true)
            {
                Task pending;
                lock (gate) { if (ended) throw new OperationCanceledException(); pending = resumed?.Task; }
                token.ThrowIfCancellationRequested();
                if (pending == null) return;
                await pending.WaitAsync(token);
            }
        }

        internal async Task Announce(string tool, CancellationToken token)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (ended) throw new OperationCanceledException();
                if (strip == null) Build();
                phase = "Waiting for your approval";
                status.Text = Paused ? "Paused · no new input" : phase;
                detail.Text = Label(tool);
            });
            await Wait(token);
        }

        internal async Task Update(JsonElement message, CancellationToken token)
        {
            await Wait(token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (ended) throw new OperationCanceledException();
                if (strip == null) Build();
                string step = message.GetProperty("phase").GetString();
                string tool = message.GetProperty("tool").GetString();
                phase = step == "before" ? Label(tool) : step == "failed" ? "Action failed · no automatic retry" : "Checking the result";
                status.Text = Paused ? "Paused · no new input" : phase;
                detail.Text = step == "before" ? "Exact window · background delivery" : "Capri must observe fresh state before the next input";
                HideTargets();
                if (step == "before" && message.TryGetProperty("target", out JsonElement box) && box.ValueKind == JsonValueKind.Object)
                {
                    double x = box.GetProperty("x").GetDouble(), y = box.GetProperty("y").GetDouble(), w = box.GetProperty("width").GetDouble(), h = box.GetProperty("height").GetDouble();
                    target ??= MakeTarget();
                    target.Position = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
                    target.Width = w; target.Height = h;
                    target.Show();
                    cursor ??= new HerCursor();
                    cursor.Point(new PixelPoint((int)Math.Round(x + w / 2), (int)Math.Round(y + h / 2)));
                    cursor.Tap();
                    detail.Text = box.GetProperty("app").GetString() + " · highlighted target";
                }
            });
            if (message.GetProperty("phase").GetString() == "before" && message.TryGetProperty("target", out JsonElement targetBox) && targetBox.ValueKind == JsonValueKind.Object) await Task.Delay(300, token);
            await Wait(token);
        }

        private void Build()
        {
            strip = new Window { Title = "Capri · Computer use", Width = 610, Height = 126, CanResize = false, Topmost = true, ShowActivated = false, Background = MainframeTheme.Ground, Foreground = MainframeTheme.White, FontFamily = MainframeTheme.Text, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            StackPanel body = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
            status = new TextBlock { Text = phase, FontSize = 20, Foreground = MainframeTheme.Cyan };
            detail = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            body.Children.Add(status); body.Children.Add(detail);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            pause = ControlButton("Pause", MainframeTheme.Cyan);
            Button cancel = ControlButton("Stop", MainframeTheme.Rose);
            pause.Click += delegate { TogglePause(); };
            cancel.Click += delegate { stop(); Dispose(); };
            buttons.Children.Add(pause); buttons.Children.Add(cancel); body.Children.Add(buttons);
            strip.Content = body;
            strip.Closed += delegate { if (!ended) { stop(); Dispose(); } };
            strip.Show();
        }

        private static Button ControlButton(string label, IBrush ink)
        {
            return new Button { Content = label, Foreground = ink, Background = MainframeTheme.Deep, BorderBrush = MainframeTheme.DeepEdge, BorderThickness = new Thickness(1), Padding = new Thickness(12, 6), FontSize = 16 };
        }

        private static Window MakeTarget()
        {
            Window window = new Window();
            Look.Overlay(window, "Capri computer target");
            window.IsHitTestVisible = false;
            window.Content = new Border { BorderBrush = MainframeTheme.Cyan, BorderThickness = new Thickness(2), Background = new SolidColorBrush(Color.FromArgb(26, 0, 239, 239)) };
            window.Opened += delegate { MacOverlay.ClickThrough(window); };
            return window;
        }

        private static string Label(string tool)
        {
            return tool switch { "computer_list_apps" => "Finding apps", "computer_list_windows" => "Finding windows", "computer_get_window_state" => "Observing a window", "computer_click" => "Clicking the highlighted target", "computer_type_text" => "Typing in the highlighted target", "computer_press_key" => "Sending a key", "computer_scroll" => "Scrolling the highlighted target", _ => "Using the computer" };
        }

        private void HideTargets() { cursor?.Park(); if (target?.IsVisible == true) target.Hide(); }

        public void Dispose()
        {
            lock (gate) { if (ended) return; ended = true; resumed?.TrySetCanceled(); resumed = null; }
            Dispatcher.UIThread.Post(() => { HideTargets(); cursor?.Close(); target?.Close(); strip?.Close(); });
        }
    }

    internal static class MacOverlay
    {
        [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Object(IntPtr instance, IntPtr selector);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern void Flag(IntPtr instance, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

        internal static void ClickThrough(Window overlay)
        {
            if (!OperatingSystem.IsMacOS()) return;
            var handle = overlay.TryGetPlatformHandle();
            if (handle == null) throw new InvalidOperationException("Computer overlay has no native window.");
            IntPtr window = handle.HandleDescriptor == "NSWindow" ? handle.Handle : Object(handle.Handle, sel_registerName("window"));
            if (window == IntPtr.Zero) throw new InvalidOperationException("Computer overlay cannot pass mouse clicks through.");
            Flag(window, sel_registerName("setIgnoresMouseEvents:"), true);
        }
    }
}
