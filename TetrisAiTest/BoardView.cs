using TetrisAiTest.Core;
using System.ComponentModel;

namespace TetrisAiTest;

/// <summary>
/// 盤面＋床（最下段の仮想行）を描画するコントロール。
/// 選択中の配置について、ミノ位置・ミノ下位置・通過領域を重ねて表示する。
/// </summary>
public sealed class BoardView : Control
{
    public const int LabelWidth = 28;
    private int _cellSize = 24;

    private Board _board;
    private Placement? _placement;
    private bool _showSwept;

    /// <summary>盤面セルがクリックされた（x, y は盤面座標）。</summary>
    public event EventHandler<(int X, int Y)>? CellClicked;

    public BoardView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(24, 24, 32);
        UpdateSize();
    }

    /// <summary>1マスのピクセル数。</summary>
    /// 
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CellSize
    {
        get => _cellSize;
        set { _cellSize = Math.Max(8, value); UpdateSize(); Invalidate(); }
    }

    /// <summary>false にするとクリックで盤面を編集できない（表示専用）。</summary>
    /// 
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Editable { get; set; } = true;

    private void UpdateSize() =>
        Size = new Size(LabelWidth + Board.Width * _cellSize + 1, (Board.Height + 1) * _cellSize + 1);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Board Board
    {
        get => _board;
        set { _board = value; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Placement? Placement
    {
        get => _placement;
        set { _placement = value; Invalidate(); }
    }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSwept
    {
        get => _showSwept;
        set { _showSwept = value; Invalidate(); }
    }

    public static Color MinoColor(MinoType t) => t switch
    {
        MinoType.I => Color.FromArgb(0, 200, 230),
        MinoType.O => Color.FromArgb(235, 210, 0),
        MinoType.T => Color.FromArgb(170, 60, 210),
        MinoType.S => Color.FromArgb(60, 200, 70),
        MinoType.Z => Color.FromArgb(225, 50, 50),
        MinoType.J => Color.FromArgb(50, 90, 230),
        MinoType.L => Color.FromArgb(240, 140, 20),
        _ => Color.White,
    };

    // raw row 0 = 床（一番下）、raw row 20 = 盤面最上段（一番上）
    private Rectangle CellRect(int x, int rawRow) =>
        new(LabelWidth + x * _cellSize, (Board.Height - rawRow) * _cellSize, _cellSize, _cellSize);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Board support = _board | Board.Floor;

        using var blockBrush = new SolidBrush(Color.FromArgb(130, 130, 140));
        using var floorBrush = new SolidBrush(Color.FromArgb(90, 70, 50));
        using var sweptBrush = new SolidBrush(Color.FromArgb(70, 255, 255, 255));
        using var overlapBrush = new SolidBrush(Color.FromArgb(255, 0, 140));
        using var gridPen = new Pen(Color.FromArgb(50, 50, 64));
        using var belowPen = new Pen(Color.Red, 3);
        using var contactBrush = new SolidBrush(Color.Red);
        using var labelFont = new Font("Consolas", 8f);
        using var pieceBrush = new SolidBrush(_placement is { } pp ? MinoColor(pp.Type) : Color.White);

        for (int rawRow = 0; rawRow <= Board.Height; rawRow++)
        {
            // 行ラベル（盤面 y）
            var lr = new Rectangle(0, (Board.Height - rawRow) * _cellSize, LabelWidth - 2, _cellSize);
            string label = rawRow == 0 ? "床" : (rawRow - 1).ToString();
            TextRenderer.DrawText(g, label, labelFont, lr, Color.FromArgb(150, 150, 160),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            for (int x = 0; x < Board.Width; x++)
            {
                var r = CellRect(x, rawRow);
                bool filled = rawRow == 0 || _board.GetRaw(x, rawRow);

                if (rawRow == 0) g.FillRectangle(floorBrush, r);
                else if (filled) g.FillRectangle(blockBrush, r);

                if (_placement is { } p)
                {
                    if (_showSwept && rawRow > 0 && p.Swept.GetRaw(x, rawRow))
                        g.FillRectangle(sweptBrush, r);

                    if (rawRow > 0 && p.Cells.GetRaw(x, rawRow))
                    {
                        // ミノ位置（盤面と重なっていたら警告色）
                        g.FillRectangle(filled ? overlapBrush : pieceBrush, Rectangle.Inflate(r, -1, -1));
                    }

                    if (p.Below.GetRaw(x, rawRow))
                    {
                        // ミノ下位置：赤枠。そこにブロック/床があれば（接地）赤丸
                        var ir = Rectangle.Inflate(r, -3, -3);
                        g.DrawRectangle(belowPen, ir);
                        if (support.GetRaw(x, rawRow))
                            g.FillEllipse(contactBrush, Rectangle.Inflate(r, -_cellSize / 3, -_cellSize / 3));
                    }
                }

                g.DrawRectangle(gridPen, r);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Editable) return;
        int x = (e.X - LabelWidth) / _cellSize;
        int rawRow = Board.Height - e.Y / _cellSize;
        if (e.X < LabelWidth || x < 0 || x >= Board.Width) return;
        if (rawRow < 1 || rawRow > Board.Height) return; // 床はクリック不可
        CellClicked?.Invoke(this, (x, rawRow - 1));
    }
}
