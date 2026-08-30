#!/usr/bin/env node
// Unity MCP Bridge — Claude Code / Antigravity <-> Unity Editor koprusu
// Unity tarafindaki McpBridge.cs 127.0.0.1:6400'de dinler; bu sunucu MCP tool'larini oraya iletir.

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import net from "node:net";
import crypto from "node:crypto";

const UNITY_HOST = "127.0.0.1";
const UNITY_PORT = parseInt(process.env.UNITY_MCP_PORT || "6400", 10);

function callUnity(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const socket = net.createConnection({ host: UNITY_HOST, port: UNITY_PORT });
    let buffer = "";
    const timeout = setTimeout(() => {
      socket.destroy();
      reject(new Error("Unity did not respond (60s). Is the editor open? It may still be compiling - wait a moment and retry."));
    }, 60000);

    socket.on("connect", () => {
      socket.write(JSON.stringify({ id, method, params }) + "\n");
    });
    socket.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      const nl = buffer.indexOf("\n");
      if (nl === -1) return;
      clearTimeout(timeout);
      socket.end();
      try {
        const resp = JSON.parse(buffer.slice(0, nl));
        if (resp.error) reject(new Error(resp.error));
        else resolve(resp.result);
      } catch (e) {
        reject(new Error("Malformed response from Unity: " + e.message));
      }
    });
    socket.on("error", (e) => {
      clearTimeout(timeout);
      reject(new Error(
        e.code === "ECONNREFUSED"
          ? "Cannot reach the Unity Editor. Is Unity open with McpBridge.cs in the project? (Tools > MCP Bridge > Restart Server)"
          : e.message
      ));
    });
  });
}

// Server-level guidance. The MCP `instructions` field is returned in the
// initialize response, so EVERY client (Claude Code, Cursor, Windsurf, Cline,
// Antigravity, ...) receives it - not just one editor's rules file.
const INSTRUCTIONS = `Unity Editor bridge: these tools drive a LIVE Unity Editor over TCP.

COST — screenshots and console dumps are by far the most expensive calls here:

1. unity_capture_screenshot is for VISUAL checks only (UI layout, sprite look,
   effects). For logic or data verification do NOT screenshot: write a report to
   a file from an editor script and read that file instead. It is far cheaper and
   gives exact values instead of a guess from pixels.
2. Always call unity_read_console with a small limit (5) and type:"Error".
   Repeated identical messages are already collapsed with a 'count'. After the
   first read, pass the returned 'cursor' back as 'since' to fetch ONLY new
   entries - this is the cheapest way to poll and avoids re-reading old errors.
3. Prefer unity_get_object over unity_get_scene when one object is enough;
   get_scene returns the entire hierarchy.
4. Read and write C# with your own filesystem tools, not through Unity. Then
   compile with ONE unity_execute_menu "Assets/Refresh".
5. Batch several edits and test once. Avoid a play/stop cycle per small change.
6. Building a scene? Use unity_create_objects once instead of calling
   unity_create_object in a loop.

PLAY MODE:
- Call unity_get_play_state BEFORE unity_play. Calling play while already
  playing toggles the mode and wastes a full reload.
- Never edit or compile scripts while in play mode. Unity performs a domain
  reload: static singletons reset while Awake is NOT called again, so managers
  and pooled objects silently become null. Stop play mode first.
- After entering play mode, wait a moment before sending commands; menu items
  invoked during the load are dropped.

RELIABLE LOOP:
  write file -> unity_execute_menu "Assets/Refresh"
  -> unity_read_console (type:"Error", limit:5) -> fix -> repeat
  -> only then unity_play.

The bridge runs on the Unity main thread, so a long-running or infinite loop in
an editor script will freeze the whole editor and every tool call will time out.
Always bound loops in editor scripts you write.`;

const server = new McpServer(
  { name: "unity-bridge", version: "1.0.0" },
  { instructions: INSTRUCTIONS }
);

function textResult(data) {
  return { content: [{ type: "text", text: typeof data === "string" ? data : JSON.stringify(data, null, 2) }] };
}

function tool(name, description, schema, method, mapParams = (a) => a) {
  server.registerTool(name, { description, inputSchema: schema }, async (args) => {
    try {
      return textResult(await callUnity(method, mapParams(args)));
    } catch (e) {
      return { content: [{ type: "text", text: "ERROR: " + e.message }], isError: true };
    }
  });
}

