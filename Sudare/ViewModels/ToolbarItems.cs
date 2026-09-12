namespace Sudare.ViewModels;

/// <summary>
/// ツールバーに出せる項目 1 つ分の目録。
/// </summary>
/// <remarks>
/// 「表示」ポップアップに入っている項目と同じものを並べている。
/// ツールバーはその中から、利用者が選んだものだけを出す窓口という位置づけ。
/// </remarks>
public sealed class ToolbarItemInfo
{
    public ToolbarItemInfo(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>設定ファイルに書く識別子。表示名を変えても保存済みの選択が壊れないようにする。</summary>
    public string Id { get; }

    public string Name { get; }
}

/// <summary>ツールバーに出せる項目の一覧と、既定の並び。</summary>
public static class ToolbarCatalog
{
    public const string WordWrap = "wrap";
    public const string LineNumbers = "linenumbers";
    public const string Highlight = "highlight";
    public const string FontSize = "fontsize";
    public const string Font = "font";
    public const string Encoding = "encoding";
    public const string FilterPane = "filterpane";

    /// <summary>並び順はここで固定する（利用者が選ぶのは「出すかどうか」だけ）。</summary>
    public static IReadOnlyList<ToolbarItemInfo> Items { get; } = new[]
    {
        new ToolbarItemInfo(WordWrap, "折り返し"),
        new ToolbarItemInfo(LineNumbers, "行番号"),
        new ToolbarItemInfo(Highlight, "強調"),
        new ToolbarItemInfo(FontSize, "文字サイズ"),
        new ToolbarItemInfo(Font, "フォント"),
        new ToolbarItemInfo(Encoding, "文字コード"),
        new ToolbarItemInfo(FilterPane, "条件ペイン"),
    };

    /// <summary>初期状態で出す項目。読みながら触るのはこの 2 つだけ、という想定。</summary>
    public static IReadOnlyList<string> Default { get; } = new[] { Highlight, FontSize };

    public static bool Exists(string id) => Items.Any(i => i.Id == id);
}

/// <summary>
/// ツールバーのどの項目を出しているか。
/// </summary>
/// <remarks>
/// XAML からは <c>ToolbarVisibility[wrap]</c> のようにインデクサで引く。
/// 変更時は "Item[]" を通知するので、実体化済みのバインドがまとめて更新される。
/// </remarks>
public sealed class ToolbarVisibility : ObservableObject
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);

    public ToolbarVisibility(IEnumerable<string>? shown = null) => Reset(shown ?? ToolbarCatalog.Default);

    public bool this[string id] => _shown.Contains(id);

    /// <summary>保存用。目録の並び順で返す。</summary>
    public List<string> ShownIds => ToolbarCatalog.Items.Where(i => _shown.Contains(i.Id)).Select(i => i.Id).ToList();

    public void Reset(IEnumerable<string> shown)
    {
        _shown.Clear();
        foreach (var id in shown)
        {
            if (ToolbarCatalog.Exists(id)) _shown.Add(id);
        }
        OnPropertyChanged("Item[]");
    }

    public void Set(string id, bool shown)
    {
        bool changed = shown ? _shown.Add(id) : _shown.Remove(id);
        if (changed) OnPropertyChanged("Item[]");
    }
}

/// <summary>ツールバーを右クリックしたときに出すチェックリストの 1 項目。</summary>
public sealed class ToolbarMenuEntry : ObservableObject
{
    private readonly ToolbarVisibility _visibility;

    public ToolbarMenuEntry(ToolbarItemInfo info, ToolbarVisibility visibility)
    {
        Info = info;
        _visibility = visibility;
    }

    public ToolbarItemInfo Info { get; }

    public string Name => Info.Name;

    public bool IsShown
    {
        get => _visibility[Info.Id];
        set
        {
            if (_visibility[Info.Id] == value) return;
            _visibility.Set(Info.Id, value);
            OnPropertyChanged();
        }
    }

    /// <summary>外から一括で入れ替えたときに、チェック状態を引き直させる。</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsShown));
}
