using JustDownload.Core;
using JustDownload.Core.Data;
using JustDownload.Core.Media;
using JustDownload.Core.Media.Extraction;
using JustDownload.Core.Media.Hls;
using JustDownload.Core.Settings;
using JustDownload.LiveSmoke;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

// JustDownload.LiveSmoke — diagnostic-only harness for the task "Live smoke-test harness: diagnose real
// extraction failures for YouTube, Facebook, Twitter/X, Instagram". NOT a test project: it hits real,
// live external sites, so it must never run automatically (dotnet build/dotnet test never invoke it —
// this is a plain console Main, no [Fact]/[Trait]). Run it explicitly and read the output:
//
//   dotnet run --project JustDownload.LiveSmoke -c Release
//
// It builds the real DI container via JustDownload.Core's own AddJustDownloadCore() (which internally
// calls AddJustDownloadMedia() — ServiceCollectionExtensions.cs) — the exact composition root every real
// host (App/NativeHost/Cli) uses — then, for each scenario, iterates the real registered IMediaExtractor
// set itself (via IMediaExtractorRegistry.Extractors, DI-resolved) and calls TryExtractAsync on each one
// directly, in its own try/catch. This deliberately bypasses MediaExtractorRegistry.ExtractAsync's own
// swallow-and-continue behavior (JustDownload.Core/Media/Extraction/MediaExtractorRegistry.cs) — that
// behavior is a correct, deliberate production contract (one misbehaving extractor must not break the
// chain) and is NOT changed here; this harness only adds a second, diagnostic-only vantage point on top
// of it, so a human can see the real per-extractor reason instead of the one generic string the app UI
// shows.
//
// URL sourcing (task AC1/AC2 — "real, publicly-known video URLs, not synthetic fixtures"): every URL
// below was found via a live web search against a real, currently-indexed page (a GitHub README, a news
// article, or an archive.today/archive.ph capture of the real live page) rather than invented by hand.
// Two Facebook shapes could not be sourced this way within the scope of this task — see the FACEBOOK
// section below for exactly which two and why. Live URLs go stale over time (an account renames, a post
// is deleted) — that is expected drift for a *live* smoke test, not a harness bug; if a scenario starts
// reporting "declined" for a shape that used to be "accepted", check the URL is still live before
// suspecting the extractor.
//
// Known sandbox constraint (carried forward from the task's own investigation, and reconfirmed live by
// this run — see the completion report): facebook.com and instagram.com are unreachable (connection
// failure, not an HTTP error) from this specific dev sandbox, while twitter.com/x.com and youtube.com are
// reachable. That is a fact about this machine's network, not the app — run this on an unrestricted
// machine to see the Facebook/Instagram scenarios' real network-dependent outcomes.

