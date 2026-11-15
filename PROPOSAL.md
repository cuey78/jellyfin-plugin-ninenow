# Nine Network (9Now) Live TV plugin for Jellyfin — analysis & implementation options

Date: 2026-11-15 · Jellyfin current stable: **v12.4** (verified against `master`, Oct 2026)
Sources: `/home/cuey/Documents/ninetv/ninetv_rust` (source + `RE_FINDINGS.md`) + live API probes + Jellyfin source inspection.

---

## 1. What the Rust app already gives us

Everything below was re-verified live on 2026-11-15 (marked ✔) or comes from `RE_FINDINGS.md`.

### 1.1 Channel list — fully dynamic, no auth ✔

```
GET https://api.9now.com.au/apps/live-experience?slug=channel-9
→ data.getLXP.switcherRail[] : { slug, name, type: "channel"|"live-event-group", selected }
```

23 entries today (5 simulcast channels + 18 FAST/live-event groups). Same payload also carries:

| Path | Content |
|---|---|
| `getLXP.market` | `{name: "Sydney", state: "nsw", ...}` — geo/market in use |
| `getLXP.stream.video.url` | Yospace SSAI manifest (simulcast channels only) |
| `getLXP.stream.video.ssai.postfixParams` | query string containing `yo.up` / `yo.eb.fb` (also present for FAST channels) |
| `getLXP.stream.video.referenceId` | Brightcove ref (FAST fallback) |
| `getLXP.stream.display.listings[]` | EPG, ~13 consecutive programmes (next ~10–12 h) |
| `getLXP.stream.display.promoImage.sizes.{w320…w1920}` | channel card art ✔ |
| `switcherRail[].switcherLogo.sizes.{w320…w1920}` | **channel logos for every channel** ✔ |

### 1.2 Stream resolution — already solved in Rust (`clean_stream_url`, `resolve_live`, `resolve_brightcove`)

1. Simulcast channels (`channel-9`, `gem`, `go`, `life`, `rush`): `video.url` is a Yospace SSAI master. The clean, **ad-free** origin is embedded in the query string:
   - `yo.up` = upstream directory → `<yo.up>index.m3u8` ✔ (200, `application/x-mpegURL`)
   - `yo.eb.fb` = base64 fallback master ✔ **but for ch9 this now returns HTTP 400** — prefer `yo.up`, fall back to `yo.eb.fb` (the Rust app tries `eb.fb` first; plugin should flip the order or validate).
2. FAST / live-event channels (`seinfeld`, `bbc-news`, …): `video.url` is `null`, **but `video.ssai.postfixParams` now contains a `yo.eb.fb` with the clean `fast-channels-prod.akamaized.net/.../live.m3u8`** ✔ (200, HLS, no DRM). This is a *new* path — the Rust app falls back to Brightcove (account `4460760524001` + policy key) for these; the Brightcove route can return Yospace SSAI with ads, so prefer the postfixParams trick and keep Brightcove as last resort.
3. Main channels report `hasDrm: false`; clean HLS decodes with plain FFmpeg — which is exactly what Jellyfin pipes through FFmpeg. No license handling needed.
4. Start-over: `display.buttons.startOverButton` exists and the web player uses an `offset` query param on the manifest (playback-level; Jellyfin has no catch-up support for M3U-style tuners — see §2.3).
5. Auth is **not required** for any of this (device-code OIDC flow in the Rust app is optional; `token` param is accepted and ignored).

### 1.3 EPG

`display.listings[]` per channel: `title, subtitle, description, genreString, classification, startDate, endDate (ISO-UTC), hasStreamingRights, upNextTitle, secondaryListing`. ~13 programmes ≈ next 10–12 hours only; no further-future paging (`offset` is for start-over, not guide browsing).

Caveat for FAST channels: the listing is a single mega-window (`startDate` 2023 → `endDate` 2032, current episode in `subtitle`) — must be clipped/synthesised (see §4.3).

### 1.4 Programme artwork — not in the live feeds, but obtainable ✔

No per-programme images in `live-experience`. However the 9Now VOD search works and returns posters + show logos:

```
GET https://tv-api.9now.com.au/v2/pages/search?term=<title>&device=web
→ results[].items[] : { name, slug, image.sizes.*, showLogo.sizes.*, genre, classificationRating, ... }
```

Matching listing titles against this gives real programme poster art (phase 2). `promoImage` gives per-channel card art usable immediately as programme backdrop.

---

## 2. Jellyfin integration surface (verified in source, master @ v12.4)

### 2.1 Plugins can supply *native* tuners and EPG providers

