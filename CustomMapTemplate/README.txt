================================================================
  PROJECT HARDLINE — CUSTOM MAP TEMPLATE
  Step-by-step guide for complete beginners
================================================================

WHAT YOU NEED
─────────────
  • Unity Hub    (free — the installer/launcher for Unity)
  • Unity 2022.3.27f1  (the exact version the game uses — IMPORTANT)
  • This folder's files  (Assets/Editor/CustomMapExporter.cs  +  Assets/SpawnMarker.cs)
  • Optional: Blender (free) for making detailed 3D geometry


════════════════════════════════════════════════════════════════
  PART 1 — Install Unity (one-time setup, ~15 minutes)
════════════════════════════════════════════════════════════════

1. Download Unity Hub from:
       https://unity.com/download
   Install and open it.

2. In Unity Hub, click "Installs" on the left → "Install Editor".

3. Switch to the "Archive" tab, then click "download archive".
   This opens the Unity download archive in your browser.

4. Find "Unity 2022.x" in the list, then find version 2022.3.27f1.
   Click "Unity Hub" next to it to install via Unity Hub.
   (Only tick "Windows Build Support (IL2CPP)" — nothing else is needed.)

5. Wait for it to finish installing.


════════════════════════════════════════════════════════════════
  PART 2 — Create a New Unity Project (one-time, ~2 minutes)
════════════════════════════════════════════════════════════════

1. In Unity Hub, click "Projects" → "New project".

2. Select "3D (Built-In Render Pipeline)" — the plain 3D template.
   (Do NOT choose URP or HDRP.)

3. Give it a name like "HardlineMapMaker" and click "Create project".
   Unity will open. It may take a minute the first time.


════════════════════════════════════════════════════════════════
  PART 3 — Install the Template Scripts (one-time, ~1 minute)
════════════════════════════════════════════════════════════════

1. Open Windows Explorer and find the folder this README is in:
       ...Project Hardline\CustomMapTemplate\

2. Inside it you'll see:
       Assets\
         Editor\
           CustomMapExporter.cs
         SpawnMarker.cs

3. Copy BOTH of these into your Unity project's Assets folder.
   The easiest way:
     a. In Unity, look at the "Project" panel at the bottom.
     b. Right-click on "Assets" → "Show in Explorer".
     c. Copy "CustomMapExporter.cs" into the "Editor" sub-folder
        (create a folder named "Editor" if it doesn't exist).
     d. Copy "SpawnMarker.cs" directly into "Assets".

4. Switch back to Unity. It will compile for a few seconds.
   When done, you'll see a new "Custom Maps" menu in the top menu bar.


════════════════════════════════════════════════════════════════
  PART 4 — Making Your First Map
════════════════════════════════════════════════════════════════

─── 4a. Set up the scene ───────────────────────────────────────

1. In Unity's top menu bar click:
       Custom Maps → Create Map Template in Scene

   This creates a "MapGeometry" object in the scene with:
     • A large flat ground plane (100 × 100 metres)
     • 2 blue spawn points for Team 1 (one side of the map)
     • 2 red spawn points for Team 2 (other side)

   You should see coloured markers in the scene view.
   If you don't see them, press the Gizmos button (top-right of the scene view).

─── 4b. Build your map geometry ────────────────────────────────

OPTION A — Use Unity's built-in shapes (easiest, no extra tools):
  • In the top menu: GameObject → 3D Object → Cube / Sphere / Plane / etc.
  • Move, scale, and rotate them to build walls, floors, cover, etc.
  • IMPORTANT: In the Hierarchy panel, drag each object you create
    INTO the "MapGeometry" object so it becomes a child of it.

OPTION B — Import a model from Blender (best looking):
  • In Blender, model your map and export it as FBX or OBJ.
  • In Unity's Project panel, drag the exported file into Assets/.
  • Drag the model from the Project panel into the scene.
  • In the Hierarchy, drag it INTO MapGeometry to make it a child.

─── 4c. Place spawn points ─────────────────────────────────────

The template already has 2 Team-1 and 2 Team-2 spawn points.
To move them: click a spawn marker in the Hierarchy or scene view,
then use the Move tool (W key) to drag it to where players should spawn.

To add MORE spawn points:
  1. Right-click "MapGeometry" in the Hierarchy → Create Empty.
  2. With the new object selected, look at the Inspector panel (right side).
  3. Click "Add Component" → type "SpawnMarker" → press Enter.
  4. In the SpawnMarker settings, set Team to 1 or 2.
  5. Move the object to the desired spawn location.

─── 4d. Export ─────────────────────────────────────────────────

1. Click:  Custom Maps → Open Exporter

2. In the exporter window:
     • Enter a map name (letters and numbers only, no spaces).
     • Click "..." to browse to the game's CustomMaps/ folder:
           ...Steam\steamapps\common\Project Hardline\CustomMaps\
     • All 4 checklist items should show green checkmarks.

3. Click "Export Map Bundle".
   Unity will process for a few seconds, then show a success popup.

4. The .bundle file is now in your CustomMaps/ folder.
   Everyone who wants to play the map needs this same .bundle file
   placed in their own CustomMaps/ folder.


════════════════════════════════════════════════════════════════
  PART 5 — Playing on the Custom Map
════════════════════════════════════════════════════════════════

1. Make sure all players have the .bundle file in:
       ...Project Hardline\CustomMaps\

2. Start the game. Host a lobby.

3. In the lobby, click the map dropdown — your map will appear at
   the bottom of the list with a "[Custom]" prefix.

4. Select it and start the game. The mod will automatically load
   your map geometry, hide Level 1's terrain, and place everyone
   at your spawn points.


════════════════════════════════════════════════════════════════
  TIPS & TROUBLESHOOTING
════════════════════════════════════════════════════════════════

• "Custom Maps" menu is missing after copying the scripts:
  → Unity may have failed to compile. Check Window → General → Console
    for red errors and fix them.

• The map loads but players fall through the floor:
  → Your geometry doesn't have colliders. Select the mesh in the scene,
    look in the Inspector, and add a "Mesh Collider" component.
    Unity primitives (Cube, Plane, etc.) include colliders automatically.

• Players can't see the custom map (Level 1 terrain is still visible):
  → The mod only hides Level 1 geometry that has Renderer/Terrain components
    and isn't a recognised game-system object. Make sure your .bundle file
    is the correct one and is in the right folder on all machines.

• The exporter button is greyed out:
  → Check the checklist in the Exporter window — all four items must be green.

• Map looks too dark / lighting is wrong:
  → In Unity: Window → Rendering → Lighting → click "Generate Lighting".
    Or add a Directional Light: GameObject → Light → Directional Light.

• I want to add textures / colours to my map:
  → Create a Material: right-click in the Project panel → Create → Material.
    Change its colour or assign a texture. Then drag the material onto a mesh
    in the scene to apply it.

================================================================
