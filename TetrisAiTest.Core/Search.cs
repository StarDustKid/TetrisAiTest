using System.Diagnostics;

namespace TetrisAiTest.Core;

public sealed class SearchOptions
{
    /// <summary>何手先まで読むか（ミノが足りなくなればそこで止まる）。</summary>
    public int Depth { get; set; } = 6;
    /// <summary>最適手探索：深さごとに残す局面数。</summary>
    public int BeamWidth { get; set; } = 300;
    /// <summary>ルート探索：展開する局面数の上限。</summary>
    public int NodeLimit { get; set; } = 2_000_000;
    public bool UseHold { get; set; } = true;
    /// <summary>配置の作り方（ハードドロップのみ／SRS／接地すべて）。</summary>
    public MoveGeneration Generation { get; set; } = MoveGeneration.HardDrop;
    /// <summary>ルート探索：途中でのライン消去を許すか。</summary>
    public bool AllowLineClears { get; set; } = false;
    public Evaluator Evaluator { get; set; } = new();
}

/// <summary>結果の1手分。</summary>
public sealed record SearchStep(
    int Number,          // 1始まりの手数
    Move Move,
    Board Before,        // 置く前の盤面
    Board After,         // 置いて消去した後の盤面
    int Cleared,         // この手で消した行数
    int Attack,          // この手で送った行数
    int SinceClear,      // 最後の消去からの手数（この手で消したら 0）
    byte HoldAfter);     // この手の後のホールド（7 = 空）

public sealed record SearchResult(
    bool Found,
    IReadOnlyList<SearchStep> Steps,
    double Score,
    long Expanded,       // 生成した子の数
    long States,         // 残した局面の数
    TimeSpan Elapsed,
    string Message)
{
    public IReadOnlyList<Move> Moves => Steps.Select(s => s.Move).ToArray();
    public int TotalAttack => Steps.Sum(s => s.Attack);
    public int TotalCleared => Steps.Sum(s => s.Cleared);
}

/// <summary>探索中の進み具合（UI表示用）。</summary>
public readonly record struct SearchProgress(
    int Depth,           // いま展開している深さ（1始まり）
    int MaxDepth,
    long Expanded,       // 生成した子の数
    long States,         // 残した局面の数
    TimeSpan Elapsed);

/// <summary>探索木のノード（親はインデックスで持つ）。</summary>
internal struct SearchNode
{
    public GameState State;
    public int Parent;       // 1つ前の深さの配列でのインデックス（根は -1）
    public Move Move;
    public int TotalAttack;
    public byte SinceClear;
    public double Score;
}

public static partial class Searcher
{
    /// <summary>約0.1秒ごとに進捗を通知する（毎回通知するとUIが詰まるため間引く）。</summary>
    private sealed class Reporter(IProgress<SearchProgress>? progress, Stopwatch sw, int maxDepth)
    {
        private long _last = -1000;

        public void Report(int depth, long expanded, long states, bool force = false)
        {
            if (progress is null) return;
            long now = sw.ElapsedMilliseconds;
            if (!force && now - _last < 100) return;
            _last = now;
            progress.Report(new SearchProgress(depth, maxDepth, expanded, states, sw.Elapsed));
        }
    }

    // ------------------------------------------------------------------
    // 最適手探索（ビームサーチ）
    // ------------------------------------------------------------------

