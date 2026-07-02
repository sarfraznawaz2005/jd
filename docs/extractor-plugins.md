# Third-party media extractor plugins

> How to add your own site-specific `IMediaExtractor` to the engine without touching JustDownload.Core
> (TASK-150). See also `IMediaExtractor` / `IMediaExtractorRegistry` in
> `JustDownload.Core/Media/Extraction/` and locked decision **D3**.

## What this is — and isn't

This is **in-process dependency-injection registration**. Your own project references
`JustDownload.Core`, implements `IMediaExtractor`, and calls one public extension method on the
`IServiceCollection` you (or your fork) build the app's composition root from. There is no
runtime-loaded `.dll` / plugins-folder system — JustDownload has no assembly-loading infrastructure,
and this feature doesn't add any.

## The contract

```csharp
public interface IMediaExtractor
{
    string Name { get; }
    int Priority { get; }
    Task<MediaSource?> TryExtractAsync(MediaRequest request, CancellationToken cancellationToken = default);
}
```

- `TryExtractAsync` returns a `MediaSource` when it recognises `request.Url`, or `null` when it
  doesn't. **Never throw for an ordinary unrecognised URL** — that's not an error, it just means "not
  mine, try the next extractor." The registry does catch and log a throwing extractor so one bad
  plugin can't break the chain, but treat that as a safety net, not a return path.
- `Priority` decides try-order — the registry sorts every registered extractor ascending by `Priority`
  once, then tries each in turn until one returns non-null. Built-in priorities: YouTube = 90,
  Facebook = 91, HLS = 100, DASH = 110, Progressive (generic catch-all) = 1000, yt-dlp (last resort) =
  `int.MaxValue`. Pick a `Priority` in the open **100–999** band so your extractor runs after the
  protocol-level HLS/DASH extractors but before the generic Progressive catch-all — or wherever else
  makes sense for your site.
- Degrade gracefully (CLAUDE.md §5 "Honest extraction"): if you can't safely extract media, return
  `null` rather than guessing or fabricating a result.

## Registering your extractor

```csharp
using JustDownload.Core;
using JustDownload.Core.Media.Extraction;
using Microsoft.Extensions.DependencyInjection;

public sealed class AcmeClipsExtractor : IMediaExtractor
{
    public string Name => "acme-clips";
    public int Priority => 500;

    public Task<MediaSource?> TryExtractAsync(MediaRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Url.Host != "clips.acme.example")
        {
            return Task.FromResult<MediaSource?>(null);
        }

        var source = new MediaSource
        {
            ExtractorName = Name,
            Kind = MediaKind.Progressive,
            Url = request.Url,
        };
        return Task.FromResult<MediaSource?>(source);
    }
}

var services = new ServiceCollection();
services.AddJustDownloadCore(); // or AddJustDownloadMedia(), whichever your host already calls
services.AddThirdPartyMediaExtractor<AcmeClipsExtractor>();
```

`AddThirdPartyMediaExtractor<TExtractor>()` wraps the same `TryAddEnumerable` registration the
built-in extractors use, so it composes cleanly regardless of call order relative to
`AddJustDownloadCore()` / `AddJustDownloadMedia()` — the registry only sorts by `Priority` at
construction, not by registration order.
