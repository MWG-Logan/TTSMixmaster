using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace TTSMixmaster.Core.Imaging;

/// <summary>
/// Lays out and draws the collection name: Cinzel (whose lowercase are small caps, like the original chip),
/// shaped with HarfBuzz, with per-character fallback to a system font for scripts Cinzel lacks (CJK, emoji, …).
/// Fit order: shrink to one line, then wrap to two balanced lines, then ellipsize. All sizes are in
/// native 478px chip units; the caller scales the canvas.
/// </summary>
internal sealed class ChipTitle : IDisposable
{
    internal const float MaxWidth = 350;
    internal const float CenterX = 239;
    internal const float SingleLineBaseline = 386;
    internal const float LastLineBaseline = 392;
    internal const float LineSpacing = 1.2f;
    internal const float OneLineMax = 34, OneLineMin = 22;
    internal const float TwoLineMax = 28, TwoLineMin = 18;

    /// <summary>Titles are cut to this many characters before layout; nobody reads more on a chip.</summary>
    internal const int MaxElements = 200;

    private const float ReferenceSize = 100;

    private readonly SKTypeface _primary;
    private readonly Dictionary<int, SKTypeface> _fallbacks = [];
    private readonly Dictionary<SKTypeface, SKShaper> _shapers = [];
    private readonly Dictionary<(SKTypeface, string), float> _widths = [];

    internal ChipTitle(SKTypeface primary) => _primary = primary;

    /// <summary>A grapheme cluster with the typeface that will draw it.</summary>
    internal readonly record struct Element(string Text, SKTypeface Typeface)
    {
        internal bool IsSpace => Text == " ";
    }

    internal sealed record Line(IReadOnlyList<Element> Elements, float Width);

    /// <summary>The fitted title: its lines (one or two), the font size, and whether it was ellipsized.</summary>
    internal sealed record Layout(IReadOnlyList<Line> Lines, float Size, bool Truncated)
    {
        internal string Text => string.Join("\n", Lines.Select(l => string.Concat(l.Elements.Select(e => e.Text))));
    }

    /// <summary>Normalizes the title: NFC, no control characters, whitespace collapsed, trimmed.</summary>
    internal static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var builder = new StringBuilder(title.Length);
        // Drop unpaired surrogates (enumerated as U+FFFD) and zero-width spaces first: Normalize throws on the former.
        var valid = new StringBuilder(title.Length);
        foreach (var rune in title.EnumerateRunes())
            if (rune != Rune.ReplacementChar && rune.Value is not (0x200B or 0x2060 or 0xFEFF))
                valid.Append(rune.ToString());

        var space = false;
        foreach (var rune in valid.ToString().Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    /// <summary>Splits text into grapheme clusters and picks a typeface for each.</summary>
    internal IReadOnlyList<Element> Elements(string text)
    {
        var elements = new List<Element>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext() && elements.Count < MaxElements)
        {
            var cluster = enumerator.GetTextElement();
            // Skip clusters with nothing to draw (a stray joiner), and the double space that could leave.
            if (cluster != " " && cluster.EnumerateRunes().All(IsInvisible)) continue;
            if (cluster == " " && (elements.Count == 0 || elements[^1].IsSpace)) continue;
            elements.Add(new Element(cluster, cluster == " " ? _primary : TypefaceFor(cluster)));
        }
        while (elements.Count > 0 && elements[^1].IsSpace) elements.RemoveAt(elements.Count - 1);
        // Spaces take their neighbour's typeface so runs aren't split around them.
        for (var i = 0; i < elements.Count; i++)
        {
            if (!elements[i].IsSpace) continue;
            var neighbour = i > 0 ? elements[i - 1].Typeface : elements.Skip(i + 1).FirstOrDefault(e => !e.IsSpace).Typeface;
            if (neighbour is not null) elements[i] = elements[i] with { Typeface = neighbour };
        }
        return elements;
    }

    /// <summary>The typeface for one grapheme cluster: Cinzel when it has every glyph, else a system fallback.</summary>
    internal SKTypeface TypefaceFor(string cluster)
    {
        var runes = cluster.EnumerateRunes().Where(r => !IsInvisible(r)).ToList();
        if (runes.Count == 0 || runes.All(r => HasGlyph(_primary, r.Value))) return _primary;

        // Emoji and other symbols: ask for the cluster's lead character so ZWJ sequences stay in one font.
        var lead = runes[0].Value;
        if (_fallbacks.TryGetValue(lead, out var cached)) return cached;
        var match = SKFontManager.Default.MatchCharacter(null, _primary.FontStyle, null, lead);
        var typeface = match is not null && HasGlyph(match, lead) ? match : _primary;
        _fallbacks[lead] = typeface;
        return typeface;
    }

