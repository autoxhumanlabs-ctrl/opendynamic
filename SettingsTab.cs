using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace Island;

// Settings tab: everything saved in HKCU\Software\Island and applied live.
public partial class MainWindow
{
    double waveStrength = 1.3;
    bool hideNear = true, peekOnTrack = true, hideFullscreen = true, saver;
    string colour = "Album";
    Color albumA = Colors.White, albumB = Colors.White;

    static readonly (string Name, Color A, Color B)[] Palettes =
    {
        ("Album", Colors.Transparent, Colors.Transparent),
        ("Violet", Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xEC, 0x48, 0x99)),
        ("Ocean", Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0x63, 0x66, 0xF1)),
        ("Mint", Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x22, 0xD3, 0xEE)),
        ("Sunset", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xF4, 0x3F, 0x5E)),
        ("Gold", Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xF5, 0x9E, 0x0B)),
        ("Mono", Colors.White, Color.FromRgb(0x9C, 0xA3, 0xAF)),
    };

    double GetD(string k, double def) => double.TryParse(settings.GetValue(k) as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    bool GetB(string k, bool def) => settings.GetValue(k) is int i ? i == 1 : def;
    void Save(string k, double v) => settings.SetValue(k, v.ToString(CultureInfo.InvariantCulture));
    void Save(string k, bool v) => settings.SetValue(k, v ? 1 : 0);

    void InitSettings()
    {
        waveStrength = GetD("WaveStrength", 1.3);
        double beat = GetD("BeatSens", 0.5);
        hideNear = GetB("HideNear", true);
        peekOnTrack = GetB("PeekOnTrack", true);
        hideFullscreen = GetB("HideFullscreen", true);
        saver = GetB("Saver", false);
        colour = settings.GetValue("Colour") as string ?? "Album";
        viz.BeatRatio = BeatRatioFor(beat);

        SetWave.Value = waveStrength;
        SetWave.ValueChanged += (_, e) => { waveStrength = e.NewValue; Save("WaveStrength", e.NewValue); };
        SetBeat.Value = beat;
        SetBeat.ValueChanged += (_, e) => { viz.BeatRatio = BeatRatioFor(e.NewValue); Save("BeatSens", e.NewValue); };

        foreach (var p in Palettes)
        {
            var dot = new Ellipse
            {
                Width = 20, Height = 20, Margin = new Thickness(0, 0, 9, 0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = p.Name,
                Fill = p.Name == "Album" ? AlbumSwatch() : new LinearGradientBrush(p.A, p.B, 45),
                StrokeThickness = 2,
            };
            dot.MouseLeftButtonUp += (_, _) => { colour = p.Name; settings.SetValue("Colour", p.Name); ApplyAccent(); };
            Swatches.Children.Add(dot);
        }

        InitLookPage();
        Toggle("Hide when cursor is near", hideNear, v => { hideNear = v; Save("HideNear", v); });
        Toggle("Pop up on song change", peekOnTrack, v => { peekOnTrack = v; Save("PeekOnTrack", v); });
        Toggle("Hide in fullscreen video/games", hideFullscreen, v => { hideFullscreen = v; Save("HideFullscreen", v); SlowTick(); });
        Toggle("Battery saver (30 fps)", saver, v => { saver = v; Save("Saver", v); });
        Toggle("Floating pill", pill, v => { pill = v; Save("Pill", v); UpdateTargets(); });
        Toggle("Synced lyrics (lrclib.net)", lyricsOn, v =>
        {
            lyricsOn = v; Save("Lyrics", v); UpdateTargets();
            if (HasMedia) _ = LoadLyrics(TrackTitle.Text, Artist.Text);
        });
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            Toggle("Start with Windows", run?.GetValue("Island") != null, v =>
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (v) k.SetValue("Island", $"\"{Environment.ProcessPath}\""); else k.DeleteValue("Island", false);
            });
    }

    // Sensitivity 0..1 -> bass jump needed for a beat: 1.65x (only big kicks) .. 1.2x (every hit).
    static double BeatRatioFor(double s) => 1.65 - 0.45 * Math.Clamp(s, 0, 1);

    static Brush AlbumSwatch()
    {
        var b = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0xF4, 0x3F, 0x5E), 0), new(Color.FromRgb(0xFB, 0xBF, 0x24), .35),
            new(Color.FromRgb(0x34, 0xD3, 0x99), .65), new(Color.FromRgb(0x63, 0x66, 0xF1), 1),
        }, 45);
        b.Freeze();
        return b;
    }

    void Toggle(string label, bool on, Action<bool> changed, Panel? host = null)
    {
        var cb = new CheckBox { Style = (Style)FindResource("Switch"), Content = label, IsChecked = on };
        cb.Click += (_, _) => changed(cb.IsChecked == true);
        (host ?? Toggles).Children.Add(cb);
    }

    void LookSlider(string label, double min, double max, double value, Action<double> changed)
    {
        var row = new Grid { Height = 26 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center });
        var sl = new Slider { Style = (Style)FindResource("SeekBar"), Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
        sl.ValueChanged += (_, e) => changed(e.NewValue);
        Grid.SetColumn(sl, 1);
        row.Children.Add(sl);
        LookSliders.Children.Add(row);
    }

    void InitLookPage()
    {
        glassOn = GetB("Glass", true);
        prism = GetB("Prism", true);
        liquid = GetB("Liquid", true);
        frost = GetD("Frost", frost);
        tintAmt = GetD("GlassTint", tintAmt);
        refract = GetD("Refract", refract);
        shine = GetD("Shine", shine);
        cornerMax = GetD("Corners", cornerMax);
        SetShine();

        PageGeneral.Checked += (_, _) => { GeneralPage.Visibility = Visibility.Visible; LookPage.Visibility = Visibility.Collapsed; UpdateTargets(); };
        PageLook.Checked += (_, _) => { GeneralPage.Visibility = Visibility.Collapsed; LookPage.Visibility = Visibility.Visible; UpdateTargets(); };

        LookSlider("Frost", 0, 1, frost, v => { frost = v; Save("Frost", v); Frost(); });
        LookSlider("Darkness", 0, 0.8, tintAmt, v => { tintAmt = v; Save("GlassTint", v); Tint(); });
        LookSlider("Refraction", 0, 1.6, refract, v => { refract = v; Save("Refract", v); RenderGlass(); });
        LookSlider("Edge shine", 0, 1, shine, v => { shine = v; Save("Shine", v); SetShine(); });
        LookSlider("Roundness", 12, 46, cornerMax, v => { cornerMax = v; Save("Corners", v); Apply(); RenderGlass(); });

        Toggle("Liquid Glass", glassOn, v =>
        {
            glassOn = v; Save("Glass", v);
            if (v && expanded) CaptureGlass();
            ApplyGlassVisibility();
        }, LookToggles);
        Toggle("Prism edges", prism, v => { prism = v; Save("Prism", v); RenderGlass(); }, LookToggles);
        Toggle("Liquid motion (bass)", liquid, v => { liquid = v; Save("Liquid", v); RenderGlass(); }, LookToggles);
        SetAurora(GetB("Aurora", true));
        Toggle("Aurora background", auroraOn, v => { SetAurora(v); Save("Aurora", v); }, LookToggles);
    }

    void ApplyAccent()
    {
        foreach (Ellipse e in Swatches.Children) e.Stroke = (string)e.ToolTip == colour ? Brushes.White : null;
        foreach (var p in Palettes)
            if (p.Name == colour && p.Name != "Album") { SetAccent(p.A, p.B); return; }
        SetAccent(albumA, albumB);
    }

    // Hide only for real fullscreen: D3D games / presentation mode, or a borderless window covering the whole
    // monitor the notch is on. The desktop, the taskbar and maximized windows never count.
    bool FullscreenInFront()
    {
        if (SHQueryUserNotificationState(out int st) == 0 && st is 3 or 4) return true;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == hwnd || IsZoomed(fg)) return false;
        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var mon = MonitorFromWindow(fg, 2);
        if (mon != MonitorFromWindow(hwnd, 2)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(fg, out var r)) return false;
        return r.L <= mi.M.L && r.T <= mi.M.T && r.R >= mi.M.R && r.B >= mi.M.B;
    }

    struct RECT { public int L, T, R, B; }
    struct MONITORINFO { public int cbSize; public RECT M, W; public int Flags; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
}
