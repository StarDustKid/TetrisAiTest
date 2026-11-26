namespace TetrisAiTest.Core;

/// <summary>ある局面から1手で進める先（子）。</summary>
public readonly record struct Child(Move Move, GameState State, int Cleared, int Attack);

/// <summary>
/// 1手進める処理。ミノ譜の再生・最適手探索・ルート探索・目標探索のすべてがこのルールで動く。
///
/// ホールドのルール：
///   ホールドなし            … queue[i] を置く。次は i+1
///   ホールドあり（空のとき） … queue[i] をホールドに入れ、queue[i+1] を置く。次は i+2
///   ホールドあり（入っている）… ホールドのミノを置き、queue[i] をホールドに入れる。次は i+1
/// </summary>
public static class Simulator
{
    /// <summary>この手で置くことになるミノを返す。ミノが足りなければ false。</summary>
    public static bool TryGetPiece(in GameState s, ReadOnlySpan<byte> queue, bool useHold, out MinoType piece)
    {
        piece = default;
        int i = s.QueueIndex;
        if (i >= queue.Length) return false;

        if (!useHold) { piece = (MinoType)queue[i]; return true; }
        if (s.HasHold) { piece = (MinoType)s.Hold; return true; }
        if (i + 1 >= queue.Length) return false;
        piece = (MinoType)queue[i + 1];
        return true;
    }

    /// <summary>配置を適用して次の局面を返す（配置の有効性はチェックしない）。</summary>
    public static GameState Apply(in GameState s, ReadOnlySpan<byte> queue, bool useHold, in Placement p, SpinKind spin,
        out int cleared, out int attack)
    {
        byte hold = s.Hold;
        int qi = s.QueueIndex;
        if (useHold)
        {
            hold = queue[qi];
            qi += s.HasHold ? 1 : 2;
        }
        else
        {
            qi += 1;
        }

        Board next = s.Board.Place(p, out cleared);
        int combo = cleared > 0 ? s.Combo + 1 : 0;
        attack = AttackRules.Attack(cleared, combo, s.BackToBack, cleared > 0 && next.IsZero, spin, out bool b2b);
        return new GameState(next, hold, (byte)qi, (byte)Math.Min(combo, 255), b2b);
    }

    /// <summary>配置が置けるか（重ならない＋接地＋必要なら上から到達できる）。SRS 以外のモード用。</summary>
    public static bool CanPlace(in Board board, in Placement p, bool hardDropOnly)
    {
        if (p.Cells.Intersects(board)) return false;
        if (!p.Below.Intersects(board | Board.Floor)) return false;
        if (hardDropOnly && p.Swept.Intersects(board)) return false;
        return true;
    }

    [ThreadStatic] private static List<ReachablePlacement>? _reach;

    /// <summary>盤面にミノを置ける配置を列挙する（モードに応じて）。</summary>
    public static void Placements(in Board board, MinoType piece, MoveGeneration gen, List<ReachablePlacement> output)
    {
        output.Clear();
        if (gen == MoveGeneration.Srs)
        {
            Srs.Reachable(board, piece, output);
            return;
        }

        bool hardDrop = gen == MoveGeneration.HardDrop;
        Board support = board | Board.Floor;
        foreach (ref readonly var p in PlacementTable.Get(piece).AsSpan())
        {
            if (p.Cells.Intersects(board)) continue;
            if (!p.Below.Intersects(support)) continue;
            if (hardDrop && p.Swept.Intersects(board)) continue;
            output.Add(new ReachablePlacement(p.Index, SpinKind.None));
        }
    }

    /// <summary>1手を検証して適用する（ミノ譜の再生用）。</summary>
    public static bool TryStep(in GameState s, ReadOnlySpan<byte> queue, Move m, MoveGeneration gen,
        out GameState next, out int cleared, out int attack, out string? error)
    {
        next = s; cleared = 0; attack = 0; error = null;
        if (!TryGetPiece(s, queue, m.Hold, out var piece))
        {
            error = "ミノが足りません";
            return false;
        }
        if (piece != m.Type)
        {
            error = $"置けるミノは {piece} ですが、手の記録は {m.Type} です";
            return false;
        }

        var reach = _reach ??= new List<ReachablePlacement>(256);
        Placements(s.Board, piece, gen, reach);
        bool ok = false;
        foreach (var r in reach)
        {
            if (r.Index != m.PlacementIndex) continue;
            // スピンの記録は、到達計算の結果以下ならOK（スピンなしで置くことはいつでもできる）
            ok = m.Spin == SpinKind.None || m.Spin == r.Spin;
            break;
        }
        if (!ok)
        {
            error = $"{m} は置けません";
            return false;
        }

        next = Apply(s, queue, m.Hold, m.Placement, m.Spin, out cleared, out attack);
        return true;
    }

    /// <summary>局面から1手で行ける子をすべて列挙する（同じ盤面の重複はまだ除かない）。</summary>
    public static void GenerateChildren(in GameState s, ReadOnlySpan<byte> queue, bool useHold, MoveGeneration gen,
        List<Child> output)
    {
        var reach = _reach ??= new List<ReachablePlacement>(256);
        for (int h = 0; h < (useHold ? 2 : 1); h++)
        {
            bool hold = h == 1;
            if (!TryGetPiece(s, queue, hold, out var piece)) continue;

            // ホールドのミノと現在のミノが同じなら、ホールドしても結果は同じ
            if (hold && s.HasHold && s.Hold == queue[s.QueueIndex]) continue;

            Placements(s.Board, piece, gen, reach);
            var table = PlacementTable.Get(piece);
            foreach (var r in reach)
            {
                ref readonly var p = ref table[r.Index];
                var next = Apply(s, queue, hold, p, r.Spin, out int cleared, out int attack);
                output.Add(new Child(Move.Create(piece, hold, p.Index, r.Spin), next, cleared, attack));
            }
        }
    }
}
