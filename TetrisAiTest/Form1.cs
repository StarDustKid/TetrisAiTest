using TetrisAiTest.Core;

namespace TetrisAiTest
{
    /// <summary>
    /// ミノ配置テーブルのビューアー。
    /// ・ミノ種を選ぶと、そのミノの全配置（または盤面に置ける配置）を一覧表示
    /// ・一覧で選んだ配置の「ミノ位置」と「ミノ下位置」を盤面上に表示
    /// ・盤面クリックでブロックを編集、「この配置を置く」で次の盤面へ
    /// </summary>
    public partial class Form1 : Form
    {
        private Board _board = Board.Empty;

        private readonly BoardView _view = new();
        private readonly ComboBox _typeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly CheckBox _validOnly = new() { Text = "盤面に置ける配置のみ（重ならない＋接地）", AutoSize = true };
        private readonly CheckBox _hardDropOnly = new() { Text = "上から落として到達できるもののみ（Swept が空）", AutoSize = true, Enabled = false };
        private readonly CheckBox _showSwept = new() { Text = "通過領域（Swept）を表示", AutoSize = true };
        private readonly ListBox _list = new() { Width = 380, Height = 220, Font = new Font("Consolas", 10f), IntegralHeight = false };
        private readonly Label _countLabel = new() { AutoSize = true };
        private readonly TextBox _info = new()
        {
            Multiline = true, ReadOnly = true, Width = 380, Height = 130,
            Font = new Font("Consolas", 9f), ScrollBars = ScrollBars.Vertical,
        };
        private readonly Button _placeButton = new() { Text = "この配置を置く", AutoSize = true };
        private readonly Button _clearButton = new() { Text = "盤面クリア", AutoSize = true };
        private readonly Button _randomButton = new() { Text = "ランダム盤面", AutoSize = true };
        private readonly Random _rnd = new();

        public Form1()
        {
            InitializeComponent();
            Text = "Tetris AI Test - ミノ配置ビューアー";
            BuildLayout();

            _typeBox.Items.AddRange(MinoShapes.AllTypes.Cast<object>().ToArray());
            _typeBox.SelectedIndexChanged += (_, _) => RefreshList(keepSelection: false);
            _validOnly.CheckedChanged += (_, _) =>
            {
                _hardDropOnly.Enabled = _validOnly.Checked;
                RefreshList(keepSelection: true);
            };
            _hardDropOnly.CheckedChanged += (_, _) => RefreshList(keepSelection: true);
            _showSwept.CheckedChanged += (_, _) => _view.ShowSwept = _showSwept.Checked;
            _list.SelectedIndexChanged += (_, _) => OnPlacementSelected();
            _view.CellClicked += (_, c) => { _board = _board.ToggleCell(c.X, c.Y); OnBoardChanged(); };
            _placeButton.Click += (_, _) => PlaceSelected();
            _clearButton.Click += (_, _) => { _board = Board.Empty; OnBoardChanged(); };
            _randomButton.Click += (_, _) => { _board = RandomBoard(); OnBoardChanged(); };

            _typeBox.SelectedIndex = (int)MinoType.T;
        }

        private void BuildLayout()
        {
            _view.Location = new Point(12, 12);

            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Location = new Point(_view.Right + 16, 8),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
            };

            var typeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            typeRow.Controls.Add(new Label { Text = "ミノ：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 0, 0) });
            typeRow.Controls.Add(_typeBox);

            var buttonRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            buttonRow.Controls.AddRange([_placeButton, _clearButton, _randomButton]);

            var legend = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                Text = "凡例：色付き＝ミノ位置／赤枠＝ミノ下位置（赤丸＝ブロックか床に接地）\n" +
                       "　　　ピンク＝盤面と重なっている／白い影＝通過領域\n" +
                       "盤面をクリックするとブロックを置く・消すができます。",
            };

            panel.Controls.AddRange([typeRow, _validOnly, _hardDropOnly, _showSwept,
                _list, _countLabel, _info, buttonRow, legend]);

            // タブ：配置ビューアー／最適手探索／ルート探索
            var viewerPage = new TabPage("配置ビューアー") { AutoScroll = true };
            viewerPage.Controls.Add(_view);
            viewerPage.Controls.Add(panel);

            var bestPage = new TabPage("最適手探索") { AutoScroll = true };
            bestPage.Controls.Add(new BestMovePage { Dock = DockStyle.Fill });

            var routePage = new TabPage("ルート探索") { AutoScroll = true };
            routePage.Controls.Add(new RoutePage { Dock = DockStyle.Fill });

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.AddRange([viewerPage, bestPage, routePage]);
            Controls.Add(tabs);

            ClientSize = new Size(1280, 760);
            MinimumSize = new Size(800, 600);
        }

