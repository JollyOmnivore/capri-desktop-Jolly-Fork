using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CapriDesktop;
using System.Text.Json;

internal sealed class DshTestApp : Application
{
    private Pet pet;
    private IClassicDesktopStyleApplicationLifetime desktop;
    private readonly List<string> checks = new();
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted()
    {
        desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        pet = new Pet(desktop); pet.SetSpeak(false); pet.SetFollowUps(false); pet.Show();
        DispatcherTimer.RunOnce(async () => await Run(), TimeSpan.FromMilliseconds(400));
        base.OnFrameworkInitializationCompleted();
    }
    private void Check(bool good, string label) { if (!good) throw new Exception(label); checks.Add("PASS " + label); Console.WriteLine(checks[^1]); }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private async Task<Window> Approval()
    {
        for (int i = 0; i < 100; i++)
        {
            Window window = desktop.Windows.FirstOrDefault(w => w.Title == "Capri · Allow DSH operation?");
            if (window != null) return window;
            await Task.Delay(20);
        }
        throw new Exception("Missing approval window");
    }
    private async Task Run()
    {
        string failure = null;
        try
        {
            Check(!pet.Saved.Dsh, "DSH is off by default");
            pet.OpenDsh();
            Window settings = desktop.Windows.Single(w => w.Title == "Capri · Experimental DSH");
            await Task.Delay(80);
            Check(settings.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked == false, "backend menu opens a disabled experimental option");
            Check(settings.GetVisualDescendants().OfType<TextBlock>().Any(x => (x.Text ?? "").Contains("Shell commands can read other files")), "backend setup explains shell read and network access");
            using (RenderTargetBitmap image = new RenderTargetBitmap(new PixelSize(640, 740))) { image.Render((Control)settings.Content); image.Save(Path.Combine(Entry.Folder, "dsh-settings.png")); }
            Click(settings.GetVisualDescendants().OfType<Button>().Single(x => (string)x.Content == "Save backend settings"));
            Check(File.Exists(Path.Combine(Entry.Folder, "dsh.json")) && !pet.Saved.Dsh, "disabled backend settings save only to the isolated test folder");
            Task<bool> denied = pet.ApproveDsh("write", "{\"file_path\":\"test.txt\",\"content\":\"exact content\"}", "/test/workspace", CancellationToken.None);
            Window prompt = await Approval();
            Check(prompt.GetVisualDescendants().OfType<TextBox>().Single().Text.Contains("exact content"), "approval displays the exact proposed file contents");
            Check(pet.Talk.Activity.Label == "Waiting for permission", "reply status identifies pending permission");
            Click(prompt.GetVisualDescendants().OfType<Button>().Single(x => (string)x.Content == "Deny"));
            Check(!await denied, "Deny refuses the operation");
            Task<bool> allowed = pet.ApproveDsh("bash", "{\"command\":\"printf hello\"}", "/test/workspace", CancellationToken.None);
            prompt = await Approval();
            Check(prompt.GetVisualDescendants().OfType<TextBox>().Single().Text.Contains("printf hello"), "shell approval displays the exact command");
            Click(prompt.GetVisualDescendants().OfType<Button>().Single(x => (string)x.Content == "Allow once"));
            Check(await allowed, "Allow once approves only the pending operation");
            Task<bool> dismissed = pet.ApproveDsh("read", "{\"file_path\":\"test.txt\"}", "/test/workspace", CancellationToken.None);
            (await Approval()).Close();
            Check(!await dismissed, "closing permission window fails closed");
            using CancellationTokenSource stop = new CancellationTokenSource();
            Task<bool> canceled = pet.ApproveDsh("bash", "{\"command\":\"printf hello\"}", "/test/workspace", stop.Token);
            await Approval(); stop.Cancel();
            Check(!await canceled, "canceling a turn withdraws its pending permission");
            pet.PickBrain("qwen3.5:9b");
            DshOptions options = new DshOptions { Node = "/usr/local/bin/node", Runtime = Path.GetFullPath("out/dsh-runtime"), Workspace = Path.Combine(Entry.Folder, "workspace") };
            File.WriteAllText(Path.Combine(Entry.Folder, "dsh.json"), JsonSerializer.Serialize(options));
            pet.OpenMainframe();
            var frame = pet.Frame;
            Check(frame.Row("DSH backend").IsVisible, "DSH toggle is visible on the Mainframe main page");
            frame.Change("DSH backend", 1);
            Check(pet.Saved.Dsh && ((NeonSwitch)frame.Row("DSH backend")).Checked, "Mainframe toggle enables the configured local DSH backend");
            frame.SavePicture(Path.Combine(Entry.Folder, "dsh-mainframe.png"));
            Check(frame.Overlap() == null && frame.Outside() == null, "Mainframe DSH controls fit without overlapping brain and voice rows");
            frame.Change("DSH backend", 0);
            Check(!pet.Saved.Dsh && !((NeonSwitch)frame.Row("DSH backend")).Checked, "Mainframe toggle restores the original engine");
            frame.Press("DSH setup");
            Check(desktop.Windows.Any(w => w.Title == "Capri · Experimental DSH"), "Mainframe setup button opens DSH configuration");
            desktop.Windows.Single(w => w.Title == "Capri · Experimental DSH").Close();
            options.Runtime = Path.Combine(Entry.Folder, "missing-runtime");
            File.WriteAllText(Path.Combine(Entry.Folder, "dsh.json"), JsonSerializer.Serialize(options));
            frame.Change("DSH backend", 1);
            Check(!pet.Saved.Dsh && !((NeonSwitch)frame.Row("DSH backend")).Checked && pet.DshProblem != null, "missing runtime leaves Mainframe toggle off and shows a setup error");
            desktop.Windows.Single(w => w.Title == "Capri · Experimental DSH").Close();
        }
        catch (Exception error) { failure = error.ToString(); checks.Add("FAIL " + failure); Console.Error.WriteLine(failure); }
        File.WriteAllLines(Path.Combine(Entry.Folder, "checks.txt"), checks);
        pet.Finish(failure == null ? 0 : 1);
    }
}
