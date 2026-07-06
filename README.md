# Project Hardline Mods

BepInEx mods for [Project Hardline](https://store.steampowered.com/), built with Harmony.

These are unofficial, fan-made mods and are not affiliated with, endorsed by, or supported by the developer/publisher of Project Hardline.

## Mods

- **CustomMapsMod** — Lets a host load custom maps from AssetBundles dropped into a `CustomMaps/` folder next to the game. Injects spawn points, resupply stations, uplink placement, lighting, and per-renderer colors/textures for the imported geometry.
- **FFAMod** — Adds a Free-For-All mode and a Gun Game mode on top of the game's normal team play, including its own spawn selection, weapon progression, and score tracking.
- **HostKickMod** — Adds a host-only menu to disconnect players, plus a configurable custom crosshair overlay for everyone.
- **CustomMapTemplate** — Not a mod; a set of Unity Editor scripts (exporter + spawn/lighting marker components) plus two beginner-friendly guides for building and exporting `.bundle` map files that work with CustomMapsMod.

## Requirements

- [BepInEx](https://github.com/BepInEx/BepInEx) already installed for Project Hardline (so `BepInEx/core/`, `BepInEx/plugins/`, etc. exist in the game folder)
- .NET Framework 4.7.2 targeting pack (each project targets `net472`)
  - Comes with Visual Studio if you select the ".NET desktop development" workload, **or**
  - `dotnet build` from the .NET SDK also works on Windows as long as the net472 reference assemblies are installed (Visual Studio Build Tools installs these; a plain SDK install may not)

## Building

Each mod is a standalone class library project. The `.csproj` files reference game DLLs using relative paths like `..\Project Hardline_Data\Managed\...` and `..\BepInEx\core\...`, so each project folder needs to sit at the same directory depth as `BepInEx/` and `Project Hardline_Data/` inside your game install.

### 1. Place the project folders

Copy `CustomMapsMod/`, `FFAMod/`, and `HostKickMod/` directly into your Project Hardline install directory (the folder containing `Project Hardline.exe`), so the layout looks like:

```
Project Hardline/
├── BepInEx/
├── Project Hardline_Data/
├── CustomMapsMod/
├── FFAMod/
└── HostKickMod/
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

### 3. Build the other two mods

```
cd FFAMod
dotnet build
cd ..

cd CustomMapsMod
dotnet build
cd ..
```

Order between FFAMod and CustomMapsMod doesn't matter — only HostKickMod has to come first.

### 4. Output

Each project has a post-build step (`CopyToPlugins` in the `.csproj`) that automatically copies the built DLL into `BepInEx\plugins\`, so make sure that folder exists. The raw build output also lands in each project's own `bin\Debug\net472\` folder.

## Installing (pre-built)

Drop the built `CustomMapsMod.dll`, `FFAMod.dll`, and `HostKickMod.dll` into your `BepInEx/plugins/` folder.

## Making maps with CustomMapTemplate

`CustomMapTemplate/` isn't built or installed like the three mods above — it's a couple of C# scripts you copy into a separate Unity project (Unity 2022.3.27f1, Built-in Render Pipeline) to build and export `.bundle` map files for CustomMapsMod. See `CustomMapTemplate/README.txt` for full setup steps and `CustomMapTemplate/HOW_TO_MAKE_A_MAP.txt` for details on building geometry, spawn markers, and lighting.

## License

MIT — see [LICENSE](LICENSE).
