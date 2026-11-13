using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Island;

// Pure logic, no WPF: run `Island.exe --selftest` (exit code 0 = pass).
public static class Logic
{
    static readonly (string Key, string Name)[] Known =
    {
        ("spotify", "Spotify"), ("msedge", "Edge"), ("chrome", "Chrome"), ("firefox", "Firefox"),
        ("308046b0af4a39cb", "Firefox"), ("brave", "Brave"), ("opera", "Opera"), ("applemusic", "Apple Music"),
        ("zunemusic", "Media Player"), ("vlc", "VLC"), ("foobar", "foobar2000"), ("youtubemusic", "YouTube Music"),
        ("amazonmusic", "Amazon Music"), ("tidal", "TIDAL"), ("deezer", "Deezer"), ("musicbee", "MusicBee"), ("aimp", "AIMP"),
    };

    // SourceAppUserModelId ("Spotify.exe", "308046B0AF4A39CB", "Pkg_hash!App") -> friendly name.
    public static string AppName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var s = id.ToLowerInvariant();
        foreach (var (k, n) in Known) if (s.Contains(k)) return n;
        var name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : id;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : id;
    }

    // Apps report position rarely; interpolate locally. Bad/missing LastUpdatedTime -> use when we received it.
    public static TimeSpan Position(TimeSpan pos, TimeSpan end, DateTimeOffset updated, DateTimeOffset received, DateTimeOffset now, bool playing)
    {
        if (playing) pos += now - (updated.Year > 2000 && updated <= now ? updated : received);
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (end > TimeSpan.Zero && pos > end) pos = end;
        return pos;
    }

    public static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    // ---------- lyrics (lrclib.net, opt-in) ----------

    // "Radhe - The Wedding Song | Movie (Official Video) [4K]" -> "Radhe - The Wedding Song"
    public static string CleanTitle(string t)
    {
        t = Regex.Replace(t, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        int bar = t.IndexOfAny(new[] { '|', '｜' });
        if (bar > 0) t = t[..bar];
        t = Regex.Replace(t, @"(?i)\b(official|lyrics?|lyrical|video|audio|full song|hd|4k)\b", "");
        return Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '–', ':');
    }

    public static List<(TimeSpan T, string Line)> ParseLrc(string lrc)
    {
        var list = new List<(TimeSpan, string)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var ms = Regex.Matches(raw, @"\[(\d+):(\d+(?:[.:]\d+)?)\]");
            if (ms.Count == 0) continue;
            var text = raw[(ms[^1].Index + ms[^1].Length)..].Trim();
            foreach (Match m in ms)
            {
                var sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                list.Add((TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(sec), text));
            }
        }
        return list.OrderBy(x => x.Item1).ToList();
    }

    // Index of the line being sung at pos (last line whose time <= pos), -1 before the first.
    public static int LyricIndex(List<(TimeSpan T, string Line)> lines, TimeSpan pos)
    {
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (lines[mid].T <= pos) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
        return ans;
    }

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(6) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Island/1.0 (desktop notch app)");
        return h;
    }

    public static async Task<List<(TimeSpan T, string Line)>?> FetchLyrics(string title, string artist, TimeSpan duration)
    {
        var clean = CleanTitle(title);
        string track = clean, who = artist;
        int dash = clean.IndexOf(" - ", StringComparison.Ordinal);   // YouTube style "Artist - Song"
        if (dash > 0) { who = clean[..dash]; track = clean[(dash + 3)..]; }
        string[] queries =
        {
            $"api/search?track_name={Uri.EscapeDataString(track)}&artist_name={Uri.EscapeDataString(who)}",
            $"api/search?q={Uri.EscapeDataString(clean + " " + artist)}",
            $"api/search?q={Uri.EscapeDataString(clean)}",
        };
        foreach (var q in queries)
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(q));
                var hits = doc.RootElement.EnumerateArray()
                    .Where(x => x.TryGetProperty("syncedLyrics", out var l) && l.ValueKind == JsonValueKind.String)
                    .OrderBy(x => duration > TimeSpan.Zero && x.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? Math.Abs(d.GetDouble() - duration.TotalSeconds) : 0)
                    .ToList();
                if (hits.Count > 0) return ParseLrc(hits[0].GetProperty("syncedLyrics").GetString()!);
            }
            catch { /* offline / rate-limited: try the next query, then give up quietly */ }
        }
        return null;
    }

    // ---------- extras: clipboard history, links, market quotes ----------

    // Most-recent-first list without duplicates: re-adding an item moves it to the top.
    public static void PushRecent<T>(IList<T> list, T item, int cap)
    {
        int i = list.IndexOf(item);
        if (i == 0) return;
        if (i > 0) list.RemoveAt(i);
        list.Insert(0, item);
        while (list.Count > cap) list.RemoveAt(list.Count - 1);
    }

    // links.txt: "Name | https://url" per line, # comments. Only http(s) URLs are accepted.
    public static List<(string Name, string Url)> ParseLinks(string text)
    {
        var res = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int bar = line.IndexOf('|');
            if (line.Length == 0 || line[0] == '#' || bar < 1) continue;
            var url = line[(bar + 1)..].Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
                res.Add((line[..bar].Trim(), url));
        }
        return res;
    }

    public sealed record Quote(double Price, double Prev, double[] Closes);

    // Yahoo chart API (v8/finance/chart): meta.regularMarketPrice, meta.chartPreviousClose, quote[0].close (has nulls).
    public static Quote? ParseChart(string json)
    {
        try
        {
            var r = JsonDocument.Parse(json).RootElement.GetProperty("chart").GetProperty("result")[0];
            var meta = r.GetProperty("meta");
            var closes = r.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close")
                .EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetDouble()).ToArray();
            return new Quote(meta.GetProperty("regularMarketPrice").GetDouble(), meta.GetProperty("chartPreviousClose").GetDouble(), closes);
        }
        catch { return null; }
    }

    public static void SelfTest()
    {
        static void Check(bool ok, string what) { if (!ok) throw new Exception("selftest failed: " + what); }
        Check(AppName("Spotify.exe") == "Spotify", "spotify");
        Check(AppName("308046B0AF4A39CB") == "Firefox", "firefox aumid");
        Check(AppName("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic") == "Media Player", "zune");
        Check(AppName("potplayer.exe") == "Potplayer", "fallback");
        var now = DateTimeOffset.Now;
        var s10 = TimeSpan.FromSeconds(10); var end = TimeSpan.FromSeconds(100);
        Check(Position(s10, end, now.AddSeconds(-5), now, now, false) == s10, "paused holds");
        Check(Position(s10, end, now.AddSeconds(-5), now, now, true) == TimeSpan.FromSeconds(15), "playing advances");
        Check(Position(s10, end, default, now.AddSeconds(-2), now, true) == TimeSpan.FromSeconds(12), "bad timestamp uses received");
        Check(Position(s10, end, now.AddSeconds(-500), now, now, true) == end, "clamped to end");
        Check(Fmt(TimeSpan.FromSeconds(65)) == "1:05" && Fmt(TimeSpan.FromSeconds(3725)) == "1:02:05", "fmt");
        Check(CleanTitle("Radhe - The Wedding Song | Ibbani Tabbida (Official Video) [4K]") == "Radhe - The Wedding Song", "clean title");
        var l = ParseLrc("[00:01.50] one\n[00:05.00][00:20.00] two\nnoise\n[00:10.00] three");
        Check(l.Count == 4 && l[0].Line == "one" && l[1].T == TimeSpan.FromSeconds(5) && l[3].Line == "two", "lrc parse");
        Check(LyricIndex(l, TimeSpan.FromSeconds(0.5)) == -1 && LyricIndex(l, TimeSpan.FromSeconds(7)) == 1
              && LyricIndex(l, TimeSpan.FromSeconds(99)) == 3, "lyric index");
        var rec = new List<string> { "a", "b", "c" };
        PushRecent(rec, "c", 3); PushRecent(rec, "d", 3);
        Check(string.Join("", rec) == "dca", "push recent");
        var links = ParseLinks("# c\nYouTube | https://youtube.com\nbad | javascript:alert(1)\nno bar\n TV|http://tv.com ");
        Check(links.Count == 2 && links[1] == ("TV", "http://tv.com"), "parse links");
        var q = ParseChart("{\"chart\":{\"result\":[{\"meta\":{\"regularMarketPrice\":10.5,\"chartPreviousClose\":10},\"indicators\":{\"quote\":[{\"close\":[1,null,2]}]}}]}}");
        Check(q is { Price: 10.5, Prev: 10 } && q.Closes.Length == 2, "parse chart");
        Check(ParseChart("{}") is null, "bad chart json");
    }
}
