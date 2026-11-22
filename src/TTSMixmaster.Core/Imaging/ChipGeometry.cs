using SkiaSharp;

namespace TTSMixmaster.Core.Imaging;

/// <summary>
/// Where the art window sits in the 478x478 chip frame, in source-pixel coordinates
/// (pixel (x, y) covers the square [x, x+1] x [y, y+1]).
/// </summary>
/// <remarks>
/// Measured from Assets/Chip/front.png. The black panel's straight edges fall one third of the way
/// into pixels 31 and 446: those rows/columns are exactly 2/3 frame colour (#262632 -> #191921), corner
/// pixel (31,31) is (2/3)^2. The bottom-left notch is a 45° staircase: per row y, pixel y-356 is frame,
/// y-355 is a dark edge pixel and y-354 onward is pure black, so the diagonal is placed on the
/// staircase's midline x - y = -354.5. It meets the left edge at y = 386 1/6 and the bottom at x = 91 5/6.
/// </remarks>
public static class ChipGeometry
{
    /// <summary>Native width and height of the chip art.</summary>
    public const int Size = 478;

    public const double WindowLeft = 31 + 2.0 / 3;
    public const double WindowTop = 31 + 2.0 / 3;
    public const double WindowRight = 446 + 1.0 / 3;
    public const double WindowBottom = 446 + 1.0 / 3;

    /// <summary>The notch diagonal is the line x - y = <see cref="NotchOffset"/>; the window is where x - y ≥ it.</summary>
    public const double NotchOffset = -354.5;

    /// <summary>Where the notch diagonal meets the window's left edge (y) and bottom edge (x).</summary>
    public const double NotchLeftY = WindowLeft - NotchOffset;
    public const double NotchBottomX = WindowBottom + NotchOffset;

    /// <summary>The window polygon, clockwise from the top-left corner.</summary>
    public static IReadOnlyList<(double X, double Y)> Window { get; } =
    [
        (WindowLeft, WindowTop),
        (WindowRight, WindowTop),
        (WindowRight, WindowBottom),
        (NotchBottomX, WindowBottom),
        (WindowLeft, NotchLeftY),
    ];

    /// <summary>The window's bounding box.</summary>
    public static SKRect WindowBounds => new((float)WindowLeft, (float)WindowTop, (float)WindowRight, (float)WindowBottom);

    /// <summary>The window as a path, optionally grown outward by <paramref name="outset"/> pixels (in source units).</summary>
    public static SKPath WindowPath(float outset = 0)
    {
        // Growing a convex polygon: move every edge out by `outset`. The rect edges just shift; the
        // diagonal x - y = c shifts to c - outset*sqrt(2), and its end points slide along the shifted edges.
        var l = (float)WindowLeft - outset;
        var t = (float)WindowTop - outset;
        var r = (float)WindowRight + outset;
        var b = (float)WindowBottom + outset;
        var c = (float)(NotchOffset - outset * Math.Sqrt(2));
        var path = new SKPathBuilder();
        path.MoveTo(l, t);
        path.LineTo(r, t);
        path.LineTo(r, b);
        path.LineTo(b + c, b);
        path.LineTo(l, l - c);
        path.Close();
        return path.Detach();
    }

    /// <summary>Exact fraction (0..1) of pixel (x, y) that lies inside the window.</summary>
    public static double Coverage(int x, int y)
    {
        // Fast paths for pixels well clear of every edge.
        if (x + 1 <= WindowLeft || y + 1 <= WindowTop || x >= WindowRight || y >= WindowBottom) return 0;
        if (x - (y + 1) >= NotchOffset && x >= WindowLeft && y >= WindowTop && x + 1 <= WindowRight && y + 1 <= WindowBottom) return 1;

        // Clip the pixel square against each window half-plane (Sutherland–Hodgman), then take its area.
        Span<(double X, double Y)> a = stackalloc (double, double)[16];
        Span<(double X, double Y)> b = stackalloc (double, double)[16];
        a[0] = (x, y); a[1] = (x + 1, y); a[2] = (x + 1, y + 1); a[3] = (x, y + 1);
        var n = 4;
        n = Clip(a, n, b, p => p.X - WindowLeft); // x ≥ left
        n = Clip(b, n, a, p => p.Y - WindowTop); // y ≥ top
        n = Clip(a, n, b, p => WindowRight - p.X); // x ≤ right
        n = Clip(b, n, a, p => WindowBottom - p.Y); // y ≤ bottom
        n = Clip(a, n, b, p => p.X - p.Y - NotchOffset); // x - y ≥ notch
        var area = 0.0;
        for (var i = 0; i < n; i++)
        {
            var (x0, y0) = b[i];
            var (x1, y1) = b[(i + 1) % n];
            area += x0 * y1 - x1 * y0;
        }
        return Math.Clamp(Math.Abs(area) / 2, 0, 1);
    }

    private static int Clip(Span<(double X, double Y)> input, int count, Span<(double X, double Y)> output, Func<(double X, double Y), double> inside)
    {
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            var p = input[i];
            var q = input[(i + 1) % count];
            var dp = inside(p);
            var dq = inside(q);
            if (dp >= 0) output[n++] = p;
            if ((dp >= 0) != (dq >= 0))
            {
                var t = dp / (dp - dq);
                output[n++] = (p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t);
            }
        }
        return n;
    }
}
