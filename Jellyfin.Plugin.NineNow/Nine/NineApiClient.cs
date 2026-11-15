using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NineNow.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NineNow.Nine;

/// <summary>
/// 9Now live-experience API client: channel discovery, stream resolution and guide data.
/// Ported from the reverse-engineered Rust reference (ninetv_rust), updated for the
/// FAST-channel clean-HLS path discovered in 2026.
/// </summary>
public sealed class NineApiClient : INineApiClient
{
    private const string ApiBase = "https://api.9now.com.au";
    private const string LiveExperiencePath = "/apps/live-experience";
    private const string BrightcovePlaybackBase = "https://edge.api.brightcove.com/playback/v1";

    private static readonly TimeSpan ChannelCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LiveInfoCacheTtl = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ResolveCacheTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan PosterCacheTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan PosterMissCacheTtl = TimeSpan.FromHours(6);

    private const string SearchPagePath = "/web/search-page";
    private const int MaxPosterCacheEntries = 4000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NineApiClient> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, PosterCacheEntry> _posters = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _posterFetches = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NineApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public NineApiClient(IHttpClientFactory httpClientFactory, ILogger<NineApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the market (region) the 9Now API reports for this server's egress IP.
    /// </summary>
    public NineMarket? Market { get; private set; }

    /// <summary>
    /// Gets the live channel list from the switcher rail.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channels, or an empty list on failure.</returns>
    public async Task<IReadOnlyList<NineChannel>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = await GetOrFetchAsync<IReadOnlyList<NineChannel>>("channels", ChannelCacheTtl, async ct =>
        {
            using var doc = await GetJsonAsync($"{ApiBase}{LiveExperiencePath}?slug=channel-9", ct).ConfigureAwait(false);
            var root = UnwrapLxp(doc.RootElement);

            if (root.TryGetProperty("market", out var market))
            {
                Market = new NineMarket(
                    market.TryGetString("name"),
                    market.TryGetString("state"));
            }

            var result = new List<NineChannel>();
            if (!root.TryGetProperty("switcherRail", out var rail) || rail.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in rail.EnumerateArray())
            {
                var slug = item.TryGetString("slug");
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                result.Add(new NineChannel(
                    slug,
                    item.TryGetString("name") ?? slug,
                    item.TryGetString("type") ?? "channel",
                    PickImage(item.TryGetPropertyOrNull("switcherLogo"), "w480")));
            }

            return (IReadOnlyList<NineChannel>)result;
        }, cancellationToken).ConfigureAwait(false);

        return channels ?? Array.Empty<NineChannel>();
    }

