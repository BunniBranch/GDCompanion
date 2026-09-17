# GDCompanion

A Windows companion for Grim Dawn, made for single-player tinkering: trying out
gear, rebuilding a character, and finding your way around Cairn.

GDCompanion brings an item browser, quest and POI radar, saved position bookmarks,
and character tools into one desktop app. It builds its catalogs from your own
installed game, so the available items and data come from the content you have.
No game files or extracted catalogs are included in the download.

This is an unofficial community project by BunniBranch. It is not affiliated
with or endorsed by Crate Entertainment.

## What you can do

- **Browse and spawn items.** Search by name, category, classification, source,
  or record path. Compatible equipment supports prefix and suffix selection,
  with readable affix names and bonus summaries.
- **Keep track of quests and places.** The radar shows active quest targets and
  points of interest around the minimap, with hover labels, POI filters, and
  directional markers for distant targets in the current connected map region.
  Supported game builds use an in-game renderer, including in Fullscreen.
- **Save places to return to.** Position bookmarks stay available across app
  restarts and characters, with location labels and optional hotkeys. Returns
  require a compatible campaign world and installed map data.
- **Rework a character.** Respec Character inspects your build before offering
  a confirmed reset of skills, masteries, and devotions. Character Resources
  lets you set money and unspent point balances within checked limits, or raise
  your level up to the loaded campaign's cap.
- **Use optional gameplay assists.** Invincibility, unlimited available energy,
  faster movement, and instant player skill cooldowns can be toggled for your
  local session. They are not automatically restored on the next session.
- **Find blueprints and inspect quest tokens.** Browse installed blueprint
  records to spawn and learn them. The token inspector checks the loaded
  character and asks for confirmation before granting or removing a token.

## Before you change a character

Keep a separate, restorable backup of your saves. Steam Cloud is not a backup.
Even though the app works through the running game rather than editing save
files directly, Grim Dawn can autosave changes. Level changes may also trigger
achievements.

GDCompanion is intended for **local, single-player play only**. Compatibility
checks and confirmations help catch unsupported situations, but they cannot
promise that every build, mod, or quest state is safe. Try character-changing
features on a disposable character first.

## Requirements

- A 64-bit Windows PC.
- An installed copy of the **Steam x64 edition of Grim Dawn**. Other editions
  are not currently supported.
- The **.NET 8 Desktop Runtime (x64)** to run the app. The SDK is only needed
  if you want to build it yourself.
- The game's `ArchiveTool.exe`, readable game archives, and permission to write
  to your local application-data folder for catalog generation.

Installed expansions are indexed along with the base game. Some features need
a verified game build and may be unavailable after a patch until compatibility
is confirmed. Do not assume every mod or custom campaign is supported.

The external radar fallback needs **Borderless Windowed** mode for reliable
display; the supported in-game renderer also works in Fullscreen.

## Getting started

1. Download the Windows ZIP from this repository's **Releases** section, when
   available, and extract the whole archive into a folder. Keep the supplied
   files together.
2. Install the .NET 8 Desktop Runtime (x64) if it is not already installed.
3. Start Grim Dawn using its 64-bit option and load a single-player character.
4. Run `GDCompanion.exe`. The first launch builds a local catalog and can take
   several minutes. Later launches reuse and check the cache.
5. Wait for compatibility and catalog status to show **Ready**, then choose
   **Connect to Game**. Use Item Catalog, Navigation, or Character Utilities
   depending on what you want to do.

If the game folder is not detected, select it in **Settings**. If indexing fails,
check the installation and file permissions, then use **Force Refresh Data**.
There is no bundled fallback catalog.

Releases are currently unsigned. The app loads a local DLL bridge into the game
when you connect, which may prompt antivirus warnings. Matching source is
provided for inspection; do not disable your security software just to run it.
The bridge has no network access or anti-cheat bypass behavior.

Choose **Disconnect** when you are finished. It stops active assists and radar
and requests that the bridge unload without closing the game. The
**Automatically Unload Bridge When Closing** setting is also enabled by default.

## Local data and privacy

Settings, compatibility information, generated catalogs, and saved bookmarks
are stored under `%LOCALAPPDATA%\GrimDawnCompanion`. The folder keeps its original
name even though the app is now called GDCompanion. Bookmark data is local and
is not synced by Steam Cloud; back it up separately if you want to keep it.

