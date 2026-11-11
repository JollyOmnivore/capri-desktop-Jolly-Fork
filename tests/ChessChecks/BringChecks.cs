using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CapriDesktop;

internal sealed class BringTestApp : Application
{
    private Pet pet;
    public override void Initialize() { Styles.Add(new FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        pet = new Pet(desktop) { Think = _ => "Fixture response." };
        CapriDesktop.Program.RouteQuit(desktop, pet);
        bool nativeQuit = false, lateBring = false, closed = false;
        pet.Closed += (_, _) => closed = true;
        desktop.Exit += (_, e) => { if (nativeQuit && lateBring && e.ApplicationExitCode == 0) Console.WriteLine("PASS native Quit completes clean shutdown and ignores late Bring here"); };
        pet.SetSpeak(false); pet.SetFollowUps(false);
        Sfx.Out = _ => { };
        pet.Show();
        Dispatcher.UIThread.UnhandledException += (_, e) => { File.WriteAllText(Path.Combine(Entry.Folder,"bring-error.txt"),e.Exception.ToString()); Console.Error.WriteLine(e.Exception); e.Handled = true; pet.Finish(1); };
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(300);
                pet.OpenMainframe();
                pet.Talk.Answer("(fixture)", "A visible response while bringing Capri back.");
                pet.Talk.Open();
                for (int i = 0; i < 80; i++)
                {
                    if (i % 4 == 0) pet.HideAway();
                    if (i % 5 == 0) pet.SetOnTop(i % 10 == 0);
                    pet.Frame.Press("Bring here");
                    await Task.Delay(i % 2 == 0 ? 40 : 160);
                    if (!pet.IsVisible || pet.Away) throw new Exception("Bring here did not restore Capri.");
                }
                Console.WriteLine("PASS repeated Bring here during replies, typing, hiding and stacking");
                nativeQuit = true;
                desktop.TryShutdown();
                if (closed) throw new Exception("Native Quit closed the character before Capri's shutdown completed.");
                pet.Bring(); lateBring = true;
                DispatcherTimer.RunOnce(() => { Console.Error.WriteLine("Native Quit did not finish shutdown."); pet.Finish(1); }, TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { File.WriteAllText(Path.Combine(Entry.Folder,"bring-error.txt"),error.ToString()); Console.Error.WriteLine(error); pet.Finish(1); }
        });
        base.OnFrameworkInitializationCompleted();
    }
}
