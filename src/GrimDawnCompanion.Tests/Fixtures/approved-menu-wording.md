# Approved menu wording regression fixture

Expected description text from 2.3.84, including the 2.3.85 resource warning.
This is test input, not a pending wording proposal. Keep exact strings in sync
with intentionally approved UI changes; do not update them merely to hide a failure.

| ID | Location | Expected wording |
|---|---|---|
| I1 | Page description | Search installed items and deliver your selection to the loaded character. |
| I2 | Delivery note | Items are delivered to the loaded character. Single-player only. |
| I3 | Affix help | Choose compatible prefixes and suffixes from the installed game data. Higher-tier affixes may increase item requirements. Select None to leave that affix unchanged from the base item. |
| I4 | No compatible affixes | No compatible prefix or suffix options were found for this item. |
| N1 | Page description | Track quest objectives and points of interest, or return to saved locations. |
| N2 | Radar description | Show quest objectives and points of interest on a click-through overlay aligned with Grim Dawn's minimap. |
| N3 | Automatic alignment | Automatically matches the minimap's position, size, zoom and rotation. No resolution preset is needed. |
| N4 | Manual fallback | Turn off automatic alignment to adjust these saved manual settings. |
| N5 | Range explanation | Automatic mode follows the game's minimap zoom and rotation. This range applies only to manual mode. |
| N6 | Right-card heading | Fullscreen-Compatible Overlay |
| N7 | Right-card description | Automatic mode draws markers and hover labels within Grim Dawn's interface. Alignment follows the game's resolution, interface scale and minimap each frame. The overlay hides when a supported minimap is unavailable. Manual mode use a separate overlay window. No game settings or save data are changed. |
| B1 | Save description | Save a named location for use across characters, difficulties and hardcore modes when the campaign map data matches. |
| B2 | Persistence | Bookmarks are stored locally and remain available after restarting Companion or Grim Dawn. |
| B3 | Compatibility note | Saved difficulty is for reference only. Any character can use a bookmark when the map data is compatible. |
| B4 | Hotkey instructions | Assign Ctrl+Alt, optionally with Shift, plus a letter, number or F1–F12. Choose a shortcut that Grim Dawn does not use; Hotkeys work only while Grim Dawn has focus and the loaded player is ready. |
| B5 | Additional hotkey safety note | Only one bookmark return can run at a time. Returns have a two-second cooldown. |
| A1 | Overview | Assists start off and are not carried to another character or session. |
| A2 | Invincibility | Uses the game's player-invincibility check. Some scripted deaths may still occur. |
| A3 | Unlimited Energy | Maintains available energy and bypasses normal energy use. Energy reserved by buffs remains reserved, and skills must fit within the available pool. |
| A4 | Instant Skill Cooldown | Clears player skill cooldowns. Casting animations and other skill requirements still apply. |
| A5 | Movement Speed tooltip | Changes apply automatically while Movement Speed is enabled. Attack and casting speeds are unchanged. |
| A6 | Stop behavior | Uncheck an assist to turn it off. Stopping restores normal behavior. |
| R1 | Level description | Raise the loaded character to the active game or mod's level cap using normal level-up rewards. Lowering levels is not supported. Remove bonus-XP equipment, and back up the character before continuing. |
| R2 | Level inspection status | Companion checks the loaded character before asking you to confirm. No changes are made until you confirm. |
| R3 | Resource description | Set money or an unspent point balance. Active mod rules take priority; otherwise, installed game and DLC rules apply. Allocated points count toward the full endgame limit and are not changed. |
| R4 | Bypass description | Optional and session-only. Allows up to 10,000 total points per pool, or the active mod's higher limit. Money, character-level and individual skill-rank limits are unchanged. Though hard safety caps are in place, above-cap save and mod compatibility is not guaranteed. Use with caution. |
| M1 | Overview | Respec Character checks the loaded character and prepares a refund for your confirmation. After a verified respec, choose new masteries in the game's skill window. |
| M2 | Initial status | No character checked yet. Select Respec Character to inspect the loaded character and review the confirmation. |
| M3 | Checklist heading | Before you respec |
| M4 | Backup bullet | Back up your character. Keep a separate, restorable copy; cloud saving is not a backup. |
| M5 | Equipment bullet | Remove any equipped items that grant skills. |
| M6 | Skills-page bullet | Open the Skills page once for the loaded character. |
| M7 | Detailed explanation | Refunds devotion points, removes devotion bindings, refunds skill and mastery-bar points, then clears both selected masteries. Celestial-power experience is retained. Requires a living single-player character outside combat, no item-granted skills, and point totals that match the inspection. |
| O1 | Blueprints description | Search installed recipes and spawn a blueprint. Use it from the character's inventory to learn it. |
| O2 | Quest-token warning | Warning: Quest-token changes can permanently break quests or character progression. Change only states you understand. Search for a quest token to inspect or change its state. |
| O3 | Quest-token action note | Changes require confirmation. Quest tokens depend on one another; back up the character before editing them. |
| O4 | Compatibility explanation | Required game functions are checked by name. Addresses and file checks update automatically when the game files change. |
| O5 | Game-folder description | Your Steam installation is detected automatically. Choose a folder only if the correct installation was not found. |
| O6 | Force Refresh Data description | Rebuild the item catalogue and refresh compatibility, quest and navigation data from the installed game files. This may take some time. |
| O7 | Technical-safety description | Companion does not modify game archives. Catalogue updates only read installed data. The bridge loads on demand, communicates through a local Windows pipe and runs actions on the game's update thread. It has no stealth, persistence, networking or multiplayer support. Character-changing tools can still affect saves. |
| O8 | Bridge-unload description | Companion can remove its update-thread hook and unload the bridge when closing, allowing the companion to reconnect without restarting Grim Dawn when unloading succeeds. |

## Radar bullets

- Distant active quest objectives appear as gold direction stars beyond the POI rim.
- Nearby POIs include services and travel utilities, such as boats.
- Hover over a marker to identify it.
- The nearest distant catalogue POIs appear at the rim to show their direction.
- The overlay hides when Grim Dawn is minimized or another app has focus.
- Markers clear when the overlay is disabled, Companion disconnects, or Companion closes.
