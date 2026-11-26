using System.Diagnostics;
using System.Numerics;

namespace TetrisAiTest.Core;

/// <summary>目標探索の目標。</summary>
public enum SearchGoal
{
    Single,        // ちょうど1ライン消去
    Double,        // ちょうど2ライン消去
    Triple,        // ちょうど3ライン消去
    Tetris,        // 4ライン消去
    TSpin,         // Tスピン（Mini を除く）で1ライン以上消去
    PerfectClear,  // 全消し
}

/// <summary>目標の達成判定と、目標に近づいているかの評価（ヒューリスティック）。</summary>
public static class GoalRules
{
    public static string Name(SearchGoal g) => g switch
    {
        SearchGoal.Single => "シングル",
        SearchGoal.Double => "ダブル",
        SearchGoal.Triple => "トリプル",
        SearchGoal.Tetris => "テトリス",
        SearchGoal.TSpin => "Tスピン",
        SearchGoal.PerfectClear => "全消し",
        _ => g.ToString(),
    };

    /// <summary>その1手で目標を達成したか。</summary>
    public static bool IsAchieved(SearchGoal g, in Child c) => g switch
    {
        SearchGoal.Single => c.Cleared == 1,
        SearchGoal.Double => c.Cleared == 2,
        SearchGoal.Triple => c.Cleared == 3,
        SearchGoal.Tetris => c.Cleared == 4,
        SearchGoal.TSpin => c.Move.Spin == SpinKind.Full && c.Cleared >= 1,
        SearchGoal.PerfectClear => c.Cleared > 0 && c.State.Board.IsZero,
        _ => false,
    };

    /// <summary>目標に近いほど大きくなる評価値。ビームに残す局面の選別に使う。</summary>
    public static double Heuristic(SearchGoal g, in Board b)
    {
        var f = Evaluator.Analyze(b);
        Span<int> counts = stackalloc int[Board.Height];
        double sq = 0;
        for (int y = 0; y < Board.Height; y++)
        {
            counts[y] = BitOperations.PopCount((uint)b.GetRow(y));
            sq += counts[y] * counts[y];
        }

        switch (g)
        {
            case SearchGoal.Single:
            case SearchGoal.Double:
            case SearchGoal.Triple:
            case SearchGoal.Tetris:
            {
                int n = (int)g + 1;
                // 埋まった行が多いほど良い。穴・凹凸・高さは減点
                double score = sq - 8.0 * f.Holes - 0.5 * f.Bumpiness - 1.0 * f.MaxHeight;
                if (n >= 2) score += 30.0 * Math.Min(BestWell(b, f), n);
                return score;
            }

            case SearchGoal.TSpin:
                return 0.2 * sq - 6.0 * f.Holes - 0.4 * f.Bumpiness - 0.3 * f.AggregateHeight + BestTSlot(b, counts);

            case SearchGoal.PerfectClear:
            {
                // 全消しできない局面は捨てる（−∞）
                if (!PerfectClearFeasible(b, f)) return double.NegativeInfinity;
                // 下4行の中で平らに埋める
                double low = 0;
                int above = 0;
                for (int y = 0; y < Board.Height; y++)
                {
                    if (y < 4) low += counts[y] * counts[y];
                    else above += counts[y];
                }
                return 0.5 * low - 15.0 * f.Holes - 0.6 * f.Bumpiness - 20.0 * above;
            }
        }
        return 0;
    }

    /// <summary>
    /// 全消しの見込みがあるか（必要条件のチェック）。
    /// ・高さ4以下、穴なし
    /// ・ある高さ h（今の高さ〜4）で、h 行ぶんの空きマスがミノで埋められる数（4の倍数）
    ///   かつ、空きマスのつながった塊それぞれが4の倍数
    /// </summary>
    private static bool PerfectClearFeasible(in Board b, in BoardFeatures f)
    {
        if (f.MaxHeight > 4 || f.Holes > 0) return false;
        int cells = b.PopCount;
        for (int h = Math.Max(f.MaxHeight, 1); h <= 4; h++)
        {
            if ((Board.Width * h - cells) % 4 != 0) continue;
            if (EmptyRegionsDivisibleBy4(b, h)) return true;
        }
        return false;
    }