const vec3 = z.array(z.number()).length(3);

// ---------------- SAHNE OKUMA ----------------
tool(
  "unity_get_scene",
  "Returns the hierarchy of ALL loaded scenes (objects, instanceIds, positions, component lists). Returns prefab contents while in prefab mode. Can be large on big scenes - prefer unity_get_object when a single object is enough.",
  {},
  "get_scene"
);

tool(
  "unity_get_object",
  "Returns details of a single object: all components and their serialized properties (with propertyPaths). Use it before set_property to find the exact propertyPath.",
  {
    instanceId: z.number().optional().describe("instanceId from get_scene"),
    path: z.string().optional().describe("Hierarchy path, e.g. 'Environment/Tree_01'"),
  },
  "get_object"
);

// ---------------- NESNE ISLEMLERI ----------------
tool(
  "unity_create_object",
  "Creates a new GameObject in the scene. If `primitive` is given (Cube, Sphere, Capsule, Cylinder, Plane, Quad) it comes with a ready mesh.",
  {
    name: z.string(),
    primitive: z.enum(["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"]).optional(),
    parent: z.string().optional().describe("Hierarchy path of the parent object"),
    position: vec3.optional(),
    rotation: vec3.optional().describe("Euler angles"),
    scale: vec3.optional(),
  },
  "create_object"
);

tool(
  "unity_create_objects",
  "Creates MANY GameObjects in ONE call - use this instead of calling unity_create_object in a loop when building a scene. " +
  "Fields common to every item can be given once in `shared`; each item overrides them. " +
  "A failing item does not abort the rest: the result reports { created, createdCount, failed, failedCount }.",
  {
    items: z.array(z.object({
      name: z.string().optional(),
      primitive: z.enum(["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"]).optional(),
      parent: z.union([z.string(), z.number()]).optional(),
      position: vec3.optional(),
      rotation: vec3.optional(),
      scale: vec3.optional(),
    })).describe("One entry per object to create"),
    shared: z.object({
      primitive: z.enum(["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"]).optional(),
      parent: z.union([z.string(), z.number()]).optional(),
      scale: vec3.optional(),
    }).optional().describe("Defaults applied to every item (each item can override)"),
  },
  "create_objects"
);

tool(
  "unity_delete_object",
  "Deletes an object from the scene (Undo-aware).",
  { instanceId: z.number().optional(), path: z.string().optional() },
  "delete_object"
);

tool(
  "unity_set_transform",
  "Sets an object's position / rotation / scale.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    position: vec3.optional(),
    rotation: vec3.optional().describe("Euler angles"),
    scale: vec3.optional(),
    space: z.enum(["world", "local"]).optional().describe("Default: world"),
  },
  "set_transform"
);

// ---------------- COMPONENT ISLEMLERI ----------------
tool(
  "unity_add_component",
  "Adds a component to an object. The type name may be short ('Rigidbody') or fully qualified ('UnityEngine.Rigidbody'); your own scripts work too.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    componentType: z.string(),
  },
  "add_component"
);

tool(
  "unity_remove_component",
  "Removes a component from an object.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    componentType: z.string(),
  },
  "remove_component"
);

tool(
  "unity_set_property",
  "Sets a serialized property on a component OR an asset (ScriptableObject, prefab). Call unity_get_object / unity_get_asset first to find the exact propertyPath (e.g. 'm_Mass', 'm_Intensity'). Value types: number, bool, string, [x,y,z] vector, [r,g,b,a] color, enum name. Object references: an asset path ('Assets/...'), 'Assets/atlas.png#SpriteName' for a sub-asset such as a sprite, or an instanceId.",
  {
    componentInstanceId: z.number().optional().describe("Component instanceId directly (preferred)"),
    assetPath: z.string().optional().describe("Asset path when the target is an asset (e.g. a ScriptableObject)"),
    instanceId: z.number().optional().describe("GameObject instanceId (together with componentType)"),
    path: z.string().optional(),
    componentType: z.string().optional(),
    propertyPath: z.string(),
    value: z.any(),
  },
  "set_property"
);