string tempDataDir = Path.Combine(Path.GetTempPath(), "jd-livesmoke-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("JUSTDOWNLOAD_DATA_DIR", tempDataDir);

try
{
    var services = new ServiceCollection();
    services.AddSingleton<IDatabasePathProvider>(new SmokeDatabasePathProvider(tempDataDir));

    // This harness points JUSTDOWNLOAD_DATA_DIR at a throwaway directory so it never touches the user's real
    // database — but AppDataPaths honours that same variable, so AddJustDownloadMedia's default
    // YtDlpOptions.VendorDirectory would resolve to the (empty) temp directory and the yt-dlp probe below
    // would report "not provisioned" even on a machine where the user HAS downloaded it. Registering the
    // option first (Core uses TryAddSingleton, so this one wins) points the locator at the real app-data
    // vendor directory, making the probe reflect what the running app actually sees.
    services.AddSingleton(new YtDlpOptions
    {
        VendorDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JustDownload", "yt-dlp"),
    });

    services.AddJustDownloadCore();
    await using ServiceProvider provider = services.BuildServiceProvider();
    await provider.InitializeJustDownloadCoreAsync();

    var registry = provider.GetRequiredService<IMediaExtractorRegistry>();
    var settings = provider.GetRequiredService<ISettingsService>();
    var ytDlpLocator = provider.GetRequiredService<IYtDlpLocator>();
    await settings.LoadAsync();

    Console.WriteLine("JustDownload.LiveSmoke — real per-extractor diagnostic run");
    Console.WriteLine(new string('=', 78));
    Console.WriteLine("Registered extractors (ascending Priority, the real order the app tries them):");
    foreach (IMediaExtractor extractor in registry.Extractors)
    {
        Console.WriteLine($"  [{extractor.Priority,10}] {extractor.Name}");
    }

    Console.WriteLine();
    Console.WriteLine($"VideoCaptureEnabled (yt-dlp master toggle) at startup: {settings.Current.VideoCaptureEnabled} (default is false, D3).");
    YtDlpInfo? ytDlpInfo = await ytDlpLocator.LocateAsync();
    Console.WriteLine(ytDlpInfo is null
        ? "yt-dlp provisioning: NOT FOUND in this environment (no configured path / vendor download / PATH entry) — the yt-dlp fallback can only ever decline here, regardless of the toggle."
        : $"yt-dlp provisioning: FOUND at {ytDlpInfo.ExecutablePath} (version {ytDlpInfo.Version}).");

    var scenarios = new List<Scenario>
    {
        // --- The three exact URLs the user reported failing on 2026-08-14, kept verbatim so a re-run
        // reproduces his case rather than an equivalent-looking substitute.
        new("YouTube", "USER-REPORTED failing URL", "https://www.youtube.com/watch?v=CqlDf9ba4jA",
            "Reported symptom: quality picker opened, then \"Couldn't find downloadable media at this URL.\""),
        new("Twitter/X", "USER-REPORTED failing URL", "https://x.com/unicodef1wn/status/2087461469881336049/video/1",
            "Reported symptom: plain New-download dialog (no quality picker), output file not playable."),
        new("Facebook", "USER-REPORTED failing URL", "https://www.facebook.com/reel/2044478973099445",
            "Reported symptom: quality picker opened, then \"Couldn't find downloadable media at this URL.\""),

        // --- YouTube (task item 6): 2-3 real public URLs, one chosen to stress the extractor differently
        // from the simplest case, to try to reproduce the "sometimes couldn't find downloadable media"
        // report. YouTubeMediaExtractor (JustDownload.Core/Media/YouTube/YouTubeMediaExtractor.cs) only
        // ever accepts an unciphered, unthrottled `formats` entry — by its own doc comment, most real
        // videos today expose none, so "declined" here is the expected, correct D3 outcome, not a bug.
        new("YouTube", "standard watch URL", "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "Rick Astley — Never Gonna Give You Up; the canonical stable public test video."),
        new("YouTube", "first-ever YouTube upload (very old encode)", "https://www.youtube.com/watch?v=jNQXAC9IVRw",
            "\"Me at the zoo\" (2005) — official YouTube channel; stresses an unusually old/short encode."),
        new("YouTube", "high-bitrate 4K/60fps stress case", "https://www.youtube.com/watch?v=aqz-KE-bpKQ",
            "\"Big Buck Bunny 60fps 4K\" — many more/larger formats than the simplest case."),

        // --- Facebook (task item 3 / AC2): all 7 shapes FacebookMediaExtractor's VideoIdRegex claims to
        // support (commit 9a39d18) — JustDownload.Core/Media/Facebook/FacebookMediaExtractor.cs:211-213.
        // 5 of 7 are sourced from a real, currently-indexed page (see comment per scenario). The
        // "/stories/" and "/reels/deeplink/" shapes could NOT be sourced as a confirmed-real, still-
        // resolvable URL within this task's search budget: Stories are ephemeral by design (expire ~24h,
        // so no long-lived real example can exist) and reels/deeplink is an in-app share deep-link rarely
        // surfaced in indexable web content. Their URLs below are structurally-representative (same id
        // length/shape as the confirmed-real ones) but are NOT independently verified as currently live —
        // called out explicitly rather than passed off as equally sourced.
        new("Facebook", "?v= (watch page)", "https://www.facebook.com/watch/?v=650298589662755",
            "Confirmed real via an archive.today capture of the live facebook.com/watch page."),
        new("Facebook", "/reel/", "https://www.facebook.com/reel/1351675560413773",
            "Confirmed real via an archive.today capture of the live facebook.com/reel page."),
        new("Facebook", "/videos/", "https://www.facebook.com/nytimes/videos/tetris-loses-to-13-year-old-boy/351231740960920/",
            "Confirmed real — The New York Times' verified Page, still-indexed video post."),
        new("Facebook", "/groups/<id>/permalink/<id>/ (group-post permalink)", "https://www.facebook.com/groups/pcsruins/permalink/570761527756076/",
            "Confirmed real — cited as a live example in the pedruhb/FacebookVideoScraper README on GitHub."),
        new("Facebook", "/<page>/posts/<id> (page post)", "https://www.facebook.com/Nintendo/posts/1926829187474205",
            "Confirmed real — cited as a live example in a kevinzg/facebook-scraper GitHub issue."),
        new("Facebook", "/stories/<id>/<id>/ (story) — NOT independently confirmed live, see note above", "https://www.facebook.com/stories/570761527756076/570761527756077/",
            "Best-effort: structurally matches the documented shape; Stories expire ~24h so no durable real example exists to cite."),
        new("Facebook", "/reels/deeplink/?id=<id> — NOT independently confirmed live, see note above", "https://www.facebook.com/reels/deeplink/?id=1351675560413773",
            "Best-effort: structurally matches the documented shape; this in-app deep-link form is rarely surfaced in indexable pages."),

        // --- Twitter/X (task item 4a): a raw page URL through the full extractor set. Expected: every
        // in-house extractor declines (no Twitter-specific extractor exists at all — see task notes).
        new("Twitter/X", "raw status page URL (no in-house extractor exists)", "https://x.com/RodneyMKirabo/status/1954770955336585444",
            "Confirmed real, currently-live tweet with an attached video (archive.ph capture confirms it existed as posted)."),

        // --- Twitter/X (task item 4c): an independent proof that the OTHER half of the real working
        // pipeline (browser extension sniffs .m3u8 -> routed straight to HlsMediaExtractor, bypassing
        // page-URL extraction entirely) still functions. Not itself a Twitter URL — a known-stable public
        // HLS test stream fed directly through the same extractor set, so HlsMediaExtractor (priority 100,
        // runs before the generic catch-all) gets first look, exactly as it would for a sniffed .m3u8.
        new("Twitter/X (HLS half of the pipeline)", "known .m3u8 fed through the real extractor set", "https://devstreaming-cdn.apple.com/videos/streaming/examples/bipbop_16x9/bipbop_16x9_variant.m3u8",
            "Apple's own public HLS test stream (bipbop) — stable, well-known, not Twitter-hosted; proves HlsMediaExtractor itself works."),

        // --- Instagram (task item 5): same shape as Twitter — no in-house extractor exists at all.
        new("Instagram", "reel page URL (no in-house extractor exists)", "https://www.instagram.com/reel/C4PYiJQubeR/",
            "Confirmed real — cited as a live example in Instagram's own utools.readme.io API reference."),
    };

    // The yt-dlp fallback declines instantly, without even calling the locator, while VideoCaptureEnabled is
    // off (YtDlpMediaExtractor.cs:80-83). Opting in BEFORE the scenarios matters: with the default-off
    // toggle every scenario below would report "yt-dlp declined" for a reason that has nothing to do with
    // the URL, hiding the one result this harness exists to establish.
    await settings.UpdateAsync(s => s with { VideoCaptureEnabled = true });
    Console.WriteLine($"VideoCaptureEnabled flipped on for this run (real ISettingsService): {settings.Current.VideoCaptureEnabled}.");

    Console.WriteLine();
    Console.WriteLine("--- Full-pipeline scenarios (every registered extractor tried, in real priority order) ---");
    foreach (Scenario scenario in scenarios)
    {
        await RunScenarioAsync(scenario, registry.Extractors);
    }

    // --- Twitter/X (task item 4b) + Instagram (task item 5b): does the optional yt-dlp fallback change
    // the outcome once the user opts in? Flip the real settings toggle through the real ISettingsService
    // (not a hand-rolled substitute), then call the real YtDlpMediaExtractor directly.
    Console.WriteLine();
    Console.WriteLine("--- yt-dlp direct probe (isolating the fallback from the rest of the chain) ---");

    IMediaExtractor? ytDlpExtractor = registry.Extractors.FirstOrDefault(e => e.Name == "yt-dlp");
    if (ytDlpExtractor is null)
    {
        Console.WriteLine("yt-dlp extractor was not found in the registered set — cannot probe it.");
    }
    else if (ytDlpInfo is null)
    {
        Console.WriteLine("yt-dlp is not provisioned in this environment (see above) — skipping the live yt-dlp");
        Console.WriteLine("subprocess probe rather than reporting a fake outcome. On a machine where the user has");
        Console.WriteLine("explicitly downloaded yt-dlp via Settings, re-run this harness to see the real result.");
    }
    else
    {
        await RunSingleExtractorAsync(ytDlpExtractor, "Twitter/X", "https://x.com/RodneyMKirabo/status/1954770955336585444");
        await RunSingleExtractorAsync(ytDlpExtractor, "Instagram", "https://www.instagram.com/reel/C4PYiJQubeR/");
    }

    // --- Real HLS pipeline, end to end (fragmented-MP4 / #EXT-X-MAP). Twitter/X serves CMAF: the media
    // playlist declares an #EXT-X-MAP initialization segment holding the ftyp/moov boxes, and the media
    // segments are .m4s fragments that are meaningless without it. This drives the real DI-wired
    // IHlsDownloader + IHlsConcatenator and reports the first MP4 box of the concatenated output —
    // "ftyp" means the init segment was included, "styp" means it was dropped and the file is unplayable.
    Console.WriteLine();
    Console.WriteLine("--- Real HLS pipeline end-to-end (fragmented-MP4 / EXT-X-MAP) ---");
    var hlsDownloader = provider.GetRequiredService<IHlsDownloader>();
    var hlsConcatenator = provider.GetRequiredService<IHlsConcatenator>();
    const string hlsPlaylistUrl =
        "https://video.twimg.com/amplify_video/2087460753578098688/pl/avc1/396x270/0HkqU7YAO_9dKEpe.m3u8";
    string hlsWorkDirectory = Path.Combine(Path.GetTempPath(), "jd-livesmoke-hls");
    Console.WriteLine($"  Playlist: {hlsPlaylistUrl}");
    try
    {
        HlsDownloadResult hls = await hlsDownloader.DownloadAsync(new Uri(hlsPlaylistUrl), hlsWorkDirectory);
        string hlsOutput = Path.Combine(hlsWorkDirectory, "output.mp4");
        await hlsConcatenator.ConcatenateAsync(hls.SegmentFiles, hlsOutput);

        var header = new byte[8];
        int headerBytes;
        await using (FileStream stream = File.OpenRead(hlsOutput))
        {
            headerBytes = await stream.ReadAsync(header);
        }

        string firstBox = headerBytes == header.Length
            ? System.Text.Encoding.ASCII.GetString(header, 4, 4)
            : "(file too short)";
        Console.WriteLine($"  files concatenated: {hls.SegmentFiles.Count} (including the init segment when present)");
        Console.WriteLine($"  output bytes:       {new FileInfo(hlsOutput).Length}");
        Console.WriteLine($"  first MP4 box:      '{firstBox}'  (expect 'ftyp'; 'styp' = EXT-X-MAP init segment missing -> unplayable)");
        Console.WriteLine($"  output file:        {hlsOutput}   (verify with: ffprobe \"{hlsOutput}\")");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  HLS pipeline THREW {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine("  (Twitter media-playlist URLs expire — a 403/404 here means the URL went stale, not a pipeline bug.)");
    }

    Console.WriteLine();
    Console.WriteLine("Done. This is a diagnostic tool, not a pass/fail gate — read the per-extractor lines above.");
}
finally
{
    Environment.SetEnvironmentVariable("JUSTDOWNLOAD_DATA_DIR", null);

    // Microsoft.Data.Sqlite pools native connection handles by default even after the DI container (and
    // every SqliteConnection it created) has been disposed, which can still hold the temp DB file locked
    // for a moment — clear the pool first so the delete below actually succeeds instead of silently
    // leaving the temp directory behind.
    SqliteConnection.ClearAllPools();

    try
    {
        if (Directory.Exists(tempDataDir))
        {
            Directory.Delete(tempDataDir, recursive: true);
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

return 0;

static async Task RunScenarioAsync(Scenario scenario, IReadOnlyList<IMediaExtractor> extractors)
{
    Console.WriteLine();
    Console.WriteLine($"[{scenario.Site}] {scenario.Shape}");
    Console.WriteLine($"  URL:  {scenario.Url}");
    Console.WriteLine($"  Note: {scenario.Note}");

    var request = new MediaRequest { Url = new Uri(scenario.Url) };
    foreach (IMediaExtractor extractor in extractors)
    {
        await PrintOutcomeAsync(extractor, request);
    }
}

static async Task RunSingleExtractorAsync(IMediaExtractor extractor, string site, string url)
{
    Console.WriteLine();
    Console.WriteLine($"[{site}] yt-dlp direct probe");
    Console.WriteLine($"  URL:  {url}");
    await PrintOutcomeAsync(extractor, new MediaRequest { Url = new Uri(url) });
}

static async Task PrintOutcomeAsync(IMediaExtractor extractor, MediaRequest request)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    try
    {
        MediaSource? source = await extractor.TryExtractAsync(request, cts.Token);
        Console.WriteLine(source is null
            ? $"    {extractor.Name,-12} declined (returned null)"
            : $"    {extractor.Name,-12} ACCEPTED — kind={source.Kind}, variants={source.Variants.Count}, audioVariants={source.AudioVariants.Count}, suggestedFileName={source.SuggestedFileName ?? "(none)"}");
    }
    catch (MediaExtractionFailedException ex)
    {
        // "Mine, but it failed" — the same reason the app's dialogs now show the user.
        Console.WriteLine($"    {extractor.Name,-12} FAILED (recognised the URL, could not extract): {ex.Message}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"    {extractor.Name,-12} TIMED OUT after 25s (treated separately from a real exception — likely an unreachable host in this sandbox)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    {extractor.Name,-12} THREW {ex.GetType().Name}: {ex.Message}");
    }
}

internal sealed record Scenario(string Site, string Shape, string Url, string Note);
