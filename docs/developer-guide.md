# GDCompanion developer guide

See the [README](../README.md) for build prerequisites and licensing, and the
[user guide](user-guide.md) for current feature boundaries and save precautions.

## Project layout

- `GrimDawnCompanion.App`: WPF interface, navigation, and desktop radar fallback.
- `GrimDawnCompanion.Core`: archive parsing, local catalogs, compatibility checks,
  bridge injection, and the named-pipe client.
- `GrimDawnBridge`: x64 native bridge with bounded game-thread requests and
  capability-scoped calls.
- `GrimDawnCompanion.CatalogBuilder`: optional local catalog inspection tool.
- `GrimDawnCompanion.Tests`: managed integration and regression checks. Native
  transaction/behavior tests are built from `GrimDawnBridge`.

The bridge loads only when the user connects. It has no network access, stealth,
persistence, multiplayer support, or anti-cheat bypass. Export verification and
feature-specific executable guards must remain intact; an unresolved or unsupported
operation must not guess addresses or blindly retry writes.

## Building and testing

From the source root in PowerShell:

```powershell
.\scripts\build.ps1
```

The script uses Visual Studio's x64 C++ environment, CMake/Ninja, and the .NET 8
SDK to build the bridge and app, run native/managed checks, and package releases.
It prefers the local `.tools/dotnet-sdk` if present, otherwise the installed SDK.
The full suite requires a local Steam installation with the tested expansions;
installed-data checks generate their own isolated catalog under ignored `tmp/`.

`-SkipTests` compiles/packages without those checks and is not a validated release.
Automated fake-runtime tests are not equivalent to live mutation/save/reload tests.
Do not use a real character for mutation testing without explicit authorization
and a separate restorable backup.

The `--smoke-test-window` app argument tests cold WPF startup with an isolated
temporary profile, without connecting to the game. It produces `startup-result.json`
and leaves the temporary profile local; it is not manual visual validation.

Menu description expectations live in
`src/GrimDawnCompanion.Tests/Fixtures/approved-menu-wording.md`. Keep this fixture
in source distributions. The tests do not depend on historical wording proposals
or private development notes.

The app icon/title assets are supplied. Regeneration with `scripts/build_icon.py`
additionally requires Python 3 and Pillow, not needed for an ordinary build.

## Catalogs and patches

No generated game catalog belongs in the repository or release. First launch
uses the installed game's ArchiveTool and readable archives to build a local
catalog; later launches verify/reuse the cache. Force Refresh Data rebuilds it.
Installed archives are read-only. Unknown builds or unavailable exports disable
connection or the affected capability rather than weakening compatibility guards.

Keep extracted archives, DBR records, generated catalogs, saves, test profiles,
research, logs, signing credentials, and toolchains local. Ignore rules are not
a complete privacy audit: inspect the files before publishing.

## Release packaging

`scripts/source-manifest.txt` is an explicit list of source-release files. Add
new source, test fixtures, build inputs, dependencies, and required notices there.
`scripts/package-source.ps1` validates the manifest and rejects generated game data,
saves, binaries, symbols, credentials, and linked source paths requiring review.

The only public `docs/` files are this guide and `user-guide.md`. Both are included
in the source ZIP and Windows ZIP so the README's local links work in either.
Historical validation records, drafts, and maintainer setup notes may remain in a
developer's local `docs/` folder, but are ignored by Git and excluded from packaging
and Git source exports. They are not build inputs.

The build creates `GDCompanion-v<version>-win-x64.zip` and the matching
`GDCompanion-v<version>-source.zip` under `build/`. Publish both together with
equally accessible download links. Packaging-only documentation changes must also
reach the regenerated archives; old ZIPs do not update automatically.

Release checks require the GPL license, copyright notice, third-party notices,
MinHook license, and font license. Managed build paths are normalized and debug
symbols omitted. Keep all dependency notices and corresponding build inputs.
Code-signing credentials must never enter the source or ZIPs; current releases
are unsigned, and author/company metadata is not a trusted digital signature.

## Funding links

Optional donations go to [BunniBranch on Ko-fi](https://ko-fi.com/bunnibranch).
The README and user guide contain the support link, and `.github/FUNDING.yml`
configures the repository's funding option with `ko_fi: bunnibranch`.
Donations do not change the license or unlock app features.
