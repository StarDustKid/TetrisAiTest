namespace TetrisAiTest.Core;

/// <summary>スピンの種類。</summary>
public enum SpinKind : byte { None = 0, Mini = 1, Full = 2 }

/// <summary>配置の作り方。</summary>
public enum MoveGeneration
{
    /// <summary>真上から落として届く配置だけ（速い）。</summary>
    HardDrop,
    /// <summary>出現位置から左右移動・ソフトドロップ・回転（SRSキック付き）で届く配置。Tスピンも判定する。</summary>
    Srs,
    /// <summary>接地して重ならない配置すべて（到達できるかは見ない）。</summary>
    AllGrounded,
}

/// <summary>到達できる配置1つ（PlacementTable のインデックスとスピン）。</summary>
public readonly record struct ReachablePlacement(int Index, SpinKind Spin);

/// <summary>
/// SRS（Super Rotation System）による移動生成。
/// ミノの状態 = (回転 0/R/2/L, 回転中心 cx, cy)。y は上向き、盤面座標。
/// 出現位置から 左右・下・右回転・左回転 で幅優先探索し、
/// これ以上下に動けない状態を「置ける配置」として PlacementTable のインデックスに変換する。
/// </summary>
public static class Srs
{
    private const int MinX = -2, MaxX = 11, MinY = -2, MaxY = 23;
    private const int W = MaxX - MinX + 1, H = MaxY - MinY + 1;
    private const int StatesPerType = 4 * W * H;

    public const int SpawnX = 4, SpawnY = 20;

    // 回転中心からの相対座標（回転0の形、y上向き）
    private static readonly (int X, int Y)[][] Spawn =
    [
        /* I */ [(-1, 0), (0, 0), (1, 0), (2, 0)],
        /* O */ [(0, 0), (1, 0), (0, 1), (1, 1)],
        /* T */ [(-1, 0), (0, 0), (1, 0), (0, 1)],
        /* S */ [(-1, 0), (0, 0), (0, 1), (1, 1)],
        /* Z */ [(-1, 1), (0, 1), (0, 0), (1, 0)],
        /* J */ [(-1, 1), (-1, 0), (0, 0), (1, 0)],
        /* L */ [(1, 1), (-1, 0), (0, 0), (1, 0)],
    ];

    // I の4状態（4×4の箱の中で中心がずれるので個別に定義）
    private static readonly (int X, int Y)[][] IStates =
    [
        [(-1, 0), (0, 0), (1, 0), (2, 0)],
        [(1, 1), (1, 0), (1, -1), (1, -2)],
        [(-1, -1), (0, -1), (1, -1), (2, -1)],
        [(0, 1), (0, 0), (0, -1), (0, -2)],
    ];

    // キック表（y上向き）。キーは from*4+to。
    private static readonly Dictionary<int, (int X, int Y)[]> KicksJlstz = new()
    {
        [0 * 4 + 1] = [(0, 0), (-1, 0), (-1, 1), (0, -2), (-1, -2)],
        [1 * 4 + 0] = [(0, 0), (1, 0), (1, -1), (0, 2), (1, 2)],
        [1 * 4 + 2] = [(0, 0), (1, 0), (1, -1), (0, 2), (1, 2)],
        [2 * 4 + 1] = [(0, 0), (-1, 0), (-1, 1), (0, -2), (-1, -2)],
        [2 * 4 + 3] = [(0, 0), (1, 0), (1, 1), (0, -2), (1, -2)],
        [3 * 4 + 2] = [(0, 0), (-1, 0), (-1, -1), (0, 2), (-1, 2)],
        [3 * 4 + 0] = [(0, 0), (-1, 0), (-1, -1), (0, 2), (-1, 2)],
        [0 * 4 + 3] = [(0, 0), (1, 0), (1, 1), (0, -2), (1, -2)],
    };

