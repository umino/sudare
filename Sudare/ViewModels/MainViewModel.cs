using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Sudare.Models;
using Sudare.Services;
using Sudare.Views;
using Microsoft.Win32;

namespace Sudare.ViewModels;

/// <summary>
/// 「最近使ったファイル」メニュー 1 項目。ポップアップ内の MenuItem からは
/// RelativeSource で ViewModel を辿れないため、コマンドを項目自身に持たせている。
/// </summary>
public sealed class RecentFileItem
{
    public RecentFileItem(string path, System.Windows.Input.ICommand command)
    {
        Path = path;
        Command = command;

        string name = System.IO.Path.GetFileName(path);
        string? directory = System.IO.Path.GetDirectoryName(path);
        string label = string.IsNullOrEmpty(directory) ? name : $"{name}  —  {directory}";
        // メニューでは _ がアクセスキー指定になるのでエスケープする
        DisplayPath = label.Replace("_", "__");
    }

    public string Path { get; }
    public string DisplayPath { get; }
    public System.Windows.Input.ICommand Command { get; }
}

/// <summary>マーカー一覧の 1 項目。行番号と、行頭（先頭の空白を除く）の抜粋を持つ。</summary>
public sealed class MarkerItem
{
    public MarkerItem(int lineNumber, string preview, int colorIndex)
    {
        LineNumber = lineNumber;
        Preview = preview;
        ColorIndex = colorIndex;
        Color = MarkerPalette.Get(colorIndex);
    }

    /// <summary>元ファイル上の行番号（1 基点）。</summary>
    public int LineNumber { get; }

    public string Preview { get; }

    /// <summary>パレット上の色番号。</summary>
    public int ColorIndex { get; }

    /// <summary>一覧に出す色見本。ログ本文側の帯と同じ色を指す。</summary>
    public MarkerColor Color { get; }

    public string Display => $"{LineNumber:N0}: {Preview}";

    public override string ToString() => Display;
}

public sealed class MainViewModel : ObservableObject
{
    private const int MaxRecentFiles = 12;
    private const int MaxContextLines = 1000;
    private const int MarkerPreviewLength = 160;
    private const string ClipboardDisplayName = "クリップボード";

    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _debounceTimer;

    private LogDocument _document = LogDocument.Empty;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _filterCts;
    private bool _initializing;

    /// <summary>直近の照合結果。前後行数の変更や個別展開はこれを使い回して再照合を避ける。</summary>
    private bool[]? _hits;
    private int _hitCount;

    private readonly List<LineRange> _expansions = new();

