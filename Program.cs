using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class Program
{
    const string OUTPUT_DIR  = "downloads";
    const string YTDLP_PATH  = @"C:\Users\6ril95\source\repos\Spotify\cotify\bin\Debug\net10.0\yt-dlp.exe";
    const string FFMPEG_PATH = @"C:\Users\6ril95\source\repos\Spotify\cotify\bin\Debug\net10.0\ffmpeg.exe";

    static readonly HttpClient Http = new();

    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: dotnet run \"<spotify_url>\" [url2] ...");
            return;
        }

        if (!File.Exists(YTDLP_PATH))
        {
            Console.Error.WriteLine($"[ERROR] yt-dlp not found at: {YTDLP_PATH}");
            return;
        }

        Directory.CreateDirectory(OUTPUT_DIR);

        foreach (var url in args)
            await ProcessUrl(url.Trim());

        Console.WriteLine("\n[DONE] All downloads complete.");
    }

    static async Task ProcessUrl(string spotifyUrl)
    {
        Console.WriteLine($"\n[META] Resolving: {spotifyUrl}");

        var cleanUrl = Regex.Replace(spotifyUrl, @"[?&]si=[^&]+", "").TrimEnd('?').TrimEnd('&');

        var (kind, id) = ParseSpotifyUrl(cleanUrl);
        if (id == null)
        {
            Console.Error.WriteLine($"  [SKIP] Not a recognised Spotify URL: {spotifyUrl}");
            return;
        }

        if (kind == "track")
        {
            var (title, artist) = await ResolveTrackMeta(cleanUrl);
            if (title == null)
            {
                Console.Error.WriteLine($"  [ERROR] Could not resolve metadata for {cleanUrl}");
                return;
            }
            Console.WriteLine($"  [TRACK] {title} — {artist}");
            await YoutubeDownload(title, artist, "");
        }
        else if (kind == "album" || kind == "playlist")
        {
            await ResolveAndDownloadCollection(cleanUrl, kind);
        }
    }

    static async Task<(string? title, string? artist)> ResolveTrackMeta(string spotifyUrl)
    {
        try
        {
            var oembedUrl = $"https://open.spotify.com/oembed?url={Uri.EscapeDataString(spotifyUrl)}";
            var json      = await Http.GetStringAsync(oembedUrl);
            using var doc = JsonDocument.Parse(json);
            var root      = doc.RootElement;
            var rawTitle  = root.GetProperty("title").GetString() ?? "";
            var artist    = await ResolveArtistFromEmbedPage(spotifyUrl);
            return (rawTitle, artist ?? "Unknown Artist");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [META ERR] oembed failed: {ex.Message}");
            return (null, null);
        }
    }

    static async Task<string?> ResolveArtistFromEmbedPage(string spotifyUrl)
    {
        try
        {
            var embedUrl = spotifyUrl.Replace("open.spotify.com/", "open.spotify.com/embed/");

            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var html  = await Http.GetStringAsync(embedUrl);

            var match = Regex.Match(html, @"<title>[^–]*–\s*([^·]+)·([^<]+)</title>");
            if (match.Success)
                return match.Groups[2].Value.Trim();

            var jsonMatch = Regex.Match(html,
                @"<script id=""__NEXT_DATA__"" type=""application/json"">(.*?)</script>",
                RegexOptions.Singleline);

            if (jsonMatch.Success)
            {
                using var doc = JsonDocument.Parse(jsonMatch.Groups[1].Value);
                var artists   = doc.RootElement
                    .GetProperty("props")
                    .GetProperty("pageProps")
                    .GetProperty("state")
                    .GetProperty("data")
                    .GetProperty("entity")
                    .GetProperty("artists");

                if (artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
                    return artists[0].GetProperty("name").GetString();
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    static async Task ResolveAndDownloadCollection(string spotifyUrl, string kind)
    {
        try
        {
            var embedUrl = spotifyUrl.Replace("open.spotify.com/", "open.spotify.com/embed/");

            Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var html      = await Http.GetStringAsync(embedUrl);
            var jsonMatch = Regex.Match(html,
                @"<script id=""__NEXT_DATA__"" type=""application/json"">(.*?)</script>",
                RegexOptions.Singleline);

            if (!jsonMatch.Success)
            {
                Console.Error.WriteLine("  [ERROR] Could not find track list in embed page.");
                return;
            }

            using var doc = JsonDocument.Parse(jsonMatch.Groups[1].Value);
            var entity    = doc.RootElement
                .GetProperty("props")
                .GetProperty("pageProps")
                .GetProperty("state")
                .GetProperty("data")
                .GetProperty("entity");

            var collectionName = entity.GetProperty("name").GetString() ?? kind;
            Console.WriteLine($"  [{kind.ToUpper()}] {collectionName}");

            var outDir = Path.Combine(OUTPUT_DIR, Sanitise(collectionName));
            Directory.CreateDirectory(outDir);

            JsonElement trackArray;
            if (kind == "album")
                trackArray = entity.GetProperty("tracks").GetProperty("items");
            else
                trackArray = entity.GetProperty("trackList");

            int i = 1;
            foreach (var item in trackArray.EnumerateArray())
            {
                try
                {
                    string title, artist;
                    if (kind == "album")
                    {
                        var t  = item.GetProperty("track");
                        title  = t.GetProperty("name").GetString() ?? "";
                        artist = t.GetProperty("artists")[0].GetProperty("name").GetString() ?? "";
                    }
                    else
                    {
                        title  = item.GetProperty("title").GetString() ?? "";
                        artist = item.GetProperty("subtitle").GetString() ?? "";
                    }

                    if (string.IsNullOrEmpty(title)) continue;
                    Console.WriteLine($"  [{i++}] {title} — {artist}");
                    await YoutubeDownload(title, artist, collectionName, outDir);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [SKIP] Track parse error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [ERROR] Collection resolve failed: {ex.Message}");
        }
    }

    static (string kind, string? id) ParseSpotifyUrl(string input)
    {
        input = input.Trim();
        if (Regex.IsMatch(input, @"^[A-Za-z0-9]{22}$")) return ("track", input);
        var m = Regex.Match(input,
            @"open\.spotify\.com/(track|album|playlist)/([A-Za-z0-9]+)",
            RegexOptions.IgnoreCase);
        return m.Success
            ? (m.Groups[1].Value.ToLower(), m.Groups[2].Value)
            : ("unknown", null);
    }

    static async Task YoutubeDownload(
        string title, string artist, string album, string? outDir = null)
    {
        outDir ??= OUTPUT_DIR;
        var safeName    = Sanitise($"{title} - {artist}");
        var expectedMp3 = Path.Combine(outDir, $"{safeName}.mp3");
        var outTemplate = Path.Combine(outDir, $"{safeName}.%(ext)s");

        if (File.Exists(expectedMp3))
        {
            Console.WriteLine($"    [SKIP] Already exists: {safeName}.mp3");
            return;
        }

        var ffmpegDir = Path.GetDirectoryName(FFMPEG_PATH) ?? ".";
        var query     = $"ytsearch1:{title} {artist} audio";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = YTDLP_PATH,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        psi.ArgumentList.Add(query);
        psi.ArgumentList.Add("--extract-audio");
        psi.ArgumentList.Add("--audio-format");
        psi.ArgumentList.Add("mp3");
        psi.ArgumentList.Add("--audio-quality");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("--ffmpeg-location");
        psi.ArgumentList.Add(ffmpegDir);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outTemplate);
        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--add-metadata");
        psi.ArgumentList.Add("--embed-thumbnail");
        psi.ArgumentList.Add("--progress");
        psi.ArgumentList.Add("--postprocessor-args");
        psi.ArgumentList.Add(
            $"ffmpeg:-metadata \"title={title}\" " +
            $"-metadata \"artist={artist}\" " +
            $"-metadata \"album={album}\"");

        var proc = new System.Diagnostics.Process { StartInfo = psi };

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("    " + e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) Console.Error.WriteLine("    [ERR] " + e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
            Console.Error.WriteLine($"    [WARN] yt-dlp exited {proc.ExitCode} for: {title} — {artist}");
        else
            Console.WriteLine($"    [OK] {safeName}.mp3");
    }

    static string Sanitise(string input) =>
        Regex.Replace(input, @"[\\/:*?""<>|]", "_").Trim().TrimEnd('.');
}