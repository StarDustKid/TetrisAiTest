using TetrisAiTest.Core;

namespace TetrisAiTest;

/// <summary>
/// 探索結果の表示：手順の一覧と、選んだ手の再生盤面。
/// 一覧の先頭は「開始盤面」、途中は「置く前の盤面＋置くミノ」、最後は「最終盤面」。
/// </summary>
public sealed class SearchResultView : UserControl
{
    private readonly BoardView _replay = new() { CellSize = 20, Editable = false };
    private readonly Label _summary = new() { AutoSize = true, MaximumSize = new Size(440, 0) };
    private readonly ListBox _steps = new()
    {
        Width = 440, Height = 330, Font = new Font("Consolas", 9.5f), IntegralHeight = false,
        HorizontalScrollbar = true,
    };
    private readonly Label _detail = new() { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = Color.DimGray };

    private Board _start;
    private SearchResult? _result;

    public SearchResultView()
    {
        var right = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        right.Controls.AddRange([_summary, _steps, _detail]);

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(0, 0),
        };
        row.Controls.AddRange([_replay, right]);

        Controls.Add(row);
        // AutoSize に頼ると大きさが 0 になることがあるので固定サイズにする
        AutoSize = false;
        Size = new Size(720, 560);
        MinimumSize = Size;

        _steps.SelectedIndexChanged += (_, _) => ShowSelected();
        _steps.KeyDown += (_, e) =>
        {
            // 左右キーでも1手ずつ進める・戻す
            if (e.KeyCode == Keys.Right && _steps.SelectedIndex < _steps.Items.Count - 1) { _steps.SelectedIndex++; e.Handled = true; }
            if (e.KeyCode == Keys.Left && _steps.SelectedIndex > 0) { _steps.SelectedIndex--; e.Handled = true; }
        };
        Clear("（まだ探索していません）");
    }

    public void Clear(string message)
    {
        _result = null;
        _summary.Text = message;
        _steps.Items.Clear();
        _detail.Text = "";
        _replay.Board = Board.Empty;
        _replay.Placement = null;
    }

    public void ShowResult(Board start, SearchResult result)
    {
        _start = start;
        _result = result;

        _summary.Text =
            (result.Found ? "✔ " : "✘ ") + result.Message + Environment.NewLine +
            $"手数 {result.Steps.Count}／消去 {result.TotalCleared} 行／攻撃 {result.TotalAttack} 行" +
            (result.Found && result.Score != 0 ? $"／評価値 {result.Score:F1}" : "") + Environment.NewLine +
            $"生成した子 {result.Expanded:N0}／残した局面 {result.States:N0}／{result.Elapsed.TotalMilliseconds:N0} ms";

        _steps.BeginUpdate();
        _steps.Items.Clear();
        _steps.Items.Add("  0: 開始盤面");
        foreach (var s in result.Steps)
        {
            string hold = s.HoldAfter == GameState.NoHold ? "-" : ((MinoType)s.HoldAfter).ToString();
            _steps.Items.Add(
                $"{s.Number,3}: {s.Move.Placement}{(s.Move.Hold ? " [H]" : "    ")}" +
                $"  消{s.Cleared} 攻{s.Attack} 消去後{s.SinceClear,2}手 Hold={hold}" +
                (s.Move.Spin != SpinKind.None ? $"  ★{s.Move.SpinLabel}" : "") +
                (s.Cleared > 0 && s.After.IsZero ? "  ★全消し" : ""));
        }
        if (result.Steps.Count > 0) _steps.Items.Add("     最終盤面");
        _steps.EndUpdate();

        // 最適手探索なら「次の1手」が分かるように1手目を選んでおく
        _steps.SelectedIndex = result.Steps.Count > 0 ? 1 : 0;
    }

    private void ShowSelected()
    {
        int i = _steps.SelectedIndex;
        if (_result is null || i < 0) return;

        var steps = _result.Steps;
        if (i == 0)
        {
            _replay.Board = _start;
            _replay.Placement = null;
            _detail.Text = "開始盤面";
        }
        else if (i <= steps.Count)
        {
            var s = steps[i - 1];
            _replay.Board = s.Before;
            _replay.Placement = s.Move.Placement;
            _detail.Text =
                $"{s.Number} 手目：{s.Move.Placement}{(s.Move.Hold ? "（ホールドを使用）" : "")}" +
                (s.Move.Spin != SpinKind.None ? $"　{s.Move.SpinLabel}" : "") + Environment.NewLine +
                $"この手で {s.Cleared} 行消去、{s.Attack} 行送る。最後の消去から {s.SinceClear} 手。" + Environment.NewLine +
                $"手の記録（16ビット）：0x{s.Move.Raw:X4}";
        }
        else
        {
            _replay.Board = steps.Count > 0 ? steps[^1].After : _start;
            _replay.Placement = null;
            _detail.Text = "最終盤面　ミノ譜：" + string.Join(" ", steps.Select(s => $"{s.Move.Raw:X4}"));
        }
    }
}