    /// <summary>
    /// Gets the live info (stream candidates + listings) for one channel slug.
    /// </summary>
    /// <param name="slug">The channel slug.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The live info, or null on failure.</returns>
    public async Task<NineLiveInfo?> GetLiveInfoAsync(string slug, CancellationToken cancellationToken)
    {
        return await GetOrFetchAsync("live:" + slug, LiveInfoCacheTtl, async ct =>
        {
            using var doc = await GetJsonAsync(
                $"{ApiBase}{LiveExperiencePath}?slug={Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
            var root = UnwrapLxp(doc.RootElement);

            if (!root.TryGetProperty("stream", out var stream))
            {
                return null;
            }

            var video = stream.TryGetPropertyOrNull("video");
            var rawUrl = video?.TryGetString("url");
            var referenceId = video?.TryGetString("referenceId");
            var hasDrm = video?.TryGetBoolean("hasDrm") ?? false;

            string? postfix = null;
            if (video?.TryGetPropertyOrNull("ssai") is JsonElement ssai)
            {
                postfix = ssai.TryGetString("postfixParams");
            }

            string? promoImage = null;
            var programmes = new List<NineProgramme>();
            if (stream.TryGetPropertyOrNull("display") is JsonElement display)
            {
                promoImage = PickImage(display.TryGetPropertyOrNull("promoImage"), "w768");

                if (display.TryGetPropertyOrNull("listings") is JsonElement listings
                    && listings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in listings.EnumerateArray())
                    {
                        var programme = ParseProgramme(item);
                        if (programme is not null)
                        {
                            programmes.Add(programme);
                        }
                    }
                }
            }

            return new NineLiveInfo(
                slug,
                rawUrl,
                postfix,
                referenceId,
                hasDrm,
                programmes.Count > 0 ? programmes[0].Title : null,
                promoImage,
                programmes);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the live info for every channel, fetching with bounded parallelism.
    /// </summary>
    /// <param name="channels">The channels.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Slug to live info map; failed channels are omitted.</returns>
    public async Task<Dictionary<string, NineLiveInfo>> GetAllLiveInfoAsync(
        IReadOnlyList<NineChannel> channels,
        CancellationToken cancellationToken)
    {
        var result = new ConcurrentDictionary<string, NineLiveInfo>();
        var semaphore = new SemaphoreSlim(4);

        var tasks = channels.Select(async channel =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var info = await GetLiveInfoAsync(channel.Slug, cancellationToken).ConfigureAwait(false);
                if (info is not null)
                {
                    result[channel.Slug] = info;
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Dictionary<string, NineLiveInfo>(result);
    }

    /// <summary>
    /// Resolves a playable stream URL for a channel.
    /// Order: clean upstream (ad-free) → Brightcove fallback → raw SSAI URL as last resort.
    /// </summary>
    /// <param name="slug">The channel slug.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolved stream, or null when nothing playable was found.</returns>
    public Task<ResolvedStream?> ResolveStreamUrlAsync(
        string slug,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        return GetOrFetchAsync("resolve:" + slug, ResolveCacheTtl,
            ct => ResolveUncachedAsync(slug, config, ct), cancellationToken);
    }

    /// <summary>
    /// Looks up a show's card image via the 9Now search API, matched on the normalised
    /// programme title. Results (including misses) are cached; concurrent lookups for the
    /// same show share one request.
    /// </summary>
    /// <param name="title">The programme/show title as it appears in the guide feed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The card image URL, or null when no confident match was found.</returns>
    public async Task<string?> GetShowPosterUrlAsync(string title, CancellationToken cancellationToken)
    {
        var key = NormalizeTitle(title);
        if (key.Length < 3)
        {
            return null;
        }

        if (_posters.TryGetValue(key, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Url;
        }

        var fetch = _posterFetches.GetOrAdd(
            key,
            _ => FetchShowPosterAsync(title, key, CancellationToken.None));

        string? url;
        try
        {
            url = await fetch.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "9Now poster lookup failed for {Title}", title);
            return null;
        }
        finally
        {
            _posterFetches.TryRemove(key, out _);
        }

        if (_posters.Count >= MaxPosterCacheEntries)
        {
            _posters.Clear();
        }

        _posters[key] = new PosterCacheEntry(url, DateTime.UtcNow + (url is null ? PosterMissCacheTtl : PosterCacheTtl));
        return url;
    }

    private async Task<string?> FetchShowPosterAsync(string query, string normalizedKey, CancellationToken cancellationToken)
    {
        var url = ApiBase + SearchPagePath
            + "?query=" + Uri.EscapeDataString(query)
            + "&region=&token=&queryFlagsArgs=" + Uri.EscapeDataString("{\"flags\":{\"viewId\":\"\"}}");

        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("getSearchPage", out var page)
            || !page.TryGetProperty("sections", out var sections)
            || !sections.TryGetProperty("content", out var content)
            || !content.TryGetProperty("tabs", out var tabs)
            || tabs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var prefixMatch = false;
        var prefixCard = default(JsonElement);

        foreach (var tab in tabs.EnumerateArray())
        {
            if (!tab.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var card in cards.EnumerateArray())
            {
                var cardTitle = card.TryGetProperty("title", out var titleEl)
                                && titleEl.TryGetProperty("text", out var textEl)
                    ? textEl.GetString()
                    : null;

                var normalized = NormalizeTitle(cardTitle);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (normalized == normalizedKey)
                {
                    return PickSearchCardImage(card);
                }

                // "Antiques Roadshow: Series 29" against card "Antiques Roadshow" and
                // "Seinfeld" against "Seinfeld 24/7" are the same show.
                if (!prefixMatch
                    && (normalized.StartsWith(normalizedKey + " ", StringComparison.Ordinal)
                        || normalizedKey.StartsWith(normalized + " ", StringComparison.Ordinal)))
                {
                    prefixMatch = true;
                    prefixCard = card;
                }
            }
        }

        return prefixMatch ? PickSearchCardImage(prefixCard) : null;
    }

    private static string NormalizeTitle(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }

    private static string? PickSearchCardImage(JsonElement card)
    {
        if (!card.TryGetProperty("cardImage", out var img) || img.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var variantName in new[] { "default", "webp" })
        {
            if (!img.TryGetProperty(variantName, out var variant) || variant.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var size in new[] { "w768", "w1280", "w480", "w362", "w320", "w1920" })
            {
                if (variant.TryGetProperty(size, out var sizeObj)
                    && sizeObj.ValueKind == JsonValueKind.Object
                    && sizeObj.TryGetProperty("url", out var urlEl)
                    && urlEl.ValueKind == JsonValueKind.String)
                {
                    return urlEl.GetString();
                }
            }
        }

        return null;
    }

    private async Task<ResolvedStream?> ResolveUncachedAsync(
        string slug,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var info = await GetLiveInfoAsync(slug, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return null;
        }

        var cleanCandidates = new List<string>();

        // 1. Yospace wrapper query params embed the clean origin (main channels).
        if (info.RawVideoUrl is not null)
        {
            var query = ParseQuery(info.RawVideoUrl);

            if (query.TryGetValue("yo.up", out var yoUp)
                && yoUp.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && yoUp.Contains("://", StringComparison.Ordinal))
            {
                cleanCandidates.Add(yoUp.TrimEnd('/') + "/index.m3u8");
            }

            if (query.TryGetValue("yo.eb.fb", out var fbB64)
                && TryDecodeBase64Url(fbB64, out var fbUrl)
                && fbUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                cleanCandidates.Add(fbUrl);
            }
        }

        // 2. FAST channels expose the clean origin only in ssai.postfixParams (2026+ behaviour).
        if (info.SsaiPostfixParams is not null
            && ParseQuery(info.SsaiPostfixParams).TryGetValue("yo.eb.fb", out var postfixFb)
            && TryDecodeBase64Url(postfixFb, out var postfixUrl)
            && postfixUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            cleanCandidates.Add(postfixUrl);
        }

        if (config.AdFree)
        {
            foreach (var candidate in cleanCandidates)
            {
                if (await LooksLikePlayableManifestAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    return new ResolvedStream(candidate, "9now-clean", true);
                }

                _logger.LogDebug("9Now clean candidate rejected: {Url}", candidate);
            }
        }

        // 3. Brightcove reference (older FAST flow; may carry SSAI ads).
        if (!string.IsNullOrEmpty(info.ReferenceId)
            && !string.IsNullOrWhiteSpace(config.BrightcoveAccountId)
            && !string.IsNullOrWhiteSpace(config.BrightcovePolicyKey))
        {
            var bc = await ResolveBrightcoveAsync(
                info.ReferenceId, config.BrightcoveAccountId, config.BrightcovePolicyKey, cancellationToken)
                .ConfigureAwait(false);
            if (bc is not null)
            {
                return bc;
            }
        }

        // 4. Last resort: the raw Yospace SSAI manifest (has ad markers).
        if (info.RawVideoUrl is not null)
        {
            return new ResolvedStream(info.RawVideoUrl, "9now-ssai", false);
        }

        _logger.LogWarning("No playable stream found for 9Now channel {Slug}", slug);
        return null;
    }

    private async Task<ResolvedStream?> ResolveBrightcoveAsync(
        string referenceId,
        string accountId,
        string policyKey,
        CancellationToken cancellationToken)
    {
        var url = $"{BrightcovePlaybackBase}/accounts/{accountId}/videos/ref:{referenceId}";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", $"application/json;pk={policyKey}");
            request.Headers.TryAddWithoutValidation("BCOV-POLICY", policyKey);

            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Brightcove lookup failed for {Ref}: {Status}", referenceId, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("sources", out var sources)
                && sources.ValueKind == JsonValueKind.Array)
            {
                string? fallback = null;
                foreach (var source in sources.EnumerateArray())
                {
                    var src = source.TryGetString("src");
                    if (string.IsNullOrEmpty(src))
                    {
                        continue;
                    }

                    var hasKeySystems = source.TryGetPropertyOrNull("key_systems") is not null;
                    var type = source.TryGetString("type") ?? string.Empty;
                    var isHls = type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);

                    if (isHls && !hasKeySystems)
                    {
                        return new ResolvedStream(src, "brightcove", false);
                    }

                    if (hasKeySystems)
                    {
                        _logger.LogInformation(
                            "Brightcove source for {Ref} is DRM protected; skipping", referenceId);
                    }
                    else
                    {
                        fallback ??= src;
                    }
                }

                if (fallback is not null)
                {
                    return new ResolvedStream(fallback, "brightcove", false);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Brightcove lookup error for {Ref}", referenceId);
        }

        return null;
    }

    private async Task<bool> LooksLikePlayableManifestAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await SendAsync(request, cancellationToken, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
            if (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[8];
            var read = await stream.ReadAsync(buffer, 0, 8, cancellationToken).ConfigureAwait(false);
            return read >= 7 && Encoding.ASCII.GetString(buffer, 0, 7) == "#EXTM3U";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await _httpClientFactory
                .CreateClient(NamedClient.Default)
                .SendAsync(request, completionOption, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            request.Dispose();
            throw new HttpRequestException("Request timed out: " + request.RequestUri);
        }
    }

    private async Task<T?> GetOrFetchAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> fetch,
        CancellationToken cancellationToken)
        where T : class
    {
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            return (T)entry.Value;
        }

        try
        {
            var value = await fetch(cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                _cache[key] = new CacheEntry(value, DateTime.UtcNow + ttl);
                return value;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "9Now API call failed for cache key {Key}", key);
        }

        // Stale fallback: rather than failing hard, serve expired data if we have it.
        if (entry.Value is not null)
        {
            _logger.LogDebug("Serving stale 9Now cache entry {Key}", key);
            return (T)entry.Value;
        }

        return null;
    }

    /// <summary>Navigates the GraphQL-style {"data":{"getLXP":{...}}} wrapper if present.</summary>
    private static JsonElement UnwrapLxp(JsonElement root)
    {
        if (root.TryGetPropertyOrNull("data") is JsonElement data
            && data.TryGetPropertyOrNull("getLXP") is JsonElement lxp)
        {
            return lxp;
        }

        return root;
    }

    private static NineProgramme? ParseProgramme(JsonElement item)
    {
        var title = item.TryGetString("title");
        var startRaw = item.TryGetString("startDate") ?? item.TryGetString("programStartDate");
        var endRaw = item.TryGetString("endDate") ?? item.TryGetString("programEndDate");

        if (title is null
            || !DateTimeOffset.TryParse(startRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)
            || !DateTimeOffset.TryParse(endRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var end))
        {
            return null;
        }

        // FAST mega-listings span years (startDate 2023 -> endDate 2032). The feed also
        // carries the *actual* current episode window in programStartDate/programEndDate;
        // prefer that so the guide shows real ~30 minute blocks.
        if (end - start > TimeSpan.FromHours(30)
            && DateTimeOffset.TryParse(item.TryGetString("programStartDate"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var actualStart)
            && DateTimeOffset.TryParse(item.TryGetString("programEndDate"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var actualEnd)
            && actualEnd > actualStart)
        {
            start = actualStart;
            end = actualEnd;
        }

        NineProgramme? secondary = null;
        if (item.TryGetPropertyOrNull("secondaryListing") is JsonElement sec
            && sec.ValueKind == JsonValueKind.Object
            && sec.TryGetString("title") is string secTitle
            && !string.IsNullOrEmpty(secTitle))
        {
            // 2026 feed: the next-episode window is in programStartDate/programEndDate;
            // startTime/endTime still exist but are null. Accept either.
            var secStartRaw = sec.TryGetString("programStartDate") ?? sec.TryGetString("startTime");
            var secEndRaw = sec.TryGetString("programEndDate") ?? sec.TryGetString("endTime");
            if (DateTimeOffset.TryParse(secStartRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var secStart)
                && DateTimeOffset.TryParse(secEndRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var secEnd))
            {
                secondary = new NineProgramme(
                    secTitle,
                    sec.TryGetString("subTitle"),
                    null,
                    null,
                    null,
                    secStart,
                    secEnd,
                    null,
                    null);
            }
        }

        return new NineProgramme(
            title,
            item.TryGetString("subtitle"),
            item.TryGetString("description"),
            item.TryGetString("genreString"),
            item.TryGetString("classification"),
            start,
            end,
            item.TryGetString("upNextTitle"),
            secondary);
    }

    /// <summary>Splits a raw query string (or full URL query) into unescaped key/value pairs.</summary>
    private static Dictionary<string, string> ParseQuery(string input)
    {
        var query = input;
        var mark = query.IndexOf('?');
        if (mark >= 0)
        {
            query = query[(mark + 1)..];
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = pair[..eq];
            var value = pair[(eq + 1)..];
            try
            {
                value = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                // keep raw
            }

            result[key] = value;
        }

        return result;
    }

    private static bool TryDecodeBase64Url(string input, out string? decoded)
    {
        decoded = null;
        try
        {
            var s = input.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: return false;
            }

            var text = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            if (text.Length > 0 && (text.StartsWith("http://", StringComparison.Ordinal)
                || text.StartsWith("https://", StringComparison.Ordinal)))
            {
                decoded = text.Trim();
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }

    private static string? PickImage(JsonElement? container, string preferredSize)
    {
        if (container is null || container.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (container.Value.TryGetPropertyOrNull("sizes") is not JsonElement sizes
            || sizes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (sizes.TryGetString(preferredSize) is string preferred)
        {
            return preferred;
        }

        var sizeNames = new[] { "w768", "w320", "w1280", "w1920" };
        foreach (var order in sizeNames)
        {
            if (sizes.TryGetString(order) is string alt)
            {
                return alt;
            }
        }

        foreach (var prop in sizes.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    private readonly record struct CacheEntry(object Value, DateTime ExpiresUtc);

    private readonly record struct PosterCacheEntry(string? Url, DateTime ExpiresUtc);
}

internal static class JsonElementExtensions
{
    public static string? TryGetString(this JsonElement element, string property)
    {
        return element.TryGetPropertyOrNull(property) is JsonElement value
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static bool TryGetBoolean(this JsonElement element, string property)
    {
        return element.TryGetPropertyOrNull(property) is JsonElement value
            && value.ValueKind == JsonValueKind.True;
    }

    public static JsonElement? TryGetPropertyOrNull(this JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind != JsonValueKind.Null)
        {
            return value;
        }

        return null;
    }
}