// ---------------- ASSET / PREFAB ----------------
tool(
  "unity_find_assets",
  "Searches project assets. Filter examples: 't:Prefab tree', 't:Material', 't:Scene', 't:Script Player'. Returns asset paths.",
  { filter: z.string(), limit: z.number().optional() },
  "find_assets"
);

tool(
  "unity_instantiate_prefab",
  "Instantiates a prefab into the scene. Use unity_find_assets first to get its assetPath.",
  {
    assetPath: z.string().describe("e.g. 'Assets/Prefabs/Tree.prefab'"),
    name: z.string().optional(),
    position: vec3.optional(),
    rotation: vec3.optional(),
    parent: z.string().optional(),
  },
  "instantiate_prefab"
);

// ---------------- KOD ----------------
tool(
  "unity_create_script",
  "Writes a C# script under Assets and triggers compilation. Check unity_read_console afterwards for compile errors. Overwrites an existing file.",
  {
    path: z.string().describe("Path relative to Assets, e.g. 'Scripts/PlayerController.cs'"),
    content: z.string().describe("Full C# contents of the file"),
  },
  "create_script"
);

tool(
  "unity_read_file",
  "Reads a text file under Assets (to inspect current contents before editing a script).",
  { path: z.string().describe("Path relative to Assets") },
  "read_file"
);

// ---------------- DURUM / KONTROL ----------------
tool(
  "unity_read_console",
  "Returns recent Unity console entries. Call after writing scripts or running an action to check for errors.\n" +
  "Returns { entries, cursor, returned, matched, dropped }. Identical consecutive messages are collapsed into one entry with a `count`, so a per-frame exception no longer floods the result.\n" +
  "INCREMENTAL READ: pass the `cursor` from the previous call as `since` to get ONLY what is new. This is the cheapest way to poll after an action - use it instead of re-reading the whole buffer.\n" +
  "Always pass `type` (usually \"Error\") and a small `limit`. `dropped:true` means the buffer overflowed and some entries were lost.",
  {
    limit: z.number().optional().describe("Max entries to return (default 30). Keep it small."),
    type: z.enum(["Error", "Warning", "Log"]).optional().describe("Filter by severity; \"Error\" also includes exceptions"),
    since: z.number().optional().describe("Return only entries newer than this sequence number - pass the `cursor` from the previous call"),
    clear: z.boolean().optional().describe("Clear the buffer after reading"),
  },
  "read_console"
);

tool("unity_save_scene", "Saves the open scenes.", {}, "save_scene");

// ---------------- v2: MATERIAL ----------------
tool(
  "unity_create_material",
  "Creates a new Material asset. If no shader is given, a suitable 2D shader is chosen (URP 2D or Sprites/Default).",
  {
    path: z.string().describe("e.g. 'Materials/PlayerMat' (.mat added automatically)"),
    shader: z.string().optional().describe("e.g. 'Sprites/Default', 'UI/Default'"),
    color: z.array(z.number()).min(3).max(4).optional().describe("[r,g,b] or [r,g,b,a], 0-1 arasi"),
  },
  "create_material"
);

tool(
  "unity_set_material",
  "Sets several material properties at once. In the properties object: [r,g,b,a] -> color, number -> float, string -> texture asset path. Example: {\"_Color\": [1,0,0,1], \"_Glossiness\": 0.2, \"_MainTex\": \"Assets/Textures/wood.png\"}. Call unity_get_material first for the exact property names.",
  {
    path: z.string().describe("Material asset path"),
    properties: z.record(z.any()).optional(),
    shader: z.string().optional().describe("To change the shader"),
  },
  "set_material"
);

tool(
  "unity_get_material",
  "Returns a material's shader and all of its properties (names, types, current values).",
  { path: z.string() },
  "get_material"
);

// ---------------- v2: SCRIPTABLEOBJECT / ASSET ----------------
tool(
  "unity_create_scriptable_object",
  "Creates a ScriptableObject asset (the type must already be defined via unity_create_script and compiled). Use unity_set_property with the assetPath parameter to fill its fields.",
  {
    typeName: z.string().describe("ScriptableObject class name, e.g. 'EnemyData'"),
    assetPath: z.string().describe("e.g. 'Data/Goblin' (.asset added automatically)"),
  },
  "create_scriptable_object"
);

