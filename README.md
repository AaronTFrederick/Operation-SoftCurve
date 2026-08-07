# Project Hardline Mods

BepInEx mods for [Project Hardline](https://store.steampowered.com/), built with Harmony — plus a Discord bot for a free, self-hostable ranked leaderboard.

These are unofficial, fan-made projects and are not affiliated with, endorsed by, or supported by the developer/publisher of Project Hardline.

## Mods

- **CustomMapsMod** — Lets a host load custom maps from AssetBundles dropped into a `CustomMaps/` folder next to the game. Injects spawn points, resupply stations, uplink placement, lighting, and per-renderer colors/textures for the imported geometry.
- **FFAMod** — Adds a Free-For-All mode and a Gun Game mode on top of the game's normal team play, including its own spawn selection, weapon progression, and score tracking.
- **HostKickMod** — Adds a host-only menu to disconnect players, plus a configurable custom crosshair overlay for everyone.
- **MaxPlayersMod** — Removes the game's hardcoded 5-player lobby cap and creates the Steam lobby at Steam's own maximum (250) instead.
- **HardlineLeaderboard** — Reports match results to [HardRankBot](HardRankBot/) so they count toward the ranked leaderboard. Third-party plugin (Apache 2.0, not MIT like the others above) — see [HardlineLeaderboard/](HardlineLeaderboard/).
- **CustomMapTemplate** — Not a mod; a set of Unity Editor scripts (exporter + spawn/lighting marker components) plus two beginner-friendly guides for building and exporting `.bundle` map files that work with CustomMapsMod.

## Requirements

- [BepInEx](https://github.com/BepInEx/BepInEx) already installed for Project Hardline (so `BepInEx/core/`, `BepInEx/plugins/`, etc. exist in the game folder)
- .NET Framework 4.7.2 targeting pack (each project targets `net472`)
  - Comes with Visual Studio if you select the ".NET desktop development" workload, **or**
  - `dotnet build` from the .NET SDK also works on Windows as long as the net472 reference assemblies are installed (Visual Studio Build Tools installs these; a plain SDK install may not)

## Building

Each mod is a standalone class library project. The `.csproj` files reference game DLLs using relative paths like `..\Project Hardline_Data\Managed\...` and `..\BepInEx\core\...`, so each project folder needs to sit at the same directory depth as `BepInEx/` and `Project Hardline_Data/` inside your game install.

### 1. Place the project folders

Copy `CustomMapsMod/`, `FFAMod/`, `HostKickMod/`, and `MaxPlayersMod/` directly into your Project Hardline install directory (the folder containing `Project Hardline.exe`), so the layout looks like:

```
Project Hardline/
├── BepInEx/
├── Project Hardline_Data/
├── CustomMapsMod/
├── FFAMod/
├── HostKickMod/
└── MaxPlayersMod/
```

### 2. Build HostKickMod first

FFAMod references `HostKickMod.dll` via a hardcoded path to `HostKickMod\bin\Debug\net472\HostKickMod.dll` (used so the FFA keybind menu can show/edit HostKickMod's keybinds), so that DLL has to exist before FFAMod will compile.

From the game directory:

```
cd HostKickMod
dotnet build
cd ..
```

(Or open `HostKickMod.csproj` in Visual Studio and use Build → Build Solution — just make sure it's built in **Debug** config, since that's the path FFAMod's `.csproj` points at.)

### 3. Build the other mods

```
cd FFAMod
dotnet build
cd ..

cd CustomMapsMod
dotnet build
cd ..

cd MaxPlayersMod
dotnet build
cd ..
```

Order between FFAMod, CustomMapsMod, and MaxPlayersMod doesn't matter — only HostKickMod has to come first.

### 4. Output

Each project has a post-build step (`CopyToPlugins` in the `.csproj`) that automatically copies the built DLL into `BepInEx\plugins\`, so make sure that folder exists. The raw build output also lands in each project's own `bin\Debug\net472\` folder.

`HardlineLeaderboard/` builds differently (different target framework, different references, no `CopyToPlugins` step) since it's a separate third-party project rather than one of the four mods above — see [HardlineLeaderboard/README.md](HardlineLeaderboard/README.md).

## Installing (pre-built)

Just want to run the mods, not build them? See [INSTALL.md](INSTALL.md) for a full Windows + Mac walkthrough, including installing BepInEx itself. Short version: drop the built `CustomMapsMod.dll`, `FFAMod.dll`, `HostKickMod.dll`, and `MaxPlayersMod.dll` into your `BepInEx/plugins/` folder. Add `HardlineLeaderboard.dll` too if you want ranked tracking — see [HardlineLeaderboard/README.md](HardlineLeaderboard/README.md).

## Making maps with CustomMapTemplate

`CustomMapTemplate/` isn't built or installed like the three mods above — it's a couple of C# scripts you copy into a separate Unity project (Unity 2022.3.27f1, Built-in Render Pipeline) to build and export `.bundle` map files for CustomMapsMod. See `CustomMapTemplate/README.txt` for full setup steps and `CustomMapTemplate/HOW_TO_MAKE_A_MAP.txt` for details on building geometry, spawn markers, and lighting.

## HardRankBot

A Discord bot that provides a free, self-hosted ranked leaderboard (ELO/MMR, per-map rankings, match history) for communities running the [HardlineLeaderboard](HardlineLeaderboard/) BepInEx plugin — see [HardRankBot/](HardRankBot/). It's a Python project (not a BepInEx mod) with its own setup: see [HardRankBot/README.md](HardRankBot/README.md) for creating the Discord bot, configuring it, and free 24/7 hosting instructions.

## License

This repo has two licenses, one per project type:

- **The BepInEx mods and CustomMapTemplate** (`CustomMapsMod/`, `FFAMod/`, `HostKickMod/`, `MaxPlayersMod/`, `CustomMapTemplate/`) — MIT, see [LICENSE](LICENSE).
- **HardRankBot and HardlineLeaderboard** — Apache 2.0, both originating from Matthias Muhl's ("fleeter") HardRank project. HardRankBot is a derivative (a Discord-bot reimplementation of the backend); HardlineLeaderboard is his original plugin with minimal changes. See [HardRankBot/LICENSE](HardRankBot/LICENSE)/[NOTICE](HardRankBot/NOTICE) and [HardlineLeaderboard/LICENSE](HardlineLeaderboard/LICENSE)/[NOTICE](HardlineLeaderboard/NOTICE) for exactly what was reused and what changed in each.
