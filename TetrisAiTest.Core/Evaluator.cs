using System.Numerics;

namespace TetrisAiTest.Core;

/// <summary>盤面の特徴量。</summary>
public readonly record struct BoardFeatures(
    int AggregateHeight,  // 各列の高さの合計
    int MaxHeight,        // 一番高い列
    int Holes,            // 上をふさがれた空きマス
    int Bumpiness,        // 隣り合う列の高さの差の合計
    int RowTransitions);  // 行ごとの「埋まり⇔空き」の切り替わり回数（壁を埋まりとみなす）

/// <summary>
/// 盤面の評価関数（大きいほど良い）。
/// 重みは一般的なテトリスAIでよく使われる値を目安にした初期値なので、調整前提です。
/// </summary>
public sealed class Evaluator
{
    public double AggregateHeightWeight { get; set; } = -0.5;
    public double HolesWeight { get; set; } = -4.0;
    public double BumpinessWeight { get; set; } = -0.4;
    public double RowTransitionsWeight { get; set; } = -0.3;
    /// <summary>この高さを超えた分は二乗でペナルティ。</summary>
    public int DangerHeight { get; set; } = 12;
    public double DangerWeight { get; set; } = -2.0;
    /// <summary>経路上で送った行数の合計に対する重み。</summary>
    public double AttackWeight { get; set; } = 3.0;

    public double Evaluate(in Board board, int totalAttack)
    {
        var f = Analyze(board);
        double score =
            AggregateHeightWeight * f.AggregateHeight +
            HolesWeight * f.Holes +
            BumpinessWeight * f.Bumpiness +
            RowTransitionsWeight * f.RowTransitions +
            AttackWeight * totalAttack;

        if (f.MaxHeight > DangerHeight)
        {
            int over = f.MaxHeight - DangerHeight;
            score += DangerWeight * over * over;
        }
        return score;
    }

    public static BoardFeatures Analyze(in Board board)
    {
        Span<int> heights = stackalloc int[Board.Width];
        int holes = 0, transitions = 0;
        int covered = 0, seen = 0;

        for (int y = Board.Height - 1; y >= 0; y--)
        {
            int row = board.GetRow(y);
            if (row == 0 && seen == 0) continue;

            // 上にブロックがある列の空きマス = 穴
            holes += BitOperations.PopCount((uint)(~row & covered & Board.FullRow));
            covered |= row;

            // この行で初めて現れた列の高さを確定
            int fresh = row & ~seen;
            while (fresh != 0)
            {
                int x = BitOperations.TrailingZeroCount(fresh);
                heights[x] = y + 1;
                fresh &= fresh - 1;
            }
            seen |= row;

            // 行の切り替わり（左右の壁を「埋まり」とみなす）
            int withWalls = (row << 1) | 1 | (1 << (Board.Width + 1));
            transitions += BitOperations.PopCount((uint)(withWalls ^ (withWalls >> 1)) & ((1u << (Board.Width + 1)) - 1));
        }

        int aggregate = 0, max = 0, bump = 0;
        for (int x = 0; x < Board.Width; x++)
        {
            aggregate += heights[x];
            if (heights[x] > max) max = heights[x];
            if (x > 0) bump += Math.Abs(heights[x] - heights[x - 1]);
        }
        return new BoardFeatures(aggregate, max, holes, bump, transitions);
    }
}
