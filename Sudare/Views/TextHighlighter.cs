using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Sudare.Views;

/// <summary>
/// <see cref="TextBlock"/> にキーワード強調と、文字単位の選択範囲を描く添付プロパティ。
/// 仮想化された ListBox 上で、実体化された行に対してのみ働く。
/// </summary>
public static class TextHighlighter
{
    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.RegisterAttached(
            "SourceText", typeof(string), typeof(TextHighlighter),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty RulesProperty =
        DependencyProperty.RegisterAttached(
            "Rules", typeof(HighlightRuleSet), typeof(TextHighlighter),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SelectionStartProperty =
        DependencyProperty.RegisterAttached(
            "SelectionStart", typeof(int), typeof(TextHighlighter),
            new PropertyMetadata(0, OnChanged));

    public static readonly DependencyProperty SelectionLengthProperty =
        DependencyProperty.RegisterAttached(
            "SelectionLength", typeof(int), typeof(TextHighlighter),
            new PropertyMetadata(0, OnChanged));

    public static string? GetSourceText(DependencyObject d) => (string?)d.GetValue(SourceTextProperty);
    public static void SetSourceText(DependencyObject d, string? value) => d.SetValue(SourceTextProperty, value);

    public static HighlightRuleSet? GetRules(DependencyObject d) => (HighlightRuleSet?)d.GetValue(RulesProperty);
    public static void SetRules(DependencyObject d, HighlightRuleSet? value) => d.SetValue(RulesProperty, value);

    public static int GetSelectionStart(DependencyObject d) => (int)d.GetValue(SelectionStartProperty);
    public static void SetSelectionStart(DependencyObject d, int value) => d.SetValue(SelectionStartProperty, value);

    public static int GetSelectionLength(DependencyObject d) => (int)d.GetValue(SelectionLengthProperty);
    public static void SetSelectionLength(DependencyObject d, int value) => d.SetValue(SelectionLengthProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock textBlock) Render(textBlock);
    }

    private static void Render(TextBlock textBlock)
    {
        string text = GetSourceText(textBlock) ?? string.Empty;
        var rules = GetRules(textBlock);

        int selectionStart = GetSelectionStart(textBlock);
        int selectionLength = GetSelectionLength(textBlock);
        bool hasSelection = selectionLength > 0 && selectionStart >= 0 && selectionStart < text.Length;

        // 行全体を塗るだけで選択も無いときは背景を行コンテナ側で塗るので、本文は素のテキストでよい
        if (text.Length == 0 || (!hasSelection && (rules is null || !rules.HasRangeRules)))
        {
            // Text を直接設定すると Inlines は自動的に破棄される（最速の経路）
            textBlock.Text = text;
            return;
        }

        var ranges = rules is null ? new List<(int Start, int Length, Brush Brush)>() : rules.Compute(text);
        if (hasSelection) ranges = ApplySelection(ranges, selectionStart, selectionLength, text.Length);

        if (ranges.Count == 0)
        {
            textBlock.Text = text;
            return;
        }

        var inlines = textBlock.Inlines;
        inlines.Clear();

        int cursor = 0;
        foreach (var (start, length, brush) in ranges)
        {
            if (start > cursor) inlines.Add(new Run(text[cursor..start]));

            var run = new Run(text.Substring(start, length)) { Background = brush };
            // 選択範囲は濃い色で塗るので、文字色も合わせないと読めなくなる
            if (ReferenceEquals(brush, SelectionBrush)) run.Foreground = SystemColors.HighlightTextBrush;
            inlines.Add(run);

            cursor = start + length;
        }
        if (cursor < text.Length) inlines.Add(new Run(text[cursor..]));
    }

    /// <summary>選択範囲の色。OS の選択色に合わせる。</summary>
    private static readonly Brush SelectionBrush = SystemColors.HighlightBrush;

    /// <summary>
    /// 強調範囲に選択範囲を重ねる。選択が最優先で、重なった強調は左右に切り分ける。
    /// </summary>
    private static List<(int Start, int Length, Brush Brush)> ApplySelection(
        List<(int Start, int Length, Brush Brush)> ranges, int start, int length, int textLength)
    {
        start = Math.Clamp(start, 0, textLength);
        length = Math.Clamp(length, 0, textLength - start);
        if (length == 0) return ranges;

        int end = start + length;
        var result = new List<(int Start, int Length, Brush Brush)>(ranges.Count + 3);

        foreach (var (s, l, brush) in ranges)
        {
            int e = s + l;
            if (e <= start || s >= end)
            {
                result.Add((s, l, brush));
                continue;
            }
            if (s < start) result.Add((s, start - s, brush));
            if (e > end) result.Add((end, e - end, brush));
        }

        result.Add((start, length, SelectionBrush));
        result.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return result;
    }
}
