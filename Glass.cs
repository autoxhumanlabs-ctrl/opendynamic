using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Island;

// Liquid Glass for the open card. When the card opens we grab what's on screen behind it (before it covers
// anything), then:
//   frost      - box blur + a little extra saturation, like frosted glass
//   refraction - near the edges the background is sampled from further inside, so it bends like a thick lens
//   prism      - red and blue bend by slightly different amounts at the rim (chromatic fringe)
//   liquid     - the lens edge ripples and swells with the bass while music plays
//   shine      - a specular highlight on the rim that follows the cursor like a light source
// Everything is computed at half resolution (it is blurred anyway) into one bitmap that is scaled up.
public partial class MainWindow
{
    bool glassOn = true, prism = true, liquid = true;
    double frost = 0.12, tintAmt = 0.15, refract = 0.8, cornerMax = 30, shine = 0.8;

    const int GW = 264, GH = 230;                       // half-res of the 528 x 460 DIP glass area
    const double GlassPad = 24;                         // glass extends 24 DIP each side of the card (shoulders)
    int[]? glassRaw, glassBase, glassTinted;
    readonly int[] glassOut = new int[GW * GH];
    readonly WriteableBitmap glassBmp = new(GW, GH, 96, 96, PixelFormats.Bgr32, null);
    readonly RadialGradientBrush edgeBrush = new() { RadiusX = 0.32, RadiusY = 0.9 };
    double glassPhase;

    void InitGlass()
    {
        GlassImg.Source = glassBmp;
        RenderOptions.SetBitmapScalingMode(GlassImg, BitmapScalingMode.Linear);
        GlassEdge.Stroke = edgeBrush;
        SetShine();
    }

