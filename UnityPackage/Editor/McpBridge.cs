// McpBridge.cs
// Unity Editor icinde calisan MCP koprusu.
// localhost:6400 uzerinden satir-bazli JSON komutlari alir, ana thread'de isler, sonucu geri yazar.
// Gereksinim: com.unity.nuget.newtonsoft-json paketi (README'ye bak).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

namespace McpUnity
{
    [InitializeOnLoad]
    public static class McpBridge
    {
        const int Port = 6400;

        static TcpListener _listener;
        static Thread _listenThread;
        static volatile bool _running;

        class Pending
        {
            public JObject Request;
            public StreamWriter Writer;
            public object WriteLock;
        }

        static readonly ConcurrentQueue<Pending> Queue = new ConcurrentQueue<Pending>();

        // ---- Konsol log yakalama ----
        class LogEntry { public string type; public string message; public string stack; public string time; }
        static readonly List<LogEntry> Logs = new List<LogEntry>();
        const int MaxLogs = 500;

        static McpBridge()
        {
            Application.logMessageReceived += OnLog;
            EditorApplication.update += ProcessQueue;
            Start();
            AppDomain.CurrentDomain.DomainUnload += (s, e) => Stop();
        }

        static void OnLog(string message, string stack, LogType type)
        {
            lock (Logs)
            {
                Logs.Add(new LogEntry
                {
                    type = type.ToString(),
                    message = message,
                    stack = type == LogType.Error || type == LogType.Exception ? stack : null,
                    time = DateTime.Now.ToString("HH:mm:ss")
                });
                if (Logs.Count > MaxLogs) Logs.RemoveAt(0);
            }
        }

        [MenuItem("Tools/MCP Bridge/Restart Server")]
        static void RestartMenu() { Stop(); Start(); }

