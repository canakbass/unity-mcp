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
      reject(new Error("Unity yanit vermedi (60sn). Editor acik mi? Derleme surüyor olabilir — biraz bekleyip tekrar dene."));
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
        reject(new Error("Unity'den bozuk yanit: " + e.message));
      }
    });
    socket.on("error", (e) => {
      clearTimeout(timeout);
      reject(new Error(
        e.code === "ECONNREFUSED"
          ? "Unity Editor'e baglanilamadi. Unity acik mi ve McpBridge.cs projede mi? (Tools > MCP Bridge > Restart Server)"
          : e.message
      ));
    });
  });
}

const server = new McpServer({ name: "unity-bridge", version: "1.0.0" });

function textResult(data) {
  return { content: [{ type: "text", text: typeof data === "string" ? data : JSON.stringify(data, null, 2) }] };
}

function tool(name, description, schema, method, mapParams = (a) => a) {
  server.registerTool(name, { description, inputSchema: schema }, async (args) => {
    try {
      return textResult(await callUnity(method, mapParams(args)));
    } catch (e) {
      return { content: [{ type: "text", text: "HATA: " + e.message }], isError: true };
    }
  });
}

const vec3 = z.array(z.number()).length(3);

// ---------------- SAHNE OKUMA ----------------
tool(
  "unity_get_scene",
  "Yuklu TUM sahnelerin hiyerarsisini dondurur (nesneler, instanceId'ler, pozisyonlar, component listeleri). Prefab modu acikken prefabin icerigini dondurur. Degisiklik yapmadan once mutlaka cagir.",
  {},
  "get_scene"
);

tool(
  "unity_get_object",
  "Tek bir nesnenin detayini dondurur: tum component'ler ve serialize edilmis ozellikleri (propertyPath'leriyle). set_property cagirmadan once dogru propertyPath'i bulmak icin kullan.",
  {
    instanceId: z.number().optional().describe("get_scene'den gelen instanceId"),
    path: z.string().optional().describe("Hiyerarsi yolu, orn: 'Environment/Tree_01'"),
  },
  "get_object"
);

// ---------------- NESNE ISLEMLERI ----------------
tool(
  "unity_create_object",
  "Sahnede yeni GameObject olusturur. primitive verilirse (Cube, Sphere, Capsule, Cylinder, Plane, Quad) hazir mesh ile gelir.",
  {
    name: z.string(),
    primitive: z.enum(["Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad"]).optional(),
    parent: z.string().optional().describe("Ebeveyn nesnenin hiyerarsi yolu"),
    position: vec3.optional(),
    rotation: vec3.optional().describe("Euler acilari"),
    scale: vec3.optional(),
  },
  "create_object"
);

tool(
  "unity_delete_object",
  "Sahnedeki bir nesneyi siler (Undo destekli).",
  { instanceId: z.number().optional(), path: z.string().optional() },
  "delete_object"
);

tool(
  "unity_set_transform",
  "Nesnenin pozisyon/rotasyon/olcegini degistirir.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    position: vec3.optional(),
    rotation: vec3.optional().describe("Euler acilari"),
    scale: vec3.optional(),
    space: z.enum(["world", "local"]).optional().describe("Varsayilan: world"),
  },
  "set_transform"
);

// ---------------- COMPONENT ISLEMLERI ----------------
tool(
  "unity_add_component",
  "Nesneye component ekler. Tip adi kisa ('Rigidbody') veya tam ('UnityEngine.Rigidbody') olabilir; kendi scriptlerin de calisir.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    componentType: z.string(),
  },
  "add_component"
);

tool(
  "unity_remove_component",
  "Nesneden component kaldirir.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    componentType: z.string(),
  },
  "remove_component"
);

