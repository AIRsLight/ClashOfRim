<!-- Keep this list to final, user-visible outcomes on the dev branch. Do not list temporary commits, reverted work, or implementation-only refactors. -->

## Current development changes

- **Authoritative world baseline:** new players receive the server's complete world substrate, preventing divergent world generation when all players use the same baseline.
- **Compatibility and configuration:** registered mod settings are checked consistently; configuration differences are shown separately from file differences. You can either use a server-scoped configuration overlay or write the server configuration into local `Config` files before restarting.
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
