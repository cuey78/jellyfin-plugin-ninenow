using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.NineNow.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether to rewrite Yospace SSAI URLs to the
    /// clean (ad-free) upstream HLS origins when available.
    /// </summary>
    public bool AdFree { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to include FAST / 24x7 live-event channels.
    /// </summary>
    public bool IncludeFastChannels { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether FAST channels expose their show card
    /// (promoImage) as programme artwork in the guide.
    /// </summary>
    public bool ProgrammeArtwork { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether shows without a promoImage get their
    /// card image looked up from the 9Now search API by title.
    /// </summary>
    public bool ShowPosterMatching { get; set; } = true;

    /// <summary>
    /// Gets or sets the Brightcove account id used as a last-resort stream fallback.
    /// Leave empty to disable the Brightcove fallback.
    /// </summary>
    public string BrightcoveAccountId { get; set; } = "4460760524001";

    /// <summary>
    /// Gets or sets the Brightcove policy key used as a last-resort stream fallback.
    /// </summary>
    public string BrightcovePolicyKey { get; set; } =
        "BCpkADawqM1TWX5yhWjKdzhXnHCmGvnaozGSDICiEFNRv0fs12m6WA2hLxMHM8TGAEM6pv7lhJsdNhiQi76p4IcsT_jmXdtEU-wnfXhOBTx-cGR7guCqVwjyFAtQa75PFF-TmWESuiYaNTzg";

    /// <summary>
    /// Gets or sets how many hours into the past the generated guide reaches.
    /// </summary>
    public int GuideLookbackHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets how many hours into the future the generated guide reaches.
    /// The 9Now guide feed only covers roughly the next 12 hours.
    /// </summary>
    public int GuideLookaheadHours { get; set; } = 12;
}