    void ApplyGlassVisibility()
    {
        bool on = glassOn && glassBase != null;
        GlassLayer.Visibility = GlassSheen.Visibility = GlassEdge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Ambient.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        RimPath.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    void SetShine()
    {
        byte a = (byte)(255 * shine), b = (byte)(70 * shine);
        edgeBrush.GradientStops = new GradientStopCollection
        {
            new(Color.FromArgb(a, 255, 255, 255), 0), new(Color.FromArgb(b, 255, 255, 255), 1),
        };
        GlassSheen.Opacity = 0.4 + 0.6 * shine;
    }

    // Light source = cursor: the highlight slides along the rim toward wherever the pointer is.
    void UpdateShine(double cx, double cy)
    {
        double iw = Island.ActualWidth, ih = Math.Max(Island.ActualHeight, 1);
        if (iw < 1) return;
        double left = Left + (Width - iw) / 2, top = pill ? 8 : 0;
        var c = new Point(Math.Clamp((cx - left) / iw, -0.1, 1.1), Math.Clamp((cy - top) / ih, 0, 1.2));
        edgeBrush.Center = c;
        edgeBrush.GradientOrigin = c;
    }

    // ---------- capture + frost (once per open, or when frost changes) ----------

    void CaptureGlass()
    {
        if (!glassOn || hwnd == IntPtr.Zero) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pw = (int)(GW * 2 * s), ph = (int)(GH * 2 * s);
        int px0 = (int)Math.Round((Left + Width / 2 - GW) * s), py0 = (int)Math.Round((pill ? 8 : 0) * s);
        int[] src;
        try { src = ScreenGrab(px0, py0, pw, ph); } catch { return; }

        glassRaw ??= new int[GW * GH];
        for (int y = 0; y < GH; y++)                    // box-average down to half-res DIP
        {
            int sy0 = y * ph / GH, sy1 = Math.Max(sy0 + 1, (y + 1) * ph / GH);
            for (int x = 0; x < GW; x++)
            {
                int sx0 = x * pw / GW, sx1 = Math.Max(sx0 + 1, (x + 1) * pw / GW);
                int r = 0, g = 0, b = 0, n = 0;
                for (int yy = sy0; yy < sy1; yy++)
                    for (int xx = sx0; xx < sx1; xx++)
                    {
                        int p = src[yy * pw + xx];
                        r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++;
                    }
                glassRaw[y * GW + x] = (r / n << 16) | (g / n << 8) | b / n;
            }
        }
        // Our own collapsed notch was on screen at the top: paint over it with the row just below it.
        int fillY = Math.Min(GH - 1, (int)Math.Ceiling((CompactH + 6) / 2.0));
        int half = (int)Math.Ceiling((CompactW / 2 + 26) / 2.0);
        for (int y = 0; y < fillY; y++)
            for (int x = Math.Max(0, GW / 2 - half); x < Math.Min(GW, GW / 2 + half); x++)
                glassRaw[y * GW + x] = glassRaw[fillY * GW + x];
        Frost();
    }

    void Frost()
    {
        if (glassRaw is null) return;
        int n = GW * GH;
        var r = new int[n]; var g = new int[n]; var b = new int[n]; var tmp = new int[n];
        for (int i = 0; i < n; i++) { int p = glassRaw[i]; r[i] = (p >> 16) & 255; g[i] = (p >> 8) & 255; b[i] = p & 255; }
        int rad = (int)Math.Round(frost * 10);
        foreach (var ch in new[] { r, g, b })
            for (int pass = 0; pass < 3 && rad > 0; pass++) BoxBlur(ch, tmp, rad);
        glassBase ??= new int[n];
        for (int i = 0; i < n; i++)
        {
            double lum = 0.3 * r[i] + 0.59 * g[i] + 0.11 * b[i];   // +25% saturation: glass looks juicier than the source
            int R = Clamp255(lum + (r[i] - lum) * 1.25), G = Clamp255(lum + (g[i] - lum) * 1.25), B = Clamp255(lum + (b[i] - lum) * 1.25);
            glassBase[i] = (R << 16) | (G << 8) | B;
        }
        Tint();
    }

    void Tint()
    {
        if (glassBase is null) return;
        glassTinted ??= new int[GW * GH];
        double k = 1 - tintAmt;
        for (int i = 0; i < glassTinted.Length; i++)
        {
            int p = glassBase[i];
            glassTinted[i] = ((int)(((p >> 16) & 255) * k) << 16) | ((int)(((p >> 8) & 255) * k) << 8) | (int)((p & 255) * k);
        }
        ApplyGlassVisibility();
        RenderGlass();
    }

    static int Clamp255(double v) => v < 0 ? 0 : v > 255 ? 255 : (int)v;

    static void BoxBlur(int[] ch, int[] tmp, int r)
    {
        int win = 2 * r + 1;
        for (int y = 0; y < GH; y++)                    // horizontal, running sum, clamped edges
        {
            int row = y * GW, sum = 0;
            for (int k = -r; k <= r; k++) sum += ch[row + Math.Clamp(k, 0, GW - 1)];
            for (int x = 0; x < GW; x++)
            {
                tmp[row + x] = sum / win;
                sum += ch[row + Math.Min(x + r + 1, GW - 1)] - ch[row + Math.Max(x - r, 0)];
            }
        }
        for (int x = 0; x < GW; x++)                    // vertical
        {
            int sum = 0;
            for (int k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, GH - 1) * GW + x];
            for (int y = 0; y < GH; y++)
            {
                ch[y * GW + x] = sum / win;
                sum += tmp[Math.Min(y + r + 1, GH - 1) * GW + x] - tmp[Math.Max(y - r, 0) * GW + x];
            }
        }
    }

    // ---------- refraction / prism / liquid (cheap: only the rim band is recomputed) ----------