- `ApplicationHost.Init()` calls `_pluginManager.RegisterServices(serviceCollection)` **before the DI container is built**; a plugin implementing `IPluginServiceRegistrator` has its services in the root container.
- `TunerHostManager` is constructed with `IEnumerable<ITunerHost>` and `ListingsManager` with `IEnumerable<IListingsProvider>` → a plugin-registered `ITunerHost` appears in the *Tuner type* dropdown and a plugin `IListingsProvider` in the *EPG service* dropdown, as first-class citizens.
- `ITunerHost`, `IListingsProvider`, `ChannelInfo`, `ProgramInfo`, `MediaSourceInfo` are all public plugin-visible types (`MediaBrowser.Controller`, `MediaBrowser.Model`).
- Note: the old Emby-style `IExternalListingsProvider` (with `GetImageAsync`) **no longer exists** in Jellyfin master — custom EPG = implement `IListingsProvider` (4 methods: `GetProgramsAsync`, `Validate`, `GetLineups`, `GetChannels`).
- Auto-configuration is possible: `ITunerHostManager.SaveTunerHost()` and `IListingsManager.SaveProvider()` are injectable, and `SaveTunerHost` queues the guide-refresh task itself. A plugin config page with one "Enable" button can register tuner + EPG without touching `livetv.xml` by hand.

### 2.2 Images are stored from provider metadata (no extra work)

- `GuideManager` sets channel Primary image from `ChannelInfo.ImageUrl` and downloads/caches it (`PreCacheImages`).
- For programmes, `GuideManager.UpdateImages` maps `ProgramInfo.ImageUrl → Primary`, `ThumbImageUrl → Thumb`, `LogoImageUrl → Logo`, `BackdropImageUrl → Backdrop`. The built-in XMLTV provider already feeds `ImageUrl` from `<programme><icon src=…>` — programme artwork in the guide is a supported, live feature.
- M3U tuner parses `tvg-id`, `tvg-name`, `tvg-logo`/`logo`, `group-title`, `tvg-chno`, channel numbers → `ChannelInfo.ImageUrl` etc. Tuner-level User-Agent only; **no `#EXTVLCOPT` support** (grep: zero hits); Akamai origins accept default UA ✔ (probed).

### 2.3 What Jellyfin does *not* give us

