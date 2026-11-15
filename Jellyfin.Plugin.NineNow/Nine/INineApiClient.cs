using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NineNow.Configuration;

namespace Jellyfin.Plugin.NineNow.Nine;

/// <summary>
/// 9Now live-experience API surface used by the plugin.
/// </summary>
public interface INineApiClient
{
    /// <summary>
    /// Gets the market (region) the 9Now API reports for this server's egress IP.
    /// </summary>
    NineMarket? Market { get; }

    /// <summary>Gets the live channel list from the switcher rail.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The channels, or an empty list on failure.</returns>
    Task<IReadOnlyList<NineChannel>> GetChannelsAsync(CancellationToken cancellationToken);

    /// <summary>Gets the live info (stream candidates + listings) for one channel slug.</summary>
    /// <param name="slug">The channel slug.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The live info, or null on failure.</returns>
    Task<NineLiveInfo?> GetLiveInfoAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Gets the live info for every channel, fetching with bounded parallelism.</summary>
    /// <param name="channels">The channels.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Slug to live info map; failed channels are omitted.</returns>
    Task<Dictionary<string, NineLiveInfo>> GetAllLiveInfoAsync(
        IReadOnlyList<NineChannel> channels,
        CancellationToken cancellationToken);

    /// <summary>Resolves a playable stream URL for a channel.</summary>
    /// <param name="slug">The channel slug.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolved stream, or null when nothing playable was found.</returns>
    Task<ResolvedStream?> ResolveStreamUrlAsync(
        string slug,
        PluginConfiguration config,
        CancellationToken cancellationToken);
}
