using Sudare.Models;

namespace Sudare.Services;

/// <summary>名前付きフィルタ設定。</summary>
public sealed class FilterPreset
{
    public string Name { get; set; } = string.Empty;
    public string Include { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="Include"/> の空でない行と同じ並びの強調色番号。<c>null</c> は並び順による自動。
    /// 色を持たない古いプリセットは、すべて自動として読む。
    /// </summary>
    public List<int?> IncludeColors { get; set; } = new();

    public string Exclude { get; set; } = string.Empty;
    public MatchMode Mode { get; set; } = MatchMode.Plain;
    public bool CaseSensitive { get; set; }
    public LogicMode ExcludeLogic { get; set; } = LogicMode.Or;

    /// <summary>「含む」を絞り込みには使わず、強調表示だけに使うか。</summary>
    public bool IncludeHighlightOnly { get; set; }

    public override string ToString() => Name;
}

/// <summary>%APPDATA%\Sudare\settings.json に保存される内容。</summary>
public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    public double FilterPaneWidth { get; set; } = 300;
    public bool FilterPaneVisible { get; set; } = true;

    public string IncludeText { get; set; } = string.Empty;

    /// <summary><see cref="IncludeText"/> の空でない行と同じ並びの強調色番号。<c>null</c> は自動。</summary>
    public List<int?> IncludeColors { get; set; } = new();

    public string ExcludeText { get; set; } = string.Empty;
    public MatchMode Mode { get; set; } = MatchMode.Plain;
    public bool CaseSensitive { get; set; }
    public LogicMode ExcludeLogic { get; set; } = LogicMode.Or;
    public bool AutoApply { get; set; } = true;
    public int ContextLines { get; set; }

    /// <summary>「含む」を絞り込みには使わず、強調表示だけに使うか（除外は効いたまま）。</summary>
    public bool IncludeHighlightOnly { get; set; }

    /// <summary>「含む」を行ごとのリストではなく素のテキストとして編集するか。</summary>
    public bool IncludeAsText { get; set; }

    public bool WordWrap { get; set; }
    public bool ShowLineNumbers { get; set; } = true;
    public bool HighlightMatches { get; set; } = true;

    /// <summary>一致箇所ではなく、行全体の背景を「含む」の色で塗るか。</summary>
    public bool HighlightWholeLine { get; set; }

    /// <summary>
    /// 強調の段階（なし / 一致箇所 / 行全体）。
    /// 上の 2 つの真偽値をまとめたもので、古い設定しか無いファイルでは <c>null</c> になる。
    /// その場合は読み込み側で 2 つの値から組み立てる。書き戻すときは両方を更新するので、
    /// 古い版で開いても設定は失われない。
    /// </summary>
    public HighlightMode? HighlightMode { get; set; }

    /// <summary>
    /// ツールバーに出す項目の識別子。空なら既定（強調・文字サイズ）。
    /// 並び順は目録側で固定なので、ここには「出すかどうか」だけを持つ。
    /// </summary>
    public List<string> ToolbarItems { get; set; } = new();

    public double FontSize { get; set; } = 13;
    public string FontFamily { get; set; } = "Consolas, MS Gothic";

    public string EncodingKey { get; set; } = string.Empty;

    public List<FilterPreset> Presets { get; set; } = new();
    public List<string> RecentFiles { get; set; } = new();
}
