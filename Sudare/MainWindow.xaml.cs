using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Sudare.Models;
using Sudare.Services;
using Sudare.ViewModels;
using Sudare.Views;

namespace Sudare;

public partial class MainWindow : Window
{
    /// <summary>この行数を超える一括選択・コピーは確認してから行う。</summary>
    private const int BulkOperationThreshold = 200_000;

    public static readonly RoutedUICommand CopyWithLineNumbersCommand =
        new("行番号付きでコピー", nameof(CopyWithLineNumbersCommand), typeof(MainWindow));

    public static readonly RoutedUICommand FocusSearchCommand =
        new("検索ボックスへ", nameof(FocusSearchCommand), typeof(MainWindow));

    public static readonly RoutedUICommand FocusGoToCommand =
        new("行番号ボックスへ", nameof(FocusGoToCommand), typeof(MainWindow));

    public static readonly RoutedUICommand AboutCommand =
        new("バージョン情報", nameof(AboutCommand), typeof(MainWindow));

    public static readonly RoutedUICommand ToggleMarkerCommand =
        new("マーカーの ON/OFF", nameof(ToggleMarkerCommand), typeof(MainWindow));

    /// <summary>色を指定してマーカーを反転させる。CommandParameter は色番号（0 起点）。</summary>
    public static readonly RoutedUICommand ToggleMarkerColorCommand =
        new("色を指定してマーカーの ON/OFF", nameof(ToggleMarkerColorCommand), typeof(MainWindow));

    public static readonly RoutedUICommand ExpandAroundCommand =
        new("この行の前後を展開", nameof(ExpandAroundCommand), typeof(MainWindow));

    /// <summary>本文で選んだ文字列を「含む」に追加する。</summary>
    public static readonly RoutedUICommand AddSelectionToIncludeCommand =
        new("選択文字列を「含む」に追加", nameof(AddSelectionToIncludeCommand), typeof(MainWindow));

    /// <summary>本文で選んだ文字列を検索欄へ入れる。</summary>
    public static readonly RoutedUICommand SearchSelectionCommand =
        new("選択文字列で検索", nameof(SearchSelectionCommand), typeof(MainWindow));

    /// <summary>本文で選んだ文字列だけをコピーする（行のコピーとは別）。</summary>
    public static readonly RoutedUICommand CopySelectionTextCommand =
        new("選択文字列をコピー", nameof(CopySelectionTextCommand), typeof(MainWindow));

    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private ScrollViewer? _listScrollViewer;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        _viewModel = viewModel;
        _settings = settings;

        InitializeComponent();
        DataContext = viewModel;

        RestoreWindowPlacement();
        ApplyFilterPaneState();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ScrollRequested += OnScrollRequested;
        viewModel.FocusSearchRequested += () => FocusAndSelect(SearchBox);

        LineList.PreviewKeyDown += LineList_PreviewKeyDown;