tool(
  "unity_get_asset",
  "Returns the serialized properties and sub-assets (such as sprite atlas contents) of any asset (ScriptableObject, prefab, ...).",
  { assetPath: z.string() },
  "get_asset"
);

// ---------------- v2: COKLU SAHNE ----------------
tool(
  "unity_list_scenes",
  "Lists loaded scenes (with their active/dirty state) and every scene file in the project.",
  {},
  "list_scenes"
);

tool(
  "unity_open_scene",
  "Opens a scene. With additive=true it loads alongside the currently open scenes (multi-scene editing).",
  {
    path: z.string().describe("e.g. 'Assets/Scenes/Level1.unity'"),
    additive: z.boolean().optional(),
    saveCurrent: z.boolean().optional().describe("Save currently open scenes first (default true)"),
  },
  "open_scene"
);

tool(
  "unity_new_scene",
  "Creates a new scene. Saves it to disk immediately if savePath is given.",
  {
    savePath: z.string().optional().describe("e.g. 'Scenes/MainMenu' (.unity added automatically)"),
    additive: z.boolean().optional(),
    empty: z.boolean().optional().describe("true: completely empty; false: with camera + light (default)"),
  },
  "new_scene"
);

tool(
  "unity_close_scene",
  "Closes a loaded scene (at least one scene must stay open).",
  { path: z.string().describe("Scene path or name"), save: z.boolean().optional() },
  "close_scene"
);

tool(
  "unity_set_active_scene",
  "Changes the active scene (new objects are added to the active scene).",
  { path: z.string().describe("Scene path or name") },
  "set_active_scene"
);

// ---------------- v2: PREFAB MODU ----------------
tool(
  "unity_open_prefab",
  "Opens a prefab in edit mode. While it is open, all object/component/property commands operate INSIDE the prefab. Remember to call unity_close_prefab when done.",
  { assetPath: z.string().describe("e.g. 'Assets/Prefabs/Enemy.prefab'") },
  "open_prefab"
);

tool(
  "unity_save_prefab",
  "Saves prefab-mode changes back to the asset (stays in prefab mode).",
  {},
  "save_prefab"
);

tool(
  "unity_close_prefab",
  "Leaves prefab mode. Saves the changes unless save=false.",
  { save: z.boolean().optional() },
  "close_prefab"
);

// ---------------- v2: EKRAN GORUNTUSU ----------------
server.registerTool(
  "unity_capture_screenshot",
  {
    description:
      "Returns a PNG of the scene as an image the model can see. EXPENSIVE (~2k tokens per call): use it ONLY for VISUAL verification - UI layout, sprite appearance, effects. For logic or numeric verification write a report file from an editor script and read that instead. view: 'game' renders from the main camera (includes Screen Space Overlay canvases), 'scene' renders from the Scene View camera.",
    inputSchema: {
      view: z.enum(["game", "scene"]).optional().describe("Default: game"),
      width: z.number().optional().describe("Default 960, max 1920"),
      height: z.number().optional().describe("Default 540, max 1080"),
    },
  },
  async (args) => {
    try {
      const r = await callUnity("capture_screenshot", args);
      return {
        content: [
          { type: "image", data: r.base64, mimeType: "image/png" },
          {
            type: "text",
            text: `${r.width}x${r.height} — kamera: ${r.camera || "scene view"}${
              r.overlayCanvasesIncluded ? `, ${r.overlayCanvasesIncluded} overlay canvas dahil` : ""
            }`,
          },
        ],
      };
    } catch (e) {
      return { content: [{ type: "text", text: "ERROR: " + e.message }], isError: true };
    }
  }
);

tool(
  "unity_execute_menu",
  "Runs a Unity menu command, e.g. 'GameObject/Light/Directional Light', 'File/Save Project'. For anything the other tools do not cover.",
  { path: z.string() },
  "execute_menu"
);

// ---------------- v2.1: PREFAB / BUILD ----------------
tool(
  "unity_save_as_prefab",
  "Saves a GameObject from the scene as a new prefab asset.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    savePath: z.string().describe("e.g. 'Prefabs/Enemy' (.prefab added automatically)"),
  },
  "save_as_prefab"
);

