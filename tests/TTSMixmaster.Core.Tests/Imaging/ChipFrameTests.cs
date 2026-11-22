using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Imaging;

[TestClass]
public sealed class ChipFrameTests
{
    [TestMethod]
    public void Committed_frame_matches_the_derivation_from_front()
    {
        using var front = Decode(ChipAssets.Front);
        using var derived = ChipFrame.Derive(front);
        using var committed = Decode(ChipAssets.FrameBytes);
        Assert.AreEqual(ChipGeometry.Size, committed.Width);
        Assert.AreEqual(ChipGeometry.Size, committed.Height);
        for (var y = 0; y < ChipGeometry.Size; y++)
            for (var x = 0; x < ChipGeometry.Size; x++)
                Assert.IsTrue(derived.GetPixel(x, y) == committed.GetPixel(x, y),
                    $"frame.png differs from derivation at ({x},{y}); rerun scripts/derive-chip-frame.cs");
    }

    [TestMethod]
    [DataRow(239, 239, 1.0)] // centre
    [DataRow(32, 32, 1.0)] // just inside the top-left corner
    [DataRow(445, 445, 1.0)] // just inside the bottom-right corner
    [DataRow(100, 440, 1.0)] // beside the notch
    [DataRow(10, 240, 0.0)] // frame
    [DataRow(40, 430, 0.0)] // inside the notch tab
    [DataRow(31, 240, 1.0 / 3)] // left edge: a third of the pixel is window
    [DataRow(446, 240, 1.0 / 3)] // right edge
    [DataRow(240, 31, 1.0 / 3)] // top edge
    [DataRow(240, 446, 1.0 / 3)] // bottom edge
    [DataRow(31, 31, 1.0 / 9)] // top-left corner pixel
    [DataRow(45, 400, 0.125)] // notch diagonal, frame side (x = y - 355)
    [DataRow(46, 400, 0.875)] // notch diagonal, window side (x = y - 354)
    [DataRow(44, 400, 0.0)] // frame beside the diagonal
    public void Coverage_matches_the_measured_panel_edges(int x, int y, double expected) =>
        Assert.AreEqual(Math.Round(expected, 6), Math.Round(ChipGeometry.Coverage(x, y), 6));

    [TestMethod]
    public void Coverage_integrates_to_the_window_area()
    {
        var total = 0.0;
        for (var y = 0; y < ChipGeometry.Size; y++)
            for (var x = 0; x < ChipGeometry.Size; x++)
                total += ChipGeometry.Coverage(x, y);

        var side = ChipGeometry.WindowRight - ChipGeometry.WindowLeft;
        var notchLeg = ChipGeometry.WindowBottom - ChipGeometry.NotchLeftY;
        Assert.AreEqual(Math.Round(side * side - notchLeg * notchLeg / 2, 6), Math.Round(total, 6));
    }

    [TestMethod]
    public void Window_path_follows_the_geometry()
    {
        using var path = ChipGeometry.WindowPath();
        Assert.IsTrue(path.Contains(239, 239));
        Assert.IsTrue(path.Contains(100, 440));
        Assert.IsFalse(path.Contains(40, 430));
        Assert.IsFalse(path.Contains(20, 240));
        Assert.AreEqual(ChipGeometry.WindowBounds, path.Bounds);
    }

    [TestMethod]
    public void Frame_window_is_transparent_and_the_frame_is_opaque_and_untouched()
    {
        using var front = Decode(ChipAssets.Front);
        using var frame = Decode(ChipAssets.FrameBytes);

        Assert.AreEqual(0, frame.GetPixel(239, 239).Alpha); // centre (was the crest)
        Assert.AreEqual(0, frame.GetPixel(239, 370).Alpha); // where the original title was
        Assert.AreEqual(0, frame.GetPixel(100, 440).Alpha); // beside the notch
        Assert.AreEqual(255, frame.GetPixel(15, 240).Alpha); // frame edge
        Assert.AreEqual(255, frame.GetPixel(40, 430).Alpha); // inside the notch tab
        Assert.AreEqual(new SKColor(0xFF, 0xFF, 0xFF), frame.GetPixel(33, 436)); // the ♫ stem
        Assert.AreEqual(0, frame.GetPixel(0, 0).Alpha); // rounded outer corners stay transparent
        Assert.AreEqual(0, frame.GetPixel(477, 0).Alpha);
        Assert.AreEqual(0, frame.GetPixel(0, 477).Alpha);
        Assert.AreEqual(0, frame.GetPixel(477, 477).Alpha);

        for (var y = 0; y < ChipGeometry.Size; y++)
            for (var x = 0; x < ChipGeometry.Size; x++)
                if (ChipGeometry.Coverage(x, y) == 0)
                    Assert.IsTrue(front.GetPixel(x, y) == frame.GetPixel(x, y), $"frame pixel ({x},{y}) changed");
    }

    [TestMethod]
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
                Assert.AreEqual(Math.Round(255 * (1 - c), MidpointRounding.AwayFromZero), pixel.Alpha);
                // Frame slate is #262632; anything much darker would show as a black fringe.
                Assert.IsTrue(pixel.Red >= 0x20 && pixel.Blue >= 0x28, $"dark fringe at ({x},{y}): {pixel}");
            }
        }
        Assert.IsTrue(edges > 1000, $"expected a full ring of edge pixels, found {edges}");
    }
}