- No catch-up/timeshift/`offset` support in the M3U tuner or tuner-host API surface → "Start Over" cannot be expressed natively (best effort: expose a start-at-programme-start media source variant, or skip).
- Guide horizon = whatever the provider returns → ~12 h ahead (vs Schedules Direct's 24 h). Acceptable; refresh every 30–60 min.
- No DVR of live TV to disk… actually recording *does* work: Live TV recording runs through FFmpeg remux of the HLS source (`EncodedRecorder`), which the clean HLS feeds happily. Recordings are a freebie.

---

## 3. Implementation options

### Option A — Native plugin: custom `ITunerHost` + `IListingsProvider` (recommended)

Pure C# plugin (`Jellyfin.Plugin.NineNow`). Port the Rust logic (~350 lines) to an `INineApi` service.

- `NineTunerHost : ITunerHost` (`Type="ninetv"`): `GetChannels` from `switcherRail` (names, numbers, `switcherLogo` → `ChannelInfo.ImageUrl`, `ExternalId`/`TunerChannelId` = slug); `GetChannelStreamMediaSources` resolves clean HLS (yo.up → yo.eb.fb → Brightcove fallback); `GetChannelStream` returns `new SharedHttpStream(...)` (public class, ctor args all DI-available) or derives `BaseTunerHost` (public abstract) to get caching for free.
- `NineListingsProvider : IListingsProvider` (`Type="ninetv"`): `GetChannels` (ExternalId = slug) + `GetProgramsAsync` from `display.listings`, deterministic `ProgramInfo.Id`s; sets `ImageUrl` from search-match (phase 2) / `promoImage` (phase 1 backdrop).
- Plugin config page: market display, ad-free toggle, channel enable/disable + numbering, "Register in Live TV settings" (calls `SaveTunerHost`/`SaveProvider`, queues guide refresh).
- Background `ScheduledTask` (15–30 min) refetches rail + listings; per-channel resolve cache TTL ~2 min.

**Pros:** fully native UX (tuner type "9Now" in UI, auto channel numbers, logos, guide, recordings); single deployable; no self-URL plumbing; matches how the guide image pipeline already works.
**Cons:** rides semi-public interfaces (`ITunerHost`/`IListingsProvider` are real but barely documented for plugins — small breakage risk across majors; note `IExternalListingsProvider` already died once); more code than Option B.

### Option B — Thin plugin serving M3U + XMLTV to the *built-in* tuner & EPG provider

Plugin exposes ASP.NET Core endpoints (plugin controllers are normal API controllers):

- `GET /NineNow/playlist.m3u` — dynamic M3U: `#EXTINF:-1 tvg-id="slug" tvg-name="Channel 9" tvg-logo="<switcherLogo w768>" group-title="9Now" tvg-chno="1"` + `http://<server>/NineNow/stream/{slug}/master.m3u8`
- `GET /NineNow/stream/{slug}/...` — 302 → freshly resolved clean HLS (Akamai URLs are static directory paths, so redirects are cache-proof)
- `GET /NineNow/epg.xml` — XMLTV: `<channel><icon src=switcherLogo>` + `<programme><icon>` (feeds `ProgramInfo.ImageUrl` → guide art for free); clip FAST mega-listings.

Setup = add tuner type **M3U tuner** with the playlist URL + EPG **xmltv** with the epg URL (the plugin can auto-register these too via `ITunerHostManager`/`IListingsManager`).

**Pros:** smallest surface (only public, stable endpoints: M3U tuner + XMLTV format); least code (~500–700 lines total); upgrade-robust; logos & programme art work via stock parsers.
**Cons:** two config entries (unless auto-registered); channel IDs are MD5-of-URL (churn if stream URLs change — keep the `/stream/{slug}` URL stable to avoid re-mapping); one extra redirect hop at stream open; relies on guide-refresh to re-sync channel list.

### Option B2 — No C# at all: extend the Rust app with a small axum server

Same endpoints as B, implemented in the existing Rust codebase (reqwest/serde logic already exists); Jellyfin configured against it manually.
**Pros:** reuses everything you built; fastest demo. **Cons:** extra daemon to run/version on the server; no plugin config UI, no auto-register, packaging outside Jellyfin; artwork/search logic duplicated in Rust.

### Option C — Emulate an HDHomeRun (`lineup.json` + UDP/HTTP tuner)

Not needed: A and B are strictly better for Jellyfin (HDHR emulation buys cross-app compatibility, but forces fake device discovery and still needs an EPG provider). Mentioned for completeness only.

### Recommendation

**Option A**, implemented in two phases:

1. **Phase 1 (MVP, ~1–2 evenings):** Option B's endpoints *inside the plugin* + auto-registration, giving working channels/logos/EPG in Jellyfin with near-zero risk. This is ~60 % of Option A's code anyway (the Nine API client is shared).
2. **Phase 2:** add `ITunerHost` + `IListingsProvider` for the fully native tuner type; keep M3U/XMLTV endpoints as a documented fallback.
3. **Phase 3:** programme posters via `tv-api` search matching (cache title→poster, only accept confident matches), channel backdrop from `promoImage`.

---

## 4. Design details to carry into the implementation

### 4.1 Resolve order (per stream open / media source request)
```
live-experience?slug=S
  video.url has yo.up / yo.eb.fb   → prefer yo.up + "index.m3u8"; validate 200; fallback yo.eb.fb
  video.ssai.postfixParams         → decode yo.eb.fb (base64) → clean FAST HLS        ← new, not in Rust app
  video.referenceId                → Brightcove playback API (may carry ads/DRM; last resort)
```
Cache resolutions ~2 min; treat `yo.up`/`fast-channels` URLs as non-expiring. Flip the Rust app's `eb.fb`-first order (ch9 `eb.fb` = HTTP 400 on 2026-11-15).

### 4.2 EPG refresh
One `live-experience` call per slug gives channel + listings + logos: 23 requests per sweep. Scheduled task every 15–30 min; guide horizon ~12 h → document that the guide tab past tonight is empty.

### 4.3 FAST mega-listings
Clip the 2023→2032 window to the guide window and stamp `subtitle`/`upNextTitle` from the current response. Phase 2 refinement: record when `subtitle` changes between polls to learn episode boundaries and split listings progressively.

### 4.4 Matching & IDs
Use `slug` as the single join key: tuner `ChannelInfo.TunerChannelId` = listings `ChannelInfo.ExternalId` = `slug` → Jellyfin maps channels↔EPG automatically (manual mapping UI remains as escape hatch).

### 4.5 Risks / notes
- **API drift**: endpoints are what the 5.4.35 APK uses; they've been stable long enough to survive the app rebrand, but have no compatibility promise. Options B/B2 are the least brittle.
- **Geo**: AU egress required for playback (API answers regardless). Document; optionally surface `getLXP.market`.
- **Ads/legal**: clean origins bypass SSAI — same posture as the existing desktop app; personal use, may violate 9Now ToS. Brightcove path may return DRM (`key_systems`) → flag those channels as unplayable rather than fail.
- **Concurrency**: Nine tolerates the web app's polling; keep refresh polite, resolve lazily per stream. No per-channel rate limits observed.
- **Jellyfin majors**: `ITunerHost`/`IListingsProvider` changed before (`IExternalListingsProvider` removed). Target ≥ v12 SDK; keep Phase-1 endpoints as the stable fallback.

## 5. Suggested repo layout (Option A+B combined)

```
jellyfin-plugin/
  Jellyfin.Plugin.NineNow/
    Plugin.cs                       (IPluginServiceRegistrator, config page, auto-register)
    NineApi/  (INineApi, Models, CleanStreamUrl, Brightcove)   ← direct port of main.rs
    Tuner/    NineTunerHost.cs (ITunerHost / BaseTunerHost)
    Listings/ NineListingsProvider.cs (IListingsProvider)
    Api/      NineNowController.cs (playlist.m3u, epg.xml, /stream/{slug})
    Tasks/    RefreshNineNowGuideTask.cs
    Configuration/ (plugin config + options)
  build.yml, manifest.json, docs/
```