tool(
  "unity_set_build_settings_scenes",
  "Replaces the Build Settings scene list with the given scenes (all enabled).",
  { scenes: z.array(z.string()).describe("Scene asset paths, e.g. ['Assets/Scenes/Menu.unity', 'Assets/Scenes/Level1.unity']") },
  "set_build_settings_scenes"
);

// ---------------- v3: PLAY MODE ----------------
tool("unity_play", "Enters Play mode. Call unity_get_play_state FIRST - invoking this while already playing toggles the mode and wastes a reload. A domain reload follows, so wait briefly before sending further commands (menu items invoked during the load are dropped).", {}, "play");
tool("unity_stop", "Exits Play mode (stops the game).", {}, "stop");
tool(
  "unity_pause",
  "Pauses or resumes Play mode. Toggles the current state if 'paused' is omitted.",
  { paused: z.boolean().optional() },
  "pause"
);
tool("unity_step", "Advances Play mode by one frame (useful while paused).", {}, "step");
tool(
  "unity_get_play_state",
  "Returns editor state: isPlaying, isPaused, isCompiling, isUpdating. Call it before unity_play, and to confirm the state after entering or leaving Play mode.",
  {},
  "get_play_state"
);

// ---------------- v3: TEXTURE / SPRITE IMPORT ----------------
tool(
  "unity_set_texture_import_settings",
  "Changes a texture/sprite file's import settings and reimports it. The ones that matter most for 2D: textureType='Sprite', spriteMode='Single'|'Multiple', pixelsPerUnit, filterMode='Point' (crisp edges for pixel art).",
  {
    assetPath: z.string().describe("e.g. 'Assets/Sprites/hero.png'"),
    textureType: z.enum(["Default", "NormalMap", "Sprite", "Cursor", "Cookie", "Lightmap", "SingleChannel"]).optional(),
    spriteMode: z.enum(["Single", "Multiple", "Polygon"]).optional(),
    pixelsPerUnit: z.number().optional(),
    filterMode: z.enum(["Point", "Bilinear", "Trilinear"]).optional().describe("'Point' for pixel art"),
    wrapMode: z.enum(["Repeat", "Clamp", "Mirror", "MirrorOnce"]).optional(),
    maxTextureSize: z.number().optional(),
    compression: z.enum(["Uncompressed", "Compressed", "CompressedHQ", "CompressedLQ"]).optional(),
  },
  "set_texture_import_settings"
);

// ---------------- v3: TILEMAP ----------------
tool(
  "unity_create_tilemap",
  "Creates a Grid + Tilemap (with a TilemapRenderer) in the scene. Pass the returned tilemapInstanceId to unity_set_tiles.",
  {
    name: z.string().optional().describe("Grid object name (default 'Grid')"),
    tilemapName: z.string().optional().describe("Name of the Tilemap child (default 'Tilemap')"),
  },
  "create_tilemap"
);

tool(
  "unity_create_tile_asset",
  "Creates a Tile asset from a sprite (required before placing it on a Tilemap). For an atlas sub-asset use the 'Assets/atlas.png#Tile_0' form.",
  {
    sprite: z.string().describe("Sprite spec, e.g. 'Assets/Tiles/grass.png' or 'Assets/atlas.png#grass'"),
    assetPath: z.string().describe("e.g. 'Tiles/Grass' (.asset added automatically)"),
    color: z.array(z.number()).min(3).max(4).optional().describe("[r,g,b] or [r,g,b,a]"),
  },
  "create_tile_asset"
);

tool(
  "unity_set_tiles",
  "Places tiles into cells of a Tilemap. Give one shared tileAssetPath, or a tile per cell. Create the Tile first with unity_create_tile_asset.",
  {
    instanceId: z.number().optional().describe("instanceId of the object holding the Tilemap component"),
    path: z.string().optional(),
    tileAssetPath: z.string().optional().describe("Shared Tile asset path for all cells"),
    cells: z.array(z.object({
      x: z.number(),
      y: z.number(),
      z: z.number().optional(),
      tileAssetPath: z.string().optional().describe("Tile for this cell (overrides the shared one)"),
    })).describe("List of cells to fill"),
  },
  "set_tiles"
);

