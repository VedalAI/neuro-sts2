# neuro-sts2

A Slay the Spire 2 mod that integrates the game with Neuro SDK over websocket.

## Overview

The mod watches the current game context, registers the actions that are valid for that decision point, and sends context updates back to the connected controller. The controller answers with Neuro SDK `action` messages, which the mod validates and executes in-game.

This repository no longer exposes the older local HTTP API. The current integration path is the websocket-based Neuro SDK flow implemented under `NeuroSdk/`.

## Features

- **Context-aware action registration** for combat, map movement, events, rewards, shops, rest sites, card selection, timelines, and more
- **Decision-point signaling** driven by the stability detector so actions are only offered when the game is ready
- **Queued action execution** with revalidation before execution to avoid stale UI actions
- **Event logging and context narration** so the connected controller receives useful state updates between actions

## Connection setup

The websocket URL is discovered from the `NEURO_SDK_WS_URL` environment variable. The lookup checks process, user, and machine scopes in that order.

Example:

```bash
export NEURO_SDK_WS_URL=ws://127.0.0.1:8000
```

When the game starts, the mod initializes `NeuroSdkSetup`, opens the websocket connection, registers available actions, and begins sending context / force messages to the connected controller.

## Installation

1. Build the mod from this repository.
2. Copy the produced `neuro-sts2.dll` and `neuro-sts2.json` into your Slay the Spire 2 `mods/` folder if your build step did not already copy them there.
3. Set `NEURO_SDK_WS_URL` so the mod can reach the controller.
4. Launch the game.

## Supported contexts

The mod handles every major game screen:

- **Main Menu** / **Character Select**
- **Map**
- **Combat**
- **Events**
- **Rest Sites**
- **Shop**
- **Treasure**
- **Rewards**
- **Card / Hand Selection**
- **Game Over**
- **Timelines**
- **Crystal Ball**

## Building from source

Requires .NET 9.0 and a local STS2 installation so `sts2.dll`, `GodotSharp.dll`, and `0Harmony.dll` can be resolved.

```bash
dotnet build --nologo
```

The project is configured to copy the built DLL and `neuro-sts2.json` into the STS2 mods directory after a successful build.

## Logs

The mod writes logs to `~/sts2agent.log`.