    /// <summary>Fits the title into at most two lines of <see cref="MaxWidth"/>.</summary>
    internal Layout? Fit(string title)
    {
        var elements = Elements(Clean(title));
        if (elements.Count == 0) return null;

        // 1. One line, shrinking from the largest size.
        var full = Width(elements);
        var size = Math.Min(OneLineMax, MaxWidth / full);
        if (size >= OneLineMin) return new([new(elements, full * size)], size, false);

        // 2. Two balanced lines. Break at spaces (and between CJK characters); a title with no
        //    break opportunity at all (one long word) may break anywhere.
        var breaks = BreakOpportunities(elements);
        if (breaks.Count == 0) breaks = Enumerable.Range(1, elements.Count - 1).Select(i => (i, i)).ToList();
        var best = breaks
            .Select(b => (Head: Trim(elements, 0, b.End), Tail: Trim(elements, b.Start, elements.Count)))
            .Where(b => b.Head.Count > 0 && b.Tail.Count > 0)
            .Select(b => (b.Head, b.Tail, Wide: Math.Max(Width(b.Head), Width(b.Tail))))
            .OrderBy(b => b.Wide)
            .FirstOrDefault();
        if (best.Head is not null)
        {
            size = Math.Min(TwoLineMax, MaxWidth / best.Wide);
            if (size >= TwoLineMin)
                return new([new(best.Head, Width(best.Head) * size), new(best.Tail, Width(best.Tail) * size)], size, false);
        }

        // 3. Ellipsize: fill line one, then cut line two and end it with an ellipsis.
        size = TwoLineMin;
        var limit = MaxWidth / size;
        var head = FillLine(elements, breaks, limit);
        var rest = Trim(elements, head.Next, elements.Count);
        var lines = new List<Line> { new(head.Line, Width(head.Line) * size) };
        if (rest.Count > 0)
        {
            var tail = Ellipsize(rest, limit, out var cut);
            lines.Add(new(tail, Width(tail) * size));
            return new(lines, size, cut);
        }
        return new(lines, size, head.Line.Count < elements.Count);
    }

