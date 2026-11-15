using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.NineNow.Configuration;
using Jellyfin.Plugin.NineNow.LiveTv;
using Jellyfin.Plugin.NineNow.Nine;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.NineNow;

/// <summary>
/// Nine Network (9Now) Live TV plugin: exposes the Nine channels as a Jellyfin
/// Live TV service provider with an EPG guide, channel logos and programme art.
/// Phase 1 serves an M3U playlist + XMLTV guide to Jellyfin's built-in providers;
/// a native tuner host follows in phase 2.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Unique id of the built-in M3U tuner type.</summary>
    public const string M3uTunerType = "m3u";

    /// <summary>Unique id of the built-in XMLTV listings provider type.</summary>
    public const string XmlTvProviderType = "xmltv";

    /// <summary> Friendly name we give our tuner/EPG entries, used to recognise them.</summary>
    public const string FriendlyName = "9Now";

    public Plugin(MediaBrowser.Controller.IServerApplicationPaths appPaths, IXmlSerializer xmlSerializer)
        : base(appPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Nine Network (9Now)";

    /// <inheritdoc />
    public override string Description =>
        "Adds the Nine Network channels (9, 9Go!, 9Life, 9Gem, TVH + FAST channels) to Jellyfin Live TV "
        + "with an electronic programme guide, channel logos and artwork. Australian viewers only.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => new Guid("343f098f-4087-425d-a3a2-182e50992673");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}

/// <summary>
/// Registers the plugin's long-lived services into the DI container.
/// Jellyfin instantiates this via Activator.CreateInstance, so it must have a
/// parameterless constructor (the Plugin class itself does not).
/// </summary>
public sealed class NineNowServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, MediaBrowser.Controller.IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<INineApiClient, NineApiClient>();
        serviceCollection.AddSingleton<ILiveTvSetupService, LiveTvSetupService>();
        serviceCollection.AddHostedService<ChannelCatalogueService>();
    }
}

/// <summary>
/// Background service: keeps the Nine channel catalogue warm so the guide and
/// playlist endpoints always respond quickly, and proactively refreshes it.
/// </summary>
public sealed class ChannelCatalogueService : IHostedService
{
    private readonly INineApiClient _api;
    private readonly ILogger<ChannelCatalogueService> _logger;
    private Timer? _timer;

    public ChannelCatalogueService(INineApiClient api, ILogger<ChannelCatalogueService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var channels = _api.GetChannelsAsync(cts.Token).GetAwaiter().GetResult();
            _logger.LogInformation("Nine channel catalogue: {Count} channels", channels.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh Nine channel catalogue");
        }
    }
}
