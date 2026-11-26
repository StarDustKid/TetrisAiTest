namespace TetrisAiTest.Core;

public enum MinoType { I, O, T, S, Z, J, L }

/// <summary>
/// ミノの形状定義。座標は (x, y)、y は上向き。
/// 出現時の形から時計回りに回転させ、重複する形を除いた回転だけを持つ。
/// （I/S/Z は2種、O は1種、T/J/L は4種）
/// </summary>
public static class MinoShapes
{
    public static readonly MinoType[] AllTypes = Enum.GetValues<MinoType>();

    private static readonly Dictionary<MinoType, (int X, int Y)[]> Spawn = new()
    {
        // ####
        [MinoType.I] = [(0, 0), (1, 0), (2, 0), (3, 0)],
        // ##
        // ##
        [MinoType.O] = [(0, 0), (1, 0), (0, 1), (1, 1)],
        // .#.
        // ###
        [MinoType.T] = [(0, 0), (1, 0), (2, 0), (1, 1)],
        // .##
        // ##.
        [MinoType.S] = [(0, 0), (1, 0), (1, 1), (2, 1)],
        // ##.
        // .##
        [MinoType.Z] = [(1, 0), (2, 0), (0, 1), (1, 1)],
        // #..
        // ###
        [MinoType.J] = [(0, 0), (1, 0), (2, 0), (0, 1)],
        // ..#
        // ###
        [MinoType.L] = [(0, 0), (1, 0), (2, 0), (2, 1)],
    };

    private static readonly Dictionary<MinoType, MinoShape[]> _rotations = Build();

    /// <summary>重複を除いた回転形状の一覧。</summary>
    public static MinoShape[] GetRotations(MinoType type) => _rotations[type];

    private static Dictionary<MinoType, MinoShape[]> Build()
    {
        var result = new Dictionary<MinoType, MinoShape[]>();
        foreach (var (type, spawn) in Spawn)
        {
            var list = new List<MinoShape>();
            var seen = new HashSet<string>();
            var cells = spawn;
            for (int r = 0; r < 4; r++)
            {
                var norm = Normalize(cells);
                string key = string.Join(";", norm.Select(c => $"{c.X},{c.Y}"));
                if (seen.Add(key)) list.Add(new MinoShape(type, list.Count, norm));
                // 時計回りに90°回転（y上向き）: (x, y) -> (y, -x)
                cells = cells.Select(c => (c.Y, -c.X)).ToArray();
            }
            result[type] = list.ToArray();
        }
        return result;
    }

    private static (int X, int Y)[] Normalize((int X, int Y)[] cells)
    {
        int minX = cells.Min(c => c.X), minY = cells.Min(c => c.Y);
        return cells.Select(c => (c.X - minX, c.Y - minY))
                    .OrderBy(c => c.Item2).ThenBy(c => c.Item1)
                    .ToArray();
    }
}

/// <summary>回転済みの形状（左下が (0,0) に正規化済み）。</summary>
public sealed class MinoShape
{
    public MinoType Type { get; }
    public int Rotation { get; }
    public (int X, int Y)[] Cells { get; }
    public int Width { get; }
    public int Height { get; }

    public MinoShape(MinoType type, int rotation, (int X, int Y)[] cells)
    {
        Type = type;
        Rotation = rotation;
        Cells = cells;
        Width = cells.Max(c => c.X) + 1;
        Height = cells.Max(c => c.Y) + 1;
    }

    public bool Contains(int x, int y) => Cells.Any(c => c.X == x && c.Y == y);
}
