# Jellyfin MediUX Plugin

Remote image provider that downloads movie and TV artwork from [MediUX](https://mediux.pro/) using their beta GraphQL API (`https://images.mediux.io`).

This plugin follows the same pattern as the [Fanart.tv plugin](https://github.com/jellyfin/jellyfin-plugin-fanart): Jellyfin asks for images during library scans and “Refresh images”, and the plugin returns preferred MediUX artwork. There is **no** background auto-sync (use [AURA](https://github.com/mediux-team/AURA) if you want scheduled set updates).

## Features

- Movie posters and backdrops
- Series posters and backdrops
- Season posters
- Episode title cards
- Logo and optional album art (Box)
- Configurable MediUX API key
- Ordered **Author Priority** (highest complete set wins for preferred images)
- Gap-fill from other complete/popular sets for missing image types
- Sticky set bindings (preferred sets remembered after download)
- Browse By **Fanart Sets** in the image editor (requires File Transformation)

## Requirements

- Jellyfin **10.10.7** (.NET 8) **or** **10.11.x** (.NET 9)
- MediUX beta API token (account settings / Discord — not publicly open yet)
- Titles should have **TMDB** IDs (TVDB is resolved to TMDB via MediUX when needed)
- For Fanart Sets UI: [File Transformation](https://www.iamparadox.dev/jellyfin/plugins/manifest.json) matching your Jellyfin major version (1.2.x-era for 10.10; current release for 10.11)

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **➕** and add the repository:

   `https://raw.githubusercontent.com/ranaldsgift/MediUX-Artwork-Provider/main/manifest.json`

3. Go to the **Catalog** tab, find **MediUX**, and click **Install**
4. Restart your Jellyfin server
5. **Dashboard → Plugins → MediUX** → set your API Key (and other options)
6. For each library: manage library → image fetchers → enable **MediUX**

Jellyfin installs the build that matches your server version (10.10.7 or 10.11).

## Install (manual)

For local development or offline install:

1. Build (see below) and pick `dist/10.10.7` or `dist/10.11`
2. Create `<jellyfin-data>/plugins/MediUX/`
3. Copy **both** `Jellyfin.Plugin.Mediux.dll` and `meta.json` from that dist folder into it
4. Restart Jellyfin and configure as above

Do **not** mix a net9 DLL with a 10.10.7 server (or the reverse). Do not copy Jellyfin/NuGet dependency DLLs into the plugins folder.

## Build

```bash
dotnet build Jellyfin.Plugin.Mediux/Jellyfin.Plugin.Mediux.csproj -c Release
```

| Jellyfin | Framework | Output |
|----------|-----------|--------|
| 10.10.7 | net8.0 | `dist/10.10.7/` |
| 10.11.x | net9.0 | `dist/10.11/` |

## Releasing

Maintainers (after a Release build):

```powershell
.\scripts\pack-release.ps1
```

This zips each `dist/...` folder into `dist/release/`, writes MD5 checksums into `manifest.json`, and prints next steps. Then:

1. Commit and push the updated `manifest.json`
2. Create GitHub Release tag `v1.0.0` (or matching `-Tag`)
3. Upload the two ZIP files from `dist/release/`

The pack script does **not** create the GitHub release or upload assets.

## How set selection works

When Jellyfin refreshes images for an item (or scans a new item without art):

1. **Sticky bindings** — if a set was previously downloaded for a category, prefer that set when it still exists.
2. **Highest priority** — walk the ordered creator list; take the most complete set from the first creator who has usable art.
3. **High priority** — if no list / no match, pick the set that fills the most needed slots (tie-break: item count → popularity → newest).
4. **Regular priority** — fill any remaining slots from other sets using the same completeness/popularity rules.
5. **Alternatives** — other MediUX images for those slots are also listed in the image picker after preferred ones.

Needed slots are based on what exists in your library (seasons/episodes present), so show completeness includes title cards for episodes you actually have.

## Smoke test checklist

- [ ] Plugin loads after restart (Dashboard → Plugins shows MediUX)
- [ ] Saving API key persists after page reload
- [ ] Movie with TMDB id: Refresh images → Primary + Backdrop from preferred creator when available
- [ ] Series: show poster/backdrop applied
- [ ] Season poster applied for a season in library
- [ ] Episode Primary image (title card) applied when MediUX has one
- [ ] With two priority creators, higher creator wins even if lower creator has a larger set
- [ ] Incomplete priority set still used; missing types filled from other sets
- [ ] Empty API key → no MediUX images / no crashes

## Security

Store the MediUX token only in plugin configuration. Do not commit API keys to git. If a key was shared publicly, rotate it.

## Development

```bash
dotnet test
```

`SetSelector` unit tests cover creator priority, completeness ranking, and gap-fill.

## Licence

MIT
