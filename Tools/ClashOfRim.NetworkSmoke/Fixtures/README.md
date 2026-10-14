# Network smoke fixture

`NetworkSnapshot.rws` is synthetic XML authored for this repository. It contains no player save, personal data, game binaries, or extracted game assets; tests can ship it without relying on a developer's local saves.

The fixture exercises server snapshot parsing, hashing, lineage, map/site identity, relocation and HTTP transactions. Tests derive independent owners and subsequent snapshots from it. Its stable map is `Map_0`, parent settlement is `WorldObject_100`, player faction is `Faction_0`, starting tile is `9,0`, and starting game time is 1000 ticks. One player colonist and a silver stack provide colony and inventory data.

This is deliberately a server-format fixture, not a complete save that RimWorld can load. Passing these tests does not establish engine compatibility or replace two-client gameplay verification. The project copies the fixture to its output directory so tests do not depend on a local `SaveSample` folder or the shell working directory.
