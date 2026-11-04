using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CapriDesktop;

internal static class Entry
{
    internal static string Folder;
    [STAThread]
    private static int Main(string[] args)
    {
        Folder = Path.GetFullPath(args.Length == 0 ? "out/chess-checks" : args[0]);
        Directory.CreateDirectory(Folder);
        if (args.Contains("--dsh"))
        {
            CapriDesktop.Program.Smoke = Folder;
            CapriDesktop.Program.CheckChess = true;
            return AppBuilder.Configure<DshTestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        if (args.Contains("--activity"))
        {
            CapriDesktop.Program.Smoke = Folder;
            CapriDesktop.Program.CheckChess = true;
            return AppBuilder.Configure<ActivityTestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        if (args.Contains("--microphone"))
        {
            CapriDesktop.Program.Smoke = Folder;
            CapriDesktop.Program.CheckChess = true;
            return AppBuilder.Configure<MicrophoneTestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        if (args.Contains("--sync"))
        {
            CapriDesktop.Program.Smoke = Folder;
            CapriDesktop.Program.CheckChess = true;
            return AppBuilder.Configure<SyncTestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        try { EngineChecks.Run(); }
        catch (Exception error) { File.WriteAllLines(Path.Combine(Folder, "checks.txt"), EngineChecks.Lines.Append("FAIL " + error)); Console.Error.WriteLine(error); return 1; }
        CapriDesktop.Program.Smoke = Folder;
        CapriDesktop.Program.CheckChess = true;
        return AppBuilder.Configure<ChessTestApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}

internal sealed class ChessTestApp : Application
{
    private Pet pet;
    private ChessBoard board;
    private ChessWork old;
    private int step, brainCalls, voices;
    private bool small;
    private DateTime began;
    private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
    private IClassicDesktopStyleApplicationLifetime desktop;
    private readonly Pointer pointer = new Pointer(17, PointerType.Mouse, true);

    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Dark; }

    public override void OnFrameworkInitializationCompleted()
    {
        desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
        pet = new Pet(desktop) { Think = delegate { brainCalls++; throw new Exception("Chess asked an AI model."); } };
        Voice.Playback = delegate { voices++; };
        pet.Show();
        began = DateTime.UtcNow;
        timer.Tick += delegate
        {
            try
            {
                if ((DateTime.UtcNow - began).TotalSeconds > 40) throw new Exception("Chess UI checks timed out at step " + step);
                if (Step()) step++;
            }
            catch (Exception error) { Finish(error.ToString()); }
        };
        timer.Start();
        base.OnFrameworkInitializationCompleted();
    }

    private static void Check(bool result, string name) { EngineChecks.Check(result, name); }
    private static int At(string square) { return ChessPosition.At(square); }
    private static void Click(Button button) { button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); }
    private Point Location(string square) { return board.Surface.TranslatePoint(board.Surface.Tile(At(square)).Center, board).Value; }
    private void Press(string square)
    {
        board.Surface.RaiseEvent(new PointerPressedEventArgs(board.Surface, pointer, board, Location(square), 0, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
    }
    private void Release(string square)
    {
        board.Surface.RaiseEvent(new PointerReleasedEventArgs(board.Surface, pointer, board, Location(square), 1, new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
    }
    private void Square(string square) { Press(square); Release(square); }
    private void Drag(string from, string to)
    {
        Press(from);
        board.Surface.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, board.Surface, pointer, board, Location(to), 1, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), KeyModifiers.None));
        Release(to);
    }
    private void Snapshot(string name, double scale = 1)
    {
        Control face = (Control)board.Content;
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
        Point bottom = board.NewButton.TranslatePoint(new Point(0, board.NewButton.Bounds.Height), face).Value;
        Check(bottom.Y <= face.Bounds.Height, "chess controls remain inside the window in " + name);
        using RenderTargetBitmap image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(face.Bounds.Width * scale), (int)Math.Ceiling(face.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        image.Render(face);
        image.Save(Path.Combine(Entry.Folder, name));
    }

    private bool Step()
    {
        if (step == 0)
        {
            pet.Brain.Model = null;
            pet.Hear("let's play chess");
            board = pet.ChessWindow;
            Check(board != null && board.IsVisible && board.Game.You == 1 && board.Game.Level == "normal", "chat opens native chess without a model or key");
            return true;
        }
        if (step == 1)
        {
            if (board.Surface.Bounds.Width < 100) return false;
            Check(board.Surface.At(board.Surface.Tile(At("e2")).Center) == At("e2"), "white-side screen coordinates map to the correct board square");
            Snapshot("start.png"); Snapshot("start-2x.png", 2);
            Square("e2"); Square("e4");
            old = board.Pending;
            Check(board.Game.Board.Cells[At("e4")] == 1 && old != null, "clicking a piece and destination starts a local computer reply");
            pet.Hear("take that back");
            Check(old.Cancelled && board.Game.Board.Fen() == ChessPosition.Start, "chat undo cancels a pending reply and restores the board");
            return true;
        }
        if (step == 2)
        {
            if (!old.Done) return false;
            Check(board.Game.Board.Fen() == ChessPosition.Start && !board.Busy, "a stale engine reply cannot land after undo");
            board.Difficulty.SelectedIndex = 0;
            Drag("d2", "d4");
            Check(board.Game.Board.Cells[At("d4")] == 1 && board.Pending != null, "drag pointer events move a piece and start the engine");
            return true;
        }
        if (step == 3)
        {
            if (board.Busy) return false;
            Check(board.Game.Board.Turn == board.Game.You && board.Game.Board.Full == 2, "Capri replies with a legal local move and returns the turn");
            Click(board.UndoButton);
            Check(board.Game.Board.Fen() == ChessPosition.Start, "Undo button takes back the player's move and Capri's reply");
            pet.Hear("I'll play black");
            Check(board.Game.You == -1 && board.Surface.At(board.Surface.Tile(At("a1")).Center) == At("a1"), "chat changes color and flips board coordinates");
            old = board.Pending;
            board.Side.SelectedIndex = 0;
            Check(old.Cancelled && board.Game.You == 1 && board.Game.Board.Fen() == ChessPosition.Start, "changing side cancels the previous opening search");
            Click(board.HintButton);
            return true;
        }
        if (step == 4)
        {
            if (board.Busy) return false;
            Check(board.Hint.From >= 0 && board.Legal.Any(move => move.Same(board.Hint)) && board.Game.Board.Fen() == ChessPosition.Start, "Hint button highlights a legal move without playing it");
            Snapshot("hint.png");
            board.Command("again", "easy", "white");
            board.Game.Board = new ChessPosition("7k/P7/6K1/8/8/8/8/8 w - - 0 1");
            board.Command("level", "easy", null);
            Square("a7"); Square("a8");
            Check(board.Promotion && !board.Busy && board.Game.Board.Cells[At("a7")] == 1, "promotion waits for a piece choice before moving or thinking");
            return true;
        }
        if (step == 5)
        {
            if (!small)
            {
                Snapshot("promotion.png");
                board.Width = 450; board.Height = 640;
                small = true;
                return false;
            }
            Snapshot("promotion-small.png");
            board.Width = 580; board.Height = 760;
            Click(((Control)board.Content).GetVisualDescendants().OfType<Button>().First(button => (string)button.Content == "Knight"));
            Check(board.Game.Board.Cells[At("a8")] == 2 && board.Game.Result == 3, "underpromotion to a knight handles the insufficient-material draw");
            board.Command("again", "easy", "white");
            board.Game.Board = new ChessPosition("7k/5Q2/5K2/8/8/8/8/8 w - - 0 1");
            board.Command("level", "easy", null);
            Square("f7"); Square("g7");
            Check(board.Game.Result == 1 && !board.Busy && board.Game.Score.You == 1, "native board finishes checkmate, scores the win and stops the engine");
            return true;
        }
        if (step == 6)
        {
            Snapshot("mate.png");
            Click(board.NewButton);
            board.Difficulty.SelectedIndex = 2;
            Check(board.Game.Level == "hard", "difficulty selector changes the local engine level");
            Click(board.HintButton);
            old = board.Pending;
            Click(board.ResignButton);
            Check(old.Cancelled && board.Game.Result == 2 && !board.Ticking, "Resign button cancels pending work and ends the game");
            pet.SetOnTop(false);
            Check(!board.Topmost, "chess follows the companion's Always on top setting");
            pet.SetOnTop(true);
            Check(board.Topmost, "chess restores Always on top with the companion");
            Click(board.NewButton); Click(board.HintButton);
            old = board.Pending;
            pet.Hear("close chess");
            Check(board.Ended && old.Cancelled && !board.Ticking && pet.ChessWindow == null && Hands.MiniBoard() == "", "closing through chat cancels work, stops the timer and clears board context");
            pet.Hear("play chess on easy");
            Check(pet.ChessWindow != null && pet.ChessWindow != board && pet.ChessWindow.Game.Level == "easy", "a closed board reopens with the requested level");
            Check(brainCalls == 0 && voices == 0, "playing and chess commands never call an AI model or paid voice");
            Finish(null);
            return false;
        }
        return false;
    }

    private void Finish(string error)
    {
        timer.Stop();
        List<string> lines = new List<string>(EngineChecks.Lines);
        if (error != null) { lines.Add("FAIL " + error); Console.Error.WriteLine(error); }
        File.WriteAllLines(Path.Combine(Entry.Folder, "checks.txt"), lines);
        pet.Finish(error == null ? 0 : 1);
    }
}
