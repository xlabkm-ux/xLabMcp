using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Profiling;

#nullable enable

namespace XLab.UnityMcp.Editor
{
    [InitializeOnLoad]
    public static class McpBridgeProcessor
    {
    private static readonly Queue<string> LogBuffer = new();
    private static readonly List<PendingInputJob> PendingInputJobs = new();
    private static readonly object PendingInputJobsGate = new();
    private static double _lastPollAt;
    private static DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private static string? _lastCommandId;
    private static string? _lastCommandName;
    private static DateTime _lastCommandAtUtc = DateTime.MinValue;
    private static bool _lastCommandSucceeded;
    private static string? _lastCommandMessage;
    private static string? _lastScreenshotPath;
    private static DateTime _lastScreenshotAtUtc = DateTime.MinValue;
    private static string? _lastScreenshotScenario;
    private static string? _lastScreenshotStep;
    private static string? _lastScreenshotLabel;

    private sealed class PendingInputJob
    {
        public string JobId { get; init; } = string.Empty;
        public DateTime ReleaseAtUtc { get; init; }
        public List<KeyCode> Keys { get; init; } = new();
        public List<int> MouseButtons { get; init; } = new();
        public (int x, int y)? MousePosition { get; init; }
        public int FrameStart { get; init; }
        public int FrameEnd { get; init; }
    }

    private sealed class RiskFileEntry
    {
        public string Path { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }

    static McpBridgeProcessor()
    {
        Application.logMessageReceived += OnLog;
        EditorApplication.update += Poll;
        EditorApplication.update += ProcessPendingInputJobs;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
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
            _lastHeartbeatUtc = DateTime.UtcNow;

            foreach (var cmdFile in Directory.GetFiles(commands, "*.json")
                         .Select(p => new FileInfo(p))
                         .OrderBy(f => f.CreationTimeUtc)
                         .ThenBy(f => f.Name, StringComparer.Ordinal)
                         .Select(f => f.FullName))
            {
                ProcessCommand(cmdFile, responses, bridgeRoot);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"MCP bridge poll error: {ex.Message}");
        }
    }

    private static void ProcessCommand(string cmdFile, string responsesDir, string bridgeRoot)
    {
        var raw = File.ReadAllText(cmdFile);
        var id = JsonString(raw, "id") ?? Guid.NewGuid().ToString("N");
        var command = JsonString(raw, "command") ?? "unknown";

        var result = Execute(command, raw);
        _lastCommandId = id;
        _lastCommandName = command;
        _lastCommandAtUtc = DateTime.UtcNow;
        _lastCommandSucceeded = result.Success;
        _lastCommandMessage = result.Message;

        AppendAuditEntry(bridgeRoot, new JsonObject
        {
            ["atUtc"] = _lastCommandAtUtc.ToString("O"),
            ["id"] = id,
            ["command"] = command,
            ["success"] = result.Success,
            ["message"] = result.Message
        }.ToJsonString());

        var responsePath = Path.Combine(responsesDir, Path.GetFileName(cmdFile));
        File.WriteAllText(responsePath, BuildResponseJson(id, result.Success, result.Message));
        File.Delete(cmdFile);
    }

    private static void AppendAuditEntry(string bridgeRoot, string line)
    {
        var auditPath = Path.Combine(bridgeRoot, "audit.log");
        Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);

        var lines = File.Exists(auditPath)
            ? File.ReadAllLines(auditPath).TakeLast(499).ToList()
            : new List<string>();
        lines.Add(line);
        File.WriteAllLines(auditPath, lines, Encoding.UTF8);
    }

    private static (bool Success, string Message) Execute(string command, string raw)
    {
        try
        {
            return command switch
            {
                "editor.state" => EditorState(),
                "project.info" => ProjectCapabilities(),
                "project.health_check" => ProjectCapabilities(),
                "project.capabilities" => ProjectCapabilities(),
                "read_console" => ConsoleRead(),
                "manage_asset" => ManageAsset(raw),
                "manage_hierarchy" => ManageHierarchy(raw),
                "manage_scene" => ManageScene(raw),
                "manage_gameobject" => ManageGameObject(raw),
                "manage_components" => ManageComponents(raw),
                "manage_script" => ManageScript(raw),
                "manage_scriptableobject" => ManageScriptableObject(raw),
                "manage_prefabs" => ManagePrefabs(raw),
                "manage_graph" => ManageGraph(raw),
                "manage_ui" => ManageUi(raw),
                "manage_localization" => ManageLocalization(raw),
                "manage_editor" => ManageEditor(raw),
                "manage_input" => ManageInput(raw),
                "manage_camera" => ManageCamera(raw),
                "manage_graphics" => ManageGraphics(raw),
                "manage_profiler" => ManageProfiler(raw),
                "manage_build" => ManageBuild(raw),
                "run_tests" => TestsRun(raw),
                "get_test_job" => TestsResults(),
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
            // For pipeline stability, manage_prefabs should not fail hard on missing source objects.
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
            Debug.LogWarning($"MCP manage_prefabs: source missing, created fallback object '{fallbackName}'.");
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

        var type = FindComponentType(typeName!);
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

    private static (bool, string) GameObjectInvokeMethod(string raw)
    {
        var targetPath = JsonString(raw, "targetPath") ?? JsonString(raw, "target");
        var componentTypeName = JsonString(raw, "component_type") ?? JsonString(raw, "componentType");
        var methodName = JsonString(raw, "method");
        if (string.IsNullOrWhiteSpace(targetPath)) return (false, "targetPath is required");
        if (string.IsNullOrWhiteSpace(componentTypeName)) return (false, "component_type is required");
        if (string.IsNullOrWhiteSpace(methodName)) return (false, "method is required");

        var go = FindByPath(targetPath!);
        if (go == null) return (false, $"target not found: {targetPath}");

        var component = FindComponent(go, componentTypeName!);
        if (component == null)
        {
            return (false, $"component not found: {componentTypeName} on {targetPath}");
        }

        using var doc = JsonDocument.Parse(raw);
        var args = doc.RootElement.TryGetProperty("arguments", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : doc.RootElement;
        var invocationArgs = args.TryGetProperty("arguments", out var argsArray) && argsArray.ValueKind == JsonValueKind.Array ? argsArray : default;

        var warnings = new List<string>();
        var methods = component.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .OrderBy(m => m.GetParameters().Length)
            .ToList();

        foreach (var method in methods)
        {
            if (!TryBindInvocationArguments(method, invocationArgs, out var boundArgs, out var warning))
            {
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    warnings.Add(warning!);
                }
                continue;
            }

            try
            {
                Undo.RecordObject(component, $"MCP invoke {methodName}");
                var returnValue = method.Invoke(component, boundArgs);
                EditorUtility.SetDirty(component);
                var payload = new JsonObject
                {
                    ["success"] = true,
                    ["target"] = targetPath,
                    ["component_type"] = component.GetType().FullName,
                    ["method"] = method.Name,
                    ["return_value"] = SerializeInvocationReturnValue(returnValue),
                    ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray())
                };
                return (true, payload.ToJsonString());
            }
            catch (TargetInvocationException tie)
            {
                return (false, $"Invocation failed: {tie.InnerException?.Message ?? tie.Message}");
            }
            catch (Exception ex)
            {
                return (false, $"Invocation failed: {ex.Message}");
            }
        }

        if (warnings.Count > 0)
        {
            return (false, $"{methodName} could not be bound: {string.Join("; ", warnings.Distinct())}");
        }

        return (false, $"No matching overload found for {methodName}");
    }

    private static (bool, string) ComponentGetSerialized(string raw)
    {
        var targetPath = JsonString(raw, "targetPath") ?? JsonString(raw, "target");
        var componentTypeName = JsonString(raw, "component_type") ?? JsonString(raw, "componentType");
        if (string.IsNullOrWhiteSpace(targetPath)) return (false, "targetPath is required");
        if (string.IsNullOrWhiteSpace(componentTypeName)) return (false, "component_type is required");

        var go = FindByPath(targetPath!);
        if (go == null) return (false, $"target not found: {targetPath}");

        var component = FindComponent(go, componentTypeName!);
        if (component == null)
        {
            return (false, $"component not found: {componentTypeName} on {targetPath}");
        }

        var report = BuildSerializedComponentReport(component, targetPath!);
        return (true, report.ToJsonString());
    }

    private static (bool, string) ComponentSetSerialized(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var args = doc.RootElement.TryGetProperty("arguments", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : doc.RootElement;
        var targetPath = JsonString(raw, "targetPath") ?? JsonString(raw, "target");
        var componentTypeName = JsonString(raw, "component_type") ?? JsonString(raw, "componentType");
        if (string.IsNullOrWhiteSpace(targetPath)) return (false, "targetPath is required");
        if (string.IsNullOrWhiteSpace(componentTypeName)) return (false, "component_type is required");

        if (!args.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return (false, "properties is required");
        }

        var go = FindByPath(targetPath!);
        if (go == null) return (false, $"target not found: {targetPath}");

        var component = FindComponent(go, componentTypeName!);
        if (component == null)
        {
            return (false, $"component not found: {componentTypeName} on {targetPath}");
        }

        var so = new SerializedObject(component);
        var changedFields = new JsonArray();
        var warnings = new JsonArray();

        so.UpdateIfRequiredOrScript();
        Undo.RecordObject(component, "MCP set serialized");

        foreach (var property in properties.EnumerateObject())
        {
            var serializedProperty = so.FindProperty(property.Name);
            if (serializedProperty == null)
            {
                warnings.Add($"property not found: {property.Name}");
                continue;
            }

            if (!TrySetSerializedPropertyValue(serializedProperty, property.Value, out var warning))
            {
                warnings.Add(warning ?? $"unsupported property type: {serializedProperty.propertyType}");
                continue;
            }

            changedFields.Add(property.Name);
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(component);
        if (PrefabUtility.IsPartOfPrefabInstance(component))
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        var report = new JsonObject
        {
            ["success"] = true,
            ["target"] = targetPath,
            ["component_type"] = component.GetType().FullName,
            ["changed_fields"] = changedFields,
            ["warnings"] = warnings
        };
        return (true, report.ToJsonString());
    }

    private static Type? FindComponentType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var trimmed = typeName!.Trim();
        var direct = Type.GetType(trimmed);
        if (direct != null)
        {
            return direct;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            var match = types.FirstOrDefault(t =>
                string.Equals(t.FullName, trimmed, StringComparison.Ordinal) ||
                string.Equals(t.Name, trimmed, StringComparison.Ordinal));
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static Component? FindComponent(GameObject go, string? typeName)
    {
        var type = FindComponentType(typeName);
        if (type == null || !typeof(Component).IsAssignableFrom(type))
        {
            return null;
        }

        return go.GetComponent(type) as Component;
    }

    private static JsonObject BuildSerializedComponentReport(Component component, string targetPath)
    {
        var so = new SerializedObject(component);
        var fields = new JsonArray();
        var objectReferences = new JsonArray();
        var missingReferences = new JsonArray();

        so.UpdateIfRequiredOrScript();

        var prop = so.GetIterator();
        var enterChildren = true;
        while (prop.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (string.Equals(prop.propertyPath, "m_Script", StringComparison.Ordinal))
            {
                continue;
            }

            var field = new JsonObject
            {
                ["path"] = prop.propertyPath,
                ["name"] = prop.name,
                ["type"] = prop.propertyType.ToString()
            };

            var value = SerializeSerializedPropertyValue(prop);
            if (value != null)
            {
                field["value"] = value;
            }

            fields.Add(field);

            if (prop.propertyType == SerializedPropertyType.ObjectReference)
            {
                if (prop.objectReferenceValue != null)
                {
                    objectReferences.Add(SerializeObjectReference(prop.objectReferenceValue, prop.propertyPath, prop.name));
                }
                else
                {
                    missingReferences.Add(new JsonObject
                    {
                        ["path"] = prop.propertyPath,
                        ["name"] = prop.name,
                        ["reason"] = "null"
                    });
                }
            }
        }

        return new JsonObject
        {
            ["success"] = true,
            ["target"] = targetPath,
            ["component_type"] = component.GetType().FullName,
            ["fields"] = fields,
            ["object_references"] = objectReferences,
            ["missing_references"] = missingReferences
        };
    }

    private static JsonNode? SerializeSerializedPropertyValue(SerializedProperty property)
    {
        try
        {
            return property.propertyType switch
            {
                SerializedPropertyType.Boolean => JsonValue.Create(property.boolValue),
                SerializedPropertyType.Integer => JsonValue.Create(property.longValue),
                SerializedPropertyType.Float => JsonValue.Create(property.doubleValue),
                SerializedPropertyType.String => JsonValue.Create(property.stringValue),
                SerializedPropertyType.Enum => JsonValue.Create(SerializeEnumProperty(property)),
                SerializedPropertyType.ObjectReference => SerializeObjectReference(property.objectReferenceValue, property.propertyPath, property.name),
                SerializedPropertyType.LayerMask => JsonValue.Create(property.intValue),
                _ => JsonValue.Create(property.ToString())
            };
        }
        catch
        {
            return JsonValue.Create(property.ToString());
        }
    }

    private static string SerializeEnumProperty(SerializedProperty property)
    {
        if (property.enumDisplayNames != null && property.enumDisplayNames.Length > 0)
        {
            var index = Math.Clamp(property.enumValueIndex, 0, property.enumDisplayNames.Length - 1);
            return property.enumDisplayNames[index];
        }

        return property.enumValueIndex.ToString();
    }

    private static JsonObject SerializeObjectReference(UnityEngine.Object? obj, string? propertyPath = null, string? propertyName = null)
    {
        var node = new JsonObject();
        if (propertyPath != null)
        {
            node["path"] = propertyPath;
        }
        if (propertyName != null)
        {
            node["name"] = propertyName;
        }
        if (obj == null)
        {
            node["value"] = null;
            return node;
        }

        node["value"] = obj.name;
        node["type"] = obj.GetType().FullName;
        var assetPath = AssetDatabase.GetAssetPath(obj);
        if (!string.IsNullOrWhiteSpace(assetPath))
        {
            node["assetPath"] = assetPath;
        }
        if (obj is GameObject go)
        {
            node["scenePath"] = GetPath(go);
        }
        else if (obj is Component component)
        {
            node["scenePath"] = GetPath(component.gameObject);
        }

        return node;
    }

    private static bool TrySetSerializedPropertyValue(SerializedProperty property, JsonElement value, out string? warning)
    {
        warning = null;
        try
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (TryReadBoolean(value, out var boolValue))
                    {
                        property.boolValue = boolValue;
                        return true;
                    }
                    warning = $"expected boolean for {property.propertyPath}";
                    return false;
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    if (TryReadLong(value, out var longValue))
                    {
                        property.longValue = longValue;
                        return true;
                    }
                    warning = $"expected integer for {property.propertyPath}";
                    return false;
                case SerializedPropertyType.Float:
                    if (TryReadDouble(value, out var doubleValue))
                    {
                        property.doubleValue = doubleValue;
                        return true;
                    }
                    warning = $"expected number for {property.propertyPath}";
                    return false;
                case SerializedPropertyType.String:
                    property.stringValue = value.ValueKind == JsonValueKind.Null ? string.Empty : value.ToString();
                    return true;
                case SerializedPropertyType.Enum:
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var enumName = value.GetString() ?? string.Empty;
                        var index = Array.FindIndex(property.enumDisplayNames, n => string.Equals(n, enumName, StringComparison.Ordinal));
                        if (index < 0)
                        {
                            index = Array.FindIndex(property.enumNames, n => string.Equals(n, enumName, StringComparison.Ordinal));
                        }
                        if (index >= 0)
                        {
                            property.enumValueIndex = index;
                            return true;
                        }

                        warning = $"unknown enum value '{enumName}' for {property.propertyPath}";
                        return false;
                    }

                    if (TryReadLong(value, out var enumIndex))
                    {
                        property.enumValueIndex = (int)enumIndex;
                        return true;
                    }

                    warning = $"expected enum string or integer for {property.propertyPath}";
                    return false;
                case SerializedPropertyType.ObjectReference:
                    if (TryResolveObjectReference(value, out var objectReference, out var objectWarning))
                    {
                        property.objectReferenceValue = objectReference;
                        return true;
                    }

                    warning = objectWarning ?? $"unable to resolve object reference for {property.propertyPath}";
                    return false;
                default:
                    warning = $"unsupported serialized type {property.propertyType} for {property.propertyPath}";
                    return false;
            }
        }
        catch (Exception ex)
        {
            warning = ex.Message;
            return false;
        }
    }

    private static bool TryResolveObjectReference(JsonElement value, out UnityEngine.Object? reference, out string? warning)
    {
        reference = null;
        warning = null;

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                reference = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(text);
                if (reference != null)
                {
                    return true;
                }
            }

            var go = FindByPath(text);
            if (go != null)
            {
                reference = go;
                return true;
            }

            warning = $"object reference not found: {text}";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("assetPath", out var assetPath) && assetPath.ValueKind == JsonValueKind.String)
            {
                reference = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath.GetString() ?? string.Empty);
                if (reference != null)
                {
                    return true;
                }
            }

