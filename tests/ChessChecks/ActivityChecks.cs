using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CapriDesktop;

internal sealed class ActivityTestApp : Application
{
    private Pet pet;
    private readonly List<string> checks = new();
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        pet = new Pet(desktop); pet.SetSpeak(false); pet.SetFollowUps(false); pet.Show();
        DispatcherTimer.RunOnce(async () => await Run(), TimeSpan.FromMilliseconds(400));
        base.OnFrameworkInitializationCompleted();
    }
    private void Check(bool good, string message)
    {
        if (!good) throw new Exception(message);
        checks.Add("PASS " + message); Console.WriteLine(checks[^1]);
    }
    private async Task Wait(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > until) throw new Exception("Activity UI timed out."); await Task.Delay(20); }
    }
    private void Picture(string name, double phase = 1, double scale = 1)
    {
        pet.Talk.Update(pet.Now);
        var sheet = pet.Talk.Bubble.Sheet;
        sheet.Glitch = 0; sheet.Phase = phase;
        sheet.Measure(new Size(pet.Talk.Bubble.Width, pet.Talk.Bubble.Height));
        sheet.Arrange(new Rect(0, 0, pet.Talk.Bubble.Width, pet.Talk.Bubble.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(sheet.Bounds.Width * scale), (int)Math.Ceiling(sheet.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(sheet); bitmap.Save(Path.Combine(Entry.Folder, name));
        Check(sheet.Panel.Height >= Sheet.PanelHeight(sheet.Reply) && sheet.Status.Length > 0, "status footer fits in " + name);
    }
    private async Task Run()
    {
        string error = null;
        using var release = new ManualResetEventSlim();
        try
        {
            var activity = pet.Talk.Activity;
            pet.Talk.Thinking("Find the project website");
            Check(activity.Active && activity.Label == "Thinking" && activity.Tools == 0, "model generation is distinct from a running tool");
            Picture("thinking.png");
            var run = pet.Brain.Observed((tool, args) => { release.Wait(TimeSpan.FromSeconds(5)); return "Found the project website."; }, pet.Brain.Revision);
            var task = Task.Run(() => run("web_search", new ToolArguments { Query = "public fixture" }));
            await Wait(() => activity.Tools == 1);
            Check(activity.Active && activity.Label == "Searching the web", "actual tool execution starts the working animation");
            Picture("searching-a.png", 0); Picture("searching-b.png", .25); Picture("searching-2x.png", .25, 2);
            Check(!File.ReadAllBytes(Path.Combine(Entry.Folder, "searching-a.png")).SequenceEqual(File.ReadAllBytes(Path.Combine(Entry.Folder, "searching-b.png"))), "working status visibly animates between frames");
            release.Set(); await task;
            await Wait(() => activity.Label.Contains("writing reply"));
            Check(activity.Active, "a finished tool stays busy while the answer is being composed");
            pet.Talk.PreparingVoice("Find the project website");
            Check(activity.Active && activity.Label == "Preparing voice" && activity.Tools == 1, "voice generation retains the completed tool count");
            activity.Complete();
            Check(activity.Active && activity.Label == "Preparing voice", "text fallback cannot claim completion while voice generation is still pending");
            pet.Talk.Answer("Find the project website", "Found the project website.");
            pet.Talk.Update(pet.Now + 100); pet.Talk.Update(pet.Now + 200); pet.Talk.Update(pet.Now + 201);
            Check(pet.Talk.Bubble.IsVisible && !pet.Talk.Finished && !pet.Talk.Quiet, "slow voice generation keeps the activity visible and prevents premature follow-ups");
            activity.VoiceReady();
            pet.Talk.Answer("Find the project website", "Found the project website.");
            Check(activity.Active && activity.Label == "Replying", "an answer remains active while text is being revealed");
            await Wait(() => !activity.Active);
            Check(!activity.Active && activity.Label == "Action finished", "finished replies stop animating and report action completion");
            Picture("finished.png");
            activity.Begin(); activity.Tool("open_website", "Couldn't open that site."); activity.Replying(); activity.Complete();
            Check(!activity.Active && activity.Label == "Action failed", "failed tools never show successful completion");
            Picture("failed.png");
            Security.Set(Security.Open, false);
            string held = Security.Gate("open_website", new ToolArguments { Site = "https://example.com" });
            activity.Begin(); activity.Tool("open_website", held); activity.Complete();
            Check(!activity.Active && activity.Label == "Waiting for permission", "permission holds never appear as finished actions");
            Picture("permission.png");
            Security.Pick(Security.Trusted); Security.Drop();
            int revision = pet.Brain.Revision;
            pet.Talk.Thinking("Old request");
            var old = pet.Brain.Observed((tool, args) => "old result", revision);
            pet.Brain.Cancel();
            bool canceled = false;
            try { old("web_search", new ToolArguments()); } catch (System.Net.WebException) { canceled = true; }
            Check(canceled && activity.Tools == 0, "canceled requests cannot start another tool");
            pet.Talk.Dismiss();
            Check(!activity.Active && activity.Label == "Canceled", "dismissing a response clears the working animation");
            using (var late = new ManualResetEventSlim())
            {
                pet.Talk.Thinking("Previous request");
                var stale = pet.Brain.Observed((tool, args) => { late.Wait(TimeSpan.FromSeconds(3)); return "Previous action finished."; }, pet.Brain.Revision);
                var previous = Task.Run(() => stale("web_search", new ToolArguments()));
                await Wait(() => activity.Tools == 1);
                pet.Brain.Cancel(); pet.Talk.Thinking("New request");
                late.Set(); await previous; await Task.Delay(60);
                Check(activity.Tools == 0 && activity.Label == "Thinking", "a late tool result cannot overwrite the new request's status");
            }
            pet.Think = _ => "Hold on, let me do that for you.";
            pet.Hear("hello fixture");
            await Wait(() => pet.Talk.Reply == Hands.Unpromised);
            Check(activity.Label == "No action started" && activity.Tools == 0, "empty action promises are visibly distinguished from executed tools");
            await Wait(() => pet.Talk.Done);
            Picture("no-action.png");
            pet.Think = _ => throw new IOException("fixture failure");
            pet.Hear("hello failure");
            await Wait(() => activity.Label == "Couldn't finish");
            Check(!activity.Active, "model failures stop the working animation");
            Check(Hands.Promised("Hold on, let me do that for you.") && !Hands.WaitingPromise("Hold on, that sounds surprising."), "action-promise detection leaves ordinary conversation alone");
        }
        catch (Exception e) { error = e.ToString(); checks.Add("FAIL " + error); Console.Error.WriteLine(error); }
        release.Set();
        File.WriteAllLines(Path.Combine(Entry.Folder, "checks.txt"), checks);
        pet.Finish(error == null ? 0 : 1);
    }
}
