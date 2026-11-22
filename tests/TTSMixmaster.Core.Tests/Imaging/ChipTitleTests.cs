using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Tests.Imaging;

public sealed class ChipTitleTests
{
    private static ChipTitle Title() => new(ChipAssets.TitleTypeface);

    private static void AssertFits(ChipTitle.Layout layout)
    {
        Assert.InRange(layout.Lines.Count, 1, 2);
        Assert.InRange(layout.Size, ChipTitle.TwoLineMin, ChipTitle.OneLineMax);
        foreach (var line in layout.Lines)
        {
            Assert.NotEmpty(line.Elements);
            Assert.True(line.Width <= ChipTitle.MaxWidth + 0.01f, $"line '{string.Concat(line.Elements.Select(e => e.Text))}' is {line.Width}px wide");
        }
        // The text block stays inside the window, clear of the notch.
        var left = ChipTitle.CenterX - layout.Lines.Max(l => l.Width) / 2;
        Assert.True(left > ChipGeometry.NotchBottomX - 30, $"title starts at x={left}");
    }

    [Fact]
    public void The_title_font_is_the_bundled_Cinzel()
    {
        Assert.Equal("Cinzel", ChipAssets.TitleTypeface.FamilyName);
        Assert.True(ChipAssets.TitleTypeface.FontStyle.Weight >= (int)SKFontStyleWeight.Bold);
    }

    [Fact]
    public void A_short_title_is_one_line_at_full_size()
    {
        using var title = Title();
        var layout = title.Fit("Woody's Fine EDM")!;
        Assert.Single(layout.Lines);
        Assert.Equal(ChipTitle.OneLineMax, layout.Size);
        Assert.False(layout.Truncated);
        Assert.Equal("Woody's Fine EDM", layout.Text);
        AssertFits(layout);
    }

    [Fact]
    public void A_longer_title_shrinks_before_wrapping()
    {
        using var title = Title();
        var layout = title.Fit("Big Buck Bunny Soundtrack")!;
        Assert.Single(layout.Lines);
        Assert.InRange(layout.Size, ChipTitle.OneLineMin, ChipTitle.OneLineMax - 0.01f);
        AssertFits(layout);
    }

    [Fact]
    public void A_title_too_long_for_one_line_wraps_to_two_balanced_lines()
    {
        using var title = Title();
        var layout = title.Fit("Songs To Play While Rolling Initiative Against Dragons")!;
        Assert.Equal(2, layout.Lines.Count);
        Assert.False(layout.Truncated);
        Assert.Equal("Songs To Play While Rolling Initiative Against Dragons", layout.Text.Replace('\n', ' '));
        Assert.InRange(layout.Lines[0].Width / layout.Lines[1].Width, 0.6f, 1.6f);
        AssertFits(layout);
    }

    [Fact]
    public void A_very_long_title_is_ellipsized_on_a_word()
    {
        using var title = Title();
        var layout = title.Fit("The Absolutely Definitive Collection of Every Synthwave Banger I Have Ever Heard On A Late Night Drive Through The City")!;
        Assert.Equal(2, layout.Lines.Count);
        Assert.True(layout.Truncated);
        Assert.Equal(ChipTitle.TwoLineMin, layout.Size);
        var last = string.Concat(layout.Lines[1].Elements.Select(e => e.Text));
        Assert.True(last.EndsWith('…') || last.EndsWith("..."), last);
        Assert.DoesNotContain(" …", last);
        AssertFits(layout);
    }

    [Fact]
    public void One_enormous_word_still_fits()
    {
        using var title = Title();
        var layout = title.Fit(new string('W', 500))!;
        Assert.True(layout.Truncated);
        AssertFits(layout);
    }

    [Theory]
    [InlineData("夜のドライブ 作業用BGM")]
    [InlineData("深夜のドライブで聴きたいシティポップ名曲集・完全版")]
    [InlineData("최고의 케이팝 플레이리스트 모음집 2024 최신판")]
    [InlineData("Party Mix 🎉🔥 Вечеринка")]
    [InlineData("👨‍👩‍👧‍👦👍🏽🏳️‍🌈")]
    [InlineData("مزيج الحفلة العربية")]
    [InlineData("Ünïcödé Çàfé — “quoted” & co.")]
    [InlineData("\u0000\u0007tab\tseparated\r\nlines​")]
    [InlineData("\uD800 lone surrogate \uDFFF")]
    public void Unicode_titles_fit_without_throwing(string text)
    {
        using var title = Title();
        var layout = title.Fit(text);
        Assert.NotNull(layout);
        AssertFits(layout);
        var chip = ChipImageBuilder.RenderFront(null, text, ServiceKind.YouTube);
        Assert.NotEmpty(chip.Png);
    }

    [Fact]
    public void Long_CJK_titles_wrap_between_characters()
    {
        using var title = Title();
        var layout = title.Fit("深夜のドライブで聴きたいシティポップ名曲集・完全版")!;
        Assert.Equal(2, layout.Lines.Count);
        var second = layout.Lines[1].Elements[0].Text;
        Assert.DoesNotContain(second, new[] { "ィ", "ッ", "ー", "・", "。" });
    }

    [Fact]
    public void Characters_missing_from_Cinzel_use_a_system_fallback_font()
    {
        using var title = Title();
        var elements = title.Elements("Mix 夜 🎉");
        Assert.Same(ChipAssets.TitleTypeface, elements[0].Typeface); // M
        Assert.Same(ChipAssets.TitleTypeface, elements[3].Typeface); // the space after "Mix"

        // Which fallback font exists depends on the machine; when there is one, it must really have the glyph.
        foreach (var element in elements.Where(e => e.Text is "夜" or "🎉"))
        {
            if (ReferenceEquals(element.Typeface, ChipAssets.TitleTypeface)) continue; // no system font covers it
            using var font = new SKFont(element.Typeface);
            Assert.True(font.ContainsGlyph(char.ConvertToUtf32(element.Text, 0)), $"{element.Typeface.FamilyName} lacks {element.Text}");
        }
        using var cinzel = new SKFont(ChipAssets.TitleTypeface);
        Assert.False(cinzel.ContainsGlyph('夜'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("​‍")]
    public void Blank_titles_have_no_layout(string? text)
    {
        using var title = Title();
        Assert.Null(title.Fit(text!));
    }

    [Fact]
    public void Cleaning_collapses_whitespace_and_drops_control_characters()
    {
        Assert.Equal("a b c", ChipTitle.Clean("  a\t\r\n b \u0007 c  "));
        Assert.Equal("é", ChipTitle.Clean("é"));
        Assert.Equal("", ChipTitle.Clean(null));
    }
}
