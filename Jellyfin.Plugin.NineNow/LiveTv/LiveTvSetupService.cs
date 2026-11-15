using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NineNow.LiveTv;

/// <summary>
/// Registers/removes the 9Now entries in Jellyfin's Live TV configuration using the
/// server's own tuner-host and listings-manager services.
/// </summary>
public sealed class LiveTvSetupService : ILiveTvSetupService
{
    /// <summary>Friendly name used for the created tuner host.</summary>
    public const string TunerFriendlyName = "9Now";

    private const string PlaylistMarker = "/NineNow/playlist.m3u";
    private const string EpgMarker = "/NineNow/epg.xml";

    private readonly ITunerHostManager _tunerHostManager;
    private readonly IListingsManager _listingsManager;
    private readonly IConfigurationManager _config;
    private readonly ILogger<LiveTvSetupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvSetupService"/> class.
    /// </summary>
    /// <param name="tunerHostManager">Instance of the <see cref="ITunerHostManager"/> interface.</param>
    /// <param name="listingsManager">Instance of the <see cref="IListingsManager"/> interface.</param>
    /// <param name="config">Instance of the <see cref="IConfigurationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TCategoryName}"/> interface.</param>
    public LiveTvSetupService(
        ITunerHostManager tunerHostManager,
        IListingsManager listingsManager,
        IConfigurationManager config,
        ILogger<LiveTvSetupService> logger)
    {
        _tunerHostManager = tunerHostManager;
        _listingsManager = listingsManager;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Creates or updates the M3U tuner host and XmlTV listing provider pointing at this plugin.
    /// </summary>
    /// <param name="baseUrl">The server base URL reachable by the server itself.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registration result.</returns>
    public async Task<SetupResult> RegisterAsync(string baseUrl, CancellationToken cancellationToken)
    {
        baseUrl = baseUrl.TrimEnd('/');
        var playlistUrl = baseUrl + PlaylistMarker;
        var epgUrl = baseUrl + EpgMarker;

        var livetv = (LiveTvOptions)_config.GetConfiguration("livetv");

        // Remove stale 9Now tuner entries pointing at a different base URL.
        foreach (var stale in livetv.TunerHosts
                     .Where(t => string.Equals(t.FriendlyName, TunerFriendlyName, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(t.Url, playlistUrl, StringComparison.Ordinal))
                     .ToArray())
        {
            _tunerHostManager.DeleteTunerHost(stale.Id);
        }

        var tuner = livetv.TunerHosts.FirstOrDefault(t =>
            string.Equals(t.Type, "m3u", StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.Url, playlistUrl, StringComparison.Ordinal));

        if (tuner is null)
        {
            tuner = new TunerHostInfo
            {
                Type = "m3u",
                Url = playlistUrl,
                FriendlyName = TunerFriendlyName,
                AllowHWTranscoding = true,
                AllowStreamSharing = true
            };
        }

        tuner = await _tunerHostManager.SaveTunerHost(tuner).ConfigureAwait(false);
        _logger.LogInformation("9Now tuner host registered: {Url}", playlistUrl);

        foreach (var stale in livetv.ListingProviders
                     .Where(p => string.Equals(p.Type, "xmltv", StringComparison.OrdinalIgnoreCase)
                                 && p.Path is not null
                                 && p.Path.Contains(EpgMarker, StringComparison.Ordinal)
                                 && !string.Equals(p.Path, epgUrl, StringComparison.Ordinal))
                     .ToArray())
        {
            _listingsManager.DeleteListingsProvider(stale.Id);
        }

        var epg = livetv.ListingProviders.FirstOrDefault(p =>
            string.Equals(p.Type, "xmltv", StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Path, epgUrl, StringComparison.Ordinal));

        if (epg is null)
        {
            epg = new ListingsProviderInfo
            {
                Type = "xmltv",
                Path = epgUrl
            };
        }

        epg = await _listingsManager.SaveListingProvider(epg, false, true).ConfigureAwait(false);
        _logger.LogInformation("9Now XMLTV provider registered: {Url}", epgUrl);

        // Pull the guide into Jellyfin.
        cancellationToken.ThrowIfCancellationRequested();

        return new SetupResult(tuner.Id, epg.Id, playlistUrl, epgUrl);
    }

    /// <summary>
    /// Removes all 9Now tuner and EPG provider entries created by this plugin.
    /// </summary>
    public void Unregister()
    {
        var livetv = (LiveTvOptions)_config.GetConfiguration("livetv");

        foreach (var tuner in livetv.TunerHosts
                     .Where(t => (t.Url is not null && t.Url.Contains(PlaylistMarker, StringComparison.Ordinal))
                                 || string.Equals(t.FriendlyName, TunerFriendlyName, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            _tunerHostManager.DeleteTunerHost(tuner.Id);
        }

        foreach (var epg in livetv.ListingProviders
                     .Where(p => p.Path is not null && p.Path.Contains(EpgMarker, StringComparison.Ordinal))
                     .ToArray())
        {
            _listingsManager.DeleteListingsProvider(epg.Id);
        }

        _logger.LogInformation("9Now Live TV registrations removed");
    }
}

/// <summary>Result of registration.</summary>
/// <param name="TunerHostId">The created tuner host id.</param>
/// <param name="ListingsProviderId">The created listings provider id.</param>
/// <param name="PlaylistUrl">The playlist URL.</param>
/// <param name="EpgUrl">The XMLTV URL.</param>
public sealed record SetupResult(string TunerHostId, string ListingsProviderId, string PlaylistUrl, string EpgUrl);
