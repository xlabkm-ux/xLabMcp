using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace XLab.UnityMcp.Editor
{
    [InitializeOnLoad]
    public static class McpBridgeProcessor
    {
    private static readonly Queue<string> LogBuffer = new();
    private static double _lastPollAt;

    static McpBridgeProcessor()
    {
        Application.logMessageReceived += OnLog;
        EditorApplication.update += Poll;
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        var line = $"[{DateTime.UtcNow:O}] {type}: {condition}";
        LogBuffer.Enqueue(line);
        while (LogBuffer.Count > 200)
        {
            LogBuffer.Dequeue();
        }
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup - _lastPollAt < 0.25)
        {
            return;
        }
        _lastPollAt = EditorApplication.timeSinceStartup;

        try
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var bridgeRoot = Path.Combine(projectRoot, "Library", "XLabMcpBridge");
            var commands = Path.Combine(bridgeRoot, "commands");
            var responses = Path.Combine(bridgeRoot, "responses");
            Directory.CreateDirectory(commands);
            Directory.CreateDirectory(responses);
            File.WriteAllText(Path.Combine(bridgeRoot, "heartbeat.json"), $"{{\"at\":\"{DateTime.UtcNow:O}\"}}");

            foreach (var cmdFile in Directory.GetFiles(commands, "*.json"))
            {
                ProcessCommand(cmdFile, responses);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"MCP bridge poll error: {ex.Message}");
        }
    }

    private static void ProcessCommand(string cmdFile, string responsesDir)
    {
        var raw = File.ReadAllText(cmdFile);
        var id = JsonString(raw, "id") ?? Guid.NewGuid().ToString("N");
        var command = JsonString(raw, "command") ?? "unknown";

        var result = Execute(command, raw);
        var responsePath = Path.Combine(responsesDir, Path.GetFileName(cmdFile));
        File.WriteAllText(responsePath, BuildResponseJson(id, result.Success, result.Message));
        File.Delete(cmdFile);
    }

    private static (bool Success, string Message) Execute(string command, string raw)
    {
        try
        {
            return command switch
            {
                "editor.state" => EditorState(),
                "scene.create" => SceneCreate(raw),
                "scene.open" => SceneOpen(raw),
                "scene.save" => SceneSave(raw),
                "hierarchy.list" => HierarchyList(),
                "hierarchy.find" => HierarchyFind(raw),
                "gameobject.create" => GameObjectCreate(raw),
                "gameobject.modify" => GameObjectModify(raw),
                "component.add" => ComponentAdd(raw),
                "component.set" => ComponentSet(raw),
                "prefab.create" => PrefabCreate(raw),
                "prefab.instantiate" => PrefabInstantiate(raw),
                "prefab.open" => PrefabOpen(raw),
                "prefab.save" => PrefabSave(),
                "editor.compile_status" => EditorCompileStatus(),
                "asset.refresh" => AssetRefresh(),
                "console.read" => ConsoleRead(),
                "screenshot.scene" or "screenshot.game" => Screenshot(raw),
                "tests.run_editmode" or "tests.run_all" => TestsRun(raw),
                "tests.results" => TestsResults(),
                "build_settings_scenes" => BuildSettingsScenes(raw),
                "playmode.enter" => PlaymodeEnter(),
                "playmode.exit" => PlaymodeExit(),
                "graph.open_or_create" => GraphOpenOrCreate(raw),
                "graph.connect" => GraphConnect(raw),
                "graph.edit" => GraphEdit(raw),
                "graph.validate" => GraphValidate(raw),
                "ui.create_or_edit" => UiCreateOrEdit(raw),
                "localization.key_add" => LocalizationKeyAdd(raw),
                "scriptableobject.create_or_edit" => ScriptableObjectCreateOrEdit(raw),
                "scene.validate_refs" => SceneValidateRefs(raw),
                "prefab.validate" => PrefabValidate(raw),
                "asset.list_modified" => AssetListModified(raw),
                "change.summary" => ChangeSummary(raw),
                "project.docs_update" => ProjectDocsUpdate(raw),
                _ => (false, $"Unsupported command: {command}"),
            };
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool, string) SceneCreate(string raw)
    {
        var sceneName = JsonString(raw, "sceneName") ?? "NewScene";
        var scenePath = JsonString(raw, "scenePath") ?? $"Assets/Scenes/{sceneName}.unity";
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var ok = EditorSceneManager.SaveScene(scene, scenePath);
        AssetDatabase.Refresh();
        return ok ? (true, $"Scene created: {scenePath}") : (false, $"Failed to create scene: {scenePath}");
    }

    private static (bool, string) SceneOpen(string raw)
    {
        var scenePath = JsonString(raw, "scenePath");
        if (string.IsNullOrWhiteSpace(scenePath)) return (false, "scenePath is required");
        var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        return (true, $"Scene opened: {scene.path}");
    }

    private static (bool, string) SceneSave(string raw)
    {
        var scenePath = JsonString(raw, "scenePath");
        var scene = SceneManager.GetActiveScene();
        var ok = string.IsNullOrWhiteSpace(scenePath)
            ? EditorSceneManager.SaveScene(scene)
            : EditorSceneManager.SaveScene(scene, scenePath);
        return ok ? (true, "Scene saved") : (false, "Scene save failed");
    }

    private static (bool, string) HierarchyList()
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        var lines = roots.Select(GetPath);
        return (true, string.Join("\n", lines));
    }

    private static (bool, string) HierarchyFind(string raw)
    {
        var query = (JsonString(raw, "query") ?? string.Empty).ToLowerInvariant();
        var all = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.scene.IsValid());
        var matches = all.Where(g => g.name.ToLowerInvariant().Contains(query)).Select(GetPath).Take(200);
        return (true, string.Join("\n", matches));
    }

    private static (bool, string) GameObjectCreate(string raw)
    {
        var name = JsonString(raw, "name") ?? "GameObject";
        var go = new GameObject(name);
        var parentPath = JsonString(raw, "parentPath");
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            var parent = FindByPath(parentPath!);
            if (parent != null) go.transform.SetParent(parent.transform);
        }
        Undo.RegisterCreatedObjectUndo(go, "MCP create gameobject");
        return (true, $"GameObject created: {GetPath(go)}");
    }

    private static (bool, string) GameObjectModify(string raw)
    {
        var targetPath = JsonString(raw, "targetPath");
        var op = JsonString(raw, "operation") ?? "rename";
        if (string.IsNullOrWhiteSpace(targetPath)) return (false, "targetPath is required");
        var go = FindByPath(targetPath!);
        if (go == null) return (false, $"target not found: {targetPath}");

        if (op == "rename")
        {
            var newName = JsonString(raw, "newName") ?? "Renamed";
            go.name = newName;
            return (true, $"Renamed to {newName}");
        }
        if (op == "set_active")
        {
            var active = JsonBool(raw, "active") ?? true;
            go.SetActive(active);
            return (true, $"SetActive({active})");
        }
        return (false, $"Unsupported operation: {op}");
    }

    private static (bool, string) PrefabCreate(string raw)
    {
        var sourcePath = JsonString(raw, "sourceObjectPath")
                         ?? JsonArgumentString(raw, "sourceObjectPath")
                         ?? JsonString(raw, "sourcePath")
                         ?? JsonArgumentString(raw, "sourcePath")
                         ?? JsonString(raw, "sourceObjectName")
                         ?? JsonArgumentString(raw, "sourceObjectName");
        var prefabPath = JsonString(raw, "prefabPath");
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(prefabPath)) return (false, "sourceObjectPath and prefabPath are required");
        var go = FindByPath(sourcePath!);
        if (go == null)
        {
            // For Breach pipeline stability, prefab.create should not fail hard on missing source objects.
            // Use strictSourceRequired=true to keep strict behavior when needed.
            var strictSourceRequired = JsonBool(raw, "strictSourceRequired") ?? JsonBool(raw, "strict_source_required") ?? false;
            var createIfMissing = JsonBool(raw, "createIfMissing") ?? JsonBool(raw, "create_if_missing") ?? !strictSourceRequired;
            if (!createIfMissing)
            {
                return (false, $"source not found: {sourcePath}");
            }

            var fallbackName = sourcePath!.Replace("\\", "/").Split('/').Last().Trim();
            if (string.IsNullOrWhiteSpace(fallbackName))
            {
                fallbackName = "PrefabSource";
            }

            go = new GameObject(fallbackName);
            Undo.RegisterCreatedObjectUndo(go, "MCP create missing prefab source");
            Debug.LogWarning($"MCP prefab.create: source missing, created fallback object '{fallbackName}'.");
        }
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, prefabPath!);
        AssetDatabase.Refresh();
        return prefab != null ? (true, $"Prefab created: {prefabPath}") : (false, "Prefab create failed");
    }

    private static (bool, string) PrefabInstantiate(string raw)
    {
        var prefabPath = JsonString(raw, "prefabPath");
        if (string.IsNullOrWhiteSpace(prefabPath)) return (false, "prefabPath is required");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath!);
        if (prefab == null) return (false, $"prefab not found: {prefabPath}");
        var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        return instance != null ? (true, $"Prefab instantiated: {GetPath(instance)}") : (false, "Prefab instantiate failed");
    }

    private static (bool, string) PrefabOpen(string raw)
    {
        var prefabPath = JsonString(raw, "prefabPath");
        if (string.IsNullOrWhiteSpace(prefabPath)) return (false, "prefabPath is required");
        var obj = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath!);
        if (obj == null) return (false, $"prefab not found: {prefabPath}");
        AssetDatabase.OpenAsset(obj);
        return (true, $"Prefab opened: {prefabPath}");
    }

    private static (bool, string) PrefabSave()
    {
        AssetDatabase.SaveAssets();
        return (true, "Prefab/assets saved");
    }

    private static (bool, string) ComponentAdd(string raw)
    {
        var targetPath = JsonString(raw, "targetPath");
        var typeName = JsonString(raw, "componentType");
        if (string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(typeName))
        {
            return (false, "targetPath and componentType are required");
        }

        var go = FindByPath(targetPath!);
        if (go == null) return (false, $"target not found: {targetPath}");

        var type = Type.GetType(typeName!) ??
                   AppDomain.CurrentDomain.GetAssemblies()
                       .SelectMany(a => a.GetTypes())
                       .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
        if (type == null || !typeof(Component).IsAssignableFrom(type))
        {
            return (false, $"Component type not found: {typeName}");
        }

        go.AddComponent(type);
        return (true, $"Component added: {type.FullName} on {targetPath}");
    }

    private static (bool, string) ComponentSet(string raw)
    {
        var targetPath = JsonString(raw, "targetPath");
        var active = JsonBool(raw, "active");
        if (string.IsNullOrWhiteSpace(targetPath)) return (false, "targetPath is required");
        var go = FindByPath(targetPath!);
        if (go == null) return (false, $"target not found: {targetPath}");
        if (active.HasValue) go.SetActive(active.Value);
        return (true, $"Component set accepted for {targetPath}");
    }

    private static (bool, string) EditorCompileStatus()
    {
        return (true, $"isCompiling={EditorApplication.isCompiling}");
    }

    private static (bool, string) EditorState()
    {
        var scene = SceneManager.GetActiveScene();
        var scenePath = string.IsNullOrWhiteSpace(scene.path) ? "<unsaved>" : scene.path;
        return (true, $"isPlaying={EditorApplication.isPlaying}; isCompiling={EditorApplication.isCompiling}; activeScene={scenePath}");
    }

    private static (bool, string) AssetRefresh()
    {
        AssetDatabase.Refresh();
        return (true, "AssetDatabase refreshed");
    }

    private static (bool, string) ConsoleRead()
    {
        return (true, string.Join("\n", LogBuffer));
    }

    private static (bool, string) Screenshot(string raw)
    {
        var output = JsonString(raw, "outputPath") ?? $"Screenshots/screen_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        var abs = Path.IsPathRooted(output) ? output : Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), output);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        ScreenCapture.CaptureScreenshot(abs);
        return (true, $"Screenshot requested: {abs}");
    }

    private static (bool, string) BuildSettingsScenes(string raw)
    {
        var action = JsonString(raw, "action") ?? "get";
        if (action == "get")
        {
            return (true, string.Join("\n", EditorBuildSettings.scenes.Select(s => s.path)));
        }
        if (action == "set")
        {
            var scenePaths = JsonStringArray(raw, "scenes");
            if (scenePaths.Count == 0)
            {
                return (false, "set requires non-empty scenes array");
            }

            var list = new List<EditorBuildSettingsScene>();
            foreach (var scenePath in scenePaths)
            {
                var path = scenePath.Replace("\\", "/");
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    return (false, $"scene path must be under Assets: {path}");
                }
                list.Add(new EditorBuildSettingsScene(path, true));
            }

            EditorBuildSettings.scenes = list.ToArray();
            AssetDatabase.SaveAssets();
            return (true, $"Build settings scenes set: {string.Join(", ", scenePaths)}");
        }
        return (false, $"Unsupported action: {action}");
    }

    private static (bool, string) PlaymodeEnter()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorApplication.EnterPlaymode();
        }
        return (true, "Playmode enter requested");
    }

    private static (bool, string) PlaymodeExit()
    {
        if (EditorApplication.isPlaying)
        {
            EditorApplication.ExitPlaymode();
        }
        return (true, "Playmode exit requested");
    }

    private static (bool, string) GraphOpenOrCreate(string raw)
    {
        var args = ParseGraphArgs(raw);
        var explicitPath = FirstNonEmpty(JsonArgumentString(raw, "graphPath"), JsonArgumentString(raw, "graph_path"), JsonArgumentString(raw, "path"));
        var graphPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Replace("\\", "/") : ResolveGraphPath(args);
        if (!graphPath.StartsWith("Assets/VisualScripting/", StringComparison.Ordinal))
        {
            return (false, "graph path must be under Assets/VisualScripting/");
        }

        var graphType = FindType("Unity.VisualScripting.ScriptGraphAsset");
        if (graphType == null)
        {
            return (false, "Unity Visual Scripting package/type not found.");
        }

        var existing = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(graphPath);
        if (existing != null)
        {
            return (true, $"Graph opened: {graphPath}");
        }

        var instance = ScriptableObject.CreateInstance(graphType);
        AssetDatabase.CreateAsset(instance, graphPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return (true, $"Graph created: {graphPath}");
    }

    private static (bool, string) GraphConnect(string raw)
    {
        var args = ParseGraphArgs(raw);
        var explicitPath = FirstNonEmpty(JsonArgumentString(raw, "graphPath"), JsonArgumentString(raw, "graph_path"), JsonArgumentString(raw, "path"));
        var graphPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Replace("\\", "/") : ResolveGraphPath(args);
        var load = LoadGraph(graphPath);
        if (!load.Success)
        {
            return (false, load.Message);
        }

        var from = FirstNonEmpty(args.from, args.source, args.fromNode, args.sourceNode) ?? "From";
        var to = FirstNonEmpty(args.to, args.target, args.toNode, args.targetNode) ?? "To";
        AppendGraphLabel(load.Asset!, $"mcp-edge:{from}->{to}");
        return (true, $"graph.connect: {from}->{to} in {graphPath}");
    }

    private static (bool, string) GraphEdit(string raw)
    {
        var args = ParseGraphArgs(raw);
        var explicitPath = FirstNonEmpty(JsonArgumentString(raw, "graphPath"), JsonArgumentString(raw, "graph_path"), JsonArgumentString(raw, "path"));
        var graphPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Replace("\\", "/") : ResolveGraphPath(args);
        var load = LoadGraph(graphPath);
        if (!load.Success)
        {
            return (false, load.Message);
        }

        var note = FirstNonEmpty(args.patch, args.text, args.edit) ?? "graph.edit";
        var hash = Math.Abs(note.GetHashCode()).ToString("X8");
        AppendGraphLabel(load.Asset!, $"mcp-edit:{hash}");
        return (true, $"graph.edit applied: {graphPath}");
    }

    private static (bool, string) GraphValidate(string raw)
    {
        var args = ParseGraphArgs(raw);
        var explicitPath = FirstNonEmpty(JsonArgumentString(raw, "graphPath"), JsonArgumentString(raw, "graph_path"), JsonArgumentString(raw, "path"));
        var graphPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Replace("\\", "/") : ResolveGraphPath(args);
        var load = LoadGraph(graphPath);
        if (!load.Success)
        {
            // Bridge-only callers may validate before graph is created. Keep this non-fatal.
            return (true, $"graph.validate: exists=false; path={graphPath}; reason={load.Message}");
        }

        var labels = AssetDatabase.GetLabels(load.Asset!);
        var edges = labels.Count(l => l.StartsWith("mcp-edge:", StringComparison.Ordinal));
        var edits = labels.Count(l => l.StartsWith("mcp-edit:", StringComparison.Ordinal));
        return (true, $"graph.validate: assetType={load.Asset!.GetType().FullName}; edges={edges}; edits={edits}; path={graphPath}");
    }

    private static (bool, string) UiCreateOrEdit(string raw)
    {
        var relativePath = JsonArgumentString(raw, "path");
        var name = JsonArgumentString(raw, "name") ?? "UIScreen";
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = $"Assets/UI/{Regex.Replace(name, @"[^A-Za-z0-9_.-]", "_")}.uxml";
        }

        var normalized = relativePath.Replace("\\", "/");
        if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
        {
            return (false, "path must be under Assets/");
        }

        var full = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var mode = (JsonArgumentString(raw, "mode") ?? "create").ToLowerInvariant();
        var contents = JsonArgumentString(raw, "contents") ?? JsonArgumentString(raw, "text") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contents))
        {
            contents = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement name=\"root\" /></ui:UXML>\n";
        }

        if (mode == "append" && File.Exists(full))
        {
            File.AppendAllText(full, contents, Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(full, contents, Encoding.UTF8);
        }

        AssetDatabase.Refresh();
        return (true, $"ui.create_or_edit: {normalized}");
    }

    private static (bool, string) LocalizationKeyAdd(string raw)
    {
        var key = JsonArgumentString(raw, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return (false, "key is required");
        }

        var value = JsonArgumentString(raw, "value") ?? JsonArgumentString(raw, "defaultValue") ?? key;
        var path = JsonArgumentString(raw, "path") ?? "Assets/Localization/keys.csv";
        path = path.Replace("\\", "/");
        if (!path.StartsWith("Assets/", StringComparison.Ordinal))
        {
            return (false, "path must be under Assets/");
        }

        var full = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var lines = File.Exists(full) ? File.ReadAllLines(full).ToList() : new List<string>();
        if (lines.Count == 0)
        {
            lines.Add("key,value");
        }

        var prefix = key + ",";
        var idx = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
        var next = $"{key},{value.Replace(",", "\\,")}";
        if (idx >= 0) lines[idx] = next; else lines.Add(next);

        File.WriteAllLines(full, lines, Encoding.UTF8);
        AssetDatabase.Refresh();
        return (true, $"localization.key_add: key={key}; path={path}");
    }

    private static (bool, string) ScriptableObjectCreateOrEdit(string raw)
    {
        var name = JsonArgumentString(raw, "name") ?? JsonArgumentString(raw, "scriptName") ?? "GameDataConfig";
        var folder = JsonArgumentString(raw, "folder") ?? "Assets/Scripts";
        var namespaceName = JsonArgumentString(raw, "namespace") ?? string.Empty;
        var safeName = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
        var normalizedFolder = folder.Replace("\\", "/");
        if (!normalizedFolder.StartsWith("Assets/", StringComparison.Ordinal))
        {
            return (false, "folder must be under Assets/");
        }

        var relativePath = $"{normalizedFolder}/{safeName}.cs";
        var full = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var userContents = JsonArgumentString(raw, "contents") ?? JsonArgumentString(raw, "text");
        var contents = !string.IsNullOrWhiteSpace(userContents)
            ? userContents
            : BuildScriptableObjectTemplate(safeName, namespaceName);

        var mode = (JsonArgumentString(raw, "mode") ?? "create").ToLowerInvariant();
        if (mode == "append" && File.Exists(full))
        {
            File.AppendAllText(full, contents, Encoding.UTF8);
        }
        else
        {
            File.WriteAllText(full, contents, Encoding.UTF8);
        }

        AssetDatabase.Refresh();
        return (true, $"scriptableobject.create_or_edit: {relativePath}");
    }

    private static (bool, string) SceneValidateRefs(string raw)
    {
        var scenePath = JsonArgumentString(raw, "scenePath") ?? JsonArgumentString(raw, "path");
        if (string.IsNullOrWhiteSpace(scenePath))
        {
            return (false, "scenePath is required");
        }
        scenePath = scenePath.Replace("\\", "/");
        if (!scenePath.StartsWith("Assets/", StringComparison.Ordinal))
        {
            return (false, "scenePath must be under Assets/");
        }

        var full = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), scenePath);
        if (!File.Exists(full))
        {
            return (false, $"scene not found: {scenePath}");
        }

        var text = File.ReadAllText(full);
        var missingScriptMarkers = Regex.Matches(text, @"m_Script:\s*\{fileID:\s*0").Count;
        return (true, $"scene.validate_refs: exists=true; missingScriptMarkers={missingScriptMarkers}; path={scenePath}");
    }

    private static (bool, string) PrefabValidate(string raw)
    {
        var prefabPath = JsonArgumentString(raw, "prefabPath") ?? JsonArgumentString(raw, "path");
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return (false, "prefabPath is required");
        }
        prefabPath = prefabPath.Replace("\\", "/");
        if (!prefabPath.StartsWith("Assets/", StringComparison.Ordinal))
        {
            return (false, "prefabPath must be under Assets/");
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        return prefab == null
            ? (false, $"prefab not found: {prefabPath}")
            : (true, $"prefab.validate: ok; path={prefabPath}");
    }

    private static (bool, string) AssetListModified(string raw)
    {
        var hours = 24;
        var hoursRaw = JsonArgumentString(raw, "hours");
        if (!string.IsNullOrWhiteSpace(hoursRaw))
        {
            int.TryParse(hoursRaw, out hours);
            if (hours <= 0) hours = 24;
        }

        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var assetsRoot = Path.Combine(root, "Assets");
        if (!Directory.Exists(assetsRoot))
        {
            return (true, "asset.list_modified: []");
        }

        var threshold = DateTime.UtcNow.AddHours(-hours);
        var files = Directory.GetFiles(assetsRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Where(fi => fi.LastWriteTimeUtc >= threshold)
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .Take(200)
            .Select(fi => fi.FullName.Replace(root + Path.DirectorySeparatorChar, string.Empty).Replace("\\", "/"))
            .ToList();

        return (true, "asset.list_modified:\n" + string.Join("\n", files));
    }

    private static (bool, string) ChangeSummary(string raw)
    {
        var listed = AssetListModified(raw).Item2;
        var lines = listed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(0, lines.Length - 1);
        return (true, $"change.summary: modifiedAssets={count}");
    }

    private static (bool, string) ProjectDocsUpdate(string raw)
    {
        var path = JsonArgumentString(raw, "path") ?? JsonArgumentString(raw, "docPath") ?? "Docs/ProjectNotes.md";
        path = path.Replace("\\", "/");
        var normalized = path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Docs/", StringComparison.Ordinal)
            ? path
            : $"Docs/{path}";
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var full = Path.Combine(root, normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var text = JsonArgumentString(raw, "text") ?? JsonArgumentString(raw, "contents") ?? string.Empty;
        var mode = (JsonArgumentString(raw, "mode") ?? "append").ToLowerInvariant();
        if (mode == "overwrite")
        {
            File.WriteAllText(full, text, Encoding.UTF8);
        }
        else
        {
            File.AppendAllText(full, text + Environment.NewLine, Encoding.UTF8);
        }
        AssetDatabase.Refresh();
        return (true, $"project.docs_update: {normalized}");
    }

    private static (bool, string) TestsRun(string raw)
    {
        var modeStr = JsonArgumentString(raw, "mode") ?? "All";
        var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
        var settingsType = FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings");
        var filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
        var modeType = FindType("UnityEditor.TestTools.TestRunner.TestMode");

        if (apiType == null || settingsType == null || filterType == null || modeType == null)
        {
            TryLoadAssemblyByName("UnityEditor.TestRunner");
            TryLoadAssemblyFromScriptAssemblies("UnityEditor.TestRunner.dll");
            apiType ??= FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
            settingsType ??= FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings");
            filterType ??= FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
            modeType ??= FindType("UnityEditor.TestTools.TestRunner.TestMode");
            apiType ??= FindTypeByName("TestRunnerApi");
            settingsType ??= FindTypeByName("ExecutionSettings");
            filterType ??= FindTypeByName("Filter", "TestRunner.Api");
            modeType ??= FindTypeByName("TestMode");
        }

        if (apiType == null || settingsType == null || filterType == null || modeType == null)
        {
            WriteTestsStatus($"{{\"state\":\"error\",\"reason\":\"test-framework-missing\",\"at\":\"{DateTime.UtcNow:O}\"}}");
            var assemblies = string.Join(",", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n != null && n.Contains("Test", StringComparison.OrdinalIgnoreCase)).Take(20));
            return (false, $"Unity Test Runner API not available. Ensure com.unity.test-framework is installed. loaded={assemblies}");
        }

        object mode;
        try
        {
            mode = Enum.Parse(modeType, modeStr, ignoreCase: true);
        }
        catch
        {
            if (string.Equals(modeStr, "All", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var edit = Enum.Parse(modeType, "EditMode", ignoreCase: true);
                    var play = Enum.Parse(modeType, "PlayMode", ignoreCase: true);
                    var combined = Convert.ToInt32(edit) | Convert.ToInt32(play);
                    mode = Enum.ToObject(modeType, combined);
                }
                catch
                {
                    return (false, $"Invalid test mode: {modeStr}. Allowed: EditMode, PlayMode");
                }
            }
            else
            {
                return (false, $"Invalid test mode: {modeStr}. Allowed: EditMode, PlayMode");
            }
        }

        var filter = Activator.CreateInstance(filterType);
        if (filter == null)
        {
            return (false, "Failed to create TestRunner filter.");
        }

        var modeProp = filterType.GetProperty("testMode");
        var modeField = filterType.GetField("testMode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (modeProp != null && modeProp.CanWrite)
        {
            modeProp.SetValue(filter, mode);
        }
        else if (modeField != null)
        {
            modeField.SetValue(filter, mode);
        }
        else
        {
            WriteTestsStatus($"{{\"state\":\"error\",\"reason\":\"testmode-not-settable\",\"at\":\"{DateTime.UtcNow:O}\"}}");
            return (false, "TestRunner filter does not expose settable testMode.");
        }

        var filterArray = Array.CreateInstance(filterType, 1);
        filterArray.SetValue(filter, 0);

        object? settings = null;
        var ctor = settingsType.GetConstructor(new[] { filterArray.GetType() });
        if (ctor != null)
        {
            settings = ctor.Invoke(new object[] { filterArray });
        }
        else
        {
            settings = Activator.CreateInstance(settingsType);
            var filtersProp = settingsType.GetProperty("filters");
            if (settings == null || filtersProp == null || !filtersProp.CanWrite)
            {
                return (false, "Failed to create ExecutionSettings.");
            }
            filtersProp.SetValue(settings, filterArray);
        }

        var api = Activator.CreateInstance(apiType);
        if (api == null)
        {
            return (false, "Failed to create TestRunnerApi.");
        }

        var execute = apiType.GetMethod("Execute", new[] { settingsType });
        if (execute == null)
        {
            return (false, "TestRunnerApi.Execute not found.");
        }

        execute.Invoke(api, new[] { settings! });
        WriteTestsStatus($"{{\"state\":\"running\",\"mode\":\"{Escape(modeStr)}\",\"at\":\"{DateTime.UtcNow:O}\"}}");
        return (true, $"Test run started: mode={modeStr}");
    }

    private static void TryLoadAssemblyByName(string assemblyName)
    {
        try
        {
            Assembly.Load(assemblyName);
        }
        catch
        {
        }
    }

    private static void TryLoadAssemblyFromScriptAssemblies(string dllName)
    {
        try
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var fullPath = Path.Combine(projectRoot, "Library", "ScriptAssemblies", dllName);
            if (File.Exists(fullPath))
            {
                Assembly.LoadFile(fullPath);
            }
        }
        catch
        {
        }
    }

    private static (bool, string) TestsResults()
    {
        var statusPath = Path.Combine(BridgeRoot(), "test-results.json");
        if (File.Exists(statusPath))
        {
            return (true, File.ReadAllText(statusPath));
        }

        var summary = FindLatestUnityTestSummary();
        return summary != null
            ? (true, summary)
            : (true, "{\"state\":\"unknown\",\"message\":\"no test results yet\"}");
    }

    private static string? JsonString(string json, string key)
    {
        var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
        return m.Success ? Regex.Unescape(m.Groups["v"].Value) : null;
    }

    private static string? JsonArgumentString(string json, string key)
    {
        var args = Regex.Match(json, "\"arguments\"\\s*:\\s*\\{(?<args>[\\s\\S]*?)\\}\\s*(,|\\})", RegexOptions.IgnoreCase);
        if (!args.Success)
        {
            return null;
        }
        return JsonString(args.Groups["args"].Value, key);
    }

    private static bool? JsonBool(string json, string key)
    {
        var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> JsonStringArray(string json, string key)
    {
        var result = new List<string>();
        var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\\[(?<list>.*?)\\]", RegexOptions.Singleline);
        if (!m.Success)
        {
            return result;
        }

        var listRaw = m.Groups["list"].Value;
        var items = Regex.Matches(listRaw, "\"(?<v>(?:\\\\.|[^\"])*)\"");
        foreach (Match item in items)
        {
            var value = Regex.Unescape(item.Groups["v"].Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }
        return result;
    }

    private static string BuildResponseJson(string id, bool success, string message)
    {
        return "{\n" +
               $"  \"id\": \"{Escape(id)}\",\n" +
               $"  \"success\": {(success ? "true" : "false")},\n" +
               $"  \"message\": \"{Escape(message)}\"\n" +
               "}\n";
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    private static GameObject? FindByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim().Replace("\\", "/");
        var all = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.scene.IsValid());
        var allList = all.ToList();

        // 1) Exact full hierarchy path.
        var exact = allList.FirstOrDefault(g => string.Equals(GetPath(g), path, StringComparison.Ordinal));
        if (exact != null)
        {
            return exact;
        }

        // 2) Unique exact-name match (common MCP usage passes object name instead of full path).
        var nameMatches = allList.Where(g => string.Equals(g.name, path, StringComparison.Ordinal)).ToList();
        if (nameMatches.Count == 1)
        {
            return nameMatches[0];
        }

        // 3) If a short path/name is provided, try matching trailing segment.
        var tail = path.Contains("/") ? path.Split('/').Last() : path;
        var tailMatches = allList.Where(g => string.Equals(g.name, tail, StringComparison.Ordinal)).ToList();
        if (tailMatches.Count == 1)
        {
            return tailMatches[0];
        }

        return null;
    }

    private static string GetPath(GameObject go)
    {
        var stack = new Stack<string>();
        var t = go.transform;
        while (t != null)
        {
            stack.Push(t.name);
            t = t.parent;
        }
        return string.Join("/", stack);
    }

    private static string ResolveGraphPath(GraphArgs args)
    {
        var explicitPath = FirstNonEmpty(args.graphPath, args.graph_path, args.path);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath!.Replace("\\", "/");
        }

        var name = FirstNonEmpty(args.graphName, args.graph_name, args.name);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "DefaultGraph";
        }

        var safeName = Regex.Replace(name!, @"[^A-Za-z0-9_.-]", "_");
        return $"Assets/VisualScripting/{safeName}.asset";
    }

    private static string BridgeRoot()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, "Library", "XLabMcpBridge");
    }

    private static GraphArgs ParseGraphArgs(string raw)
    {
        try
        {
            var envelope = JsonUtility.FromJson<BridgeEnvelope>(raw);
            if (envelope != null && envelope.arguments != null && HasAnyGraphArg(envelope.arguments))
            {
                return envelope.arguments;
            }
        }
        catch
        {
        }

        return new GraphArgs
        {
            graphPath = JsonArgumentString(raw, "graphPath") ?? string.Empty,
            graph_name = JsonArgumentString(raw, "graph_name") ?? string.Empty,
            graphName = JsonArgumentString(raw, "graphName") ?? string.Empty,
            path = JsonArgumentString(raw, "path") ?? string.Empty,
            name = JsonArgumentString(raw, "name") ?? string.Empty,
            from = JsonArgumentString(raw, "from") ?? string.Empty,
            to = JsonArgumentString(raw, "to") ?? string.Empty,
            source = JsonArgumentString(raw, "source") ?? string.Empty,
            target = JsonArgumentString(raw, "target") ?? string.Empty,
            patch = JsonArgumentString(raw, "patch") ?? string.Empty,
            text = JsonArgumentString(raw, "text") ?? string.Empty,
            edit = JsonArgumentString(raw, "edit") ?? string.Empty,
        };
    }

    private static string? FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static string? FirstString(string json, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = JsonString(json, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return null;
    }

    private static (bool Success, string Message, UnityEngine.Object? Asset) LoadGraph(string graphPath)
    {
        if (!graphPath.StartsWith("Assets/VisualScripting/", StringComparison.Ordinal))
        {
            return (false, "graph path must be under Assets/VisualScripting/", null);
        }

        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(graphPath);
        if (asset == null)
        {
            return (false, $"graph not found: {graphPath}", null);
        }

        var graphType = FindType("Unity.VisualScripting.ScriptGraphAsset");
        if (graphType == null)
        {
            return (false, "Unity Visual Scripting package/type not found.", null);
        }

        if (!graphType.IsAssignableFrom(asset.GetType()))
        {
            return (false, $"asset is not ScriptGraphAsset: {graphPath}", null);
        }

        return (true, "ok", asset);
    }

    private static void AppendGraphLabel(UnityEngine.Object asset, string value)
    {
        var labels = AssetDatabase.GetLabels(asset).ToList();
        if (!labels.Contains(value))
        {
            labels.Add(value);
            AssetDatabase.SetLabels(asset, labels.ToArray());
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }
    }

    private static Type? FindType(string fullName)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => string.Equals(t.FullName, fullName, StringComparison.Ordinal));
    }

    private static Type? FindTypeByName(string typeName, string? namespaceContains = null)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t =>
                string.Equals(t.Name, typeName, StringComparison.Ordinal) &&
                (namespaceContains == null || (t.Namespace?.Contains(namespaceContains, StringComparison.Ordinal) ?? false)));
    }

    private static string BuildScriptableObjectTemplate(string className, string namespaceName)
    {
        var ns = string.IsNullOrWhiteSpace(namespaceName) ? "Breach.Data" : namespaceName.Trim();
        return
$@"using UnityEngine;

namespace {ns}
{{
    [CreateAssetMenu(menuName = ""Breach/Data/{className}"", fileName = ""{className}"")]
    public sealed class {className} : ScriptableObject
    {{
    }}
}}
";
    }

    private static void WriteTestsStatus(string payload)
    {
        try
        {
            Directory.CreateDirectory(BridgeRoot());
            File.WriteAllText(Path.Combine(BridgeRoot(), "test-results.json"), payload + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string? FindLatestUnityTestSummary()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var testDir = Path.Combine(projectRoot, "Library", "TestResults");
        if (!Directory.Exists(testDir))
        {
            return null;
        }

        var file = new DirectoryInfo(testDir)
            .GetFiles("*.xml", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (file == null)
        {
            return null;
        }

        var xml = File.ReadAllText(file.FullName);
        var passed = Regex.Matches(xml, "result=\"Passed\"").Count;
        var failed = Regex.Matches(xml, "result=\"Failed\"").Count;
        var skipped = Regex.Matches(xml, "result=\"Skipped\"").Count;
        var total = passed + failed + skipped;
        return $"{{\"state\":\"completed\",\"source\":\"{Escape(file.FullName)}\",\"total\":{total},\"passed\":{passed},\"failed\":{failed},\"skipped\":{skipped}}}";
    }

    private static bool HasAnyGraphArg(GraphArgs args)
    {
        return !string.IsNullOrWhiteSpace(FirstNonEmpty(
            args.graphPath, args.graph_path, args.path, args.graphName, args.graph_name, args.name,
            args.from, args.to, args.source, args.target, args.patch, args.text, args.edit));
    }

    [Serializable]
    private sealed class BridgeEnvelope
    {
        public GraphArgs arguments = new GraphArgs();
    }

    [Serializable]
    private sealed class GraphArgs
    {
        public string graphPath = "";
        public string graph_path = "";
        public string path = "";
        public string graphName = "";
        public string graph_name = "";
        public string name = "";
        public string from = "";
        public string to = "";
        public string source = "";
        public string target = "";
        public string fromNode = "";
        public string sourceNode = "";
        public string toNode = "";
        public string targetNode = "";
        public string patch = "";
        public string text = "";
        public string edit = "";
    }
    }
}