    private static bool EmptyRegionsDivisibleBy4(in Board b, int h)
    {
        Span<bool> seen = stackalloc bool[Board.Width * 4];
        Span<int> stack = stackalloc int[Board.Width * 4];
        for (int start = 0; start < Board.Width * h; start++)
        {
            int sx = start % Board.Width, sy = start / Board.Width;
            if (seen[start] || b[sx, sy]) continue;

            int size = 0, top = 0;
            stack[top++] = start;
            seen[start] = true;
            while (top > 0)
            {
                int c = stack[--top];
                size++;
                int x = c % Board.Width, y = c / Board.Width;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0);
                    int ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                    if (nx < 0 || nx >= Board.Width || ny < 0 || ny >= h) continue;
                    int id = ny * Board.Width + nx;
                    if (seen[id] || b[nx, ny]) continue;
                    seen[id] = true;
                    stack[top++] = id;
                }
            }
            if (size % 4 != 0) return false;
        }
        return true;
    }

    /// <summary>
    /// 1列だけ空いた「9マス埋まった行」が、同じ列で上から開いたまま何行あるか（その列ごとの最大）。
    /// テトリスなど複数ライン消去の準備度。
    /// </summary>
    private static int BestWell(in Board b, in BoardFeatures f)
    {
        int best = 0;
        for (int x = 0; x < Board.Width; x++)
        {
            ushort pattern = (ushort)(Board.FullRow & ~(1 << x));
            int count = 0;
            for (int y = 0; y < Board.Height; y++)
            {
                if (b.GetRow(y) != pattern) continue;
                if (ColumnTop(b, x) > y) continue; // その列の上がふさがっている
                count++;
            }
            if (count > best) best = count;
        }
        return best;
    }

    private static int ColumnTop(in Board b, int x)
    {
        for (int y = Board.Height - 1; y >= 0; y--)
            if (b[x, y]) return y + 1;
        return 0;
    }

    /// <summary>
    /// T字の穴（下向きのTが収まり、3コーナーが埋まり、屋根がある形）を探し、
    /// 消せるライン数に応じた点数を返す。Tスピンの準備度。
    /// </summary>
    private static double BestTSlot(in Board b, ReadOnlySpan<int> counts)
    {
        double best = 0;
        for (int cy = 1; cy < Board.Height - 1; cy++)
        {
            for (int cx = 1; cx < Board.Width - 1; cx++)
            {
                // 下向きTのマス：(cx-1,cy) (cx,cy) (cx+1,cy) (cx,cy-1)
                if (b[cx - 1, cy] || b[cx, cy] || b[cx + 1, cy] || b[cx, cy - 1]) continue;

                bool tl = b[cx - 1, cy + 1], tr = b[cx + 1, cy + 1];
                bool bl = b[cx - 1, cy - 1], br = b[cx + 1, cy - 1];
                int corners = (tl ? 1 : 0) + (tr ? 1 : 0) + (bl ? 1 : 0) + (br ? 1 : 0);
                if (corners < 3 || !(tl || tr)) continue; // 3コーナー＋屋根

                int lines = (counts[cy] + 3 == Board.Width ? 1 : 0) + (counts[cy - 1] + 1 == Board.Width ? 1 : 0);
                double score = 15 + 30 * lines;
                if (score > best) best = score;
            }
        }
        return best;
    }
}

public static partial class Searcher
{
    /// <summary>
    /// 目標（最短シングル・Tスピン・全消しなど）を、できるだけ少ない手数で達成する手順を探す。
    /// 深さごとに生成した子をすべて目標判定し、最初に達成できた深さで止める。
    /// 局面は目標別の評価で上位 BeamWidth 件だけ残すので、ビーム幅の範囲内での最短になる。
    /// Tスピンが目標のときは、配置の作り方を自動的に SRS にする。
    /// </summary>
    public static SearchResult FindGoal(GameState start, byte[] queue, SearchGoal goal, SearchOptions o,
        IProgress<SearchProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var reporter = new Reporter(progress, sw, o.Depth);
        var gen = goal == SearchGoal.TSpin ? MoveGeneration.Srs : o.Generation;
        string name = GoalRules.Name(goal);

        if (goal == SearchGoal.TSpin && start.Hold != (byte)MinoType.T && !queue.Contains((byte)MinoType.T))
            return new SearchResult(false, [], 0, 0, 1, sw.Elapsed, "ネクストにもホールドにも T がないので、Tスピンはできません。");

        var layers = new List<SearchNode[]>
        {
            new[] { new SearchNode { State = start, Parent = -1, Score = GoalRules.Heuristic(goal, start.Board) } }
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

            // この深さで目標を達成した手のうち、攻撃量が一番大きいもの
            int hitParent = -1, hitAttack = -1;
            Move hitMove = default;

            for (int i = 0; i < current.Length; i++)
            {
                if ((i & 31) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    reporter.Report(d + 1, expanded, states + next.Count);
                }
                ref readonly var node = ref current[i];
                children.Clear();
                Simulator.GenerateChildren(node.State, queue, o.UseHold, gen, children);

                foreach (var c in children)
                {
                    expanded++;
                    if (GoalRules.IsAchieved(goal, c))
                    {
                        if (c.Attack > hitAttack) { hitParent = i; hitAttack = c.Attack; hitMove = c.Move; }
                        continue;
                    }
                    if (hitParent >= 0) continue; // 見つかった深さでは、これ以上の局面は不要

                    var key = c.State.WithoutScoreState();
                    var n = new SearchNode
                    {
                        State = c.State,
                        Parent = i,
                        Move = c.Move,
                        TotalAttack = node.TotalAttack + c.Attack,
                        SinceClear = c.Cleared > 0 ? (byte)0 : (byte)Math.Min(node.SinceClear + 1, 255),
                        Score = GoalRules.Heuristic(goal, c.State.Board),
                    };
                    if (double.IsNegativeInfinity(n.Score)) continue; // 目標に届かないと分かっている局面
                    if (index.TryGetValue(key, out int at))
                    {
                        if (next[at].Score < n.Score) next[at] = n;
                    }
                    else
                    {
                        index[key] = next.Count;
                        next.Add(n);
                    }
                }
            }

            if (hitParent >= 0)
            {
                var moves = Trace(layers, layers.Count - 1, hitParent);
                moves.Add(hitMove);
                sw.Stop();
                return new SearchResult(true, Replay(start, queue, moves, gen), 0, expanded, states, sw.Elapsed,
                    $"{moves.Count} 手で{name}できます。");
            }

            if (next.Count == 0)
            {
                sw.Stop();
                return new SearchResult(false, [], 0, expanded, states, sw.Elapsed,
                    $"{d} 手目でミノが尽きました。{d} 手以内では{name}できません。");
            }

            next.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (next.Count > o.BeamWidth) next.RemoveRange(o.BeamWidth, next.Count - o.BeamWidth);
            layers.Add(next.ToArray());
            states += next.Count;
        }

        sw.Stop();
        return new SearchResult(false, [], 0, expanded, states, sw.Elapsed,
            $"{o.Depth} 手以内では{name}の手順が見つかりませんでした（深さかビーム幅を増やしてみてください）。");
    }
}
