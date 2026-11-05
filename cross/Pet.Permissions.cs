using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CapriDesktop
{
    internal sealed partial class Pet
    {
        internal void OpenMacPermissions()
        {
            Window sheet = DshWindow("Capri · Mac permissions", 700, 730);
            StackPanel body = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
            body.Children.Add(new TextBlock { Text = "Mac permissions", FontSize = 24, Foreground = MainframeTheme.Cyan });
            void Explain(string text) { body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }); }
            Explain("These permissions belong to this running preview. Approve macOS prompts yourself. No microphone recording or screen capture starts from this panel.");
            TextBlock status = new TextBlock { Text = MacComputerAccess.Status(), TextWrapping = TextWrapping.Wrap };
            TextBlock microphone = new TextBlock { Text = "Microphone: checking..." };
            body.Children.Add(status); body.Children.Add(microphone);
            TextBlock progress = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = MainframeTheme.Cyan };
            async Task Refresh()
            {
                status.Text = MacComputerAccess.Status();
                try { microphone.Text = "Microphone: " + await MacComputerAccess.Microphone(false); }
                catch (Exception error) { microphone.Text = "Microphone: " + error.Message; }
            }
            bool busy = false;
            Button Action(string text, Func<Task> run)
            {
                Button button = DshButton(text);
                button.Click += async delegate
                {
                    if (busy) return;
                    busy = true; button.IsEnabled = false;
                    try { progress.Text = "Complete any macOS prompts, then return here."; await run(); await Refresh(); progress.Text = "Refresh status after changing settings. Restart Capri after granting screen or Accessibility access."; }
                    catch (Exception error) { progress.Text = error.Message; }
                    finally { busy = false; button.IsEnabled = true; }
                };
                body.Children.Add(button);
                return button;
            }
            Action("Request required permissions", async () =>
            {
                MacComputerAccess.AccessibilityTrusted(true);
                MacComputerAccess.RequestScreen();
                string mic = await MacComputerAccess.Microphone(true);
                if (!MacComputerAccess.AccessibilityTrusted(false)) MacComputerAccess.OpenPrivacy("Accessibility");
                else if (!MacComputerAccess.ScreenAllowed()) MacComputerAccess.OpenPrivacy("ScreenCapture");
                else if (mic != "allowed") MacComputerAccess.OpenPrivacy("Microphone");
            });
            Action("Request Accessibility", () => { MacComputerAccess.AccessibilityTrusted(true); MacComputerAccess.OpenPrivacy("Accessibility"); return Task.CompletedTask; });
            Action("Request Screen Recording", () => { MacComputerAccess.RequestScreen(); MacComputerAccess.OpenPrivacy("ScreenCapture"); return Task.CompletedTask; });
            Action("Request Microphone", async () => { await MacComputerAccess.Microphone(true); MacComputerAccess.OpenPrivacy("Microphone"); });
            Explain("Automation is granted separately for each target app when Capri first controls it. macOS has no single request-all Automation prompt. Review its per-app switches here:");
            Action("Open Automation settings", () => { MacComputerAccess.OpenPrivacy("Automation"); return Task.CompletedTask; });
            Explain("Files & Folders access is granted when an operation needs a protected location. DSH file tools stay inside your selected workspace. Full Disk Access is not required for the desktop test.");
            Action("Open Files & Folders settings", () => { MacComputerAccess.OpenPrivacy("FilesAndFolders"); return Task.CompletedTask; });
            Explain("Permission resets: Capri no longer automatically resets screen grants. Ad-hoc signed test rebuilds can still invalidate macOS grants. Keep using the same preview path; a stable signing certificate is needed for reliable grants across changed builds.");
            Action("Refresh permission status", Refresh);
            Explain("Check the actual DSH child process without capturing the screen or prompting. This saves a local permission report, so you can compare its grants with Capri's.");
            TextBox driver = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, Foreground = MainframeTheme.White, Background = MainframeTheme.Field };
            Action("Check DSH driver permissions", async () => { driver.Text = "Checking..."; driver.Text = await MacComputerAccess.DriverPermissions(DshOptions.Read(DshFolder), System.IO.Path.Combine(DshFolder, "dsh")); });
            body.Children.Add(driver);
            Action("View DSH activity", () => { OpenDshActivity(); return Task.CompletedTask; });
            body.Children.Add(progress);
            sheet.Content = new ScrollViewer { Content = body };
            sheet.Opened += async delegate { await Refresh(); };
            sheet.Activated += async delegate { await Refresh(); };
            sheet.Show();
        }
    }
}
