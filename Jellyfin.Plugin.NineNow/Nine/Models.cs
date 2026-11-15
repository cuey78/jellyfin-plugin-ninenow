using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.NineNow.Nine;

/// <summary>A channel from the 9Now live switcher rail.</summary>
public sealed record NineChannel(
    string Slug,
    string Name,
    string Kind,
    string? LogoUrl);

/// <summary>A programme listing from the 9Now guide feed.</summary>
public sealed record NineProgramme(
    string Title,
    string? Subtitle,
    string? Description,
    string? Genre,
    string? Classification,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? UpNextTitle,
    NineProgramme? Secondary)
{
    /// <summary>Mega-listings used by FAST channels span years; clip to the guide window.</summary>
    public bool IsOpenEnded => End - Start > TimeSpan.FromHours(30);
}

/// <summary>Everything the per-slug live-experience response carries.</summary>
public sealed record NineLiveInfo(
    string Slug,
    string? RawVideoUrl,
    string? SsaiPostfixParams,
    string? ReferenceId,
    bool HasDrm,
    string? NowPlaying,
    string? PromoImageUrl,
    IReadOnlyList<NineProgramme> Programmes);

/// <summary>A resolved, playable stream URL.</summary>
public sealed record ResolvedStream(string Url, string Source, bool AdFree);

/// <summary>Market detected for this server's egress.</summary>
public sealed record NineMarket(string? Name, string? State);
