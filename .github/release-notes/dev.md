<!-- Keep this list to final, user-visible outcomes on the dev branch. Do not list temporary commits, reverted work, or implementation-only refactors. -->

## Current development changes

### 0.4.3 testing update

- **Periodic stutter:** location polling no longer exports the entire ideology catalog. Entry, explicit colony changes, saves and ideology reforms still collect live extensions. Ideology timestamps now change with content, not with every observation. Post-save site and extension followups now share one collection.
- **World synchronization:** accepted responses are isolated in the background and reused during publication; content fingerprints use bounded hashes instead of retaining multi-megabyte serialized strings. Session ownership and independent marker/site projections remain enforced.
- **Snapshot saving:** lineage verification runs with background packaging and preserves already-correct save bytes. Native Scribe and compatibility cleanup callbacks remain on the game thread, so saving can still cause a pause.
- **Compatibility:** protocol requirements are unchanged. RimWorld 1.6 remains frozen at 0.3.4. This update does not claim to fix the intermittent first-colony single-player entry report.

### 0.4.2 testing update

- **Neutral colony observation:** observing a neutral colony preserves its diplomatic relationship and neutral pawn behavior. Scout maps still hide traps and apply fog of war; player-list synchronization no longer produces a spurious relationship recovery notice.
- **Compatibility update recovery:** administrators can replace the server compatibility baseline after a game or mod update even when world entry is rejected. Update both the client and server to receive this fix.
- **Actionable compatibility controls:** game/mod version and file differences no longer offer an ineffective mod-list application. Actual mod selection and configuration differences retain their applicable recovery actions, with manual updates explained separately.
- **Temporary configuration application:** the configuration overlay is now labeled **Apply temporarily** (Chinese: **临时套用**) across all five supported languages. Descriptions clarify that this preserves local configuration files and requires a restart.
- **First-colony startup investigation:** repeated new-world tests with the supplied mod list, Miho single-pawn scenario and fixed custom ideology did not reproduce the reported missing multiplayer controls. That issue remains under observation and is not claimed as fixed in this build.

### 0.4.1 testing update

- **Remote pocket maps:** entering a portal loads the defender's saved submap on demand, including nested maps. Supported unopened portals can generate an isolated attacker-side map; unsupported entries are blocked. Session cleanup and raid losses include units in loaded submaps.
- **Remote map loading:** snapshot validation, decompression and XML parsing now run in the background before map import. Native map initialization still pauses the game thread; this does not eliminate loading stalls.
- **Remote defense and third-party compatibility:** repaired NPC defense lords and faction identities, colony mech defense eligibility, vehicle AI exclusions, and MVCF state restoration.
- **Pawn transfers:** gifts, support and returns preserve the species and PawnKind of missing relationship pawns, including alien races and animal bonds. Explicit species that are unavailable fail restoration instead of silently becoming human.
- **World and session correctness:** repaired complete world-tile baseline replacement, configuration application in the incoming server profile, logout authorization, and stale connection cleanup.
- **Multilingual UI and DLC:** improved dialog layouts across languages, repaired shared ideology identity collisions, and added a targeted workaround for the malformed Odyssey Russian faction naming rule.
- **Testing scope:** this build includes targeted unit and RimTest coverage accumulated during development. The release pipeline checks portable behavior and package isolation; it does not certify every DLC/mod combination or replace a fresh full in-game regression.

### Included development features

- **RimWorld 1.7 migration:** active clients now target Waylith, .NET 10 and Harmony 3. Existing 1.6 support is frozen at 0.3.4, with isolated client resources and a preserved Windows server package; all future features target 1.7.

- **Authoritative world baseline:** new players receive the server's complete world substrate, preventing divergent world generation when all players use the same baseline.
- **Storyteller and difficulty synchronization:** server storyteller, custom difficulty, and threat-scale settings are now enforced after snapshot loading, lightweight world refreshes, and live baseline updates.
- **Compatibility and configuration:** registered mod settings are checked consistently; configuration differences are shown separately from file differences. You can either temporarily apply the server configuration while preserving local files or write it into local `Config` files before restarting.
- **Multiplayer session startup:** new colonies now release the world-entry gate before map play begins, allowing automatic login and the initial snapshot upload to start normally.
- **Snapshot and remote-world reliability:** improved snapshot metadata, remote map projection, player colony/site handling, and stale session cleanup.
- **Trading and transfers:** expanded support for stateful items, animals, mechanoids, pawn-related payloads, and best-effort fallback data for unsupported item state.
- **World state compatibility:** improved ideology ownership and remote faction projection, language-aware world feature catalogs, and gravship colony relocation handling.
- **Server persistence:** growing server registries use structured SQLite tables, with explicit versioned migration tooling instead of implicit legacy-data conversion.
- **Server diagnostics:** normal snapshot downloads no longer emit misleading raid cleanup warnings.
- **Safer offline accounts:** unknown account names now require explicit registration and password confirmation. Failed authentication can no longer claim the initial administrator role or mutate world-session state.
- **Session recovery:** interrupted in-game sessions can reconnect from the disconnect dialog without forcing an immediate return to the main menu when recovery is safe.
- **Raid settlement pipeline:** raid settlement now runs through a durable asynchronous queue with idempotent recovery, compatibility-editor failure handling, and clearer server diagnostics instead of blocking snapshot uploads.
- **Raid defense behavior:** restored defender activation, defense-point patrol movement, hidden-trap triggering, guard-team arrival notices, and server-side raid cooldown administration.
- **Delivery feedback:** delivered items and pawns are tracked as typed delivery events, and arrival letters point to the actual spawned targets when available.
- **Transfer safety:** gifts, trades, and pawn/item transfers share a centralized preprocessing policy that rejects unsafe references while preserving supported stateful content.
- **Cross-language sessions:** language differences are shown in the compatibility overview and can be acknowledged explicitly; landmark and ideology synchronization now preserve shared identity more reliably across languages.
- **Interface safeguards:** multiplayer controls remain hidden outside active sessions, login fields support Tab navigation, dangerous administrator actions are visually distinguished, and selection controls use consistent labels.