        // ドラッグで文字を選ぶ。クリック（ドラッグなし）は従来どおり行選択のまま
        LineList.PreviewMouseLeftButtonDown += LineList_PreviewMouseLeftButtonDown;
        LineList.PreviewMouseMove += LineList_PreviewMouseMove;
        LineList.PreviewMouseLeftButtonUp += LineList_PreviewMouseLeftButtonUp;
    }

    #region ウィンドウ状態

    private void RestoreWindowPlacement()
    {
        if (_settings.WindowWidth > 200) Width = _settings.WindowWidth;
        if (_settings.WindowHeight > 200) Height = _settings.WindowHeight;

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            // 前回のモニタが外れている場合に画面外へ行かないようにする
            var area = SystemParameters.WorkArea;
            if (_settings.WindowLeft > area.Left - Width + 80 && _settings.WindowLeft < area.Right - 80 &&
                _settings.WindowTop >= area.Top - 40 && _settings.WindowTop < area.Bottom - 80)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }
        }

        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        if (_settings.FilterPaneWidth >= 240) FilterColumn.Width = new GridLength(_settings.FilterPaneWidth);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (bounds.Width > 0)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }

        if (_viewModel.FilterPaneVisible && FilterColumn.Width.IsAbsolute && FilterColumn.Width.Value >= 240)
        {
            _settings.FilterPaneWidth = FilterColumn.Width.Value;
        }

        _viewModel.SaveSettings();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FilterPaneVisible)) ApplyFilterPaneState();

        // 抽出をやり直すと行が作り直されるので、文字選択は持ち越さない
        if (e.PropertyName == nameof(MainViewModel.Lines)) SetTextSelectionRow(null);
    }

    private void ApplyFilterPaneState()
    {
        bool visible = _viewModel.FilterPaneVisible;

        if (!visible && FilterColumn.Width.IsAbsolute && FilterColumn.Width.Value >= 240)
        {
            _settings.FilterPaneWidth = FilterColumn.Width.Value;
        }

        FilterPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PaneSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FilterColumn.MinWidth = visible ? 240 : 0;
        FilterColumn.Width = visible
            ? new GridLength(Math.Max(240, _settings.FilterPaneWidth))
            : new GridLength(0);
    }

    #endregion

    #region スクロール / フォーカス

    private void OnScrollRequested(int viewIndex)
    {
        // 仮想化された ListBox では ScrollIntoView(item) が全件走査になりかねないので、
        // ScrollUnit=Item を前提に「行単位のオフセット」を直接指定する。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var scrollViewer = GetListScrollViewer();
            if (scrollViewer is null) return;

            double viewport = Math.Max(1, scrollViewer.ViewportHeight);
            double offset = scrollViewer.VerticalOffset;

            if (viewIndex >= offset + 1 && viewIndex < offset + viewport - 1) return;   // 既に見えている

            double target = viewIndex - Math.Floor(viewport / 3);
            scrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private ScrollViewer? GetListScrollViewer()
    {
        if (_listScrollViewer is not null) return _listScrollViewer;
        _listScrollViewer = FindDescendant<ScrollViewer>(LineList);
        return _listScrollViewer;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static void FocusAndSelect(TextBox box)
    {
        box.Focus();
        box.SelectAll();
    }

    private void FocusSearchExecuted(object sender, ExecutedRoutedEventArgs e) => FocusAndSelect(SearchBox);

    /// <summary>
    /// 行ジャンプ専用の入力欄は置かず、検索欄に <c>:</c> を入れて続きを打てる状態にする。
    /// </summary>
    private void FocusGoToExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!MainViewModel.IsLineJump(_viewModel.SearchText)) _viewModel.SearchText = ":";
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _viewModel.FindPreviousCommand.Execute(null);
        else _viewModel.FindNextCommand.Execute(null);
        e.Handled = true;
    }

    private void LineList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+A は全行を実体化してしまうため、行数が多いときは確認する
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (_viewModel.Lines.Count > BulkOperationThreshold && !ConfirmBulkOperation(_viewModel.Lines.Count))
            {
                e.Handled = true;
            }
        }
    }

    private static bool ConfirmBulkOperation(int count) =>
        MessageBox.Show($"{count:N0} 行を対象にします。時間とメモリを消費しますが続行しますか?\n" +
                        "（全体をファイルに書き出すだけなら「抽出結果を保存」のほうが高速です）",
                        "Sudare", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    #endregion

    #region マーカー / 前後の展開

    private void SelectionRequired(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = LineList.SelectedItems.Count > 0;

    private void ToggleMarkerExecuted(object sender, ExecutedRoutedEventArgs e) =>
        _viewModel.ToggleMarkers(SelectedLineNumbers());

    private void ToggleMarkerColorExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!MainViewModel.TryParseColorIndex(e.Parameter, out int colorIndex)) return;
        _viewModel.ToggleMarkerColor(SelectedLineNumbers(), colorIndex);
    }

    private void ExpandAroundExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!int.TryParse(e.Parameter as string, out int radius)) radius = 20;
        _viewModel.ExpandAround(SelectedLineNumbers(), radius);
    }

    private List<int> SelectedLineNumbers() =>
        LineList.SelectedItems.OfType<LineRow>().Select(r => r.LineNumber).ToList();

    #endregion

    #region 文字単位の選択

    /// <summary>いま文字選択を持っている行。選択は同時に 1 行だけ。</summary>
    private LineRow? _textSelectionRow;

    private LineRow? _dragRow;
    private TextBlock? _dragTextBlock;
    private int _dragAnchorOffset;
    private Point _dragOrigin;
    private bool _draggingText;

    private void LineList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingText = false;
        _dragRow = null;
        _dragTextBlock = null;

        var textBlock = FindContentTextBlock(e.OriginalSource as DependencyObject);
        if (textBlock?.DataContext is not LineRow row) return;

        _dragTextBlock = textBlock;
        _dragRow = row;
        _dragOrigin = e.GetPosition(LineList);
        _dragAnchorOffset = CharOffsetAt(textBlock, e.GetPosition(textBlock));
    }

    private void LineList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null || _dragTextBlock is null) return;

        if (!_draggingText)
        {
            // クリックと区別が付く距離まで動いてから、文字選択に切り替える
            var current = e.GetPosition(LineList);
            if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            _draggingText = true;
            SetTextSelectionRow(_dragRow);
            Mouse.Capture(LineList);
        }

        int offset = CharOffsetAt(_dragTextBlock, e.GetPosition(_dragTextBlock));
        SelectRange(_dragRow, _dragAnchorOffset, offset);
        e.Handled = true;   // ListBox 側の行ドラッグ選択を止める
    }

    private void LineList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingText)
        {
            Mouse.Capture(null);
            e.Handled = true;          // 行選択は変えない
        }
        else
        {
            SetTextSelectionRow(null); // ただのクリックなら文字選択は解除
        }

        _draggingText = false;
        _dragRow = null;
        _dragTextBlock = null;
    }

    private void SetTextSelectionRow(LineRow? row)
    {
        if (ReferenceEquals(_textSelectionRow, row)) return;
        _textSelectionRow?.ClearSelection();
        _textSelectionRow = row;
        CommandManager.InvalidateRequerySuggested();
    }

    private static void SelectRange(LineRow row, int anchor, int head)
    {
        int start = Math.Clamp(Math.Min(anchor, head), 0, row.Text.Length);
        int end = Math.Clamp(Math.Max(anchor, head), 0, row.Text.Length);
        row.SelectionStart = start;
        row.SelectionLength = end - start;
    }

    /// <summary>クリックされた場所から、本文の TextBlock を探す（行番号の TextBlock は対象外）。</summary>
    private static TextBlock? FindContentTextBlock(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TextBlock textBlock)
            {
                return TextHighlighter.GetSourceText(textBlock) is null ? null : textBlock;
            }
            if (source is ListBoxItem) return null;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>
    /// TextBlock 上の座標を、行テキストの文字位置に直す。
    /// </summary>
    /// <remarks>
    /// 強調のために本文は複数の Run に分かれているため、TextPointer の差をそのまま使うと
    /// 要素の境界まで数に入ってしまう。テキストの分だけを数える。
    /// </remarks>
    private static int CharOffsetAt(TextBlock textBlock, Point point)
    {
        var position = textBlock.GetPositionFromPoint(point, true);
        if (position is null) return 0;

        int count = 0;
        var p = textBlock.ContentStart;
        while (p is not null && p.CompareTo(position) < 0)
        {
            if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                int runLength = p.GetTextRunLength(LogicalDirection.Forward);
                var next = p.GetPositionAtOffset(runLength, LogicalDirection.Forward);
                if (next is not null && next.CompareTo(position) <= 0)
                {
                    count += runLength;
                    p = next;
                    continue;
                }
                count += Math.Max(0, p.GetOffsetToPosition(position));
                break;
            }
            p = p.GetNextContextPosition(LogicalDirection.Forward);
        }
        return count;
    }

    private void TextSelectionRequired(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = _textSelectionRow is { SelectionLength: > 0 };

    private void AddSelectionToIncludeExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        string text = _textSelectionRow?.SelectedText ?? string.Empty;
        if (text.Length > 0) _viewModel.AddIncludePattern(text);
    }

    private void SearchSelectionExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        string text = _textSelectionRow?.SelectedText ?? string.Empty;
        if (text.Length == 0) return;

        _viewModel.SearchText = text;
        FocusAndSelect(SearchBox);
    }

    private void CopySelectionTextExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        string text = _textSelectionRow?.SelectedText ?? string.Empty;
        if (text.Length == 0) return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"クリップボードにコピーできませんでした。\n{ex.Message}", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region コピー

    private void CopyCanExecute(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = LineList.SelectedItems.Count > 0;

    private void CopyExecuted(object sender, ExecutedRoutedEventArgs e) => CopySelection(false);

    private void CopyWithLineNumbersExecuted(object sender, ExecutedRoutedEventArgs e) => CopySelection(true);

    private void CopySelection(bool withLineNumbers)
    {
        var rows = LineList.SelectedItems.OfType<LineRow>().OrderBy(r => r.ViewIndex).ToList();
        if (rows.Count == 0) return;
        if (rows.Count > BulkOperationThreshold && !ConfirmBulkOperation(rows.Count)) return;

        int width = withLineNumbers ? rows[^1].LineNumberText.Length : 0;
        var builder = new StringBuilder(rows.Count * 80);
        foreach (var row in rows)
        {
            if (withLineNumbers)
            {
                builder.Append(row.LineNumberText.PadLeft(width)).Append(": ");
            }
            builder.AppendLine(row.Text);
        }

        try
        {
            Clipboard.SetText(builder.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"クリップボードにコピーできませんでした。\n{ex.Message}", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region ドラッグ＆ドロップ

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;

        string path = files[0];
        _ = ProjectService.IsProjectPath(path)
            ? _viewModel.OpenProjectAsync(path)
            : _viewModel.OpenAsync(path);
    }

    #endregion

    private void AboutExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "1.0";
        MessageBox.Show(
            $"Sudare（簾） {version}\n\n" +
            "見たい行だけを残して読む、ログ閲覧に特化したテキストビューアです。\n" +
            "含む語 / 除外語による行フィルタ、ワイルドカード・正規表現検索、\n" +
            "抽出結果の保存、折り返し表示、文字コード切り替えに対応しています。\n\n" +
            "旧名 LogFilterView。旧形式のプロジェクト (.lfvproj) は読み込みだけ対応しています。",
            "バージョン情報", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