Catalog generation reads the installed game archives without modifying them.
The app does not distribute or grant rights to the game's data, and generated
catalogs should not be shared with the repository or in release packages.

Do not delete the local data folder as a first troubleshooting step: it also
contains your settings and bookmarks. Use **Force Refresh Data** to rebuild
catalog data instead.

## Current limits

- Bookmark returns support outdoor campaign areas, not dungeons or custom/challenge
  maps. Travel to unloaded numbered surface regions is implemented, but broader
  live validation is still pending. Once native travel starts, disconnecting or
  a timeout does not cancel it; check the game before trying again.
- Radar targets belong to the current connected map region. Possible spawn
  locations are not a guarantee that an enemy is there, and some scripted quest
  placements can only be resolved from live game data.
- Equipment-affix spawning and above-cap resource save/reload behavior still
  need live validation. Natural Cap Bypass is off by default; above-cap values
  may not survive saves or work with every mod.

The [user guide](docs/user-guide.md) explains feature behavior, safeguards,
and troubleshooting in more detail.

## Building from source

You will need the **.NET 8 SDK**, Visual Studio with **Desktop development with
C++**, MSVC x64 tools, the Windows SDK, CMake, and Ninja.

From the project folder, run:

```powershell
.\scripts\build.ps1
```

The full test suite requires a local Steam game installation with the tested
expansions. To compile and package without those installed-game checks, run:

```powershell
.\scripts\build.ps1 -SkipTests
```

Skipping tests does not make a build a validated release. The build produces
both a Windows ZIP and its matching source ZIP; publish them together with
equally accessible downloads. New source files must be added to
`scripts/source-manifest.txt`.

Generated catalogs, extracted game data, and character saves should stay local
and must not be uploaded. See the [developer guide](docs/developer-guide.md)
for architecture, testing, and packaging details.

## Feedback

Bug reports and suggestions are welcome in this repository's **Issues** section.
Include the Companion version, game version, installed expansions or mods, what
you expected, and what happened instead. Remove personal paths or other private
details from logs, and do not attach saves or extracted game data.

For compatibility problems, include the status message and whether the issue
started after a game update. If connection is blocked, do not bypass the check
or copy bridge files from a different release. Use the complete matching package.
For a partial or unverified character change, do not repeatedly retry it; stop
using that feature and include the reported outcome in your bug report.

Contributions are welcome. Keep changes focused, describe how you tested them,
and retain the relevant license and copyright notices. Discuss larger changes
in an issue before spending a lot of time on an implementation.

## Support the project

GDCompanion is free to use. If it has been useful to you and you would like to
support its development, you can [buy me a coffee on Ko-fi](https://ko-fi.com/bunnibranch).

No pressure—donations are optional and no features are locked behind them.
Reporting bugs, sharing feedback, and contributing improvements are helpful too.

## Licensing and redistribution

Copyright (C) 2026 BunniBranch and contributors.

Except where a file or component has its own notice, GDCompanion's original
source code, documentation, and artwork are offered under the
[GNU General Public License, version 3 only](LICENSE) (`GPL-3.0-only`), to the
extent copyright and licensable rights exist. This is version 3 only, not
"version 3 or any later version."

You may use, modify, and redistribute the software, including commercially,
under the license's conditions. When sharing copies or modified versions,
retain the required notices and license, identify your changes as required,
and meet the applicable GPLv3 source-availability obligations. This project's
release workflow is to publish the matching source ZIP alongside each Windows
ZIP with equally accessible download links, not a binary-only release.

The software is provided **without warranty**, including warranties of
merchantability or fitness for a particular purpose. Read the [full license](LICENSE)
for the actual terms; this overview does not replace it. The GNU project's
[licensing FAQ](https://www.gnu.org/licenses/gpl-faq.en.html) provides additional
background.

Third-party components retain their own licenses: MinHook uses the 2-clause BSD
license, its bundled disassembler retains its upstream notices, and the
unmodified Pirata One font uses the SIL Open Font License 1.1. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for credits and license locations.

Grim Dawn, its game content, and third-party mods are **not** relicensed by this
project. This license grants no rights to distribute their assets. The app's
original artwork/icon was generated using OpenAI tools at the maintainer's
direction; no exclusive copyright is asserted over purely AI-generated elements.
See [COPYRIGHT.md](COPYRIGHT.md) for the full project notice.
