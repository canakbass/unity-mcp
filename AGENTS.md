# Working with Unity MCP Bridge — guidance for AI agents

These rules mirror the `instructions` field the server sends on `initialize`.
They are repeated here for clients that ignore that field, and for humans who
want to copy them into their own rules file (`CLAUDE.md`, `.cursorrules`,
`.windsurfrules`, `.clinerules`, `.github/copilot-instructions.md`, …).

Every rule below comes from a real failure while building a full game with this
bridge — none of them are speculative.

## Cost: what actually burns tokens

The tools are not equally priced. In descending order:

| Call | Typical cost | Use it when |
|---|---|---|
| `unity_capture_screenshot` | ~2k tokens | **Visual** checks only: UI layout, sprite look, effects |
| `unity_read_console` | 100 – 5k tokens | After a compile or an action — **always** with `limit` and `type` |
| `unity_get_scene` | grows with scene | You genuinely need the whole hierarchy |
| everything else | small | freely |

### 1. Do not screenshot to verify logic

A screenshot tells you *roughly* what is on screen. For anything numeric or
structural, have an editor script write a report and read the file:

```csharp
System.IO.File.WriteAllText("Logs/report.txt", sb.ToString());
```

This is cheaper **and** more precise. Verifying "which weapon dealt the most
damage" by reading `sectorDamage=1395` beats squinting at a health bar.

### 2. Read the console incrementally

```json
{ "type": "Error", "limit": 5 }                 // first call
{ "type": "Error", "limit": 5, "since": 23 }    // then: only what is new
```

The response is `{ entries, cursor, returned, matched, dropped }`. Pass the
returned `cursor` back as `since` and you get **only new entries** — no
re-reading the same errors after every fix.

Identical consecutive messages are collapsed into one entry with a `count`,
so a per-frame exception no longer floods the result:

```
seq=21  count=20  NullReferenceException ...   ← 20 occurrences, one entry
```

`dropped: true` means the buffer overflowed and some entries were lost.

### 3. Prefer `unity_get_object` over `unity_get_scene`

`get_scene` serialises every object in every loaded scene.

### 3b. Build scenes with one call

`unity_create_objects` creates many objects at once. Put fields common to all
of them in `shared`; each item overrides what it needs. One bad item does not
abort the rest — the result reports `created` and `failed` separately.

### 4. Edit code with your own file tools

Read and write `.cs` files directly, then compile with a single
`unity_execute_menu` → `Assets/Refresh`. Round-tripping source through the
bridge is slower and costs more.

## Play mode: three traps

**Check state before entering.** Calling `unity_play` while already playing
*toggles* the mode — you silently exit play, and every following command runs
against the wrong state.

```
unity_get_play_state  →  isPlaying? → only then unity_play
```

**Never compile while playing.** Unity performs a domain reload: `static`
fields reset, but `Awake()` is **not** called again. Singletons and object
pools become `null` and the game throws `NullReferenceException` every frame
without any compile error. Stop play mode, then edit.

To survive this anyway, make managers resolve lazily instead of caching in
`Awake`:

```csharp
static GameManager _i;
public static GameManager I =>
    _i != null ? _i : (_i = FindAnyObjectByType<GameManager>());
```

…and build object pools lazily too, on first use rather than in `Awake`.

**Commands sent during the load are dropped.** After `unity_play`, wait a
moment before issuing menu commands.

## The bridge runs on Unity's main thread

An unbounded loop in an editor script freezes the entire editor and every tool
call times out. Always bound loops you write:

```csharp
// Upgrade() returns false when it cannot afford the cost, but CanUpgrade
// stays true -> `while (CanUpgrade) Upgrade();` never terminates.
for (int i = 0; i < MaxLevel && home.CanUpgrade; i++)
    if (!home.Upgrade()) break;
```

## A loop that works

```
write .cs file
  → unity_execute_menu "Assets/Refresh"
  → unity_read_console (type: Error, limit: 5)
  → fix, repeat until clean
  → unity_get_play_state → unity_play
  → screenshot ONLY if the thing being checked is visual
```

## Useful pattern: menu commands as a test harness

Expose debug actions as `[MenuItem]` entries and call them with
`unity_execute_menu`. It gives the agent deterministic control over runtime
state without needing to simulate input (which the bridge cannot do):

```csharp
[MenuItem("Tools/Debug/Give XP")]          // jump to a level-up screen
[MenuItem("Tools/Debug/Kill boss")]        // trigger the reward flow
[MenuItem("Tools/Debug/Weapon report")]    // dump state to Logs/*.txt
```
