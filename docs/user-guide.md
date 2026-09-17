# GDCompanion user guide

See the [README](../README.md) for requirements, first launch, licensing, and
downloads. Use the complete matching app package, and keep separate, restorable
character backups before changing anything. Steam Cloud is not a backup.

## Items and blueprints

Search Item Catalog, select a record, and connect to a loaded single-player
character before spawning. Quantity is limited to 1–100. The game handles delivery;
if inventory space is insufficient, an item may drop nearby.

Compatible equipment exposes prefix and suffix choices from your installed
loot tables. Some special crafted items support affixes too; quest and
non-equipment records do not. Changing the base item clears your selections.
Choose None to leave that affix unchanged from the base item. Affix tiers can
raise equipment requirements. Displayed bonus summaries do not guarantee the
final randomized roll or that your character can equip it.

Items labelled **Internal** may be default appearance pieces or NPC gear, not
ordinary loot. Being listed does not mean they are safe or useful to equip.
**Name unavailable** means localization was missing, not necessarily that the
item is internal. Record paths remain available for identifying variants.

Blueprints has a separate searchable recipe list. Spawn a blueprint, then use
it in the character's inventory to learn it. Equipment stays in Item Catalog.
Affix spawning still needs broader live inventory and save/reload validation.
After an uncertain delivery, do not retry blindly: check the game first.

## Radar

Enable Radar Overlay in Navigation or use the title-bar radar indicator.
Automatic alignment follows the minimap's position, size, zoom, and rotation.
Supported builds use an in-game renderer that works in Fullscreen. The external
fallback needs Borderless Windowed mode for reliable display.

Quest stars represent supported active tracked objectives. Distant bearings
cover the current connected map region, not unrelated maps or dungeons.
Possible spawn areas are not proof that an enemy is present; some custom-script
placements require live game data. Missing or stale information can hide markers.

POI Type Filter controls which Companion markers appear. Preferences persist;
turning off the master POI switch retains your category selections. Filtering
does not hide the game's own icons or change quest tracking. Hover over a marker
to identify it. Radar itself does not complete quests, unlock rifts, or write
quest tokens. Disabling radar or disconnecting clears its markers.

Advanced manual calibration is a fallback, not the recommended mode. Manual
range applies only when automatic alignment is off.

## Position bookmarks

Save a named outdoor campaign location, select it later, and choose Return to
Selected Position. Bookmarks persist across sessions and are shared across
characters. Saved character, difficulty, and hardcore mode are informational;
returns still require compatible installed map data and the same campaign world.
A game/map update may invalidate a destination; revisit it and save a new one.
Dungeons, custom campaigns, and challenge maps are not supported.

Loaded destinations use precise saved coordinates. Unloaded numbered surface
regions use the game's own loading/travel activity and rounded coordinates.
A distant Homestead/Devil's Crossing round trip was live-tested on one
installation; this is not exhaustive coverage of every destination or failure mode.
Normal arrival can trigger exploration, proximity logic, and autosaving.

Travel requires fresh readiness checks and an eligible character outside combat.
Only one return can run at a time, and buttons and hotkeys share a two-second
cooldown. Once native travel starts, a timeout or disconnect does not cancel it.
Check the game before trying again; there is no automatic movement retry.

Assign Ctrl+Alt, optionally Shift, with a letter, digit, or F1–F12 to a selected
bookmark. Duplicate assignments are rejected. Hotkeys work only while connected
Grim Dawn is foreground and the player is ready. Choose shortcuts unused by the
game: Companion does not suppress the game's own key handling. Blocked presses
are discarded rather than queued.

Bookmarks live in `%LOCALAPPDATA%\GrimDawnCompanion\position-bookmarks.json`,
with a `.bak` copy of the previous file. This is not a character-save backup or
a Steam Cloud-synced file. Back up bookmarks separately if you want to keep them.

## Resources and levels

