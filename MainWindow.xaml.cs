using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Media;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Island;

public record ShelfItem(string FullPath)
{
    public string Name => System.IO.Path.GetFileName(FullPath.TrimEnd('\\')) is { Length: > 0 } n ? n : FullPath;
}

public partial class MainWindow : Window
{
    const double ExpW = 480;
    static readonly Geometry PlayGeo = Geometry.Parse("M8,5 L19,12 L8,19 Z");
    static readonly Geometry PauseGeo = Geometry.Parse("M7,5 H10 V19 H7 Z M14,5 H17 V19 H14 Z");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly ObservableCollection<ShelfItem> shelf = new();
    readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(15) };   // one GetCursorPos: free, and reacts a frame sooner
    readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer slowTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer volTimer = new() { Interval = TimeSpan.FromSeconds(1.3) };
    readonly RegistryKey settings = Registry.CurrentUser.CreateSubKey(@"Software\Island");
    readonly Visualizer viz = new();
    readonly float[] disp = new float[Visualizer.B];

    GlobalSystemMediaTransportControlsSessionManager? mgr;
    Session? session;
    bool expanded, interactive, hidden, playing, seeking, pill, lyricsOn, dragHover, draggingOut, frames, shuffleOn;
    MediaPlaybackAutoRepeatMode repeat;
    string lastTrack = "", lyricsKey = "";
    int propsVersion, switchIndex, lyricIdx = -1;
    TimeSpan tlPos, tlEnd;
    DateTimeOffset tlUpdated, tlReceived;
    DateTime peekUntil, edgeSince, vizUntil;
    bool near;
    List<(TimeSpan T, string Line)>? lyrics;
    IntPtr hwnd;

    // Springs: island width/height and the notch's vertical offset (slides up into the screen edge).
    double w = 180, h = 30, vw, vh, tw = 180, th = 30, oy, voy, ty;
    bool animating;
    TimeSpan? lastFrame;
    double vizAcc;

    bool HasMedia => session != null && TrackTitle.Text.Length > 0;
    bool VizActive => IsVisible && (playing || DateTime.Now < vizUntil);

    public MainWindow()
    {
        InitializeComponent();
        pill = settings.GetValue("Pill") is int p && p == 1;
        lyricsOn = settings.GetValue("Lyrics") is int l && l == 1;
        ShelfItems.ItemsSource = shelf;
        shelf.CollectionChanged += (_, _) => RefreshShelf();
        MakeBars(MiniBars, 4, 14);
        MakeBars(BigBars, 5, 28);

        poll.Tick += (_, _) => Poll();
        progressTimer.Tick += (_, _) => UpdateProgress();
        slowTimer.Tick += (_, _) => SlowTick();
        volTimer.Tick += (_, _) => { volTimer.Stop(); Fade(VolPill, 0, 300); };

        Island.DragEnter += OnDragEnter;
        Island.DragOver += OnDragEnter;
        Island.Drop += OnDrop;
        Island.MouseWheel += OnWheel;

        PrevBtn.Click += (_, _) => Cmd(s => s.TrySkipPreviousAsync());
        NextBtn.Click += (_, _) => Cmd(s => s.TrySkipNextAsync());
        PlayBtn.Click += (_, _) =>
        {
            SetPlaying(!playing);                       // optimistic: UI reacts instantly, the player's event confirms
            Cmd(s => s.TryTogglePlayPauseAsync());
        };
        ShuffleBtn.Click += (_, _) => { var on = !shuffleOn; Cmd(s => s.TryChangeShuffleActiveAsync(on)); };
        RepeatBtn.Click += (_, _) =>
        {
            var next = repeat switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None,
            };
            Cmd(s => s.TryChangeAutoRepeatModeAsync(next));
        };
        SwitchBtn.Click += (_, _) =>
        {
            var all = mgr?.GetSessions();
            if (all is null || all.Count < 2) return;
            switchIndex = (switchIndex + 1) % all.Count;
            Attach(all[switchIndex]);
        };
        ClearShelf.Click += (_, _) => shelf.Clear();

        Seek.PreviewMouseLeftButtonDown += (_, _) => seeking = true;
        Seek.ValueChanged += (_, _) => { if (seeking) Elapsed.Text = Logic.Fmt(TimeSpan.FromSeconds(Seek.Value)); };
        Seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!seeking) return;
            seeking = false;
            var target = TimeSpan.FromSeconds(Seek.Value);
            tlPos = target; tlUpdated = default; tlReceived = DateTimeOffset.Now;
            Cmd(s => s.TryChangePlaybackPositionAsync(target.Ticks));
        };

        SourceInitialized += (_, _) =>
        {
            // Tool window (no Alt-Tab), never takes focus, and click-through until you open it.
            hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        };
        Loaded += async (_, _) =>
        {
            FitWindow(false);
            Place();
            Apply();
            SlowTick();
            slowTimer.Start();
            poll.Start();
            await InitMedia();
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(Place);
        BuildMenu();
        InitExtras();
        InitFx();
        InitGlass();
        InitSettings();
        ApplyAccent();
    }

    void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    // A layered window repaints its whole surface every animated frame, so stay short while collapsed and
    // only grow for the card. Only the HEIGHT changes: changing width means re-centring (a second move),
    // and for one frame the notch would jump sideways. Width stays fixed, the top edge never moves -> no jump.
    const double WinW = ExpW + 80;                      // card + room for the spring's overshoot + ears
    void FitWindow(bool big)
    {
        double H = Math.Ceiling(th) + (pill ? 8 : 0) + (big ? 34 : 14);
        if (Width != WinW) { Width = WinW; Place(); }
        if (Height != H) Height = H;
    }

    // ---------- cursor: open from the top edge, get out of the way otherwise ----------

    // Polling (15 ms, one GetCursorPos) instead of mouse events: the notch is click-through while collapsed,
    // so it never receives mouse events, and that is exactly what lets you click the browser tab under it.
    // Smoothness: zones use the TARGET sizes, never the animating ones (otherwise the zone moves with the
    // spring and the notch flickers or sticks), and "near" is wider to leave than to enter (hysteresis).
    void Poll()
    {
        if (!IsVisible || !GetCursorPos(out var p)) return;
        ApplyHitStyle();
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double cx = (Left + Width / 2) * s, top = (pill ? 8 : 0) * s;
        double dx = Math.Abs(p.X - cx);
        var now = DateTime.Now;

        double nHalf = CompactW / 2 * s, nBottom = top + CompactH * s;
        bool over = expanded
            ? dx <= tw / 2 * s + 8 * s && p.Y <= top + th * s + 8 * s
            : dx <= nHalf + 8 * s && p.Y <= nBottom + 8 * s;
        bool atEdge = p.Y <= 1 && dx <= Math.Max(nHalf, 110 * s) + 24 * s;
        double mx = (near ? 72 : 48) * s, my = (near ? 80 : 56) * s;
        if (expanded && glassOn) UpdateShine(p.X / s, p.Y / s);
        near = hideNear && dx <= nHalf + mx && p.Y <= nBottom + my;

        if (expanded)
        {
            if (!interactive)                                   // track-change peek
            {
                if (atEdge) { SetInteractive(true); return; }   // user wants it: keep it open
                if (over || now >= peekUntil) { SetExpanded(false); SetHidden(over || near); }
                return;
            }
            if (over || atEdge) return;
            SetExpanded(false);
            SetHidden(near);
            return;
        }
        if (atEdge)
        {
            if (edgeSince == default) edgeSince = now;
            else if ((now - edgeSince).TotalMilliseconds >= 60) { edgeSince = default; SetHidden(false); SetExpanded(true, true); }
            return;
        }
        edgeSince = default;
        SetHidden(near);
    }

    double CompactW => HasMedia ? 270 : 180;
    double CompactH => HasMedia ? 32 : 30;

    void SetInteractive(bool on)
    {
        if (interactive == on) return;
        interactive = on;
        Expanded.IsHitTestVisible = on;
        ApplyHitStyle();
    }

    // Click-through (WS_EX_TRANSPARENT) unless the card is open for you. Re-checked every poll because
    // WPF rewrites extended styles on some property changes; one GetWindowLong is nanoseconds.
    void ApplyHitStyle()
    {
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        int want = (interactive ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (want != ex) SetWindowLong(hwnd, GWL_EXSTYLE, want);
    }

    void SetHidden(bool v)
    {
        if (hidden == v) return;
        hidden = v;
        UpdateTargets();
    }

    void SetExpanded(bool e, bool interact = false)
    {
        SetInteractive(e && interact);
        if (expanded == e) return;
        if (e) CaptureGlass();                      // grab what's behind BEFORE the card covers it
        expanded = e;
        if (e) { UpdateClock(); UpdateProgress(); UpdateLyric(true); }
        else dragHover = false;
        RefreshShelf();
        SyncTimers();
    }

    public void ShowFromLauncher() { SetHidden(false); Peek(); }

    void Peek()
    {
        if (expanded || hidden) return;             // hidden = cursor is near: don't cover what you're doing
        peekUntil = DateTime.Now.AddSeconds(3);
        TabMusic.IsChecked = true;                  // a track change shows the player, whatever tab was open
        SetHidden(false);
        SetExpanded(true, false);
    }

    // ---------- spring animation ----------

    void UpdateTargets()
    {
        bool media = HasMedia;
        ApplyTabVisibility(media);
        Compact.Visibility = media ? Visibility.Visible : Visibility.Hidden;
        IdleClock.Visibility = media ? Visibility.Collapsed : Visibility.Visible;
        LyricLine.Visibility = lyricsOn && media ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            Expanded.Measure(new Size(ExpW, double.PositiveInfinity));
            tw = ExpW;
            th = Math.Ceiling(Expanded.DesiredSize.Height);
            if (glassOn) RenderGlass();                 // the lens rim follows the card's new height
        }
        else
        {
            tw = CompactW;
            th = CompactH;
        }
        ty = hidden && !expanded ? -(th + (pill ? 8 : 0)) + 2 : 0;   // tuck up, leave a 2px lip as a hint
        if (expanded || tw > w || th > h) FitWindow(true);           // grow before animating out; shrink once settled
        animating = true;
        EnsureFrames();
    }

    void EnsureFrames()
    {
        if (frames || !(animating || VizActive)) return;
        frames = true;
        lastFrame = null;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == lastFrame) return;                     // Rendering can fire more than once per frame
        double dt = lastFrame is null ? 1 / 60.0 : Math.Min((t - lastFrame.Value).TotalSeconds, 1 / 20.0);
        lastFrame = t;

        if (animating)
        {
            Spring(ref w, ref vw, tw, dt);
            Spring(ref h, ref vh, th, dt);
            Spring(ref oy, ref voy, ty, dt);
            if (Math.Abs(w - tw) < 0.3 && Math.Abs(h - th) < 0.3 && Math.Abs(oy - ty) < 0.3
                && Math.Abs(vw) < 2 && Math.Abs(vh) < 2 && Math.Abs(voy) < 2)
            {
                w = tw; h = th; oy = ty; vw = vh = voy = 0;
                animating = false;
                if (!expanded) FitWindow(false);
            }
            Apply();
        }

        // Visualizer is time-capped (60 fps open, 30 collapsed): on a 144 Hz screen per-frame updates
        // cost ~5x the CPU for no visible gain. The spring above still runs every frame, so motion stays silky.
        bool viz = VizActive;
        vizAcc += dt;
        if (viz && vizAcc >= (expanded && !saver ? 1 / 61.0 : 1 / 31.0)) { UpdateViz(vizAcc); vizAcc = 0; }
        if (!animating && !viz)
        {
            frames = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    // Slightly under-damped (critical ~39) -> a small, quick overshoot like the iPhone island.
    static void Spring(ref double x, ref double v, double target, double dt)
    {
        const double k = 380, c = 30;
        v += (k * (target - x) - c * v) * dt;
        x += v * dt;
    }

    void Apply()
    {
        double W = Math.Max(w, 20), H = Math.Max(h, 10);
        // Shoulders: concave curves where the notch meets the screen edge, growing with the card so the open
        // card flows out of the top edge like a real notch. Part of the island's own shape, so they get the
        // card's background (glass/ambient), not a black patch.
        double e = pill ? 0 : Math.Clamp(H * 0.1, 10, 22);
        Island.Width = W + 2 * e;
        Island.Height = H;
        Island.Margin = new Thickness(0, pill ? 8 : 0, 0, 0);
        Compact.Margin = new Thickness(6 + e, 0, 12 + e, 0);
        RowShift.Y = oy;
        double r = pill ? Math.Min(H / 2, 32) : Math.Clamp(H / 2.6, 10, cornerMax);
        Island.Clip = NotchShape(W, H, r, e, pill);          // clip children (glow, wave) to the notch shape
        RimPath.Data = RimShape(W, H, r, e, pill);
        if (glassOn) GlassEdge.Data = EdgeShape(W, H, r, e, pill);
        Compact.Opacity = IdleClock.Opacity = Clamp01(1 - (H - 34) / 24);
        Expanded.Opacity = GlassEdge.Opacity = Clamp01((H - 80) / 70);
    }

    // w = body width; e = shoulder radius (body starts at x = e). Concave shoulders at the top, rounded bottom.
    static Geometry NotchShape(double w, double h, double r, double e, bool pill)
    {
        if (pill) return new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        e = Math.Min(e, h - r);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true, true);
            c.LineTo(new Point(w + 2 * e, 0), false, false);
            c.ArcTo(new Point(w + e, e), new Size(e, e), 0, false, SweepDirection.Counterclockwise, false, false);
            c.LineTo(new Point(w + e, h - r), false, false);
            c.ArcTo(new Point(w + e - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(e + r, h), false, false);
            c.ArcTo(new Point(e, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(e, e), false, false);
            c.ArcTo(new Point(0, 0), new Size(e, e), 0, false, SweepDirection.Counterclockwise, false, false);
        }
        g.Freeze();
        return g;
    }

    // Bottom edge (incl. the rounded corners) for the accent hairline; half the stroke falls outside the clip.
    static Geometry RimShape(double w, double h, double r, double e, bool pill)
    {
        if (pill) return new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(e, h - r), false, false);
            c.ArcTo(new Point(e + r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(w + e - r, h), true, false);
            c.ArcTo(new Point(w + e, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    // ---------- visualizer ----------

    static void MakeBars(Panel host, int n, double height)
    {
        for (int i = 0; i < n; i++)
        {
            var r = new Rectangle
            {
                Width = 3, Height = height, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1.5, 0, 1.5, 0),
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = new ScaleTransform(1, .2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            r.SetResourceReference(Shape.FillProperty, "AccentGrad");
            host.Children.Add(r);
        }
    }

    void UpdateViz(double dt)
    {
        bool live = viz.Live;
        double tsec = Environment.TickCount64 / 1000.0;
        for (int i = 0; i < disp.Length; i++)
        {
            // Real spectrum when we can hear the output; a gentle synthetic wave if loopback is unavailable.
            float target = !playing ? 0 : live ? viz.Bands[i] : (float)(0.35 + 0.25 * Math.Sin(tsec * (2.1 + i * 0.37) + i));
            disp[i] += (target - disp[i]) * (target > disp[i] ? 0.55f : 0.13f);   // fast attack, slow fall
        }
        int beats = viz.Beats;
        if (beats != seenBeats) { seenBeats = beats; if (playing && live) beatPulse = 1; }
        beatPulse *= Math.Exp(-dt / 0.3);
        MiniThump.ScaleX = MiniThump.ScaleY = 1 + 0.16 * beatPulse;
        RingScale.ScaleX = RingScale.ScaleY = 1 + 0.75 * (1 - beatPulse);
        BeatRing.Opacity = 0.9 * beatPulse;
        var pos = Logic.Position(tlPos, tlEnd, tlUpdated, tlReceived, DateTimeOffset.Now, playing);
        ProgScale.ScaleX = tlEnd > TimeSpan.Zero ? Clamp01(pos.TotalSeconds / tlEnd.TotalSeconds) : 0;
        ProgLine.Opacity = 0.45 + 0.55 * beatPulse;
        SetBars(MiniBars);
        if (playing) MiniSpin.Angle = (MiniSpin.Angle + 70 * dt) % 360;
        if (!expanded) return;
        SetBars(BigBars);
        if (playing) DiscSpin.Angle = (DiscSpin.Angle + 140 * dt) % 360;
        DrawWave(dt);
        DrawFx(dt);
        if (glassOn && liquid && glassBase != null)
        {
            glassPhase += dt * (2 + 5 * bassLv);
            RenderGlass();
            GlassEdge.StrokeThickness = 2.4 + 2.4 * bassLv + 1.2 * beatPulse;
        }
    }

    void SetBars(Panel panel)
    {
        int n = panel.Children.Count;
        for (int j = 0; j < n; j++)
        {
            float v = disp[1 + j * (disp.Length - 4) / n];
            ((ScaleTransform)((Rectangle)panel.Children[j]).RenderTransform).ScaleY = 0.2 + 0.8 * v;
        }
    }

    // ---------- liquid beat wave ----------
    // Mirrored spectrum (bass in the middle), blurred across bands and over time so it moves like liquid,
    // a slow travelling ripple so it flows between beats, and a swell + brighter crest on every kick.
    const int WaveN = 40;
    const double WaveH = 96;
    readonly double[] waveLv = new double[Visualizer.B], blurA = new double[Visualizer.B], blurB = new double[Visualizer.B];
    readonly Point[] frontPts = new Point[WaveN], backPts = new Point[WaveN];
    double beatPulse, flowT, bassLv, vocalLv;
    int seenBeats;

    void DrawWave(double dt)
    {
        if (playing) flowT += dt;

        double follow = Math.Min(1, dt * 14);           // extra time smoothing on top of the bars' attack/decay
        for (int i = 0; i < waveLv.Length; i++) { waveLv[i] += (disp[i] - waveLv[i]) * follow; blurA[i] = waveLv[i]; }
        for (int pass = 0; pass < 2; pass++)            // [1 2 1] blur across neighbouring bands, twice
        {
            for (int i = 0; i < blurA.Length; i++)
                blurB[i] = 0.25 * blurA[Math.Max(i - 1, 0)] + 0.5 * blurA[i] + 0.25 * blurA[Math.Min(i + 1, blurA.Length - 1)];
            Array.Copy(blurB, blurA, blurA.Length);
        }

        // Bass and vocals drive the wave continuously (not just on detected beats): fast rise so it lands on the
        // hit, softer fall so it breathes. Bass = swell in the middle, vocals = a ripple across the back layer.
        bool hear = playing && viz.Live;
        double bT = hear ? viz.Bass : 0, vT = hear ? viz.Vocal : 0;
        bassLv += (bT - bassLv) * Math.Min(1, dt * (bT > bassLv ? 38 : 7));
        vocalLv += (vT - vocalLv) * Math.Min(1, dt * (vT > vocalLv ? 22 : 6));
        if (playing) flowT += dt * vocalLv * 2.2;        // ripple travels faster while someone's singing

        double str = waveStrength, amp = 1 + 0.9 * beatPulse, last = Visualizer.B - 1;
        for (int k = 0; k < WaveN; k++)
        {
            double u = k / (double)(WaveN - 1), edge = 0.3 + 0.7 * Math.Sin(Math.PI * u);   // fade toward card edges
            double f = Math.Abs(u - 0.5) * 2 * last;
            double lf = Level(f), lb = Level(f * 0.8 + 1.2);
            double centre = Math.Exp(-Math.Pow((u - 0.5) / 0.24, 2));
            double front = 4 + (lf * 26 + Math.Pow(bassLv, 1.6) * 58 * centre) * amp * str * edge
                         + Math.Sin(u * 9.4 - flowT * 1.7) * (1.5 + 4 * bassLv) * edge;
            double back = 6 + (lb * 30 + vocalLv * 22 * (0.6 + 0.4 * edge)) * amp * str * edge
                        + Math.Sin(u * 6.3 + flowT * 1.2 + 1) * (2 + 7 * vocalLv) * edge;
            frontPts[k] = new Point(u * ExpW, WaveH - SoftCap(front));
            backPts[k] = new Point(u * ExpW, WaveH - SoftCap(back));
        }
        WaveBack.Data = Curve(backPts, true);
        WaveFront.Data = Curve(frontPts, true);
        WaveCrest.Data = Curve(frontPts, false);
        WaveCrest.StrokeThickness = 1.3 + 1.2 * beatPulse + 0.8 * bassLv;
    }

    // Big bass hits round off near the top instead of flattening against the ceiling.
    static double SoftCap(double v) => v < 1 ? 1 : 1 + (WaveH - 4) * Math.Tanh((v - 1) / (WaveH - 4));

    // Cosine interpolation between blurred bands: no corners anywhere along the curve.
    double Level(double f)
    {
        f = Math.Clamp(f, 0, blurA.Length - 1);
        int i = (int)f, j = Math.Min(i + 1, blurA.Length - 1);
        double t = (1 - Math.Cos((f - i) * Math.PI)) / 2;
        return blurA[i] + (blurA[j] - blurA[i]) * t;
    }

    static Geometry Curve(Point[] p, bool filled)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            int n = p.Length;
            c.BeginFigure(filled ? new Point(p[0].X, WaveH) : p[0], filled, filled);
            if (filled) c.LineTo(p[0], false, false);
            for (int i = 0; i < n - 1; i++)             // Catmull-Rom through the points as cubic Beziers
            {
                Point p0 = p[Math.Max(i - 1, 0)], p1 = p[i], p2 = p[i + 1], p3 = p[Math.Min(i + 2, n - 1)];
                c.BezierTo(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2, true, true);
            }
            if (filled) c.LineTo(new Point(p[n - 1].X, WaveH), false, false);
        }
        g.Freeze();
        return g;
    }

    // ---------- media (event-driven via Windows GSMTC) ----------

    async Task InitMedia()
    {
        try { mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { return; }
        mgr.CurrentSessionChanged += (m, _) => Ui(() => Attach(m.GetCurrentSession()));
        mgr.SessionsChanged += (m, _) => Ui(() =>
        {
            var n = m.GetSessions().Count;
            SwitchBtn.Visibility = n > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (n == 0) Attach(null);
        });
        SwitchBtn.Visibility = mgr.GetSessions().Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Attach(mgr.GetCurrentSession());
    }

    void Ui(Action a) => Dispatcher.InvokeAsync(a);

    void Attach(Session? s)
    {
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnProps;
            session.PlaybackInfoChanged -= OnPlayback;
            session.TimelinePropertiesChanged -= OnTimeline;
        }
        session = s;
        if (s != null)
        {
            s.MediaPropertiesChanged += OnProps;
            s.PlaybackInfoChanged += OnPlayback;
            s.TimelinePropertiesChanged += OnTimeline;
        }
        _ = RefreshProps();
        RefreshPlayback();
        RefreshTimeline();
    }

    void OnProps(Session s, MediaPropertiesChangedEventArgs e) => Ui(() => _ = RefreshProps());
    void OnPlayback(Session s, PlaybackInfoChangedEventArgs e) => Ui(RefreshPlayback);
    void OnTimeline(Session s, TimelinePropertiesChangedEventArgs e) => Ui(RefreshTimeline);

    async Task RefreshProps()
    {
        int v = ++propsVersion;
        var s = session;
        string title = "", artist = "";
        (BitmapSource Art, BitmapSource Tiny, Color A, Color B)? art = null;
        if (s != null)
        {
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                title = p.Title ?? "";
                artist = string.IsNullOrEmpty(p.Artist) ? p.AlbumArtist ?? "" : p.Artist;
                if (p.Thumbnail != null)
                {
                    using var ras = await p.Thumbnail.OpenReadAsync();
                    var ms = new MemoryStream();
                    await ras.AsStreamForRead().CopyToAsync(ms);
                    art = await Task.Run(() => Decode(ms));
                }
            }
            catch { /* session vanished mid-read or art undecodable: show what we have */ }
        }
        if (v != propsVersion) return;                  // a newer refresh superseded this one

        string key = title + "\n" + artist;
        bool changed = key != lastTrack;
        lastTrack = key;

        TrackTitle.Text = title;
        Artist.Text = artist;
        HeaderLeft.Text = s is null ? "" : Logic.AppName(s.SourceAppUserModelId);
        Brush artBrush = art is null ? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) : new ImageBrush(art.Value.Art) { Stretch = Stretch.UniformToFill };
        Art.Background = artBrush;
        MiniArt.Fill = artBrush;
        DiscLabel.Fill = artBrush;
        Ambient.Background = art is null ? null : AmbientBrush(art.Value.Tiny);
        albumA = art?.A ?? Colors.White;
        albumB = art?.B ?? Colors.White;
        ApplyAccent();

        UpdateClock();
        UpdateTargets();
        if (changed && title.Length > 0)
        {
            TrackIn();
            _ = LoadLyrics(title, artist);
            if (primed && peekOnTrack) Peek();
            primed = true;
        }
    }
    bool primed;

    // Blurred cover + dark gradient rendered ONCE per track into a card-sized bitmap, so each animated frame
    // is a plain 1:1 copy instead of re-scaling/blurring (the biggest per-frame cost in the open card).
    static ImageBrush AmbientBrush(BitmapSource tiny)
    {
        const int W = 528, H = 340;                     // card + 24 px each side for the curved shoulders
        var img = new System.Windows.Controls.Image
        {
            Source = tiny, Stretch = Stretch.UniformToFill, Width = W + 80, Height = H + 80,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 40, KernelType = System.Windows.Media.Effects.KernelType.Gaussian },
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
        var shade = new Border
        {
            Width = W + 80, Height = H + 80,
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x80, 0, 0, 0), 0), new(Color.FromArgb(0xC0, 0, 0, 0), .45), new(Color.FromArgb(0xE6, 0, 0, 0), .8),
            }, 90),
        };
        var root = new Grid { Width = W + 80, Height = H + 80, Background = Brushes.Black, Children = { img, shade } };
        root.Measure(new Size(W + 80, H + 80));
        root.Arrange(new Rect(-40, -40, W + 80, H + 80));   // crop the blur's soft edges
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return new ImageBrush(rtb) { Stretch = Stretch.None, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top };
    }

    void SetAccent(Color a, Color b)
    {
        Resources["Accent"] = new SolidColorBrush(a);
        Resources["AccentGrad"] = new LinearGradientBrush(a, b, 0);
        Resources["WaveFill"] = new LinearGradientBrush(Color.FromArgb(115, a.R, a.G, a.B), Color.FromArgb(10, b.R, b.G, b.B), 90);
        Resources["WaveFill2"] = new LinearGradientBrush(Color.FromArgb(80, b.R, b.G, b.B), Color.FromArgb(0, a.R, a.G, a.B), 90);
        Resources["Rim"] = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(40, a.R, a.G, a.B), 0), new(Color.FromArgb(150, a.R, a.G, a.B), .3),
            new(Color.FromArgb(150, b.R, b.G, b.B), .7), new(Color.FromArgb(40, b.R, b.G, b.B), 1),
        }, 0);
        SetFxColours(a, b);
        var mid = Color.FromRgb((byte)((a.R + b.R + 255) / 3), (byte)((a.G + b.G + 255) / 3), (byte)((a.B + b.B + 255) / 3));
        Resources["WaveCrest"] = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0, a.R, a.G, a.B), 0), new(Color.FromArgb(220, a.R, a.G, a.B), .25),
            new(Color.FromArgb(240, mid.R, mid.G, mid.B), .5),
            new(Color.FromArgb(220, b.R, b.G, b.B), .75), new(Color.FromArgb(0, b.R, b.G, b.B), 1),
        }, 0);
    }

    static (BitmapSource, BitmapSource, Color, Color) Decode(MemoryStream ms)
    {
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 200;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        var tiny = new FormatConvertedBitmap(
            new TransformedBitmap(bmp, new ScaleTransform(12.0 / bmp.PixelWidth, 12.0 / bmp.PixelHeight)),
            PixelFormats.Bgra32, null, 0);
        var frozen = BitmapFrame.Create(tiny);           // materialise so the brush doesn't re-scale every frame
        frozen.Freeze();
        var (a, b) = Accents(frozen);
        return (bmp, frozen, a, b);
    }

    // Two vivid colours from the cover: the most saturated one, then the best one with a clearly different hue.
    static (Color, Color) Accents(BitmapSource small)
    {
        int sw = small.PixelWidth, sh = small.PixelHeight;
        var px = new byte[sw * sh * 4];
        small.CopyPixels(px, sw * 4, 0);
        var cands = new List<(double Score, double Hue, Color C)>();
        for (int i = 0; i < px.Length; i += 4)
        {
            byte B = px[i], G = px[i + 1], R = px[i + 2];
            int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
            if (max < 40) continue;
            double sat = (max - min) / (double)max, d = max - min, hue = 0;
            if (d > 0) hue = max == R ? 60 * (((G - B) / d + 6) % 6) : max == G ? 60 * ((B - R) / d + 2) : 60 * ((R - G) / d + 4);
            double k = 255.0 / max;
            cands.Add((sat * max, hue, Color.FromRgb((byte)(R * k), (byte)(G * k), (byte)(B * k))));
        }
        if (cands.Count == 0) return (Colors.White, Colors.White);
        var first = cands.MaxBy(c => c.Score);
        if (first.Score < 25) return (Colors.White, Color.FromRgb(0xCC, 0xCC, 0xCC));   // greyscale cover
        var second = cands.Where(c => HueDist(c.Hue, first.Hue) > 35 && c.Score > 25).OrderByDescending(c => c.Score).FirstOrDefault();
        var b = second.Score > 0 ? second.C
            : Color.FromRgb((byte)((first.C.R + 255) / 2), (byte)((first.C.G + 255) / 2), (byte)((first.C.B + 255) / 2));
        return (first.C, b);
    }

    static double HueDist(double a, double b) { var d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    void RefreshPlayback()
    {
        var info = session?.GetPlaybackInfo();
        SetPlaying(info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        PrevBtn.IsEnabled = info?.Controls.IsPreviousEnabled ?? false;
        NextBtn.IsEnabled = info?.Controls.IsNextEnabled ?? false;
        PlayBtn.IsEnabled = info != null;
        Seek.IsEnabled = info?.Controls.IsPlaybackPositionEnabled ?? false;
        ShuffleBtn.Visibility = info?.Controls.IsShuffleEnabled == true ? Visibility.Visible : Visibility.Hidden;
        RepeatBtn.Visibility = info?.Controls.IsRepeatEnabled == true ? Visibility.Visible : Visibility.Hidden;
        shuffleOn = info?.IsShuffleActive == true;
        repeat = info?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
        Toggle(ShuffleBtn, shuffleOn);
        Toggle(RepeatBtn, repeat != MediaPlaybackAutoRepeatMode.None);
        RepeatOne.Visibility = repeat == MediaPlaybackAutoRepeatMode.Track ? Visibility.Visible : Visibility.Collapsed;
    }

    static void Toggle(Button b, bool on)
    {
        if (on) b.SetResourceReference(ForegroundProperty, "Accent");
        else b.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
    }

    void SetPlaying(bool p)
    {
        if (p == playing) return;
        // Freeze the interpolated position so a pause/resume without a timeline event doesn't jump.
        var now = DateTimeOffset.Now;
        tlPos = Logic.Position(tlPos, tlEnd, tlUpdated, tlReceived, now, playing);
        tlUpdated = default;
        tlReceived = now;
        playing = p;

        PlayIcon.Data = p ? PauseGeo : PlayGeo;
        Anim(PlayScale, ScaleTransform.ScaleXProperty, .4, 1, 380, new BackEase { Amplitude = .5 });
        Anim(PlayScale, ScaleTransform.ScaleYProperty, .4, 1, 380, new BackEase { Amplitude = .5 });
        Anim(PlayTurn, RotateTransform.AngleProperty, p ? -90 : 90, 0, 380, new CubicEase());
        // Apple-Music style: cover breathes back to full size on play, sinks on pause; vinyl slides out/in.
        Anim(ArtPause, ScaleTransform.ScaleXProperty, null, p ? 1 : .86, 520, new BackEase { Amplitude = .35 });
        Anim(ArtPause, ScaleTransform.ScaleYProperty, null, p ? 1 : .86, 520, new BackEase { Amplitude = .35 });
        Anim(DiscSlide, TranslateTransform.XProperty, null, p ? 32 : 0, 650, new CubicEase());

        if (p) viz.Start();
        else { viz.Stop(); vizUntil = DateTime.Now.AddSeconds(1.2); }   // let bars fall instead of snapping
        SyncTimers();
        EnsureFrames();
    }

    // New track: cover flips in like a card (3D-ish via X scale), text glides in from the right.
    void TrackIn()
    {
        Anim(ArtFlip, ScaleTransform.ScaleXProperty, 0, 1, 520, new BackEase { Amplitude = .45 });
        Anim(TextShift, TranslateTransform.XProperty, 26, 0, 480, new CubicEase());
        Anim(TextCol, OpacityProperty, 0, 1, 420, new CubicEase());
    }

    static void Anim(Animatable target, DependencyProperty dp, double? from, double to, int ms, EasingFunctionBase ease)
    {
        ease.EasingMode = EasingMode.EaseOut;
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
        if (from is double f) a.From = f;
        target.BeginAnimation(dp, a);
    }

    static void Anim(UIElement target, DependencyProperty dp, double? from, double to, int ms, EasingFunctionBase ease)
    {
        ease.EasingMode = EasingMode.EaseOut;
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease };
        if (from is double f) a.From = f;
        target.BeginAnimation(dp, a);
    }

    static void Fade(UIElement e, double to, int ms) => Anim(e, OpacityProperty, null, to, ms, new QuadraticEase());

    void RefreshTimeline()
    {
        var t = session?.GetTimelineProperties();
        tlPos = t is null ? TimeSpan.Zero : t.Position - t.StartTime;
        tlEnd = t is null ? TimeSpan.Zero : t.EndTime - t.StartTime;
        tlUpdated = t?.LastUpdatedTime ?? default;
        tlReceived = DateTimeOffset.Now;
        UpdateProgress();
    }

    void UpdateProgress()
    {
        if (seeking) return;
        var pos = Logic.Position(tlPos, tlEnd, tlUpdated, tlReceived, DateTimeOffset.Now, playing);
        Seek.Maximum = Math.Max(1, tlEnd.TotalSeconds);
        Seek.Value = pos.TotalSeconds;
        Elapsed.Text = tlEnd > TimeSpan.Zero ? Logic.Fmt(pos) : "";
        Remaining.Text = tlEnd > TimeSpan.Zero ? "-" + Logic.Fmt(tlEnd - pos) : "";
        UpdateLyric(false);
    }

    void SyncTimers()
    {
        if (expanded && playing) progressTimer.Start(); else progressTimer.Stop();
        SyncMarketTimer();
    }

    async void Cmd(Func<Session, Windows.Foundation.IAsyncOperation<bool>> f)
    {
        try { if (session != null) await f(session); }
        catch { /* player closed between click and call */ }
    }

    // ---------- lyrics (opt-in: sends title + artist to lrclib.net) ----------

    async Task LoadLyrics(string title, string artist)
    {
        lyrics = null;
        lyricIdx = -1;
        LyricLine.Text = "";
        var key = title + "\n" + artist;
        lyricsKey = key;
        if (!lyricsOn) return;
        LyricLine.Text = "…";
        var res = await Logic.FetchLyrics(title, artist, tlEnd);
        if (lyricsKey != key) return;                   // track changed while we were fetching
        lyrics = res;
        LyricLine.Text = res is null ? "No synced lyrics found" : "";
        UpdateLyric(true);
    }

    void UpdateLyric(bool force)
    {
        if (lyrics is null || lyrics.Count == 0) return;
        var pos = Logic.Position(tlPos, tlEnd, tlUpdated, tlReceived, DateTimeOffset.Now, playing) + TimeSpan.FromMilliseconds(250);
        int i = Logic.LyricIndex(lyrics, pos);
        if (i == lyricIdx && !force) return;
        lyricIdx = i;
        var text = i < 0 ? "♪" : lyrics[i].Line;
        LyricLine.Text = text.Length == 0 ? "♪" : text;
        Anim(LyricShift, TranslateTransform.YProperty, 10, 0, 320, new CubicEase());
        Anim(LyricLine, OpacityProperty, 0, 1, 320, new CubicEase());
    }

    // ---------- volume (mouse wheel on the open card) ----------

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var vol = dev.AudioEndpointVolume;
            float nv = Math.Clamp(vol.MasterVolumeLevelScalar + e.Delta / 120f * 0.04f, 0, 1);
            vol.MasterVolumeLevelScalar = nv;
            VolText.Text = $"Volume {Math.Round(nv * 100)}%";
            VolFill.Width = 54 * nv;
            VolPill.BeginAnimation(OpacityProperty, null);
            VolPill.Opacity = 1;
            volTimer.Stop();
            volTimer.Start();
        }
        catch { /* no output device */ }
        e.Handled = true;
    }

    // ---------- clock / battery / fullscreen ----------

    void SlowTick()
    {
        // Hide while a fullscreen app/game/video/presentation is in front.
        var vis = hideFullscreen && FullscreenInFront() ? Visibility.Hidden : Visibility.Visible;
        if (Visibility != vis) { Visibility = vis; EnsureFrames(); }
        if (expanded) UpdateClock();
        var t = DateTime.Now.ToShortTimeString();
        if (IdleClock.Text != t) IdleClock.Text = t;
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        Clock.Text = now.ToShortTimeString();
        BigClock.Text = now.ToShortTimeString();
        if (!HasMedia) HeaderLeft.Text = now.ToString("dddd, d MMMM");
        if (GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryLifePercent <= 100)
            Battery.Text = (ps.ACLineStatus == 1 ? "⚡ " : "") + ps.BatteryLifePercent + "%";
        else
            Battery.Text = "";
    }

    // ---------- file shelf (drag files to the top edge to open it, then drop) ----------

    void RefreshShelf()
    {
        Shelf.Visibility = shelf.Count > 0 || dragHover ? Visibility.Visible : Visibility.Collapsed;
        ShelfHint.Visibility = shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearShelf.Visibility = shelf.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTargets();
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        bool files = !draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (files && !dragHover) { dragHover = true; RefreshShelf(); }
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        dragHover = false;
        if (!draggingOut && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var p in paths)
                if (!shelf.Any(x => string.Equals(x.FullPath, p, StringComparison.OrdinalIgnoreCase)))
                    shelf.Add(new ShelfItem(p));
        RefreshShelf();
        e.Handled = true;
    }

    void Chip_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: ShelfItem it } el) return;
        draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(el, new DataObject(DataFormats.FileDrop, new[] { it.FullPath }),
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally { draggingOut = false; }
        if (!File.Exists(it.FullPath) && !Directory.Exists(it.FullPath)) shelf.Remove(it);   // it was moved away
    }

    void Chip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShelfItem it }) shelf.Remove(it);
        e.Handled = true;
    }

    // ---------- menu (right-click the open card) ----------

    // Everything else lives on the Settings tab.
    void BuildMenu()
    {
        var open = new MenuItem { Header = "Settings" };
        open.Click += (_, _) => TabSettings.IsChecked = true;
        var quit = new MenuItem { Header = "Quit Island" };
        quit.Click += (_, _) => Application.Current.Shutdown();
        Island.ContextMenu = new ContextMenu { Items = { open, new Separator(), quit } };
    }

    // ---------- Win32 ----------

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    struct POINT { public int X, Y; }
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
}