    /// <summary>
    /// 盤面とネクストから、評価値が最も高くなる手順を探す。
    /// 結果の最初の1手が「今の最適手」。
    /// </summary>
    public static SearchResult FindBest(GameState root, byte[] queue, SearchOptions o,
        IProgress<SearchProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var reporter = new Reporter(progress, sw, o.Depth);
        var layers = new List<SearchNode[]>
        {
            new[] { new SearchNode { State = root, Parent = -1, Score = o.Evaluator.Evaluate(root.Board, 0) } }
        };
        var children = new List<Child>(256);
        long expanded = 0, states = 1;

        for (int d = 0; d < o.Depth; d++)
        {
            ct.ThrowIfCancellationRequested();
            reporter.Report(d + 1, expanded, states, force: true);
            var current = layers[^1];
            var index = new Dictionary<GameState, int>();
            var next = new List<SearchNode>();

            for (int i = 0; i < current.Length; i++)
            {
                if ((i & 63) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    reporter.Report(d + 1, expanded, states + next.Count);
                }
                ref readonly var node = ref current[i];
                children.Clear();
                Simulator.GenerateChildren(node.State, queue, o.UseHold, o.Generation, children);

                foreach (var c in children)
                {
                    expanded++;
                    int total = node.TotalAttack + c.Attack;
                    var n = new SearchNode
                    {
                        State = c.State,
                        Parent = i,
                        Move = c.Move,
                        TotalAttack = total,
                        SinceClear = c.Cleared > 0 ? (byte)0 : (byte)Math.Min(node.SinceClear + 1, 255),
                        Score = o.Evaluator.Evaluate(c.State.Board, total),
                    };

                    // 同じ局面に複数の経路で来たら評価の良い方だけ残す
                    if (index.TryGetValue(c.State, out int at))
                    {
                        if (next[at].Score < n.Score) next[at] = n;
                    }
                    else
                    {
                        index[c.State] = next.Count;
                        next.Add(n);
                    }
                }
            }

            if (next.Count == 0) break; // ミノが尽きた・置ける場所がない
            next.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (next.Count > o.BeamWidth) next.RemoveRange(o.BeamWidth, next.Count - o.BeamWidth);
            layers.Add(next.ToArray());
            states += next.Count;
        }

        sw.Stop();
        if (layers.Count == 1)
            return new SearchResult(false, [], 0, expanded, states, sw.Elapsed, "置ける手がありません（ミノが足りないか、置ける場所がありません）。");

        var moves = Trace(layers, layers.Count - 1, 0);
        var steps = Replay(root, queue, moves, o.Generation);
        double score = layers[^1][0].Score;
        return new SearchResult(true, steps, score, expanded, states, sw.Elapsed,
            $"{steps.Count} 手先まで読みました。");
    }

    // ------------------------------------------------------------------
    // ルート探索（幅優先探索＋刈り込み）
    // ------------------------------------------------------------------

    /// <summary>
    /// 開始局面から目標の盤面にたどり着く手順を探す（幅優先なので最短手数）。
    /// AllowLineClears = false のときは「目標にないマスには置かない」で強く刈り込む。
    /// 目標を空の盤面にして AllowLineClears = true にするとパーフェクトクリア探索になる。
    /// </summary>
    public static SearchResult FindPath(GameState start, byte[] queue, Board target, SearchOptions o,
        IProgress<SearchProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var reporter = new Reporter(progress, sw, o.Depth);
        if (start.Board == target)
            return new SearchResult(true, [], 0, 0, 1, sw.Elapsed, "開始盤面がすでに目標と同じです。");

        if (!o.AllowLineClears && !start.Board.AndNot(target).IsZero)
            return new SearchResult(false, [], 0, 0, 1, sw.Elapsed,
                "開始盤面に目標にないブロックがあります。ライン消去なしでは到達できません（「ライン消去を許可」をオンにしてください）。");

        int targetCount = target.PopCount;
        var layers = new List<SearchNode[]>
        {
            new[] { new SearchNode { State = start, Parent = -1 } }
        };
        var visited = new HashSet<GameState> { start.WithoutScoreState() };
        var children = new List<Child>(256);
        long expanded = 0, states = 1;

        for (int d = 0; d < o.Depth; d++)
        {
            var current = layers[^1];
            var next = new List<SearchNode>();
            reporter.Report(d + 1, expanded, states, force: true);

            for (int i = 0; i < current.Length; i++)
            {
                if ((i & 63) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    reporter.Report(d + 1, expanded, states);
                }
                ref readonly var node = ref current[i];
                children.Clear();
                Simulator.GenerateChildren(node.State, queue, o.UseHold, o.Generation, children);

                foreach (var c in children)
                {
                    expanded++;
                    Board b = c.State.Board;

                    if (!o.AllowLineClears)
                    {
                        if (c.Cleared > 0) continue;
                        if (!b.AndNot(target).IsZero) continue; // 目標にないマスに置いた
                    }

                    int remaining = Math.Min(o.Depth - (d + 1), PiecesLeft(c.State, queue.Length));
                    if (!CanReachCount(b.PopCount, targetCount, remaining, o.AllowLineClears)) continue;

                    if (!visited.Add(c.State.WithoutScoreState())) continue;

                    var n = new SearchNode
                    {
                        State = c.State,
                        Parent = i,
                        Move = c.Move,
                        TotalAttack = node.TotalAttack + c.Attack,
                        SinceClear = c.Cleared > 0 ? (byte)0 : (byte)Math.Min(node.SinceClear + 1, 255),
                    };
                    next.Add(n);
                    states++;

                    if (b == target)
                    {
                        layers.Add(next.ToArray());
                        var moves = Trace(layers, layers.Count - 1, next.Count - 1);
                        sw.Stop();
                        return new SearchResult(true, Replay(start, queue, moves, o.Generation), 0,
                            expanded, states, sw.Elapsed, $"{moves.Count} 手で目標に到達しました。");
                    }

                    if (states >= o.NodeLimit)
                    {
                        sw.Stop();
                        return new SearchResult(false, [], 0, expanded, states, sw.Elapsed,
                            $"局面数の上限（{o.NodeLimit:N0}）に達したので打ち切りました。");
                    }
                }
            }

            if (next.Count == 0) break;
            layers.Add(next.ToArray());
        }

        sw.Stop();
        return new SearchResult(false, [], 0, expanded, states, sw.Elapsed,
            layers.Count - 1 < o.Depth
                ? $"{layers.Count - 1} 手目で候補がなくなり、目標に到達できませんでした（ミノの種類・数や目標の形を確認してください）。"
                : $"{o.Depth} 手以内では目標に到達できませんでした。");
    }

