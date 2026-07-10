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
