// Setup window for the Unity MCP Bridge.
// Removes the two fiddly steps of installation: finding server.js and hand-editing
// a client's MCP config. Everything it writes is a plain JSON file the user can
// inspect, and it never overwrites unrelated servers in those files.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace McpUnity
{
    public class McpBridgeSetup : EditorWindow
    {
        const string PrefKey = "UnityMcp.ServerJsPath";
        const int Port = 6400;

        string _serverPath;
        string _status = "";
        MessageType _statusKind = MessageType.None;
        Vector2 _scroll;

        [MenuItem("Tools/MCP Bridge/Setup...", false, 0)]
        public static void Open()
        {
            var w = GetWindow<McpBridgeSetup>(true, "Unity MCP Bridge — Setup");
            w.minSize = new Vector2(560, 420);
            w._serverPath = EditorPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(w._serverPath)) w._serverPath = Guess() ?? "";
        }

        // Looks in the usual places so most users never have to browse for it.
        static string Guess()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new List<string>
            {
                Path.GetFullPath(Path.Combine(Application.dataPath, "../../unity-mcp/mcp-server/server.js")),
                Path.Combine(home, "unity-mcp/mcp-server/server.js"),
                Path.Combine(home, "İndirilenler/unity-mcp/mcp-server/server.js"),
                Path.Combine(home, "Downloads/unity-mcp/mcp-server/server.js"),
                Path.Combine(home, "Documents/unity-mcp/mcp-server/server.js"),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("1 — Unity side", EditorStyles.boldLabel);
            bool listening = McpBridge.IsRunning;
            EditorGUILayout.HelpBox(
                listening
                    ? $"Bridge is listening on 127.0.0.1:{Port}."
                    : "Bridge is NOT listening. Use Tools > MCP Bridge > Restart Server.",
                listening ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("2 — Node server", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _serverPath = EditorGUILayout.TextField("server.js", _serverPath);
                if (GUILayout.Button("Browse", GUILayout.Width(70)))
                {
                    var p = EditorUtility.OpenFilePanel("Select server.js", "", "js");
                    if (!string.IsNullOrEmpty(p)) { _serverPath = p; EditorPrefs.SetString(PrefKey, p); }
                }
            }

            bool ok = !string.IsNullOrEmpty(_serverPath) && File.Exists(_serverPath);
            if (!ok)
                EditorGUILayout.HelpBox(
                    "Clone the repo and point this at mcp-server/server.js:\n" +
                    "git clone https://github.com/canakbass/unity-mcp.git", MessageType.Warning);
            else
            {
                bool deps = Directory.Exists(Path.Combine(Path.GetDirectoryName(_serverPath), "node_modules"));
                if (!deps && GUILayout.Button("Run npm install"))
                    RunNpmInstall(Path.GetDirectoryName(_serverPath));
                if (deps) EditorGUILayout.HelpBox("Dependencies installed.", MessageType.Info);
            }

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(!ok))
            {
                EditorGUILayout.LabelField("3 — Connect a client", EditorStyles.boldLabel);
                if (GUILayout.Button("Write .mcp.json for this project (Claude Code)"))
                    WriteConfig(Path.Combine(ProjectRoot(), ".mcp.json"), "Claude Code");
                if (GUILayout.Button("Write .cursor/mcp.json for this project (Cursor)"))
                    WriteConfig(Path.Combine(ProjectRoot(), ".cursor/mcp.json"), "Cursor");
                if (GUILayout.Button("Copy 'claude mcp add' command to clipboard"))
                {
                    EditorGUIUtility.systemCopyBuffer =
                        $"claude mcp add unity -s user -- node \"{_serverPath}\"";
                    Set("Command copied. Paste it into a terminal.", MessageType.Info);
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Token budget (optional)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "All 52 tool schemas cost an agent ~8.8k tokens per session. Add an env var " +
                "UNITY_MCP_TOOLS to the config to load only what you need, e.g. \"core\" (19 tools, " +
                "~3.1k) or \"core,tilemap\". Leaving it unset exposes everything.", MessageType.None);

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_status, _statusKind);
            }
            EditorGUILayout.EndScrollView();
        }

        void Set(string msg, MessageType kind) { _status = msg; _statusKind = kind; Repaint(); }

        static string ProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // Merges into an existing config instead of replacing it, so other MCP
        // servers the user already configured survive.
        void WriteConfig(string path, string clientName)
        {
            try
            {
                JObject root;
                if (File.Exists(path))
                {
                    root = JObject.Parse(File.ReadAllText(path));
                    File.Copy(path, path + ".bak", true);
                }
                else root = new JObject();

                var servers = root["mcpServers"] as JObject;
                if (servers == null) { servers = new JObject(); root["mcpServers"] = servers; }

                servers["unity"] = new JObject
                {
                    ["command"] = "node",
                    ["args"] = new JArray { _serverPath.Replace("\\", "/") }
                };

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, root.ToString(Newtonsoft.Json.Formatting.Indented));
                Set($"Wrote {path}\nRestart {clientName} so it picks the server up.", MessageType.Info);
                UnityEngine.Debug.Log($"[MCP Bridge] Wrote MCP config: {path}");
            }
            catch (Exception e)
            {
                Set("Could not write config: " + e.Message, MessageType.Error);
            }
        }

        void RunNpmInstall(string dir)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Application.platform == RuntimePlatform.WindowsEditor ? "cmd.exe" : "npm",
                    Arguments = Application.platform == RuntimePlatform.WindowsEditor ? "/c npm install" : "install",
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    if (p.ExitCode == 0) Set("npm install finished.", MessageType.Info);
                    else Set("npm install failed:\n" + (string.IsNullOrEmpty(err) ? outp : err), MessageType.Error);
                }
            }
            catch (Exception e)
            {
                Set("Could not run npm (is Node installed and on PATH?): " + e.Message, MessageType.Error);
            }
        }
    }
}