Set changes money or an **unspent** point balance, including zero and reductions.
It does not remove allocated skills, attributes, masteries, or devotion stars.
Allocated points count toward the permitted total. Standard campaign budgets are:

| Highest loaded expansion | Level cap | Total skills | Total attributes | Devotion cap |
|---|---:|---:|---:|---:|
| Base game | 85 | 223 | 90 | 50 |
| Ashes of Malmouth | 100 | 244 | 105 | 55 |
| Forgotten Gods | 100 | 248 | 107 | 55 |
| Fangs of Asterkarn | 100 | 250 | 109 | 55 |

Money is capped at 2,000,000,000 iron bits. Active loaded mod progression takes
priority when supported; merely installed mods are not selected. Custom campaigns
and mods replacing campaign quest-point rewards are not universally supported.
Follow the live limits shown in the app, not the standard table, for modded play.
If devotion accounting disagrees, open Skills/Devotion and refresh; the app does
not silently repair the mismatch.

Natural Cap Bypass is off by default and resets on reconnect. It permits a total
point budget of 10,000 per pool, or a supported active mod's higher normal budget.
It does not change money, level, or individual skill-rank caps. Disabling it does
not remove previously granted points. Above-cap save/mod compatibility is not
guaranteed and save/reload testing remains pending.

Level Character raises levels only, through normal XP/level-up processing, up to
the loaded cap. Remove bonus-XP equipment and buffs first to avoid overshoot.
Achievements may trigger. Filling a point budget does not complete quests or
stop future rewards; normal gameplay may subsequently grant additional points.
Review each confirmation. Partial or unverified changes block further writes;
never treat reconnecting as permission to repeat an uncertain change.

## Respec Character

Before using it, back up the character, remove equipped items granting skills,
and open the game's Skills and Devotion pages once. The character must be alive,
outside combat, in single-player, with the supported skill set and consistent totals.

Respec Character inspects the current build and asks for confirmation. It refunds
devotions, clears their bindings, refunds regular skills and mastery bars, then
clears both masteries. Celestial-power experience is retained. After verified
success, choose new masteries in the game's skill window. Another respec is
allowed after fully verified success, but partial or unknown outcomes block it.
Class-dependent gear, pets, buffs, and hotbar references are not exhaustively tested.

## Gameplay assists and quest tokens

Assist checkboxes toggle invincibility, unlimited available energy, instant player
skill cooldowns, and 100–200% movement speed. Speed changes apply automatically;
attack/casting speeds are unchanged. Reserved energy, casting animations, and other
skill requirements still apply. Some scripted deaths may bypass invincibility.
Assists start off, are not restored automatically across sessions/characters,
and need no activation confirmation. Uncheck them or disconnect to stop them.
If the app reports stopping or an uncertain result, do not assume cleanup finished.
Broader assist edge cases remain unverified.

Quest-Token Inspector can check, grant, or remove a selected token. Changes require
confirmation, but tokens depend on each other: editing one can permanently break
quests or progression. Change only states you understand, with a restorable backup.

## Troubleshooting

If the game is not detected, choose its folder in Settings. Missing ArchiveTool,
unreadable archives, or an unwritable cache folder can prevent first-launch indexing.
Correct the cause and use Force Refresh Data; no fallback catalog is bundled.
Do not delete the whole local data folder: it also contains settings and bookmarks.

After a patch, unavailable functions may disable connection or specific features.
Do not bypass compatibility checks or mix bridge files from different releases.
For partial or unknown character changes, stop using that feature and report the
outcome instead of repeatedly retrying. Include app/game versions, expansions,
mods, status messages, and reproduction steps in an issue. Remove private details
from logs; do not upload character saves or extracted game data.

## Optional support

If you would like to support development, you can
[buy me a coffee on Ko-fi](https://ko-fi.com/bunnibranch). Donations are entirely
optional; every feature is available without donating. Bug reports, feedback,
and contributions are welcome too.
