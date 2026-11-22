using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Imaging;

public sealed class ChipFrameTests
{
    [Fact]
    public void Committed_frame_matches_the_derivation_from_front()
    {
        using var front = Decode(ChipAssets.Front);
        using var derived = ChipFrame.Derive(front);
        using var committed = Decode(ChipAssets.FrameBytes);
        Assert.Equal(ChipGeometry.Size, committed.Width);
        Assert.Equal(ChipGeometry.Size, committed.Height);
        for (var y = 0; y < ChipGeometry.Size; y++)
            for (var x = 0; x < ChipGeometry.Size; x++)
                Assert.True(derived.GetPixel(x, y) == committed.GetPixel(x, y),
                    $"frame.png differs from derivation at ({x},{y}); rerun scripts/derive-chip-frame.cs");
    }

    [Theory]
    [InlineData(239, 239, 1.0)] // centre
    [InlineData(32, 32, 1.0)] // just inside the top-left corner
    [InlineData(445, 445, 1.0)] // just inside the bottom-right corner
    [InlineData(100, 440, 1.0)] // beside the notch
    [InlineData(10, 240, 0.0)] // frame
    [InlineData(40, 430, 0.0)] // inside the notch tab
    [InlineData(31, 240, 1.0 / 3)] // left edge: a third of the pixel is window
    [InlineData(446, 240, 1.0 / 3)] // right edge
    [InlineData(240, 31, 1.0 / 3)] // top edge
    [InlineData(240, 446, 1.0 / 3)] // bottom edge
    [InlineData(31, 31, 1.0 / 9)] // top-left corner pixel
    [InlineData(45, 400, 0.125)] // notch diagonal, frame side (x = y - 355)
    [InlineData(46, 400, 0.875)] // notch diagonal, window side (x = y - 354)
    [InlineData(44, 400, 0.0)] // frame beside the diagonal
    public void Coverage_matches_the_measured_panel_edges(int x, int y, double expected) =>
        Assert.Equal(expected, ChipGeometry.Coverage(x, y), 6);

    [Fact]
    public void Coverage_integrates_to_the_window_area()
    {
        var total = 0.0;
        for (var y = 0; y < ChipGeometry.Size; y++)
            for (var x = 0; x < ChipGeometry.Size; x++)
                total += ChipGeometry.Coverage(x, y);

        var side = ChipGeometry.WindowRight - ChipGeometry.WindowLeft;
        var notchLeg = ChipGeometry.WindowBottom - ChipGeometry.NotchLeftY;
        Assert.Equal(side * side - notchLeg * notchLeg / 2, total, 6);
    }

    [Fact]
    public void Window_path_follows_the_geometry()
    {
        using var path = ChipGeometry.WindowPath();
        Assert.True(path.Contains(239, 239));
        Assert.True(path.Contains(100, 440));
        Assert.False(path.Contains(40, 430));
        Assert.False(path.Contains(20, 240));
        Assert.Equal(ChipGeometry.WindowBounds, path.Bounds);
    }

    [Fact]
    public void Frame_window_is_transparent_and_the_frame_is_opaque_and_untouched()
    {
        using var front = Decode(ChipAssets.Front);
        using var frame = Decode(ChipAssets.FrameBytes);

        Assert.Equal(0, frame.GetPixel(239, 239).Alpha); // centre (was the crest)
        Assert.Equal(0, frame.GetPixel(239, 370).Alpha); // where the original title was
        Assert.Equal(0, frame.GetPixel(100, 440).Alpha); // beside the notch
        Assert.Equal(255, frame.GetPixel(15, 240).Alpha); // frame edge
        Assert.Equal(255, frame.GetPixel(40, 430).Alpha); // inside the notch tab
        Assert.Equal(new SKColor(0xFF, 0xFF, 0xFF), frame.GetPixel(33, 436)); // the ♫ stem
        Assert.Equal(0, frame.GetPixel(0, 0).Alpha); // rounded outer corners stay transparent
        Assert.Equal(0, frame.GetPixel(477, 0).Alpha);
        Assert.Equal(0, frame.GetPixel(0, 477).Alpha);
        Assert.Equal(0, frame.GetPixel(477, 477).Alpha);

        for (var y = 0; y < ChipGeometry.Size; y++)
            for (var x = 0; x < ChipGeometry.Size; x++)
                if (ChipGeometry.Coverage(x, y) == 0)
                    Assert.True(front.GetPixel(x, y) == frame.GetPixel(x, y), $"frame pixel ({x},{y}) changed");
    }

    [Fact]
    public void Edge_pixels_carry_frame_colour_not_the_black_panel()
    {
        using var frame = Decode(ChipAssets.FrameBytes);
        var edges = 0;
        for (var y = 0; y < ChipGeometry.Size; y++)
        {
            for (var x = 0; x < ChipGeometry.Size; x++)
            {
                var c = ChipGeometry.Coverage(x, y);
                if (c is <= 0 or >= 1) continue;
                edges++;
                var pixel = frame.GetPixel(x, y);
                Assert.Equal(Math.Round(255 * (1 - c), MidpointRounding.AwayFromZero), pixel.Alpha);
                // Frame slate is #262632; anything much darker would show as a black fringe.
                Assert.True(pixel.Red >= 0x20 && pixel.Blue >= 0x28, $"dark fringe at ({x},{y}): {pixel}");
            }
        }
        Assert.True(edges > 1000, $"expected a full ring of edge pixels, found {edges}");
    }
}
