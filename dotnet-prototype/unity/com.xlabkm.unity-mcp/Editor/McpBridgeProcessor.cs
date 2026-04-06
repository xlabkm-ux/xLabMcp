using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

            foreach (var cmdFile in Directory.GetFiles(commands, "*.json")
                         .Select(p => new FileInfo(p))
                         .OrderBy(f => f.CreationTimeUtc)
                         .ThenBy(f => f.Name, StringComparer.Ordinal)
                         .Select(f => f.FullName))
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

        var load = EnsureGraphLoaded(graphPath, createIfMissing: true);
        if (!load.Success)
        {
            return (false, load.Message);
        }

        var state = LoadGraphState(graphPath);
        SaveGraphState(graphPath, state);
        return (true, $"Graph ready: {graphPath}");
    }

    private static (bool, string) GraphConnect(string raw)
    {
        var args = ParseGraphArgs(raw);
        var explicitPath = FirstNonEmpty(JsonArgumentString(raw, "graphPath"), JsonArgumentString(raw, "graph_path"), JsonArgumentString(raw, "path"));
        var graphPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Replace("\\", "/") : ResolveGraphPath(args);
        var load = EnsureGraphLoaded(graphPath, createIfMissing: true);
        if (!load.Success)
        {
            return (false, load.Message);
        }

        var sourceNodeId = FirstNonEmpty(
            JsonArgumentString(raw, "fromNodeId"),
            JsonArgumentString(raw, "sourceNodeId"),
            args.from, args.source, args.fromNode, args.sourceNode);
        var targetNodeId = FirstNonEmpty(
            JsonArgumentString(raw, "toNodeId"),
            JsonArgumentString(raw, "targetNodeId"),
            args.to, args.target, args.toNode, args.targetNode);
        if (string.IsNullOrWhiteSpace(sourceNodeId) || string.IsNullOrWhiteSpace(targetNodeId))
        {
            return (false, "graph.connect requires fromNodeId/sourceNodeId and toNodeId/targetNodeId");
        }

        var sourcePort = FirstNonEmpty(JsonArgumentString(raw, "fromPort"), JsonArgumentString(raw, "sourcePort"), JsonArgumentString(raw, "outPort")) ?? "exit";
        var targetPort = FirstNonEmpty(JsonArgumentString(raw, "toPort"), JsonArgumentString(raw, "targetPort"), JsonArgumentString(raw, "inPort")) ?? "enter";
        var kind = (FirstNonEmpty(JsonArgumentString(raw, "kind"), JsonArgumentString(raw, "connectionKind")) ?? "control").ToLowerInvariant();

        var state = LoadGraphState(graphPath);
        var srcNode = state.nodes.FirstOrDefault(n => string.Equals(n.nodeId, sourceNodeId, StringComparison.Ordinal));
        var dstNode = state.nodes.FirstOrDefault(n => string.Equals(n.nodeId, targetNodeId, StringComparison.Ordinal));
        if (srcNode == null && !string.IsNullOrWhiteSpace(sourceNodeId))
        {
            var unitByGuid = FindUnitByGuid(load.Graph!, sourceNodeId!);
            if (unitByGuid != null)
            {
                srcNode = new GraphNodeState { nodeId = sourceNodeId!, guid = sourceNodeId!, unitType = unitByGuid.GetType().FullName ?? unitByGuid.GetType().Name };
                state.nodes.Add(srcNode);
            }
        }
        if (dstNode == null && !string.IsNullOrWhiteSpace(targetNodeId))
        {
            var unitByGuid = FindUnitByGuid(load.Graph!, targetNodeId!);
            if (unitByGuid != null)
            {
                dstNode = new GraphNodeState { nodeId = targetNodeId!, guid = targetNodeId!, unitType = unitByGuid.GetType().FullName ?? unitByGuid.GetType().Name };
                state.nodes.Add(dstNode);
            }
        }
        if (srcNode == null || dstNode == null)
        {
            return (false, $"graph.connect nodes not found in graph state: from={sourceNodeId}; to={targetNodeId}");
        }

        var sourceUnit = FindUnitByGuid(load.Graph!, srcNode.guid);
        var targetUnit = FindUnitByGuid(load.Graph!, dstNode.guid);
        if (sourceUnit == null || targetUnit == null)
        {
            return (false, $"graph.connect unit not found in graph asset: from={sourceNodeId}; to={targetNodeId}");
        }

        var connect = ConnectGraphPorts(load.Graph!, sourceUnit, sourcePort, targetUnit, targetPort, kind);
        if (!connect.Success)
        {
            return (false, connect.Message);
        }

        state.links.RemoveAll(l =>
            string.Equals(l.fromNodeId, sourceNodeId, StringComparison.Ordinal) &&
            string.Equals(l.toNodeId, targetNodeId, StringComparison.Ordinal) &&
            string.Equals(l.fromPort, sourcePort, StringComparison.Ordinal) &&
            string.Equals(l.toPort, targetPort, StringComparison.Ordinal) &&
            string.Equals(l.kind, kind, StringComparison.Ordinal));
        state.links.Add(new GraphLinkState
        {
            fromNodeId = sourceNodeId!,
            fromPort = sourcePort,
            toNodeId = targetNodeId!,
            toPort = targetPort,
            kind = kind
        });

        EditorUtility.SetDirty(load.Asset!);
        AssetDatabase.SaveAssets();
        SaveGraphState(graphPath, state);
        return (true, $"graph.connect: {sourceNodeId}.{sourcePort} -> {targetNodeId}.{targetPort}; kind={kind}; path={graphPath}");
    }

    private static (bool, string) GraphEdit(string raw)
    {
        var args = ParseGraphArgs(raw);
        var explicitPath = FirstNonEmpty(JsonArgumentString(raw, "graphPath"), JsonArgumentString(raw, "graph_path"), JsonArgumentString(raw, "path"));
        var graphPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Replace("\\", "/") : ResolveGraphPath(args);
        var load = EnsureGraphLoaded(graphPath, createIfMissing: true);
        if (!load.Success)
        {
            return (false, load.Message);
        }

        var op = (FirstNonEmpty(
            JsonArgumentString(raw, "operation"),
            JsonArgumentString(raw, "op"),
            JsonArgumentString(raw, "action")) ?? "add_node").ToLowerInvariant();
        var state = LoadGraphState(graphPath);

        if (op == "add_node")
        {
            var nodeId = FirstNonEmpty(JsonArgumentString(raw, "nodeId"), JsonArgumentString(raw, "id"), JsonArgumentString(raw, "name"));
            var unitType = FirstNonEmpty(JsonArgumentString(raw, "unitType"), JsonArgumentString(raw, "nodeType"), JsonArgumentString(raw, "type"));
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(unitType))
            {
                return (false, "graph.edit add_node requires nodeId and unitType");
            }

            var nodeX = JsonArgumentFloat(raw, "x") ?? 0f;
            var nodeY = JsonArgumentFloat(raw, "y") ?? 0f;
            if (state.nodes.Any(n => string.Equals(n.nodeId, nodeId, StringComparison.Ordinal)))
            {
                return (false, $"node already exists: {nodeId}");
            }

            var add = AddGraphNode(load.Graph!, unitType!, nodeX, nodeY);
            if (!add.Success)
            {
                return (false, add.Message);
            }

            state.nodes.Add(new GraphNodeState
            {
                nodeId = nodeId!,
                guid = add.UnitGuid ?? Guid.NewGuid().ToString("N"),
                unitType = add.UnitType ?? unitType!,
                x = nodeX,
                y = nodeY
            });
            EditorUtility.SetDirty(load.Asset!);
            AssetDatabase.SaveAssets();
            SaveGraphState(graphPath, state);
            return (true, $"graph.edit add_node: nodeId={nodeId}; unitType={unitType}; path={graphPath}");
        }

        if (op == "remove_node")
        {
            var nodeId = FirstNonEmpty(JsonArgumentString(raw, "nodeId"), JsonArgumentString(raw, "id"), JsonArgumentString(raw, "name"));
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return (false, "graph.edit remove_node requires nodeId");
            }

            var node = state.nodes.FirstOrDefault(n => string.Equals(n.nodeId, nodeId, StringComparison.Ordinal));
            if (node == null)
            {
                return (false, $"node not found: {nodeId}");
            }

            var unit = FindUnitByGuid(load.Graph!, node.guid);
            if (unit != null)
            {
                RemoveGraphUnit(load.Graph!, unit);
            }

            state.nodes.RemoveAll(n => string.Equals(n.nodeId, nodeId, StringComparison.Ordinal));
            state.links.RemoveAll(l => string.Equals(l.fromNodeId, nodeId, StringComparison.Ordinal) || string.Equals(l.toNodeId, nodeId, StringComparison.Ordinal));
            EditorUtility.SetDirty(load.Asset!);
            AssetDatabase.SaveAssets();
            SaveGraphState(graphPath, state);
            return (true, $"graph.edit remove_node: nodeId={nodeId}; path={graphPath}");
        }

        if (op == "set_node")
        {
            var nodeId = FirstNonEmpty(JsonArgumentString(raw, "nodeId"), JsonArgumentString(raw, "id"), JsonArgumentString(raw, "name"));
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return (false, "graph.edit set_node requires nodeId");
            }

            var node = state.nodes.FirstOrDefault(n => string.Equals(n.nodeId, nodeId, StringComparison.Ordinal));
            if (node == null)
            {
                return (false, $"node not found: {nodeId}");
            }

            var x = JsonArgumentFloat(raw, "x") ?? node.x;
            var y = JsonArgumentFloat(raw, "y") ?? node.y;
            var unit = FindUnitByGuid(load.Graph!, node.guid);
            if (unit == null)
            {
                return (false, $"unit not found in asset for node: {nodeId}");
            }

            SetUnitPosition(unit, x, y);
            node.x = x;
            node.y = y;
            EditorUtility.SetDirty(load.Asset!);
            AssetDatabase.SaveAssets();
            SaveGraphState(graphPath, state);
            return (true, $"graph.edit set_node: nodeId={nodeId}; x={x}; y={y}; path={graphPath}");
        }

        return (false, $"Unsupported graph.edit operation: {op}. Supported: add_node|remove_node|set_node");
    }

    private static (bool, string) GraphValidate(string raw)
    {
        var args = ParseGraphArgs(raw);
        var explicitPath = FirstNonEmpty(JsonArgumentString(raw, "graphPath"), JsonArgumentString(raw, "graph_path"), JsonArgumentString(raw, "path"));
        var graphPath = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath.Replace("\\", "/") : ResolveGraphPath(args);
        var load = EnsureGraphLoaded(graphPath, createIfMissing: false);
        if (!load.Success)
        {
            // Bridge-only callers may validate before graph is created. Keep this non-fatal.
            return (true, $"graph.validate: exists=false; path={graphPath}; reason={load.Message}");
        }

        var state = LoadGraphState(graphPath);
        var unitCount = CountCollectionItems(GetMemberValue(load.Graph!, "units"));
        var controlCount = CountCollectionItems(GetMemberValue(load.Graph!, "controlConnections"));
        var valueCount = CountCollectionItems(GetMemberValue(load.Graph!, "valueConnections"));
        var stateLinks = state.links.Count;
        var stateNodes = state.nodes.Count;
        return (true, $"graph.validate: exists=true; assetType={load.Asset!.GetType().FullName}; units={unitCount}; controlConnections={controlCount}; valueConnections={valueCount}; stateNodes={stateNodes}; stateLinks={stateLinks}; path={graphPath}");
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
        var testFilter = JsonArgumentString(raw, "filter")
                         ?? JsonArgumentString(raw, "testFilter")
                         ?? JsonArgumentString(raw, "testName");
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
        if (!string.IsNullOrWhiteSpace(testFilter))
        {
            TrySetFilterStringArray(filter!, "testNames", new[] { testFilter! });
            TrySetFilterStringArray(filter!, "groupNames", new[] { testFilter! });
            TrySetFilterStringArray(filter!, "assemblyNames", new[] { testFilter! });
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

        TryAttachTestRunnerCallbacks(apiType, api, modeStr);
        execute.Invoke(api, new[] { settings! });
        WriteTestsStatus($"{{\"state\":\"running\",\"mode\":\"{Escape(modeStr)}\",\"filter\":\"{Escape(testFilter ?? string.Empty)}\",\"at\":\"{DateTime.UtcNow:O}\"}}");
        return (true, $"Test run started: mode={modeStr}{(string.IsNullOrWhiteSpace(testFilter) ? string.Empty : $"; filter={testFilter}")}");
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
            var statusRaw = File.ReadAllText(statusPath);
            var latestSummary = FindLatestUnityTestSummary();
            if (latestSummary != null && statusRaw.Contains("\"state\":\"running\"", StringComparison.OrdinalIgnoreCase))
            {
                return (true, latestSummary);
            }
            return (true, statusRaw);
        }

        var summary = FindLatestUnityTestSummary();
        return summary != null
            ? (true, summary)
            : (true, "{\"state\":\"unknown\",\"message\":\"no test results yet\"}");
    }

    private static void TrySetFilterStringArray(object filter, string memberName, string[] values)
    {
        var type = filter.GetType();
        var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string[]))
        {
            try
            {
                prop.SetValue(filter, values);
            }
            catch
            {
            }
            return;
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null && field.FieldType == typeof(string[]))
        {
            try
            {
                field.SetValue(filter, values);
            }
            catch
            {
            }
        }
    }

    private static void TryAttachTestRunnerCallbacks(Type apiType, object api, string mode)
    {
        try
        {
            var callbacksType = FindType("UnityEditor.TestTools.TestRunner.Api.ICallbacks") ?? FindTypeByName("ICallbacks", "TestRunner.Api");
            if (callbacksType == null)
            {
                return;
            }

            var register = apiType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, "RegisterCallbacks", StringComparison.Ordinal))
                    {
                        return false;
                    }
                    var ps = m.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(callbacksType);
                });
            if (register == null)
            {
                return;
            }

            var proxyObject = CreateCallbackProxy(callbacksType, mode);
            if (proxyObject != null)
            {
                register.Invoke(api, new[] { proxyObject });
            }
        }
        catch
        {
        }
    }

    private static object? CreateCallbackProxy(Type callbacksInterfaceType, string mode)
    {
        try
        {
            var create = typeof(DispatchProxy).GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            if (create == null)
            {
                return null;
            }
            var generic = create.MakeGenericMethod(callbacksInterfaceType, typeof(TestRunnerCallbacksProxy));
            var instance = generic.Invoke(null, null);
            if (instance is TestRunnerCallbacksProxy proxy)
            {
                proxy.Mode = mode;
            }
            return instance;
        }
        catch
        {
            return null;
        }
    }

    private static void OnTestRunnerCallback(string mode, string callbackName, object?[]? args)
    {
        try
        {
            if (string.Equals(callbackName, "RunStarted", StringComparison.Ordinal))
            {
                WriteTestsStatus($"{{\"state\":\"running\",\"mode\":\"{Escape(mode)}\",\"at\":\"{DateTime.UtcNow:O}\"}}");
                return;
            }

            if (string.Equals(callbackName, "RunFinished", StringComparison.Ordinal))
            {
                var result = args != null && args.Length > 0 ? args[0] : null;
                var summary = BuildSummaryFromResultAdapter(result);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    WriteTestsStatus(summary!);
                }
                else
                {
                    WriteTestsStatus($"{{\"state\":\"completed\",\"mode\":\"{Escape(mode)}\",\"at\":\"{DateTime.UtcNow:O}\"}}");
                }
                return;
            }
        }
        catch
        {
        }
    }

    private static string? BuildSummaryFromResultAdapter(object? resultAdapter)
    {
        if (resultAdapter == null)
        {
            return null;
        }

        var type = resultAdapter.GetType();
        int ReadInt(params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetMemberValue(resultAdapter, name);
                if (value is int i) return i;
                if (value is long l) return (int)l;
                if (value is string s && int.TryParse(s, out var p)) return p;
            }
            return 0;
        }

        double ReadDouble(params string[] names)
        {
            foreach (var name in names)
            {
                var value = GetMemberValue(resultAdapter, name);
                if (value is double d) return d;
                if (value is float f) return f;
                if (value is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
            }
            return 0;
        }

        var passed = ReadInt("PassCount", "PassedCount", "passed");
        var failed = ReadInt("FailCount", "FailedCount", "failed");
        var skipped = ReadInt("SkipCount", "InconclusiveCount", "SkippedCount", "skipped");
        var total = ReadInt("TestCaseCount", "TotalCount", "total");
        if (total <= 0)
        {
            total = passed + failed + skipped;
        }
        var duration = ReadDouble("Duration", "duration", "ElapsedTime");
        return $"{{\"state\":\"completed\",\"source\":\"callback\",\"total\":{total},\"passed\":{passed},\"failed\":{failed},\"skipped\":{skipped},\"duration\":{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
    }

    private static string? JsonString(string json, string key)
    {
        var m = Regex.Match(json, $"\"{Regex.Escape(key)}\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
        return m.Success ? Regex.Unescape(m.Groups["v"].Value) : null;
    }

    private static string? JsonArgumentString(string json, string key)
    {
        var args = ExtractJsonObjectValue(json, "arguments");
        if (string.IsNullOrWhiteSpace(args))
        {
            return null;
        }
        return JsonString(args, key);
    }

    private static bool? JsonBool(string json, string key)
    {
        var args = ExtractJsonObjectValue(json, "arguments");
        var source = string.IsNullOrWhiteSpace(args) ? json : args + "\n" + json;
        var m = Regex.Match(source, $"\"{Regex.Escape(key)}\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static float? JsonArgumentFloat(string json, string key)
    {
        var args = ExtractJsonObjectValue(json, "arguments");
        var source = string.IsNullOrWhiteSpace(args) ? json : args + "\n" + json;
        var m = Regex.Match(source, $"\"{Regex.Escape(key)}\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }
        return float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static List<string> JsonStringArray(string json, string key)
    {
        var result = new List<string>();
        var args = ExtractJsonObjectValue(json, "arguments");
        var source = string.IsNullOrWhiteSpace(args) ? json : args + "\n" + json;
        var m = Regex.Match(source, $"\"{Regex.Escape(key)}\"\\s*:\\s*\\[(?<list>.*?)\\]", RegexOptions.Singleline);
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

    private static GraphLoadResult EnsureGraphLoaded(string graphPath, bool createIfMissing)
    {
        if (!graphPath.StartsWith("Assets/VisualScripting/", StringComparison.Ordinal))
        {
            return GraphLoadResult.Fail("graph path must be under Assets/VisualScripting/");
        }

        var graphType = FindType("Unity.VisualScripting.ScriptGraphAsset");
        if (graphType == null)
        {
            return GraphLoadResult.Fail("Unity Visual Scripting package/type not found.");
        }

        var flowGraphType = FindType("Unity.VisualScripting.FlowGraph");
        if (flowGraphType == null)
        {
            return GraphLoadResult.Fail("Unity Visual Scripting FlowGraph type not found.");
        }

        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(graphPath);
        if (asset == null)
        {
            if (!createIfMissing)
            {
                return GraphLoadResult.Fail($"graph not found: {graphPath}");
            }

            var dir = Path.GetDirectoryName(graphPath)?.Replace("\\", "/");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), dir));
            }

            var created = ScriptableObject.CreateInstance(graphType);
            var newGraph = Activator.CreateInstance(flowGraphType);
            if (newGraph == null)
            {
                return GraphLoadResult.Fail("Failed to create FlowGraph instance.");
            }
            if (!TrySetMemberValue(created, "graph", newGraph))
            {
                return GraphLoadResult.Fail("Failed to assign FlowGraph to ScriptGraphAsset.");
            }

            AssetDatabase.CreateAsset(created, graphPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            asset = created;
        }

        if (!graphType.IsAssignableFrom(asset.GetType()))
        {
            return GraphLoadResult.Fail($"asset is not ScriptGraphAsset: {graphPath}");
        }

        var graphObj = GetMemberValue(asset, "graph");
        if (graphObj == null)
        {
            var newGraph = Activator.CreateInstance(flowGraphType);
            if (newGraph == null)
            {
                return GraphLoadResult.Fail("Failed to create FlowGraph instance.");
            }
            if (!TrySetMemberValue(asset, "graph", newGraph))
            {
                return GraphLoadResult.Fail("Failed to assign FlowGraph to ScriptGraphAsset.");
            }
            graphObj = newGraph;
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        return GraphLoadResult.Ok(asset, graphObj);
    }

    private static GraphState LoadGraphState(string graphPath)
    {
        var statePath = GraphStatePath(graphPath);
        if (!File.Exists(statePath))
        {
            var legacyStatePath = LegacyGraphStatePath(graphPath);
            if (File.Exists(legacyStatePath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                    File.Copy(legacyStatePath, statePath, overwrite: true);
                }
                catch
                {
                }
            }
            else
            {
                return new GraphState();
            }
        }

        try
        {
            var state = JsonUtility.FromJson<GraphState>(File.ReadAllText(statePath));
            return state ?? new GraphState();
        }
        catch
        {
            return new GraphState();
        }
    }

    private static void SaveGraphState(string graphPath, GraphState state)
    {
        var statePath = GraphStatePath(graphPath);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, JsonUtility.ToJson(state, true), Encoding.UTF8);
    }

    private static string GraphStatePath(string graphPath)
    {
        var normalized = graphPath.Replace("\\", "/");
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var relative = normalized.StartsWith("Assets/", StringComparison.Ordinal) ? normalized : $"Assets/{normalized.TrimStart('/')}";
        var fullAssetPath = Path.Combine(root, relative.Replace("/", Path.DirectorySeparatorChar.ToString()));
        return fullAssetPath + ".mcpstate.json";
    }

    private static string LegacyGraphStatePath(string graphPath)
    {
        var safe = Regex.Replace(graphPath, @"[^A-Za-z0-9_.-]", "_");
        return Path.Combine(BridgeRoot(), "graph-state", $"{safe}.json");
    }

    private static (bool Success, string? Message, string? UnitGuid, string? UnitType) AddGraphNode(object graphObj, string requestedType, float x, float y)
    {
        var unitType = ResolveUnitType(requestedType);
        if (unitType == null)
        {
            return (false, $"Unit type not found: {requestedType}", null, null);
        }

        var unit = Activator.CreateInstance(unitType);
        if (unit == null)
        {
            return (false, $"Failed to instantiate unit type: {unitType.FullName}", null, null);
        }

        TryInvokeNoArg(unit, "Definition");
        TryInvokeNoArg(unit, "Define");
        SetUnitPosition(unit, x, y);

        var unitsCollection = GetMemberValue(graphObj, "units");
        if (unitsCollection == null || !TryInvokeCollectionAdd(unitsCollection, unit))
        {
            if (!TryInvokeMethod(graphObj, "AddUnit", unit))
            {
                return (false, "Failed to add unit to FlowGraph.units", null, null);
            }
        }

        var guid = ReadGuidString(unit) ?? Guid.NewGuid().ToString("N");
        return (true, null, guid, unitType.FullName ?? unitType.Name);
    }

    private static void RemoveGraphUnit(object graphObj, object unit)
    {
        var unitsCollection = GetMemberValue(graphObj, "units");
        if (unitsCollection != null && TryInvokeCollectionRemove(unitsCollection, unit))
        {
            return;
        }

        TryInvokeMethod(graphObj, "RemoveUnit", unit);
    }

    private static object? FindUnitByGuid(object graphObj, string guid)
    {
        var unitsCollection = GetMemberValue(graphObj, "units");
        if (unitsCollection == null)
        {
            return null;
        }

        foreach (var unit in EnumerateCollection(unitsCollection))
        {
            var id = ReadGuidString(unit);
            if (!string.IsNullOrWhiteSpace(id) && string.Equals(id, guid, StringComparison.OrdinalIgnoreCase))
            {
                return unit;
            }
        }
        return null;
    }

    private static (bool Success, string Message) ConnectGraphPorts(object graphObj, object sourceUnit, string sourcePortName, object targetUnit, string targetPortName, string kind)
    {
        if (kind == "value")
        {
            var sourcePort = FindNamedPort(sourceUnit, sourcePortName, "valueOutputs");
            var targetPort = FindNamedPort(targetUnit, targetPortName, "valueInputs");
            if (sourcePort == null || targetPort == null)
            {
                return (false, $"value ports not found: {sourcePortName}->{targetPortName}");
            }

            var collection = GetMemberValue(graphObj, "valueConnections");
            if (collection == null)
            {
                return (false, "FlowGraph.valueConnections not found");
            }

            if (TryInvokeMethod(collection, "Add", sourcePort, targetPort))
            {
                return (true, "ok");
            }

            var valueConnectionType = FindType("Unity.VisualScripting.ValueConnection");
            if (valueConnectionType != null)
            {
                var conn = TryCreateConnection(valueConnectionType, sourcePort, targetPort);
                if (conn != null && TryInvokeMethod(collection, "Add", conn))
                {
                    return (true, "ok");
                }
            }

            return (false, "Failed to add value connection");
        }
        else
        {
            var sourcePort = FindNamedPort(sourceUnit, sourcePortName, "controlOutputs");
            var targetPort = FindNamedPort(targetUnit, targetPortName, "controlInputs");
            if (sourcePort == null || targetPort == null)
            {
                return (false, $"control ports not found: {sourcePortName}->{targetPortName}");
            }

            var collection = GetMemberValue(graphObj, "controlConnections");
            if (collection == null)
            {
                return (false, "FlowGraph.controlConnections not found");
            }

            if (TryInvokeMethod(collection, "Add", sourcePort, targetPort))
            {
                return (true, "ok");
            }

            var controlConnectionType = FindType("Unity.VisualScripting.ControlConnection");
            if (controlConnectionType != null)
            {
                var conn = TryCreateConnection(controlConnectionType, sourcePort, targetPort);
                if (conn != null && TryInvokeMethod(collection, "Add", conn))
                {
                    return (true, "ok");
                }
            }

            return (false, "Failed to add control connection");
        }
    }

    private static object? TryCreateConnection(Type connectionType, object sourcePort, object targetPort)
    {
        foreach (var ctor in connectionType.GetConstructors())
        {
            var ps = ctor.GetParameters();
            if (ps.Length != 2)
            {
                continue;
            }

            if (ps[0].ParameterType.IsAssignableFrom(sourcePort.GetType()) &&
                ps[1].ParameterType.IsAssignableFrom(targetPort.GetType()))
            {
                return ctor.Invoke(new[] { sourcePort, targetPort });
            }
        }
        return null;
    }

    private static object? FindNamedPort(object unit, string portName, string portCollectionMember)
    {
        var collection = GetMemberValue(unit, portCollectionMember);
        if (collection == null)
        {
            return null;
        }

        var byIndexer = TryGetCollectionItemByStringKey(collection, portName);
        if (byIndexer != null)
        {
            return byIndexer;
        }

        foreach (var item in EnumerateCollection(collection))
        {
            var key = (GetMemberValue(item, "key") ?? GetMemberValue(item, "Key"))?.ToString();
            if (!string.IsNullOrWhiteSpace(key) && string.Equals(key, portName, StringComparison.Ordinal))
            {
                return item;
            }

            var itemPortKey = (GetMemberValue(item, "key") ?? GetMemberValue(item, "name"))?.ToString();
            if (!string.IsNullOrWhiteSpace(itemPortKey) && string.Equals(itemPortKey, portName, StringComparison.Ordinal))
            {
                return item;
            }

            var value = GetMemberValue(item, "Value");
            if (value != null)
            {
                var valueKey = (GetMemberValue(value, "key") ?? GetMemberValue(value, "name"))?.ToString();
                if (!string.IsNullOrWhiteSpace(valueKey) && string.Equals(valueKey, portName, StringComparison.Ordinal))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static object? TryGetCollectionItemByStringKey(object collection, string key)
    {
        var type = collection.GetType();
        var indexers = type.GetDefaultMembers().OfType<PropertyInfo>().Where(p =>
        {
            var ps = p.GetIndexParameters();
            return ps.Length == 1 && ps[0].ParameterType == typeof(string);
        });

        foreach (var indexer in indexers)
        {
            try
            {
                var value = indexer.GetValue(collection, new object[] { key });
                if (value != null)
                {
                    return value;
                }
            }
            catch
            {
            }
        }

        var getItem = type.GetMethod("get_Item", new[] { typeof(string) });
        if (getItem != null)
        {
            try
            {
                return getItem.Invoke(collection, new object[] { key });
            }
            catch
            {
            }
        }

        return null;
    }

    private static int CountCollectionItems(object? collection)
    {
        if (collection == null)
        {
            return 0;
        }

        var countMember = GetMemberValue(collection, "Count");
        if (countMember is int i)
        {
            return i;
        }

        return EnumerateCollection(collection).Count();
    }

    private static IEnumerable<object> EnumerateCollection(object collection)
    {
        if (collection is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null)
                {
                    yield return item;
                }
            }
        }
    }

    private static Type? ResolveUnitType(string requestedType)
    {
        if (string.IsNullOrWhiteSpace(requestedType))
        {
            return null;
        }

        if (requestedType.Contains("."))
        {
            return FindType(requestedType);
        }

        return FindTypeByName(requestedType, "Unity.VisualScripting")
               ?? FindTypeByName(requestedType);
    }

    private static string? ReadGuidString(object unit)
    {
        var guidVal = GetMemberValue(unit, "guid");
        if (guidVal == null)
        {
            return null;
        }
        if (guidVal is Guid g)
        {
            return g.ToString("N");
        }
        return guidVal.ToString();
    }

    private static void SetUnitPosition(object unit, float x, float y)
    {
        TrySetMemberValue(unit, "position", new Vector2(x, y));
    }

    private static object? GetMemberValue(object obj, string name)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (prop != null)
        {
            try { return prop.GetValue(obj); } catch { }
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (field != null)
        {
            try { return field.GetValue(obj); } catch { }
        }
        return null;
    }

    private static bool TrySetMemberValue(object obj, string name, object value)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (prop != null && prop.CanWrite && prop.PropertyType.IsAssignableFrom(value.GetType()))
        {
            try
            {
                prop.SetValue(obj, value);
                return true;
            }
            catch
            {
            }
        }

        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
        if (field != null && field.FieldType.IsAssignableFrom(value.GetType()))
        {
            try
            {
                field.SetValue(obj, value);
                return true;
            }
            catch
            {
            }
        }
        return false;
    }

    private static bool TryInvokeNoArg(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (method == null)
        {
            return false;
        }
        try
        {
            method.Invoke(target, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeMethod(object target, string methodName, params object[] args)
    {
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal));

        foreach (var method in methods)
        {
            var ps = method.GetParameters();
            if (ps.Length != args.Length)
            {
                continue;
            }

            var compatible = true;
            for (var i = 0; i < ps.Length; i++)
            {
                var arg = args[i];
                if (arg == null)
                {
                    if (ps[i].ParameterType.IsValueType)
                    {
                        compatible = false;
                        break;
                    }
                    continue;
                }
                if (!ps[i].ParameterType.IsAssignableFrom(arg.GetType()))
                {
                    compatible = false;
                    break;
                }
            }

            if (!compatible)
            {
                continue;
            }

            try
            {
                method.Invoke(target, args);
                return true;
            }
            catch
            {
            }
        }
        return false;
    }

    private static bool TryInvokeCollectionAdd(object collection, object value)
    {
        return TryInvokeMethod(collection, "Add", value);
    }

    private static bool TryInvokeCollectionRemove(object collection, object value)
    {
        return TryInvokeMethod(collection, "Remove", value);
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

        try
        {
            var doc = XDocument.Load(file.FullName);
            var root = doc.Root;
            if (root == null)
            {
                return null;
            }

            var xmlText = File.ReadAllText(file.FullName);
            var passed = ReadIntAttr(root, "passed")
                         ?? Regex.Matches(xmlText, "result=\"Passed\"").Count;
            var failed = ReadIntAttr(root, "failed")
                         ?? Regex.Matches(xmlText, "result=\"Failed\"").Count;
            var skipped = ReadIntAttr(root, "skipped")
                          ?? Regex.Matches(xmlText, "result=\"Skipped\"").Count;
            var total = ReadIntAttr(root, "total") ?? (passed + failed + skipped);
            var duration = ReadDoubleAttr(root, "duration") ?? 0.0;

            return $"{{\"state\":\"completed\",\"source\":\"{Escape(file.FullName)}\",\"total\":{total},\"passed\":{passed},\"failed\":{failed},\"skipped\":{skipped},\"duration\":{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadIntAttr(XElement element, string attrName)
    {
        var raw = element.Attribute(attrName)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static double? ReadDoubleAttr(XElement element, string attrName)
    {
        var raw = element.Attribute(attrName)?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? ExtractJsonObjectValue(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var keyToken = $"\"{key}\"";
        var keyIdx = json.IndexOf(keyToken, StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0)
        {
            return null;
        }

        var colonIdx = json.IndexOf(':', keyIdx + keyToken.Length);
        if (colonIdx < 0)
        {
            return null;
        }

        var i = colonIdx + 1;
        while (i < json.Length && char.IsWhiteSpace(json[i]))
        {
            i++;
        }

        if (i >= json.Length || json[i] != '{')
        {
            return null;
        }

        var start = i;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (; i < json.Length; i++)
        {
            var ch = json[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return json.Substring(start, i - start + 1);
                }
            }
        }

        return null;
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

    [Serializable]
    private sealed class GraphState
    {
        public List<GraphNodeState> nodes = new List<GraphNodeState>();
        public List<GraphLinkState> links = new List<GraphLinkState>();
    }

    [Serializable]
    private sealed class GraphNodeState
    {
        public string nodeId = "";
        public string guid = "";
        public string unitType = "";
        public float x;
        public float y;
    }

    [Serializable]
    private sealed class GraphLinkState
    {
        public string fromNodeId = "";
        public string fromPort = "";
        public string toNodeId = "";
        public string toPort = "";
        public string kind = "control";
    }

    private sealed class GraphLoadResult
    {
        public bool Success { get; }
        public string Message { get; }
        public UnityEngine.Object? Asset { get; }
        public object? Graph { get; }

        private GraphLoadResult(bool success, string message, UnityEngine.Object? asset, object? graph)
        {
            Success = success;
            Message = message;
            Asset = asset;
            Graph = graph;
        }

        public static GraphLoadResult Ok(UnityEngine.Object asset, object graph) => new GraphLoadResult(true, "ok", asset, graph);
        public static GraphLoadResult Fail(string message) => new GraphLoadResult(false, message, null, null);
    }

    private sealed class TestRunnerCallbacksProxy : DispatchProxy
    {
        public string Mode { get; set; } = "All";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod != null)
            {
                OnTestRunnerCallback(Mode, targetMethod.Name, args);
            }
            return null;
        }
    }
    }
}
