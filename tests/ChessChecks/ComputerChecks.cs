using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CapriDesktop;

internal sealed class ComputerTestApp : Application
{
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr Obj(IntPtr instance, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool Bool(IntPtr instance, IntPtr selector);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public double X, Y, Width, Height; }
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern NativeRect Frame(IntPtr instance, IntPtr selector);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern uint CGMainDisplayID();
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern NativeRect CGDisplayBounds(uint display);
    private int count;
    private void Check(bool yes, string label) { if (!yes) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    public override void Initialize() { Styles.Add(new FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        Dispatcher.UIThread.Post(async () =>
        {
            DshComputer computer = null;
            try
            {
                Check(!new DshOptions().Computer, "computer use defaults off");
                bool trusted = MacComputerAccess.AccessibilityTrusted(false);
                Check(MacComputerAccess.Status().StartsWith("Accessibility: " + (trusted ? "allowed" : "not allowed")), "native Accessibility options work without requesting permissions");
                for (int i = 0; i < 50; i++) CheckTrust(trusted);
                Check(true, "native permission dictionaries remain valid across repeated allocation and release");
                int stops = 0;
                computer = new DshComputer(() => stops++);
                using CancellationTokenSource cancel = new();
                await computer.Announce("computer_click", cancel.Token);
                var strip = desktop.Windows.Single(w => w.Title == "Capri · Computer use");
                Check(strip.IsVisible && strip.Topmost, "visible activity strip accompanies computer use");
                computer.TogglePause();
                Task waiting = computer.Wait(cancel.Token);
                Check(computer.Paused && !waiting.IsCompleted, "pause blocks admission of new input");
                computer.TogglePause(); await waiting;
                Check(!computer.Paused, "resume releases queued operation");
                using JsonDocument message = JsonDocument.Parse("{\"phase\":\"before\",\"tool\":\"computer_click\",\"target\":{\"x\":100,\"y\":200,\"width\":160,\"height\":40,\"app\":\"Test fixture\"}}");
                await computer.Update(message.RootElement, cancel.Token);
                var target = desktop.Windows.Single(w => w.Title == "Capri computer target");
                var cursor = desktop.Windows.Single(w => w.Title == "Capri pointer");
                using (var picture = new RenderTargetBitmap(new PixelSize(610, 126))) { picture.Render((Control)strip.Content); picture.Save(Path.Combine(Entry.Folder, "computer-controls.png")); }
                Check(target.IsVisible && cursor.IsVisible, "highlight and artist cursor are displayed together");
                Check(target.Position == new PixelPoint(100, 200) && target.Width == 160, "target uses supplied desktop bounds");
                foreach (var overlay in new[] { target, cursor })
                {
                    var handle = overlay.TryGetPlatformHandle();
                    IntPtr native = handle.HandleDescriptor == "NSWindow" ? handle.Handle : Obj(handle.Handle, sel_registerName("window"));
                    Check(Bool(native, sel_registerName("ignoresMouseEvents")), overlay.Title + " passes native mouse clicks through");
                }
                Check(cursor.Position == new PixelPoint(90, 130), "Mac cursor tip aligns with the highlighted target on Retina");
                using JsonDocument delivered = JsonDocument.Parse("{\"phase\":\"after\",\"tool\":\"computer_click\"}");
                await computer.Update(delivered.RootElement, cancel.Token);
                Check(cursor.IsVisible && cursor.Position == new PixelPoint(90, 130), "cursor remains at the last attempted point after input");
                Console.WriteLine("SCREEN " + target.Screens.Primary.Bounds + " scale=" + target.RenderScaling);
                using JsonDocument capture = JsonDocument.Parse("{\"phase\":\"before\",\"tool\":\"computer_get_window_state\",\"target\":null}");
                await computer.Update(capture.RootElement, cancel.Token);
                Check(!target.IsVisible && !cursor.IsVisible, "visual overlays hide before observation");
                using JsonDocument observed = JsonDocument.Parse("{\"phase\":\"after\",\"tool\":\"computer_get_window_state\"}");
                await computer.Update(observed.RootElement, cancel.Token);
                Check(cursor.IsVisible && !target.IsVisible && cursor.Position == new PixelPoint(90, 130), "last attempted cursor returns after observation without obscuring the capture");
                using JsonDocument failed = JsonDocument.Parse("{\"phase\":\"failed\",\"tool\":\"computer_click\"}");
                await computer.Update(failed.RootElement, cancel.Token);
                Check(cursor.IsVisible, "failed operation retains the last attempted cursor until turn cleanup");
                foreach (var screen in target.Screens.All)
                {
                    int x = screen.Bounds.X + 120, y = screen.Bounds.Y + 120;
                    using JsonDocument location = JsonDocument.Parse(JsonSerializer.Serialize(new { phase = "before", tool = "computer_click", target = new { x, y, width = 80, height = 40, app = "Disposable monitor fixture" } }));
                    await computer.Update(location.RootElement, cancel.Token);
                    var handle = cursor.TryGetPlatformHandle();
                    IntPtr native = handle.HandleDescriptor == "NSWindow" ? handle.Handle : Obj(handle.Handle, sel_registerName("window"));
                    NativeRect actual = Frame(native, sel_registerName("frame"));
                    double tipX = actual.X + 90, tipY = CGDisplayBounds(CGMainDisplayID()).Height - actual.Y - actual.Height + 90;
                    Console.WriteLine("MONITOR bounds=" + screen.Bounds + " scale=" + screen.Scaling + " expected=" + (x + 40) + "," + (y + 20) + " nativeTip=" + tipX + "," + tipY);
                    Check(Math.Abs(tipX - (x + 40)) <= 2 && Math.Abs(tipY - (y + 20)) <= 2, "native cursor tip aligns on display " + screen.Bounds);
                }
                using JsonDocument queued = JsonDocument.Parse("{\"phase\":\"running\",\"steps\":[{\"title\":\"Inspect fixture\",\"status\":\"done\",\"result\":\"Window verified\"},{\"title\":\"Click fixture\",\"status\":\"running\"}]}");
                await computer.UpdateQueue(queued.RootElement, cancel.Token);
                Check(strip.Height == 380 && strip.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "1. [done] Inspect fixture") && strip.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "2. [running] Click fixture"), "queue displays completed and current steps in the existing computer controls");
                computer.TogglePause();
                Task queueWaiting = computer.UpdateQueue(queued.RootElement, cancel.Token);
                Check(!queueWaiting.IsCompleted, "pause holds the queue transition acknowledgment before the next step");
                computer.TogglePause(); await queueWaiting;
                computer.TogglePause();
                Task stopped = computer.Wait(cancel.Token);
                cancel.Cancel();
                try { await stopped; throw new Exception("Canceled pause resumed input"); } catch (OperationCanceledException) { }
                Check(true, "stop cancellation releases paused wait without input");
                strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Stop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(stops == 1, "Stop cancels active DSH turn");
                await Task.Delay(100);
                Check(!desktop.Windows.Any(w => w.Title.StartsWith("Capri computer") || w.Title == "Capri pointer" || w.Title == "Capri · Computer use"), "Stop cleans all computer windows");
                File.WriteAllText(Path.Combine(Entry.Folder, "computer-checks.txt"), count + " computer UI checks passed");
                desktop.Shutdown(0);
            }
            catch (Exception error) { computer?.Dispose(); Console.Error.WriteLine(error); File.WriteAllText(Path.Combine(Entry.Folder, "computer-checks.txt"), error.ToString()); desktop.Shutdown(1); }
        });
        base.OnFrameworkInitializationCompleted();
    }

    private static void CheckTrust(bool expected)
    {
        if (MacComputerAccess.AccessibilityTrusted(false) != expected) throw new Exception("Unexpected permission change during a read-only check.");
    }
}