        private MinoType CurrentType => (MinoType)(_typeBox.SelectedItem ?? MinoType.T);

        private void OnBoardChanged()
        {
            _view.Board = _board;
            RefreshList(keepSelection: true);
        }

        private void RefreshList(bool keepSelection)
        {
            var type = CurrentType;
            Placement? prev = keepSelection ? _list.SelectedItem as Placement? : null;

            Placement[] items = _validOnly.Checked
                ? PlacementFinder.ValidPlacements(_board, type, _hardDropOnly.Checked).ToArray()
                : PlacementTable.Get(type);

            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Items.AddRange(items.Cast<object>().ToArray());
            _list.EndUpdate();

            int nextCount = PlacementFinder.NextBoards(_board, type, _hardDropOnly.Checked && _validOnly.Checked).Count;
            _countLabel.Text =
                $"表示 {items.Length} 件 ／ {type} の全配置 {PlacementTable.Get(type).Length} 件（全ミノ合計 {PlacementTable.TotalCount} 件）\n" +
                $"この盤面から {type} で作れる次の盤面（重複除く）：{nextCount} 通り";

            int idx = prev is { } pv ? Array.IndexOf(items, pv) : -1;
            if (idx < 0 && items.Length > 0) idx = 0;
            _list.SelectedIndex = idx;
            if (idx < 0) OnPlacementSelected();
        }

        private void OnPlacementSelected()
        {
            if (_list.SelectedItem is not Placement p)
            {
                _view.Placement = null;
                _info.Text = "";
                return;
            }

            _view.Placement = p;
            Board support = _board | Board.Floor;
            bool overlap = p.Cells.Intersects(_board);
            bool grounded = p.Below.Intersects(support);
            bool reachable = !p.Swept.Intersects(_board);

            _info.Text = string.Join(Environment.NewLine,
                $"{p.Type}  回転 {p.Rotation}  位置 x={p.X} y={p.Y}  （占有行 y={p.MinY}～{p.MaxY}）",
                $"重ならない: {Yes(!overlap)}   接地: {Yes(grounded)}   上から到達: {Yes(reachable)}",
                $"→ 置ける: {Yes(!overlap && grounded)}" + (!overlap && grounded && !reachable ? "（ただし上から落とすだけでは入らない）" : ""),
                "",
                "ビット (D C B A の順、16進)",
                $"Cells : {p.Cells.ToHex()}",
                $"Below : {p.Below.ToHex()}",
                $"Swept : {p.Swept.ToHex()}");
        }

        private static string Yes(bool b) => b ? "○" : "×";

        private void PlaceSelected()
        {
            if (_list.SelectedItem is not Placement p) return;
            if (p.Cells.Intersects(_board))
            {
                MessageBox.Show(this, "盤面と重なっているので置けません。", Text);
                return;
            }
            if (!p.Below.Intersects(_board | Board.Floor))
            {
                MessageBox.Show(this, "接地していない（浮いている）配置です。", Text);
                return;
            }
            _board = _board.Place(p, out int cleared);
            OnBoardChanged();
            if (cleared > 0) _countLabel.Text += $"\n{cleared} ライン消去しました。";
        }

        private Board RandomBoard()
        {
            var rows = new ushort[Board.Height];
            int height = _rnd.Next(2, 10);
            for (int y = 0; y < height; y++)
            {
                // 下の方ほど埋まりやすく、各行に最低1つは穴を残す
                ushort row = 0;
                double density = 0.85 - y * 0.07;
                for (int x = 0; x < Board.Width; x++)
                    if (_rnd.NextDouble() < density) row |= (ushort)(1 << x);
                if (row == Board.FullRow) row &= (ushort)~(1 << _rnd.Next(Board.Width));
                rows[y] = row;
            }
            return Board.FromRows(rows);
        }
    }
}