// ---------------- v3: ANIMATION / ANIMATOR ----------------
tool(
  "unity_create_sprite_animation",
  "Creates a 2D sprite animation clip (.anim) from a sequence of sprite frames (a SpriteRenderer sprite curve). For frame-by-frame animation such as character walk/run.",
  {
    assetPath: z.string().describe("e.g. 'Animations/Hero_Run' (.anim added automatically)"),
    sprites: z.array(z.string()).describe("Ordered list of sprite specs, e.g. ['Assets/hero.png#run_0', 'Assets/hero.png#run_1', ...]"),
    frameRate: z.number().optional().describe("Frames per second, default 12"),
    loop: z.boolean().optional().describe("Default true"),
  },
  "create_sprite_animation"
);

tool(
  "unity_create_animation_clip",
  "Creates a general animation clip: float curves on one or more properties. E.g. animate a Transform's position/scale or a color channel over time.",
  {
    assetPath: z.string().describe("e.g. 'Animations/DoorOpen' (.anim added automatically)"),
    frameRate: z.number().optional().describe("Default 60"),
    loop: z.boolean().optional(),
    curves: z.array(z.object({
      type: z.string().describe("Component type, e.g. 'Transform', 'SpriteRenderer'"),
      path: z.string().optional().describe("Path of the target child object (empty = root)"),
      property: z.string().describe("Serialized property path, e.g. 'm_LocalPosition.x', 'm_LocalScale.y'"),
      keys: z.array(z.object({ time: z.number(), value: z.number() })).describe("Keyframes (time in seconds, value)"),
    })).describe("At least one curve"),
  },
  "create_animation_clip"
);

tool(
  "unity_create_animator_controller",
  "Creates an Animator Controller: states (with clips), parameters and transitions. Then assign it to an object with unity_assign_animator_controller.",
  {
    assetPath: z.string().describe("e.g. 'Animators/Hero' (.controller added automatically)"),
    parameters: z.array(z.object({
      name: z.string(),
      type: z.enum(["Float", "Int", "Bool", "Trigger"]),
    })).optional(),
    states: z.array(z.object({
      name: z.string(),
      clip: z.string().optional().describe("AnimationClip asset path (.anim)"),
      default: z.boolean().optional().describe("Is this the default state"),
    })).optional(),
    transitions: z.array(z.object({
      from: z.string(),
      to: z.string(),
      hasExitTime: z.boolean().optional(),
      exitTime: z.number().optional(),
      condition: z.object({
        parameter: z.string(),
        mode: z.enum(["If", "IfNot", "Greater", "Less", "Equals", "NotEqual"]),
        threshold: z.number().optional(),
      }).optional(),
    })).optional(),
  },
  "create_animator_controller"
);

tool(
  "unity_assign_animator_controller",
  "Assigns an Animator Controller to an object in the scene (adds an Animator component if missing).",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    controllerPath: z.string().describe("e.g. 'Assets/Animators/Hero.controller'"),
  },
  "assign_animator_controller"
);

// ---------------- v4: ANIMATOR (blend tree / sub-state machine) ----------------
tool(
  "unity_create_blend_tree",
  "Adds a state with a blend tree to an existing Animator Controller. blendType='Simple1D' (single parameter) or a 2D variant. Missing blend parameters are added automatically as Float.",
  {
    controllerPath: z.string().describe("e.g. 'Assets/Animators/Hero.controller'"),
    name: z.string().optional().describe("State name (default 'BlendTree')"),
    layer: z.number().optional(),
    blendType: z.enum(["Simple1D", "SimpleDirectional2D", "FreeformDirectional2D", "FreeformCartesian2D", "Direct"]).optional(),
    blendParameter: z.string().optional().describe("Blend parameter for 1D (default 'Blend')"),
    blendParameterX: z.string().optional().describe("2D X parameter"),
    blendParameterY: z.string().optional().describe("2D Y parameter"),
    default: z.boolean().optional().describe("Make this the default state"),
    children: z.array(z.object({
      clip: z.string().describe("AnimationClip asset path"),
      threshold: z.number().optional().describe("1D threshold value"),
      x: z.number().optional().describe("2D X position"),
      y: z.number().optional().describe("2D Y position"),
    })).optional(),
  },
  "create_blend_tree"
);

