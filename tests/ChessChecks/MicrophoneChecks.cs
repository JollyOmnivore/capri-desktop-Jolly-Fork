using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CapriDesktop;

internal sealed class MicrophoneTestApp : Application
{
    private Pet pet;
    private readonly List<string> checks = new();
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        pet = new Pet(desktop);
        pet.MicrophoneList = () => Task.FromResult(new[] { new MicrophoneInput("built-in", "Built-in microphone", true), new MicrophoneInput("usb fixture", "USB headset") });
        pet.Show();
        DispatcherTimer.RunOnce(async () => await Run(), TimeSpan.FromMilliseconds(300));
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
        while (!ready()) { if (DateTime.UtcNow > until) throw new Exception("Microphone UI timed out."); await Task.Delay(20); }
    }
    private async Task Run()
    {
        string error = null;
        MainframeSheet sheet = null;
        try
        {
            pet.OpenMainframe(); pet.Frame.ShowSettings(true);
            var button = pet.Frame.Row("Microphone input");
            Check(button.IsVisible, "microphone settings stay available in Simple Settings");
            foreach (int scale in new[] { 100, 150, 200 })
            {
                pet.SetMainframeSize(scale); pet.Frame.Sync();
                pet.Frame.SavePicture(Path.Combine(Entry.Folder, "settings-" + scale + ".png"));
                Check(Canvas.GetTop(button) >= 0 && button.Height > 0, "microphone button has a usable layout at " + scale + "%");
            }
            pet.SetMainframeSize(100);
            sheet = new MainframeSheet(pet, "MICROPHONE INPUT", "microphone");
            sheet.Show(pet.Frame);
            await Wait(() => !pet.MicrophoneLoading && pet.MicrophoneChoices.Length == 3);
            var picker = (NeonPicker)sheet.Rows["Input device"];
            Check(pet.MicrophoneChoices[0].Name.Contains("Built-in microphone") && pet.MicrophoneId == "", "default input follows the system without selecting a device silently");
            picker.Pick(2);
            Check(pet.MicrophoneId == "usb fixture" && pet.Saved.MicrophoneInput == "usb fixture", "device picker selects and saves the USB microphone UID");
            var persisted = Path.Combine(Entry.Folder, "microphone-settings.json");
            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(Pet.NewSettings));
            using (var stream = File.Create(persisted)) serializer.WriteObject(stream, Pet.Newest(pet.Saved));
            Pet.NewSettings loaded;
            using (var stream = File.OpenRead(persisted)) loaded = (Pet.NewSettings)serializer.ReadObject(stream);
            Check(Pet.Kept(new Settings(), loaded).MicrophoneInput == "usb fixture", "microphone choice is restored from persisted settings after restarting");
            var newer = Pet.Newest(pet.Saved);
            Check(newer.MicrophoneInput == "usb fixture", "microphone choice survives the secondary settings file");
            sheet.SavePicture(Path.Combine(Entry.Folder, "microphone.png"));
            pet.MicrophoneList = () => Task.FromResult(new[] { new MicrophoneInput("built-in", "Built-in microphone", true) });
            ((NeonButton)sheet.Rows["Refresh devices"]).Press();
            await Wait(() => !pet.MicrophoneLoading);
            Check(pet.MicrophoneId == "usb fixture" && pet.MicrophoneChoices[^1].Name.Contains("not connected"), "disconnecting a chosen microphone preserves its UID without falling back");
            picker.Pick(0);
            Check(pet.MicrophoneId == "" && pet.Saved.MicrophoneInput == "", "system default can be selected explicitly");
            var pending = new TaskCompletionSource<MicrophoneInput[]>();
            pet.MicrophoneList = () => pending.Task; pet.RefreshMicrophones();
            pet.MicrophoneList = () => Task.FromResult(new[] { new MicrophoneInput("new", "New USB input") }); pet.RefreshMicrophones();
            await Wait(() => !pet.MicrophoneLoading);
            pending.SetResult(new[] { new MicrophoneInput("old", "Stale USB input") });
            await Task.Delay(60);
            Check(pet.MicrophoneChoices.Any(i => i.Id == "new") && !pet.MicrophoneChoices.Any(i => i.Id == "old"), "a slow refresh cannot replace a newer device list");
            pet.MicrophoneList = () => Task.FromException<MicrophoneInput[]>(new IOException("fixture failure")); pet.RefreshMicrophones();
            await Wait(() => !pet.MicrophoneLoading);
            Check(pet.MicrophoneProblem.Contains("Couldn't list") && pet.MicrophoneId == "", "enumeration failures leave the saved selection intact");
        }
        catch (Exception e) { error = e.ToString(); checks.Add("FAIL " + error); Console.Error.WriteLine(error); }
        sheet?.Close();
        File.WriteAllLines(Path.Combine(Entry.Folder, "checks.txt"), checks);
        pet.Finish(error == null ? 0 : 1);
    }
}