    private static int CountHits(bool[]? hits, int lineCount)
    {
        if (hits is null) return lineCount;
        int count = 0;
        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i]) count++;
        }
        return count;
    }

    public MainViewModel(SettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;

        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = ApplyFilterAsync();
        };

        Encodings = new ObservableCollection<EncodingChoice>(TextEncodings.All);
        Presets = new ObservableCollection<FilterPreset>(settings.Presets);
        RecentFiles = new ObservableCollection<RecentFileItem>();

        Toolbar = new ToolbarVisibility(settings.ToolbarItems.Count > 0 ? settings.ToolbarItems : ToolbarCatalog.Default);
        ToolbarMenu = new ObservableCollection<ToolbarMenuEntry>(
            ToolbarCatalog.Items.Select(i => new ToolbarMenuEntry(i, Toolbar)));

        OpenCommand = new AsyncRelayCommand(OpenWithDialogAsync);
        OpenRecentCommand = new AsyncRelayCommand(p => OpenAsync(p as string ?? string.Empty));
        PasteFromClipboardCommand = new AsyncRelayCommand(LoadFromClipboardAsync);
        foreach (var path in settings.RecentFiles) RecentFiles.Add(new RecentFileItem(path, OpenRecentCommand));
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => HasDocument);
        CloseFileCommand = new RelayCommand(CloseFile, () => HasDocument);
        ApplyFilterCommand = new AsyncRelayCommand(() => ApplyFilterAsync());
        ClearFilterCommand = new RelayCommand(ClearFilter);
        ExportCommand = new AsyncRelayCommand(() => ExportAsync(false), () => HasDocument);
        ExportWithLineNumbersCommand = new AsyncRelayCommand(() => ExportAsync(true), () => HasDocument);
        CancelCommand = new RelayCommand(CancelRunningWork, () => IsBusy);
        FindNextCommand = new RelayCommand(() => Find(true));
        FindPreviousCommand = new RelayCommand(() => Find(false));
        GoToLineCommand = new RelayCommand(GoToLine);
        ToggleWordWrapCommand = new RelayCommand(() => WordWrap = !WordWrap);
        ToggleLineNumbersCommand = new RelayCommand(() => ShowLineNumbers = !ShowLineNumbers);
        IncreaseFontCommand = new RelayCommand(() => FontSize = Math.Min(48, FontSize + 1));
        DecreaseFontCommand = new RelayCommand(() => FontSize = Math.Max(6, FontSize - 1));
        ResetFontCommand = new RelayCommand(() => FontSize = 13);
        RemovePatternCommand = new RelayCommand(RemovePattern);
        SavePresetCommand = new RelayCommand(SavePreset);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => SelectedPreset is not null);
        OpenProjectCommand = new AsyncRelayCommand(OpenProjectWithDialogAsync);
        SaveProjectCommand = new RelayCommand(SaveProject, CanSaveProject);
        SaveProjectAsCommand = new RelayCommand(SaveProjectAs, CanSaveProject);
        ClearExpansionsCommand = new RelayCommand(ClearExpansions, () => _expansions.Count > 0);
        ClearMarkersCommand = new RelayCommand(ClearMarkers, () => _markedLines.Count > 0);
        RemoveMarkerCommand = new RelayCommand(RemoveSelectedMarker, () => SelectedMarker is not null);
        ChangeMarkerColorCommand = new RelayCommand(
            p => { if (TryParseColorIndex(p, out int index)) ChangeSelectedMarkerColor(index); },
            _ => SelectedMarker is not null);
        ExitCommand = new RelayCommand(() => Application.Current.Shutdown());

        LoadFromSettings();
        RebuildIncludePatterns();   // 設定が空文字だと setter が素通りするので、ここで必ず組む
        SyncSelectedPresetWithConditions();
        UpdateView(LogDocument.Empty);
    }

    #region コマンド

    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand OpenRecentCommand { get; }
    public AsyncRelayCommand PasteFromClipboardCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public RelayCommand CloseFileCommand { get; }
    public AsyncRelayCommand ApplyFilterCommand { get; }
    public RelayCommand ClearFilterCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public AsyncRelayCommand ExportWithLineNumbersCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand FindNextCommand { get; }
    public RelayCommand FindPreviousCommand { get; }
    public RelayCommand GoToLineCommand { get; }
    public RelayCommand ToggleWordWrapCommand { get; }
    public RelayCommand ToggleLineNumbersCommand { get; }
    public RelayCommand IncreaseFontCommand { get; }
    public RelayCommand DecreaseFontCommand { get; }
    public RelayCommand ResetFontCommand { get; }
    public RelayCommand RemovePatternCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public AsyncRelayCommand OpenProjectCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand SaveProjectAsCommand { get; }
    public RelayCommand ClearExpansionsCommand { get; }
    public RelayCommand ClearMarkersCommand { get; }
    public RelayCommand RemoveMarkerCommand { get; }
    public RelayCommand ChangeMarkerColorCommand { get; }
    public RelayCommand ExitCommand { get; }

    #endregion

    #region ビューへの通知

    /// <summary>指定した表示位置までスクロールさせる。</summary>
    public event Action<int>? ScrollRequested;

    /// <summary>検索ボックスへフォーカスを移す。</summary>
    public event Action? FocusSearchRequested;

    #endregion

    public ViewSettings View { get; } = new();
    public ObservableCollection<EncodingChoice> Encodings { get; }
    public ObservableCollection<FilterPreset> Presets { get; }
    public ObservableCollection<RecentFileItem> RecentFiles { get; }

    /// <summary>ツールバーに出している項目。XAML からは <c>Toolbar[highlight]</c> のように引く。</summary>
    public ToolbarVisibility Toolbar { get; }

    /// <summary>ツールバーを右クリックしたときに出す、出し入れのチェックリスト。</summary>
    public ObservableCollection<ToolbarMenuEntry> ToolbarMenu { get; }

    #region フィルタ条件

    private string _includeText = string.Empty;
    public string IncludeText
    {
        get => _includeText;
        set
        {
            if (!SetProperty(ref _includeText, value)) return;
            // リスト側の編集が発端のときは、書き戻した文字列で組み直すと入力中の行が作り直されてしまう
            if (!_syncingPatterns) RebuildIncludePatterns();
            OnFilterConditionChanged();
        }
    }

    private string _excludeText = string.Empty;
    public string ExcludeText
    {
        get => _excludeText;
        set
        {
            if (!SetProperty(ref _excludeText, value)) return;
            // 設定やプロジェクトから中身が入ってきたら開く。畳むのは利用者の操作にまかせる
            if (value.Length > 0) ExcludeExpanded = true;
            OnFilterConditionChanged();
        }
    }

    private bool _excludeExpanded;

    /// <summary>
    /// 「除外」欄を開いているか。使うまでは 1 行に畳んでおき、縦を「含む」とマーカーに回す。
    /// </summary>
    public bool ExcludeExpanded
    {
        get => _excludeExpanded;
        set => SetProperty(ref _excludeExpanded, value);
    }

    private MatchMode _mode = MatchMode.Plain;
    public MatchMode Mode
    {
        get => _mode;
        set { if (SetProperty(ref _mode, value)) { OnPropertyChanged(nameof(ModeHint)); OnFilterConditionChanged(); } }
    }

    private bool _caseSensitive;
    public bool CaseSensitive
    {
        get => _caseSensitive;
        set { if (SetProperty(ref _caseSensitive, value)) OnFilterConditionChanged(); }
    }

    private LogicMode _includeLogic = LogicMode.Or;
    public LogicMode IncludeLogic
    {
        get => _includeLogic;
        set { if (SetProperty(ref _includeLogic, value)) OnFilterConditionChanged(); }
    }

    private LogicMode _excludeLogic = LogicMode.Or;
    public LogicMode ExcludeLogic
    {
        get => _excludeLogic;
        set { if (SetProperty(ref _excludeLogic, value)) OnFilterConditionChanged(); }
    }

    private bool _includeHighlightOnly;

    /// <summary>
    /// 「含む」を行の絞り込みには使わず、強調表示だけに使うか。
    /// </summary>
    /// <remarks>
    /// ON のあいだは全行が表示され、含む語は色が付くだけになる。
    /// 「除外」はそのまま効くので、ノイズだけ落とした全文を眺めながら
    /// 注目したい語を色で追う、という読み方ができる。
    /// </remarks>
    public bool IncludeHighlightOnly
    {
        get => _includeHighlightOnly;
        set { if (SetProperty(ref _includeHighlightOnly, value)) OnFilterConditionChanged(); }
    }

    private bool _autoApply = true;
    public bool AutoApply
    {
        get => _autoApply;
        set { if (SetProperty(ref _autoApply, value) && value) OnFilterConditionChanged(); }
    }

    public string ModeHint => Mode switch
    {
        MatchMode.Plain => "単純な部分一致（1 行に 1 パターン、# 始まりはコメント）",
        MatchMode.Wildcard => "* は 0 文字以上、? は任意の 1 文字。部分一致で判定します",
        MatchMode.Regex => ".NET 正規表現。部分一致で判定します（^ $ でアンカー可）",
        _ => string.Empty,
    };

    private string? _filterError;
    public string? FilterError
    {
        get => _filterError;
        private set => SetProperty(ref _filterError, value);
    }

    #endregion

    #region 含む条件のリスト表示

    /// <summary>
    /// 「含む」の各行。<see cref="IncludeText"/> を行ごとに分解したもので、両者は常に同じ内容を指す。
    /// </summary>
    public ObservableCollection<PatternLine> IncludePatterns { get; } = new();

    private bool _syncingPatterns;

    private bool _includeAsText;

    /// <summary>「含む」をリストではなく素のテキストとして編集するか（まとめて貼り付けたいとき用）。</summary>
    public bool IncludeAsText
    {
        get => _includeAsText;
        set => SetProperty(ref _includeAsText, value);
    }

    /// <summary><see cref="IncludeText"/> からリストを組み直す。</summary>
    private void RebuildIncludePatterns()
    {
        // テキスト欄での編集は 1 文字ごとにここを通るので、選んだ色は引き継がないと消えてしまう
        var previous = IncludePatterns.Where(p => !p.IsBlank).ToList();

        foreach (var line in IncludePatterns) line.PropertyChanged -= OnPatternLineChanged;
        IncludePatterns.Clear();

        var rebuilt = new List<PatternLine>();
        foreach (var raw in _includeText.Split('\n'))
        {
            var trimmed = raw.Trim('\r', ' ', '\t');
            if (trimmed.Length == 0) continue;

            bool enabled = trimmed[0] != '#';
            string text = enabled ? trimmed : trimmed[1..].TrimStart();
            rebuilt.Add(new PatternLine(text, enabled));
        }

        // 購読前に色を入れておく（ここで変更通知が走ると、組み立て途中のリストを書き戻してしまう）
        CarryOverChosenColors(previous, rebuilt.Where(p => !p.IsBlank).ToList());

        foreach (var line in rebuilt)
        {
            line.PropertyChanged += OnPatternLineChanged;
            IncludePatterns.Add(line);
        }

        EnsureTrailingBlank();
        AssignPatternColors();
    }

    /// <summary>
    /// 組み直す前の行で選ばれていた色を、組み直した行へ引き継ぐ。
    /// </summary>
    /// <remarks>
    /// まずは同じ文言の行へ渡す。行を挿入・削除して位置がずれても、色がパターンに付いて回る。
    /// 文言で見つからない行は、行数が変わっていないときに限り同じ位置から受け取る
    /// （1 行の文言を書き換えている途中なので、その行の色を保つ）。
    /// </remarks>
    private static void CarryOverChosenColors(List<PatternLine> previous, List<PatternLine> rebuilt)
    {
        if (!previous.Any(p => p.ChosenColorIndex is not null)) return;

        var taken = new bool[previous.Count];
        var unmatched = new List<int>();

        for (int i = 0; i < rebuilt.Count; i++)
        {
            int match = -1;
            for (int j = 0; j < previous.Count; j++)
            {
                if (taken[j] || !string.Equals(previous[j].Text, rebuilt[i].Text, StringComparison.Ordinal)) continue;
                match = j;
                break;
            }

            if (match < 0)
            {
                unmatched.Add(i);
                continue;
            }
            taken[match] = true;
            rebuilt[i].ChosenColorIndex = previous[match].ChosenColorIndex;
        }

        if (previous.Count != rebuilt.Count) return;
        foreach (int i in unmatched)
        {
            if (!taken[i]) rebuilt[i].ChosenColorIndex = previous[i].ChosenColorIndex;
        }
    }

    /// <summary>入力するそばから行が足りなくなることのないよう、末尾に空行を 1 つだけ残しておく。</summary>
    private void EnsureTrailingBlank()
    {
        // 消した行がそのまま溜まっていかないよう、末尾の余分な空行は畳む。
        // 落とすのは常に最後の 1 行なので、いま編集している行からフォーカスが外れることはない。
        while (IncludePatterns.Count >= 2 && IncludePatterns[^1].IsBlank && IncludePatterns[^2].IsBlank)
        {
            IncludePatterns[^1].PropertyChanged -= OnPatternLineChanged;
            IncludePatterns.RemoveAt(IncludePatterns.Count - 1);
        }

        if (IncludePatterns.Count > 0 && IncludePatterns[^1].IsBlank) return;

        var blank = new PatternLine(string.Empty, true);
        blank.PropertyChanged += OnPatternLineChanged;
        IncludePatterns.Add(blank);
    }

    private void OnPatternLineChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PatternLine.Color)) return;

        if (e.PropertyName == nameof(PatternLine.ChosenColorIndex))
        {
            // 色は抽出結果を変えないので、照合はやり直さずに本文の色だけ塗り直す
            AssignPatternColors();
            UpdateHighlight();
            // プリセットやプロジェクトを読み込んでいる途中で合わせると、選択中のプリセットが外れてしまう
            if (!_initializing) SyncSelectedPresetWithConditions();
            return;
        }

        if (sender is PatternLine line && e.PropertyName == nameof(PatternLine.Text) && line.Text.Contains('\n'))
        {
            SplitPastedLines(line);
            return;
        }

        EnsureTrailingBlank();
        WriteBackIncludePatterns();

        // ON/OFF は本文の色が変わる操作なので、抽出の完了を待たずに反映する
        if (e.PropertyName == nameof(PatternLine.IsEnabled)) UpdateHighlight();
    }

    /// <summary>1 行の入力欄に複数行が貼り付けられたときは、行ごとに分ける。</summary>
    private void SplitPastedLines(PatternLine line)
    {
        int at = IncludePatterns.IndexOf(line);
        if (at < 0) return;

        var parts = line.Text.Split('\n')
                        .Select(p => p.Trim('\r', ' ', '\t'))
                        .Where(p => p.Length > 0)
                        .ToArray();

        _syncingPatterns = true;
        try
        {
            line.Text = parts.Length > 0 ? parts[0] : string.Empty;
        }
        finally
        {
            _syncingPatterns = false;
        }

        for (int i = 1; i < parts.Length; i++)
        {
            bool enabled = parts[i][0] != '#';
            var added = new PatternLine(enabled ? parts[i] : parts[i][1..].TrimStart(), enabled);
            added.PropertyChanged += OnPatternLineChanged;
            IncludePatterns.Insert(at + i, added);
        }

        EnsureTrailingBlank();
        WriteBackIncludePatterns();
    }

    /// <summary>リストの内容を <see cref="IncludeText"/> へ書き戻す。</summary>
    private void WriteBackIncludePatterns()
    {
        if (_syncingPatterns) return;

        var composed = string.Join("\r\n", IncludePatterns
            .Where(p => !p.IsBlank)
            .Select(p => p.IsEnabled ? p.Text : "#" + p.Text));

        _syncingPatterns = true;
        try
        {
            IncludeText = composed;
        }
        finally
        {
            _syncingPatterns = false;
        }

        AssignPatternColors();
    }

    /// <summary>
    /// 各行に強調色を割り当てる。
    /// </summary>
    /// <remarks>
    /// 利用者が色を選んだ行はその色、選んでいない行は「空でない行の並び順」で決める。
    /// OFF の行も順番だけは数に入れるので、チェックを外しても他の行の色がずれない。
    /// </remarks>
    private void AssignPatternColors()
    {
        int ordinal = 0;
        foreach (var line in IncludePatterns)
        {
            if (line.IsBlank)
            {
                line.Color = null;
                continue;
            }
            line.Color = line.IsEnabled
                ? HighlightRuleSet.GetPaletteBrush(line.ChosenColorIndex ?? ordinal)
                : null;
            ordinal++;
        }
    }

    /// <summary>
    /// 保存されていた色を、空でない行の並びに沿って割り当てる。足りない分は自動に戻す。
    /// <see cref="IncludeText"/> を入れて行を組み直したあとに呼ぶこと。
    /// </summary>
    private void ApplyIncludeColors(IReadOnlyList<int?>? colors)
    {
        int ordinal = 0;
        foreach (var line in IncludePatterns)
        {
            if (line.IsBlank) continue;
            line.ChosenColorIndex = colors is not null && ordinal < colors.Count ? colors[ordinal] : null;
            ordinal++;
        }
    }

    /// <summary>
    /// 空でない行の並びで、選ばれている色を返す。保存用。
    /// 末尾の「自動」は省くので、1 つも選んでいなければ空になる。
    /// </summary>
    private List<int?> CurrentIncludeColors()
    {
        var colors = IncludePatterns.Where(p => !p.IsBlank).Select(p => p.ChosenColorIndex).ToList();
        while (colors.Count > 0 && colors[^1] is null) colors.RemoveAt(colors.Count - 1);
        return colors;
    }

    /// <summary>色の並びが同じか。足りない分は「自動」とみなす（古いプリセットは空のため）。</summary>
    private static bool SameIncludeColors(IReadOnlyList<int?>? a, IReadOnlyList<int?> b)
    {
        a ??= Array.Empty<int?>();
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            int? left = i < a.Count ? a[i] : null;
            int? right = i < b.Count ? b[i] : null;
            if (left != right) return false;
        }
        return true;
    }

    private void RemovePattern(object? parameter)
    {
        if (parameter is not PatternLine line) return;
        if (!IncludePatterns.Remove(line)) return;

        line.PropertyChanged -= OnPatternLineChanged;
        EnsureTrailingBlank();
        WriteBackIncludePatterns();
        UpdateHighlight();
    }

    #endregion

    #region 表示設定

    private bool _wordWrap;
    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (!SetProperty(ref _wordWrap, value)) return;
            View.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            UpdateContentMetrics();
        }
    }

    private bool _showLineNumbers = true;
    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set
        {
            if (!SetProperty(ref _showLineNumbers, value)) return;
            View.LineNumberVisibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateContentMetrics();
        }
    }

    private Models.HighlightMode _highlightMode = Models.HighlightMode.Match;

    /// <summary>
    /// 本文の強調の仕方。なし / 一致箇所 / 行全体 の 3 段階。
    /// </summary>
    /// <remarks>
    /// 以前は「強調するか」と「行全体を塗るか」の 2 つの真偽値だったが、後者は前者に従属していて
    /// チェックボックス 2 つでは関係が見えなかったため、1 つの段階にまとめた。
    /// 検索語はどの段階でも一致箇所だけを塗る（行の色の上に重ねて、どこに一致したかを示す）。
    /// </remarks>
    public Models.HighlightMode HighlightMode
    {
        get => _highlightMode;
        set { if (SetProperty(ref _highlightMode, value)) UpdateHighlight(); }
    }

    private double _fontSize = 13;
    public double FontSize
    {
        get => _fontSize;
        set { if (SetProperty(ref _fontSize, value)) UpdateContentMetrics(); }
    }

    private string _fontFamilyName = "Consolas, MS Gothic";
    public string FontFamilyName
    {
        get => _fontFamilyName;
        set => SetProperty(ref _fontFamilyName, value);
    }

    private bool _filterPaneVisible = true;
    public bool FilterPaneVisible
    {
        get => _filterPaneVisible;
        set => SetProperty(ref _filterPaneVisible, value);
    }

    private EncodingChoice _selectedEncoding = TextEncodings.Auto;
    public EncodingChoice SelectedEncoding
    {
        get => _selectedEncoding;
        set
        {
            if (!SetProperty(ref _selectedEncoding, value)) return;
            // クリップボードは既にテキストなので読み直す意味がない（保存時の文字コードとしてだけ使う）
            if (!_initializing && _document.Source == LogSource.File) _ = ReloadAsync();
        }
    }

    #endregion

    #region 検索

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) UpdateHighlight(); }
    }

    private int _selectedIndex = -1;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set { if (SetProperty(ref _selectedIndex, value)) UpdateStatus(); }
    }

    #endregion

    #region 状態

    private VirtualLineCollection _lines = new(LogDocument.Empty, null);
    public VirtualLineCollection Lines
    {
        get => _lines;
        private set => SetProperty(ref _lines, value);
    }

    public bool HasDocument => !_document.IsEmptyDocument;

    /// <summary>操作バーに出す、開いているログの名前（パスはツールチップで見せる）。</summary>
    public string DocumentName => HasDocument ? _document.DisplayName : "(未読み込み)";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    private string _progressText = string.Empty;
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    private string _statusText = "ファイルを開いてください（ドラッグ＆ドロップ可）";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _fileStatusText = "(未読み込み)";
    public string FileStatusText
    {
        get => _fileStatusText;
        private set => SetProperty(ref _fileStatusText, value);
    }

    private string _encodingStatusText = string.Empty;
    public string EncodingStatusText
    {
        get => _encodingStatusText;
        private set => SetProperty(ref _encodingStatusText, value);
    }

    private string _elapsedText = string.Empty;
    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string Title
    {
        get
        {
            string project = _projectPath is null ? string.Empty : $" [{ProjectDisplayName}]";
            return HasDocument
                ? $"{_document.DisplayName}{project} - Sudare"
                : $"Sudare{project}";
        }
    }

    #endregion

    #region プリセット

    private FilterPreset? _selectedPreset;
    private bool _syncingPreset;

    public FilterPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;
            DeletePresetCommand.RaiseCanExecuteChanged();
            if (value is null || _initializing) return;
            ApplyPreset(value);
        }
    }

    /// <summary>
    /// 現在の抽出条件と一致するプリセットを選択状態にする（一致するものが無ければ選択なし）。
    /// </summary>
    /// <remarks>
    /// ComboBox は既に選ばれている項目をもう一度選んでも変更として扱わないため、
    /// 選択状態を実際の条件に追随させておかないと「一度選んだプリセットを選び直す」ことが
    /// できなくなる（プロジェクト読み込みなどで条件だけが変わった場合に詰む）。
    /// 条件がプリセットから外れた時点で選択を解除しておけば、選び直しが変更として成立する。
    /// </remarks>
    private void SyncSelectedPresetWithConditions()
    {
        if (_syncingPreset) return;

        var match = Presets.FirstOrDefault(MatchesCurrentConditions);
        if (ReferenceEquals(match, _selectedPreset)) return;

        _syncingPreset = true;
        try
        {
            // setter を通すと ApplyPreset が走って条件を上書きしてしまうので、直接入れ替える
            _selectedPreset = match;
            OnPropertyChanged(nameof(SelectedPreset));
            DeletePresetCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _syncingPreset = false;
        }
    }

    private bool MatchesCurrentConditions(FilterPreset preset) =>
        string.Equals(preset.Include, IncludeText, StringComparison.Ordinal)
        && string.Equals(preset.Exclude, ExcludeText, StringComparison.Ordinal)
        && preset.Mode == Mode
        && preset.CaseSensitive == CaseSensitive
        && preset.IncludeLogic == IncludeLogic
        && preset.ExcludeLogic == ExcludeLogic
        && preset.IncludeHighlightOnly == IncludeHighlightOnly
        && SameIncludeColors(preset.IncludeColors, CurrentIncludeColors());

    private void ApplyPreset(FilterPreset preset)
    {
        bool previous = _initializing;
        _initializing = true;
        try
        {
            IncludeText = preset.Include;
            ApplyIncludeColors(preset.IncludeColors);
            ExcludeText = preset.Exclude;
            Mode = preset.Mode;
            CaseSensitive = preset.CaseSensitive;
            IncludeLogic = preset.IncludeLogic;
            ExcludeLogic = preset.ExcludeLogic;
            IncludeHighlightOnly = preset.IncludeHighlightOnly;
        }
        finally
        {
            _initializing = previous;
        }
        _ = ApplyFilterAsync();
    }

    private void SavePreset()
    {
        string suggested = SelectedPreset?.Name ?? string.Empty;
        string? name = InputDialog.Ask(Application.Current.MainWindow, "プリセットの保存",
                                       "プリセット名を入力してください。既存の名前を指定すると上書きします。", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;

        var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        bool isNew = preset is null;
        preset ??= new FilterPreset();

        preset.Name = name;
        preset.Include = IncludeText;
        preset.IncludeColors = CurrentIncludeColors();
        preset.Exclude = ExcludeText;
        preset.Mode = Mode;
        preset.CaseSensitive = CaseSensitive;
        preset.IncludeLogic = IncludeLogic;
        preset.ExcludeLogic = ExcludeLogic;
        preset.IncludeHighlightOnly = IncludeHighlightOnly;

        if (isNew) Presets.Add(preset);

        _initializing = true;
        SelectedPreset = preset;
        _initializing = false;
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    private void DeletePreset()
    {
        if (SelectedPreset is null) return;
        var target = SelectedPreset;
        if (MessageBox.Show($"プリセット「{target.Name}」を削除しますか?", "確認",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _initializing = true;
        SelectedPreset = null;
        _initializing = false;
        Presets.Remove(target);
    }

    #endregion

    #region ファイル読み込み

    private async Task OpenWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ログファイルを開く",
            Filter = "ログ/テキスト (*.log;*.txt;*.csv;*.out)|*.log;*.txt;*.csv;*.out|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        await OpenAsync(dialog.FileName);
    }

    public async Task OpenAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            MessageBox.Show($"ファイルが見つかりません。\n{path}", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            RemoveRecent(path);
            return;
        }

        var info = new FileInfo(path);
        if (info.Length > 700L * 1024 * 1024)
        {
            var answer = MessageBox.Show(
                $"ファイルサイズが {FormatBytes(info.Length)} あります。\n" +
                "読み込みに時間がかかり、多くのメモリを消費します。続行しますか?",
                "Sudare", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "読み込み中";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var progress = new Progress<LoadProgress>(p =>
            {
                ProgressText = p.Phase;
                ProgressValue = p.Percent;
            });

            var document = await LogDocument.LoadAsync(path, SelectedEncoding, progress, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            ApplyDocument(document);
            AddRecent(path);

            stopwatch.Stop();
            ElapsedText = $"読込 {stopwatch.ElapsedMilliseconds:N0} ms";
            await ApplyFilterAsync(resetScroll: true);
        }
        catch (OperationCanceledException)
        {
            // 別のファイルが開かれた
        }
        catch (OutOfMemoryException)
        {
            MessageBox.Show("メモリが不足しました。より小さいファイルを開いてください。", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"読み込みに失敗しました。\n{ex.Message}", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                IsBusy = false;
                ProgressText = string.Empty;
                ProgressValue = 0;
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// クリップボードのテキストをそのままログとして読み込む。
    /// ファイルと同じフィルタ・検索・保存機能がそのまま使える。
    /// </summary>
    private async Task LoadFromClipboardAsync()
    {
        string? text = await ReadClipboardTextAsync();
        if (text is null) return;

        if (text.Length == 0)
        {
            MessageBox.Show("クリップボードにテキストがありません。", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _loadCts?.Cancel();
        _loadCts = null;

        IsBusy = true;
        ProgressValue = 0;
        ProgressText = "クリップボードを読み込み中";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var document = await Task.Run(() => LogDocument.FromText(text, ClipboardDisplayName));
            ApplyDocument(document);

            stopwatch.Stop();
            ElapsedText = $"読込 {stopwatch.ElapsedMilliseconds:N0} ms";
            await ApplyFilterAsync(resetScroll: true);
        }
        catch (OutOfMemoryException)
        {
            MessageBox.Show("メモリが不足しました。クリップボードの内容が大きすぎます。", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"クリップボードの読み込みに失敗しました。\n{ex.Message}", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            ProgressValue = 0;
        }
    }

    /// <summary>
    /// クリップボードは他プロセスがロックしていることがあるので数回やり直す。
    /// 取得できなかった場合は null（利用者にはメッセージ済み）。
    /// </summary>
    private static async Task<string?> ReadClipboardTextAsync()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(60);
            }
        }

        MessageBox.Show($"クリップボードを開けませんでした。\n{last?.Message}", "Sudare",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    private void ApplyDocument(LogDocument document)
    {
        // 同じファイルの再読み込みならマーカーは残す。別のログに切り替えたときだけ捨てる。
        bool sameSource = _document.Source != LogSource.None
                          && _document.Source == document.Source
                          && string.Equals(_document.FilePath, document.FilePath, StringComparison.OrdinalIgnoreCase);

        _document = document;
        _hits = null;
        _expansions.Clear();
        ClearExpansionsCommand.RaiseCanExecuteChanged();

        if (!sameSource) _markedLines.Clear();
        RebuildMarkerList();

        // プロジェクトが参照していたログとは別のものを開いたら、関連付けを外す。
        // そうしないと「プロジェクトを保存」が別のログを指すよう黙って書き換えてしまう。
        if (!_restoringProject && _projectPath is not null
            && !string.Equals(document.FilePath, _projectLogPath, StringComparison.OrdinalIgnoreCase))
        {
            _projectPath = null;
            _projectLogPath = null;
        }

        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(Title));
        ReloadCommand.RaiseCanExecuteChanged();
        CloseFileCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        ExportWithLineNumbersCommand.RaiseCanExecuteChanged();
        SaveProjectCommand.RaiseCanExecuteChanged();
        SaveProjectAsCommand.RaiseCanExecuteChanged();
    }

    #endregion

    #region プロジェクト

    private string? _projectPath;
    private string? _projectLogPath;
    private bool _restoringProject;

    public string ProjectDisplayName =>
        _projectPath is null ? string.Empty : Path.GetFileNameWithoutExtension(_projectPath);

    private bool CanSaveProject() => HasDocument && _document.Source == LogSource.File;

    private async Task OpenProjectWithDialogAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "プロジェクトを開く",
            Filter = ProjectFile.OpenFilterText,
            DefaultExt = ProjectFile.Extension,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        await OpenProjectAsync(dialog.FileName);
    }

    /// <summary>
    /// プロジェクトを読み込み、抽出条件・マーカー・表示設定を復元する。
    /// 参照先のログが見つからない場合はエラーにして何も変更しない。
    /// </summary>
    public async Task OpenProjectAsync(string path)
    {
        ProjectFile project;
        try
        {
            project = ProjectService.Load(path);
        }
        catch (ProjectLoadException ex)
        {
            MessageBox.Show(ex.Message, "Sudare", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string logPath = ProjectService.ResolveLogPath(path, project);
        if (logPath.Length == 0)
        {
            MessageBox.Show(
                "プロジェクトが参照しているログファイルが見つかりません。\n\n" +
                $"記録されているパス:\n{project.LogFilePath}",
                "Sudare", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _restoringProject = true;
        _initializing = true;
        try
        {
            IncludeText = project.IncludeText;
            ApplyIncludeColors(project.IncludeColors);
            ExcludeText = project.ExcludeText;
            Mode = project.Mode;
            CaseSensitive = project.CaseSensitive;
            IncludeLogic = project.IncludeLogic;
            ExcludeLogic = project.ExcludeLogic;
            IncludeHighlightOnly = project.IncludeHighlightOnly;
            ContextLines = Math.Clamp(project.ContextLines, 0, MaxContextLines);
            SelectedEncoding = TextEncodings.FromKey(project.EncodingKey);
            WordWrap = project.WordWrap;
            ShowLineNumbers = project.ShowLineNumbers;
            // 段階を持たない旧形式では、2 つの真偽値から組み立てる
            HighlightMode = project.HighlightMode
                ?? (!project.HighlightMatches ? Models.HighlightMode.None
                    : project.HighlightWholeLine ? Models.HighlightMode.WholeLine
                    : Models.HighlightMode.Match);
            if (project.FontSize >= 6) FontSize = project.FontSize;
            if (!string.IsNullOrWhiteSpace(project.FontFamily)) FontFamilyName = project.FontFamily;
            SearchText = project.SearchText;
        }
        finally
        {
            _initializing = false;
        }

        // 条件がプリセットから外れたなら選択を解除しておく（同じプリセットを選び直せるように）
        SyncSelectedPresetWithConditions();

        try
        {
            // ここでログの読み込みと抽出まで済む
            await OpenAsync(logPath);
            if (!HasDocument) return;

            // マーカーは本文が読めるようになってから復元する。
            // 色は Markers と同じ並びの MarkerColors から引く。
            // 色が無い（旧形式）・数が合わない場合は既定色に倒す。
            _markedLines.Clear();
            var colors = project.MarkerColors;
            for (int i = 0; i < project.Markers.Count; i++)
            {
                int lineIndex = project.Markers[i] - 1;
                if (lineIndex < 0 || lineIndex >= _document.LineCount) continue;
                int colorIndex = i < colors.Count ? MarkerPalette.Normalize(colors[i]) : MarkerPalette.DefaultIndex;
                _markedLines[lineIndex] = colorIndex;
            }
            RebuildMarkerList();

            _projectPath = path;
            _projectLogPath = logPath;
        }
        finally
        {
            _restoringProject = false;
        }

        OnPropertyChanged(nameof(Title));
        SaveProjectCommand.RaiseCanExecuteChanged();
        SaveProjectAsCommand.RaiseCanExecuteChanged();

        if (project.CursorLineNumber > 0 && Lines.Count > 0)
        {
            int index = Lines.FromLineNumberNearest(project.CursorLineNumber, out _);
            if (index >= 0)
            {
                SelectedIndex = index;
                ScrollRequested?.Invoke(index);
            }
        }

        int dropped = project.Markers.Count - _markedLines.Count;
        StatusText = dropped > 0
            ? $"プロジェクト「{ProjectDisplayName}」を読み込みました（{dropped:N0} 件のマーカーは行数の範囲外のため除外）"
            : $"プロジェクト「{ProjectDisplayName}」を読み込みました";

        // 旧形式は上書きせず名前を付けて保存させるので、その予告をしておく
        if (IsLegacyProject) StatusText += $"　※ 旧形式です。保存すると {ProjectFile.Extension} になります";
    }

    /// <summary>改名前の <c>.lfvproj</c> を開いている状態か。この場合は上書き保存しない。</summary>
    private bool IsLegacyProject =>
        _projectPath is not null
        && _projectPath.EndsWith(ProjectFile.LegacyExtension, StringComparison.OrdinalIgnoreCase);

    private void SaveProject()
    {
        if (!CanSaveProject()) return;

        // 旧形式は「読み込みだけ」の扱いなので、上書きせず新しい拡張子での保存先を尋ねる
        if (_projectPath is null || IsLegacyProject) { SaveProjectAs(); return; }
        SaveProjectTo(_projectPath);
    }

    private void SaveProjectAs()
    {
        if (!CanSaveProject()) return;

        // 旧形式から来た場合も、拡張子だけ差し替えた名前を既定にする
        string suggested = _projectPath is not null
            ? Path.GetFileNameWithoutExtension(_projectPath)
            : Path.GetFileNameWithoutExtension(_document.FilePath);

        var dialog = new SaveFileDialog
        {
            Title = "プロジェクトを保存",
            FileName = suggested + ProjectFile.Extension,
            Filter = ProjectFile.SaveFilterText,
            DefaultExt = ProjectFile.Extension,
            AddExtension = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;
        SaveProjectTo(dialog.FileName);
    }

    /// <summary>現在の状態を指定パスへプロジェクトとして書き出し、以後の保存先にする。</summary>
    public void SaveProjectTo(string path)
    {
        try
        {
            ProjectService.Save(path, BuildProject());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"プロジェクトの保存に失敗しました。\n{ex.Message}", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _projectPath = path;
        _projectLogPath = _document.FilePath;
        OnPropertyChanged(nameof(Title));
        SaveProjectCommand.RaiseCanExecuteChanged();
        SaveProjectAsCommand.RaiseCanExecuteChanged();
        StatusText = $"プロジェクトを保存しました: {path}";
    }

    private ProjectFile BuildProject()
    {
        // Markers と MarkerColors は同じ並びで書き出す（読み込み側も並びで対応付ける）
        var ordered = _markedLines.Keys.Order().ToList();

        return new ProjectFile
        {
            LogFilePath = _document.FilePath,
            EncodingKey = SelectedEncoding.Key,
            IncludeText = IncludeText,
            IncludeColors = CurrentIncludeColors(),
            ExcludeText = ExcludeText,
            Mode = Mode,
            CaseSensitive = CaseSensitive,
            IncludeLogic = IncludeLogic,
            ExcludeLogic = ExcludeLogic,
            IncludeHighlightOnly = IncludeHighlightOnly,
            ContextLines = ContextLines,
            Markers = ordered.Select(i => i + 1).ToList(),
            MarkerColors = ordered.Select(i => _markedLines[i]).ToList(),
            WordWrap = WordWrap,
            ShowLineNumbers = ShowLineNumbers,
            HighlightMode = HighlightMode,
            HighlightMatches = HighlightMode != Models.HighlightMode.None,
            HighlightWholeLine = HighlightMode == Models.HighlightMode.WholeLine,
            FontSize = FontSize,
            FontFamily = FontFamilyName,
            SearchText = SearchText,
            CursorLineNumber = Math.Max(0, CurrentLineNumber()),
        };
    }

    #endregion

    #region 再読み込み / 最近使ったファイル

    private async Task ReloadAsync()
    {
        switch (_document.Source)
        {
            case LogSource.File:
                await OpenAsync(_document.FilePath);
                break;
            case LogSource.Clipboard:
                await LoadFromClipboardAsync();
                break;
        }
    }

    private void CloseFile()
    {
        _loadCts?.Cancel();
        _filterCts?.Cancel();
        ApplyDocument(LogDocument.Empty);
        ElapsedText = string.Empty;
        UpdateView(_document);
    }

    private void AddRecent(string path)
    {
        RemoveRecent(path);
        RecentFiles.Insert(0, new RecentFileItem(path, OpenRecentCommand));
        while (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveAt(RecentFiles.Count - 1);
    }

    private void RemoveRecent(string path)
    {
        for (int i = RecentFiles.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentFiles[i].Path, path, StringComparison.OrdinalIgnoreCase)) RecentFiles.RemoveAt(i);
        }
    }

    #endregion

    #region フィルタ適用

    private void OnFilterConditionChanged()
    {
        if (_initializing) return;
        SyncSelectedPresetWithConditions();
        _debounceTimer.Stop();
        if (AutoApply) _debounceTimer.Start();
    }

    private void ClearFilter()
    {
        _initializing = true;
        IncludeText = string.Empty;
        ExcludeText = string.Empty;
        SearchText = string.Empty;
        _initializing = false;
        SyncSelectedPresetWithConditions();
        _ = ApplyFilterAsync();
    }

    public async Task ApplyFilterAsync(bool resetScroll = false)
    {
        _debounceTimer.Stop();

        if (!HasDocument)
        {
            _hits = null;
            UpdateView(_document);
            return;
        }

        CompiledFilter filter;
        try
        {
            // 「強調のみ」では含む語を絞り込みに渡さない。除外語はそのまま効かせる。
            // 絞り込みに使わない場合でも、書き間違いは黙って捨てずに知らせる。
            if (IncludeHighlightOnly) CompiledFilter.CompilePatterns(IncludeText, Mode, CaseSensitive);

            filter = CompiledFilter.Compile(new FilterRequest(
                IncludeHighlightOnly ? string.Empty : IncludeText,
                ExcludeText, Mode, CaseSensitive, IncludeLogic, ExcludeLogic));
            FilterError = null;
        }
        catch (FilterPatternException ex)
        {
            FilterError = ex.Message;
            return;
        }

        _filterCts?.Cancel();
        var cts = new CancellationTokenSource();
        _filterCts = cts;

        int previousLineNumber = CurrentLineNumber();

        IsBusy = true;
        ProgressText = "抽出中";
        ProgressValue = 0;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var document = _document;
            var progress = new Progress<double>(v => ProgressValue = v);
            bool[]? hits = await Task.Run(() => FilterEngine.Match(document, filter, progress, cts.Token), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            stopwatch.Stop();
            _hits = hits;
            _hitCount = CountHits(hits, document.LineCount);
            _expansions.Clear();        // 条件が変わったので個別展開は破棄する
            ClearExpansionsCommand.RaiseCanExecuteChanged();
            UpdateView(document);
            ElapsedText = $"抽出 {stopwatch.ElapsedMilliseconds:N0} ms";

            // 抽出前に見ていた行の位置をできるだけ保つ
            if (resetScroll || previousLineNumber <= 0)
            {
                if (Lines.Count > 0) ScrollRequested?.Invoke(0);
            }
            else
            {
                int index = Lines.FromLineNumber(previousLineNumber);
                if (index >= 0) ScrollRequested?.Invoke(index);
            }
        }
        catch (OperationCanceledException)
        {
            // 新しい条件で再実行される
        }
        catch (Exception ex)
        {
            FilterError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_filterCts, cts))
            {
                _filterCts = null;
                IsBusy = false;
                ProgressText = string.Empty;
                ProgressValue = 0;
            }
            cts.Dispose();
        }
    }

    private void CancelRunningWork()
    {
        _loadCts?.Cancel();
        _filterCts?.Cancel();
    }

    private int CurrentLineNumber()
    {
        int index = SelectedIndex;
        if (index < 0 || index >= Lines.Count) return -1;
        return Lines.ToLineNumber(index);
    }

    private void UpdateView(LogDocument document)
    {
        var composition = FilterEngine.Compose(document, _hits, ContextLines, _expansions);
        Lines = new VirtualLineCollection(document, composition.Map, composition.IsContext, _markedLines);
        SelectedIndex = -1;
        UpdateContentMetrics();
        UpdateHighlight();
        UpdateStatus();
    }

    /// <summary>
    /// 照合はやり直さず、表示する行の組み立てだけをやり直す。
    /// 前後行数の変更や個別展開はこちらだけで済むので、大きなファイルでも即座に反映される。
    /// </summary>
    private void RecomposeView()
    {
        if (!HasDocument) return;

        int keepLineNumber = CurrentLineNumber();
        UpdateView(_document);

        if (keepLineNumber > 0)
        {
            int index = Lines.FromLineNumber(keepLineNumber);
            if (index >= 0)
            {
                SelectedIndex = index;
                ScrollRequested?.Invoke(index);
            }
        }
    }

    #endregion

    #region 前後の行 / 個別展開

    private int _contextLines;

    /// <summary>ヒット行の前後に何行を文脈として表示するか。</summary>
    public int ContextLines
    {
        get => _contextLines;
        private set
        {
            if (!SetProperty(ref _contextLines, value)) return;
            OnPropertyChanged(nameof(ContextLinesText));
            RecomposeView();
        }
    }

    /// <summary>入力欄用。数字以外や範囲外は無視して直前の値を保つ。</summary>
    public string ContextLinesText
    {
        get => _contextLines.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ContextLines = 0;
                OnPropertyChanged();
                return;
            }
            if (int.TryParse(value.Trim(), out int parsed))
            {
                ContextLines = Math.Clamp(parsed, 0, MaxContextLines);
            }
            OnPropertyChanged();
        }
    }

    /// <summary>指定した行の前後を、フィルタとは無関係に一時的に表示する。</summary>
    public void ExpandAround(IEnumerable<int> lineNumbers, int radius)
    {
        if (!HasDocument || _hits is null) return;

        bool added = false;
        foreach (int lineNumber in lineNumbers)
        {
            int center = lineNumber - 1;
            if (center < 0 || center >= _document.LineCount) continue;
            _expansions.Add(new LineRange(center - radius, center + radius));
            added = true;
        }

        if (!added) return;
        ClearExpansionsCommand.RaiseCanExecuteChanged();
        RecomposeView();
    }

    private void ClearExpansions()
    {
        if (_expansions.Count == 0) return;
        _expansions.Clear();
        ClearExpansionsCommand.RaiseCanExecuteChanged();
        RecomposeView();
    }

    #endregion

    #region マーカー

    /// <summary>行インデックス（0 基点）→ マーカーの色番号。</summary>
    private readonly Dictionary<int, int> _markedLines = new();

    /// <summary>直近に選ばれた色。色を指定しない <c>Ctrl+M</c> はこの色で付ける。</summary>
    private int _lastMarkerColorIndex = MarkerPalette.DefaultIndex;

    public ObservableCollection<MarkerItem> Markers { get; } = new();

    /// <summary>メニューに並べるマーカー色の一覧。</summary>
    public IReadOnlyList<MarkerColor> MarkerColors => MarkerPalette.Colors;

    public string MarkerHeader => $"マーカー ({Markers.Count})";

    private MarkerItem? _selectedMarker;
    public MarkerItem? SelectedMarker
    {
        get => _selectedMarker;
        set
        {
            if (!SetProperty(ref _selectedMarker, value)) return;
            RemoveMarkerCommand.RaiseCanExecuteChanged();
            ChangeMarkerColorCommand.RaiseCanExecuteChanged();
            if (value is not null) JumpToMarker(value);
        }
    }

    /// <summary>選択されている行のマーカーを 1 行ずつ反転させる。色は直近に使ったもの。</summary>
    public void ToggleMarkers(IEnumerable<int> lineNumbers)
    {
        if (!HasDocument) return;

        bool changed = false;
        foreach (int lineIndex in ValidLineIndexes(lineNumbers))
        {
            if (!_markedLines.Remove(lineIndex)) _markedLines[lineIndex] = _lastMarkerColorIndex;
            changed = true;
        }

        if (changed) RebuildMarkerList();
    }

    /// <summary>
    /// 選択されている行に、指定した色のマーカーを付ける。
    /// 既に付いている行は色だけを変える（ここでは外さない。外すのは <see cref="ToggleMarkers"/>）。
    /// </summary>
    public void SetMarkerColor(IEnumerable<int> lineNumbers, int colorIndex)
    {
        if (!HasDocument) return;

        colorIndex = MarkerPalette.Normalize(colorIndex);
        bool changed = false;
        foreach (int lineIndex in ValidLineIndexes(lineNumbers))
        {
            if (_markedLines.TryGetValue(lineIndex, out int current) && current == colorIndex) continue;
            _markedLines[lineIndex] = colorIndex;
            changed = true;
        }

        // 次の Ctrl+M も同じ色で付くほうが、色を選び直す手間がない
        _lastMarkerColorIndex = colorIndex;
        if (changed) RebuildMarkerList();
    }

    /// <summary>
    /// 選択されている行の「その色のマーカー」を反転させる（<c>Ctrl+1</c>〜<c>Ctrl+6</c>）。
    /// </summary>
    /// <remarks>
    /// 色が違う行に押したときは外さずにその色へ揃える。色を選び直すたびに
    /// 2 回押す必要があると、キーボードだけで色を塗り替えられないため。
    /// 複数行を選んでいる場合は、全部が既にその色のときだけまとめて外す。
    /// </remarks>
    public void ToggleMarkerColor(IEnumerable<int> lineNumbers, int colorIndex)
    {
        if (!HasDocument) return;

        colorIndex = MarkerPalette.Normalize(colorIndex);
        var targets = ValidLineIndexes(lineNumbers).ToList();
        if (targets.Count == 0) return;

        bool remove = targets.All(i => _markedLines.TryGetValue(i, out int current) && current == colorIndex);
        foreach (int lineIndex in targets)
        {
            if (remove) _markedLines.Remove(lineIndex);
            else _markedLines[lineIndex] = colorIndex;
        }

        // 次の Ctrl+M も同じ色で付くほうが、色を選び直す手間がない
        _lastMarkerColorIndex = colorIndex;
        RebuildMarkerList();

        // 手が止まらないよう、どの色になったのかは画面を見なくても分かるようにする
        string name = MarkerPalette.Get(colorIndex).Name;
        StatusText = remove
            ? $"{targets.Count:N0} 行の{name}のマーカーを外しました"
            : $"{targets.Count:N0} 行に{name}のマーカーを付けました";
    }

    /// <summary>マーカー一覧で選んでいる 1 件の色を変える。</summary>
    public void ChangeSelectedMarkerColor(int colorIndex)
    {
        if (SelectedMarker is null) return;

        int lineNumber = SelectedMarker.LineNumber;
        SetMarkerColor(new[] { lineNumber }, colorIndex);
        RestoreMarkerSelection(lineNumber);
    }

    /// <summary>
    /// 一覧を組み直したあとで、同じ行の項目を選び直す。
    /// 色を選ぶたびに選択が外れると次の色を試せないので、ここだけは復元する。
    /// setter を通すとジャンプが再発火するため、フィールドへ直接入れる。
    /// </summary>
    private void RestoreMarkerSelection(int lineNumber)
    {
        _selectedMarker = Markers.FirstOrDefault(m => m.LineNumber == lineNumber);
        OnPropertyChanged(nameof(SelectedMarker));
        RemoveMarkerCommand.RaiseCanExecuteChanged();
        ChangeMarkerColorCommand.RaiseCanExecuteChanged();
    }

    /// <summary>メニューの CommandParameter（XAML では文字列）を色番号として読む。</summary>
    public static bool TryParseColorIndex(object? parameter, out int colorIndex)
    {
        switch (parameter)
        {
            case int number:
                colorIndex = number;
                return true;
            case string text when int.TryParse(text, out int parsed):
                colorIndex = parsed;
                return true;
            default:
                colorIndex = MarkerPalette.DefaultIndex;
                return false;
        }
    }

    private IEnumerable<int> ValidLineIndexes(IEnumerable<int> lineNumbers)
    {
        foreach (int lineNumber in lineNumbers)
        {
            int lineIndex = lineNumber - 1;
            if (lineIndex >= 0 && lineIndex < _document.LineCount) yield return lineIndex;
        }
    }

    private void ClearMarkers()
    {
        if (_markedLines.Count == 0) return;
        _markedLines.Clear();
        RebuildMarkerList();
    }

    private void RemoveSelectedMarker()
    {
        if (SelectedMarker is null) return;
        _markedLines.Remove(SelectedMarker.LineNumber - 1);
        RebuildMarkerList();
    }

    private void RebuildMarkerList()
    {
        // 再読み込みでファイルが縮んでいることがあるので、範囲外のマーカーは落とす
        foreach (int lineIndex in _markedLines.Keys.Where(i => i < 0 || i >= _document.LineCount).ToList())
        {
            _markedLines.Remove(lineIndex);
        }

        _selectedMarker = null;
        Markers.Clear();
        foreach (int lineIndex in _markedLines.Keys.Order())
        {
            Markers.Add(new MarkerItem(lineIndex + 1, MakePreview(_document.GetText(lineIndex)),
                                       _markedLines[lineIndex]));
        }

        // 一覧の選択は復元しない（復元するとジャンプが再発火してしまう）
        OnPropertyChanged(nameof(SelectedMarker));
        OnPropertyChanged(nameof(MarkerHeader));
        ClearMarkersCommand.RaiseCanExecuteChanged();
        RemoveMarkerCommand.RaiseCanExecuteChanged();
        ChangeMarkerColorCommand.RaiseCanExecuteChanged();
        Lines.RefreshMarkers();
    }

    private static string MakePreview(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        return trimmed.Length <= MarkerPreviewLength
            ? trimmed.ToString()
            : string.Concat(trimmed[..MarkerPreviewLength], "…");
    }

    private void JumpToMarker(MarkerItem marker)
    {
        if (Lines.Count == 0) return;

        int index = Lines.FromLineNumberNearest(marker.LineNumber, out bool exact);
        if (index < 0) return;

        SelectedIndex = index;
        ScrollRequested?.Invoke(index);

        StatusText = exact
            ? $"{marker.LineNumber:N0} 行目へ移動しました"
            : $"{marker.LineNumber:N0} 行目は現在の抽出結果に含まれないため、"
              + $"最も近い {Lines.ToLineNumber(index):N0} 行目へ移動しました";
    }

    #endregion

    #region 表示メトリクス / ステータス

    private void UpdateContentMetrics()
    {
        // 等幅フォント前提の概算。実際に描画された行のほうが広ければ WPF 側が伸ばしてくれるので、
        // ここでは「最低限これだけの横幅がある」ことだけを保証すればよい。
        double charWidth = FontSize * 0.62;

        int digits = Math.Max(3, (_document.LineCount == 0 ? 1 : _document.LineCount).ToString().Length);
        View.LineNumberWidth = ShowLineNumbers ? digits * charWidth + 10 : 0;

        View.ContentMinWidth = WordWrap ? 0 : Math.Min(Lines.MaxLineLength * charWidth + 24, 120000);
    }

    private void UpdateHighlight()
    {
        if (HighlightMode == Models.HighlightMode.None)
        {
            View.Highlight = HighlightRuleSet.Empty;
            return;
        }

        var rules = new List<HighlightRule>();

        // 行ジャンプの指定は検索語ではないので、本文には色を付けない
        if (!string.IsNullOrWhiteSpace(SearchText) && !IsLineJump(SearchText))
        {
            try
            {
                rules.Add(new HighlightRule(PatternMatcher.Create(SearchText, Mode, CaseSensitive),
                                            HighlightRuleSet.SearchBrush));
            }
            catch (FilterPatternException)
            {
                // 入力途中の不正なパターンは無視
            }
        }

        // リストに表示している色をそのまま使う。こうしておけば、チェック欄の色と
        // 本文の色が食い違うことがない。
        foreach (var line in IncludePatterns)
        {
            if (!line.IsEnabled || line.IsBlank || line.Color is null) continue;
            try
            {
                rules.Add(new HighlightRule(PatternMatcher.Create(line.Text, Mode, CaseSensitive), line.Color,
                                            paintsWholeLine: HighlightMode == Models.HighlightMode.WholeLine));
            }
            catch (FilterPatternException)
            {
                // 1 行が不正でも、他の行の強調は続ける
            }
        }

        View.Highlight = rules.Count == 0 ? HighlightRuleSet.Empty : new HighlightRuleSet(rules);
    }

    private void UpdateStatus()
    {
        if (!HasDocument)
        {
            FileStatusText = "(未読み込み)";
            EncodingStatusText = string.Empty;
            StatusText = "ファイルを開く（ドラッグ＆ドロップ可）か、クリップボードから読み込んでください";
            return;
        }

        FileStatusText = _document.Source == LogSource.File ? _document.FilePath : _document.DisplayName;
        EncodingStatusText = _document.Source == LogSource.Clipboard
            ? $"クリップボード / {FormatBytes(_document.SizeBytes)}"
            : $"{_document.EncodingName} / {FormatBytes(_document.SizeBytes)}";

        int total = _document.LineCount;
        int shown = Lines.Count;
        string ratio = total == 0 ? "0.0" : (shown * 100.0 / total).ToString("0.0");
        string position = SelectedIndex >= 0 && SelectedIndex < Lines.Count
            ? $"　カーソル行: {Lines.ToLineNumber(SelectedIndex):N0}"
            : string.Empty;

        // 「含む」を書いたのに行数が減らないのは意図した動作なので、その旨を出しておく
        string highlightOnly = IncludeHighlightOnly && !string.IsNullOrWhiteSpace(IncludeText)
            ? "　含む: 強調のみ"
            : string.Empty;

        if (Lines.IsUnfiltered)
        {
            StatusText = $"全 {total:N0} 行（フィルタなし）{highlightOnly}{position}";
        }
        else if (shown > _hitCount)
        {
            StatusText = $"全 {total:N0} 行 / 表示 {shown:N0} 行 ({ratio}%) "
                       + $"= ヒット {_hitCount:N0} 行 + 前後 {shown - _hitCount:N0} 行{highlightOnly}{position}";
        }
        else
        {
            StatusText = $"全 {total:N0} 行 / 表示 {shown:N0} 行 ({ratio}%){highlightOnly}{position}";
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    #endregion

    #region 検索 / 移動

    public void RequestSearchFocus() => FocusSearchRequested?.Invoke();

    private void Find(bool forward)
    {
        if (string.IsNullOrEmpty(SearchText) || Lines.Count == 0) return;

        // 「:1234」と書かれていたら、検索ではなく行ジャンプとして扱う（専用の入力欄を置かない代わり）
        if (IsLineJump(SearchText)) { GoToLine(); return; }

        PatternMatcher matcher;
        try
        {
            matcher = PatternMatcher.Create(SearchText, Mode, CaseSensitive);
        }
        catch (FilterPatternException ex)
        {
            FilterError = ex.Message;
            return;
        }

        int start = SelectedIndex < 0 ? (forward ? 0 : Lines.Count - 1) : SelectedIndex + (forward ? 1 : -1);
        if (start < 0) start = Lines.Count - 1;
        if (start >= Lines.Count) start = 0;

        int found = Lines.Find(matcher, start, forward);
        if (found < 0)
        {
            StatusText = $"「{SearchText}」は見つかりませんでした";
            return;
        }

        SelectedIndex = found;
        ScrollRequested?.Invoke(found);
        UpdateStatus();
    }

    /// <summary>検索欄の内容が行ジャンプの指定（<c>:1234</c>）か。</summary>
    public static bool IsLineJump(string text) => text.StartsWith(':');

    /// <summary>検索欄に書かれた <c>:1234</c> の行へ移動する。</summary>
    private void GoToLine()
    {
        if (Lines.Count == 0) return;
        if (!int.TryParse(SearchText.TrimStart(':').Trim(), out int lineNumber) || lineNumber <= 0) return;

        int index = Lines.FromLineNumber(lineNumber);
        if (index < 0) return;

        SelectedIndex = index;
        ScrollRequested?.Invoke(index);
        UpdateStatus();
    }

    #endregion

    #region 書き出し

    private async Task ExportAsync(bool withLineNumbers)
    {
        if (!HasDocument || Lines.Count == 0)
        {
            MessageBox.Show("保存できる行がありません。", "Sudare", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string baseName = _document.Source == LogSource.File
            ? Path.GetFileNameWithoutExtension(_document.FilePath)
            : "clipboard";
        var dialog = new SaveFileDialog
        {
            Title = "抽出結果を保存",
            FileName = $"{baseName}_filtered.txt",
            Filter = "テキスト (*.txt)|*.txt|ログ (*.log)|*.log|すべてのファイル (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".txt",
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return;

        var encoding = SelectedEncoding.IsAuto ? _document.Encoding : SelectedEncoding.CreateEncoding();

        IsBusy = true;
        ProgressText = "保存中";
        ProgressValue = 0;
        var cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(v => ProgressValue = v);
            await LogExporter.ExportAsync(dialog.FileName, Lines, encoding, withLineNumbers, "\r\n", progress, cts.Token);
            StatusText = $"{Lines.Count:N0} 行を保存しました: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました。\n{ex.Message}", "Sudare",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            ProgressValue = 0;
            cts.Dispose();
        }
    }

    #endregion

    #region 設定の読み書き

    private void LoadFromSettings()
    {
        _initializing = true;
        try
        {
            IncludeText = _settings.IncludeText;
            ApplyIncludeColors(_settings.IncludeColors);
            ExcludeText = _settings.ExcludeText;
            Mode = _settings.Mode;
            CaseSensitive = _settings.CaseSensitive;
            IncludeLogic = _settings.IncludeLogic;
            ExcludeLogic = _settings.ExcludeLogic;
            IncludeHighlightOnly = _settings.IncludeHighlightOnly;
            AutoApply = _settings.AutoApply;
            IncludeAsText = _settings.IncludeAsText;
            ContextLines = Math.Clamp(_settings.ContextLines, 0, MaxContextLines);
            WordWrap = _settings.WordWrap;
            ShowLineNumbers = _settings.ShowLineNumbers;
            // 段階を持たない古い設定では、2 つの真偽値から組み立てる
            HighlightMode = _settings.HighlightMode
                ?? (!_settings.HighlightMatches ? Models.HighlightMode.None
                    : _settings.HighlightWholeLine ? Models.HighlightMode.WholeLine
                    : Models.HighlightMode.Match);
            FontSize = _settings.FontSize <= 0 ? 13 : _settings.FontSize;
            FontFamilyName = string.IsNullOrWhiteSpace(_settings.FontFamily) ? "Consolas, MS Gothic" : _settings.FontFamily;
            FilterPaneVisible = _settings.FilterPaneVisible;
            SelectedEncoding = TextEncodings.FromKey(_settings.EncodingKey);
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>終了時に呼ぶ。ウィンドウ位置は呼び出し側で詰めてから渡すこと。</summary>
    public void SaveSettings()
    {
        _settings.IncludeText = IncludeText;
        _settings.IncludeColors = CurrentIncludeColors();
        _settings.ExcludeText = ExcludeText;
        _settings.Mode = Mode;
        _settings.CaseSensitive = CaseSensitive;
        _settings.IncludeLogic = IncludeLogic;
        _settings.ExcludeLogic = ExcludeLogic;
        _settings.IncludeHighlightOnly = IncludeHighlightOnly;
        _settings.AutoApply = AutoApply;
        _settings.IncludeAsText = IncludeAsText;
        _settings.ContextLines = ContextLines;
        _settings.WordWrap = WordWrap;
        _settings.ShowLineNumbers = ShowLineNumbers;
        // 古い版で開いても設定が失われないよう、まとめた段階と元の 2 つの両方を書く
        _settings.HighlightMode = HighlightMode;
        _settings.HighlightMatches = HighlightMode != Models.HighlightMode.None;
        _settings.HighlightWholeLine = HighlightMode == Models.HighlightMode.WholeLine;
        _settings.ToolbarItems = Toolbar.ShownIds;
        _settings.FontSize = FontSize;
        _settings.FontFamily = FontFamilyName;
        _settings.FilterPaneVisible = FilterPaneVisible;
        _settings.EncodingKey = SelectedEncoding.Key;
        _settings.Presets = Presets.ToList();
        _settings.RecentFiles = RecentFiles.Select(r => r.Path).ToList();
        _settingsService.Save(_settings);
    }

    public AppSettings Settings => _settings;

    #endregion
}