tool(
  "unity_add_animator_sub_state_machine",
  "Adds a sub-state machine to a layer of an Animator Controller (with its own states, default state and internal transitions).",
  {
    controllerPath: z.string(),
    name: z.string().optional().describe("Sub-state machine name"),
    layer: z.number().optional(),
    states: z.array(z.object({
      name: z.string(),
      clip: z.string().optional(),
      default: z.boolean().optional(),
    })).optional(),
    transitions: z.array(z.object({
      from: z.string(),
      to: z.string(),
      hasExitTime: z.boolean().optional(),
      exitTime: z.number().optional(),
      condition: z.object({
        parameter: z.string(),
        mode: z.enum(["If", "IfNot", "Greater", "Less", "Equals", "NotEqual"]),
        threshold: z.number().optional(),
      }).optional(),
    })).optional(),
  },
  "add_animator_sub_state_machine"
);

// ---------------- v4: RULE TILE ----------------
tool(
  "unity_create_rule_tile",
  "Creates an auto-tiling RuleTile asset. Requires the com.unity.2d.tilemap.extras package. Each rule: one sprite + a mask of 8 neighbours (0=any, 1=must be this tile, 2=must not be this tile).",
  {
    assetPath: z.string().describe("e.g. 'Tiles/GroundRule' (.asset added automatically)"),
    defaultSprite: z.string().optional().describe("Sprite used when no rule matches"),
    rules: z.array(z.object({
      sprite: z.string().describe("Sprite shown when this rule matches"),
      neighbors: z.array(z.number()).length(8).describe("8 neighbours: [TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight] - each 0/1/2"),
    })).optional(),
  },
  "create_rule_tile"
);

// ---------------- v4: PARTICLE SYSTEM ----------------
tool(
  "unity_create_particle_system",
  "Creates a new ParticleSystem (or adds one to an existing object) and applies common settings. If instanceId/path is given it is added to that object; otherwise a new GameObject is created.",
  {
    name: z.string().optional(),
    instanceId: z.number().optional(),
    path: z.string().optional(),
    duration: z.number().optional(),
    looping: z.boolean().optional(),
    startLifetime: z.number().optional(),
    startSpeed: z.number().optional(),
    startSize: z.number().optional(),
    startColor: z.array(z.number()).min(3).max(4).optional().describe("[r,g,b] or [r,g,b,a]"),
    gravityModifier: z.number().optional(),
    maxParticles: z.number().optional(),
    rateOverTime: z.number().optional().describe("Particles emitted per second"),
    shapeType: z.enum(["Sphere", "Hemisphere", "Cone", "Box", "Circle", "Edge", "Rectangle"]).optional(),
    material: z.string().optional().describe("Material asset path"),
  },
  "create_particle_system"
);

// ---------------- v4: TERRAIN ----------------
tool(
  "unity_create_terrain",
  "Creates a new Terrain (+ TerrainData asset).",
  {
    assetPath: z.string().optional().describe("TerrainData path (default 'Terrain/TerrainData')"),
    name: z.string().optional(),
    heightmapResolution: z.number().optional().describe("Must be 2^n+1 (33, 65, ..., 513, 1025). Default 513"),
    width: z.number().optional().describe("Default 500"),
    height: z.number().optional().describe("Maximum height, default 600"),
    length: z.number().optional().describe("Default 500"),
  },
  "create_terrain"
);

tool(
  "unity_set_terrain_heights",
  "Sets a Terrain's heightmap. uniform: a single height for the whole terrain (0-1). heights: a normalized 2D array (0-1), resampled to the terrain resolution.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    uniform: z.number().optional().describe("Flat height, 0-1"),
    heights: z.array(z.array(z.number())).optional().describe("2D height array (each value 0-1)"),
  },
  "set_terrain_heights"
);

tool(
  "unity_add_terrain_layer",
  "Adds a texture layer (TerrainLayer) to a Terrain.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    texture: z.string().describe("Diffuse texture asset path"),
    assetPath: z.string().optional().describe("TerrainLayer path (default 'Terrain/Layer')"),
    tileSize: z.array(z.number()).length(2).optional().describe("[x,y] tile size"),
  },
  "add_terrain_layer"
);

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`[unity-mcp] hazir — Unity: ${UNITY_HOST}:${UNITY_PORT}`);
