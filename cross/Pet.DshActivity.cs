using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CapriDesktop
{
    internal sealed partial class Pet
    {
        internal void OpenDshActivity()
        {
            string path = Path.Combine(DshFolder, "dsh", "activity.jsonl");
            Window sheet = DshWindow("Capri · DSH activity", 950, 720);
            DockPanel layout = new DockPanel { Margin = new Thickness(18) };
            StackPanel top = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
            top.Children.Add(new TextBlock { Text = "Local DSH activity · visible replies and tool events", FontSize = 22 });
            top.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap });
            top.Children.Add(new TextBlock { Text = "Enable Record local DSH activity in DSH setup before reproducing a problem. No images or reasoning are recorded. Logs may contain private file/screen text; review before sharing.", TextWrapping = TextWrapping.Wrap });
            TextBox content = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Look.Mono, FontSize = 14, Foreground = MainframeTheme.White, Background = MainframeTheme.Field };
            void Refresh()
            {
                try
                {
                    if (!File.Exists(path)) { content.Text = "No activity log yet. Enable logging and run one test, or use Check DSH driver permissions."; return; }
                    using FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (file.Length > 250000) { file.Seek(-250000, SeekOrigin.End); }
                    using StreamReader read = new StreamReader(file);
                    string text = read.ReadToEnd();
                    content.Text = Brain.Redacted(text);
                }
                catch (Exception error) { content.Text = error.Message; }
            }
            Button refresh = DshButton("Refresh activity");
            refresh.Click += delegate { Refresh(); };
            top.Children.Add(refresh);
            DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top); layout.Children.Add(content);
            sheet.Content = layout; Refresh(); sheet.Show();
        }
    }
}
