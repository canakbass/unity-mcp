# Unity MCP Bridge

Let **Claude Code** and **Antigravity** drive the Unity Editor directly: see the scene, create/move/delete objects, edit components and their properties, place prefabs, write C# scripts and read/fix compile errors, control **Play mode**, and build **tilemaps**, **animations** and **animator controllers** — all from natural language. **No API key required** — it works with your existing subscription.

> Compatible with **Unity 6.2+ (EntityId API)** as well as older versions — see [Unity version compatibility](#unity-version-compatibility).

**Languages:** [English](#english) · [Türkçe](#türkçe)

---

## English

### Why this exists

Describe what you want in plain language and an AI agent builds it inside Unity — laying out UI, wiring components, generating scripts and fixing their compile errors, arranging 2D levels, and **verifying the result visually via screenshots the model can actually see.** Work that takes hours by hand often takes minutes.

### Architecture

```
Claude Code / Antigravity  ─MCP(stdio)→  server.js  ─TCP:6400→  Unity Editor (McpBridge.cs)
```

- `McpBridge.cs` runs inside the Unity Editor and listens on `127.0.0.1:6400`, executing commands on the main thread (Undo-aware).
- `server.js` is a Node MCP server that exposes `unity_*` tools and forwards them to Unity over TCP.

### Requirements

- **Unity 2021.2+** (tested up to **Unity 6.x**). Prefab-mode APIs need 2021.2+.
- **Node.js 18+**
- The **Newtonsoft JSON** package (`com.unity.nuget.newtonsoft-json`) — already present in most projects.

### Installation

#### 1) Unity side
1. Copy `UnityPackage/Editor/McpBridge.cs` into your project at `Assets/Editor/McpBridge.cs`.
2. Make sure Newtonsoft JSON is installed. If not: **Window > Package Manager > + > Add package by name** → `com.unity.nuget.newtonsoft-json`.
3. After it compiles, the Console should show: `[MCP Bridge] Dinleniyor: 127.0.0.1:6400`. If not: **Tools > MCP Bridge > Restart Server**.

#### 2) MCP server
```bash
git clone https://github.com/canakbass/unity-mcp.git
cd unity-mcp/mcp-server
npm install
```

#### 3) Connect to Claude Code
```bash
# run from the mcp-server directory:
claude mcp add unity -s user -- node "$(pwd)/server.js"
```
Verify with `claude mcp list` → `unity` should show **✔ Connected**.

#### 4) Connect to Antigravity
Add this to Antigravity's MCP config (via the Agent panel → MCP servers → *view raw config*):
```json
{
  "mcpServers": {
    "unity": {
      "command": "node",
      "args": ["/path/to/unity-mcp/mcp-server/server.js"]
    }
  }
}
```

### Tool list

**Perception & reading**

| Tool | Purpose |
|---|---|
| `unity_get_scene` | Scene hierarchy + instanceIds |
| `unity_get_object` | Full component/property dump of an object (incl. propertyPaths) |
| `unity_get_material` / `unity_get_asset` | Read a material's / any asset's fields (sub-assets included) |
| `unity_find_assets` | Search project assets (`t:Prefab tree`, `t:Material`, …) |
| `unity_read_file` | Read a C# / text file under `Assets` |
| `unity_read_console` | Read console logs / compile errors |
| `unity_capture_screenshot` | Capture the scene/game as PNG; the model **sees** the image (Screen Space Overlay UI included) |

**Scene & object editing**

| Tool | Purpose |
|---|---|
| `unity_create_object` | Create empty or primitive GameObject |
| `unity_delete_object` | Delete an object (Undo-aware) |
| `unity_set_transform` | Position / rotation / scale (world or local) |
| `unity_instantiate_prefab` | Place a prefab into the scene |
| `unity_add_component` / `unity_remove_component` | Add/remove components (your own scripts too) |
| `unity_set_property` | Change any component/asset property (via SerializedObject) |
| `unity_save_as_prefab` | Save a scene object as a new prefab asset |

**Code**

| Tool | Purpose |
|---|---|
| `unity_create_script` | Write a C# script and trigger compilation |
| `unity_read_console` | Read compile errors to fix them (write → check → fix loop) |

**Materials**

| Tool | Purpose |
|---|---|
| `unity_create_material` / `unity_set_material` / `unity_get_material` | Create materials, read/set properties (color, float, texture) |

**ScriptableObjects & data**

| Tool | Purpose |
|---|---|
| `unity_create_scriptable_object` | Create a ScriptableObject asset |
| `unity_get_asset` | Read any asset's serialized fields |

**Scene management**

| Tool | Purpose |
|---|---|
| `unity_list_scenes` / `unity_open_scene` / `unity_new_scene` / `unity_close_scene` / `unity_set_active_scene` / `unity_save_scene` | Full multi-scene management (additive loading included) |
| `unity_set_build_settings_scenes` | Set the Build Settings scene list |

**Prefab mode**

| Tool | Purpose |
|---|---|
| `unity_open_prefab` / `unity_save_prefab` / `unity_close_prefab` | Edit inside prefab mode — while open, all commands run inside the prefab |

**Play mode** *(new in v3)*

| Tool | Purpose |
|---|---|
| `unity_play` / `unity_stop` | Enter / exit Play mode |
| `unity_pause` / `unity_step` | Pause/resume, step one frame |
| `unity_get_play_state` | `isPlaying`, `isPaused`, `isCompiling`, `isUpdating` |

**2D: Tilemap** *(new in v3)*

| Tool | Purpose |
|---|---|
| `unity_create_tilemap` | Create a Grid + Tilemap |
| `unity_create_tile_asset` | Create a Tile asset from a sprite |
| `unity_set_tiles` | Paint tiles onto a Tilemap at cell coordinates |

**2D: Sprite import** *(new in v3)*

| Tool | Purpose |
|---|---|
| `unity_set_texture_import_settings` | Texture type, sprite mode, Pixels Per Unit, filter mode, … (+ reimport) |

**Animation** *(new in v3)*

| Tool | Purpose |
|---|---|
| `unity_create_sprite_animation` | Frame-by-frame 2D sprite clip from a list of sprites |
| `unity_create_animation_clip` | Generic clip with float curves on any property |
| `unity_create_animator_controller` | Controller with states (from clips), parameters and transitions |
| `unity_assign_animator_controller` | Attach a controller to an object's Animator |

**Misc**

| Tool | Purpose |
|---|---|
| `unity_execute_menu` | Run any Unity menu command |

### Example prompts

- "Look at the scene, lay out a 5×5 grid of cubes on the ground, add a Rigidbody to each."
- "Write a PlayerController script, add it to Player, and fix any compile errors."
- "Build a MainMenu scene: a Canvas with a centered title and 3 buttons — take a screenshot and check the alignment."
- "Set the sprites in Assets/Hero to Point filter and 32 pixels-per-unit, then make a run animation from run_0…run_5 at 10 fps."
- "Create a tilemap and paint a 10×3 ground row using the grass tile."
- "Create an Animator with Idle/Run states and a Bool 'isRunning' transition, then assign it to Player."
- "Enter play mode, wait a moment, screenshot the game view, then stop."

### 2D / UI tips

- **Assign a sprite:** pass `Assets/Sprites/atlas.png#SpriteName` to `unity_set_property` — sub-assets resolve automatically.
- **ScriptableObject fields:** call `unity_set_property` with the `assetPath` parameter.
- **UI verification:** after layout work, call `unity_capture_screenshot`; Screen Space Overlay canvases are included (settings auto-restored afterward).
- **Pixel art:** use `unity_set_texture_import_settings` with `filterMode: "Point"`.

### Unity version compatibility

Unity **6.2** turned `Object.GetInstanceID()` / `EditorUtility.InstanceIDToObject()` — and even the `EntityId ↔ int` conversions — into **hard compile errors**. This bridge stays compatible across versions by resolving instance IDs through those APIs at runtime, preserving the classic integer `instanceId` used by the protocol. It compiles cleanly on both **Unity 6.x** and older versions.

> Using Unity 2020? Add `using UnityEditor.Experimental.SceneManagement;` at the top of `McpBridge.cs`.

### Troubleshooting

- **"Couldn't connect to Unity Editor"** → Is Unity open? Is the bridge message in the Console? **Tools > MCP Bridge > Restart Server**.
- **Timeout during compilation** → Unity can't answer while compiling scripts; wait a few seconds and retry (60s timeout).
- **Port conflict** → Set the `Port` constant in `McpBridge.cs` and the `UNITY_MCP_PORT` env var on the server to the same value.
- **set_property "property not found"** → Call `unity_get_object` first; Unity uses internal names (e.g. `m_Mass`, `m_Intensity`).

### Roadmap (v4 ideas)

- Animator sub-state machines & blend trees
- Tile Palette / rule tiles
- Particle system authoring
- Terrain tools

### License

MIT — see `LICENSE`.

---

## Türkçe

**Claude Code** ve **Antigravity**'nin Unity Editor'ü doğrudan kontrol etmesini sağlar: sahneyi görür, nesne oluşturur/taşır/siler, component ve özelliklerini değiştirir, prefab yerleştirir, C# script yazıp derleme hatalarını okuyup düzeltir, **Play mode**'u kontrol eder ve **tilemap**, **animasyon**, **animator controller** kurar — hepsi doğal dille. **API anahtarı gerekmez** — mevcut aboneliğinle çalışır.

> **Unity 6.2+ (EntityId API)** ve eski sürümlerle uyumlu — bkz. [Unity sürüm uyumu](#unity-sürüm-uyumu).

### Neden var?

Ne istediğini normal cümlelerle anlat, bir yapay zeka ajanı Unity içinde onu kursun — UI dizsin, component bağlasın, script üretip derleme hatalarını düzeltsin, 2D level tasarlasın ve **sonucu modelin gerçekten gördüğü ekran görüntüleriyle doğrulasın.** Elle saatler süren iş çoğu zaman dakikalara iner.

### Mimari

```
Claude Code / Antigravity  ─MCP(stdio)→  server.js  ─TCP:6400→  Unity Editor (McpBridge.cs)
```

- `McpBridge.cs` Unity Editor içinde çalışır, `127.0.0.1:6400`'ü dinler, komutları ana thread'de işler (Undo destekli).
- `server.js` `unity_*` araçlarını sunan bir Node MCP sunucusudur; komutları TCP üzerinden Unity'e iletir.

### Gereksinimler

- **Unity 2021.2+** (**Unity 6.x**'e kadar test edildi). Prefab modu API'leri için 2021.2+ gerekir.
- **Node.js 18+**
- **Newtonsoft JSON** paketi (`com.unity.nuget.newtonsoft-json`) — çoğu projede zaten vardır.

### Kurulum

#### 1) Unity tarafı
1. `UnityPackage/Editor/McpBridge.cs` dosyasını projene kopyala: `Assets/Editor/McpBridge.cs`.
2. Newtonsoft JSON paketinin kurulu olduğundan emin ol. Yoksa: **Window > Package Manager > + > Add package by name** → `com.unity.nuget.newtonsoft-json`.
3. Derleme bitince Console'da şunu görmelisin: `[MCP Bridge] Dinleniyor: 127.0.0.1:6400`. Görünmüyorsa: **Tools > MCP Bridge > Restart Server**.

#### 2) MCP sunucusu
```bash
git clone https://github.com/canakbass/unity-mcp.git
cd unity-mcp/mcp-server
npm install
```

#### 3) Claude Code'a bağlama
```bash
# mcp-server dizininden çalıştır:
claude mcp add unity -s user -- node "$(pwd)/server.js"
```
Kontrol: `claude mcp list` → `unity` **✔ Connected** görünmeli.

#### 4) Antigravity'e bağlama
Antigravity'nin MCP config'ine ekle (Agent paneli → MCP servers → *view raw config*):
```json
{
  "mcpServers": {
    "unity": {
      "command": "node",
      "args": ["/tam/yol/unity-mcp/mcp-server/server.js"]
    }
  }
}
```

### Araç listesi

**Görme & okuma:** `unity_get_scene`, `unity_get_object`, `unity_get_material`, `unity_get_asset`, `unity_find_assets`, `unity_read_file`, `unity_read_console`, `unity_capture_screenshot` (model görüntüyü **görür**).

**Sahne & nesne:** `unity_create_object`, `unity_delete_object`, `unity_set_transform`, `unity_instantiate_prefab`, `unity_add_component`, `unity_remove_component`, `unity_set_property`, `unity_save_as_prefab`.

**Kod:** `unity_create_script`, `unity_read_console` (yaz → kontrol et → düzelt döngüsü).

**Materyal:** `unity_create_material`, `unity_set_material`, `unity_get_material`.

**ScriptableObject / veri:** `unity_create_scriptable_object`, `unity_get_asset`.

**Sahne yönetimi:** `unity_list_scenes`, `unity_open_scene`, `unity_new_scene`, `unity_close_scene`, `unity_set_active_scene`, `unity_save_scene`, `unity_set_build_settings_scenes`.

**Prefab modu:** `unity_open_prefab`, `unity_save_prefab`, `unity_close_prefab`.

**Play mode (v3):** `unity_play`, `unity_stop`, `unity_pause`, `unity_step`, `unity_get_play_state`.

**2D — Tilemap (v3):** `unity_create_tilemap`, `unity_create_tile_asset`, `unity_set_tiles`.

**2D — Sprite import (v3):** `unity_set_texture_import_settings` (Pixels Per Unit, sprite mode, filter mode…).

**Animasyon (v3):** `unity_create_sprite_animation`, `unity_create_animation_clip`, `unity_create_animator_controller`, `unity_assign_animator_controller`.

**Joker:** `unity_execute_menu` (herhangi bir Unity menü komutu).

### Örnek istekler

- "Sahneye bak, zemine 5×5 küp diz, her birine Rigidbody ekle."
- "PlayerController scripti yaz, Player nesnesine ekle, derleme hatası varsa düzelt."
- "MainMenu sahnesi kur: ortalanmış başlık + 3 buton içeren bir Canvas — ekran görüntüsü alıp hizalamayı kontrol et."
- "Assets/Hero içindeki sprite'ları Point filtre ve 32 pixels-per-unit yap, sonra run_0…run_5'ten 10 fps'lik koşma animasyonu oluştur."
- "Bir tilemap oluştur ve grass tile ile 10×3'lük zemin sırası boya."
- "Idle/Run state'li, 'isRunning' Bool geçişli bir Animator kur ve Player'a ata."
- "Play mode'a gir, biraz bekle, oyun görünümünün ekran görüntüsünü al, sonra durdur."

### 2D / UI ipuçları

- **Sprite atama:** `unity_set_property`'ye `Assets/Sprites/atlas.png#SpriteAdi` ver — alt-asset'ler otomatik çözülür.
- **ScriptableObject alanları:** `unity_set_property`'yi `assetPath` parametresiyle çağır.
- **UI doğrulama:** yerleşim işlerinden sonra `unity_capture_screenshot` çağır; Screen Space Overlay canvas'lar dahil edilir (ayarlar sonra otomatik geri yüklenir).
- **Pixel art:** `unity_set_texture_import_settings` ile `filterMode: "Point"`.

### Unity sürüm uyumu

Unity **6.2**, `Object.GetInstanceID()` / `EditorUtility.InstanceIDToObject()`'i — hatta `EntityId ↔ int` dönüşümlerini bile — **derleme hatası** seviyesine çıkardı. Bu köprü, instance ID'leri çalışma zamanında bu API'ler üzerinden çözerek sürümler arası uyumlu kalır ve protokolün kullandığı klasik tamsayı `instanceId`'yi korur. Hem **Unity 6.x** hem eski sürümlerde temiz derlenir.

> Unity 2020 mı kullanıyorsun? `McpBridge.cs` başına `using UnityEditor.Experimental.SceneManagement;` ekle.

### Sorun giderme

- **"Unity Editor'e bağlanılamadı"** → Unity açık mı? Console'da köprü mesajı var mı? **Tools > MCP Bridge > Restart Server**.
- **Derleme sırasında zaman aşımı** → Unity script derlerken istekleri yanıtlayamaz; birkaç saniye bekleyip tekrar dene (60 sn timeout).
- **Port çakışması** → `McpBridge.cs` içindeki `Port` sabitini ve sunucudaki `UNITY_MCP_PORT` ortam değişkenini aynı değere ayarla.
- **set_property "property bulunamadı"** → Önce `unity_get_object` çağır; Unity içsel adları kullanır (ör. `m_Mass`, `m_Intensity`).

### Yol haritası (v4 fikirleri)

- Animator alt-state machine'leri & blend tree'ler
- Tile Palette / rule tile'lar
- Particle system düzenleme
- Terrain araçları

### Lisans

MIT — bkz. `LICENSE`.
