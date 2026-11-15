using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Jellyfin.Plugin.NineNow.Configuration;
using Jellyfin.Plugin.NineNow.LiveTv;
using Jellyfin.Plugin.NineNow.Nine;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.NineNow.Api;

/// <summary>
/// Serves the dynamic M3U playlist, XMLTV guide and stream redirects that make the
/// Nine Network available inside Jellyfin's Live TV subsystem.
/// </summary>
[ApiController]
[Route("NineNow")]
public class NineNowController : ControllerBase
{
    private const string PlaylistPath = "/NineNow/playlist.m3u";
    private const string EpgPath = "/NineNow/epg.xml";

    private readonly INineApiClient _nine;
    private readonly ILiveTvSetupService _setup;
    private readonly IServerApplicationHost _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="NineNowController"/> class.
    /// </summary>
    /// <param name="nine">The 9Now API client.</param>
    /// <param name="setup">The Live TV registration service.</param>
    /// <param name="appHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    public NineNowController(INineApiClient nine, ILiveTvSetupService setup, IServerApplicationHost appHost)
    {
        _nine = nine;
        _setup = setup;
        _appHost = appHost;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets the dynamic M3U playlist consumed by Jellyfin's built-in M3U tuner.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playlist.</returns>
    [HttpGet("playlist.m3u")]
    [AllowAnonymous]
    [Produces("application/x-mpegURL")]
    public async Task<IActionResult> GetPlaylist(CancellationToken cancellationToken)
    {
        var channels = await GetVisibleChannelsAsync(cancellationToken).ConfigureAwait(false);
        var baseUrl = _appHost.GetSmartApiUrl(Request);

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        var number = 1;
        foreach (var channel in channels)
        {
            sb.Append($"#EXTINF:-1 tvg-id=\"{channel.Slug}\" tvg-name=\"{channel.Name}\" tvg-chno=\"{number}\"");
            if (!string.IsNullOrEmpty(channel.LogoUrl))
            {
                sb.Append($" tvg-logo=\"{channel.LogoUrl}\"");
            }

            sb.Append($" group-title=\"9Now\",{channel.Name}");
            sb.AppendLine();
            sb.Append($"{baseUrl}{NineStreamPath(channel.Slug)}");
            sb.AppendLine();
            number++;
        }

        return Content(sb.ToString(), "application/x-mpegURL", Encoding.UTF8);
    }

    /// <summary>
    /// Resolves and redirects to the current playable HLS URL for a channel.
    /// </summary>
    /// <param name="slug">The 9Now channel slug.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A redirect to the resolved manifest.</returns>
    [HttpGet("stream/{slug}/master.m3u8")]
    [AllowAnonymous]
    public async Task<IActionResult> GetStream(string slug, CancellationToken cancellationToken)
    {
        var resolved = await _nine.ResolveStreamUrlAsync(slug, Config, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Could not resolve a stream for this channel right now.");
        }

        return Redirect(resolved.Url);
    }

    /// <summary>
    /// Gets the XMLTV guide generated from the 9Now listings feed.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The XMLTV document.</returns>
    [HttpGet("epg.xml")]
    [AllowAnonymous]
    [Produces("application/xml")]
    public async Task<IActionResult> GetEpg(CancellationToken cancellationToken)
    {
        var config = Config;
        var channels = await GetVisibleChannelsAsync(cancellationToken).ConfigureAwait(false);
        var liveInfo = await _nine.GetAllLiveInfoAsync(channels, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddHours(-config.GuideLookbackHours);
        var windowEnd = now.AddHours(config.GuideLookaheadHours);

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            Async = true
        };

        using var ms = new System.IO.MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("tv");
            writer.WriteAttributeString("generator-info-name", "Jellyfin.Plugin.NineNow");

            foreach (var channel in channels)
            {
                writer.WriteStartElement("channel");
                writer.WriteAttributeString("id", channel.Slug);
                writer.WriteElementString("display-name", channel.Name);
                if (!string.IsNullOrEmpty(channel.LogoUrl))
                {
                    writer.WriteStartElement("icon");
                    writer.WriteAttributeString("src", channel.LogoUrl);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            foreach (var channel in channels)
            {
                if (!liveInfo.TryGetValue(channel.Slug, out var info))
                {
                    continue;
                }

                var programmeArt = config.ProgrammeArtwork ? info.PromoImageUrl : null;

                foreach (var block in BuildProgrammeBlocks(info.Programmes, windowStart, windowEnd))
                {
                    var programme = block.Programme;
                    writer.WriteStartElement("programme");
                    writer.WriteAttributeString("start", FormatXmlTvDate(block.Start));
                    writer.WriteAttributeString("stop", FormatXmlTvDate(block.End));
                    writer.WriteAttributeString("channel", channel.Slug);

                    var title = programme.Title;
                    if (programme.IsOpenEnded && !string.IsNullOrEmpty(programme.Subtitle))
                    {
                        title = $"{title} - {programme.Subtitle}";
                    }

                    writer.WriteElementString("title", title);

                    var description = programme.Description;
                    if (string.IsNullOrEmpty(description) && !string.IsNullOrEmpty(programme.Subtitle))
                    {
                        description = programme.Subtitle;
                    }

                    if (!string.IsNullOrEmpty(description))
                    {
                        writer.WriteElementString("desc", description);
                    }

                    if (!string.IsNullOrEmpty(programme.Genre))
                    {
                        writer.WriteElementString("category", programme.Genre);
                    }

                    if (!string.IsNullOrEmpty(programme.Classification))
                    {
                        writer.WriteStartElement("rating");
                        writer.WriteAttributeString("system", "ACB");
                        writer.WriteElementString("value", programme.Classification);
                        writer.WriteEndElement();
                    }

                    if (!programme.IsOpenEnded && !string.IsNullOrEmpty(programme.Subtitle))
                    {
                        writer.WriteElementString("sub-title", programme.Subtitle);
                    }

                    if (!string.IsNullOrEmpty(programmeArt))
                    {
                        writer.WriteStartElement("icon");
                        writer.WriteAttributeString("src", programmeArt);
                        writer.WriteEndElement();

                        // Jellyfin's extended XMLTV: still -> guide cell thumb, backdrop -> detail backdrop.
                        WriteImageElement(writer, "still", programmeArt);
                        WriteImageElement(writer, "backdrop", programmeArt);
                    }

                    writer.WriteEndElement();
                }
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return Content(Encoding.UTF8.GetString(ms.ToArray()), "application/xml", Encoding.UTF8);
    }

    /// <summary>
    /// Gets current 9Now API status (market, channel counts) for the config page.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Status information.</returns>
    [HttpGet("channels")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> GetChannels(CancellationToken cancellationToken)
    {
        var channels = await GetVisibleChannelsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            market = _nine.Market,
            channels = channels.Count,
            entry = new
            {
                playlist = $"{_appHost.GetSmartApiUrl(Request)}{PlaylistPath}",
                epg = $"{_appHost.GetSmartApiUrl(Request)}{EpgPath}"
            }
        });
    }

    /// <summary>
    /// Registers (or updates) the 9Now M3U tuner and XmlTV EPG provider in Jellyfin's Live TV settings.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Registration result.</returns>
    [HttpPost("setup")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public async Task<IActionResult> Setup(CancellationToken cancellationToken)
    {
        var baseUrl = _appHost.GetSmartApiUrl(Request);
        try
        {
            var result = await _setup.RegisterAsync(baseUrl, cancellationToken).ConfigureAwait(false);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Removes the 9Now tuner and EPG provider entries from Jellyfin's Live TV settings.
    /// </summary>
    /// <returns>Removal result.</returns>
    [HttpPost("unregister")]
    [Authorize(Policy = Policies.LiveTvManagement)]
    public IActionResult Unregister()
    {
        _setup.Unregister();
        return Ok(new { removed = true });
    }

    private async Task<System.Collections.Generic.IReadOnlyList<NineChannel>> GetVisibleChannelsAsync(
        CancellationToken cancellationToken)
    {
        var channels = await _nine.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        if (!Config.IncludeFastChannels)
        {
            var filtered = new System.Collections.Generic.List<NineChannel>();
            foreach (var channel in channels)
            {
                if (string.Equals(channel.Kind, "channel", StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(channel);
                }
            }

            return filtered;
        }

        return channels;
    }

    private static string NineStreamPath(string slug)
        => $"/NineNow/stream/{Uri.EscapeDataString(slug)}/master.m3u8";

    private static System.Collections.Generic.IReadOnlyList<ProgrammeBlock> BuildProgrammeBlocks(
        System.Collections.Generic.IReadOnlyList<NineProgramme> programmes,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var blocks = new System.Collections.Generic.List<ProgrammeBlock>();
        var covered = new System.Collections.Generic.List<(DateTimeOffset Start, DateTimeOffset End)>();

        void TryAdd(NineProgramme programme, DateTimeOffset start, DateTimeOffset end)
        {
            var s = start < windowStart ? windowStart : start;
            var e = end > windowEnd ? windowEnd : end;
            if (e <= s)
            {
                return;
            }

            foreach (var (cs, ce) in covered)
            {
                if (s >= cs - TimeSpan.FromMinutes(2) && e <= ce + TimeSpan.FromMinutes(2))
                {
                    return;
                }
            }

            covered.Add((s, e));
            blocks.Add(new ProgrammeBlock(programme, s, e));
        }

        foreach (var programme in programmes)
        {
            var start = programme.Start;
            var end = programme.End;

            // FAST mega-listings: end the current block at the observed next-episode boundary.
            if (programme.IsOpenEnded && programme.Secondary is not null && programme.Secondary.Start > start)
            {
                end = programme.Secondary.Start;
            }

            TryAdd(programme, start, end);
        }

        // Fill the immediate future from secondary listings when not already covered.
        foreach (var programme in programmes)
        {
            if (programme.Secondary is null)
            {
                continue;
            }

            var coveredAlready = false;
            foreach (var listing in programmes)
            {
                if (System.Math.Abs((listing.Start - programme.Secondary.Start).TotalSeconds) < 120)
                {
                    coveredAlready = true;
                    break;
                }
            }

            if (!coveredAlready)
            {
                TryAdd(programme.Secondary, programme.Secondary.Start, programme.Secondary.End);
            }
        }

        blocks.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Fill any gap between the last known episode and the end of the window by repeating
        // the last block's show (without episode subtitle) at its own cadence. 24/7 channels
        // keep rolling; this keeps the guide continuous without claiming fake episode titles.
        if (blocks.Count > 0)
        {
            var last = blocks[blocks.Count - 1];
            var duration = last.End - last.Start;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(4))
            {
                duration = TimeSpan.FromMinutes(30);
            }

            var filler = last.Programme with { Subtitle = null };
            var cursor = last.End;
            var guard = 0;
            while (cursor < windowEnd && guard++ < 600)
            {
                var next = cursor + duration;
                if (next > windowEnd)
                {
                    next = windowEnd;
                }

                if (next > cursor)
                {
                    blocks.Add(new ProgrammeBlock(filler, cursor, next));
                }

                cursor = next;
            }
        }

        return blocks;
    }

    private static void WriteImageElement(XmlWriter writer, string type, string url)
    {
        // Jellyfin.XmlTv reads the image path from element text; the url attribute is for DTD compatibility.
        writer.WriteStartElement("image");
        writer.WriteAttributeString("type", type);
        writer.WriteAttributeString("url", url);
        writer.WriteString(url);
        writer.WriteEndElement();
    }

    private static string FormatXmlTvDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    private readonly record struct ProgrammeBlock(NineProgramme Programme, DateTimeOffset Start, DateTimeOffset End);
}
