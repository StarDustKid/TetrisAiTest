namespace TetrisAiTest.Core;

/// <summary>
/// 1手 = 16ビット（ミノ譜の1要素）。
/// bit 0-2 : 置いたミノの種類（0〜6）
/// bit 3   : ホールドを使った
/// bit 4-13: 配置インデックス（PlacementTable.Get(種類) の番号）
/// bit 14  : スピンした（Tスピン）
/// bit 15  : Mini（bit 14 が立っているときのみ有効）
/// </summary>
public readonly record struct Move(ushort Raw)
{
    public static Move Create(MinoType type, bool hold, int placementIndex, SpinKind spin = SpinKind.None) =>
        new((ushort)((int)type | (hold ? 1 << 3 : 0) | ((placementIndex & 0x3FF) << 4)
            | (spin != SpinKind.None ? 1 << 14 : 0) | (spin == SpinKind.Mini ? 1 << 15 : 0)));

    public MinoType Type => (MinoType)(Raw & 7);
    public bool Hold => (Raw & (1 << 3)) != 0;
    public int PlacementIndex => (Raw >> 4) & 0x3FF;
    public SpinKind Spin =>
        (Raw & (1 << 14)) == 0 ? SpinKind.None : (Raw & (1 << 15)) != 0 ? SpinKind.Mini : SpinKind.Full;
    public Placement Placement => PlacementTable.Get(Type)[PlacementIndex];

    public string SpinLabel => Spin switch
    {
        SpinKind.Full => "T-Spin",
        SpinKind.Mini => "T-Spin Mini",
        _ => "",
    };

    public override string ToString() =>
        Placement + (Hold ? " [Hold]" : "") + (Spin != SpinKind.None ? " " + SpinLabel : "");
}

/// <summary>
/// 探索・シミュレーション用の局面。
/// ミノ順（queue）は探索全体で1つだけ持ち、局面は「何番目まで使ったか」だけを持つ。
/// </summary>
public readonly record struct GameState(
    Board Board,
    byte Hold,          // 0〜6、7 = 空
    byte QueueIndex,    // 次に出てくるミノの位置（queue[QueueIndex] が現在のミノ）
    byte Combo,         // 直前まで連続で消去した手数（直前の手で消していなければ 0）
    bool BackToBack)    // 直前の消去がテトリスだったか
{
    public const byte NoHold = 7;

    public bool HasHold => Hold != NoHold;

    public static GameState Initial(Board board, MinoType? hold = null) =>
        new(board, hold is { } h ? (byte)h : NoHold, 0, 0, false);

    /// <summary>盤面・ホールド・ミノ位置だけを残した局面（ルート探索の重複判定用）。</summary>
    public GameState WithoutScoreState() => this with { Combo = 0, BackToBack = false };

    public ulong Hash64()
    {
        ulong meta = (ulong)Hold | (ulong)QueueIndex << 3 | (ulong)Combo << 11 | (BackToBack ? 1UL << 19 : 0);
        ulong h = Board.Hash64() ^ (meta * 0x9E3779B97F4A7C15UL);
        h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL;
        return h ^ (h >> 29);
    }

    public override int GetHashCode() => (int)Hash64();
}

/// <summary>攻撃量（送る行数）のルール。ガイドラインを簡略化したもの。</summary>
public static class AttackRules
{
    // REN 数（連続消去の2手目以降）ごとの追加攻撃
    private static readonly int[] ComboTable = [0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 4, 5];

    public const int PerfectClearBonus = 10;

    /// <param name="cleared">この手で消した行数</param>
    /// <param name="comboAfter">この手を含む連続消去の手数（消していなければ 0）</param>
    public static int Attack(int cleared, int comboAfter, bool backToBackBefore, bool perfectClear,
        SpinKind spin, out bool backToBackAfter)
    {
        if (cleared == 0)
        {
            backToBackAfter = backToBackBefore; // 消していなければ B2B は途切れない
            return 0;
        }

        int attack = spin switch
        {
            SpinKind.Full => cleared switch { 1 => 2, 2 => 4, _ => 6 },   // TSS / TSD / TST
            SpinKind.Mini => cleared switch { 1 => 0, _ => 1 },
            _ => cleared switch { 1 => 0, 2 => 1, 3 => 2, _ => 4 },
        };
        bool difficult = cleared >= 4 || spin != SpinKind.None; // テトリスとTスピン消去は B2B 対象
        if (difficult && backToBackBefore) attack += 1;
        backToBackAfter = difficult;

        attack += ComboTable[Math.Min(comboAfter - 1, ComboTable.Length - 1)];
        if (perfectClear) attack += PerfectClearBonus;
        return attack;
    }
}
