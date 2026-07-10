# Installing the Mods

This guide is for players who just want to run the mods — not build them from source. If you want to build from source instead, see the [README](README.md).

These are unofficial, fan-made mods and are not affiliated with, endorsed by, or supported by the developers/publisher of Project Hardline. Use at your own risk.

## What you need

- Project Hardline installed via Steam
- The mod DLLs from this repo's [Releases](../../releases) page: `CustomMapsMod.dll`, `FFAMod.dll`, `HostKickMod.dll`, `MaxPlayersMod.dll`, etc
- [BepInEx 5.4.23](https://github.com/BepInEx/BepInEx/releases) — the mod loader all mods run on. If you already have BepInEx installed for Project Hardline, skip to [Installing the mods](#installing-the-mods).

**Important:** `FFAMod.dll` will fail to load unless `HostKickMod.dll` is also installed — FFAMod directly depends on it (it reads HostKickMod's keybind settings for its own keybind menu). The other mods don't depend on each other, but you can just install everything you download together.

---

## Installing BepInEx

### Windows

1. Find your game's install folder — in Steam, right-click **Project Hardline** → **Manage** → **Browse local files**. This opens the folder containing `Project Hardline.exe`.
2. Download **BepInEx_x64** (the Windows build) from the [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases) — get version 5.4.23 to match what these mods were built against.
3. Extract the contents of the zip directly into the game folder, so `winhttp.dll`, `doorstop_config.ini`, and a `BepInEx/` folder all end up sitting next to `Project Hardline.exe`.
4. Launch the game once via Steam and let it fully reach the main menu, then close it. This lets BepInEx generate its full folder structure (`BepInEx/plugins/`, `BepInEx/config/`, `BepInEx/LogOutput.log`).
5. Check `BepInEx/LogOutput.log` — you should see BepInEx startup messages. If the file doesn't exist or is empty, BepInEx isn't hooking into the game; double check step 3.

### Mac

> The Mac steps below follow BepInEx's standard Unix/macOS installation pattern. If anything doesn't match what you see (folder names, exact prompts), treat BepInEx's own docs as the source of truth — Mac injection details can shift between versions.

1. Find your game's install folder — in Steam, right-click **Project Hardline** → **Manage** → **Browse local files**. This opens the folder containing `Project Hardline.app`.
2. Download the **Mac** build of BepInEx 5.4.23 (not the Windows x64 one) from the [BepInEx releases page](https://github.com/BepInEx/BepInEx/releases).
3. Extract it into the same folder as `Project Hardline.app`. So go to the local files of project hardline, right click the Project Hardline icon, click "Show Package Contents", click into the "contents" folder, then this is where you want to extract BepInEx
4. Open Terminal in that folder and run:
   ```
   chmod +x run_bepinex.sh
   ```
5. macOS will likely block the unsigned script/library the first time you run it. If you see a security warning, go to **System Settings → Privacy & Security** and allow it (the exact wording depends on your macOS version).
6. Because macOS doesn't support the same injection trick BepInEx uses on Windows, the game needs to be launched *through* `run_bepinex.sh` rather than by double-clicking the app directly. You have two options:

   **Option A — Steam launch option (lets you keep using the Play button):**
   1. In Steam, right-click **Project Hardline** → **Properties** → **General** tab → find the **LAUNCH OPTIONS** field.
   2. Enter (using the actual full path to where you extracted BepInEx):
      ```
      /full/path/to/Project Hardline/run_bepinex.sh %command%
      ```
      `%command%` is a token Steam substitutes with its normal launch command — this tells Steam to run the script first and hand it the real launch command as an argument, which is exactly what `run_bepinex.sh` expects. This is the standard pattern BepInEx documents for Unix/Steam games; if Steam on Mac doesn't honor `%command%` the way it does on Windows/Linux, fall back to Option B.
   3. Click **Play** in Steam as normal from now on.

   **Option B — launch directly from Terminal (most reliable for testing):**
   Make sure the Steam client is running and you're logged in (most games only check that Steam is running, not that you clicked Play in it), then:
   ```
   cd "/full/path/to/Project Hardline"
   ./run_bepinex.sh
   ```
   This launches the game directly, bypassing Steam's own launch button entirely.
7. Once it launches successfully once, check `BepInEx/LogOutput.log` for BepInEx startup messages to confirm it's working, then close the game.

---

## Installing the mods

1. Download `CustomMapsMod.dll`, `FFAMod.dll`, `HostKickMod.dll`, `MaxPlayersMod.dll`, etc from this repo's [Releases](../../releases) page.
2. Copy all mods into `BepInEx/plugins/` (same folder on both Windows and Mac, just found inside whichever install folder you set up above).
3. Launch the game.
4. Check `BepInEx/LogOutput.log` for lines like:
   ```
   Loading [Custom Maps 1.0.0]
   Loading [Free For All Mode Toggle ...]
   Loading [Host Kick and Crosshair Mod 3.0.0]
   Loading [Max Players Mod 1.0.0]
   ```
   with no errors underneath them. That confirms everything loaded correctly.

---

## Using the mods

### HostKickMod — host kick menu & custom crosshair

| Key | Who | What it does |
|-----|-----|---------------|
| `F8` | Host only | Toggle the kick menu — lists connected players with a KICK button next to each |
| `F9` | Everyone | Toggle the crosshair customization menu (style, size, color, outline, etc.) |

### FFAMod — Free-For-All and Gun Game modes

| Key | Who | What it does |
|-----|-----|---------------|
| `F7` | Everyone (toggle applies per-player) | Open the FFA/Gun Game mode menu |
| `F5` | Everyone | Open the keybind configuration menu, to rebind any of these keys |

In the F7 menu, pick **FFA Mode** (last player standing each round, first to 5 round wins takes the match) or **Gun Game** (get kills to upgrade weapons; the first knife kill wins). Everyone who wants the mode active needs to enable it themselves before the match starts — it's a per-player toggle, not a host-only lobby setting.

### MaxPlayersMod — bigger lobbies

No keybinds or setup — it's automatic. As soon as it's installed, lobbies can hold more than the game's normal 5-player cap (up to Steam's own limit of 250). Just host a lobby as usual.

### CustomMapsMod — load custom maps

1. Launch the game once with the mod installed — it creates an empty `CustomMaps/` folder next to `Project Hardline.exe` (or `Project Hardline.app` on Mac).
2. Drop any `.bundle` map file into that folder (see the [CustomMapTemplate](CustomMapTemplate/) folder in this repo if you want to make your own).
3. Host a lobby — custom maps show up at the bottom of the map dropdown with a `[Custom]` prefix.

---

## Troubleshooting

- **A mod doesn't show up in the log at all:** Confirm `BepInEx/plugins/` actually contains the `.dll` file and that BepInEx itself is loading (see the BepInEx installation steps above).
- **"Could not load file or assembly 'HostKickMod'" or similar for FFAMod:** `HostKickMod.dll` is missing from `plugins/` — FFAMod requires it to be present even if you don't use the kick/crosshair features.
- **Something else looks wrong:** Open `BepInEx/LogOutput.log` and look for `[Error]` or `[Warning]` lines near where a mod loads — that's almost always where the actual problem is described.
