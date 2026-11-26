using TetrisAiTest.Core;

namespace TetrisAiTest;

/// <summary>画面間で共通の小物。</summary>
internal static class UiHelpers
{
    /// <summary>下の方ほど埋まりやすいランダム盤面（各行に最低1つ穴を残す）。</summary>
    public static Board RandomBoard(Random rnd, int minHeight = 2, int maxHeight = 9)
    {
        var rows = new ushort[Board.Height];
        int height = rnd.Next(minHeight, maxHeight + 1);
        for (int y = 0; y < height; y++)
        {
            ushort row = 0;
            double density = 0.85 - y * 0.07;
            for (int x = 0; x < Board.Width; x++)
                if (rnd.NextDouble() < density) row |= (ushort)(1 << x);
            if (row == Board.FullRow) row &= (ushort)~(1 << rnd.Next(Board.Width));
            rows[y] = row;
        }
        return Board.FromRows(rows);
    }

    /// <summary>ホールド欄の文字（空・"-" なら空）を読む。</summary>
    public static byte ParseHold(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text == "-") return GameState.NoHold;
        if (text.Length == 1 && Searcher.TryParseMino(text[0], out var t)) return (byte)t;
        throw new FormatException("ホールドは I O T S Z J L のいずれか1文字か、空欄にしてください。");
    }

    public static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 2), Font = new Font(Control.DefaultFont, FontStyle.Bold) };

    public static FlowLayoutPanel Column(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(6),
        };
        p.Controls.AddRange(controls);
        return p;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
        };
        p.Controls.AddRange(controls);
        return p;
    }

    public static FlowLayoutPanel Labeled(string label, Control control) =>
        Row(new Label { Text = label, AutoSize = false, Size = new Size(110, 23), TextAlign = ContentAlignment.MiddleLeft }, control);

    /// <summary>配置の作り方の選択欄（項目の順番は MoveGeneration と同じ）。</summary>
    public static ComboBox GenerationBox(MoveGeneration initial = MoveGeneration.HardDrop)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
        box.Items.AddRange(new object[] { "ハードドロップのみ（速い）", "SRS（ずらし・回転入れ・Tスピン）", "接地する全配置（到達性は無視）" });
        box.SelectedIndex = (int)initial;
        return box;
    }

    public static MoveGeneration Generation(ComboBox box) => (MoveGeneration)Math.Max(0, box.SelectedIndex);

    public static NumericUpDown Number(int min, int max, int value, int width = 90) =>
        new() { Minimum = min, Maximum = max, Value = value, Width = width, ThousandsSeparator = true };
}