    private static readonly Dictionary<int, (int X, int Y)[]> KicksI = new()
    {
        [0 * 4 + 1] = [(0, 0), (-2, 0), (1, 0), (-2, -1), (1, 2)],
        [1 * 4 + 0] = [(0, 0), (2, 0), (-1, 0), (2, 1), (-1, -2)],
        [1 * 4 + 2] = [(0, 0), (-1, 0), (2, 0), (-1, 2), (2, -1)],
        [2 * 4 + 1] = [(0, 0), (1, 0), (-2, 0), (1, -2), (-2, 1)],
        [2 * 4 + 3] = [(0, 0), (2, 0), (-1, 0), (2, 1), (-1, -2)],
        [3 * 4 + 2] = [(0, 0), (-2, 0), (1, 0), (-2, -1), (1, 2)],
        [3 * 4 + 0] = [(0, 0), (1, 0), (-2, 0), (1, -2), (-2, 1)],
        [0 * 4 + 3] = [(0, 0), (-1, 0), (2, 0), (-1, 2), (2, -1)],
    };

    // 事前計算：状態ごとのマス、盤面内か、PlacementTable のインデックス
    private static readonly Board[] Masks = new Board[7 * StatesPerType];
    private static readonly bool[] Valid = new bool[7 * StatesPerType];
    private static readonly short[] PlacementIndex = new short[7 * StatesPerType];

    static Srs()
    {
        for (int t = 0; t < 7; t++)
        {
            var lookup = new Dictionary<Board, int>();
            foreach (var p in PlacementTable.Get((MinoType)t)) lookup[p.Cells] = p.Index;

            for (int r = 0; r < 4; r++)
            {
                var cells = CellsOf(t, r);
                for (int cx = MinX; cx <= MaxX; cx++)
                    for (int cy = MinY; cy <= MaxY; cy++)
                    {
                        int id = StateId(t, r, cx, cy);
                        PlacementIndex[id] = -1;
                        Board mask = Board.Empty;
                        bool valid = true, inField = true;
                        foreach (var (dx, dy) in cells)
                        {
                            int x = cx + dx, y = cy + dy;
                            if (x < 0 || x >= Board.Width || y < 0 || y > MaxY) { valid = false; break; }
                            if (y >= Board.Height) inField = false;
                            mask |= Board.FromRawCell(x, Board.ToRawRow(y));
                        }
                        Valid[id] = valid;
                        if (!valid) continue;
                        Masks[id] = mask;
                        if (inField && lookup.TryGetValue(mask, out int pi)) PlacementIndex[id] = (short)pi;
                    }
            }
        }
    }

    private static (int X, int Y)[] CellsOf(int type, int rot)
    {
        if (type == (int)MinoType.I) return IStates[rot];
        if (type == (int)MinoType.O) return Spawn[type]; // O は回転しても同じ
        var cells = Spawn[type];
        for (int i = 0; i < rot; i++) cells = cells.Select(c => (c.Y, -c.X)).ToArray(); // 時計回り
        return cells;
    }

    private static int StateId(int t, int r, int cx, int cy) =>
        ((t * 4 + r) * W + (cx - MinX)) * H + (cy - MinY);

    private static bool InRange(int cx, int cy) => cx >= MinX && cx <= MaxX && cy >= MinY && cy <= MaxY;

    private static bool Fits(in Board board, int t, int r, int cx, int cy)
    {
        if (!InRange(cx, cy)) return false;
        int id = StateId(t, r, cx, cy);
        return Valid[id] && !Masks[id].Intersects(board);
    }

    // スレッドごとの作業領域（探索は並列に呼ばれることがあるため）
    [ThreadStatic] private static byte[]? _visited;
    [ThreadStatic] private static int[]? _queue;
    [ThreadStatic] private static byte[]? _best;