            if (value.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String)
            {
                var go = FindByPath(pathValue.GetString() ?? string.Empty);
                if (go != null)
                {
                    reference = go;
                    return true;
                }
            }

            if (value.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                var go = FindByPath(nameValue.GetString() ?? string.Empty);
                if (go != null)
                {
                    reference = go;
                    return true;
                }
            }
        }

        warning = $"unsupported object reference payload: {value.ValueKind}";
        return false;
    }

    private static bool TryBindInvocationArguments(MethodInfo method, JsonElement argsArray, out object?[] boundArgs, out string? warning)
    {
        warning = null;
        var parameters = method.GetParameters();
        var rawArgs = new List<JsonElement>();
        if (argsArray.ValueKind == JsonValueKind.Array)
        {
            rawArgs = argsArray.EnumerateArray().ToList();
        }

        if (rawArgs.Count != parameters.Length)
        {
            boundArgs = Array.Empty<object?>();
            warning = $"parameter count mismatch for {method.Name}: expected {parameters.Length}, got {rawArgs.Count}";
            return false;
        }

        var converted = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (!TryConvertInvocationArgument(rawArgs[i], parameters[i].ParameterType, out var value, out var convertWarning))
            {
                boundArgs = Array.Empty<object?>();
                warning = convertWarning ?? $"argument {i} could not be converted for {method.Name}";
                return false;
            }

            converted[i] = value;
        }

        boundArgs = converted;
        return true;
    }

    private static bool TryConvertInvocationArgument(JsonElement value, Type targetType, out object? converted, out string? warning)
    {
        warning = null;
        converted = null;

        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType != null)
        {
            targetType = nullableType;
            if (value.ValueKind == JsonValueKind.Null)
            {
                return true;
            }
        }

        if (targetType == typeof(string))
        {
            converted = value.ValueKind == JsonValueKind.Null ? null : value.ToString();
            return true;
        }

        if (targetType == typeof(bool))
        {
            if (TryReadBoolean(value, out var boolValue))
            {
                converted = boolValue;
                return true;
            }

            warning = "expected boolean argument";
            return false;
        }

        if (targetType == typeof(int))
        {
            if (TryReadLong(value, out var longValue))
            {
                converted = (int)longValue;
                return true;
            }

            warning = "expected integer argument";
            return false;
        }

        if (targetType == typeof(long))
        {
            if (TryReadLong(value, out var longValue))
            {
                converted = longValue;
                return true;
            }

            warning = "expected integer argument";
            return false;
        }

        if (targetType == typeof(float))
        {
            if (TryReadFloat(value, out var floatValue))
            {
                converted = floatValue;
                return true;
            }

            warning = "expected floating-point argument";
            return false;
        }

        if (targetType == typeof(double))
        {
            if (TryReadDouble(value, out var doubleValue))
            {
                converted = doubleValue;
                return true;
            }

            warning = "expected floating-point argument";
            return false;
        }

        if (targetType.IsEnum)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                try
                {
                    converted = Enum.Parse(targetType, value.GetString() ?? string.Empty, ignoreCase: true);
                    return true;
                }
                catch (Exception ex)
                {
                    warning = ex.Message;
                    return false;
                }
            }

            if (TryReadLong(value, out var enumIndex))
            {
                converted = Enum.ToObject(targetType, (int)enumIndex);
                return true;
            }

            warning = "expected enum string or integer argument";
            return false;
        }

        if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
                {
                    converted = null;
                    return true;
                }

                if (targetType == typeof(GameObject))
                {
                    converted = FindByPath(text);
                    return converted != null;
                }

                if (typeof(Component).IsAssignableFrom(targetType))
                {
                    var go = FindByPath(text);
                    converted = go != null ? go.GetComponent(targetType) : null;
                    return converted != null;
                }

                converted = text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    ? AssetDatabase.LoadAssetAtPath(text, targetType)
                    : AssetDatabase.LoadAssetAtPath(text, targetType);
                return converted != null;
            }

            warning = $"unable to convert argument to {targetType.Name}";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        warning = $"unsupported argument type: {targetType.Name}";
        return false;
    }

    private static JsonNode? SerializeInvocationReturnValue(object? returnValue)
    {
        if (returnValue == null)
        {
            return null;
        }

        return returnValue switch
        {
            string s => JsonValue.Create(s),
            bool b => JsonValue.Create(b),
            int i => JsonValue.Create(i),
            long l => JsonValue.Create(l),
            float f => JsonValue.Create(f),
            double d => JsonValue.Create(d),
            decimal m => JsonValue.Create((double)m),
            Enum e => JsonValue.Create(e.ToString()),
            UnityEngine.Object obj => SerializeObjectReference(obj),
            _ => JsonValue.Create(returnValue.ToString())
        };
    }

    private static bool TryReadBoolean(JsonElement value, out bool result)
    {
        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        {
            result = value.GetBoolean();
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out result))
        {
            return true;
        }

        result = false;
        return false;
    }

    private static bool TryReadLong(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryReadDouble(JsonElement value, out double result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out result))
        {
            return true;
        }

        result = 0d;
        return false;
    }

    private static (bool, string) AssetCreateFolder(string raw)
    {
        var rel = JsonArgumentString(raw, "path") ?? JsonArgumentString(raw, "folderPath");
        if (string.IsNullOrWhiteSpace(rel))
        {
            return (false, "path is required");
        }

        rel = rel.Replace("\\", "/");
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"path must be under project root: {rel}");
        }

        Directory.CreateDirectory(full);
        AssetDatabase.Refresh();
        return (true, $"manage_asset create_folder: {rel}");
    }

    private static (bool, string) AssetExists(string raw)
    {
        var rel = JsonArgumentString(raw, "path");
        if (string.IsNullOrWhiteSpace(rel))
        {
            return (false, "path is required");
        }

        rel = rel.Replace("\\", "/");
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var exists = File.Exists(full) || Directory.Exists(full);
        return (true, $"manage_asset exists: path={rel}; exists={exists}");
    }

    private static (bool, string) AssetReadTextFile(string raw)
    {
        return ReadWriteDiagnosticsTextFile(raw, write: false);
    }

    private static (bool, string) AssetWriteTextFile(string raw)
    {
        return ReadWriteDiagnosticsTextFile(raw, write: true);
    }

    private static (bool, string) ReadWriteDiagnosticsTextFile(string raw, bool write)
    {
        var rel = JsonArgumentString(raw, "path") ?? JsonArgumentString(raw, "folderPath");
        if (string.IsNullOrWhiteSpace(rel))
        {
            return (false, "path is required");
        }

        rel = rel.Replace("\\", "/");
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"path must be under project root: {rel}");
        }

        var normalizedRel = Path.GetRelativePath(root, full).Replace("\\", "/");
        if (!IsAllowedDiagnosticsPath(normalizedRel))
        {
            return (false, $"path must be under Library/McpDiagnostics: {normalizedRel}");
        }

        if (write)
        {
            var contents = JsonArgumentString(raw, "contents") ?? JsonArgumentString(raw, "text") ?? string.Empty;
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(full, contents, Encoding.UTF8);
            AssetDatabase.Refresh();
            return (true, new JsonObject
            {
                ["success"] = true,
                ["path"] = normalizedRel,
                ["size_bytes"] = Encoding.UTF8.GetByteCount(contents)
            }.ToJsonString());
        }

        if (Directory.Exists(full))
        {
            return (false, $"path points to a directory: {normalizedRel}");
        }

        if (!File.Exists(full))
        {
            return (false, $"file not found: {normalizedRel}");
        }

        var text = File.ReadAllText(full, Encoding.UTF8);
        return (true, new JsonObject
        {
            ["success"] = true,
            ["path"] = normalizedRel,
            ["text"] = text,
            ["size_bytes"] = Encoding.UTF8.GetByteCount(text)
        }.ToJsonString());
    }

    private static bool IsAllowedDiagnosticsPath(string relativePath)
    {
        var normalized = relativePath.Replace("\\", "/").TrimStart('/');
        return normalized.Equals("Library/McpDiagnostics", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("Library/McpDiagnostics/", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool, string) EditorCompileStatus()
    {
        return (true, $"isCompiling={EditorApplication.isCompiling}");
    }

    private static (bool, string) EditorState()
    {
        return (true, BuildEditorStateReport().ToJsonString());
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

    private static (bool, string) ProjectCapabilities()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var packageJsonPath = Path.Combine(projectRoot, "Packages", "com.xlabkm.unity-mcp", "package.json");
        var packageJson = File.Exists(packageJsonPath) ? File.ReadAllText(packageJsonPath) : string.Empty;
        var packageVersion = JsonString(packageJson, "version") ?? "unknown";
        var bridgeFile = Path.Combine(projectRoot, "Packages", "com.xlabkm.unity-mcp", "Editor", "McpBridgeProcessor.cs");
        var bridgeHashSource = packageJson;
        if (File.Exists(bridgeFile))
        {
            bridgeHashSource += "\n" + File.ReadAllText(bridgeFile);
        }

        var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
        var manifest = File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : string.Empty;

        var isCompiling = EditorApplication.isCompiling;
        var isUpdating = EditorApplication.isUpdating;
        var isPlaying = EditorApplication.isPlaying;
        var isPaused = EditorApplication.isPaused;
        var blockingReasons = new List<string>();
        if (isCompiling) blockingReasons.Add("compiling");
        if (isUpdating) blockingReasons.Add("updating");
        if (isPlaying) blockingReasons.Add("play-mode-active");

        var capabilities = new JsonArray();
        AddCapability(capabilities, "project.info", "read", true, "Server-side project metadata snapshot.");
        AddCapability(capabilities, "project.health_check", "read", true, "Server-side URP/package health probe.");
        AddCapability(capabilities, "project.capabilities", "read", true, "Live bridge capability snapshot.");
        AddCapability(capabilities, "editor.state", "status", true, "Live editor state is available from the bridge.");
        AddCapability(capabilities, "read_console", "read", true, "Editor log buffer is captured by the bridge.");

        AddCapability(capabilities, "manage_asset", "create_folder", true, "Project-local folder creation is supported.");
        AddCapability(capabilities, "manage_asset", "exists", true, "Bridge can check project-local paths.");
        AddCapability(capabilities, "manage_asset", "refresh", true, "AssetDatabase refresh is supported.");
        AddCapability(capabilities, "manage_asset", "list_modified", true, "Modified asset scan is supported.");
        AddCapability(capabilities, "manage_asset", "change_summary", true, "Change summary is derived from modified assets.");
        AddCapability(capabilities, "manage_asset", "docs_update", true, "Docs write helper is supported.");
        AddCapability(capabilities, "manage_asset", "read_text_file", true, "Controlled diagnostics text read is supported.");
        AddCapability(capabilities, "manage_asset", "write_text_file", true, "Controlled diagnostics text write is supported.");
        AddCapability(capabilities, "manage_asset", "list_localization_keys", true, "Localization key discovery is supported.");
        AddCapability(capabilities, "manage_asset", "resolve_localization_keys", true, "Localization key resolution is supported.");
        AddCapability(capabilities, "manage_asset", "classify_risk", true, "Deterministic change-risk classification is supported.");

        AddCapability(capabilities, "manage_hierarchy", "list", true, "Scene root enumeration is supported.");
        AddCapability(capabilities, "manage_hierarchy", "find", true, "Name-based hierarchy search is supported.");

        AddCapability(capabilities, "manage_scene", "create", true, "Scene creation is supported.");
        AddCapability(capabilities, "manage_scene", "open", true, "Scene open is supported.");
        AddCapability(capabilities, "manage_scene", "save", true, "Scene save is supported.");
        AddCapability(capabilities, "manage_scene", "validate_references", true, "Current validation counts missing script markers.");

        AddCapability(capabilities, "manage_gameobject", "create", true, "GameObject creation is supported.");
        AddCapability(capabilities, "manage_gameobject", "modify", true, "Rename and active-state changes are supported.");
        AddCapability(capabilities, "manage_gameobject", "invoke_method", true, "Public MonoBehaviour method invocation is supported.");

        AddCapability(capabilities, "manage_components", "add", true, "Component add is supported.");
        AddCapability(capabilities, "manage_components", "set", true, "Basic component state changes are supported.");
        AddCapability(capabilities, "manage_components", "get_serialized", true, "Structured serialized readback is supported.");
        AddCapability(capabilities, "manage_components", "set_serialized", true, "Structured serialized writeback is supported.");

        AddCapability(capabilities, "manage_script", "create_or_edit", true, "Script create/edit is supported.");
        AddCapability(capabilities, "manage_scriptableobject", "create_or_edit", true, "ScriptableObject create/edit is supported.");
        AddCapability(capabilities, "manage_scriptableobject", "validate_schema", true, "Source schema validation is supported.");

        AddCapability(capabilities, "manage_prefabs", "create", true, "Prefab creation is supported.");
        AddCapability(capabilities, "manage_prefabs", "open", true, "Prefab open is supported.");
        AddCapability(capabilities, "manage_prefabs", "save", true, "Prefab save is supported.");
        AddCapability(capabilities, "manage_prefabs", "instantiate", true, "Prefab instantiation is supported.");
        AddCapability(capabilities, "manage_prefabs", "validate_references", true, "Prefab load-at-path validation is supported.");

        AddCapability(capabilities, "manage_graph", "open_or_create", true, "Graph open/create is supported.");
        AddCapability(capabilities, "manage_graph", "connect", true, "Graph connection edits are supported.");
        AddCapability(capabilities, "manage_graph", "edit", true, "Graph edit patches are supported.");
        AddCapability(capabilities, "manage_graph", "validate", true, "Graph state validation is supported.");

        AddCapability(capabilities, "manage_ui", "create_or_edit", true, "UI plan file generation is supported.");

        AddCapability(capabilities, "manage_localization", "key_add", true, "Localization key append is supported.");
        AddCapability(capabilities, "manage_localization", "tables", true, "Localization table snapshot is supported.");
        AddCapability(capabilities, "manage_localization", "validate_assets", true, "Localization asset validation is supported.");
        AddCapability(capabilities, "manage_localization", "validate_key_coverage", true, "Localization key coverage validation is supported.");
        AddCapability(capabilities, "manage_localization", "validate_fallback_language", true, "Fallback language validation is supported.");

        AddCapability(capabilities, "manage_editor", "play_mode", true, "Play mode enter/exit/status is supported.");
        AddCapability(capabilities, "manage_editor", "status", true, "Editor status is supported.");
        AddCapability(capabilities, "manage_editor", "compile_status", true, "Compile status is supported.");
        AddCapability(capabilities, "manage_editor", "install", true, "Server-side embedded Unity MCP package installation is supported.");
        AddCapability(capabilities, "manage_editor", "update", true, "Server-side embedded Unity MCP package update is supported.");
        AddCapability(capabilities, "manage_editor", "delete", true, "Server-side embedded Unity MCP package removal is supported.");

        AddCapability(capabilities, "manage_input", "send", true, "Keyboard and mouse event synthesis is supported during Play Mode.");

        AddCapability(capabilities, "manage_camera", "screenshot", true, "Screen capture is supported.");

        AddCapability(capabilities, "manage_graphics", "set_quality_level", true, "Quality level switching is supported.");
        AddCapability(capabilities, "manage_graphics", "validate_profile_assignment", true, "Current quality profile assignment validation is supported.");

        AddCapability(capabilities, "manage_profiler", "get_counters", true, "Profiler counters are supported.");
        AddCapability(capabilities, "manage_profiler", "get_frame_timing", true, "Frame timing sampling is supported.");

        AddCapability(capabilities, "manage_build", "profiles", true, "Current runtime exposes active build target switching.");
        AddCapability(capabilities, "manage_build", "scenes", true, "Build scene list access is supported.");

        AddCapability(capabilities, "run_tests", "run", true, "Test execution is supported.");
        AddCapability(capabilities, "get_test_job", "poll", true, "Test job polling is supported.");

        var report = new JsonObject
        {
            ["tool"] = "project.capabilities",
            ["projectRoot"] = projectRoot,
            ["projectName"] = Path.GetFileName(projectRoot),
            ["unityVersion"] = Application.unityVersion,
            ["bridgePackage"] = new JsonObject
            {
                ["name"] = "com.xlabkm.unity-mcp",
                ["version"] = packageVersion,
                ["buildHash"] = Sha256Hex(bridgeHashSource)
            },
            ["bridgeHealth"] = BuildBridgeHealthReport(projectRoot),
            ["editor"] = new JsonObject
            {
                ["isPlaying"] = isPlaying,
                ["isPaused"] = isPaused,
                ["isCompiling"] = isCompiling,
                ["isUpdating"] = isUpdating
            },
            ["readyForTools"] = blockingReasons.Count == 0,
            ["blockingReasons"] = new JsonArray(blockingReasons.Select(r => (JsonNode?)r).ToArray()),
            ["readinessFlags"] = new JsonObject
            {
                ["profiler"] = true,
                ["localization"] = ManifestHasDependency(manifest, "com.unity.localization"),
                ["tests"] = ManifestHasDependency(manifest, "com.unity.test-framework"),
                ["inputSystem"] = ManifestHasDependency(manifest, "com.unity.inputsystem"),
                ["urp"] = ManifestHasDependency(manifest, "com.unity.render-pipelines.universal")
            },
            ["capabilities"] = capabilities
        };

        return (true, report.ToJsonString());
    }

    private static void AddCapability(JsonArray capabilities, string tool, string action, bool supported, string notes)
    {
        capabilities.Add(new JsonObject
        {
            ["tool"] = tool,
            ["action"] = action,
            ["supported"] = supported,
            ["notes"] = notes
        });
    }

    private static bool ManifestHasDependency(string manifestJson, string packageName)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            return doc.RootElement.TryGetProperty("dependencies", out var deps)
                   && deps.ValueKind == JsonValueKind.Object
                   && deps.TryGetProperty(packageName, out _);
        }
        catch
        {
            return false;
        }
    }

    private static string Sha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static (bool, string) ManageAsset(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "refresh").ToLowerInvariant();
        return action switch
        {
            "create_folder" => AssetCreateFolder(raw),
            "exists" => AssetExists(raw),
            "refresh" => AssetRefresh(),
            "list_modified" => AssetListModified(raw),
            "change_summary" => ChangeSummary(raw),
            "docs_update" => ProjectDocsUpdate(raw),
            "read_text_file" => AssetReadTextFile(raw),
            "write_text_file" => AssetWriteTextFile(raw),
            "list_localization_keys" => LocalizationKeyList(raw),
            "resolve_localization_keys" => LocalizationKeyResolve(raw),
            "classify_risk" => RiskClassify(raw),
            _ => (false, $"Unsupported manage_asset action: {action}")
        };
    }

    private static (bool, string) ManageHierarchy(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "list").ToLowerInvariant();
        return action switch
        {
            "list" => HierarchyList(),
            "find" => HierarchyFind(raw),
            _ => (false, $"Unsupported manage_hierarchy action: {action}")
        };
    }

    private static (bool, string) ManageScene(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "validate_references").ToLowerInvariant();
        return action switch
        {
            "create" => SceneCreate(raw),
            "open" => SceneOpen(raw),
            "save" => SceneSave(raw),
            "validate_references" => SceneValidateRefs(raw),
            _ => (false, $"Unsupported manage_scene action: {action}")
        };
    }

    private static (bool, string) ManageGameObject(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "modify").ToLowerInvariant();
        return action switch
        {
            "create" => GameObjectCreate(raw),
            "modify" => GameObjectModify(raw),
            "invoke_method" => GameObjectInvokeMethod(raw),
            _ => (false, $"Unsupported manage_gameobject action: {action}")
        };
    }

    private static (bool, string) ManageComponents(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "add").ToLowerInvariant();
        return action switch
        {
            "add" => ComponentAdd(raw),
            "set" => ComponentSet(raw),
            "get_serialized" => ComponentGetSerialized(raw),
            "set_serialized" => ComponentSetSerialized(raw),
            _ => (false, $"Unsupported manage_components action: {action}")
        };
    }

    private static (bool, string) ManageScript(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "create_or_edit").ToLowerInvariant();
        return action == "create_or_edit"
            ? ScriptCreateOrEdit(raw)
            : (false, $"Unsupported manage_script action: {action}");
    }

    private static (bool, string) ManagePrefabs(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "create").ToLowerInvariant();
        return action switch
        {
            "create" => PrefabCreate(raw),
            "instantiate" => PrefabInstantiate(raw),
            "open" => PrefabOpen(raw),
            "save" => PrefabSave(),
            "validate" => PrefabValidate(raw),
            "validate_references" => PrefabValidate(raw),
            _ => (false, $"Unsupported manage_prefabs action: {action}")
        };
    }

    private static (bool, string) ManageScriptableObject(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "create_or_edit").ToLowerInvariant();
        return action switch
        {
            "create_or_edit" => ScriptableObjectCreateOrEdit(raw),
            "validate_schema" => ScriptableObjectValidateSchema(raw),
            _ => (false, $"Unsupported manage_scriptableobject action: {action}")
        };
    }

    private static (bool, string) ManageEditor(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "play_mode").ToLowerInvariant();
        var mode = (JsonArgumentString(raw, "mode") ?? "status").ToLowerInvariant();
        if (action == "compile_status")
        {
            return EditorCompileStatus();
        }

        if (action != "play_mode" && action != "status")
        {
            return (false, $"Unsupported manage_editor action: {action}");
        }

        return mode switch
        {
            "enter" => PlaymodeEnter(),
            "exit" => PlaymodeExit(),
            "status" => EditorState(),
            _ => (false, $"Unsupported play_mode mode: {mode}")
        };
    }

    private static (bool, string) ManageInput(string raw)
    {
        if (!EditorApplication.isPlaying)
        {
            return (false, "manage_input requires Play Mode");
        }

        using var doc = JsonDocument.Parse(raw);
        var args = doc.RootElement.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : doc.RootElement;

        var keys = ReadStringArray(args, "keys");
        var mouseButtons = ReadStringArray(args, "mouse_buttons");
        if (mouseButtons.Count == 0)
        {
            mouseButtons = ReadStringArray(args, "mouseButtons");
        }

        var mousePosition = ReadIntPair(args, "mouse_position") ?? ReadIntPair(args, "mousePosition");
        var durationMs = ReadInt(args, "duration_ms") ?? ReadInt(args, "durationMs") ?? 0;
        if (durationMs < 0)
        {
            durationMs = 0;
        }

        var window = ResolveGameViewWindow();
        if (window == null)
        {
            return (false, "Game view window not available");
        }

        var startFrame = Time.frameCount;
        var sentEvents = 0;

        if (mousePosition.HasValue)
        {
            SendMouseMove(window, mousePosition.Value.x, mousePosition.Value.y);
            sentEvents++;
        }

        foreach (var key in keys)
        {
            if (TryParseKeyCode(key, out var keyCode))
            {
                SendKeyEvent(window, keyCode, keyDown: true);
                sentEvents++;
            }
        }

        foreach (var button in mouseButtons)
        {
            if (TryParseMouseButton(button, out var mouseButton))
            {
                SendMouseButtonEvent(window, mouseButton, keyDown: true, mousePosition);
                sentEvents++;
            }
        }

        if (durationMs > 0)
        {
            var queuedKeys = keys
                .Select(k =>
                {
                    TryParseKeyCode(k, out var keyCode);
                    return keyCode;
                })
                .Where(keyCode => keyCode != KeyCode.None)
                .ToList();
            var queuedMouseButtons = mouseButtons
                .Where(b => TryParseMouseButton(b, out _))
                .Select(b =>
                {
                    var normalized = b.Trim().ToLowerInvariant();
                    return normalized switch
                    {
                        "right" or "rightmouse" or "right_mouse" or "1" => 1,
                        "middle" or "middlemouse" or "middle_mouse" or "2" => 2,
                        _ => 0
                    };
                })
                .ToList();
            var job = new PendingInputJob
            {
                JobId = Guid.NewGuid().ToString("N"),
                ReleaseAtUtc = DateTime.UtcNow.AddMilliseconds(durationMs),
                Keys = queuedKeys,
                MouseButtons = queuedMouseButtons,
                MousePosition = mousePosition,
                FrameStart = startFrame,
                FrameEnd = startFrame + Math.Max(1, durationMs / 16)
            };
            lock (PendingInputJobsGate)
            {
                PendingInputJobs.Add(job);
            }

            var response = new JsonObject
            {
                ["success"] = true,
                ["events_sent"] = sentEvents,
                ["frame_window"] = new JsonObject
                {
                    ["start"] = job.FrameStart,
                    ["end"] = job.FrameEnd
                },
                ["jobId"] = job.JobId,
                ["state"] = "queued"
            };
            return (true, response.ToJsonString());
        }

        foreach (var key in keys)
        {
            if (TryParseKeyCode(key, out var keyCode))
            {
                SendKeyEvent(window, keyCode, keyDown: false);
                sentEvents++;
            }
        }

        foreach (var button in mouseButtons)
        {
            if (TryParseMouseButton(button, out var mouseButton))
            {
                SendMouseButtonEvent(window, mouseButton, keyDown: false, mousePosition);
                sentEvents++;
            }
        }

        var immediateResponse = new JsonObject
        {
            ["success"] = true,
            ["events_sent"] = sentEvents,
            ["frame_window"] = new JsonObject
            {
                ["start"] = startFrame,
                ["end"] = startFrame
            },
            ["state"] = "completed"
        };
        return (true, immediateResponse.ToJsonString());
    }

    private static (bool, string) ManageCamera(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "screenshot").ToLowerInvariant();
        if (action != "screenshot")
        {
            return (false, $"Unsupported manage_camera action: {action}");
        }

        return Screenshot(raw);
    }

    private static (bool, string) ManageGraph(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "open_or_create").ToLowerInvariant();
        return action switch
        {
            "open_or_create" => GraphOpenOrCreate(raw),
            "connect" => GraphConnect(raw),
            "edit" => GraphEdit(raw),
            "validate" => GraphValidate(raw),
            _ => (false, $"Unsupported manage_graph action: {action}")
        };
    }

    private static (bool, string) ManageUi(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "create_or_edit").ToLowerInvariant();
        return action == "create_or_edit"
            ? UiCreateOrEdit(raw)
            : (false, $"Unsupported manage_ui action: {action}");
    }

    private static (bool, string) ManageLocalization(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "key_add").ToLowerInvariant();
        return action switch
        {
            "key_add" => LocalizationKeyAdd(raw),
            "tables" => LocalizationTables(raw),
            "validate_assets" => LocalizationValidateAssets(raw),
            "validate_key_coverage" => LocalizationValidateKeyCoverage(raw),
            "validate_fallback_language" => LocalizationValidateFallbackLanguage(raw),
            _ => (false, $"Unsupported manage_localization action: {action}")
        };
    }

    private static (bool, string) ManageGraphics(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "set_quality_level").ToLowerInvariant();
        if (action == "validate_profile_assignment")
        {
            return GraphicsValidateProfileAssignment(raw);
        }

        if (action != "set_quality_level")
        {
            return (false, $"Unsupported manage_graphics action: {action}");
        }

        var qualityName = JsonArgumentString(raw, "quality_level")
                          ?? JsonArgumentString(raw, "qualityLevel")
                          ?? JsonArgumentString(raw, "name");
        if (string.IsNullOrWhiteSpace(qualityName))
        {
            return (false, "quality_level is required");
        }

        var names = QualitySettings.names;
        var index = Array.FindIndex(names, n => string.Equals(n, qualityName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return (false, $"quality level not found: {qualityName}");
        }

        var applyExpensive = JsonBool(raw, "apply_expensive_changes")
                             ?? JsonBool(raw, "applyExpensiveChanges")
                             ?? true;
        QualitySettings.SetQualityLevel(index, applyExpensive);
        return (true, $"manage_graphics: active_quality_level={QualitySettings.names[QualitySettings.GetQualityLevel()]}; index={index}; applied={applyExpensive}");
    }

    private static (bool, string) GraphicsValidateProfileAssignment(string raw)
    {
        var names = QualitySettings.names ?? Array.Empty<string>();
        var activeIndex = QualitySettings.GetQualityLevel();
        var findings = new JsonArray();
        var profiles = new JsonArray();
        var errors = 0;
        var warnings = 0;

        if (names.Length == 0)
        {
            errors++;
            findings.Add(new JsonObject
            {
                ["severity"] = "error",
                ["issue"] = "no_quality_profiles",
                ["message"] = "No quality levels are configured"
            });
        }

        for (var i = 0; i < names.Length; i++)
        {
            var profileName = names[i];
            var active = i == activeIndex;
            profiles.Add(new JsonObject
            {
                ["index"] = i,
                ["name"] = profileName,
                ["active"] = active
            });

            if (string.IsNullOrWhiteSpace(profileName))
            {
                warnings++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "warning",
                    ["issue"] = "unnamed_quality_profile",
                    ["index"] = i,
                    ["message"] = "Quality level name is empty"
                });
            }
        }

        if (activeIndex < 0 || activeIndex >= names.Length)
        {
            errors++;
            findings.Add(new JsonObject
            {
                ["severity"] = "error",
                ["issue"] = "active_quality_index_out_of_range",
                ["activeIndex"] = activeIndex,
                ["message"] = "Active quality index is outside the configured range"
            });
        }

        var activeName = activeIndex >= 0 && activeIndex < names.Length ? names[activeIndex] : string.Empty;
        var report = new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_graphics",
            ["action"] = "validate_profile_assignment",
            ["buildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
            ["activeQualityLevel"] = activeName,
            ["activeQualityIndex"] = activeIndex,
            ["summary"] = new JsonObject
            {
                ["qualityProfiles"] = names.Length,
                ["errors"] = errors,
                ["warnings"] = warnings
            },
            ["profiles"] = profiles,
            ["findings"] = findings
        };
        report["recommendation"] = errors > 0
            ? "Fix quality profile assignment before release."
            : warnings > 0
                ? "Review unnamed profiles and clean up quality settings."
                : "Quality profile assignment looks healthy.";

        return (true, report.ToJsonString());
    }

    private static (bool, string) ManageProfiler(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "get_counters").ToLowerInvariant();
        return action switch
        {
            "get_counters" => (true, BuildProfilerCountersReport()),
            "get_frame_timing" => (true, BuildFrameTimingReport()),
            _ => (false, $"Unsupported manage_profiler action: {action}")
        };
    }

    private static string BuildProfilerCountersReport()
    {
        var counters = new JsonArray
        {
            BuildProfilerCounter("Render", "Batches Count"),
            BuildProfilerCounter("Render", "Draw Calls Count"),
            BuildProfilerCounter("Render", "SetPass Calls Count"),
            BuildProfilerCounter("Memory", "Total Used Memory"),
            BuildProfilerCounter("Memory", "Total Reserved Memory"),
            BuildProfilerCounter("Memory", "GC Reserved Memory"),
            BuildProfilerCounter("Scripts", "Behaviour Update Count"),
            BuildProfilerCounter("Scripts", "Script Count")
        };

        var report = new JsonObject
        {
            ["action"] = "get_counters",
            ["counters"] = counters,
            ["summary"] = new JsonObject
            {
                ["allocated"] = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),
                ["reserved"] = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
                ["unused_reserved"] = UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong(),
                ["mono_used"] = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(),
                ["mono_heap"] = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong()
            }
        };

        return report.ToJsonString();
    }

    private static string BuildFrameTimingReport()
    {
        var report = new JsonObject
        {
            ["action"] = "get_frame_timing",
            ["cpu_frame_time_ms"] = Math.Round(Time.deltaTime * 1000f, 3),
            ["gpu_frame_time_ms"] = 0.0,
            ["cpu_main_thread_ms"] = Math.Round(Time.deltaTime * 1000f, 3),
            ["cpu_render_thread_ms"] = 0.0,
            ["frame_count_sampled"] = 1
        };

        return report.ToJsonString();
    }

    private static JsonObject BuildProfilerCounter(string category, string name)
    {
        var snapshot = new JsonObject
        {
            ["category"] = category,
            ["name"] = name,
            ["supported"] = false,
            ["value"] = 0L
        };

        try
        {
            using var recorder = ProfilerRecorder.StartNew(GetProfilerCategory(category), name, 1);
            if (recorder.Valid)
            {
                snapshot["supported"] = true;
                snapshot["value"] = recorder.LastValue;
            }
        }
        catch (Exception ex)
        {
            snapshot["error"] = ex.Message;
        }

        return snapshot;
    }

    private static ProfilerCategory GetProfilerCategory(string category)
    {
        return category switch
        {
            "Render" => ProfilerCategory.Render,
            "Memory" => ProfilerCategory.Memory,
            "Scripts" => ProfilerCategory.Scripts,
            _ => ProfilerCategory.Scripts
        };
    }

    private static JsonObject BuildBridgeHealthReport(string projectRoot)
    {
        var bridgeRoot = Path.Combine(projectRoot, "Library", "XLabMcpBridge");
        var commandsDir = Path.Combine(bridgeRoot, "commands");
        var responsesDir = Path.Combine(bridgeRoot, "responses");
        var heartbeatPath = Path.Combine(bridgeRoot, "heartbeat.json");
        var now = DateTime.UtcNow;

        DateTime? heartbeatAt = null;
        if (File.Exists(heartbeatPath))
        {
            try
            {
                var heartbeatRaw = File.ReadAllText(heartbeatPath);
                if (JsonString(heartbeatRaw, "at") is string heartbeatText &&
                    DateTime.TryParse(heartbeatText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    heartbeatAt = parsed.ToUniversalTime();
                }
                else
                {
                    heartbeatAt = File.GetLastWriteTimeUtc(heartbeatPath);
                }
            }
            catch
            {
                heartbeatAt = File.GetLastWriteTimeUtc(heartbeatPath);
            }
        }
        else if (_lastHeartbeatUtc != DateTime.MinValue)
        {
            heartbeatAt = _lastHeartbeatUtc;
        }

        var commandFiles = Directory.Exists(commandsDir)
            ? Directory.GetFiles(commandsDir, "*.json")
                .Select(p => new FileInfo(p))
                .OrderBy(fi => fi.CreationTimeUtc)
                .ThenBy(fi => fi.Name, StringComparer.Ordinal)
                .ToList()
            : new List<FileInfo>();

        var queueDepth = commandFiles.Count;
        var oldestAgeMs = queueDepth == 0 ? 0L : Math.Max(0L, (long)(now - commandFiles[0].CreationTimeUtc).TotalMilliseconds);
        var heartbeatAgeMs = heartbeatAt.HasValue ? Math.Max(0L, (long)(now - heartbeatAt.Value).TotalMilliseconds) : (long?)null;
        var stuckThresholdMs = 10_000L;
        var stuckCommands = new JsonArray();
        foreach (var fi in commandFiles.Where(fi => (now - fi.CreationTimeUtc).TotalMilliseconds >= stuckThresholdMs).Take(10))
        {
            stuckCommands.Add(new JsonObject
            {
                ["file"] = fi.Name,
                ["age_ms"] = Math.Max(0L, (long)(now - fi.CreationTimeUtc).TotalMilliseconds)
            });
        }

        var responseDepth = Directory.Exists(responsesDir) ? Directory.GetFiles(responsesDir, "*.json").Length : 0;
        var state = heartbeatAgeMs is > 5000
            ? "stale"
            : queueDepth > 0
                ? "degraded"
                : "healthy";

        var recommendation = heartbeatAgeMs is > 5000
            ? "Bridge heartbeat is stale. Relaunch Unity and reopen the project."
            : queueDepth > 0
                ? "Bridge has queued commands. Wait for the queue to drain or retry after a short delay."
                : "Bridge is healthy.";

        JsonObject? lastCommand = null;
        if (!string.IsNullOrWhiteSpace(_lastCommandName))
        {
            lastCommand = new JsonObject
            {
                ["id"] = _lastCommandId,
                ["command"] = _lastCommandName,
                ["atUtc"] = _lastCommandAtUtc == DateTime.MinValue ? null : _lastCommandAtUtc.ToString("O"),
                ["success"] = _lastCommandSucceeded,
                ["message"] = _lastCommandMessage
            };
        }

        JsonObject? lastScreenshot = null;
        if (!string.IsNullOrWhiteSpace(_lastScreenshotPath))
        {
            lastScreenshot = new JsonObject
            {
                ["path"] = _lastScreenshotPath,
                ["atUtc"] = _lastScreenshotAtUtc == DateTime.MinValue ? null : _lastScreenshotAtUtc.ToString("O"),
                ["scenario"] = _lastScreenshotScenario,
                ["step"] = _lastScreenshotStep,
                ["label"] = _lastScreenshotLabel
            };
        }

        return new JsonObject
        {
            ["heartbeatAtUtc"] = heartbeatAt?.ToString("O"),
            ["heartbeatAgeMs"] = heartbeatAgeMs,
            ["queueDepth"] = queueDepth,
            ["oldestCommandAgeMs"] = oldestAgeMs,
            ["responseDepth"] = responseDepth,
            ["stuckCommands"] = stuckCommands,
            ["lastCommand"] = lastCommand,
            ["lastScreenshot"] = lastScreenshot,
            ["screenshotIndexPath"] = "Library/XLabMcpBridge/screenshots/index.jsonl",
            ["auditLogPath"] = "Library/XLabMcpBridge/audit.log",
            ["state"] = state,
            ["recommendedAction"] = recommendation,
            ["recommendedRetryAfterMs"] = heartbeatAgeMs is > 5000
                ? 2000
                : queueDepth > 0
                    ? Math.Min(5000, Math.Max(250, oldestAgeMs))
                    : 500
        };
    }

    private static (bool, string) ManageBuild(string raw)
    {
        var action = (JsonArgumentString(raw, "action") ?? "profiles").ToLowerInvariant();
        if (action == "scenes")
        {
            return BuildSettingsScenes(raw);
        }

        if (action != "profiles")
        {
            return (false, $"Unsupported manage_build action: {action}");
        }

        var mode = (JsonArgumentString(raw, "mode") ?? "get_active").ToLowerInvariant();
        var profiles = BuildTargetProfiles();
        var active = EditorUserBuildSettings.activeBuildTarget.ToString();
        var activeGroup = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString();
        var scenes = new JsonArray(EditorBuildSettings.scenes.Select(s => (JsonNode?)s.path).ToArray());

        if (mode == "list" || mode == "get_active")
        {
            return (true, new JsonObject
            {
                ["action"] = "profiles",
                ["mode"] = mode,
                ["profiles"] = profiles,
                ["active_profile"] = active,
                ["active_build_target"] = active,
                ["active_build_target_group"] = activeGroup,
                ["scenes"] = scenes
            }.ToJsonString());
        }

        if (mode == "set_active")
        {
            var profile = JsonArgumentString(raw, "profile")
                          ?? JsonArgumentString(raw, "buildTarget")
                          ?? JsonArgumentString(raw, "target");
            if (string.IsNullOrWhiteSpace(profile))
            {
                return (false, "profile is required");
            }

            if (!Enum.TryParse(profile, true, out BuildTarget target))
            {
                return (false, $"unknown build target: {profile}");
            }

            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
            {
                var switched = EditorUserBuildSettings.activeBuildTarget.ToString();
                return (true, new JsonObject
                {
                    ["action"] = "profiles",
                    ["mode"] = "set_active",
                    ["profile"] = profile,
                    ["active_profile"] = switched,
                    ["active_build_target"] = switched,
                    ["active_build_target_group"] = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString(),
                    ["profiles"] = profiles,
                    ["scenes"] = scenes
                }.ToJsonString());
            }

            return (false, $"failed to switch build target: {profile}");
        }

        return (false, $"Unsupported manage_build mode: {mode}");
    }

    private static JsonArray BuildTargetProfiles()
    {
        var profiles = new JsonArray();
        foreach (BuildTarget target in Enum.GetValues(typeof(BuildTarget)))
        {
            if (target == BuildTarget.NoTarget)
            {
                continue;
            }

            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (group == BuildTargetGroup.Unknown)
            {
                continue;
            }

            profiles.Add(new JsonObject
            {
                ["name"] = target.ToString(),
                ["buildTarget"] = target.ToString(),
                ["buildTargetGroup"] = group.ToString(),
                ["active"] = EditorUserBuildSettings.activeBuildTarget == target
            });
        }

        return profiles;
    }

    private static (bool, string) Screenshot(string raw)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var scenario = JsonString(raw, "scenario") ?? JsonString(raw, "capture_source");
        var step = JsonString(raw, "step");
        var label = JsonString(raw, "label");
        var output = JsonString(raw, "outputPath");
        if (string.IsNullOrWhiteSpace(output))
        {
            var parts = new List<string> { "screen", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") };
            var scenarioTag = SafeScreenshotSegment(scenario, string.Empty);
            var stepTag = SafeScreenshotSegment(step, string.Empty);
            var labelTag = SafeScreenshotSegment(label, string.Empty);
            if (!string.IsNullOrWhiteSpace(scenarioTag)) parts.Add(scenarioTag);
            if (!string.IsNullOrWhiteSpace(stepTag)) parts.Add(stepTag);
            if (!string.IsNullOrWhiteSpace(labelTag)) parts.Add(labelTag);
            output = Path.Combine("Screenshots", string.Join("_", parts) + ".png");
        }

        var abs = Path.IsPathRooted(output) ? output : Path.Combine(projectRoot, output);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        ScreenCapture.CaptureScreenshot(abs);
        RecordScreenshotArtifact(projectRoot, abs, scenario, step, label);
        return (true, new JsonObject
        {
            ["success"] = true,
            ["path"] = Path.GetRelativePath(projectRoot, abs).Replace("\\", "/"),
            ["scenario"] = scenario,
            ["step"] = step,
            ["label"] = label,
            ["indexPath"] = "Library/XLabMcpBridge/screenshots/index.jsonl"
        }.ToJsonString());
    }

    private static void RecordScreenshotArtifact(string projectRoot, string absPath, string? scenario, string? step, string? label)
    {
        var bridgeRoot = Path.Combine(projectRoot, "Library", "XLabMcpBridge");
        var screenshotRoot = Path.Combine(bridgeRoot, "screenshots");
        Directory.CreateDirectory(screenshotRoot);

        var relPath = Path.IsPathRooted(absPath)
            ? Path.GetFullPath(absPath).StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(projectRoot, absPath).Replace("\\", "/")
                : absPath.Replace("\\", "/")
            : absPath.Replace("\\", "/");

        var entry = new JsonObject
        {
            ["atUtc"] = DateTime.UtcNow.ToString("O"),
            ["path"] = relPath,
            ["scenario"] = scenario,
            ["step"] = step,
            ["label"] = label
        };

        _lastScreenshotPath = relPath;
        _lastScreenshotAtUtc = DateTime.UtcNow;
        _lastScreenshotScenario = scenario;
        _lastScreenshotStep = step;
        _lastScreenshotLabel = label;

        File.WriteAllText(Path.Combine(screenshotRoot, "last.json"), entry.ToJsonString(), Encoding.UTF8);

        var indexPath = Path.Combine(screenshotRoot, "index.jsonl");
        var lines = File.Exists(indexPath)
            ? File.ReadAllLines(indexPath).TakeLast(199).ToList()
            : new List<string>();
        lines.Add(entry.ToJsonString());
        File.WriteAllLines(indexPath, lines, Encoding.UTF8);
    }

    private static string? SafeScreenshotSegment(string? value, string fallback)
    {
        var cleaned = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_.-]+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
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
        var job = CreatePlayModeJob("enter");
        var report = StartPlayModeTransition(job, enteringPlayMode: true);
        return report;
    }

    private static (bool, string) PlaymodeExit()
    {
        var job = CreatePlayModeJob("exit");
        var report = StartPlayModeTransition(job, enteringPlayMode: false);
        return report;
    }

    private static JsonObject BuildEditorStateReport()
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var scene = SceneManager.GetActiveScene();
        var openScenes = new JsonArray();
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (s.IsValid())
            {
                openScenes.Add(string.IsNullOrWhiteSpace(s.path) ? "<unsaved>" : s.path);
            }
        }

        var blockingReasons = new List<string>();
        if (EditorApplication.isCompiling)
        {
            blockingReasons.Add("compiling");
        }
        if (EditorApplication.isUpdating)
        {
            blockingReasons.Add("updating");
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            blockingReasons.Add("playmode-transition");
        }

        var report = new JsonObject
        {
            ["projectPath"] = projectRoot,
            ["projectName"] = Path.GetFileName(projectRoot),
            ["unityVersion"] = Application.unityVersion,
            ["isPlaying"] = EditorApplication.isPlaying,
            ["isPaused"] = EditorApplication.isPaused,
            ["isCompiling"] = EditorApplication.isCompiling,
            ["isUpdating"] = EditorApplication.isUpdating,
            ["isDomainReloadPending"] = EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode,
            ["readyForTools"] = blockingReasons.Count == 0,
            ["blockingReasons"] = new JsonArray(blockingReasons.Select(r => (JsonNode?)r).ToArray()),
            ["activeScene"] = string.IsNullOrWhiteSpace(scene.path) ? "<unsaved>" : scene.path,
            ["openScenes"] = openScenes,
            ["selectedBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
            ["qualityLevel"] = QualitySettings.names[QualitySettings.GetQualityLevel()],
            ["bridgeHealth"] = BuildBridgeHealthReport(projectRoot),
            ["recommendedRetryAfterMs"] = blockingReasons.Count == 0 ? 500 : 2000
        };

        var playModeStatus = ReadPlayModeStatus();
        if (playModeStatus != null)
        {
            report["playModeJob"] = playModeStatus.DeepClone();
        }

        return report;
    }

    private static (bool, string) StartPlayModeTransition(JsonObject job, bool enteringPlayMode)
    {
        job["editorState"] = BuildEditorStateReport();
        job["requestedAtUtc"] = DateTime.UtcNow.ToString("O");

        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            job["state"] = "blocked";
            WritePlayModeStatus(job);
            return (true, job.ToJsonString());
        }

        if (enteringPlayMode)
        {
            if (EditorApplication.isPlaying)
            {
                job["state"] = "completed";
                job["completedAtUtc"] = DateTime.UtcNow.ToString("O");
                WritePlayModeStatus(job);
                return (true, job.ToJsonString());
            }

            EditorApplication.EnterPlaymode();
            job["state"] = "running";
        }
        else
        {
            if (!EditorApplication.isPlaying)
            {
                job["state"] = "completed";
                job["completedAtUtc"] = DateTime.UtcNow.ToString("O");
                WritePlayModeStatus(job);
                return (true, job.ToJsonString());
            }

            EditorApplication.ExitPlaymode();
            job["state"] = "running";
        }

        WritePlayModeStatus(job);
        return (true, job.ToJsonString());
    }

    private static JsonObject CreatePlayModeJob(string mode)
    {
        return new JsonObject
        {
            ["jobId"] = Guid.NewGuid().ToString("N"),
            ["tool"] = "manage_editor",
            ["action"] = "play_mode",
            ["mode"] = mode,
            ["state"] = "requested"
        };
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange change)
    {
        try
        {
            var status = ReadPlayModeStatus();
            if (status == null)
            {
                return;
            }

            status["observedPlayModeState"] = change.ToString();
            status["observedAtUtc"] = DateTime.UtcNow.ToString("O");

            var mode = StatusString(status.ToJsonString(), "mode");
            var changeName = change.ToString();
            if (string.Equals(changeName, "EnteredPlayMode", StringComparison.OrdinalIgnoreCase) && string.Equals(mode, "enter", StringComparison.OrdinalIgnoreCase))
            {
                status["state"] = "completed";
                status["completedAtUtc"] = DateTime.UtcNow.ToString("O");
            }
            else if (string.Equals(changeName, "EnteredEditMode", StringComparison.OrdinalIgnoreCase) && string.Equals(mode, "exit", StringComparison.OrdinalIgnoreCase))
            {
                status["state"] = "completed";
                status["completedAtUtc"] = DateTime.UtcNow.ToString("O");
            }
            else if (changeName.StartsWith("Entering", StringComparison.OrdinalIgnoreCase) || changeName.StartsWith("Exiting", StringComparison.OrdinalIgnoreCase))
            {
                status["state"] = "running";
            }

            WritePlayModeStatus(status);
        }
        catch
        {
        }
    }

    private static JsonObject? ReadPlayModeStatus()
    {
        return ReadStatusObject(PlayModeStatusPath());
    }

    private static void WritePlayModeStatus(JsonObject status)
    {
        try
        {
            Directory.CreateDirectory(BridgeRoot());
            File.WriteAllText(PlayModeStatusPath(), status.ToJsonString() + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
        }
    }

    private static string PlayModeStatusPath() => Path.Combine(BridgeRoot(), "playmode-status.json");

    private static void ProcessPendingInputJobs()
    {
        List<PendingInputJob> dueJobs;
        lock (PendingInputJobsGate)
        {
            dueJobs = PendingInputJobs.Where(job => job.ReleaseAtUtc <= DateTime.UtcNow).ToList();
            PendingInputJobs.RemoveAll(job => job.ReleaseAtUtc <= DateTime.UtcNow);
        }

        if (dueJobs.Count == 0)
        {
            return;
        }

        var window = ResolveGameViewWindow();
        if (window == null)
        {
            return;
        }

        foreach (var job in dueJobs)
        {
            if (job.MousePosition.HasValue)
            {
                SendMouseMove(window, job.MousePosition.Value.x, job.MousePosition.Value.y);
            }

            foreach (var key in job.Keys)
            {
                SendKeyEvent(window, key, keyDown: false);
            }

            foreach (var button in job.MouseButtons)
            {
                SendMouseButtonEvent(window, button, keyDown: false, job.MousePosition);
            }
        }
    }

    private static EditorWindow? ResolveGameViewWindow()
    {
        try
        {
            var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType != null)
            {
                return EditorWindow.GetWindow(gameViewType);
            }
        }
        catch
        {
        }

        return EditorWindow.focusedWindow ?? EditorWindow.mouseOverWindow;
    }

    private static void SendKeyEvent(EditorWindow window, KeyCode keyCode, bool keyDown)
    {
        window.SendEvent(new Event
        {
            type = keyDown ? EventType.KeyDown : EventType.KeyUp,
            keyCode = keyCode
        });
    }

    private static void SendMouseMove(EditorWindow window, float x, float y)
    {
        window.SendEvent(new Event
        {
            type = EventType.MouseMove,
            mousePosition = new Vector2(x, y)
        });
    }

    private static void SendMouseButtonEvent(EditorWindow window, int button, bool keyDown, (int x, int y)? position)
    {
        var evt = new Event
        {
            type = keyDown ? EventType.MouseDown : EventType.MouseUp,
            button = button,
            mousePosition = position.HasValue ? new Vector2(position.Value.x, position.Value.y) : Vector2.zero
        };
        window.SendEvent(evt);
    }

    private static bool TryParseKeyCode(string? key, out KeyCode keyCode)
    {
        keyCode = KeyCode.None;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (Enum.TryParse(key, true, out KeyCode parsed))
        {
            keyCode = parsed;
            return true;
        }

        if (key.Length == 1)
        {
            var upper = char.ToUpperInvariant(key[0]);
            if (Enum.TryParse(upper.ToString(), true, out parsed))
            {
                keyCode = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseMouseButton(string? button, out int mouseButton)
    {
        mouseButton = 0;
        if (string.IsNullOrWhiteSpace(button))
        {
            return false;
        }

        return button.Trim().ToLowerInvariant() switch
        {
            "left" => true,
            "leftmouse" => true,
            "left_mouse" => true,
            "0" => true,
            "right" => (mouseButton = 1) >= 0,
            "rightmouse" => (mouseButton = 1) >= 0,
            "right_mouse" => (mouseButton = 1) >= 0,
            "1" => (mouseButton = 1) >= 0,
            "middle" => (mouseButton = 2) >= 0,
            "middlemouse" => (mouseButton = 2) >= 0,
            "middle_mouse" => (mouseButton = 2) >= 0,
            "2" => (mouseButton = 2) >= 0,
            _ => false
        };
    }

    private static List<string> ReadStringArray(JsonElement args, string key)
    {
        var result = new List<string>();
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in node.EnumerateArray())
        {
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static List<string> ReadStringArray(JsonElement args, params string[] keys)
    {
        foreach (var key in keys)
        {
            var values = ReadStringArray(args, key);
            if (values.Count > 0)
            {
                return values;
            }
        }

        return new List<string>();
    }

    private static int? ReadInt(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var node))
        {
            return null;
        }

        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value))
        {
            return value;
        }

        if (node.ValueKind == JsonValueKind.String && int.TryParse(node.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static (int x, int y)? ReadIntPair(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = node.EnumerateArray().Take(2).ToArray();
        if (values.Length < 2)
        {
            return null;
        }

        if (TryReadInt(values[0], out var x) && TryReadInt(values[1], out var y))
        {
            return (x, y);
        }

        return null;
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryReadFloat(JsonElement element, out float value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && float.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0f;
        return false;
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
        return (false, "manage_graph connect requires fromNodeId/sourceNodeId and toNodeId/targetNodeId");
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
        return (false, $"manage_graph connect nodes not found in graph state: from={sourceNodeId}; to={targetNodeId}");
        }

        var sourceUnit = FindUnitByGuid(load.Graph!, srcNode.guid);
        var targetUnit = FindUnitByGuid(load.Graph!, dstNode.guid);
        if (sourceUnit == null || targetUnit == null)
        {
        return (false, $"manage_graph connect unit not found in graph asset: from={sourceNodeId}; to={targetNodeId}");
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
        return (true, $"manage_graph connect: {sourceNodeId}.{sourcePort} -> {targetNodeId}.{targetPort}; kind={kind}; path={graphPath}");
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
        return (false, "manage_graph edit add_node requires nodeId and unitType");
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
        return (true, $"manage_graph edit add_node: nodeId={nodeId}; unitType={unitType}; path={graphPath}");
        }

        if (op == "remove_node")
        {
            var nodeId = FirstNonEmpty(JsonArgumentString(raw, "nodeId"), JsonArgumentString(raw, "id"), JsonArgumentString(raw, "name"));
            if (string.IsNullOrWhiteSpace(nodeId))
            {
        return (false, "manage_graph edit remove_node requires nodeId");
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
        return (true, $"manage_graph edit remove_node: nodeId={nodeId}; path={graphPath}");
        }

        if (op == "set_node")
        {
            var nodeId = FirstNonEmpty(JsonArgumentString(raw, "nodeId"), JsonArgumentString(raw, "id"), JsonArgumentString(raw, "name"));
            if (string.IsNullOrWhiteSpace(nodeId))
            {
        return (false, "manage_graph edit set_node requires nodeId");
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
        return (true, $"manage_graph edit set_node: nodeId={nodeId}; x={x}; y={y}; path={graphPath}");
        }

        return (false, $"Unsupported manage_graph edit operation: {op}. Supported: add_node|remove_node|set_node");
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
        return (true, $"manage_graph validate: exists=false; path={graphPath}; reason={load.Message}");
        }

        var state = LoadGraphState(graphPath);
        var unitCount = CountCollectionItems(GetMemberValue(load.Graph!, "units"));
        var controlCount = CountCollectionItems(GetMemberValue(load.Graph!, "controlConnections"));
        var valueCount = CountCollectionItems(GetMemberValue(load.Graph!, "valueConnections"));
        var stateLinks = state.links.Count;
        var stateNodes = state.nodes.Count;
        return (true, $"manage_graph validate: exists=true; assetType={load.Asset!.GetType().FullName}; units={unitCount}; controlConnections={controlCount}; valueConnections={valueCount}; stateNodes={stateNodes}; stateLinks={stateLinks}; path={graphPath}");
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
        return (true, $"manage_ui create_or_edit: {normalized}");
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
        return (true, $"manage_localization key_add: key={key}; path={path}");
    }

    private static (bool, string) LocalizationTables(string raw)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var localizationRoot = Path.Combine(projectRoot, "Assets", "Localization");
        var tables = new JsonArray();
        var localeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryCounts = new JsonObject();
        var missingLocaleCounts = new JsonObject();

        if (!Directory.Exists(localizationRoot))
        {
            return (true, new JsonObject
            {
                ["resource"] = "xlabmcp://localization/tables",
                ["tables"] = tables,
                ["locales"] = new JsonArray(),
                ["entryCounts"] = entryCounts,
                ["missingLocaleCounts"] = missingLocaleCounts,
                ["sourceRoot"] = localizationRoot
            }.ToJsonString());
        }

        var csvFiles = Directory.GetFiles(localizationRoot, "*.csv", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var csv in csvFiles)
        {
            try
            {
                var table = BuildLocalizationTableSnapshot(csv, localeSet, out var entryCount, out var localeMissingCounts);
                tables.Add(table);
                var tableName = table["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(csv);
                entryCounts[tableName] = entryCount;
                missingLocaleCounts[tableName] = localeMissingCounts;
            }
            catch (Exception ex)
            {
                tables.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(csv),
                    ["path"] = csv.Replace(projectRoot + Path.DirectorySeparatorChar, string.Empty).Replace("\\", "/"),
                    ["error"] = ex.Message
                });
            }
        }

        var locales = new JsonArray(localeSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode?)x).ToArray());
        var report = new JsonObject
        {
            ["resource"] = "xlabmcp://localization/tables",
            ["tables"] = tables,
            ["locales"] = locales,
            ["entryCounts"] = entryCounts,
            ["missingLocaleCounts"] = missingLocaleCounts,
            ["sourceRoot"] = localizationRoot
        };
        return (true, report.ToJsonString());
    }

    private static (bool, string) LocalizationKeyList(string raw)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var scopeRoot = ResolveLocalizationScopeRoot(projectRoot, raw);
        var tableFilter = JsonArgumentString(raw, "table") ?? JsonArgumentString(raw, "tableName");
        var csvFiles = CollectLocalizationCsvFiles(scopeRoot, tableFilter);

        var report = new JsonObject
        {
            ["resource"] = "xlabmcp://localization/tables",
            ["table"] = tableFilter ?? string.Empty,
            ["tables"] = new JsonArray(),
            ["locales"] = new JsonArray(),
            ["keys"] = new JsonArray(),
            ["entriesByLocale"] = new JsonObject(),
            ["sourceRoot"] = scopeRoot
        };

        var localeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entriesByLocale = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var matchedTables = (JsonArray)report["tables"]!;

        foreach (var csv in csvFiles)
        {
            try
            {
                var table = ReadLocalizationCsvTable(csv, projectRoot);
                matchedTables.Add(new JsonObject
                {
                    ["name"] = table.Name,
                    ["path"] = table.RelativePath,
                    ["entryCount"] = table.Keys.Count,
                    ["locales"] = new JsonArray(table.Locales.Select(x => (JsonNode?)x).ToArray())
                });

                foreach (var locale in table.Locales)
                {
                    localeSet.Add(locale);
                    if (!entriesByLocale.TryGetValue(locale, out var localeKeys))
                    {
                        localeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        entriesByLocale[locale] = localeKeys;
                    }
                    foreach (var key in table.Keys)
                    {
                        localeKeys.Add(key);
                    }
                }

                foreach (var key in table.Keys)
                {
                    keySet.Add(key);
                }
            }
            catch (Exception ex)
            {
                matchedTables.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(csv),
                    ["path"] = RelativeProjectPath(projectRoot, csv),
                    ["error"] = ex.Message
                });
            }
        }

        report["locales"] = new JsonArray(localeSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode?)x).ToArray());
        report["keys"] = new JsonArray(keySet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode?)x).ToArray());

        var entries = new JsonObject();
        foreach (var kv in entriesByLocale.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            entries[kv.Key] = new JsonArray(kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => (JsonNode?)x).ToArray());
        }
        report["entriesByLocale"] = entries;
        return (true, report.ToJsonString());
    }

    private static (bool, string) LocalizationKeyResolve(string raw)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var scopeRoot = ResolveLocalizationScopeRoot(projectRoot, raw);
        var tableFilter = JsonArgumentString(raw, "table") ?? JsonArgumentString(raw, "tableName");
        var locale = JsonArgumentString(raw, "locale") ?? "default";
        var keys = JsonArgumentStringArray(raw, "keys");

        if (keys.Length == 0)
        {
            return (false, "keys is required");
        }

        var csvFiles = CollectLocalizationCsvFiles(scopeRoot, tableFilter);
        var selected = SelectLocalizationTable(csvFiles, tableFilter);
        if (selected is null)
        {
            return (false, tableFilter is null ? $"no localization table found under {scopeRoot}" : $"localization table not found: {tableFilter}");
        }

        var table = ReadLocalizationCsvTable(selected, projectRoot);
        if (!table.Locales.Any(l => string.Equals(l, locale, StringComparison.OrdinalIgnoreCase)))
        {
            return (true, new JsonObject
            {
                ["table"] = table.Name,
                ["locale"] = locale,
                ["resolved"] = new JsonArray(),
                ["missing"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()),
                ["empty"] = new JsonArray(),
                ["source"] = table.RelativePath
            }.ToJsonString());
        }

        var resolved = new JsonArray();
        var missing = new JsonArray();
        var empty = new JsonArray();

        foreach (var key in keys)
        {
            if (!table.EntriesByLocale.TryGetValue(locale, out var localeEntries) || !localeEntries.TryGetValue(key, out var value))
            {
                missing.Add(key);
                continue;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                empty.Add(key);
                continue;
            }

            resolved.Add(new JsonObject
            {
                ["key"] = key,
                ["value"] = value,
                ["locale"] = locale,
                ["table"] = table.Name,
                ["path"] = table.RelativePath
            });
        }

        var report = new JsonObject
        {
            ["table"] = table.Name,
            ["locale"] = locale,
            ["resolved"] = resolved,
            ["missing"] = missing,
            ["empty"] = empty,
            ["source"] = table.RelativePath
        };
        return (true, report.ToJsonString());
    }

    private static (bool, string) LocalizationValidateAssets(string raw)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var scopeRoot = ResolveLocalizationScopeRoot(projectRoot, raw);
        var tableFilter = JsonArgumentString(raw, "table") ?? JsonArgumentString(raw, "tableName");
        var csvFiles = CollectLocalizationCsvFiles(scopeRoot, tableFilter);
        var findings = new JsonArray();
        var tables = new JsonArray();
        var errors = 0;
        var warnings = 0;
        var totalEntries = 0;

        foreach (var csv in csvFiles)
        {
            try
            {
                var table = ReadLocalizationCsvTable(csv, projectRoot);
                var tableFindings = ValidateLocalizationCsvTable(csv, table, out var tableErrors, out var tableWarnings);
                errors += tableErrors;
                warnings += tableWarnings;
                totalEntries += table.Keys.Count;

                tables.Add(new JsonObject
                {
                    ["name"] = table.Name,
                    ["path"] = table.RelativePath,
                    ["entryCount"] = table.Keys.Count,
                    ["locales"] = new JsonArray(table.Locales.Select(x => (JsonNode?)x).ToArray()),
                    ["missingCounts"] = BuildCountObject(table.MissingCounts),
                    ["findings"] = tableFindings
                });
                foreach (var finding in tableFindings)
                {
                    findings.Add(finding);
                }
            }
            catch (Exception ex)
            {
                errors++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "error",
                    ["issue"] = "localization_table_unreadable",
                    ["path"] = RelativeProjectPath(projectRoot, csv),
                    ["message"] = ex.Message
                });
                tables.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(csv),
                    ["path"] = RelativeProjectPath(projectRoot, csv),
                    ["error"] = ex.Message
                });
            }
        }

        var report = new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_localization",
            ["action"] = "validate_assets",
            ["scopeRoot"] = scopeRoot,
            ["tableFilter"] = tableFilter,
            ["summary"] = new JsonObject
            {
                ["tables"] = tables.Count,
                ["entries"] = totalEntries,
                ["errors"] = errors,
                ["warnings"] = warnings
            },
            ["tables"] = tables,
            ["findings"] = findings
        };
        report["recommendation"] = errors > 0
            ? "Fix localization asset errors before release."
            : warnings > 0
                ? "Review warnings and fill missing rows where needed."
                : "Localization assets look healthy.";

        return (true, report.ToJsonString());
    }

    private static (bool, string) LocalizationValidateKeyCoverage(string raw)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var scopeRoot = ResolveLocalizationScopeRoot(projectRoot, raw);
        var tableFilter = JsonArgumentString(raw, "table") ?? JsonArgumentString(raw, "tableName");
        var csvFiles = CollectLocalizationCsvFiles(scopeRoot, tableFilter);
        var findings = new JsonArray();
        var tables = new JsonArray();
        var missingEntries = 0;
        var totalKeys = 0;
        var locales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var csv in csvFiles)
        {
            try
            {
                var table = ReadLocalizationCsvTable(csv, projectRoot);
                var coverage = new JsonArray();
                foreach (var locale in table.Locales)
                {
                    locales.Add(locale);
                    table.EntriesByLocale.TryGetValue(locale, out var localeEntries);
                    localeEntries ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var missingKeys = table.Keys
                        .Where(key => !localeEntries.ContainsKey(key))
                        .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                        .Take(50)
                        .ToList();

                    if (missingKeys.Count > 0)
                    {
                        missingEntries += missingKeys.Count;
                        findings.Add(new JsonObject
                        {
                            ["severity"] = "warning",
                            ["issue"] = "missing_localization_keys",
                            ["path"] = table.RelativePath,
                            ["locale"] = locale,
                            ["missingCount"] = missingKeys.Count,
                            ["missingKeys"] = new JsonArray(missingKeys.Select(k => (JsonNode?)k).ToArray()),
                            ["message"] = "Locale is missing keys present in the table"
                        });
                    }

                    coverage.Add(new JsonObject
                    {
                        ["locale"] = locale,
                        ["missingCount"] = missingKeys.Count,
                        ["missingKeys"] = new JsonArray(missingKeys.Select(k => (JsonNode?)k).ToArray())
                    });
                }

                totalKeys += table.Keys.Count;
                tables.Add(new JsonObject
                {
                    ["name"] = table.Name,
                    ["path"] = table.RelativePath,
                    ["entryCount"] = table.Keys.Count,
                    ["coverage"] = coverage
                });
            }
            catch (Exception ex)
            {
                findings.Add(new JsonObject
                {
                    ["severity"] = "error",
                    ["issue"] = "localization_table_unreadable",
                    ["path"] = RelativeProjectPath(projectRoot, csv),
                    ["message"] = ex.Message
                });
            }
        }

        var report = new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_localization",
            ["action"] = "validate_key_coverage",
            ["scopeRoot"] = scopeRoot,
            ["tableFilter"] = tableFilter,
            ["summary"] = new JsonObject
            {
                ["tables"] = tables.Count,
                ["locales"] = locales.Count,
                ["keys"] = totalKeys,
                ["missingEntries"] = missingEntries
            },
            ["tables"] = tables,
            ["findings"] = findings
        };
        report["coverage"] = missingEntries == 0 ? "complete" : "partial";
        report["recommendation"] = missingEntries > 0
            ? "Fill missing localized values for every locale."
            : "Key coverage is complete for the selected scope.";

        return (true, report.ToJsonString());
    }

    private static (bool, string) LocalizationValidateFallbackLanguage(string raw)
    {
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var scopeRoot = ResolveLocalizationScopeRoot(projectRoot, raw);
        var tableFilter = JsonArgumentString(raw, "table") ?? JsonArgumentString(raw, "tableName");
        var csvFiles = CollectLocalizationCsvFiles(scopeRoot, tableFilter);
        var findings = new JsonArray();
        var tables = new JsonArray();
        var missingFallback = 0;
        var fallbackLocale = (JsonArgumentString(raw, "fallbackLocale") ?? JsonArgumentString(raw, "locale") ?? "default").Trim();

        foreach (var csv in csvFiles)
        {
            try
            {
                var table = ReadLocalizationCsvTable(csv, projectRoot);
                var hasFallback = table.Locales.Any(l => string.Equals(l, fallbackLocale, StringComparison.OrdinalIgnoreCase));
                var missingCount = table.MissingCounts.TryGetValue(fallbackLocale, out var fallbackMissing) ? fallbackMissing : table.Keys.Count;
                if (!hasFallback || missingCount > 0)
                {
                    missingFallback++;
                    findings.Add(new JsonObject
                    {
                        ["severity"] = hasFallback ? "warning" : "error",
                        ["issue"] = hasFallback ? "missing_fallback_values" : "missing_fallback_locale",
                        ["path"] = table.RelativePath,
                        ["fallbackLocale"] = fallbackLocale,
                        ["missingCount"] = missingCount,
                        ["message"] = hasFallback ? "Fallback locale contains gaps" : "Fallback locale column is missing"
                    });
                }

                tables.Add(new JsonObject
                {
                    ["name"] = table.Name,
                    ["path"] = table.RelativePath,
                    ["fallbackLocale"] = fallbackLocale,
                    ["hasFallbackLocale"] = hasFallback,
                    ["missingCount"] = missingCount
                });
            }
            catch (Exception ex)
            {
                findings.Add(new JsonObject
                {
                    ["severity"] = "error",
                    ["issue"] = "localization_table_unreadable",
                    ["path"] = RelativeProjectPath(projectRoot, csv),
                    ["message"] = ex.Message
                });
            }
        }

        var report = new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_localization",
            ["action"] = "validate_fallback_language",
            ["scopeRoot"] = scopeRoot,
            ["tableFilter"] = tableFilter,
            ["fallbackLocale"] = fallbackLocale,
            ["summary"] = new JsonObject
            {
                ["tables"] = tables.Count,
                ["tablesWithFallbackProblems"] = missingFallback
            },
            ["tables"] = tables,
            ["findings"] = findings
        };
        report["recommendation"] = missingFallback > 0
            ? "Populate the fallback locale for every localization table."
            : "Fallback language coverage looks healthy.";

        return (true, report.ToJsonString());
    }

    private static JsonObject BuildLocalizationTableSnapshot(string csvPath, HashSet<string> localeSet, out int entryCount, out JsonObject localeMissingCounts)
    {
        var table = ReadLocalizationCsvTable(csvPath, Path.GetFullPath(Path.Combine(Application.dataPath, "..")));
        entryCount = table.Keys.Count;
        localeMissingCounts = new JsonObject();
        foreach (var locale in table.Locales)
        {
            localeSet.Add(locale);
            localeMissingCounts[locale] = table.MissingCounts.TryGetValue(locale, out var missingCount) ? missingCount : 0;
        }

        return new JsonObject
        {
            ["name"] = table.Name,
            ["path"] = table.RelativePath,
            ["entryCount"] = entryCount,
            ["locales"] = new JsonArray(table.Locales.Select(l => (JsonNode?)l).ToArray()),
            ["missingLocaleCounts"] = localeMissingCounts
        };
    }

    private static LocalizationTableData ReadLocalizationCsvTable(string csvPath, string projectRoot)
    {
        var normalizedPath = RelativeProjectPath(projectRoot, csvPath);
        var lines = File.ReadAllLines(csvPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        var tableName = Path.GetFileNameWithoutExtension(csvPath);
        var locales = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entriesByLocale = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var missingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (lines.Count > 0)
        {
            var header = SplitCsvLine(lines[0]);
            if (header.Length >= 2)
            {
                for (var i = 1; i < header.Length; i++)
                {
                    var locale = NormalizeLocaleHeader(header[i]);
                    if (!string.IsNullOrWhiteSpace(locale))
                    {
                        locales.Add(locale);
                        missingCounts[locale] = 0;
                        entriesByLocale[locale] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                }
            }

            foreach (var line in lines.Skip(1))
            {
                var row = SplitCsvLine(line);
                if (row.Length == 0)
                {
                    continue;
                }

                var key = row[0].Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                keys.Add(key);

                if (locales.Count == 0)
                {
                    locales.Add("default");
                    missingCounts["default"] = 0;
                    entriesByLocale["default"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                for (var i = 0; i < locales.Count; i++)
                {
                    var locale = locales[i];
                    var columnIndex = i + 1;
                    var value = row.Length > columnIndex ? row[columnIndex].Trim() : string.Empty;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        missingCounts[locale] = missingCounts.TryGetValue(locale, out var currentMissing) ? currentMissing + 1 : 1;
                        continue;
                    }

                    entriesByLocale[locale][key] = value;
                }
            }
        }

        if (locales.Count == 0)
        {
            locales.Add("default");
            missingCounts["default"] = 0;
            entriesByLocale["default"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return new LocalizationTableData(tableName, normalizedPath, locales, keys, entriesByLocale, missingCounts);
    }

    private static List<string> CollectLocalizationCsvFiles(string scopeRoot, string? tableFilter)
    {
        if (File.Exists(scopeRoot) && scopeRoot.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return MatchesLocalizationTable(scopeRoot, tableFilter) ? new List<string> { scopeRoot } : new List<string>();
        }

        if (!Directory.Exists(scopeRoot))
        {
            return new List<string>();
        }

        var files = Directory.GetFiles(scopeRoot, "*.csv", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(p => MatchesLocalizationTable(p, tableFilter))
            .ToList();
        return files;
    }

    private static string? SelectLocalizationTable(IEnumerable<string> csvFiles, string? tableFilter)
    {
        if (!string.IsNullOrWhiteSpace(tableFilter))
        {
            return csvFiles.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), tableFilter, StringComparison.OrdinalIgnoreCase));
        }

        return csvFiles.FirstOrDefault();
    }

    private static bool MatchesLocalizationTable(string csvPath, string? tableFilter)
    {
        if (string.IsNullOrWhiteSpace(tableFilter))
        {
            return true;
        }

        var tableName = Path.GetFileNameWithoutExtension(csvPath);
        if (string.Equals(tableName, tableFilter, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var relative = RelativeProjectPath(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), csvPath);
        return relative.IndexOf($"/{tableFilter}/", StringComparison.OrdinalIgnoreCase) >= 0
               || relative.EndsWith($"/{tableFilter}.csv", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLocalizationScopeRoot(string projectRoot, string raw)
    {
        var rel = JsonArgumentString(raw, "path") ?? JsonArgumentString(raw, "folderPath") ?? "Assets/Localization";
        rel = rel.Replace("\\", "/");
        if (Path.IsPathRooted(rel))
        {
            return rel;
        }

        return Path.GetFullPath(Path.Combine(projectRoot, rel));
    }

    private static string RelativeProjectPath(string projectRoot, string fullPath)
    {
        return fullPath.Replace(projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, string.Empty).Replace("\\", "/");
    }

    private sealed record LocalizationTableData(
        string Name,
        string RelativePath,
        List<string> Locales,
        HashSet<string> Keys,
        Dictionary<string, Dictionary<string, string>> EntriesByLocale,
        Dictionary<string, int> MissingCounts);

    private static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        cells.Add(current.ToString().Trim());
        return cells.ToArray();
    }

    private static string NormalizeLocaleHeader(string header)
    {
        var value = header.Trim();
        if (string.Equals(value, "value", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        if (string.Equals(value, "defaultValue", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        return value;
    }

    private static JsonObject BuildCountObject(Dictionary<string, int> counts)
    {
        var obj = new JsonObject();
        foreach (var kv in counts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            obj[kv.Key] = kv.Value;
        }

        return obj;
    }

    private static JsonArray ValidateLocalizationCsvTable(string csvPath, LocalizationTableData table, out int errors, out int warnings)
    {
        var findings = new JsonArray();
        errors = 0;
        warnings = 0;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(csvPath);
        }
        catch (Exception ex)
        {
            errors++;
            findings.Add(new JsonObject
            {
                ["severity"] = "error",
                ["issue"] = "localization_table_unreadable",
                ["path"] = table.RelativePath,
                ["message"] = ex.Message
            });
            return findings;
        }

        if (lines.Length == 0)
        {
            errors++;
            findings.Add(new JsonObject
            {
                ["severity"] = "error",
                ["issue"] = "empty_localization_table",
                ["path"] = table.RelativePath,
                ["message"] = "Localization table is empty"
            });
            return findings;
        }

        var header = SplitCsvLine(lines[0]);
        if (header.Length < 2)
        {
            warnings++;
            findings.Add(new JsonObject
            {
                ["severity"] = "warning",
                ["issue"] = "missing_locale_columns",
                ["path"] = table.RelativePath,
                ["message"] = "Table header does not define locale columns"
            });
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = SplitCsvLine(line);
            var key = row.Length > 0 ? row[0].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                errors++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "error",
                    ["issue"] = "blank_localization_key",
                    ["path"] = table.RelativePath,
                    ["message"] = "Localization row is missing a key"
                });
                continue;
            }

            if (!seenKeys.Add(key))
            {
                warnings++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "warning",
                    ["issue"] = "duplicate_localization_key",
                    ["path"] = table.RelativePath,
                    ["key"] = key,
                    ["message"] = "Duplicate localization key found"
                });
            }

            if (row.Length < header.Length)
            {
                warnings++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "warning",
                    ["issue"] = "truncated_localization_row",
                    ["path"] = table.RelativePath,
                    ["key"] = key,
                    ["missingColumns"] = header.Length - row.Length,
                    ["message"] = "Row has fewer columns than the header"
                });
            }
        }

        if (table.Keys.Count == 0)
        {
            warnings++;
            findings.Add(new JsonObject
            {
                ["severity"] = "warning",
                ["issue"] = "empty_localization_keys",
                ["path"] = table.RelativePath,
                ["message"] = "Localization table has no keys"
            });
        }

        return findings;
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
        return (true, $"manage_scriptableobject create_or_edit: {relativePath}");
    }

    private static (bool, string) ScriptableObjectValidateSchema(string raw)
    {
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        var path = ResolveScriptableObjectSourcePath(root, raw);
        if (path == null)
        {
            return (false, "path or name is required");
        }

        if (!File.Exists(path))
        {
            return (false, $"ScriptableObject source not found: {RelativeProjectPath(root, path)}");
        }

        var text = File.ReadAllText(path);
        var findings = new JsonArray();
        var errors = 0;
        var warnings = 0;

        if (!Regex.IsMatch(text, @"\b:\s*ScriptableObject\b"))
        {
            errors++;
            findings.Add(new JsonObject
            {
                ["severity"] = "error",
                ["issue"] = "missing_scriptableobject_base",
                ["path"] = RelativeProjectPath(root, path),
                ["message"] = "Class must inherit from ScriptableObject"
            });
        }

        if (!text.Contains("[CreateAssetMenu", StringComparison.Ordinal))
        {
            warnings++;
            findings.Add(new JsonObject
            {
                ["severity"] = "warning",
                ["issue"] = "missing_create_asset_menu",
                ["path"] = RelativeProjectPath(root, path),
                ["message"] = "CreateAssetMenu attribute is recommended for authoring"
            });
        }

        var classMatch = Regex.Match(text, @"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b");
        if (!classMatch.Success)
        {
            errors++;
            findings.Add(new JsonObject
            {
                ["severity"] = "error",
                ["issue"] = "missing_class_declaration",
                ["path"] = RelativeProjectPath(root, path),
                ["message"] = "No class declaration found"
            });
        }
        else
        {
            var className = classMatch.Groups["name"].Value;
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(className, fileName, StringComparison.Ordinal))
            {
                warnings++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "warning",
                    ["issue"] = "class_name_mismatch",
                    ["path"] = RelativeProjectPath(root, path),
                    ["className"] = className,
                    ["fileName"] = fileName,
                    ["message"] = "Class name does not match file name"
                });
            }
        }

        var report = new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_scriptableobject",
            ["action"] = "validate_schema",
            ["path"] = RelativeProjectPath(root, path),
            ["valid"] = errors == 0,
            ["summary"] = new JsonObject
            {
                ["errors"] = errors,
                ["warnings"] = warnings
            },
            ["findings"] = findings
        };

        report["recommendation"] = errors > 0
            ? "Fix schema errors before using this ScriptableObject in content workflows."
            : warnings > 0
                ? "Review warnings for consistency, then proceed."
                : "Schema looks healthy.";

        return (true, report.ToJsonString());
    }

    private static string? ResolveScriptableObjectSourcePath(string root, string raw)
    {
        var relPath = JsonArgumentString(raw, "path") ?? JsonArgumentString(raw, "scriptPath");
        if (!string.IsNullOrWhiteSpace(relPath))
        {
            relPath = relPath.Replace("\\", "/");
            if (!relPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return null;
            }

            return Path.Combine(root, relPath);
        }

        var name = JsonArgumentString(raw, "name") ?? JsonArgumentString(raw, "scriptName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var safeName = Regex.Replace(name, @"[^A-Za-z0-9_]", "_");
        var candidates = new[]
        {
            Path.Combine(root, "Assets", "Scripts", $"{safeName}.cs"),
            Path.Combine(root, "Assets", $"{safeName}.cs")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static (bool, string) ScriptCreateOrEdit(string raw)
    {
        var name = JsonArgumentString(raw, "name") ?? JsonArgumentString(raw, "scriptName") ?? "NewBehaviour";
        var folder = JsonArgumentString(raw, "folder") ?? "Assets/Scripts";
        var namespaceName = JsonArgumentString(raw, "namespace") ?? string.Empty;
        var baseClass = JsonArgumentString(raw, "baseClass") ?? "MonoBehaviour";
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
            : BuildScriptTemplate(safeName, namespaceName, baseClass);

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
        return (true, $"manage_script create_or_edit: {relativePath}");
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
        var report = BuildValidationReport(scenePath, "scene", text);
        return (true, report.ToJsonString());
    }

    private static (bool, string) PrefabValidate(string raw)
    {
        var prefabPath = JsonArgumentString(raw, "prefabPath");
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            prefabPath = JsonArgumentString(raw, "path");
        }
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return (false, "prefabPath is required");
        }
        prefabPath = prefabPath!;
        prefabPath = prefabPath.Replace("\\", "/");
        if (!prefabPath.StartsWith("Assets/", StringComparison.Ordinal))
        {
            return (false, "prefabPath must be under Assets/");
        }

        var full = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), prefabPath);
        if (!File.Exists(full))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return prefab == null
                ? (false, $"prefab not found: {prefabPath}")
                : (true, new JsonObject
                {
                    ["success"] = true,
                    ["summary"] = new JsonObject
                    {
                        ["missing_scripts"] = 0,
                        ["missing_object_references"] = 0,
                        ["broken_prefab_links"] = 0
                    },
                    ["findings"] = new JsonArray(),
                    ["prefabPath"] = prefabPath,
                    ["note"] = "prefab asset loaded but text serialization file was not found"
                }.ToJsonString());
        }

        var text = File.ReadAllText(full);
        var report = BuildValidationReport(prefabPath, "prefab", text);
        return (true, report.ToJsonString());
    }

    private static JsonObject BuildValidationReport(string assetPath, string kind, string text)
    {
        var findings = new JsonArray();
        var missingScripts = 0;
        var missingObjectRefs = 0;
        var brokenPrefabLinks = 0;

        using var reader = new StringReader(text);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            var trimmed = line.TrimStart();

            if (trimmed.Contains("m_Script: {fileID: 0", StringComparison.OrdinalIgnoreCase))
            {
                missingScripts++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "error",
                    ["issue"] = "missing_script",
                    ["path"] = assetPath,
                    ["line"] = lineNumber,
                    ["snippet"] = trimmed
                });
                continue;
            }

            if (trimmed.Contains("m_CorrespondingSourceObject: {fileID: 0", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("m_PrefabInstance: {fileID: 0", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("m_PrefabAsset: {fileID: 0", StringComparison.OrdinalIgnoreCase))
            {
                brokenPrefabLinks++;
                findings.Add(new JsonObject
                {
                    ["severity"] = "error",
                    ["issue"] = "broken_prefab_link",
                    ["path"] = assetPath,
                    ["line"] = lineNumber,
                    ["snippet"] = trimmed
                });
                continue;
            }

            var objectRefMatch = Regex.Match(trimmed, @"^(?<name>[A-Za-z0-9_]+):\s*\{fileID:\s*0(?:,\s*guid:\s*0,\s*type:\s*0)?\}");
            if (objectRefMatch.Success)
            {
                var name = objectRefMatch.Groups["name"].Value;
                if (!string.Equals(name, "m_Script", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "m_Father", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "m_TransformParent", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "m_CorrespondingSourceObject", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "m_PrefabInstance", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "m_PrefabAsset", StringComparison.OrdinalIgnoreCase))
                {
                    missingObjectRefs++;
                    findings.Add(new JsonObject
                    {
                        ["severity"] = "warning",
                        ["issue"] = "missing_object_reference",
                        ["path"] = assetPath,
                        ["line"] = lineNumber,
                        ["field"] = name,
                        ["snippet"] = trimmed
                    });
                }
            }
        }

        return new JsonObject
        {
            ["success"] = true,
            ["kind"] = kind,
            ["path"] = assetPath,
            ["summary"] = new JsonObject
            {
                ["missing_scripts"] = missingScripts,
                ["missing_object_references"] = missingObjectRefs,
                ["broken_prefab_links"] = brokenPrefabLinks
            },
            ["findings"] = findings
        };
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
            return (true, "manage_asset list_modified: []");
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

        return (true, "manage_asset list_modified:\n" + string.Join("\n", files));
    }

    private static (bool, string) ChangeSummary(string raw)
    {
        var listed = AssetListModified(raw).Item2;
        var entries = ParseListedAssets(listed);
        var report = new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_asset",
            ["action"] = "change_summary",
            ["summary"] = new JsonObject
            {
                ["modifiedAssets"] = entries.Count
            },
            ["changes"] = new JsonArray(entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(e => new JsonObject
            {
                ["path"] = e.Path,
                ["status"] = e.Status
            }).ToArray())
        };
        report["source"] = "list_modified";
        return (true, report.ToJsonString());
    }

    private static (bool, string) RiskClassify(string raw)
    {
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        if (!Directory.Exists(root))
        {
            return (false, "project root not found");
        }

        var gitChanges = ReadGitChanges(root);
        var entries = gitChanges.Count > 0
            ? gitChanges
            : ParseListedAssets(AssetListModified(raw).Item2);

        var report = BuildRiskReport(root, entries, gitChanges.Count > 0 ? "git status --short" : "list_modified fallback");
        return (true, report.ToJsonString());
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
        return (true, $"manage_asset docs_update: {normalized}");
    }

    private static List<RiskFileEntry> ReadGitChanges(string root)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("git", "status --short --untracked-files=all")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                return new List<RiskFileEntry>();
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Debug.LogWarning($"MCP git status failed: {stderr.Trim()}");
                }
                return new List<RiskFileEntry>();
            }

            return ParseGitStatus(stdout);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"MCP git status unavailable: {ex.Message}");
            return new List<RiskFileEntry>();
        }
    }

    private static List<RiskFileEntry> ParseGitStatus(string output)
    {
        var entries = new List<RiskFileEntry>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3)
            {
                continue;
            }

            var status = line.Substring(0, 2).Trim();
            var path = line.Substring(3).Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var renameSep = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (renameSep >= 0)
            {
                path = path.Substring(renameSep + 4);
            }

            entries.Add(new RiskFileEntry
            {
                Path = path.Replace("\\", "/"),
                Status = status
            });
        }

        return entries;
    }

    private static List<RiskFileEntry> ParseListedAssets(string output)
    {
        var entries = new List<RiskFileEntry>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1))
        {
            var path = line.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            entries.Add(new RiskFileEntry
            {
                Path = path.Replace("\\", "/"),
                Status = "modified"
            });
        }

        return entries;
    }

    private static JsonObject BuildRiskReport(string root, List<RiskFileEntry> entries, string source)
    {
        var findings = new JsonArray();
        var high = 0;
        var medium = 0;
        var low = 0;

        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var (risk, reason, category) = ClassifyRiskEntry(entry.Path);
            switch (risk)
            {
                case "high":
                    high++;
                    break;
                case "medium":
                    medium++;
                    break;
                default:
                    low++;
                    break;
            }

            findings.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["status"] = entry.Status,
                ["risk"] = risk,
                ["category"] = category,
                ["reason"] = reason
            });
        }

        var overall = high > 0 ? "high" : medium > 0 ? "medium" : "low";
        var recommendation = overall switch
        {
            "high" => "Review before merging. Runtime code, scenes, prefabs, or project settings changed.",
            "medium" => "Review the data and content impact before merging.",
            _ => "Low-risk change set. Review for documentation or small content edits only."
        };

        return new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_asset",
            ["action"] = "classify_risk",
            ["projectRoot"] = root,
            ["source"] = source,
            ["overallRisk"] = overall,
            ["summary"] = new JsonObject
            {
                ["total"] = entries.Count,
                ["high"] = high,
                ["medium"] = medium,
                ["low"] = low
            },
            ["recommendation"] = recommendation,
            ["changes"] = findings
        };
    }

    private static (string Risk, string Reason, string Category) ClassifyRiskEntry(string path)
    {
        var normalized = path.Replace("\\", "/").TrimStart('/');
        var lower = normalized.ToLowerInvariant();

        if (lower.StartsWith("docs/", StringComparison.Ordinal) || lower.StartsWith("mcp/", StringComparison.Ordinal))
        {
            return ("low", "documentation or workflow asset", "docs");
        }

        if (lower.EndsWith(".md", StringComparison.Ordinal) || lower.EndsWith(".txt", StringComparison.Ordinal) || lower.EndsWith(".rst", StringComparison.Ordinal))
        {
            return ("low", "documentation or note file", "docs");
        }

        if (lower.StartsWith("projectsettings/", StringComparison.Ordinal)
            || lower.StartsWith("packages/manifest.json", StringComparison.Ordinal)
            || lower.StartsWith("packages/packages-lock.json", StringComparison.Ordinal))
        {
            return ("high", "project configuration or package manifest", "project");
        }

        if (lower.IndexOf("/editor/", StringComparison.Ordinal) >= 0 || lower.StartsWith("assets/editor/", StringComparison.Ordinal))
        {
            if (lower.EndsWith(".cs", StringComparison.Ordinal) || lower.EndsWith(".asmdef", StringComparison.Ordinal))
            {
                return ("high", "editor runtime code", "code");
            }
        }

        if (lower.EndsWith(".cs", StringComparison.Ordinal)
            || lower.EndsWith(".asmdef", StringComparison.Ordinal)
            || lower.EndsWith(".unity", StringComparison.Ordinal)
            || lower.EndsWith(".prefab", StringComparison.Ordinal)
            || lower.EndsWith(".anim", StringComparison.Ordinal)
            || lower.EndsWith(".controller", StringComparison.Ordinal)
            || lower.EndsWith(".overridecontroller", StringComparison.Ordinal))
        {
            return ("high", "runtime code or scene content", "runtime");
        }

        if (lower.EndsWith(".shader", StringComparison.Ordinal)
            || lower.EndsWith(".compute", StringComparison.Ordinal)
            || lower.EndsWith(".cginc", StringComparison.Ordinal)
            || lower.EndsWith(".uxml", StringComparison.Ordinal)
            || lower.EndsWith(".uss", StringComparison.Ordinal)
            || lower.EndsWith(".asset", StringComparison.Ordinal)
            || lower.EndsWith(".json", StringComparison.Ordinal)
            || lower.EndsWith(".csv", StringComparison.Ordinal)
            || lower.EndsWith(".xml", StringComparison.Ordinal)
            || lower.EndsWith(".yml", StringComparison.Ordinal)
            || lower.EndsWith(".yaml", StringComparison.Ordinal)
            || lower.EndsWith(".mat", StringComparison.Ordinal))
        {
            return ("medium", "serialized content or authoring asset", "content");
        }

        if (lower.EndsWith(".meta", StringComparison.Ordinal))
        {
            return ("medium", "asset metadata changed", "content");
        }

        return ("medium", "general project file", "content");
    }

    private static (bool, string) TestsRun(string raw)
    {
        var jobId = Guid.NewGuid().ToString("N");
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
        var status = new JsonObject
        {
            ["jobId"] = jobId,
            ["state"] = "running",
            ["mode"] = modeStr,
            ["filter"] = string.IsNullOrWhiteSpace(testFilter) ? null : testFilter,
            ["startedAtUtc"] = DateTime.UtcNow.ToString("O")
        };
        WriteTestsStatus(status.ToJsonString());
        return (true, $"Test run started: job_id={jobId}; mode={modeStr}{(string.IsNullOrWhiteSpace(testFilter) ? string.Empty : $"; filter={testFilter}")}");
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
        var status = ReadStatusObject(statusPath);
        var statusTimestamp = File.Exists(statusPath) ? File.GetLastWriteTimeUtc(statusPath) : (DateTime?)null;
        var latestSummary = FindLatestUnityTestSummary(statusTimestamp);

        if (status != null)
        {
            var state = StatusString(status.ToJsonString(), "state");
            if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
            {
                var startedAt = StatusString(status.ToJsonString(), "startedAtUtc");
                if (TryParseUtc(startedAt, out var started) && DateTime.UtcNow - started > TimeSpan.FromMinutes(30))
                {
                return (true, MergeStatusAndSummary(status.ToJsonString(), latestSummary, "timeout"));
                }

                if (latestSummary != null)
                {
                    return (true, MergeStatusAndSummary(status.ToJsonString(), latestSummary, "completed"));
                }

                return (true, status.ToJsonString());
            }

            if (latestSummary != null)
            {
                return (true, MergeStatusAndSummary(status.ToJsonString(), latestSummary, state));
            }

            return (true, status.ToJsonString());
        }

        if (File.Exists(statusPath))
        {
            var statusRaw = File.ReadAllText(statusPath);
            if (latestSummary != null && statusRaw.Contains("\"state\":\"running\"", StringComparison.OrdinalIgnoreCase))
            {
                return (true, latestSummary.ToJsonString());
            }
            return (true, statusRaw);
        }

        return latestSummary != null
            ? (true, latestSummary.ToJsonString())
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
            var statusPath = Path.Combine(BridgeRoot(), "test-results.json");
            if (string.Equals(callbackName, "RunStarted", StringComparison.Ordinal))
            {
                var status = ReadStatusObject(statusPath) ?? new JsonObject();
                status["state"] = "running";
                status["mode"] = mode;
                status["callbackAtUtc"] = DateTime.UtcNow.ToString("O");
                status["startedAtUtc"] ??= DateTime.UtcNow.ToString("O");
                WriteTestsStatus(status.ToJsonString());
                return;
            }

            if (string.Equals(callbackName, "RunFinished", StringComparison.Ordinal))
            {
                var result = args != null && args.Length > 0 ? args[0] : null;
                var summary = BuildSummaryFromResultAdapter(result);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    var existing = ReadStatusObject(statusPath) ?? new JsonObject();
                    var parsed = JsonNode.Parse(summary!) as JsonObject;
                    if (parsed != null)
                    {
                        existing["state"] = "completed";
                        existing["mode"] = mode;
                        existing["finishedAtUtc"] = DateTime.UtcNow.ToString("O");
                        foreach (var kv in parsed)
                        {
                            existing[kv.Key] = kv.Value?.DeepClone();
                        }
                        WriteTestsStatus(existing.ToJsonString());
                    }
                    else
                    {
                        WriteTestsStatus(summary!);
                    }
                }
                else
                {
                    var existing = ReadStatusObject(statusPath) ?? new JsonObject();
                    existing["state"] = "completed";
                    existing["mode"] = mode;
                    existing["finishedAtUtc"] = DateTime.UtcNow.ToString("O");
                    WriteTestsStatus(existing.ToJsonString());
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
        return JsonString(args ?? string.Empty, key);
    }

    private static string[] JsonArgumentStringArray(string json, string key)
    {
        try
        {
            var args = ExtractJsonObjectValue(json, "arguments");
            var source = string.IsNullOrWhiteSpace(args) ? json : args;
            source ??= string.Empty;
            var node = JsonNode.Parse(source) as JsonObject;
            if (node == null || !node.TryGetPropertyValue(key, out var valueNode) || valueNode is null)
            {
                return Array.Empty<string>();
            }

            if (valueNode is JsonArray array)
            {
                return array.Select(v => v?.GetValue<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToArray();
            }

            if (valueNode is JsonValue scalar)
            {
                var value = scalar.GetValue<string>();
                return string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value.Trim() };
            }
        }
        catch
        {
        }

        return Array.Empty<string>();
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
        var ns = string.IsNullOrWhiteSpace(namespaceName) ? "xLabMcp.Data" : namespaceName.Trim();
        return
$@"using UnityEngine;

namespace {ns}
{{
[CreateAssetMenu(menuName = ""xLabMcp/Data/{className}"", fileName = ""{className}"")]
    public sealed class {className} : ScriptableObject
    {{
    }}
}}
";
    }

    private static string BuildScriptTemplate(string className, string namespaceName, string baseClass)
    {
        var ns = string.IsNullOrWhiteSpace(namespaceName) ? "xLabMcp.Scripts" : namespaceName.Trim();
        var safeBaseClass = string.IsNullOrWhiteSpace(baseClass) ? "MonoBehaviour" : baseClass.Trim();
        return
$@"using UnityEngine;

namespace {ns}
{{
    public sealed class {className} : {safeBaseClass}
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

    private static JsonObject? FindLatestUnityTestSummary(DateTime? notBeforeUtc)
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

        if (notBeforeUtc.HasValue && file.LastWriteTimeUtc < notBeforeUtc.Value)
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

            var failedTests = new JsonArray();
            foreach (var testCase in doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "test-case", StringComparison.OrdinalIgnoreCase) && string.Equals((string?)e.Attribute("result"), "Failed", StringComparison.OrdinalIgnoreCase)))
            {
                var failureMessage = testCase.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "message", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
                var stackTrace = testCase.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "stack-trace", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? string.Empty;
                var testName = (string?)testCase.Attribute("fullname") ?? (string?)testCase.Attribute("name") ?? string.Empty;
                var fixture = testCase.Ancestors().FirstOrDefault(e => string.Equals(e.Name.LocalName, "test-suite", StringComparison.OrdinalIgnoreCase))
                              is XElement suite
                    ? ((string?)suite.Attribute("fullname") ?? (string?)suite.Attribute("name") ?? string.Empty)
                    : string.Empty;

                failedTests.Add(new JsonObject
                {
                    ["fixture"] = fixture,
                    ["test"] = testName,
                    ["message"] = failureMessage,
                    ["stacktrace"] = stackTrace
                });
            }

            return new JsonObject
            {
                ["state"] = failed > 0 ? "failed" : "completed",
                ["source"] = file.FullName,
                ["total"] = total,
                ["passed"] = passed,
                ["failed"] = failed,
                ["skipped"] = skipped,
                ["duration"] = duration,
                ["failedTests"] = failedTests
            };
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject? ReadStatusObject(string statusPath)
    {
        if (!File.Exists(statusPath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(statusPath)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string MergeStatusAndSummary(string statusJson, JsonObject? summary, string? forcedState)
    {
        var merged = JsonNode.Parse(statusJson) as JsonObject ?? new JsonObject();
        if (!string.IsNullOrWhiteSpace(forcedState))
        {
            merged["state"] = forcedState;
        }

        if (summary != null)
        {
            foreach (var kv in summary)
            {
                merged[kv.Key] = kv.Value?.DeepClone();
            }
        }

        merged["resolvedAtUtc"] = DateTime.UtcNow.ToString("O");
        return merged.ToJsonString();
    }

    private static string? StatusString(string statusJson, string key)
    {
        var marker = $"\"{key}\":";
        var index = statusJson.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var valueStart = index + marker.Length;
        while (valueStart < statusJson.Length && char.IsWhiteSpace(statusJson[valueStart]))
        {
            valueStart++;
        }

        if (valueStart >= statusJson.Length)
        {
            return null;
        }

        if (statusJson[valueStart] == '"')
        {
            valueStart++;
            var valueEnd = statusJson.IndexOf('"', valueStart);
            return valueEnd >= 0 ? statusJson.Substring(valueStart, valueEnd - valueStart) : null;
        }

        var end = valueStart;
        while (end < statusJson.Length && statusJson[end] != ',' && statusJson[end] != '}' && !char.IsWhiteSpace(statusJson[end]))
        {
            end++;
        }

        return end > valueStart ? statusJson.Substring(valueStart, end - valueStart) : null;
    }

    private static bool TryParseUtc(string? text, out DateTime value)
    {
        return DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out value);
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