    /// <summary>Draws a fitted title, centred, with a soft shadow for legibility over any art.</summary>
    internal void Draw(SKCanvas canvas, Layout layout)
    {
        using var shadow = SKImageFilter.CreateDropShadow(0, 1.5f, 2.5f, 2.5f, SKColors.Black.WithAlpha(0xE6));
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, ImageFilter = shadow };
        var baseline = layout.Lines.Count == 1 ? SingleLineBaseline : LastLineBaseline - layout.Size * LineSpacing * (layout.Lines.Count - 1);
        foreach (var line in layout.Lines)
        {
            var x = CenterX - line.Width / 2;
            foreach (var run in Runs(line.Elements))
            {
                using var font = Font(run.Typeface, layout.Size);
                canvas.DrawShapedText(Shaper(run.Typeface), run.Text, x, baseline, SKTextAlign.Left, font, paint);
                x += RunWidth(run.Typeface, run.Text) * layout.Size;
            }
            baseline += layout.Size * LineSpacing;
        }
    }

    public void Dispose()
    {
        foreach (var shaper in _shapers.Values) shaper.Dispose();
        _shapers.Clear();
    }

    /// <summary>Width of a sequence of elements at size 1.</summary>
    internal float Width(IReadOnlyList<Element> elements) => Runs(elements).Sum(r => RunWidth(r.Typeface, r.Text));

    private static IEnumerable<(SKTypeface Typeface, string Text)> Runs(IReadOnlyList<Element> elements)
    {
        var start = 0;
        for (var i = 1; i <= elements.Count; i++)
        {
            if (i < elements.Count && ReferenceEquals(elements[i].Typeface, elements[start].Typeface)) continue;
            yield return (elements[start].Typeface, string.Concat(elements.Skip(start).Take(i - start).Select(e => e.Text)));
            start = i;
        }
    }

    private float RunWidth(SKTypeface typeface, string text)
    {
        if (_widths.TryGetValue((typeface, text), out var width)) return width;
        using var font = Font(typeface, ReferenceSize);
        width = Shaper(typeface).Shape(text, font).Width / ReferenceSize;
        _widths[(typeface, text)] = width;
        return width;
    }

    private SKShaper Shaper(SKTypeface typeface)
    {
        if (!_shapers.TryGetValue(typeface, out var shaper))
            _shapers[typeface] = shaper = new SKShaper(typeface);
        return shaper;
    }

    private static SKFont Font(SKTypeface typeface, float size) => new(typeface, size)
    {
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.None,
        Subpixel = true,
        LinearMetrics = true,
    };

    /// <summary>Break opportunities as (start of next line, end of this line): spaces, and between CJK characters.</summary>
    private static List<(int Start, int End)> BreakOpportunities(IReadOnlyList<Element> elements)
    {
        var breaks = new List<(int, int)>();
        for (var i = 1; i < elements.Count; i++)
        {
            if (elements[i].IsSpace) breaks.Add((i + 1, i));
            else if (!elements[i - 1].IsSpace && (IsCjk(elements[i - 1].Text) || IsCjk(elements[i].Text)) && !IsClosingPunctuation(elements[i].Text))
                breaks.Add((i, i));
        }
        return breaks;
    }

    /// <summary>The longest first line that fits, preferring a break opportunity; returns where line two starts.</summary>
    private (IReadOnlyList<Element> Line, int Next) FillLine(IReadOnlyList<Element> elements, List<(int Start, int End)> breaks, float limit)
    {
        foreach (var (start, end) in breaks.OrderByDescending(b => b.End))
        {
            var line = Trim(elements, 0, end);
            if (line.Count > 0 && Width(line) <= limit) return (line, start);
        }
        // Nothing fits at a break: take as many characters as fit.
        var count = elements.Count;
        while (count > 1 && Width(Trim(elements, 0, count)) > limit) count--;
        return (Trim(elements, 0, count), count);
    }

    private IReadOnlyList<Element> Ellipsize(IReadOnlyList<Element> elements, float limit, out bool cut)
    {
        cut = false;
        if (Width(elements) <= limit) return elements;
        cut = true;
        var ellipsis = HasGlyph(_primary, '…') ? "…" : "...";
        var dots = new Element(ellipsis, _primary);
        // Prefer ending on a whole word, unless that would leave the line mostly empty.
        for (var count = elements.Count - 1; count > 0; count--)
        {
            if (!elements[count].IsSpace) continue;
            var candidate = Trim(elements, 0, count).Append(dots).ToList();
            var width = Width(candidate);
            if (width <= limit && width >= limit * 0.6f) return candidate;
            if (width < limit * 0.6f) break;
        }
        for (var count = elements.Count - 1; count > 0; count--)
        {
            var candidate = Trim(elements, 0, count).Append(dots).ToList();
            if (Width(candidate) <= limit) return candidate;
        }
        return [dots];
    }

    private static bool HasGlyph(SKTypeface typeface, int codepoint)
    {
        using var font = new SKFont(typeface);
        return font.ContainsGlyph(codepoint);
    }

    private static IReadOnlyList<Element> Trim(IReadOnlyList<Element> elements, int start, int end)
    {
        while (start < end && elements[start].IsSpace) start++;
        while (end > start && elements[end - 1].IsSpace) end--;
        return elements.Skip(start).Take(end - start).ToList();
    }

    private static bool IsInvisible(Rune rune) =>
        rune.Value is 0x200D or 0x200C or >= 0xFE00 and <= 0xFE0F or >= 0xE0020 and <= 0xE007F or >= 0x1F3FB and <= 0x1F3FF
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    private static bool IsCjk(string cluster)
    {
        var value = cluster.EnumerateRunes().First().Value;
        return value is >= 0x2E80 and <= 0x9FFF // CJK radicals, kana, bopomofo, CJK unified ideographs
            or >= 0xAC00 and <= 0xD7AF // Hangul syllables
            or >= 0xF900 and <= 0xFAFF // CJK compatibility ideographs
            or >= 0xFF00 and <= 0xFFEF // half/full-width forms
            or >= 0x20000 and <= 0x3FFFF; // CJK extensions
    }

    /// <summary>Characters that must not start a line (kinsoku): closing punctuation, small kana, the long-vowel mark.</summary>
    private static bool IsClosingPunctuation(string cluster) =>
        cluster is "、" or "。" or "，" or "．" or "）" or "」" or "』" or "】" or "！" or "？" or "ー" or "・" or "～"
            or "ぁ" or "ぃ" or "ぅ" or "ぇ" or "ぉ" or "っ" or "ゃ" or "ゅ" or "ょ" or "ゎ"
            or "ァ" or "ィ" or "ゥ" or "ェ" or "ォ" or "ッ" or "ャ" or "ュ" or "ョ" or "ヮ" or "ヵ" or "ヶ";
}
