# Jellyfin Plugin — Nine Network (9Now)

Adds the Nine Network channels (Channel 9, 9Gem, 9Go!, 9Life, 9Rush + FAST/24×7
channels such as Seinfeld, BBC News, MasterClass, ... ) to Jellyfin's Live TV
UI: channel list with logos, an electronic programme guide, programme artwork,
live playback, and DVR/timers.

Channels, stream URLs, EPG and artwork are all pulled **live from the 9Now
service** — nothing is hardcoded.

Target: **Jellyfin 12.x** (built against `Jellyfin.Controller 12.4.0`, `net10.0`).
Playback requires an **Australian** egress IP (the 9Now API is geo-restricted;
the guide/channels API works from anywhere).

## How it works

The plugin exposes three plain HTTP endpoints on the Jellyfin host and lets
Jellyfin's own built-in Live TV machinery consume them:

| Endpoint | Purpose |
| --- | --- |
| `GET /NineNow/playlist.m3u` | M3U playlist of all channels (with `tvg-id`, `tvg-logo`) |
| `GET /NineNow/epg.xml` | XMLTV guide (titles, episode subtitles, ACB ratings, artwork) |
| `GET /NineNow/stream/{slug}/master.m3u8` | Live HLS stream for a channel |

Clicking **Register 9Now tuner & guide** on the plugin's config page creates
the matching entries through Jellyfin's own `ITunerHostManager` /
`IListingsManager` — an M3U tuner named "9Now" and an XmlTV listings provider —
exactly as if you had added them by hand, then triggers a guide refresh.
The join key between tuner and EPG is the 9Now channel slug (`tvg-id`).

Stream resolution follows the reverse-engineered 9Now web client:
`yo.up` manifest → `yo.eb.fb` (base64) → FAST `video.ssai.postfixParams` →
Brightcove fallback (optional, configure account id + policy key).

## Install

1. Copy `Jellyfin.Plugin.NineNow.dll` (from `dist/` or a local build) into
   Jellyfin's `plugins/ninety-now/` folder and restart the server.
2. In the web UI: **Dashboard → Plugins** — the plugin should be active.
3. Open its **Settings** page. It shows whether the 9Now API is reachable and
   how many channels it sees for your market.
4. Press **Register 9Now tuner & guide in Live TV settings**.
5. Live TV should now show the channels and guide within a minute or two
   (the plugin's hosted service also keeps the catalogue warm, and Jellyfin
   refreshes the guide on its own schedule — keep the refresh interval at
   6–12 h since the 9Now feed only reaches ~12 hours ahead).

Use **Remove 9Now entries** to unregister; it deletes only the entries the
plugin created.

## Options

- **Ad-free** — prefer the clean upstream HLS over the Yospace SSAI ad manifest.
- **Include FAST / 24×7 channels** — toggle the always-on channels.
- **Programme artwork** — use show promo cards as guide cell art / backdrops.
- **Guide lookback/lookahead hours** — XMLTV window.
- **Brightcove account id / policy key** — enables the Brightcove fallback path.

## Build

```sh
cd Jellyfin.Plugin.NineNow
~/.dotnet/dotnet build -c Release      # net10.0, produces the plugin dll
```

Bump `<Version>/<AssemblyVersion>/<FileVersion>` in the `.csproj` when you
change the config page — jellyfin-web HTTP-caches the page by assembly version.

## Repo layout

- `Jellyfin.Plugin.NineNow/` — plugin source (`Nine/` API client, `LiveTv/`
  setup service + interfaces, `Api/NineNowController.cs`, embedded config page).
- `dist/` — packaged dll + `plugin.xml`.
- `tools/container-test/` — curl/API helpers and guide/playback/recording
  probes used against a real Jellyfin 12.4 container.
- `PROPOSAL.md` — design notes: port plan from the reverse-engineered Rust app
  and the phase-2 native `ITunerHost` option.

## Disclaimer

Unofficial. Interacting with the 9Now service this way may violate Nine's
terms of service; ad-free playback and DRM-free HLS are personal-use
only. Australia-only.
