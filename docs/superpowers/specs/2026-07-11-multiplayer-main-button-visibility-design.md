# Multiplayer Main Button Visibility Design

## Goal

Show the multiplayer bottom-bar button only while the current game belongs to an active ClashOfRim server session.

## Root Cause

`ClashOfRim_Multiplayer` does not declare a `workerClass`, so RimWorld creates the default `MainButtonWorker_ToggleTab`. Vanilla `MainButtonWorker.Visible` only checks the def's static `buttonVisible` flag and cannot distinguish a single-player game from a server-backed game.

## Design

- Add a dedicated `MainButtonWorker_ToggleTab` subclass and assign it through the existing `MainButtonDef.workerClass` field.
- Preserve `base.Visible` so vanilla ideology and static visibility rules remain effective.
- Add a narrowly named runtime property on `ClashOfRimMod` that is true only when server configuration and a live session ID are present.
- Use that property from the worker. Do not patch `MainButtonsRoot` and do not mutate the global `MainButtonDef.buttonVisible` field.
- Keep the button visible during normal server play, remote observation/scouting, and raids because those modes retain the server session ID.
- Hide the button in ordinary single-player games and after the server session is disconnected.

## Verification

- A source-contract test requires the custom worker assignment and session-gated `Visible` override.
- The event test suite and client build pass.
- The packaged mod is installed locally for an in-game single-player and multiplayer check.