    /// <summary>
    /// 盤面に対して、出現位置から到達できる配置をすべて列挙する。
    /// 同じ配置に複数の経路で届く場合は、一番強いスピン（Full ＞ Mini ＞ なし）を採用する。
    /// </summary>
    public static void Reachable(in Board board, MinoType type, List<ReachablePlacement> output)
    {
        output.Clear();
        int t = (int)type;
        if (!Fits(board, t, 0, SpawnX, SpawnY)) return; // 出現位置がふさがっている

        var visited = _visited ??= new byte[StatesPerType];
        var queue = _queue ??= new int[StatesPerType * 3 + 16];
        var best = _best ??= new byte[1024];
        Array.Clear(visited);
        int placementCount = PlacementTable.Get(type).Length;
        Array.Fill(best, (byte)255, 0, placementCount);

        int head = 0, tail = 0;
        // 状態の符号化：(r, cx, cy, act)。act = 0:移動/落下, 1:回転, 2:回転（5番目のキック）
        void Push(int r, int cx, int cy, int act)
        {
            int local = (r * W + (cx - MinX)) * H + (cy - MinY);
            byte bit = (byte)(1 << act);
            if ((visited[local] & bit) != 0) return;
            visited[local] |= bit;
            queue[tail++] = (local << 2) | act;
        }

        Push(0, SpawnX, SpawnY, 0);
        int rotations = type == MinoType.O ? 0 : 4;
        var kicks = type == MinoType.I ? KicksI : KicksJlstz;

        while (head < tail)
        {
            int code = queue[head++];
            int act = code & 3;
            int local = code >> 2;
            int cy = local % H + MinY;
            int cx = (local / H) % W + MinX;
            int r = local / (H * W);

            // これ以上下に行けない → 置ける
            if (!Fits(board, t, r, cx, cy - 1))
            {
                int pi = PlacementIndex[StateId(t, r, cx, cy)];
                if (pi >= 0)
                {
                    var spin = type == MinoType.T && act != 0 ? TSpinCheck(board, r, cx, cy, act == 2) : SpinKind.None;
                    if (best[pi] == 255 || (byte)spin > best[pi]) best[pi] = (byte)spin;
                }
            }

            if (Fits(board, t, r, cx - 1, cy)) Push(r, cx - 1, cy, 0);
            if (Fits(board, t, r, cx + 1, cy)) Push(r, cx + 1, cy, 0);
            if (Fits(board, t, r, cx, cy - 1)) Push(r, cx, cy - 1, 0);

            if (rotations == 0) continue;
            for (int dir = 1; dir <= 3; dir += 2) // 1 = 右回転, 3 = 左回転
            {
                int nr = (r + dir) & 3;
                var table = kicks[r * 4 + nr];
                for (int k = 0; k < table.Length; k++)
                {
                    int nx = cx + table[k].X, ny = cy + table[k].Y;
                    if (!Fits(board, t, nr, nx, ny)) continue;
                    Push(nr, nx, ny, k == 4 ? 2 : 1);
                    break;
                }
            }
        }

        for (int i = 0; i < placementCount; i++)
            if (best[i] != 255) output.Add(new ReachablePlacement(i, (SpinKind)best[i]));
    }

    /// <summary>
    /// Tスピン判定（3コーナールール）。
    /// T の中心の斜め4マスのうち3つ以上が埋まっていればTスピン。
    /// 向いている側の2マスが両方埋まっているか、5番目のキックで入ったなら Full、そうでなければ Mini。
    /// </summary>
    private static SpinKind TSpinCheck(in Board board, int r, int cx, int cy, bool fifthKick)
    {
        bool tl = Occupied(board, cx - 1, cy + 1), tr = Occupied(board, cx + 1, cy + 1);
        bool br = Occupied(board, cx + 1, cy - 1), bl = Occupied(board, cx - 1, cy - 1);
        int count = (tl ? 1 : 0) + (tr ? 1 : 0) + (br ? 1 : 0) + (bl ? 1 : 0);
        if (count < 3) return SpinKind.None;

        bool front = r switch
        {
            0 => tl && tr,  // 上向き
            1 => tr && br,  // 右向き
            2 => br && bl,  // 下向き
            _ => bl && tl,  // 左向き
        };
        return front || fifthKick ? SpinKind.Full : SpinKind.Mini;
    }

    private static bool Occupied(in Board board, int x, int y)
    {
        if (x < 0 || x >= Board.Width || y < 0) return true; // 壁と床は埋まり扱い
        if (y >= Board.Height) return false;
        return board[x, y];
    }
}