        static void Start()
        {
            if (_running) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _running = true;
                _listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "McpBridgeListener" };
                _listenThread.Start();
                Debug.Log($"[MCP Bridge] Dinleniyor: 127.0.0.1:{Port}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCP Bridge] Baslatilamadi (port kullaniliyor olabilir): {e.Message}");
            }
        }

        static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        static void ListenLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try { client = _listener.AcceptTcpClient(); }
                catch { break; }
                var t = new Thread(() => ClientLoop(client)) { IsBackground = true };
                t.Start();
            }
        }

        static void ClientLoop(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    var writeLock = new object();
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        JObject req;
                        try { req = JObject.Parse(line); }
                        catch (Exception e)
                        {
                            lock (writeLock) writer.WriteLine(new JObject { ["error"] = "JSON parse hatasi: " + e.Message }.ToString(Formatting.None));
                            continue;
                        }
                        Queue.Enqueue(new Pending { Request = req, Writer = writer, WriteLock = writeLock });
                        // Yanit ana thread tarafindan yazilana kadar okumaya devam edebiliriz;
                        // istemci tek istek/tek yanit modeliyle calisiyor.
                        // Yaniti bekle: basit senkronizasyon icin kucuk bir bekleme dongusu yerine
                        // ana thread yaniti yazinca istemci okur. Burada ek is yok.
                    }
                }
            }
            catch { /* baglanti koptu */ }
        }

        static void ProcessQueue()
        {
            while (Queue.TryDequeue(out var p))
            {
                JToken result = null;
                string error = null;
                try { result = Handle((string)p.Request["method"], p.Request["params"] as JObject ?? new JObject()); }
                catch (Exception e) { error = e.Message; }

                var resp = new JObject { ["id"] = p.Request["id"] };
                if (error != null) resp["error"] = error; else resp["result"] = result ?? JValue.CreateNull();

                try { lock (p.WriteLock) p.Writer.WriteLine(resp.ToString(Formatting.None)); }
                catch { /* istemci gitti */ }
            }
        }

        // =====================================================================
        //  KOMUTLAR
        // =====================================================================
        static JToken Handle(string method, JObject p)
        {
            switch (method)
            {
                case "ping": return "pong";
                case "get_scene": return GetScene();
                case "get_object": return GetObject(p);
                case "create_object": return CreateObject(p);
                case "delete_object": return DeleteObject(p);
                case "set_transform": return SetTransform(p);
                case "add_component": return AddComponent(p);
                case "remove_component": return RemoveComponent(p);
                case "set_property": return SetProperty(p);
                case "instantiate_prefab": return InstantiatePrefab(p);
                case "find_assets": return FindAssets(p);
                case "create_script": return CreateScript(p);
                case "read_file": return ReadFile(p);
                case "read_console": return ReadConsole(p);
                case "save_scene": EditorSceneManager.SaveOpenScenes(); return "kaydedildi";
                // ---- v2: Material ----
                case "create_material": return CreateMaterial(p);
                case "set_material": return SetMaterial(p);
                case "get_material": return GetMaterial(p);
                // ---- v2: ScriptableObject / Asset ----
                case "create_scriptable_object": return CreateScriptableObjectCmd(p);
                case "get_asset": return GetAsset(p);
                // ---- v2: Coklu sahne ----
                case "list_scenes": return ListScenes();
                case "open_scene": return OpenScene(p);
                case "new_scene": return NewScene(p);
                case "close_scene": return CloseScene(p);
                case "set_active_scene": return SetActiveScene(p);
                // ---- v2: Prefab modu ----
                case "open_prefab": return OpenPrefabCmd(p);
                case "save_prefab": return SavePrefabCmd();
                case "close_prefab": return ClosePrefabCmd(p);
                // ---- v2: Goruntu ----
                case "capture_screenshot": return CaptureScreenshot(p);
                case "execute_menu": return EditorApplication.ExecuteMenuItem((string)p["path"]) ? "calistirildi" : throw new Exception("Menu bulunamadi: " + p["path"]);
                // ---- v2.1: prefab / build ----
                case "save_as_prefab": return SaveAsPrefab(p);
                case "set_build_settings_scenes": return SetBuildSettingsScenes(p);
                // ---- v3: Play mode ----
                case "play": EditorApplication.EnterPlaymode(); return "play mode baslatiliyor";
                case "stop": EditorApplication.ExitPlaymode(); return "play mode durduruluyor";
                case "pause": EditorApplication.isPaused = Has(p, "paused") ? (bool)p["paused"] : !EditorApplication.isPaused; return EditorApplication.isPaused ? "duraklatildi" : "devam ettirildi";
                case "step": EditorApplication.Step(); return "bir kare ilerletildi";
                case "get_play_state": return GetPlayState();
                // ---- v3: Texture / Sprite import ----
                case "set_texture_import_settings": return SetTextureImportSettings(p);
                // ---- v3: Tilemap ----
                case "create_tilemap": return CreateTilemap(p);
                case "create_tile_asset": return CreateTileAsset(p);
                case "set_tiles": return SetTiles(p);
                // ---- v3: Animation / Animator ----
                case "create_sprite_animation": return CreateSpriteAnimation(p);
                case "create_animation_clip": return CreateAnimationClip(p);
                case "create_animator_controller": return CreateAnimatorController(p);
                case "assign_animator_controller": return AssignAnimatorController(p);
                default: throw new Exception("Bilinmeyen method: " + method);
            }
        }

        // ---- Yardimcilar ----
        static bool Has(JObject p, string key) => p[key] != null && p[key].Type != JTokenType.Null;

        // Unity 6.2+ InstanceID API'lerini "hata" seviyesinde kullanimdan kaldirdi; EntityId<->int
        // donusumleri de obsolete-error oldu. Klasik int instanceId semantigini (MCP protokolu ve
        // server.js ile birebir uyumlu, negatif sahne ID'leri dahil) korumak icin eski metotlara
        // reflection ile eristik: reflection derleme-zamani obsolete kontrolunu atlar, metotlar her
        // surumde calisma-zamaninda hala mevcut.
        static readonly System.Reflection.MethodInfo _miGetInstanceID =
            typeof(UnityEngine.Object).GetMethod("GetInstanceID", Type.EmptyTypes);
        static readonly System.Reflection.MethodInfo _miInstanceIDToObject =
            typeof(EditorUtility).GetMethod("InstanceIDToObject", new[] { typeof(int) });

        static int GetIID(UnityEngine.Object o) => (int)_miGetInstanceID.Invoke(o, null);
        static UnityEngine.Object IIDToObject(int id) => (UnityEngine.Object)_miInstanceIDToObject.Invoke(null, new object[] { id });

        // FindObjectsOfType da kullanimdan kalkti; Unity 6'da parametresiz (deprecated olmayan)
        // surumu, eski surumlerde klasik FindObjectsOfType.
        static T[] FindAll<T>() where T : UnityEngine.Object
        {
#if UNITY_6000_0_OR_NEWER
            return UnityEngine.Object.FindObjectsByType<T>();
#else
            return UnityEngine.Object.FindObjectsOfType<T>();
#endif
        }

        static GameObject ResolveGameObject(JObject p)
        {
            if (Has(p, "instanceId"))
            {
                var obj = IIDToObject((int)p["instanceId"]) as GameObject;
                if (obj != null) return obj;
                var comp = IIDToObject((int)p["instanceId"]) as Component;
                if (comp != null) return comp.gameObject;
                throw new Exception("instanceId ile GameObject bulunamadi: " + p["instanceId"]);
            }
            if (Has(p, "path"))
            {
                var go = FindByPath((string)p["path"]);
                if (go == null) throw new Exception("Hiyerarsi yolunda nesne yok: " + p["path"]);
                return go;
            }
            throw new Exception("instanceId veya path gerekli.");
        }

        // Prefab modu dahil tum yuklu sahnelerde hiyerarsi yoluyla arar
        static GameObject FindByPath(string path)
        {
            var roots = new List<GameObject>();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) roots.Add(stage.prefabContentsRoot);
            else
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (s.isLoaded) roots.AddRange(s.GetRootGameObjects());
                }

            var parts = path.Split('/');
            foreach (var root in roots)
            {
                if (root.name == parts[0])
                {
                    if (parts.Length == 1) return root;
                    var t = root.transform.Find(string.Join("/", parts, 1, parts.Length - 1));
                    if (t != null) return t.gameObject;
                }
                // Prefab modunda kok adini yazmadan alt yol da kabul edilir
                var t2 = root.transform.Find(path);
                if (t2 != null) return t2.gameObject;
            }
            return null;
        }

        static Type FindAnyType(string name, Type baseFilter)
        {
            // TypeCache Unity tarafindan onceden hesaplanir: AppDomain taramasindan hem daha hizli hem
            // de domain-reload sirasinda bosaltilmis assembly riski tasimaz (UAC0005 uyarisini giderir).
            // baseFilter pratikte hep verilir (Component / ScriptableObject); null ise en genis Unity
            // tabanindan (UnityEngine.Object) araniyor. Once tam ad (namespace dahil), sonra kisa ad.
            var pool = TypeCache.GetTypesDerivedFrom(baseFilter ?? typeof(UnityEngine.Object));
            Type byShort = null;
            foreach (var t in pool)
            {
                if (t.FullName == name) return t;
                if (byShort == null && t.Name == name) byShort = t;
            }
            if (byShort != null) return byShort;
            throw new Exception("Tip bulunamadi: " + name);
        }

        static Type FindType(string name) => FindAnyType(name, typeof(Component));

        static JArray Vec3(Vector3 v) => new JArray(v.x, v.y, v.z);
        static Vector3 ToVec3(JToken t) => new Vector3((float)t[0], (float)t[1], (float)t[2]);

        // ---- get_scene: prefab modu veya tum yuklu sahneler ----
        static JToken GetScene()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null)
            {
                return new JObject
                {
                    ["mode"] = "prefab",
                    ["prefabAssetPath"] = stage.assetPath,
                    ["objects"] = new JArray(SerializeGo(stage.prefabContentsRoot, 0))
                };
            }
            var scenes = new JArray();
            var active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                var roots = new JArray();
                foreach (var go in scene.GetRootGameObjects()) roots.Add(SerializeGo(go, 0));
                scenes.Add(new JObject
                {
                    ["scene"] = scene.name,
                    ["path"] = scene.path,
                    ["isActive"] = scene == active,
                    ["objects"] = roots
                });
            }
            return new JObject { ["mode"] = "scenes", ["scenes"] = scenes };
        }

        static JObject SerializeGo(GameObject go, int depth)
        {
            var o = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = GetIID(go),
                ["active"] = go.activeSelf,
                ["tag"] = go.tag,
                ["position"] = Vec3(go.transform.position),
                ["components"] = new JArray(go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => (JToken)new JObject { ["type"] = c.GetType().Name, ["instanceId"] = GetIID(c) }))
            };
            if (go.transform.childCount > 0 && depth < 20)
            {
                var kids = new JArray();
                foreach (Transform c in go.transform) kids.Add(SerializeGo(c.gameObject, depth + 1));
                o["children"] = kids;
            }
            return o;
        }

        // ---- get_object: component ozelliklerini de doker ----
        static JToken GetObject(JObject p)
        {
            var go = ResolveGameObject(p);
            var comps = new JArray();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var cj = new JObject { ["type"] = c.GetType().FullName, ["instanceId"] = GetIID(c) };
                var props = new JObject();
                var so = new SerializedObject(c);
                var it = so.GetIterator();
                bool enterChildren = true;
                int count = 0;
                while (it.NextVisible(enterChildren) && count < 120)
                {
                    enterChildren = false;
                    props[it.propertyPath] = PropToJson(it);
                    count++;
                }
                cj["properties"] = props;
                comps.Add(cj);
            }
            return new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = GetIID(go),
                ["active"] = go.activeSelf,
                ["position"] = Vec3(go.transform.position),
                ["rotationEuler"] = Vec3(go.transform.eulerAngles),
                ["scale"] = Vec3(go.transform.localScale),
                ["components"] = comps
            };
        }

        static JToken PropToJson(SerializedProperty sp)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: return sp.intValue;
                case SerializedPropertyType.Boolean: return sp.boolValue;
                case SerializedPropertyType.Float: return sp.floatValue;
                case SerializedPropertyType.String: return sp.stringValue;
                case SerializedPropertyType.Color: var c = sp.colorValue; return new JArray(c.r, c.g, c.b, c.a);
                case SerializedPropertyType.Vector2: return new JArray(sp.vector2Value.x, sp.vector2Value.y);
                case SerializedPropertyType.Vector3: return Vec3(sp.vector3Value);
                case SerializedPropertyType.Vector4: var v4 = sp.vector4Value; return new JArray(v4.x, v4.y, v4.z, v4.w);
                case SerializedPropertyType.Quaternion: return Vec3(sp.quaternionValue.eulerAngles);
                case SerializedPropertyType.Enum:
                    return sp.enumValueIndex >= 0 && sp.enumValueIndex < sp.enumDisplayNames.Length
                        ? (JToken)sp.enumDisplayNames[sp.enumValueIndex] : sp.enumValueIndex;
                case SerializedPropertyType.ObjectReference:
                    return sp.objectReferenceValue ? sp.objectReferenceValue.name + " (" + sp.objectReferenceValue.GetType().Name + ")" : null;
                default: return "<" + sp.propertyType + ">";
            }
        }

        // ---- create_object ----
        static JToken CreateObject(JObject p)
        {
            GameObject go;
            string prim = (string)p["primitive"];
            if (!string.IsNullOrEmpty(prim))
                go = GameObject.CreatePrimitive((PrimitiveType)Enum.Parse(typeof(PrimitiveType), prim, true));
            else
                go = new GameObject();

            go.name = (string)p["name"] ?? "New GameObject";
            if (p["parent"] != null)
            {
                var parent = ResolveGameObject(new JObject { ["instanceId"] = p["parent"].Type == JTokenType.Integer ? p["parent"] : null, ["path"] = p["parent"].Type == JTokenType.String ? p["parent"] : null });
                go.transform.SetParent(parent.transform, false);
            }
            if (p["position"] != null) go.transform.position = ToVec3(p["position"]);
            if (p["rotation"] != null) go.transform.eulerAngles = ToVec3(p["rotation"]);
            if (p["scale"] != null) go.transform.localScale = ToVec3(p["scale"]);

            Undo.RegisterCreatedObjectUndo(go, "MCP Create " + go.name);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new JObject { ["name"] = go.name, ["instanceId"] = GetIID(go) };
        }

        static JToken DeleteObject(JObject p)
        {
            var go = ResolveGameObject(p);
            var name = go.name;
            Undo.DestroyObjectImmediate(go);
            return "silindi: " + name;
        }

        static JToken SetTransform(JObject p)
        {
            var go = ResolveGameObject(p);
            Undo.RecordObject(go.transform, "MCP Transform");
            bool local = p["space"] != null && (string)p["space"] == "local";
            if (p["position"] != null) { if (local) go.transform.localPosition = ToVec3(p["position"]); else go.transform.position = ToVec3(p["position"]); }
            if (p["rotation"] != null) { if (local) go.transform.localEulerAngles = ToVec3(p["rotation"]); else go.transform.eulerAngles = ToVec3(p["rotation"]); }
            if (p["scale"] != null) go.transform.localScale = ToVec3(p["scale"]);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return "tamam";
        }

        static JToken AddComponent(JObject p)
        {
            var go = ResolveGameObject(p);
            var type = FindType((string)p["componentType"]);
            var comp = Undo.AddComponent(go, type);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new JObject { ["type"] = type.FullName, ["instanceId"] = GetIID(comp) };
        }

        static JToken RemoveComponent(JObject p)
        {
            var go = ResolveGameObject(p);
            var type = FindType((string)p["componentType"]);
            var comp = go.GetComponent(type);
            if (comp == null) throw new Exception("Component nesnede yok: " + type.Name);
            Undo.DestroyObjectImmediate(comp);
            return "kaldirildi: " + type.Name;
        }

        // ---- set_property: SerializedObject uzerinden ----
        static JToken SetProperty(JObject p)
        {
            UnityEngine.Object target;
            if (Has(p, "componentInstanceId"))
                target = IIDToObject((int)p["componentInstanceId"]);
            else if (Has(p, "assetPath"))
                target = AssetDatabase.LoadMainAssetAtPath((string)p["assetPath"]);
            else
            {
                var go = ResolveGameObject(p);
                var type = FindType((string)p["componentType"]);
                target = go.GetComponent(type);
            }
            if (target == null) throw new Exception("Hedef bulunamadi (component ya da asset).");

            var so = new SerializedObject(target);
            var sp = so.FindProperty((string)p["propertyPath"]);
            if (sp == null) throw new Exception("Property bulunamadi: " + p["propertyPath"] + " (get_object ile dogru propertyPath'i gorebilirsin)");

            var val = p["value"];
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Integer: sp.intValue = (int)val; break;
                case SerializedPropertyType.Boolean: sp.boolValue = (bool)val; break;
                case SerializedPropertyType.Float: sp.floatValue = (float)val; break;
                case SerializedPropertyType.String: sp.stringValue = (string)val; break;
                case SerializedPropertyType.Color: sp.colorValue = new Color((float)val[0], (float)val[1], (float)val[2], val.Count() > 3 ? (float)val[3] : 1f); break;
                case SerializedPropertyType.Vector2: sp.vector2Value = new Vector2((float)val[0], (float)val[1]); break;
                case SerializedPropertyType.Vector3: sp.vector3Value = ToVec3(val); break;
                case SerializedPropertyType.Quaternion: sp.quaternionValue = Quaternion.Euler(ToVec3(val)); break;
                case SerializedPropertyType.Enum:
                    if (val.Type == JTokenType.String)
                    {
                        int idx = Array.IndexOf(sp.enumDisplayNames, (string)val);
                        if (idx < 0) throw new Exception("Enum degeri yok: " + val + ". Secenekler: " + string.Join(", ", sp.enumDisplayNames));
                        sp.enumValueIndex = idx;
                    }
                    else sp.enumValueIndex = (int)val;
                    break;
                case SerializedPropertyType.ObjectReference:
                    if (val == null || val.Type == JTokenType.Null) sp.objectReferenceValue = null;
                    else if (val.Type == JTokenType.Integer) sp.objectReferenceValue = IIDToObject((int)val);
                    else
                    {
                        sp.objectReferenceValue = LoadAssetSmart((string)val, sp.type);
                    }
                    break;
                default:
                    throw new Exception("Desteklenmeyen property tipi: " + sp.propertyType);
            }
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            if (AssetDatabase.Contains(target)) AssetDatabase.SaveAssets();
            return "ayarlandi: " + p["propertyPath"] + " = " + val;
        }

        static JToken InstantiatePrefab(JObject p)
        {
            string path = (string)p["assetPath"];
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new Exception("Prefab bulunamadi: " + path + " (find_assets ile arayabilirsin)");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (p["name"] != null) go.name = (string)p["name"];
            if (p["position"] != null) go.transform.position = ToVec3(p["position"]);
            if (p["rotation"] != null) go.transform.eulerAngles = ToVec3(p["rotation"]);
            if (p["parent"] != null)
            {
                var parent = ResolveGameObject(new JObject { ["path"] = p["parent"] });
                go.transform.SetParent(parent.transform, true);
            }
            Undo.RegisterCreatedObjectUndo(go, "MCP Prefab");
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new JObject { ["name"] = go.name, ["instanceId"] = GetIID(go) };
        }

        static JToken FindAssets(JObject p)
        {
            // Ornek filtreler: "t:Prefab agac", "t:Material", "t:Script Player"
            var guids = AssetDatabase.FindAssets((string)p["filter"] ?? "");
            int limit = p["limit"] != null ? (int)p["limit"] : 50;
            var arr = new JArray();
            foreach (var g in guids.Take(limit)) arr.Add(AssetDatabase.GUIDToAssetPath(g));
            return new JObject { ["count"] = guids.Length, ["paths"] = arr };
        }

        static JToken CreateScript(JObject p)
        {
            string rel = (string)p["path"]; // ornn: "Scripts/PlayerController.cs"
            if (string.IsNullOrEmpty(rel) || !rel.EndsWith(".cs")) throw new Exception("path 'Klasor/Dosya.cs' bicimin de olmali.");
            string full = Path.Combine(Application.dataPath, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, (string)p["content"], new UTF8Encoding(false));
            AssetDatabase.Refresh();
            return "yazildi: Assets/" + rel + " — derleme baslatildi, hatalar icin read_console kullan.";
        }

        static JToken ReadFile(JObject p)
        {
            string rel = (string)p["path"];
            string full = Path.Combine(Application.dataPath, rel);
            if (!File.Exists(full)) throw new Exception("Dosya yok: Assets/" + rel);
            return File.ReadAllText(full);
        }

        static JToken ReadConsole(JObject p)
        {
            int limit = p["limit"] != null ? (int)p["limit"] : 30;
            string filter = (string)p["type"]; // "Error", "Warning", "Log" veya null
            bool clear = p["clear"] != null && (bool)p["clear"];
            lock (Logs)
            {
                var q = Logs.AsEnumerable();
                if (!string.IsNullOrEmpty(filter)) q = q.Where(l => l.type == filter || (filter == "Error" && l.type == "Exception"));
                var items = q.Reverse().Take(limit).Reverse()
                    .Select(l => (JToken)new JObject { ["time"] = l.time, ["type"] = l.type, ["message"] = l.message, ["stack"] = l.stack });
                var result = new JArray(items);
                if (clear) Logs.Clear();
                return result;
            }
        }

        // =====================================================================
        //  v2: ASSET YUKLEME YARDIMCISI (sprite gibi alt-asset'ler dahil)
        //  "Assets/Sprites/atlas.png#Karakter_Idle" bicimini destekler.
        //  spType ornegi: "PPtr<$Sprite>" -> beklenen tip adi cikarilir.
        // =====================================================================
        static UnityEngine.Object LoadAssetSmart(string spec, string spType)
        {
            string path = spec, subName = null;
            int hash = spec.LastIndexOf('#');
            if (hash > 0) { path = spec.Substring(0, hash); subName = spec.Substring(hash + 1); }

            string expected = null;
            var m = System.Text.RegularExpressions.Regex.Match(spType ?? "", @"PPtr<\$?(\w+)>");
            if (m.Success) expected = m.Groups[1].Value;

            var all = AssetDatabase.LoadAllAssetsAtPath(path);
            if (all == null || all.Length == 0) throw new Exception("Asset yolu bulunamadi: " + path);

            UnityEngine.Object best = null;
            foreach (var a in all)
            {
                if (a == null) continue;
                bool typeOk = expected == null || a.GetType().Name == expected || IsSubclassByName(a.GetType(), expected);
                bool nameOk = subName == null || a.name == subName;
                if (typeOk && nameOk) { best = a; break; }
                if (best == null && nameOk) best = a;
            }
            if (best == null) best = AssetDatabase.LoadMainAssetAtPath(path);
            if (best == null) throw new Exception("Uygun asset bulunamadi: " + spec + (expected != null ? " (beklenen tip: " + expected + ")" : ""));
            return best;
        }

        static bool IsSubclassByName(Type t, string baseName)
        {
            while (t != null) { if (t.Name == baseName) return true; t = t.BaseType; }
            return false;
        }

        static string NormalizeAssetPath(string path, string ext)
        {
            if (string.IsNullOrEmpty(path)) throw new Exception("path gerekli.");
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/")) path = "Assets/" + path;
            if (!path.EndsWith(ext)) path += ext;
            var dir = Path.GetDirectoryName(Path.Combine(Directory.GetCurrentDirectory(), path));
            Directory.CreateDirectory(dir);
            return path;
        }

        // =====================================================================
        //  v2: MATERIAL
        // =====================================================================
        static Shader PickShader(string requested)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(requested)) candidates.Add(requested);
            // 2D odakli varsayilan sira (URP varsa once URP 2D)
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
            {
                candidates.Add("Universal Render Pipeline/2D/Sprite-Lit-Default");
                candidates.Add("Universal Render Pipeline/2D/Sprite-Unlit-Default");
                candidates.Add("Universal Render Pipeline/Unlit");
                candidates.Add("Universal Render Pipeline/Lit");
            }
            candidates.Add("Sprites/Default");
            candidates.Add("UI/Default");
            candidates.Add("Standard");
            foreach (var c in candidates)
            {
                var s = Shader.Find(c);
                if (s != null) return s;
            }
            throw new Exception("Shader bulunamadi. Denenenler: " + string.Join(", ", candidates));
        }

        static JToken CreateMaterial(JObject p)
        {
            string path = NormalizeAssetPath((string)p["path"], ".mat");
            var mat = new Material(PickShader((string)p["shader"]));
            if (Has(p, "color"))
            {
                var c = p["color"];
                mat.color = new Color((float)c[0], (float)c[1], (float)c[2], c.Count() > 3 ? (float)c[3] : 1f);
            }
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            return new JObject { ["path"] = path, ["shader"] = mat.shader.name };
        }

        static Material LoadMaterial(JObject p)
        {
            string path = (string)p["path"];
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) throw new Exception("Material bulunamadi: " + path + " (find_assets 't:Material' ile arayabilirsin)");
            return mat;
        }

        static JToken SetMaterial(JObject p)
        {
            var mat = LoadMaterial(p);
            Undo.RecordObject(mat, "MCP Material");
            if (Has(p, "shader")) mat.shader = PickShader((string)p["shader"]);
            var applied = new JArray();
            if (p["properties"] is JObject props)
            {
                foreach (var kv in props)
                {
                    string name = kv.Key;
                    var val = kv.Value;
                    if (!mat.HasProperty(name))
                        throw new Exception("Material'de property yok: " + name + " (get_material ile mevcutlari gorebilirsin)");
                    if (val.Type == JTokenType.Array)
                    {
                        var a = (JArray)val;
                        if (a.Count >= 3)
                            mat.SetColor(name, new Color((float)a[0], (float)a[1], (float)a[2], a.Count > 3 ? (float)a[3] : 1f));
                        else
                            mat.SetVector(name, new Vector4((float)a[0], (float)a[1], 0, 0));
                    }
                    else if (val.Type == JTokenType.Float || val.Type == JTokenType.Integer)
                        mat.SetFloat(name, (float)val);
                    else if (val.Type == JTokenType.String)
                    {
                        var tex = LoadAssetSmart((string)val, "PPtr<$Texture>") as Texture;
                        if (tex == null) throw new Exception("Texture yuklenemedi: " + val);
                        mat.SetTexture(name, tex);
                    }
                    applied.Add(name);
                }
            }
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return new JObject { ["applied"] = applied, ["shader"] = mat.shader.name };
        }

        static JToken GetMaterial(JObject p)
        {
            var mat = LoadMaterial(p);
            var shader = mat.shader;
            var props = new JObject();
            int n = shader.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                string name = shader.GetPropertyName(i);
                var type = shader.GetPropertyType(i);
                JToken value;
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        var c = mat.GetColor(name); value = new JArray(c.r, c.g, c.b, c.a); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        value = mat.GetFloat(name); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        var v = mat.GetVector(name); value = new JArray(v.x, v.y, v.z, v.w); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var t = mat.GetTexture(name);
                        value = t ? AssetDatabase.GetAssetPath(t) : null; break;
                    default: value = "<" + type + ">"; break;
                }
                props[name] = new JObject { ["type"] = type.ToString(), ["value"] = value };
            }
            return new JObject { ["path"] = AssetDatabase.GetAssetPath(mat), ["shader"] = shader.name, ["properties"] = props };
        }

        // =====================================================================
        //  v2: SCRIPTABLEOBJECT / ASSET
        // =====================================================================
        static JToken CreateScriptableObjectCmd(JObject p)
        {
            var type = FindAnyType((string)p["typeName"], typeof(ScriptableObject));
            if (!typeof(ScriptableObject).IsAssignableFrom(type))
                throw new Exception(type.Name + " bir ScriptableObject degil.");
            string path = NormalizeAssetPath((string)p["assetPath"], ".asset");
            var so = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(so, path);
            AssetDatabase.SaveAssets();
            return new JObject { ["path"] = path, ["type"] = type.FullName, ["hint"] = "Alanlari set_property (assetPath ile) uzerinden doldurabilirsin." };
        }

        static JToken GetAsset(JObject p)
        {
            string path = (string)p["assetPath"];
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) throw new Exception("Asset bulunamadi: " + path);
            var props = new JObject();
            var so = new SerializedObject(asset);
            var it = so.GetIterator();
            bool enter = true;
            int count = 0;
            while (it.NextVisible(enter) && count < 200)
            {
                enter = false;
                props[it.propertyPath] = PropToJson(it);
                count++;
            }
            var result = new JObject { ["path"] = path, ["type"] = asset.GetType().FullName, ["properties"] = props };
            // Alt-asset'leri de listele (sprite atlas'lari icin faydali)
            var subs = AssetDatabase.LoadAllAssetsAtPath(path).Where(a => a != null && a != asset)
                .Select(a => (JToken)(a.name + " (" + a.GetType().Name + ")"));
            if (subs.Any()) result["subAssets"] = new JArray(subs);
            return result;
        }

        // =====================================================================
        //  v2: COKLU SAHNE
        // =====================================================================
        static UnityEngine.SceneManagement.Scene SceneByPathOrName(string s)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.path == s || sc.name == s) return sc;
            }
            throw new Exception("Yuklu sahne bulunamadi: " + s + " (list_scenes ile kontrol et)");
        }

        static JToken ListScenes()
        {
            var loaded = new JArray();
            var active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                loaded.Add(new JObject { ["name"] = s.name, ["path"] = s.path, ["isLoaded"] = s.isLoaded, ["isActive"] = s == active, ["isDirty"] = s.isDirty, ["rootCount"] = s.rootCount });
            }
            var project = new JArray(AssetDatabase.FindAssets("t:Scene").Take(100)
                .Select(g => (JToken)AssetDatabase.GUIDToAssetPath(g)));
            return new JObject { ["loaded"] = loaded, ["projectScenes"] = project };
        }

        static JToken OpenScene(JObject p)
        {
            bool additive = Has(p, "additive") && (bool)p["additive"];
            bool saveCurrent = !Has(p, "saveCurrent") || (bool)p["saveCurrent"];
            if (saveCurrent) EditorSceneManager.SaveOpenScenes();
            var scene = EditorSceneManager.OpenScene((string)p["path"], additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new JObject { ["name"] = scene.name, ["path"] = scene.path, ["mode"] = additive ? "additive" : "single" };
        }

        static JToken NewScene(JObject p)
        {
            bool additive = Has(p, "additive") && (bool)p["additive"];
            bool empty = Has(p, "empty") && (bool)p["empty"];
            if (!additive) EditorSceneManager.SaveOpenScenes();
            var scene = EditorSceneManager.NewScene(
                empty ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects,
                additive ? NewSceneMode.Additive : NewSceneMode.Single);
            if (Has(p, "savePath"))
            {
                string path = NormalizeAssetPath((string)p["savePath"], ".unity");
                EditorSceneManager.SaveScene(scene, path);
            }
            return new JObject { ["name"] = scene.name, ["path"] = scene.path };
        }

        static JToken CloseScene(JObject p)
        {
            var scene = SceneByPathOrName((string)p["path"]);
            if (SceneManager.sceneCount <= 1) throw new Exception("Son acik sahne kapatilamaz.");
            if (Has(p, "save") && (bool)p["save"]) EditorSceneManager.SaveScene(scene);
            EditorSceneManager.CloseScene(scene, true);
            return "kapatildi";
        }

        static JToken SetActiveScene(JObject p)
        {
            var scene = SceneByPathOrName((string)p["path"]);
            SceneManager.SetActiveScene(scene);
            return "aktif sahne: " + scene.name;
        }

        // =====================================================================
        //  v2: PREFAB MODU
        // =====================================================================
        static JToken OpenPrefabCmd(JObject p)
        {
            string path = (string)p["assetPath"];
            var stage = PrefabStageUtility.OpenPrefab(path);
            if (stage == null) throw new Exception("Prefab acilamadi: " + path);
            return new JObject
            {
                ["assetPath"] = stage.assetPath,
                ["rootName"] = stage.prefabContentsRoot.name,
                ["rootInstanceId"] = GetIID(stage.prefabContentsRoot),
                ["hint"] = "Artik get_scene/create_object/set_property gibi tum komutlar bu prefabin icinde calisir. Bitince save_prefab + close_prefab cagir."
            };
        }

        static PrefabStage RequireStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) throw new Exception("Prefab modu acik degil. Once open_prefab cagir.");
            return stage;
        }

        static JToken SavePrefabCmd()
        {
            var stage = RequireStage();
            PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
            return "prefab kaydedildi: " + stage.assetPath;
        }

        static JToken ClosePrefabCmd(JObject p)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) return "prefab modu zaten kapali";
            bool save = !Has(p, "save") || (bool)p["save"];
            if (save) PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath);
            StageUtility.GoToMainStage();
            return save ? "kaydedildi ve kapatildi" : "kaydedilmeden kapatildi";
        }

        // =====================================================================
        //  v2: EKRAN GORUNTUSU
        // =====================================================================
        static JToken CaptureScreenshot(JObject p)
        {
            string view = (string)p["view"] ?? "game";
            int w = Has(p, "width") ? (int)p["width"] : 960;
            int h = Has(p, "height") ? (int)p["height"] : 540;
            w = Mathf.Clamp(w, 64, 1920);
            h = Mathf.Clamp(h, 64, 1080);

            if (view == "scene")
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv == null || sv.camera == null) throw new Exception("Acik bir Scene View yok.");
                return RenderCameraToPng(sv.camera, w, h, false);
            }

            var cam = Camera.main;
            if (cam == null)
            {
                var cams = FindAll<Camera>();
                cam = cams.FirstOrDefault(c => c.enabled) ?? cams.FirstOrDefault();
            }
            if (cam == null) throw new Exception("Sahnede kamera yok. Once bir Camera olustur.");
            return RenderCameraToPng(cam, w, h, true);
        }

        static JToken RenderCameraToPng(Camera cam, int w, int h, bool includeOverlayCanvases)
        {
            var rt = new RenderTexture(w, h, 24);
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;
            // Screen Space - Overlay canvas'lar kamera renderina girmez;
            // gecici olarak ScreenSpaceCamera moduna alip geri donduruyoruz.
            var restored = new List<(Canvas cv, RenderMode mode, Camera wc, float pd)>();
            try
            {
                if (includeOverlayCanvases)
                {
                    foreach (var cv in FindAll<Canvas>())
                    {
                        if (cv.renderMode == RenderMode.ScreenSpaceOverlay && cv.isActiveAndEnabled)
                        {
                            restored.Add((cv, cv.renderMode, cv.worldCamera, cv.planeDistance));
                            cv.renderMode = RenderMode.ScreenSpaceCamera;
                            cv.worldCamera = cam;
                            cv.planeDistance = cam.nearClipPlane + 0.5f;
                        }
                    }
                    if (restored.Count > 0) Canvas.ForceUpdateCanvases();
                }

                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
                return new JObject
                {
                    ["format"] = "png",
                    ["width"] = w,
                    ["height"] = h,
                    ["camera"] = cam.name,
                    ["overlayCanvasesIncluded"] = restored.Count,
                    ["base64"] = Convert.ToBase64String(png)
                };
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                foreach (var r in restored)
                {
                    r.cv.renderMode = r.mode;
                    r.cv.worldCamera = r.wc;
                    r.cv.planeDistance = r.pd;
                }
            }
        }

        // =====================================================================
        //  v2.1: PREFAB / BUILD SETTINGS
        // =====================================================================
        static JToken SaveAsPrefab(JObject p)
        {
            var go = ResolveGameObject(p);
            string path = NormalizeAssetPath((string)p["savePath"], ".prefab");
            PrefabUtility.SaveAsPrefabAsset(go, path);
            return new JObject { ["savePath"] = path };
        }

        static JToken SetBuildSettingsScenes(JObject p)
        {
            var arr = p["scenes"] as JArray;
            if (arr == null) throw new Exception("scenes dizi parametresi gerekli.");
            var scenes = new List<EditorBuildSettingsScene>();
            foreach (var token in arr) scenes.Add(new EditorBuildSettingsScene((string)token, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            return "Build settings guncellendi (" + scenes.Count + " sahne)";
        }

        // =====================================================================
        //  v3: PLAY MODE
        // =====================================================================
        static JToken GetPlayState() => new JObject
        {
            ["isPlaying"] = EditorApplication.isPlaying,
            ["isPaused"] = EditorApplication.isPaused,
            ["isCompiling"] = EditorApplication.isCompiling,
            ["isUpdating"] = EditorApplication.isUpdating,
            ["willChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode
        };

        // =====================================================================
        //  v3: TEXTURE / SPRITE IMPORT AYARLARI
        // =====================================================================
        static JToken SetTextureImportSettings(JObject p)
        {
            string path = (string)p["assetPath"];
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) throw new Exception("TextureImporter bulunamadi (dosya bir texture/sprite mi?): " + path);
            if (Has(p, "textureType")) imp.textureType = (TextureImporterType)Enum.Parse(typeof(TextureImporterType), (string)p["textureType"], true);
            if (Has(p, "spriteMode")) imp.spriteImportMode = (SpriteImportMode)Enum.Parse(typeof(SpriteImportMode), (string)p["spriteMode"], true);
            if (Has(p, "pixelsPerUnit")) imp.spritePixelsPerUnit = (float)p["pixelsPerUnit"];
            if (Has(p, "filterMode")) imp.filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), (string)p["filterMode"], true);
            if (Has(p, "wrapMode")) imp.wrapMode = (TextureWrapMode)Enum.Parse(typeof(TextureWrapMode), (string)p["wrapMode"], true);
            if (Has(p, "maxTextureSize")) imp.maxTextureSize = (int)p["maxTextureSize"];
            if (Has(p, "compression")) imp.textureCompression = (TextureImporterCompression)Enum.Parse(typeof(TextureImporterCompression), (string)p["compression"], true);
            imp.SaveAndReimport();
            return new JObject
            {
                ["path"] = path,
                ["textureType"] = imp.textureType.ToString(),
                ["spriteMode"] = imp.spriteImportMode.ToString(),
                ["pixelsPerUnit"] = imp.spritePixelsPerUnit,
                ["filterMode"] = imp.filterMode.ToString()
            };
        }

        // =====================================================================
        //  v3: TILEMAP
        // =====================================================================
        static Tilemap ResolveTilemap(JObject p)
        {
            var go = ResolveGameObject(p);
            var tm = go.GetComponent<Tilemap>();
            if (tm == null) throw new Exception("Nesnede Tilemap component'i yok: " + go.name);
            return tm;
        }

        static JToken CreateTilemap(JObject p)
        {
            var gridGo = new GameObject((string)p["name"] ?? "Grid", typeof(Grid));
            var tmGo = new GameObject((string)p["tilemapName"] ?? "Tilemap", typeof(Tilemap), typeof(TilemapRenderer));
            tmGo.transform.SetParent(gridGo.transform, false);
            Undo.RegisterCreatedObjectUndo(gridGo, "MCP Create Tilemap");
            EditorSceneManager.MarkSceneDirty(gridGo.scene);
            return new JObject
            {
                ["gridInstanceId"] = GetIID(gridGo),
                ["tilemapInstanceId"] = GetIID(tmGo),
                ["tilemapPath"] = gridGo.name + "/" + tmGo.name
            };
        }

        static JToken CreateTileAsset(JObject p)
        {
            var sprite = LoadAssetSmart((string)p["sprite"], "PPtr<$Sprite>") as Sprite;
            if (sprite == null) throw new Exception("Sprite yuklenemedi: " + p["sprite"]);
            var tile = ScriptableObject.CreateInstance<Tile>();
            tile.sprite = sprite;
            if (Has(p, "color")) { var c = p["color"]; tile.color = new Color((float)c[0], (float)c[1], (float)c[2], c.Count() > 3 ? (float)c[3] : 1f); }
            string path = NormalizeAssetPath((string)p["assetPath"], ".asset");
            AssetDatabase.CreateAsset(tile, path);
            AssetDatabase.SaveAssets();
            return new JObject { ["path"] = path, ["sprite"] = sprite.name };
        }

        static JToken SetTiles(JObject p)
        {
            var tm = ResolveTilemap(p);
            TileBase shared = null;
            if (Has(p, "tileAssetPath"))
            {
                shared = AssetDatabase.LoadAssetAtPath<TileBase>((string)p["tileAssetPath"]);
                if (shared == null) throw new Exception("Tile asset bulunamadi: " + p["tileAssetPath"]);
            }
            var cells = p["cells"] as JArray;
            if (cells == null) throw new Exception("cells dizisi gerekli: [{x,y,z?,tileAssetPath?}, ...]");
            Undo.RegisterCompleteObjectUndo(tm, "MCP SetTiles");
            int count = 0;
            foreach (var cellTok in cells)
            {
                var cell = (JObject)cellTok;
                int x = (int)cell["x"], y = (int)cell["y"], z = Has(cell, "z") ? (int)cell["z"] : 0;
                TileBase t = shared;
                if (Has(cell, "tileAssetPath"))
                {
                    t = AssetDatabase.LoadAssetAtPath<TileBase>((string)cell["tileAssetPath"]);
                    if (t == null) throw new Exception("Tile asset bulunamadi: " + cell["tileAssetPath"]);
                }
                if (t == null) throw new Exception("Tile belirtilmedi: tileAssetPath ya global ver ya da her hucrede.");
                tm.SetTile(new Vector3Int(x, y, z), t);
                count++;
            }
            EditorSceneManager.MarkSceneDirty(tm.gameObject.scene);
            return new JObject { ["set"] = count };
        }

        // =====================================================================
        //  v3: ANIMATION / ANIMATOR
        // =====================================================================
        static JToken CreateSpriteAnimation(JObject p)
        {
            var frames = p["sprites"] as JArray;
            if (frames == null || frames.Count == 0) throw new Exception("sprites dizisi gerekli (sprite spec listesi, orn 'Assets/atlas.png#run_0').");
            float fps = Has(p, "frameRate") ? (float)p["frameRate"] : 12f;
            var clip = new AnimationClip { frameRate = fps };
            var keys = new ObjectReferenceKeyframe[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                var sprite = LoadAssetSmart((string)frames[i], "PPtr<$Sprite>") as Sprite;
                if (sprite == null) throw new Exception("Sprite yuklenemedi: " + frames[i]);
                keys[i] = new ObjectReferenceKeyframe { time = i / fps, value = sprite };
            }
            var binding = new EditorCurveBinding { type = typeof(SpriteRenderer), path = "", propertyName = "m_Sprite" };
            AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
            bool loop = !Has(p, "loop") || (bool)p["loop"];
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            string path = NormalizeAssetPath((string)p["assetPath"], ".anim");
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            return new JObject { ["path"] = path, ["frames"] = frames.Count, ["frameRate"] = fps, ["loop"] = loop };
        }

        static JToken CreateAnimationClip(JObject p)
        {
            float fps = Has(p, "frameRate") ? (float)p["frameRate"] : 60f;
            var clip = new AnimationClip { frameRate = fps };
            var curves = p["curves"] as JArray;
            if (curves == null || curves.Count == 0) throw new Exception("curves dizisi gerekli: [{type, path?, property, keys:[{time,value}]}]");
            foreach (var cvTok in curves)
            {
                var cv = (JObject)cvTok;
                var compType = FindType((string)cv["type"]);
                string relPath = Has(cv, "path") ? (string)cv["path"] : "";
                var keysArr = cv["keys"] as JArray;
                if (keysArr == null) throw new Exception("Her curve icin keys dizisi gerekli.");
                var curve = new AnimationCurve();
                foreach (var kTok in keysArr) { var k = (JObject)kTok; curve.AddKey((float)k["time"], (float)k["value"]); }
                var binding = EditorCurveBinding.FloatCurve(relPath, compType, (string)cv["property"]);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
            bool loop = Has(p, "loop") && (bool)p["loop"];
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            string path = NormalizeAssetPath((string)p["assetPath"], ".anim");
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            return new JObject { ["path"] = path, ["curves"] = curves.Count, ["frameRate"] = fps };
        }

        static JToken CreateAnimatorController(JObject p)
        {
            string path = NormalizeAssetPath((string)p["assetPath"], ".controller");
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
            var sm = ctrl.layers[0].stateMachine;

            if (p["parameters"] is JArray pars)
                foreach (var parTok in pars)
                {
                    var par = (JObject)parTok;
                    var pt = (AnimatorControllerParameterType)Enum.Parse(typeof(AnimatorControllerParameterType), (string)par["type"], true);
                    ctrl.AddParameter((string)par["name"], pt);
                }

            var stateMap = new Dictionary<string, AnimatorState>();
            AnimatorState defState = null;
            if (p["states"] is JArray states)
                foreach (var stTok in states)
                {
                    var st = (JObject)stTok;
                    string sname = (string)st["name"];
                    var state = sm.AddState(sname);
                    if (Has(st, "clip"))
                    {
                        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>((string)st["clip"]);
                        if (clip == null) throw new Exception("Clip bulunamadi: " + st["clip"]);
                        state.motion = clip;
                    }
                    stateMap[sname] = state;
                    if (defState == null || (Has(st, "default") && (bool)st["default"])) defState = state;
                }
            if (defState != null) sm.defaultState = defState;

            if (p["transitions"] is JArray trs)
                foreach (var trTok in trs)
                {
                    var tr = (JObject)trTok;
                    if (!stateMap.TryGetValue((string)tr["from"], out var from)) throw new Exception("Gecis kaynak state yok: " + tr["from"]);
                    if (!stateMap.TryGetValue((string)tr["to"], out var to)) throw new Exception("Gecis hedef state yok: " + tr["to"]);
                    var t = from.AddTransition(to);
                    t.hasExitTime = Has(tr, "hasExitTime") && (bool)tr["hasExitTime"];
                    if (Has(tr, "exitTime")) { t.hasExitTime = true; t.exitTime = (float)tr["exitTime"]; }
                    if (tr["condition"] is JObject cond)
                    {
                        var mode = (AnimatorConditionMode)Enum.Parse(typeof(AnimatorConditionMode), (string)cond["mode"], true);
                        float threshold = Has(cond, "threshold") ? (float)cond["threshold"] : 0f;
                        t.AddCondition(mode, threshold, (string)cond["parameter"]);
                    }
                }
            AssetDatabase.SaveAssets();
            return new JObject { ["path"] = path, ["states"] = stateMap.Count };
        }

        static JToken AssignAnimatorController(JObject p)
        {
            var go = ResolveGameObject(p);
            var anim = go.GetComponent<Animator>();
            if (anim == null) anim = Undo.AddComponent<Animator>(go);
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>((string)p["controllerPath"]);
            if (ctrl == null) throw new Exception("Animator Controller bulunamadi: " + p["controllerPath"]);
            anim.runtimeAnimatorController = ctrl;
            EditorUtility.SetDirty(anim);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return new JObject { ["object"] = go.name, ["controller"] = AssetDatabase.GetAssetPath(ctrl) };
        }
    }
}
