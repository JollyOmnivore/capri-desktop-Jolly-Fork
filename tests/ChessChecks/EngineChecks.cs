using CapriDesktop;

internal static class EngineChecks
{
    internal static readonly List<string> Lines = new List<string>();
    internal static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Lines.Add("PASS " + name);
        Console.WriteLine(Lines[^1]);
    }

    private static long Perft(ChessPosition board, int depth)
    {
        if (depth == 0) return 1;
        long nodes = 0;
        foreach (ChessMove move in board.Legal())
        {
            ChessUndo undo = board.Push(move, false);
            nodes += Perft(board, depth - 1);
            board.Pop(undo);
        }
        return nodes;
    }

    internal static ChessMove Find(ChessPosition board, string move)
    {
        return board.Legal().First(candidate => candidate.Text == move);
    }

    internal static void Run()
    {
        ChessPosition start = new ChessPosition(ChessPosition.Start);
        Check(Perft(start, 1) == 20 && Perft(start, 2) == 400 && Perft(start, 3) == 8902 && start.Fen() == ChessPosition.Start, "shared engine matches standard initial-position move counts without mutating the board");
        ChessPosition castle = new ChessPosition("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1");
        string before = castle.Fen();
        ChessUndo castled = castle.Push(Find(castle, "e1g1"), true);
        Check(castle.Cells[ChessPosition.At("g1")] == 6 && castle.Cells[ChessPosition.At("f1")] == 4 && castle.Cells[ChessPosition.At("h1")] == 0, "castling moves both king and rook");
        castle.Pop(castled);
        Check(castle.Fen() == before && castle.Legal().Any(move => move.Text == "e1c1"), "undo restores castling rights and queen-side castling remains legal");
        ChessPosition ep = new ChessPosition("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1");
        ep.Push(Find(ep, "e5d6"), true);
        Check(ep.Cells[ChessPosition.At("d5")] == 0 && ep.Cells[ChessPosition.At("d6")] == 1, "en passant removes the captured pawn");
        ChessPosition pinned = new ChessPosition("4r1k1/8/8/8/8/8/4R3/4K3 w - - 0 1");
        Check(!pinned.Legal().Any(move => move.Text == "e2d2"), "pinned pieces cannot expose their king");
        ChessPosition promote = new ChessPosition("7k/P7/6K1/8/8/8/8/8 w - - 0 1");
        Check(promote.Legal().Count(move => move.From == ChessPosition.At("a7") && move.To == ChessPosition.At("a8")) == 4, "all four promotion choices are legal");
        MiniScore score = new MiniScore();
        ChessGame game = new ChessGame(score, "hard", "white") { Board = new ChessPosition("7k/5Q2/5K2/8/8/8/8/8 w - - 0 1") };
        string mateBefore = game.Board.Fen();
        game.Move(Find(game.Board, "f7g7"), null);
        Check(game.Result == 1 && score.You == 1 && game.Board.Ending() == "Checkmate", "checkmate ends the game and scores the user's win");
        game.Undo();
        Check(game.Result == 0 && score.You == 0 && game.Board.Fen() == mateBefore, "undo reverses a finished game's score and position");
        ChessPosition stale = new ChessPosition("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1");
        Check(stale.Ending() == "Stalemate", "stalemate is a draw rather than checkmate");
        ChessPosition repeat = new ChessPosition(ChessPosition.Start);
        for (int i = 0; i < 2; i++) foreach (string move in new[] { "g1f3", "g8f6", "f3g1", "f6g8" }) repeat.Push(Find(repeat, move), true);
        Check(repeat.Ending() != null && repeat.Ending().Contains("repetition", StringComparison.OrdinalIgnoreCase), "threefold repetition is recognized");
        ChessWork worker = ChessWork.Start(start, "easy", 7);
        Check(SpinWait.SpinUntil(() => worker.Done, 5000) && worker.Error == null && worker.Choice != null && start.Legal().Any(move => move.Same(worker.Choice.Move)) && start.Fen() == ChessPosition.Start, "computer chooses a legal move on a private position snapshot");
        ChessWork cancelled = ChessWork.Start(start, "hard", 1);
        cancelled.Cancelled = true;
        Check(SpinWait.SpinUntil(() => cancelled.Done, 5000) && start.Fen() == ChessPosition.Start, "cancelled search finishes without changing the live position");
        Check(ChessGame.Input("take that back").Action == "undo" && ChessGame.Input("I'll play black").Side == "black" && ChessGame.Input("hard").Level == "hard", "shared text commands recognize undo, color and difficulty");
    }
}