tool(
  "unity_set_property",
  "Bir component'in VEYA asset'in (ScriptableObject, prefab) serialize edilmis ozelligini degistirir. Dogru propertyPath icin once unity_get_object / unity_get_asset cagir (orn: 'm_Mass', 'm_Intensity'). Deger tipleri: sayi, bool, string, [x,y,z] vektor, [r,g,b,a] renk, enum adi. Nesne referanslari: asset yolu ('Assets/...'), sprite gibi alt-asset icin 'Assets/atlas.png#SpriteAdi', veya instanceId.",
  {
    componentInstanceId: z.number().optional().describe("Dogrudan component instanceId (tercih edilen)"),
    assetPath: z.string().optional().describe("Hedef bir asset ise (orn. ScriptableObject) asset yolu"),
    instanceId: z.number().optional().describe("GameObject instanceId (componentType ile birlikte)"),
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
  "Projede asset arar. Filtre ornekleri: 't:Prefab agac', 't:Material', 't:Scene', 't:Script Player'. Asset yollarini dondurur.",
  { filter: z.string(), limit: z.number().optional() },
  "find_assets"
);

tool(
  "unity_instantiate_prefab",
  "Prefab'i sahneye yerlestirir. assetPath icin once unity_find_assets kullan.",
  {
    assetPath: z.string().describe("orn: 'Assets/Prefabs/Tree.prefab'"),
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
  "Assets altina C# script yazar ve derlemeyi baslatir. Yazdiktan sonra unity_read_console ile derleme hatasi var mi kontrol et. Var olan dosyanin ustune yazar.",
  {
    path: z.string().describe("Assets'e gore yol, orn: 'Scripts/PlayerController.cs'"),
    content: z.string().describe("Dosyanin tam C# icerigi"),
  },
  "create_script"
);

tool(
  "unity_read_file",
  "Assets altindaki bir metin dosyasini okur (script duzenlemeden once mevcut icerigi gormek icin).",
  { path: z.string().describe("Assets'e gore yol") },
  "read_file"
);

// ---------------- DURUM / KONTROL ----------------
tool(
  "unity_read_console",
  "Unity konsolundaki son loglari dondurur. Script yazdiktan veya islem yaptiktan sonra hata kontrolu icin cagir. type: Error | Warning | Log",
  {
    limit: z.number().optional(),
    type: z.enum(["Error", "Warning", "Log"]).optional(),
    clear: z.boolean().optional(),
  },
  "read_console"
);

tool("unity_save_scene", "Acik sahneleri kaydeder.", {}, "save_scene");

// ---------------- v2: MATERIAL ----------------
tool(
  "unity_create_material",
  "Yeni Material asset'i olusturur. Shader belirtilmezse projeye uygun 2D shader secilir (URP 2D veya Sprites/Default).",
  {
    path: z.string().describe("orn: 'Materials/PlayerMat' (.mat otomatik eklenir)"),
    shader: z.string().optional().describe("orn: 'Sprites/Default', 'UI/Default'"),
    color: z.array(z.number()).min(3).max(4).optional().describe("[r,g,b] veya [r,g,b,a], 0-1 arasi"),
  },
  "create_material"
);

tool(
  "unity_set_material",
  "Material ozelliklerini toplu degistirir. properties objesinde: [r,g,b,a] -> renk, sayi -> float, string -> texture asset yolu. Ornek: {\"_Color\": [1,0,0,1], \"_Glossiness\": 0.2, \"_MainTex\": \"Assets/Textures/wood.png\"}. Dogru property adlari icin once unity_get_material cagir.",
  {
    path: z.string().describe("Material asset yolu"),
    properties: z.record(z.any()).optional(),
    shader: z.string().optional().describe("Shader'i degistirmek icin"),
  },
  "set_material"
);

tool(
  "unity_get_material",
  "Material'in shader'ini ve tum property'lerini (adlari, tipleri, mevcut degerleri) dondurur.",
  { path: z.string() },
  "get_material"
);

// ---------------- v2: SCRIPTABLEOBJECT / ASSET ----------------
tool(
  "unity_create_scriptable_object",
  "ScriptableObject asset'i olusturur (tip once unity_create_script ile tanimlanmis ve derlenmis olmali). Alanlarini doldurmak icin unity_set_property'yi assetPath parametresiyle cagir.",
  {
    typeName: z.string().describe("SO sinif adi, orn: 'EnemyData'"),
    assetPath: z.string().describe("orn: 'Data/Goblin' (.asset otomatik eklenir)"),
  },
  "create_scriptable_object"
);

tool(
  "unity_get_asset",
  "Herhangi bir asset'in (ScriptableObject, prefab vb.) serialize edilmis ozelliklerini ve alt-asset'lerini (sprite atlas iceriği gibi) dondurur.",
  { assetPath: z.string() },
  "get_asset"
);

// ---------------- v2: COKLU SAHNE ----------------
tool(
  "unity_list_scenes",
  "Yuklu sahneleri (aktif/dirty durumlariyla) ve projedeki tum sahne dosyalarini listeler.",
  {},
  "list_scenes"
);

tool(
  "unity_open_scene",
  "Sahne acar. additive=true ile mevcut sahnelerin yanina yukler (coklu sahne duzenleme).",
  {
    path: z.string().describe("orn: 'Assets/Scenes/Level1.unity'"),
    additive: z.boolean().optional(),
    saveCurrent: z.boolean().optional().describe("Acmadan once mevcutlari kaydet (varsayilan true)"),
  },
  "open_scene"
);

tool(
  "unity_new_scene",
  "Yeni sahne olusturur. savePath verilirse hemen diske kaydeder.",
  {
    savePath: z.string().optional().describe("orn: 'Scenes/MainMenu' (.unity otomatik)"),
    additive: z.boolean().optional(),
    empty: z.boolean().optional().describe("true: tamamen bos; false: kamera+isikla (varsayilan)"),
  },
  "new_scene"
);

tool(
  "unity_close_scene",
  "Yuklu bir sahneyi kapatir (en az bir sahne acik kalmali).",
  { path: z.string().describe("Sahne yolu veya adi"), save: z.boolean().optional() },
  "close_scene"
);

tool(
  "unity_set_active_scene",
  "Aktif sahneyi degistirir (yeni nesneler aktif sahneye eklenir).",
  { path: z.string().describe("Sahne yolu veya adi") },
  "set_active_scene"
);

// ---------------- v2: PREFAB MODU ----------------
tool(
  "unity_open_prefab",
  "Prefab'i duzenleme modunda acar. Actiktan sonra tum nesne/component/property komutlari prefabin ICINDE calisir. Bitince unity_close_prefab cagirmayi unutma.",
  { assetPath: z.string().describe("orn: 'Assets/Prefabs/Enemy.prefab'") },
  "open_prefab"
);

tool(
  "unity_save_prefab",
  "Prefab modundaki degisiklikleri asset'e kaydeder (mod acik kalir).",
  {},
  "save_prefab"
);

tool(
  "unity_close_prefab",
  "Prefab modundan cikar. save=false verilmezse degisiklikleri kaydeder.",
  { save: z.boolean().optional() },
  "close_prefab"
);

// ---------------- v2: EKRAN GORUNTUSU ----------------
server.registerTool(
  "unity_capture_screenshot",
  {
    description:
      "Sahnenin goruntusunu PNG olarak alir ve gorsel olarak dondurur — yaptigin degisikligi GOZLE dogrulamak icin kullan (ozellikle UI yerlesimi ve 2D sahnelerde). view: 'game' ana kameradan render alir (Screen Space Overlay canvas'lar dahil), 'scene' Scene View kamerasindan alir.",
    inputSchema: {
      view: z.enum(["game", "scene"]).optional().describe("Varsayilan: game"),
      width: z.number().optional().describe("Varsayilan 960, max 1920"),
      height: z.number().optional().describe("Varsayilan 540, max 1080"),
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
      return { content: [{ type: "text", text: "HATA: " + e.message }], isError: true };
    }
  }
);

tool(
  "unity_execute_menu",
  "Unity menu komutunu calistirir, orn: 'GameObject/Light/Directional Light', 'File/Save Project'. Diger tool'larin kapsamadigi isler icin.",
  { path: z.string() },
  "execute_menu"
);

// ---------------- v2.1: PREFAB / BUILD ----------------
tool(
  "unity_save_as_prefab",
  "Sahnedeki bir GameObject'i yeni bir prefab asset olarak kaydeder.",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    savePath: z.string().describe("orn: 'Prefabs/Enemy' (.prefab otomatik eklenir)"),
  },
  "save_as_prefab"
);

tool(
  "unity_set_build_settings_scenes",
  "Build Settings sahne listesini verilen sahnelerle degistirir (hepsi etkin/enabled).",
  { scenes: z.array(z.string()).describe("Sahne asset yollari, orn ['Assets/Scenes/Menu.unity', 'Assets/Scenes/Level1.unity']") },
  "set_build_settings_scenes"
);

// ---------------- v3: PLAY MODE ----------------
tool("unity_play", "Editor'u Play mode'a sokar (oyunu baslatir). Not: domain reload olabileceginden hemen ardindan gelen komutlar icin kisa bekleme gerekebilir.", {}, "play");
tool("unity_stop", "Play mode'dan cikar (oyunu durdurur).", {}, "stop");
tool(
  "unity_pause",
  "Play mode'u duraklatir/devam ettirir. 'paused' verilmezse mevcut durumu tersine cevirir.",
  { paused: z.boolean().optional() },
  "pause"
);
tool("unity_step", "Play mode'da bir kare ilerletir (duraklatilmisken faydali).", {}, "step");
tool(
  "unity_get_play_state",
  "Editor durumunu dondurur: isPlaying, isPaused, isCompiling, isUpdating. Play mode'a girdikten/ciktiktan sonra durumu dogrulamak icin kullan.",
  {},
  "get_play_state"
);

// ---------------- v3: TEXTURE / SPRITE IMPORT ----------------
tool(
  "unity_set_texture_import_settings",
  "Bir texture/sprite dosyasinin import ayarlarini degistirir ve yeniden import eder. 2D icin en onemlileri: textureType='Sprite', spriteMode='Single'|'Multiple', pixelsPerUnit, filterMode='Point' (pixel art icin net kenarlar).",
  {
    assetPath: z.string().describe("orn: 'Assets/Sprites/hero.png'"),
    textureType: z.enum(["Default", "NormalMap", "Sprite", "Cursor", "Cookie", "Lightmap", "SingleChannel"]).optional(),
    spriteMode: z.enum(["Single", "Multiple", "Polygon"]).optional(),
    pixelsPerUnit: z.number().optional(),
    filterMode: z.enum(["Point", "Bilinear", "Trilinear"]).optional().describe("Pixel art icin 'Point'"),
    wrapMode: z.enum(["Repeat", "Clamp", "Mirror", "MirrorOnce"]).optional(),
    maxTextureSize: z.number().optional(),
    compression: z.enum(["Uncompressed", "Compressed", "CompressedHQ", "CompressedLQ"]).optional(),
  },
  "set_texture_import_settings"
);

// ---------------- v3: TILEMAP ----------------
tool(
  "unity_create_tilemap",
  "Sahneye bir Grid + Tilemap (TilemapRenderer'li) olusturur. Donen tilemapInstanceId'yi unity_set_tiles icin kullan.",
  {
    name: z.string().optional().describe("Grid nesnesi adi (varsayilan 'Grid')"),
    tilemapName: z.string().optional().describe("Tilemap cocugunun adi (varsayilan 'Tilemap')"),
  },
  "create_tilemap"
);

tool(
  "unity_create_tile_asset",
  "Bir sprite'tan Tile asset'i olusturur (Tilemap'e yerlestirmek icin gereken kaynak). Atlas alt-asset'i icin 'Assets/atlas.png#Tile_0' bicimini kullan.",
  {
    sprite: z.string().describe("Sprite spec, orn 'Assets/Tiles/grass.png' veya 'Assets/atlas.png#grass'"),
    assetPath: z.string().describe("orn: 'Tiles/Grass' (.asset otomatik eklenir)"),
    color: z.array(z.number()).min(3).max(4).optional().describe("[r,g,b] veya [r,g,b,a]"),
  },
  "create_tile_asset"
);

tool(
  "unity_set_tiles",
  "Bir Tilemap uzerinde hucrelere tile yerlestirir. Ortak bir tileAssetPath verebilir ya da her hucrede ayri belirtebilirsin. Once unity_create_tile_asset ile Tile olustur.",
  {
    instanceId: z.number().optional().describe("Tilemap component'i olan nesnenin instanceId'si"),
    path: z.string().optional(),
    tileAssetPath: z.string().optional().describe("Tum hucreler icin ortak Tile asset yolu"),
    cells: z.array(z.object({
      x: z.number(),
      y: z.number(),
      z: z.number().optional(),
      tileAssetPath: z.string().optional().describe("Bu hucreye ozel Tile (ortak olani ezer)"),
    })).describe("Yerlestirilecek hucreler listesi"),
  },
  "set_tiles"
);

// ---------------- v3: ANIMATION / ANIMATOR ----------------
tool(
  "unity_create_sprite_animation",
  "Sprite kareleri dizisinden 2D sprite animasyon klip'i (.anim) olusturur (SpriteRenderer sprite curve'u). Karakter yuruyus/kosma gibi frame-by-frame animasyonlar icin.",
  {
    assetPath: z.string().describe("orn: 'Animations/Hero_Run' (.anim otomatik)"),
    sprites: z.array(z.string()).describe("Sirali sprite spec listesi, orn ['Assets/hero.png#run_0', 'Assets/hero.png#run_1', ...]"),
    frameRate: z.number().optional().describe("Kare/sn, varsayilan 12"),
    loop: z.boolean().optional().describe("Varsayilan true"),
  },
  "create_sprite_animation"
);

tool(
  "unity_create_animation_clip",
  "Genel animasyon klip'i olusturur: bir veya daha cok property'ye float curve. Orn Transform pozisyonunu/olcegini veya bir renk kanalini zamanla degistir.",
  {
    assetPath: z.string().describe("orn: 'Animations/DoorOpen' (.anim otomatik)"),
    frameRate: z.number().optional().describe("Varsayilan 60"),
    loop: z.boolean().optional(),
    curves: z.array(z.object({
      type: z.string().describe("Component tipi, orn 'Transform', 'SpriteRenderer'"),
      path: z.string().optional().describe("Hedef cocuk nesnenin yolu (bos = kok)"),
      property: z.string().describe("Serialize property yolu, orn 'm_LocalPosition.x', 'm_LocalScale.y'"),
      keys: z.array(z.object({ time: z.number(), value: z.number() })).describe("Keyframe'ler (saniye, deger)"),
    })).describe("En az bir curve"),
  },
  "create_animation_clip"
);

tool(
  "unity_create_animator_controller",
  "Animator Controller olusturur: state'ler (klip'lerle), parametreler ve gecisler. Sonra unity_assign_animator_controller ile bir nesneye ata.",
  {
    assetPath: z.string().describe("orn: 'Animators/Hero' (.controller otomatik)"),
    parameters: z.array(z.object({
      name: z.string(),
      type: z.enum(["Float", "Int", "Bool", "Trigger"]),
    })).optional(),
    states: z.array(z.object({
      name: z.string(),
      clip: z.string().optional().describe("AnimationClip asset yolu (.anim)"),
      default: z.boolean().optional().describe("Baslangic (default) state'i mi"),
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
  "Bir Animator Controller'i sahnedeki nesneye atar (Animator component'i yoksa otomatik ekler).",
  {
    instanceId: z.number().optional(),
    path: z.string().optional(),
    controllerPath: z.string().describe("orn: 'Assets/Animators/Hero.controller'"),
  },
  "assign_animator_controller"
);

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`[unity-mcp] hazir — Unity: ${UNITY_HOST}:${UNITY_PORT}`);
