namespace Sudare.Models;

/// <summary>パターンの解釈方法。</summary>
public enum MatchMode
{
    /// <summary>単純な部分一致。</summary>
    Plain,

    /// <summary><c>*</c> と <c>?</c> のみを特別扱いする部分一致。</summary>
    Wildcard,

    /// <summary>.NET 正規表現。</summary>
    Regex,
}

/// <summary>
/// 本文の強調の仕方。
/// </summary>
/// <remarks>
/// 旧設定の <c>HighlightMatches</c>（強調するか）と <c>HighlightWholeLine</c>（行全体か）は
/// 互いに従属した 2 つの真偽値だったので、1 つの段階にまとめた。
/// </remarks>
public enum HighlightMode
{
    /// <summary>強調しない。</summary>
    None,

    /// <summary>一致した箇所だけを塗る。</summary>
    Match,

    /// <summary>一致した行の全体を塗る。</summary>
    WholeLine,
}
