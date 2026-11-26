using System.Numerics;
using System.Text;

namespace TetrisAiTest.Core;

/// <summary>
/// 10×20 のビットボード（ulong×4 = 256ビット）。
/// 内部の行番号(raw row)：0 = 床（仮想行）、1〜20 = 盤面（1が最下段）。
/// ビット位置 = rawRow * 10 + x。
/// 公開APIの y は盤面座標（0 = 最下段, 19 = 最上段）。
/// 盤面データ自体には床ビットを入れない（判定時に Floor と OR する）。
/// </summary>
public readonly struct Board : IEquatable<Board>
{
    public const int Width = 10;
    public const int Height = 20;
    public const ushort FullRow = 0x3FF;

    public readonly ulong A, B, C, D;

    public Board(ulong a, ulong b, ulong c, ulong d) { A = a; B = b; C = c; D = d; }

    public static readonly Board Empty = default;

    /// <summary>床（raw row 0）だけが埋まった盤面。接地判定用。</summary>
    public static readonly Board Floor = new(FullRow, 0, 0, 0);

    // ---- 座標変換 ----
    public static int ToRawRow(int y) => y + 1;
    public static int BitIndex(int x, int rawRow) => rawRow * Width + x;

    private ulong Word(int i) => i switch { 0 => A, 1 => B, 2 => C, 3 => D, _ => 0UL };

    // ---- セル操作 ----
    public bool GetRaw(int x, int rawRow)
    {
        int bit = BitIndex(x, rawRow);
        return ((Word(bit >> 6) >> (bit & 63)) & 1UL) != 0;
    }

    public bool this[int x, int y] => GetRaw(x, ToRawRow(y));

    public static Board FromRawCell(int x, int rawRow)
    {
        int bit = BitIndex(x, rawRow);
        ulong v = 1UL << (bit & 63);
        return (bit >> 6) switch
        {
            0 => new Board(v, 0, 0, 0),
            1 => new Board(0, v, 0, 0),
            2 => new Board(0, 0, v, 0),
            3 => new Board(0, 0, 0, v),
            _ => throw new ArgumentOutOfRangeException(nameof(rawRow)),
        };
    }

    public Board WithCell(int x, int y, bool filled)
    {
        var c = FromRawCell(x, ToRawRow(y));
        return filled ? this | c : this.AndNot(c);
    }

    public Board ToggleCell(int x, int y) => this ^ FromRawCell(x, ToRawRow(y));

    /// <summary>盤面の1行（y）を10ビットで取得。</summary>
    public ushort GetRow(int y) => GetRawRow(ToRawRow(y));

    public ushort GetRawRow(int rawRow)
    {
        int bit = rawRow * Width;
        int w = bit >> 6, o = bit & 63;
        ulong v = Word(w) >> o;
        if (o > 64 - Width) v |= Word(w + 1) << (64 - o);
        return (ushort)(v & FullRow);
    }

    /// <summary>盤面の行配列（y=0 が最下段、最大20行）から生成。</summary>
    public static Board FromRows(ReadOnlySpan<ushort> rows)
    {
        Span<ulong> w = stackalloc ulong[4];
        int n = Math.Min(rows.Length, Height);
        for (int y = 0; y < n; y++)
        {
            ulong r = (ulong)(rows[y] & FullRow);
            int bit = ToRawRow(y) * Width;
            int i = bit >> 6, o = bit & 63;
            w[i] |= r << o;
            if (o > 64 - Width) w[i + 1] |= r >> (64 - o);
        }
        return new Board(w[0], w[1], w[2], w[3]);
    }

    public ushort[] ToRows()
    {
        var rows = new ushort[Height];
        for (int y = 0; y < Height; y++) rows[y] = GetRow(y);
        return rows;
    }

    // ---- ライン消去 ----
    /// <summary>揃った行を消して上を詰めた盤面を返す。</summary>
    public Board ClearLines(out int cleared, int minY = 0, int maxY = Height - 1)
    {
        cleared = 0;
        for (int y = minY; y <= maxY; y++)
            if (GetRow(y) == FullRow) { cleared++; }
        if (cleared == 0) return this;

        Span<ushort> rows = stackalloc ushort[Height];
        int dst = 0;
        for (int y = 0; y < Height; y++)
        {
            ushort r = GetRow(y);
            if (r == FullRow) continue;
            rows[dst++] = r;
        }
        // 残りは 0（stackalloc は初期化されないので明示的にクリア）
        for (int y = dst; y < Height; y++) rows[y] = 0;
        return FromRows(rows);
    }

    /// <summary>配置を置いてライン消去した次の盤面。</summary>
    public Board Place(in Placement p, out int cleared)
        => (this | p.Cells).ClearLines(out cleared, p.MinY, p.MaxY);

    // ---- ビット演算 ----
    public static Board operator &(Board x, Board y) => new(x.A & y.A, x.B & y.B, x.C & y.C, x.D & y.D);
    public static Board operator |(Board x, Board y) => new(x.A | y.A, x.B | y.B, x.C | y.C, x.D | y.D);
    public static Board operator ^(Board x, Board y) => new(x.A ^ y.A, x.B ^ y.B, x.C ^ y.C, x.D ^ y.D);
    public Board AndNot(Board y) => new(A & ~y.A, B & ~y.B, C & ~y.C, D & ~y.D);

    public bool IsZero => (A | B | C | D) == 0;
    public bool Intersects(in Board o) => ((A & o.A) | (B & o.B) | (C & o.C) | (D & o.D)) != 0;
    public int PopCount => BitOperations.PopCount(A) + BitOperations.PopCount(B)
                         + BitOperations.PopCount(C) + BitOperations.PopCount(D);

    // ---- ハッシュ・等価 ----
    /// <summary>プロセスをまたいでも同じ値になる64ビットハッシュ。</summary>
    public ulong Hash64()
    {
        ulong h = A * 0x9E3779B97F4A7C15UL;
        h ^= B * 0xC2B2AE3D27D4EB4FUL; h = BitOperations.RotateLeft(h, 31);
        h ^= C * 0x165667B19E3779F9UL; h = BitOperations.RotateLeft(h, 27);
        h ^= D * 0xD6E8FEB86659FD93UL;
        h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27; h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return h;
    }

    public bool Equals(Board o) => ((A ^ o.A) | (B ^ o.B) | (C ^ o.C) | (D ^ o.D)) == 0;
    public override bool Equals(object? obj) => obj is Board b && Equals(b);
    public override int GetHashCode() => (int)Hash64();
    public static bool operator ==(Board x, Board y) => x.Equals(y);
    public static bool operator !=(Board x, Board y) => !x.Equals(y);

    public string ToHex() => $"{D:X16} {C:X16} {B:X16} {A:X16}";

    /// <summary>上段から下段へのASCII表示（# = ブロック）。</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int y = Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < Width; x++) sb.Append(this[x, y] ? '#' : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
