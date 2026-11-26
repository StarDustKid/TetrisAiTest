namespace TetrisAiTest.Core;

/// <summary>
/// ミノ1つの配置（種類・回転・位置）と、判定用のビットマスク。
/// X, Y は形状の左下の盤面座標（Y=0 が最下段）。
/// </summary>
public readonly record struct Placement(
    MinoType Type,
    int Rotation,
    int X,
    int Y,
    Board Cells,   // ミノが占めるマス
    Board Below,   // ミノの下端のすぐ下のマス（最下段なら床 = raw row 0）
    Board Swept,   // この位置から盤面最上段まで、ミノが真上から落ちてくる間に通過する領域
    int MinY,      // ミノが占める最下段（ライン消去チェック用）
    int MaxY,      // ミノが占める最上段
    int Index)     // PlacementTable.Get(Type) の中での番号（手の記録に使う）
{
    public override string ToString() => $"{Type} R{Rotation}  x={X,2} y={Y,2}";
}

/// <summary>全ミノの取りうる全配置を事前計算したテーブル。</summary>
public static class PlacementTable
{
    private static readonly Dictionary<MinoType, Placement[]> _table = Build();

    public static Placement[] Get(MinoType type) => _table[type];

    public static int TotalCount => _table.Values.Sum(a => a.Length);

    private static Dictionary<MinoType, Placement[]> Build()
    {
        var result = new Dictionary<MinoType, Placement[]>();
        foreach (var type in MinoShapes.AllTypes)
        {
            var list = new List<Placement>();
            foreach (var shape in MinoShapes.GetRotations(type))
            {
                int maxX = Board.Width - shape.Width;
                int maxY = Board.Height - shape.Height;
                for (int y = 0; y <= maxY; y++)
                    for (int x = 0; x <= maxX; x++)
                        list.Add(Create(shape, x, y, list.Count));
            }
            result[type] = list.ToArray();
        }
        return result;
    }

    private static Placement Create(MinoShape shape, int x, int y, int index)
    {
        Board cells = ShapeAt(shape, x, y);

        // 下端：形状内のセルで、そのすぐ下が形状に含まれないもの → その下のマス
        // 盤面 y-1 の raw row は y（y=0 のとき raw row 0 = 床）
        Board below = Board.Empty;
        foreach (var (cx, cy) in shape.Cells)
        {
            if (shape.Contains(cx, cy - 1)) continue;
            below |= Board.FromRawCell(x + cx, Board.ToRawRow(y + cy - 1));
        }

        // 通過領域：y から最上段までの全位置の和集合
        Board swept = Board.Empty;
        for (int yy = y; yy <= Board.Height - shape.Height; yy++)
            swept |= ShapeAt(shape, x, yy);

        return new Placement(shape.Type, shape.Rotation, x, y,
            cells, below, swept, y, y + shape.Height - 1, index);
    }

    private static Board ShapeAt(MinoShape shape, int x, int y)
    {
        Board b = Board.Empty;
        foreach (var (cx, cy) in shape.Cells)
            b |= Board.FromRawCell(x + cx, Board.ToRawRow(y + cy));
        return b;
    }
}

/// <summary>盤面に対して有効な配置・次の盤面を求める。</summary>
public static class PlacementFinder
{
    /// <summary>
    /// 盤面に置ける配置を列挙する。
    /// 条件：ミノと盤面が重ならない ＆ ミノの下端が盤面か床に接している。
    /// hardDropOnly = true のときは、真上から落として到達できるもの（通過領域が空）に限る。
    /// </summary>
    public static IEnumerable<Placement> ValidPlacements(Board board, MinoType type, bool hardDropOnly)
    {
        Board support = board | Board.Floor;
        foreach (var p in PlacementTable.Get(type))
        {
            if (p.Cells.Intersects(board)) continue;          // 重なる
            if (!p.Below.Intersects(support)) continue;       // 浮いている
            if (hardDropOnly && p.Swept.Intersects(board)) continue; // 上から落とせない
            yield return p;
        }
    }

    /// <summary>ミノを置いてライン消去した次の盤面を、重複を除いて返す。</summary>
    public static List<(Placement Placement, Board Next, int Cleared)> NextBoards(
        Board board, MinoType type, bool hardDropOnly)
    {
        var result = new List<(Placement, Board, int)>();
        var seen = new HashSet<Board>();
        foreach (var p in ValidPlacements(board, type, hardDropOnly))
        {
            var next = board.Place(p, out int cleared);
            if (seen.Add(next)) result.Add((p, next, cleared));
        }
        return result;
    }
}
