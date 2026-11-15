using System.Net.Http;
using Jellyfin.Plugin.NineNow.Configuration;
using Jellyfin.Plugin.NineNow.Nine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

var factory = new SimpleFactory();
var logger = NullLogger<NineApiClient>.Instance;
var api = new NineApiClient(factory, logger);
var config = new PluginConfiguration();
var ct = new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token;

Console.WriteLine("== channels ==");
var channels = await api.GetChannelsAsync(ct);
Console.WriteLine($"count={channels.Count} market={api.Market?.Name}/{api.Market?.State}");
foreach (var c in channels.Take(3))
{
    Console.WriteLine($"  {c.Kind,-17} {c.Slug,-28} {c.Name} logo={(c.LogoUrl is null ? "NONE" : "ok")}");
}
Console.WriteLine($"  all logos present: {channels.All(c => c.LogoUrl is not null)}");

Console.WriteLine();
Console.WriteLine("== resolve: main channel (channel-9) ==");
var r1 = await api.ResolveStreamUrlAsync("channel-9", config, ct);
Console.WriteLine($"  {r1?.Source} adFree={r1?.AdFree} url={Trunc(r1?.Url)}");

Console.WriteLine();
Console.WriteLine("== resolve: FAST channel (seinfeld, via ssai.postfixParams) ==");
var r2 = await api.ResolveStreamUrlAsync("seinfeld", config, ct);
Console.WriteLine($"  {r2?.Source} adFree={r2?.AdFree} url={Trunc(r2?.Url)}");

Console.WriteLine();
Console.WriteLine("== resolve: another FAST (bbc-news) ==");
var r3 = await api.ResolveStreamUrlAsync("bbc-news", config, ct);
Console.WriteLine($"  {r3?.Source} adFree={r3?.AdFree} url={Trunc(r3?.Url)}");

Console.WriteLine();
Console.WriteLine("== epg: channel-9 ==");
var info = await api.GetLiveInfoAsync("channel-9", ct);
Console.WriteLine($"  programmes={info?.Programmes.Count} promo={(info?.PromoImageUrl is null ? "NONE" : "ok")} now={info?.NowPlaying}");
foreach (var p in info!.Programmes.Take(3))
{
    Console.WriteLine($"  {p.Start:HH:mm}-{p.End:HH:mm} {p.Title} [{p.Genre}] openEnded={p.IsOpenEnded}");
}

Console.WriteLine();
Console.WriteLine("== epg: seinfeld (mega-listing + secondary) ==");
var sf = await api.GetLiveInfoAsync("seinfeld", ct);
var p0 = sf!.Programmes.FirstOrDefault();
Console.WriteLine($"  programmes={sf.Programmes.Count} openEnded={p0?.IsOpenEnded} secondary={p0?.Secondary?.Title} {p0?.Secondary?.Start:u}-{p0?.Secondary?.End:u} sub={p0?.Subtitle}");

Console.WriteLine();
Console.WriteLine("== bulk live info (semaphore=4) ==");
var all = await api.GetAllLiveInfoAsync(channels, ct);
Console.WriteLine($"  fetched {all.Count}/{channels.Count}; with programmes: {all.Values.Count(v => v.Programmes.Count > 0)}");

static string Trunc(string? s) => s is null ? "NULL" : (s.Length > 110 ? s[..110] + "..." : s);

sealed class SimpleFactory : IHttpClientFactory
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    public HttpClient CreateClient(string name) => Client;
}
