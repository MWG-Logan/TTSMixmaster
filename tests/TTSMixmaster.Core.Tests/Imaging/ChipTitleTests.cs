using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Tests.Imaging;

[TestClass]
public sealed class ChipTitleTests
{
    private static ChipTitle Title() => new(ChipAssets.TitleTypeface);

    private static void AssertFits(ChipTitle.Layout layout)
    {
        Assert.IsInRange(1, 2, layout.Lines.Count);
        Assert.IsInRange(ChipTitle.TwoLineMin, ChipTitle.OneLineMax, layout.Size);
        foreach (var line in layout.Lines)
        {
            Assert.IsNotEmpty(line.Elements);
            Assert.IsTrue(line.Width <= ChipTitle.MaxWidth + 0.01f, $"line '{string.Concat(line.Elements.Select(e => e.Text))}' is {line.Width}px wide");
        }
        // The text block stays inside the window, clear of the notch.
        var left = ChipTitle.CenterX - layout.Lines.Max(l => l.Width) / 2;
        Assert.IsTrue(left > ChipGeometry.NotchBottomX - 30, $"title starts at x={left}");
    }

    [TestMethod]
    public void The_title_font_is_the_bundled_Cinzel()
    {
        Assert.AreEqual("Cinzel", ChipAssets.TitleTypeface.FamilyName);
        Assert.IsTrue(ChipAssets.TitleTypeface.FontStyle.Weight >= (int)SKFontStyleWeight.Bold);
    }

    [TestMethod]
    public void A_short_title_is_one_line_at_full_size()
    {
        using var title = Title();
        var layout = title.Fit("Woody's Fine EDM")!;
        Assert.ContainsSingle(layout.Lines);
        Assert.AreEqual(ChipTitle.OneLineMax, layout.Size);
        Assert.IsFalse(layout.Truncated);
        Assert.AreEqual("Woody's Fine EDM", layout.Text);
        AssertFits(layout);
    }

    [TestMethod]
    public void A_longer_title_shrinks_before_wrapping()
    {
        using var title = Title();
        var layout = title.Fit("Big Buck Bunny Soundtrack")!;
        Assert.ContainsSingle(layout.Lines);
        Assert.IsInRange(ChipTitle.OneLineMin, ChipTitle.OneLineMax - 0.01f, layout.Size);
        AssertFits(layout);
    }

    [TestMethod]
    public void A_title_too_long_for_one_line_wraps_to_two_balanced_lines()
    {
        using var title = Title();
        var layout = title.Fit("Songs To Play While Rolling Initiative Against Dragons")!;
        Assert.AreEqual(2, layout.Lines.Count);
        Assert.IsFalse(layout.Truncated);
        Assert.AreEqual("Songs To Play While Rolling Initiative Against Dragons", layout.Text.Replace('\n', ' '));
        Assert.IsInRange(0.6f, 1.6f, layout.Lines[0].Width / layout.Lines[1].Width);
        AssertFits(layout);
    }

    [TestMethod]
    public void A_very_long_title_is_ellipsized_on_a_word()
    {
        using var title = Title();
        var layout = title.Fit("The Absolutely Definitive Collection of Every Synthwave Banger I Have Ever Heard On A Late Night Drive Through The City")!;
        Assert.AreEqual(2, layout.Lines.Count);
        Assert.IsTrue(layout.Truncated);
        Assert.AreEqual(ChipTitle.TwoLineMin, layout.Size);
        var last = string.Concat(layout.Lines[1].Elements.Select(e => e.Text));
        Assert.IsTrue(last.EndsWith('…') || last.EndsWith("..."), last);
        Assert.DoesNotContain(" …", last);
        AssertFits(layout);
    }

    [TestMethod]
    public void One_enormous_word_still_fits()
    {
        using var title = Title();
        var layout = title.Fit(new string('W', 500))!;
        Assert.IsTrue(layout.Truncated);
        AssertFits(layout);
    }

    [TestMethod]
    [DataRow("夜のドライブ 作業用BGM")]
    [DataRow("深夜のドライブで聴きたいシティポップ名曲集・完全版")]
    [DataRow("최고의 케이팝 플레이리스트 모음집 2024 최신판")]
    [DataRow("Party Mix 🎉🔥 Вечеринка")]
    [DataRow("👨‍👩‍👧‍👦👍🏽🏳️‍🌈")]
    [DataRow("مزيج الحفلة العربية")]
    [DataRow("Ünïcödé Çàfé — “quoted” & co.")]
    [DataRow("\u0000\u0007tab\tseparated\r\nlines​")]
    [DataRow("\uD800 lone surrogate \uDFFF")]
    public void Unicode_titles_fit_without_throwing(string text)
    {
        using var title = Title();
        var layout = title.Fit(text);
        Assert.IsNotNull(layout);
        AssertFits(layout);
        var chip = ChipImageBuilder.RenderFront(null, text, ServiceKind.YouTube);
        Assert.IsNotEmpty(chip.Png);
    }

    [TestMethod]
    public void Long_CJK_titles_wrap_between_characters()
    {
        using var title = Title();
        var layout = title.Fit("深夜のドライブで聴きたいシティポップ名曲集・完全版")!;
        Assert.AreEqual(2, layout.Lines.Count);
        var second = layout.Lines[1].Elements[0].Text;
        Assert.DoesNotContain(second, new[] { "ィ", "ッ", "ー", "・", "。" });
    }

    [TestMethod]
    public void Characters_missing_from_Cinzel_use_a_system_fallback_font()
    {
        using var title = Title();
        var elements = title.Elements("Mix 夜 🎉");
        Assert.AreSame(ChipAssets.TitleTypeface, elements[0].Typeface); // M
        Assert.AreSame(ChipAssets.TitleTypeface, elements[3].Typeface); // the space after "Mix"

        // Which fallback font exists depends on the machine; when there is one, it must really have the glyph.
        foreach (var element in elements.Where(e => e.Text is "夜" or "🎉"))
        {
            if (ReferenceEquals(element.Typeface, ChipAssets.TitleTypeface)) continue; // no system font covers it
            using var font = new SKFont(element.Typeface);
            Assert.IsTrue(font.ContainsGlyph(char.ConvertToUtf32(element.Text, 0)), $"{element.Typeface.FamilyName} lacks {element.Text}");
        }
        using var cinzel = new SKFont(ChipAssets.TitleTypeface);
        Assert.IsFalse(cinzel.ContainsGlyph('夜'));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow(null)]
    [DataRow("​‍")]
    public void Blank_titles_have_no_layout(string? text)
    {
        using var title = Title();
        Assert.IsNull(title.Fit(text!));
    }

    [TestMethod]
    public void Cleaning_collapses_whitespace_and_drops_control_characters()
    {
        Assert.AreEqual("a b c", ChipTitle.Clean("  a\t\r\n b \u0007 c  "));
        Assert.AreEqual("é", ChipTitle.Clean("é"));
        Assert.AreEqual("", ChipTitle.Clean(null));
    }
}