    void RenderGlass()
    {
        if (glassTinted is null || glassBase is null) return;
        int rows = Math.Clamp((int)Math.Ceiling(th / 2) + 1, 8, GH);
        double x0 = GlassPad / 2, x1 = GW - 1 - GlassPad / 2;          // card body edges in glass pixels
        double rr = Math.Min(Math.Clamp(th / 2.6, 10, cornerMax) / 2, rows / 2.0);
        double band = 13, k = refract * band * (liquid ? 1 + 0.5 * bassLv + 0.3 * beatPulse : 1);
        double tk = 1 - tintAmt, ph = glassPhase;
        Array.Copy(glassTinted, glassOut, rows * GW);
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < GW; x++)
            {
                double nx, ny, d;
                bool corner = !pill && y > rows - 1 - rr && (x < x0 + rr || x > x1 - rr);
                if (corner)
                {
                    double cx = x < x0 + rr ? x0 + rr : x1 - rr, cy = rows - 1 - rr, vx = cx - x, vy = cy - y;
                    double len = Math.Sqrt(vx * vx + vy * vy);
                    d = rr - len; nx = len > 1e-6 ? vx / len : 0; ny = len > 1e-6 ? vy / len : -1;
                }
                else
                {
                    double dl = x - x0, dr = x1 - x, db = rows - 1 - y;
                    if (dl <= dr && dl <= db) { d = dl; nx = 1; ny = 0; }
                    else if (dr <= db) { d = dr; nx = -1; ny = 0; }
                    else { d = db; nx = 0; ny = -1; }
                }
                if (d < 0 || d >= band || (!pill && y < GlassPad / 2 && ny == 0)) continue;   // interior / shoulders: plain glass
                double t = 1 - d / band;
                double disp = k * t * t + (liquid ? Math.Sin(d * 0.9 - ph) * 1.4 * t : 0);
                double sx = x + nx * disp, sy = y + ny * disp;
                int R, G, B;
                if (prism)
                {
                    double f = disp * 0.22;
                    R = Sample(sx + nx * f, sy + ny * f, 16); G = Sample(sx, sy, 8); B = Sample(sx - nx * f, sy - ny * f, 0);
                }
                else { R = Sample(sx, sy, 16); G = Sample(sx, sy, 8); B = Sample(sx, sy, 0); }
                double lift = tk * (1 + 0.45 * t * t);          // thick glass catches more light at the rim
                glassOut[y * GW + x] = (Clamp255(R * lift) << 16) | (Clamp255(G * lift) << 8) | Clamp255(B * lift);
            }
        glassBmp.WritePixels(new Int32Rect(0, 0, GW, rows), glassOut, GW * 4, 0);
    }

    int Sample(double x, double y, int shift)
    {
        x = Math.Clamp(x, 0, GW - 1.001); y = Math.Clamp(y, 0, GH - 1.001);
        int ix = (int)x, iy = (int)y, i = iy * GW + ix;
        double fx = x - ix, fy = y - iy;
        var s = glassBase!;
        double a = (s[i] >> shift) & 255, b = (s[i + 1] >> shift) & 255, c = (s[i + GW] >> shift) & 255, d = (s[i + GW + 1] >> shift) & 255;
        return (int)(a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy);
    }

    // Edge of the notch shape without its top line (the top is the screen edge): shoulders, sides, bottom.
    static Geometry EdgeShape(double w, double h, double r, double e, bool pill)
    {
        if (pill) return new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        e = Math.Min(e, h - r);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(w + 2 * e, 0), false, false);
            c.ArcTo(new Point(w + e, e), new Size(e, e), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(w + e, h - r), true, false);
            c.ArcTo(new Point(w + e - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(e + r, h), true, false);
            c.ArcTo(new Point(e, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(e, e), true, false);
            c.ArcTo(new Point(0, 0), new Size(e, e), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    // ---------- screen grab (GDI) ----------

    static int[] ScreenGrab(int x, int y, int w, int h)
    {
        IntPtr sdc = GetDC(IntPtr.Zero), mdc = CreateCompatibleDC(sdc);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(sdc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mdc, dib);
        try
        {
            BitBlt(mdc, 0, 0, w, h, sdc, x, y, 0x00CC0020);    // SRCCOPY
            var px = new int[w * h];
            Marshal.Copy(bits, px, 0, px.Length);
            return px;
        }
        finally
        {
            SelectObject(mdc, old); DeleteObject(dib); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
