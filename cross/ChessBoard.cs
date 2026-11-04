using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CapriDesktop
{
    internal sealed class MiniScore
    {
        internal int You, Her, Draws;
        internal string Text { get { return "YOU " + You + "   CAPRI " + Her + "   DRAWS " + Draws; } }
    }

    internal sealed class ChessBoard : Window
    {
        internal readonly ChessGame Game;
        internal readonly ChessSurface Surface;
        internal readonly ComboBox Difficulty, Side;
        internal readonly Button UndoButton, HintButton, ResignButton, NewButton;
        private readonly TextBlock status, last, score;
        private readonly StackPanel promotions;
        private readonly DispatcherTimer ticks = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        private readonly Random dice = new Random();
        private ChessWork work;
        private bool help, hintAfter, syncing, closed;
        private int selected = -1, promotionFrom = -1, promotionTo = -1;
        private string note = "";
        internal ChessMove Hint = new ChessMove(-1, -1, 0);
        internal List<ChessMove> Legal;
        internal Action Played;
        internal bool Busy { get { return work != null; } }
        internal ChessWork Pending { get { return work; } }
        internal bool Ticking { get { return ticks.IsEnabled; } }
        internal bool Promotion { get { return promotionFrom >= 0; } }
        internal bool Ended { get { return closed; } }
        internal int Selected { get { return selected; } }

        internal ChessBoard(ChessGame game)
        {
            Game = game;
            Title = "Chess with Capri";
            Width = 580; Height = 760; MinWidth = 450; MinHeight = 640;
            Background = new SolidColorBrush(Look.Panel);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Grid layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"), Margin = new Thickness(16) };
            StackPanel heading = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 14) };
            heading.Children.Add(Words("CHESS WITH CAPRI", 24, Look.Cyan));
            heading.Children.Add(Words("Click a piece, then a square. You can also drag.", 13, Look.Dim));
            Add(layout, heading, 0);
            StackPanel choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
            choices.Children.Add(Words("Difficulty", 14, Look.Ink));
            Difficulty = new ComboBox { ItemsSource = new[] { "Easy", "Normal", "Hard" }, MinWidth = 110 };
            choices.Children.Add(Difficulty);
            choices.Children.Add(Words("Your pieces", 14, Look.Ink));
            Side = new ComboBox { ItemsSource = new[] { "White", "Black" }, MinWidth = 100 };
            ToolTip.SetTip(Side, "Changing your color starts a new game.");
            choices.Children.Add(Side);
            Add(layout, choices, 1);
            Surface = new ChessSurface(this);
            AutomationProperties.SetName(Surface, "Chess board. Click or drag your pieces to a highlighted square.");
            Add(layout, Surface, 2);
            promotions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(0, 8, 0, 8), IsVisible = false };
            promotions.Children.Add(Words("Promote to", 14, Look.Cyan));
            string[] names = { "Queen", "Rook", "Bishop", "Knight" };
            int[] pieces = { 5, 4, 3, 2 };
            for (int i = 0; i < names.Length; i++)
            {
                int kind = pieces[i];
                promotions.Children.Add(Button(names[i], delegate { Promote(kind); }));
            }
            Add(layout, promotions, 3);
            StackPanel information = new StackPanel { Spacing = 5, Margin = new Thickness(0, 8, 0, 12) };
            status = Words("", 16, Look.Cyan);
            last = Words("", 13, Look.Ink);
            score = Words("", 13, Look.Dim);
            information.Children.Add(status); information.Children.Add(last); information.Children.Add(score);
            Add(layout, information, 4);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            NewButton = Button("New game", delegate { Command("again", null, null); });
            UndoButton = Button("Undo", delegate { Command("undo", null, null); });
            HintButton = Button("Hint", delegate { Command("hint", null, null); });
            ResignButton = Button("Resign", delegate { Command("resign", null, null); });
            actions.Children.Add(NewButton); actions.Children.Add(UndoButton); actions.Children.Add(HintButton); actions.Children.Add(ResignButton);
            actions.Children.Add(Button("Close", Close));
            Add(layout, actions, 5);
            Content = layout;
            ticks.Tick += delegate { FinishWork(); };
            Difficulty.SelectionChanged += delegate { if (!syncing && Difficulty.SelectedIndex >= 0) Command("level", new[] { "easy", "normal", "hard" }[Difficulty.SelectedIndex], null); };
            Side.SelectionChanged += delegate { if (!syncing && Side.SelectedIndex >= 0) Command("again", null, Side.SelectedIndex == 1 ? "black" : "white"); };
            Opened += delegate { Think(false); };
            KeyDown += delegate(object sender, KeyEventArgs key)
            {
                if (key.Key == Key.Escape && Promotion) { promotionFrom = promotionTo = -1; note = "Promotion cancelled."; Refresh(); key.Handled = true; }
            };
            Legal = Game.Board.Legal();
            Refresh();
        }

        private static TextBlock Words(string text, double size, Color colour)
        {
            return new TextBlock { Text = text, FontFamily = Look.Mono, FontSize = size, Foreground = new SolidColorBrush(colour), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        }

        private static Button Button(string label, Action action)
        {
            Button button = new Button { Content = label, FontFamily = Look.Mono, FontSize = 14, Padding = new Thickness(12, 8), Foreground = new SolidColorBrush(Look.Ink), Background = new SolidColorBrush(Color.FromRgb(30, 39, 53)), Cursor = new Cursor(StandardCursorType.Hand) };
            button.Template = new FuncControlTemplate<Button>((control, scope) => new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 39, 53)), CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 8),
                Child = new ContentPresenter { Content = label, Foreground = new SolidColorBrush(Look.Ink), FontFamily = Look.Mono, FontSize = 14, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center }
            });
            button.PropertyChanged += delegate(object sender, AvaloniaPropertyChangedEventArgs property)
            {
                if (property.Property == IsEnabledProperty) button.Opacity = button.IsEnabled ? 1 : 0.45;
            };
            button.Click += delegate { action(); };
            return button;
        }

        private static void Add(Grid layout, Control control, int row) { Grid.SetRow(control, row); layout.Children.Add(control); }

        private void Refresh()
        {
            syncing = true;
            Difficulty.SelectedIndex = Game.Level == "easy" ? 0 : Game.Level == "hard" ? 2 : 1;
            Side.SelectedIndex = Game.You == -1 ? 1 : 0;
            syncing = false;
            promotions.IsVisible = Promotion;
            status.Text = note.Length > 0 ? note : Game.Status;
            last.Text = Game.Last;
            score.Text = Game.Score.Text;
            UndoButton.IsEnabled = Game.LastMove.From >= 0 || Game.Result != 0;
            HintButton.IsEnabled = ResignButton.IsEnabled = Game.Result == 0;
            Surface.InvalidateVisual();
        }

        private void Cancel()
        {
            if (work != null) work.Cancelled = true;
            work = null; hintAfter = false;
            ticks.Stop();
        }

        internal void Command(string action, string level, string side)
        {
            if (closed) return;
            if (action == "close") { Close(); return; }
            if (action == "hint")
            {
                if (Game.Result != 0) return;
                if (Game.Board.Turn != Game.You) { hintAfter = true; note = "Hint after Capri's move."; Refresh(); return; }
                Cancel(); Think(true); return;
            }
            Cancel();
            if (action == "again") Game.Again(level, side);
            else if (action == "undo") Game.Undo();
            else if (action == "resign") Game.Resign();
            else if (action == "level") { if (level == "easy" || level == "normal" || level == "hard") Game.Level = level; Game.Moment = "reset"; }
            selected = promotionFrom = promotionTo = -1;
            Hint = new ChessMove(-1, -1, 0); note = "";
            Changed();
            Think(false);
        }

        private void Think(bool helping)
        {
            if (closed || work != null || Game.Result != 0 || !helping && Game.Board.Turn == Game.You) return;
            help = helping;
            work = ChessWork.Start(Game.Board, helping ? "hard" : Game.Level, dice.Next());
            if (helping) note = "Looking for a hint...";
            ticks.Start();
            Refresh();
        }

        private void FinishWork()
        {
            if (work == null || !work.Done) return;
            ChessWork finished = work;
            bool helping = help, nextHint = hintAfter;
            work = null; hintAfter = false; ticks.Stop();
            if (closed || finished.Cancelled) return;
            if (finished.Error != null || finished.Choice == null || finished.Choice.Move.From < 0)
            {
                note = "Couldn't finish that search. Try a new game."; Refresh(); return;
            }
            if (helping)
            {
                Hint = finished.Choice.Move; selected = Hint.From;
                note = "Try " + ChessPosition.Square(Hint.From) + " - " + ChessPosition.Square(Hint.To) + (Hint.Promotion == 0 ? "." : " = " + " PNBRQK"[Hint.Promotion] + ".");
                Refresh();
            }
            else
            {
                note = "";
                Game.Move(finished.Choice.Move, finished.Choice);
                Changed();
                if (nextHint) Think(true);
            }
        }

        private void Changed()
        {
            Legal = Game.Board.Legal();
            Refresh();
            if (Played != null) Played();
        }

        internal bool TryMove(int from, int to, int promotion = 0)
        {
            if (closed || Game.Result != 0 || Game.Board.Turn != Game.You) return false;
            foreach (ChessMove move in Legal)
            {
                if (move.From != from || move.To != to) continue;
                if (move.Promotion != 0 && promotion == 0)
                {
                    Cancel(); promotionFrom = from; promotionTo = to; note = "Choose a promotion piece."; Refresh(); return true;
                }
                if (move.Promotion != promotion) continue;
                Cancel();
                if (!Game.Move(move, null)) return false;
                selected = promotionFrom = promotionTo = -1;
                Hint = new ChessMove(-1, -1, 0); note = "";
                Changed(); Think(false); return true;
            }
            return false;
        }

        internal void Promote(int kind) { if (Promotion) TryMove(promotionFrom, promotionTo, kind); }

        internal void ClickSquare(int square)
        {
            if (!ChessPosition.On(square) || Promotion || Game.Result != 0 || Game.Board.Turn != Game.You) return;
            if (selected >= 0 && square != selected && TryMove(selected, square)) return;
            selected = square != selected && Game.Board.Cells[square] * Game.You > 0 ? square : -1;
            Refresh();
        }

        protected override void OnClosed(EventArgs e)
        {
            closed = true;
            Cancel(); Played = null;
            base.OnClosed(e);
        }
    }

    internal sealed class ChessSurface : Control
    {
        private readonly ChessBoard owner;
        private int held = -1;
        private Point down;
        private bool dragging;
        internal ChessSurface(ChessBoard board) { owner = board; MinHeight = 240; Cursor = new Cursor(StandardCursorType.Hand); }

        internal Rect Board
        {
            get { double side = Math.Max(0, Math.Min(Bounds.Width - 38, Bounds.Height - 38)); return new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side); }
        }

        internal Rect Tile(int square)
        {
            Rect board = Board;
            double size = board.Width / 8;
            int file = square & 7, rank = square >> 4;
            return new Rect(board.X + (owner.Game.You == 1 ? file : 7 - file) * size, board.Y + (owner.Game.You == 1 ? 7 - rank : rank) * size, size, size);
        }

        internal int At(Point point)
        {
            Rect board = Board;
            if (board.Width <= 0 || !board.Contains(point) || point.X >= board.Right || point.Y >= board.Bottom) return -1;
            int file = Math.Min(7, (int)((point.X - board.X) * 8 / board.Width)), rank = Math.Min(7, (int)((point.Y - board.Y) * 8 / board.Height));
            return owner.Game.You == 1 ? (7 - rank) * 16 + file : rank * 16 + 7 - file;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            down = e.GetPosition(this); held = At(down); dragging = false;
            owner.ClickSquare(held); e.Pointer.Capture(this); e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            Point delta = e.GetPosition(this) - down;
            if (held >= 0 && delta.X * delta.X + delta.Y * delta.Y > 25) dragging = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (dragging && held >= 0) owner.TryMove(held, At(e.GetPosition(this)));
            held = -1; dragging = false; e.Pointer.Capture(null); e.Handled = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            Rect board = Board;
            if (board.Width <= 0) return;
            for (int rank = 0; rank < 8; rank++)
                for (int file = 0; file < 8; file++)
                {
                    int square = rank * 16 + file;
                    Rect tile = Tile(square);
                    Color shade = (rank + file) % 2 == 0 ? Color.FromRgb(24, 34, 48) : Color.FromRgb(46, 59, 78);
                    if (owner.Game.LastMove.From == square || owner.Game.LastMove.To == square) shade = Color.FromRgb(67, 50, 92);
                    context.FillRectangle(new SolidColorBrush(shade), tile);
                    int piece = owner.Game.Board.Cells[square];
                    if (piece != 0) Piece(context, piece, tile.Deflate(tile.Width * 0.04));
                    if (owner.Selected == square) context.DrawRectangle(null, new Pen(new SolidColorBrush(Look.Cyan), 3), tile.Deflate(2));
                    if (owner.Hint.To == square) context.DrawRectangle(null, new Pen(Brushes.Gold, 3), tile.Deflate(3));
                    foreach (ChessMove move in owner.Legal)
                        if (move.From == owner.Selected && move.To == square)
                        {
                            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(170, 58, 214, 235)), null, tile.Center, tile.Width * 0.10, tile.Height * 0.10);
                            break;
                        }
                    if (Math.Abs(piece) == 6 && piece * owner.Game.Board.Turn > 0 && owner.Game.Board.Check) context.DrawRectangle(null, new Pen(new SolidColorBrush(Look.Red), 3), tile.Deflate(2));
                }
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Look.Edge), 1), board);
            double size = board.Width / 8;
            for (int i = 0; i < 8; i++)
            {
                string file = ((char)('a' + (owner.Game.You == 1 ? i : 7 - i))).ToString();
                string rank = (owner.Game.You == 1 ? 8 - i : i + 1).ToString();
                FormattedText f = Look.Text(file, Look.Regular, 13, Look.Dim), r = Look.Text(rank, Look.Regular, 13, Look.Dim);
                context.DrawText(f, new Point(board.X + (i + 0.5) * size - f.Width / 2, board.Bottom + 3));
                context.DrawText(r, new Point(board.X - 17, board.Y + (i + 0.5) * size - r.Height / 2));
            }
        }

        private static void Piece(DrawingContext context, int piece, Rect box)
        {
            using (context.PushTransform(Matrix.CreateTranslation(box.X, box.Y)))
            using (context.PushTransform(Matrix.CreateScale(box.Width / 52, box.Height / 52)))
            {
                IBrush fill = new SolidColorBrush(piece > 0 ? Look.Ink : Look.Pink);
                Pen edge = new Pen(new SolidColorBrush(Color.FromRgb(8, 13, 22)), 1.4);
                int kind = Math.Abs(piece);
                string path = kind == 6 ? "M13,43 L16,37 21,34 22,28 18,24 18,19 23,19 23,15 18,15 18,10 23,10 23,6 29,6 29,10 34,10 34,15 29,15 29,19 34,19 34,24 30,28 31,34 36,37 39,43 Z" : "M13,43 L16,37 21,34 22,24 30,24 31,34 36,37 39,43 Z";
                context.DrawGeometry(fill, edge, Geometry.Parse(path));
                if (kind == 1) context.DrawEllipse(fill, edge, new Rect(19, 12, 14, 14));
                if (kind == 2) context.DrawGeometry(fill, edge, Geometry.Parse("M18,32 L20,24 14,25 12,21 21,12 23,7 30,12 35,22 33,34 Z"));
                if (kind == 3) { context.DrawGeometry(fill, edge, Geometry.Parse("M26,7 L35,19 32,27 20,27 17,19 Z")); context.DrawLine(edge, new Point(28, 11), new Point(23, 19)); }
                if (kind == 4) context.DrawGeometry(fill, edge, Geometry.Parse("M15,11 L21,11 21,16 24,16 24,11 29,11 29,16 32,16 32,11 37,11 35,25 17,25 Z"));
                if (kind == 5) context.DrawGeometry(fill, edge, Geometry.Parse("M14,13 L21,19 26,9 31,19 38,13 33,28 19,28 Z"));
                if (kind == 2) context.DrawEllipse(edge.Brush, null, new Rect(23, 16, 3, 3));
                context.DrawLine(edge, new Point(17, 38), new Point(35, 38));
            }
        }
    }
}