    /// <summary>残りのミノで使える手数の上限（ホールド中のミノも1手として数える）。</summary>
    private static int PiecesLeft(in GameState s, int queueLength) =>
        Math.Max(0, queueLength - s.QueueIndex) + (s.HasHold ? 1 : 0);

    /// <summary>
    /// マス数だけで見て、残り手数で目標のマス数にできるか。
    /// 1手で +4、1行消すと -10。
    /// </summary>
    private static bool CanReachCount(int current, int target, int remaining, bool allowClears)
    {
        for (int moves = 0; moves <= remaining; moves++)
        {
            int diff = current + 4 * moves - target; // 消すべきマス数
            if (diff < 0) continue;
            if (diff == 0) return true;
            if (allowClears && diff % 10 == 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 共通
    // ------------------------------------------------------------------

    /// <summary>最後の深さのノードから親をたどって手の列を作る。</summary>
    private static List<Move> Trace(List<SearchNode[]> layers, int depth, int index)
    {
        var moves = new List<Move>();
        for (int d = depth; d > 0; d--)
        {
            var n = layers[d][index];
            moves.Add(n.Move);
            index = n.Parent;
        }
        moves.Reverse();
        return moves;
    }

    /// <summary>手の列を開始局面から再生して、1手ごとの記録を作る（ミノ譜の再生と同じ処理）。</summary>
    public static List<SearchStep> Replay(GameState start, byte[] queue, IReadOnlyList<Move> moves, MoveGeneration gen)
    {
        var steps = new List<SearchStep>();
        var s = start;
        int sinceClear = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            if (!Simulator.TryStep(s, queue, moves[i], gen, out var next, out int cleared, out int attack, out var error))
                throw new InvalidOperationException($"{i + 1} 手目を再生できません：{error}");
            sinceClear = cleared > 0 ? 0 : sinceClear + 1;
            steps.Add(new SearchStep(i + 1, moves[i], s.Board, next.Board, cleared, attack, sinceClear, next.Hold));
            s = next;
        }
        return steps;
    }

    // ------------------------------------------------------------------
    // 文字列 ⇔ ミノ順
    // ------------------------------------------------------------------

    /// <summary>"TIOLJSZ" のような文字列をミノ順に変換する（空白・カンマは無視）。</summary>
    public static byte[] ParseQueue(string text)
    {
        var list = new List<byte>();
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == ',' || ch == '-') continue;
            if (!TryParseMino(ch, out var t))
                throw new FormatException($"ミノの文字ではありません：'{ch}'（I O T S Z J L のいずれか）");
            list.Add((byte)t);
        }
        if (list.Count > 255) throw new FormatException("ネクストは255個までです。");
        return list.ToArray();
    }

    public static bool TryParseMino(char ch, out MinoType type)
    {
        switch (char.ToUpperInvariant(ch))
        {
            case 'I': type = MinoType.I; return true;
            case 'O': type = MinoType.O; return true;
            case 'T': type = MinoType.T; return true;
            case 'S': type = MinoType.S; return true;
            case 'Z': type = MinoType.Z; return true;
            case 'J': type = MinoType.J; return true;
            case 'L': type = MinoType.L; return true;
            default: type = default; return false;
        }
    }

    /// <summary>7種1巡（7-bag）でミノ順を作る。</summary>
    public static string RandomBagQueue(Random rnd, int bags)
    {
        var chars = "IOTSZJL".ToCharArray();
        var sb = new System.Text.StringBuilder();
        for (int b = 0; b < bags; b++)
        {
            rnd.Shuffle(chars);
            sb.Append(chars);
        }
        return sb.ToString();
    }
}
