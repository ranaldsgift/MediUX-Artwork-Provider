# MediUX Artwork Provider Plugin

This is a Jellyfin artwork provider plugin for fanart sets from [MediUX](https://mediux.pro/).

The plugin will automatically download fanart sets from MediUX based on your prioritized author list and your excluded author list. It will prioritize sets from authors at the top of your prioritized list while automatically excluding any sets from authors on your excluded list. If there are no sets from your prioritized authors for a given item, the plugin will prefer sets with the highest completeness and will prefer to download Season Posters and Titlecards from sets with the highest number of these items available.

This plugin also provides an interface to browse fanart sets from within the Jellyfin UI when using the native "Edit Images" functionality.

The plugin will remember which set has been selected (either automatically or manually) for each image type on a per item basis and will always prefer to download corresponding image types from the same set when automatically downloading images.

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Manual Installation](#manual-installation)
- [Build](#build)
- [How set selection works](#how-set-selection-works)
- [Acknowledgments](#acknowledgments)
- [AI Disclaimer](#ai-disclaimer)
- [License](#licence)

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

- Jellyfin **10.10.7** or **10.11.x** or **12**
- MediUX beta API token
- For browsing fanart sets within Jellyfin: [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin by [IAmParadox27](https://github.com/IAmParadox27).

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Manage Repositories (top right corner)**
2. Click **➕** and add the repository:

   `https://raw.githubusercontent.com/ranaldsgift/MediUX-Artwork-Provider/main/manifest.json`

3. Go to the **Catalog** tab, find **MediUX**, and click **Install**
4. Restart your Jellyfin server
5. **Dashboard → Plugins → MediUX** → set your API Key (and other options)
6. For each library: manage library → image fetchers → enable **MediUX**

## Manual Installation

For local development or offline install:

1. Build (see below) and pick `dist/10.10.7` or `dist/10.11`
2. Create `<jellyfin-data>/plugins/MediUX/`
3. Copy **both** `Jellyfin.Plugin.Mediux.dll` and `meta.json` from that dist folder into it
4. Restart Jellyfin

## Build

```bash
dotnet build Jellyfin.Plugin.Mediux/Jellyfin.Plugin.Mediux.csproj -c Release
```

| Jellyfin | Framework | Output |
|----------|-----------|--------|
| 10.10.7 | net8.0 | `dist/10.10.7/` |
| 10.11.x | net9.0 | `dist/10.11/` |

## How set selection works

When Jellyfin refreshes images for an item (or scans a new item without art):

1. **Sticky bindings** — if a set was previously downloaded for a category, prefer that set when it still exists.
2. **Highest priority** — use sets from authors on the prioritized authors list; take the most complete set from the first creator who has usable art.
3. **High priority** — if no list / no match, pick the set that fills the most needed slots (tie-break: item count → popularity → newest).
4. **Regular priority** — fill any remaining slots from other sets using the same completeness/popularity rules.
5. **Alternatives** — other MediUX images for those slots are also listed in the image picker after preferred ones.

Needed slots are based on what exists in your library (seasons/episodes present), so show completeness includes title cards for episodes you actually have.

## Acknowledgments

This plugin is inspired by the [Jellyfin Fanart.TV plugin](https://github.com/jellyfin/jellyfin-plugin-fanart). This was used as the foundation of this plugin, so a big thank you to the authors and [contributors](https://github.com/jellyfin/jellyfin-plugin-fanart/graphs/contributors) to that project.

## AI Disclaimer

This project has been developed with the assistance of AI coding agents. I have reviewed all of the code and tested all of the functionality "thoroughly". If you are concerned about lack of support, please see my other long term ongoing community projects to inspire your confidence.

## Licence

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
