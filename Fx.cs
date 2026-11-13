using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Island;

// Experimental card background: three aurora blobs in the cover's colours drifting and breathing with the bass,
// plus embers rising from the wave that speed up with the bass and burst out on every kick.
// Toggle: Settings > "Aurora background". Off = the classic look, and none of this runs.
public partial class MainWindow
{
    bool auroraOn = true;
    const int EmberN = 28;
    // Aurora is drawn by hand into a tiny bitmap and scaled up: three big gradient ellipses cost ~30% of a core
    // in software rendering, this costs almost nothing and looks the same (it is all soft blur anyway).
    const int AW = 96, AH = 64;
    readonly WriteableBitmap aurora = new(AW, AH, 96, 96, PixelFormats.Pbgra32, null);
    readonly int[] auroraPx = new int[AW * AH];
    Color fxA = Colors.White, fxB = Colors.White;
    readonly Ellipse[] embers = new Ellipse[EmberN];
    readonly TranslateTransform[] emberMove = new TranslateTransform[EmberN];
    readonly double[] ex = new double[EmberN], ey = new double[EmberN], ev = new double[EmberN], eph = new double[EmberN];
    readonly Random rnd = new();
    double fxT;
    int fxBeats;

    void InitFx()
    {
        var img = new Image { Source = aurora, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
        img.SetBinding(WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = Fx });
        img.SetBinding(HeightProperty, new System.Windows.Data.Binding("ActualHeight") { Source = Fx });
        Fx.Children.Add(img);
        for (int i = 0; i < EmberN; i++)
        {
            double d = 2 + rnd.NextDouble() * 3.5;
            emberMove[i] = new TranslateTransform();
            embers[i] = new Ellipse { Width = d, Height = d, RenderTransform = emberMove[i], Opacity = 0 };
            embers[i].SetResourceReference(Shape.FillProperty, i % 3 == 0 ? "EmberB" : "EmberA");
            Fx.Children.Add(embers[i]);
            Respawn(i, 300 * rnd.NextDouble());
        }
    }

    void Respawn(int i, double y)
    {
        ex[i] = rnd.NextDouble() * ExpW;
        ey[i] = y;
        ev[i] = 14 + rnd.NextDouble() * 22;            // px/s upward
        eph[i] = rnd.NextDouble() * 6.28;
    }

    void DrawAurora()
    {
        double size = 0.85 + 0.45 * bassLv + 0.25 * beatPulse, glow = 0.45 + 0.55 * bassLv;
        Span<double> bx = stackalloc double[3], by = stackalloc double[3];
        for (int i = 0; i < 3; i++)
        {
            bx[i] = AW * (0.2 + 0.3 * i) + Math.Sin(fxT * (0.31 + 0.07 * i) + i * 2.1) * AW * 0.23;
            by[i] = AH * 0.45 + Math.Cos(fxT * (0.23 + 0.05 * i) + i) * AH * 0.25;
        }
        double rx = AW * 0.31 * size, ry = AH * 0.36 * size;
        for (int y = 0; y < AH; y++)
            for (int x = 0; x < AW; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int i = 0; i < 3; i++)
                {
                    double dx = (x - bx[i]) / rx, dy = (y - by[i]) / ry, d = dx * dx + dy * dy;
                    if (d >= 1) continue;
                    double wgt = (1 - d) * (1 - d) * glow * (i == 1 ? 0.7 : 0.78);
                    var c = i == 1 ? fxB : fxA;
                    r += c.R * wgt; g += c.G * wgt; b += c.B * wgt; a += 255 * wgt;
                }
                if (a > 255) { double k = 255 / a; r *= k; g *= k; b *= k; a = 255; }
                auroraPx[y * AW + x] = ((int)a << 24) | ((int)Math.Min(r, a) << 16) | ((int)Math.Min(g, a) << 8) | (int)Math.Min(b, a);
            }
        aurora.WritePixels(new Int32Rect(0, 0, AW, AH), auroraPx, AW * 4, 0);
    }

    void SetAurora(bool on)
    {
        auroraOn = on;
        Fx.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetFxColours(Color a, Color b)
    {
        fxA = a; fxB = b;
        var la = Color.FromRgb((byte)((a.R + 255) / 2), (byte)((a.G + 255) / 2), (byte)((a.B + 255) / 2));
        var lb = Color.FromRgb((byte)((b.R + 255) / 2), (byte)((b.G + 255) / 2), (byte)((b.B + 255) / 2));
        Resources["EmberA"] = new SolidColorBrush(la);
        Resources["EmberB"] = new SolidColorBrush(lb);
    }

    void DrawFx(double dt)
    {
        if (!auroraOn) return;
        double H = Math.Max(Fx.ActualHeight, 120);
        if (playing) fxT += dt * (0.6 + 1.4 * vocalLv);

        // Aurora: slow Lissajous drift, swelling and brightening on bass.
        DrawAurora();

        // Embers: rise faster with the bass; every kick launches a burst from the wave.
        int beats = viz.Beats, burst = 0;
        if (beats != fxBeats) { fxBeats = beats; if (playing && viz.Live) burst = 7; }
        double speed = playing ? 1 + 3.2 * bassLv : 0.25;
        for (int i = 0; i < EmberN; i++)
        {
            if (burst > 0 && ey[i] < H * 0.45) { Respawn(i, H - 10); ev[i] = 70 + rnd.NextDouble() * 80; burst--; }
            ey[i] -= ev[i] * speed * dt;
            ev[i] += (20 - ev[i]) * Math.Min(1, dt * 1.2);       // burst speed settles back to a drift
            ex[i] += Math.Sin(fxT * 1.7 + eph[i]) * 14 * dt;
            if (ey[i] < -8) Respawn(i, H + rnd.NextDouble() * 30);
            double prog = Math.Clamp(1 - ey[i] / H, 0, 1);         // 0 at the bottom, 1 at the top
            embers[i].Opacity = Math.Sin(Math.PI * prog) * (0.45 + 0.55 * bassLv);
            emberMove[i].X = ex[i];
            emberMove[i].Y = ey[i];
        }
    }
}