/// <summary>
/// 探索中の表示：プログレスバー・進み具合・経過時間・中止ボタン。
/// 経過時間はUIのタイマーで更新するので、探索から通知がなくても動き続ける。
/// </summary>
internal sealed class SearchStatusPanel : FlowLayoutPanel
{
    private readonly ProgressBar _bar = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 250, Height = 14, Visible = false };
    private readonly Label _text = new() { AutoSize = true, MaximumSize = new Size(260, 0), Text = "待機中" };
    private readonly Button _cancel = new() { Text = "中止", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly System.Diagnostics.Stopwatch _sw = new();
    private CancellationTokenSource? _cts;
    private SearchProgress? _last;

    public SearchStatusPanel()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(3, 6, 3, 3);
        Controls.AddRange([_bar, _text, _cancel]);

        _timer.Tick += (_, _) => Render();
        _cancel.Click += (_, _) =>
        {
            _cts?.Cancel();
            _cancel.Enabled = false;
            _text.Text = "中止しています…";
        };
    }

    /// <summary>探索開始。返した CancellationToken を探索に渡す。</summary>
    public CancellationToken Start()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _last = null;
        _sw.Restart();
        _bar.Visible = true;
        _cancel.Enabled = true;
        _timer.Start();
        Render();
        return _cts.Token;
    }

    /// <summary>探索側からの進捗（Progress&lt;T&gt; 経由でUIスレッドで呼ばれる）。</summary>
    public void ShowProgress(SearchProgress p)
    {
        _last = p;
        Render();
    }

    public void Stop(string message)
    {
        _timer.Stop();
        _sw.Stop();
        _bar.Visible = false;
        _cancel.Enabled = false;
        _text.Text = $"{message}（{_sw.Elapsed.TotalSeconds:F1} 秒）";
    }

    private void Render()
    {
        if (_cts is { IsCancellationRequested: true }) return;
        string elapsed = $"経過 {_sw.Elapsed.TotalSeconds:F1} 秒";
        _text.Text = _last is { } p
            ? $"探索中… 深さ {p.Depth}/{p.MaxDepth}\n生成 {p.Expanded:N0}／局面 {p.States:N0}\n{elapsed}"
            : $"探索中…\n{elapsed}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _cts?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 最適手探索：
/// ・盤面とネクストから、評価値が最も高くなる手順を探す（「最適手を探す」）
/// ・最短で目標（シングル〜テトリス／Tスピン／全消し）を達成する手順を探す（目標ボタン）
/// </summary>
public sealed class BestMovePage : UserControl
{
    private Board _board = Board.Empty;
    private readonly Random _rnd = new();

    private readonly BoardView _view = new() { CellSize = 20 };
    private readonly TextBox _next = new() { Width = 220, Text = "TIOLJSZTIOLJSZ" };
    private readonly TextBox _hold = new() { Width = 40 };
    private readonly NumericUpDown _depth = UiHelpers.Number(1, 50, 6);
    private readonly NumericUpDown _beam = UiHelpers.Number(1, 100000, 300);
    private readonly NumericUpDown _goalDepth = UiHelpers.Number(1, 60, 14);
    private readonly NumericUpDown _goalBeam = UiHelpers.Number(1, 100000, 1000);
    private readonly CheckBox _useHold = new() { Text = "ホールドを使う", AutoSize = true, Checked = true };
    private readonly ComboBox _gen = UiHelpers.GenerationBox();
    private readonly Button _search = new() { Text = "最適手を探す", AutoSize = true };
    private readonly List<Button> _goalButtons = [];
    private readonly SearchStatusPanel _status = new();
    private readonly SearchResultView _result = new();

    public BestMovePage()
    {
        var clear = new Button { Text = "盤面クリア", AutoSize = true };
        var random = new Button { Text = "ランダム盤面", AutoSize = true };
        var bag = new Button { Text = "7種1巡でランダム生成", AutoSize = true };

        // 最短で目標を達成する経路のボタン
        var goals = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            Width = 260, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
        };
        foreach (var goal in Enum.GetValues<SearchGoal>())
        {
            var b = new Button { Text = "最短" + GoalRules.Name(goal), Width = 120, Height = 28 };
            b.Click += async (_, _) => await RunGoalAsync(goal);
            _goalButtons.Add(b);
            goals.Controls.Add(b);
        }

        var left = UiHelpers.Column(
            UiHelpers.Caption("開始盤面（クリックで編集）"), _view, UiHelpers.Row(clear, random));

        var settings = UiHelpers.Column(
            UiHelpers.Caption("共通の条件"),
            UiHelpers.Labeled("ネクスト", _next), bag,
            UiHelpers.Labeled("ホールド", _hold),
            _useHold, UiHelpers.Caption("配置の作り方"), _gen,
            UiHelpers.Caption("最短で目標を達成する経路"),
            UiHelpers.Labeled("最大手数", _goalDepth),
            UiHelpers.Labeled("ビーム幅", _goalBeam),
            goals,
            UiHelpers.Caption("最適手（評価値が最大）"),
            UiHelpers.Labeled("読む深さ（手）", _depth),
            UiHelpers.Labeled("ビーム幅", _beam),
            _search,
            _status,
            new Label
            {
                AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Color.DimGray,
                Text = "ネクストは「TIOLJSZ」のように並べて入力します。ホールドは1文字（空欄なら空）。\n" +
                       "目標ボタンは、その目標を最も少ない手数で達成する手順を表示します。\n" +
                       "Tスピンは自動的に SRS で探します（Mini は対象外）。",
            });

        var right = UiHelpers.Column(UiHelpers.Caption("結果（←→キーで1手ずつ）"), _result);

        var root = UiHelpers.Row(left, settings, right);
        root.Location = new Point(4, 4);
        Controls.Add(root);
        AutoScroll = true;

        _view.CellClicked += (_, c) => { _board = _board.ToggleCell(c.X, c.Y); _view.Board = _board; };
        clear.Click += (_, _) => { _board = Board.Empty; _view.Board = _board; };
        random.Click += (_, _) => { _board = UiHelpers.RandomBoard(_rnd); _view.Board = _board; };
        bag.Click += (_, _) => _next.Text = Searcher.RandomBagQueue(_rnd, 2);
        _search.Click += async (_, _) =>
        {
            var options = CommonOptions((int)_depth.Value, (int)_beam.Value); // 画面の値はUIスレッドで読む
            await RunAsync((start, queue, progress, ct) => Searcher.FindBest(start, queue, options, progress, ct));
        };
    }

    private Task RunGoalAsync(SearchGoal goal)
    {
        var options = CommonOptions((int)_goalDepth.Value, (int)_goalBeam.Value);
        return RunAsync((start, queue, progress, ct) => Searcher.FindGoal(start, queue, goal, options, progress, ct));
    }

    private SearchOptions CommonOptions(int depth, int beam) => new()
    {
        Depth = depth,
        BeamWidth = beam,
        UseHold = _useHold.Checked,
        Generation = UiHelpers.Generation(_gen),
    };

    private delegate SearchResult SearchFunc(GameState start, byte[] queue,
        IProgress<SearchProgress> progress, CancellationToken ct);

    private async Task RunAsync(SearchFunc search)
    {
        byte[] queue;
        byte hold;
        try
        {
            queue = Searcher.ParseQueue(_next.Text);
            hold = UiHelpers.ParseHold(_hold.Text);
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, "入力エラー");
            return;
        }

        var start = new GameState(_board, hold, 0, 0, false);
        var board = _board;

        SetBusy(true);
        _result.Clear("探索中…");
        var ct = _status.Start();
        var progress = new Progress<SearchProgress>(_status.ShowProgress);
        try
        {
            var result = await Task.Run(() => search(start, queue, progress, ct), ct);
            _status.Stop(result.Found ? "完了" : "見つかりませんでした");
            _result.ShowResult(board, result);
        }
        catch (OperationCanceledException)
        {
            _status.Stop("中止しました");
            _result.Clear("中止しました。");
        }
        catch (Exception ex)
        {
            _status.Stop("エラー");
            _result.Clear("エラー：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _search.Enabled = !busy;
        foreach (var b in _goalButtons) b.Enabled = !busy;
    }
}

/// <summary>
/// ルート探索：
/// ・開始盤面から目標盤面にたどり着く手順を探す
/// ・ワンボタンで「最短シングル／ダブル／トリプル／テトリス／Tスピン／全消し」を探す
/// </summary>
public sealed class RoutePage : UserControl
{
    private Board _start = Board.Empty;
    private Board _target = Board.Empty;
    private readonly Random _rnd = new();

    private readonly BoardView _startView = new() { CellSize = 17 };
    private readonly BoardView _targetView = new() { CellSize = 17 };
    private readonly TextBox _next = new() { Width = 200, Text = "TIOLJSZ" };
    private readonly TextBox _hold = new() { Width = 40 };
    private readonly NumericUpDown _depth = UiHelpers.Number(1, 60, 12);
    private readonly NumericUpDown _limit = UiHelpers.Number(1000, 100_000_000, 2_000_000, 110);
    private readonly NumericUpDown _beam = UiHelpers.Number(1, 100_000, 1000);
    private readonly CheckBox _allowClears = new() { Text = "途中のライン消去を許可（目標盤面）", AutoSize = true };
    private readonly CheckBox _useHold = new() { Text = "ホールドを使う", AutoSize = true, Checked = true };
    private readonly ComboBox _gen = UiHelpers.GenerationBox();
    private readonly Button _search = new() { Text = "目標盤面へのルートを探す", AutoSize = true };
    private readonly List<Button> _goalButtons = [];
    private readonly SearchStatusPanel _status = new();
    private readonly SearchResultView _result = new();

    public RoutePage()
    {
        var startClear = new Button { Text = "クリア", AutoSize = true };
        var startRandom = new Button { Text = "ランダム", AutoSize = true };
        var toTarget = new Button { Text = "目標へコピー →", AutoSize = true };
        var targetClear = new Button { Text = "クリア（パフェ）", AutoSize = true };
        var fromStart = new Button { Text = "← 開始をコピー", AutoSize = true };
        var bag = new Button { Text = "7種1巡でランダム生成", AutoSize = true };

        var boards = UiHelpers.Row(
            UiHelpers.Column(UiHelpers.Caption("開始盤面"), _startView, UiHelpers.Row(startClear, startRandom), toTarget),
            UiHelpers.Column(UiHelpers.Caption("目標盤面"), _targetView, UiHelpers.Row(targetClear, fromStart)));

        // 目標のワンボタン検索
        var goals = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            Width = 260, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
        };
        foreach (var goal in Enum.GetValues<SearchGoal>())
        {
            var b = new Button { Text = "最短" + GoalRules.Name(goal), Width = 120, Height = 28, Tag = goal };
            b.Click += async (_, _) => await RunGoalAsync(goal);
            _goalButtons.Add(b);
            goals.Controls.Add(b);
        }

        var settingsA = UiHelpers.Column(
            UiHelpers.Caption("条件"),
            UiHelpers.Labeled("ネクスト", _next), bag,
            UiHelpers.Labeled("ホールド", _hold),
            UiHelpers.Labeled("最大手数", _depth),
            UiHelpers.Labeled("ビーム幅（目標）", _beam),
            UiHelpers.Labeled("局面上限（盤面）", _limit),
            _useHold,
            UiHelpers.Caption("配置の作り方"), _gen);

        var settingsB = UiHelpers.Column(
            UiHelpers.Caption("ワンボタン検索"), goals,
            UiHelpers.Caption("目標盤面への検索"), _allowClears, _search,
            _status,
            new Label
            {
                AutoSize = true, MaximumSize = new Size(260, 0), ForeColor = Color.DimGray,
                Text = "ワンボタン検索は開始盤面から探します（目標盤面は使いません）。\n" +
                       "Tスピンは自動的に SRS で探します（Mini は対象外）。\n" +
                       "見つからないときは最大手数・ビーム幅を増やしてください。",
            });

        var left = UiHelpers.Column(boards, UiHelpers.Row(settingsA, settingsB));
        var right = UiHelpers.Column(UiHelpers.Caption("結果（←→キーで1手ずつ）"), _result);

        var root = UiHelpers.Row(left, right);
        root.Location = new Point(4, 4);
        Controls.Add(root);
        AutoScroll = true;

        _startView.CellClicked += (_, c) => SetStart(_start.ToggleCell(c.X, c.Y));
        _targetView.CellClicked += (_, c) => SetTarget(_target.ToggleCell(c.X, c.Y));
        startClear.Click += (_, _) => SetStart(Board.Empty);
        startRandom.Click += (_, _) => SetStart(UiHelpers.RandomBoard(_rnd, 1, 5));
        toTarget.Click += (_, _) => SetTarget(_start);
        targetClear.Click += (_, _) => SetTarget(Board.Empty);
        fromStart.Click += (_, _) => SetStart(_target);
        bag.Click += (_, _) => _next.Text = Searcher.RandomBagQueue(_rnd, 2);
        _search.Click += async (_, _) => await RunPathAsync();
    }

    private void SetStart(Board b) { _start = b; _startView.Board = b; }
    private void SetTarget(Board b) { _target = b; _targetView.Board = b; }

    private SearchOptions Options() => new()
    {
        Depth = (int)_depth.Value,
        NodeLimit = (int)_limit.Value,
        BeamWidth = (int)_beam.Value,
        AllowLineClears = _allowClears.Checked,
        UseHold = _useHold.Checked,
        Generation = UiHelpers.Generation(_gen),
    };

    private Task RunPathAsync()
    {
        var target = _target;
        return RunAsync((start, queue, o, progress, ct) => Searcher.FindPath(start, queue, target, o, progress, ct));
    }

    private Task RunGoalAsync(SearchGoal goal) =>
        RunAsync((start, queue, o, progress, ct) => Searcher.FindGoal(start, queue, goal, o, progress, ct));

    private delegate SearchResult SearchFunc(GameState start, byte[] queue, SearchOptions options,
        IProgress<SearchProgress> progress, CancellationToken ct);

    private async Task RunAsync(SearchFunc search)
    {
        byte[] queue;
        byte hold;
        try
        {
            queue = Searcher.ParseQueue(_next.Text);
            hold = UiHelpers.ParseHold(_hold.Text);
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, "入力エラー");
            return;
        }

        var options = Options();
        var start = new GameState(_start, hold, 0, 0, false);
        var startBoard = _start;

        SetBusy(true);
        _result.Clear("探索中…");
        var ct = _status.Start();
        var progress = new Progress<SearchProgress>(_status.ShowProgress);
        try
        {
            var result = await Task.Run(() => search(start, queue, options, progress, ct), ct);
            _status.Stop(result.Found ? "完了" : "見つかりませんでした");
            _result.ShowResult(startBoard, result);
        }
        catch (OperationCanceledException)
        {
            _status.Stop("中止しました");
            _result.Clear("中止しました。");
        }
        catch (Exception ex)
        {
            _status.Stop("エラー");
            _result.Clear("エラー：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _search.Enabled = !busy;
        foreach (var b in _goalButtons) b.Enabled = !busy;
    }
}
