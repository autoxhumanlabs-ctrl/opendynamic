using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Island;

public sealed record ClipItem(string Text)
{
    public string Preview => string.Join(" ", Text.Split((char[]?)null, 60, StringSplitOptions.RemoveEmptyEntries));
}

public sealed record LinkItem(string Name, string Url, Brush Badge, string Initial);

public sealed record MarketItem(string Name, string Sub, string PriceText, string ChangeText, Brush ChangeBrush, Geometry? Spark, string Tv);

// Extra tabs on the open card: clipboard history, links, markets.
public partial class MainWindow
{
    string tab = "music";
    readonly ObservableCollection<ClipItem> clips = new();
    readonly DispatcherTimer marketTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    DateTime marketsAt;
    static readonly HttpClient Web = CreateWeb();

    static HttpClient CreateWeb()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Island/1.0");
        return h;
    }

    void InitExtras()
    {
        ClipItems.ItemsSource = clips;
        TabMusic.Checked += (_, _) => SetTab("music");
        TabClip.Checked += (_, _) => SetTab("clip");
        TabLinks.Checked += (_, _) => SetTab("links");
        TabMarkets.Checked += (_, _) => SetTab("markets");
        TabSettings.Checked += (_, _) => SetTab("settings");
        marketTimer.Tick += (_, _) => _ = LoadMarkets();
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(hwnd).AddHook(ClipHook);
            AddClipboardFormatListener(hwnd);
        };
    }

    void SetTab(string t)
    {
        tab = t;
        if (t == "links") LoadLinks();
        SyncTimers();
        UpdateTargets();
    }

    void ApplyTabVisibility(bool media)
    {
        static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = V(tab == "music" && media);
        ClockPanel.Visibility = V(tab == "music" && !media);
        ClipPanel.Visibility = V(tab == "clip");
        ClipEmpty.Visibility = V(clips.Count == 0);
        LinkItems.Visibility = V(tab == "links");
        MarketsPanel.Visibility = V(tab == "markets");
        SettingsPanel.Visibility = V(tab == "settings");
        WaveLayer.Opacity = tab == "music" ? 1 : 0.3;     // keep the beat, but don't cover the cards
    }

    void SyncMarketTimer()
    {
        bool on = expanded && tab == "markets";
        if (on && !marketTimer.IsEnabled && (DateTime.Now - marketsAt).TotalSeconds > 30) _ = LoadMarkets();
        marketTimer.IsEnabled = on;
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // ---------- clipboard ----------

    const int WM_CLIPBOARDUPDATE = 0x031D;

    IntPtr ClipHook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) Dispatcher.BeginInvoke(ReadClipboard, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    void ReadClipboard()
    {
        try
        {
            // Password managers set this format so clipboard history tools skip their secrets.
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing") || !Clipboard.ContainsText()) return;
            var t = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(t)) return;
            Logic.PushRecent(clips, new ClipItem(t), 20);
            if (tab == "clip") UpdateTargets();
        }
        catch { /* clipboard held by another app: skip this copy */ }
    }

    void Clip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipItem it })
            try { Clipboard.SetDataObject(it.Text, true); } catch { }   // the update message moves it to the top
    }

    void Clip_Remove(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ClipItem it }) { clips.Remove(it); UpdateTargets(); }
        e.Handled = true;                                               // don't open the card's menu
    }

    // ---------- links ----------

    static readonly string LinksFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "links.txt");
    const string DefaultLinks =
        "# Links shown on the island's Links tab. One per line:  Name | https://url\r\n" +
        "YouTube | https://www.youtube.com\r\n" +
        "Subscriptions | https://www.youtube.com/feed/subscriptions\r\n" +
        "Watch later | https://www.youtube.com/playlist?list=WL\r\n" +
        "Netflix | https://www.netflix.com/browse\r\n" +
        "TradingView | https://www.tradingview.com/chart/\r\n" +
        "Gold | https://www.tradingview.com/chart/?symbol=OANDA:XAUUSD\r\n" +
        "Silver | https://www.tradingview.com/chart/?symbol=OANDA:XAGUSD\r\n" +
        "NAS100 | https://www.tradingview.com/chart/?symbol=OANDA:NAS100USD\r\n";

    void LoadLinks()
    {
        try
        {
            if (!File.Exists(LinksFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LinksFile)!);
                File.WriteAllText(LinksFile, DefaultLinks);
            }
            else if (settings.GetValue("NetflixAdded") is null)          // once, for files made before Netflix was a default
                File.AppendAllText(LinksFile, "Netflix | https://www.netflix.com/browse\r\n");
            settings.SetValue("NetflixAdded", 1);
            var items = Logic.ParseLinks(File.ReadAllText(LinksFile))
                .Select(l => new LinkItem(l.Name, l.Url, Badge(l.Url), l.Name[..1].ToUpperInvariant())).ToList();
            items.Add(new LinkItem("Edit", LinksFile, Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A)), "+"));
            LinkItems.ItemsSource = items;
        }
        catch { }
    }

    static Brush Badge(string url) => Frozen(
        url.Contains("youtube.com") || url.Contains("youtu.be") ? Color.FromRgb(0xFF, 0x00, 0x33)
        : url.Contains("netflix.com") ? Color.FromRgb(0xB2, 0x07, 0x10)
        : url.Contains("tradingview.com") ? Color.FromRgb(0x29, 0x62, 0xFF)
        : Color.FromRgb(0x6D, 0x5D, 0xF6));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    void Link_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LinkItem it }) Open(it.Url);
    }

    // ---------- markets ----------

    // Yahoo futures symbols (free, no key) for the numbers; TradingView spot symbols for the chart you open.
    static readonly (string Name, string Sub, string Yahoo, string Tv)[] Markets =
    {
        ("Gold", "  GC", "GC=F", "OANDA:XAUUSD"),
        ("Silver", "  SI", "SI=F", "OANDA:XAGUSD"),
        ("NAS100", "  NQ", "NQ=F", "OANDA:NAS100USD"),
    };
    static readonly Brush Up = Frozen(Color.FromRgb(0x34, 0xD3, 0x99)), Down = Frozen(Color.FromRgb(0xF8, 0x71, 0x71)),
                          Flat = Frozen(Color.FromRgb(0x88, 0x88, 0x88));

    async Task LoadMarkets()
    {
        marketsAt = DateTime.Now;
        if (MarketItems.ItemsSource is null) MarketNote.Text = "Loading prices…";
        var quotes = await Task.WhenAll(Markets.Select(async m =>
        {
            try { return Logic.ParseChart(await Web.GetStringAsync($"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(m.Yahoo)}?range=1d&interval=5m")); }
            catch { return null; }
        }));
        MarketItems.ItemsSource = Markets.Zip(quotes, (m, q) =>
        {
            if (q is null) return new MarketItem(m.Name, m.Sub, "—", "offline", Flat, null, m.Tv);
            double ch = q.Price - q.Prev, pct = q.Prev == 0 ? 0 : ch / q.Prev * 100;
            return new MarketItem(m.Name, m.Sub, q.Price.ToString("N2"), $"{pct:+0.00;-0.00}%   {ch:+#,0.00;-#,0.00}",
                                  ch >= 0 ? Up : Down, Spark(q.Closes), m.Tv);
        }).ToList();
        MarketNote.Text = quotes.All(q => q is null)
            ? "Couldn't reach Yahoo Finance · retrying every minute"
            : $"Futures prices via Yahoo, may be delayed · {DateTime.Now:t} · click a card for the TradingView chart";
        UpdateTargets();
    }

    static Geometry? Spark(double[] c)
    {
        if (c.Length < 2) return null;
        double lo = c.Min(), hi = c.Max(), span = hi - lo == 0 ? 1 : hi - lo;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, 26 - (c[0] - lo) / span * 26), false, false);
            for (int i = 1; i < c.Length; i++)
                ctx.LineTo(new Point(i * 100.0 / (c.Length - 1), 26 - (c[i] - lo) / span * 26), true, true);
        }
        g.Freeze();
        return g;
    }

    void Market_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MarketItem it }) Open("https://www.tradingview.com/chart/?symbol=" + it.Tv);
    }

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
}
