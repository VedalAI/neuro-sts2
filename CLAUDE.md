# STS2 Mod Project

## About
This is a mod for Slay the Spire 2 using HarmonyX patches compiled to a .dll.

## Decompiled game source
The decompiled STS2 game code is in `./sts2-decompiled/`.
Use it as read-only reference to find classes and methods to patch.
Never modify files in that folder.

## Setup
- Target framework: net9.0
- Language: C#
- HarmonyX and GodotSharp are installed via NuGet
- The compiled .dll is auto-copied to the STS2 mods folder on build
- User will handle building

## How mods work
Mods use HarmonyX [HarmonyPatch] attributes to patch methods in sts2.dll at runtime.
Use Prefix patches to run before a method, Postfix to run after.
This mod is intended for a single player and a single websocket-based controller. Set `NEURO_SDK_WS_URL`, let the controller connect through the Neuro SDK websocket flow, then respond to the registered actions and context updates it receives.
