using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.NineNow.LiveTv;

/// <summary>
/// Registers/removes the 9Now entries in Jellyfin's Live TV configuration.
/// </summary>
public interface ILiveTvSetupService
{
    /// <summary>
    /// Creates or updates the M3U tuner host and XmlTV listing provider pointing at this plugin.
    /// </summary>
    /// <param name="baseUrl">The server base URL reachable by the server itself.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registration result.</returns>
    Task<SetupResult> RegisterAsync(string baseUrl, CancellationToken cancellationToken);

    /// <summary>Removes all 9Now tuner and EPG provider entries created by this plugin.</summary>
    void Unregister();
}
