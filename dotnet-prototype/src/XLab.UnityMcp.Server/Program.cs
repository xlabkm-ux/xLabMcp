using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XLab.UnityMcp.Protocol;

var server = new McpServer(new McpRequestDispatcher());
server.Run();

public sealed class McpRequestDispatcher
{
    private const string UnityMcpPackageName = "com.xlabkm.unity-mcp";
    private const string Urp = "com.unity.render-pipelines.universal";
    private string? _defaultProjectRoot;
    private readonly Dictionary<string, JsonObject> _toolSchemas = BuildToolSchemas();

    public InitializeResult BuildInitializeResult() => new(
        McpProtocol.Version,
        new ServerInfo("XLab.UnityMcp.Server", "0.3.0"),
        new { tools = new { listChanged = false } });

    public ToolsListResult BuildToolsList()
    {
        var tools = _toolSchemas
            .Where(kv => IsActiveToolName(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ToolDefinition(kv.Key, kv.Key.Replace('_', ' '), kv.Value))
            .ToList();
        return new ToolsListResult(tools);
    }

    private static bool IsActiveToolName(string name) => name switch
    {
        "project_root.set" => true,
        "project.info" => true,
        "project.health_check" => true,
        "project.capabilities" => true,
        "editor.state" => true,
        "read_console" => true,
        "manage_asset" => true,
        "manage_hierarchy" => true,
        "manage_scene" => true,
        "manage_gameobject" => true,
        "manage_components" => true,
        "manage_script" => true,
        "manage_scriptableobject" => true,
        "manage_prefabs" => true,
        "manage_graph" => true,
        "manage_ui" => true,
        "manage_localization" => true,
        "manage_editor" => true,
        "manage_input" => true,
        "manage_camera" => true,
        "manage_graphics" => true,
        "manage_profiler" => true,
        "manage_build" => true,
        "run_tests" => true,
        "get_test_job" => true,
        _ => false
    };

    public ToolCallResult HandleToolCall(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("name", out var n)) return Err("Missing params.name");
        var name = n.GetString() ?? string.Empty;
        if (!IsActiveToolName(name))
        {
            return Err($"Unknown tool: {name}");
        }
        var a = p.TryGetProperty("arguments", out var args) ? args : default;
        var validationError = ValidateToolArguments(name, a);
        if (validationError != null)
        {
            return Err($"validation error: {validationError}");
        }

        return name switch
        {
            "project_root.set" => ProjectRootSet(a),
            "project.info" => ProjectInfo(a),
            "project.capabilities" => Bridge("project.capabilities", a),
            "editor.state" => Bridge("editor.state", a),
            "project.health_check" => Health(a),
            "read_console" => ReadConsole(a),
            "manage_hierarchy" => ManageHierarchy(a),
            "manage_scene" => ManageScene(a),
            "manage_prefabs" => ManagePrefabs(a),
            "manage_script" => ManageScript(a),
            "manage_scriptableobject" => ManageScriptableObject(a),
            "manage_graph" => ManageGraph(a),
            "manage_ui" => ManageUi(a),
            "manage_localization" => ManageLocalization(a),
            "manage_editor" => ManageEditor(a),
            "manage_input" => ManageInput(a),
            "manage_camera" => ManageCamera(a),
            "manage_gameobject" => ManageGameObject(a),
            "manage_components" => ManageComponents(a),
            "manage_asset" => ManageAsset(a),
            "manage_graphics" => ManageGraphics(a),
            "manage_profiler" => ManageProfiler(a),
            "manage_build" => ManageBuild(a),
            "run_tests" => RunTests(a),
            "get_test_job" => GetTestJob(a),
            _ => Err($"Unknown tool: {name}")
        };
    }

    private string? ValidateToolArguments(string toolName, JsonElement args)
    {
        if (!_toolSchemas.TryGetValue(toolName, out var schema))
        {
            return null;
        }

        if (args.ValueKind != JsonValueKind.Undefined && args.ValueKind != JsonValueKind.Object)
        {
            return "arguments must be an object";
        }

        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        var allowed = new HashSet<string>(properties.Select(p => p.Key), StringComparer.Ordinal);
        var additionalAllowed = schema["additionalProperties"]?.GetValue<bool?>() ?? true;

        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in args.EnumerateObject())
            {
                if (!additionalAllowed && !allowed.Contains(prop.Name))
                {
                    return $"unknown property '{prop.Name}'";
                }

                if (!properties.TryGetPropertyValue(prop.Name, out var propertySchemaNode) || propertySchemaNode is not JsonObject propertySchema)
                {
                    continue;
                }

                var typeName = propertySchema["type"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(typeName) && !MatchesType(prop.Value, typeName!))
                {
                    return $"property '{prop.Name}' must be of type {typeName}";
                }

                if (propertySchema["enum"] is JsonArray en && en.Count > 0 && prop.Value.ValueKind == JsonValueKind.String)
                {
                    var value = prop.Value.GetString() ?? string.Empty;
                    var ok = en.OfType<JsonValue>().Select(x => x.GetValue<string>()).Any(x => string.Equals(x, value, StringComparison.Ordinal));
                    if (!ok)
                    {
                        return $"property '{prop.Name}' has unsupported value '{value}'";
                    }
                }
            }
        }

        if (schema["required"] is JsonArray req)
        {
            foreach (var node in req.OfType<JsonValue>())
            {
                var key = node.GetValue<string>();
                if (!HasArg(args, key))
                {
                    return $"missing required property '{key}'";
                }
            }
        }

        // one-of style constraints for target tool actions
        if (toolName == "manage_asset")
        {
            var action = (Opt(args, "action") ?? "refresh").ToLowerInvariant();
            if (action is "create_folder" or "exists" or "read_text_file" or "write_text_file" or "docs_update" && !HasAnyArg(args, "path", "folderPath"))
            {
                return "requires one of: path | folderPath";
            }
            if (action == "list_localization_keys" && !HasAnyArg(args, "path", "folderPath", "table"))
            {
                return "requires one of: path | folderPath | table";
            }
            if (action == "resolve_localization_keys")
            {
                if (!HasArg(args, "keys"))
                {
                    return "requires keys";
                }
                if (!HasAnyArg(args, "table", "tableName"))
                {
                    return "requires one of: table | tableName";
                }
                if (!HasAnyArg(args, "locale"))
                {
                    return "requires locale";
                }
            }
        }
        if (toolName == "manage_build")
        {
            var action = (Opt(args, "action") ?? "profiles").ToLowerInvariant();
            if (action == "scenes" && !HasAnyArg(args, "path", "folderPath"))
            {
                return "requires one of: path | folderPath";
            }
            if (action == "profiles")
            {
                var mode = (Opt(args, "mode") ?? "get_active").ToLowerInvariant();
                if (mode == "set_active" && !HasAnyArg(args, "profile", "buildTarget", "target"))
                {
                    return "requires one of: profile | buildTarget | target";
                }
            }
        }
        if (toolName == "manage_script" && !HasAnyArg(args, "scriptName", "path"))
        {
            return "requires one of: scriptName | path";
        }
        if (toolName == "manage_prefabs" && !HasAnyArg(args, "sourceObjectPath", "sourcePath", "sourceObjectName"))
        {
            return "requires one of: sourceObjectPath | sourcePath | sourceObjectName";
        }
        if (toolName == "manage_graph" && !HasAnyArg(args, "fromNodeId", "sourceNodeId", "from", "source"))
        {
            return "requires one of: fromNodeId | sourceNodeId | from | source";
        }
        if (toolName == "manage_graph" && !HasAnyArg(args, "toNodeId", "targetNodeId", "to", "target"))
        {
            return "requires one of: toNodeId | targetNodeId | to | target";
        }

        return null;
    }

    private static bool MatchesType(JsonElement value, string expectedType) => expectedType switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => true
    };

    private ToolCallResult ProjectInfo(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var vfile = Path.Combine(root, "ProjectSettings", "ProjectVersion.txt");
        var ver = File.Exists(vfile) ? (File.ReadLines(vfile).FirstOrDefault(l => l.Contains("m_EditorVersion:"))?.Split(':').Last().Trim() ?? "unknown") : "unknown";
        var payload = new JsonObject
        {
            ["projectRoot"] = root,
            ["projectName"] = Path.GetFileName(root),
            ["unityVersion"] = ver,
            ["hasAssets"] = Directory.Exists(Path.Combine(root, "Assets")),
            ["hasPackages"] = Directory.Exists(Path.Combine(root, "Packages"))
        };
        return Ok(payload.ToJsonString());
    }

    private ToolCallResult Health(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var mf = Path.Combine(root, "Packages", "manifest.json");
        var hasUrp = false;
        if (File.Exists(mf))
        {
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(mf));
                hasUrp = d.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object && deps.TryGetProperty(Urp, out _);
            }
            catch { return Err("manifest.json parse error"); }
        }
        return Ok($"health={(hasUrp ? "healthy" : "degraded")}; urp={hasUrp}");
    }

    private ToolCallResult Bridge(string cmd, JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var id = Guid.NewGuid().ToString("N");
        var b = BridgeRoot(root);
        var cdir = Path.Combine(b, "commands");
        var rdir = Path.Combine(b, "responses");
        Directory.CreateDirectory(cdir); Directory.CreateDirectory(rdir);
        JsonNode argsNode;
        if (a.ValueKind == JsonValueKind.Object)
        {
            argsNode = JsonNode.Parse(a.GetRawText()) ?? new JsonObject();
        }
        else
        {
            argsNode = new JsonObject();
        }
        var obj = new JsonObject { ["id"] = id, ["command"] = cmd, ["arguments"] = argsNode, ["createdAtUtc"] = DateTime.UtcNow.ToString("O") };
        File.WriteAllText(Path.Combine(cdir, $"{id}.json"), obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, Encoding.UTF8);

        var waitMs = IntOpt(a, "waitMs") ?? 1200;
        if (waitMs <= 0) return Ok($"queued:{cmd}; id={id}");
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        var rf = Path.Combine(rdir, $"{id}.json");
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(rf))
            {
                try
                {
                    var n = JsonNode.Parse(File.ReadAllText(rf)) as JsonObject;
                    var ok = n?["success"]?.GetValue<bool>() ?? false;
                    var msg = n?["message"]?.GetValue<string>() ?? string.Empty;
                    return ok ? Ok(msg) : Err(msg);
                }
                catch { return Err("Bridge response parse error"); }
            }
            Thread.Sleep(100);
        }
        return Ok($"queued:{cmd}; id={id}; response=pending");
    }

    private ToolCallResult ScriptCreate(JsonElement a)
    {
        var root = ResolveRoot(a); var script = Req(a, "scriptName");
        if (root is null) return Err("Missing projectRoot");
        if (script is null) return Err("Missing scriptName");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var folder = Opt(a, "folder") ?? "Assets/Scripts";
        var name = Ident(script);
        var path = InRoot(root, folder.Trim('/','\\') + "/" + name + ".cs");
        if (path is null) return Err("Path escapes project root");
        if (!BoolOpt(a, "overwrite") && File.Exists(path)) return Err($"Script exists: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ScriptTpl(name, Opt(a, "namespace")), Encoding.UTF8);
        return Ok($"Script created: {path}");
    }

    private ToolCallResult ScriptEdit(JsonElement a)
    {
        var root = ResolveRoot(a); var rp = Req(a, "path");
        if (root is null) return Err("Missing projectRoot");
        if (rp is null) return Err("Missing path");
        var path = InRoot(root, rp); if (path is null) return Err("Path escapes project root");
        if (!File.Exists(path)) return Err($"Script not found: {path}");
        var mode = (Opt(a, "mode") ?? "append").ToLowerInvariant();
        var txt = Opt(a, "text") ?? string.Empty;
        var cur = File.ReadAllText(path);
        string next;
        if (mode == "overwrite") next = txt;
        else if (mode == "replace")
        {
            var old = Req(a, "oldText"); if (old is null) return Err("Missing oldText for replace");
            if (!cur.Contains(old, StringComparison.Ordinal)) return Err("oldText not found");
            next = cur.Replace(old, txt, StringComparison.Ordinal);
        }
        else if (mode == "append") next = cur + txt;
        else return Err("Unknown mode: append|replace|overwrite");
        File.WriteAllText(path, next, Encoding.UTF8);
        return Ok($"Script updated: {path}");
    }

    private ToolCallResult ScriptValidate(JsonElement a)
    {
        var root = ResolveRoot(a); var rp = Req(a, "path");
        if (root is null) return Err("Missing projectRoot");
        if (rp is null) return Err("Missing path");
        var path = InRoot(root, rp); if (path is null) return Err("Path escapes project root");
        if (!File.Exists(path)) return Err($"Script not found: {path}");
        var code = File.ReadAllText(path);
        if (code.Count(c => c == '{') != code.Count(c => c == '}')) return Err("Brace mismatch");
        if (!Regex.IsMatch(code, @"\bclass\s+[A-Za-z_][A-Za-z0-9_]*")) return Err("No class declaration");
        return Ok(code.Contains("MonoBehaviour", StringComparison.Ordinal) ? "Validation passed (+MonoBehaviour)" : "Validation passed");
    }

    private ToolCallResult Bootstrap(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var force = BoolOpt(a, "force");
        var scenes = Path.Combine(root, "Assets", "Scenes");
        var scripts = Path.Combine(root, "Assets", "Scripts");
        var md = Path.Combine(scenes, "Bootstrap2DURP.codex.md");
        var cs = Path.Combine(scripts, "GameBootstrap.cs");
        Directory.CreateDirectory(scenes); Directory.CreateDirectory(scripts);
        if (!force && (File.Exists(md) || File.Exists(cs))) return Err("Bootstrap files exist. Use force=true");
        File.WriteAllText(md, "# Bootstrap2DURP\n\n- Main Camera\n- Global Light 2D\n- Player\n", Encoding.UTF8);
        File.WriteAllText(cs, "using UnityEngine;\npublic sealed class GameBootstrap : MonoBehaviour { void Start(){} }\n", Encoding.UTF8);
        return Ok($"Bootstrap scaffold created: {md}; {cs}");
    }

    private ToolCallResult EnsureUrp(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var ver = Opt(a, "version") ?? "14.0.11";
        var dir = Path.Combine(root, "Packages"); Directory.CreateDirectory(dir);
        var mf = Path.Combine(dir, "manifest.json");
        JsonObject m;
        if (File.Exists(mf))
        {
            try { m = JsonNode.Parse(File.ReadAllText(mf)) as JsonObject ?? new JsonObject(); }
            catch { return Err("manifest.json invalid json"); }
        }
        else m = new JsonObject();
        if (m["dependencies"] is not JsonObject d) { d = new JsonObject(); m["dependencies"] = d; }
        d[Urp] = ver;
        File.WriteAllText(mf, m.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, Encoding.UTF8);
        return Ok($"URP dependency set to {ver} in {mf}");
    }

    private ToolCallResult Gdd(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var title = Opt(a, "gameTitle") ?? "New Game";
        var genre = Opt(a, "genre") ?? "Arcade";
        var loop = Opt(a, "coreLoop") ?? "Explore -> Collect -> Upgrade";
        var dir = Path.Combine(root, "Docs"); Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "GameDesign.md");
        File.WriteAllText(p, $"# {title}\n\n## Genre\n{genre}\n\n## Core Loop\n{loop}\n", Encoding.UTF8);
        return Ok($"Game design doc created: {p}");
    }

    private ToolCallResult Backlog(JsonElement a)
    {
        var root = ResolveRoot(a); var f = Req(a, "featureName");
        if (root is null) return Err("Missing projectRoot");
        if (f is null) return Err("Missing featureName");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        var dir = Path.Combine(root, "Docs", "Backlog"); Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, SafeFile(f) + ".md");
        var goals = StrList(a, "goals");
        var sb = new StringBuilder();
        sb.AppendLine($"# Feature Backlog: {f}").AppendLine().AppendLine("## Goals");
        if (goals.Count == 0) sb.AppendLine("- Define gameplay intent for this feature");
        else foreach (var g in goals) sb.AppendLine($"- {g}");
        sb.AppendLine().AppendLine("## Codex Tasks").AppendLine("- [ ] Scene updates").AppendLine("- [ ] Script updates");
        File.WriteAllText(p, sb.ToString(), Encoding.UTF8);
        return Ok($"Feature backlog created: {p}");
    }

    private ToolCallResult ProjectRootSet(JsonElement a)
    {
        var root = Req(a, "projectRoot");
        if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");
        _defaultProjectRoot = root;
        return Ok($"projectRoot set: {root}");
    }

    private ToolCallResult ScriptCreateOrEdit(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var scriptName = Opt(a, "scriptName");
        var path = Opt(a, "path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            return ScriptEdit(InjectArg(a, "projectRoot", root));
        }
        if (!string.IsNullOrWhiteSpace(scriptName))
        {
            return ScriptCreate(InjectArg(a, "projectRoot", root));
        }
        return Err("Provide scriptName (create) or path (edit)");
    }

    private ToolCallResult AssetCreateFolder(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var rel = Req(a, "path") ?? Req(a, "folderPath");
        if (rel is null) return Err("Missing path");
        var full = InRoot(root, rel); if (full is null) return Err("Path escapes project root");
        Directory.CreateDirectory(full);
        return Ok($"Folder created: {full}");
    }

    private ToolCallResult AssetExists(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var rel = Req(a, "path");
        if (rel is null) return Err("Missing path");
        var full = InRoot(root, rel); if (full is null) return Err("Path escapes project root");
        var exists = File.Exists(full) || Directory.Exists(full);
        return Ok($"exists={exists}; path={full}");
    }

    private ToolCallResult TestsResults(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var path = Path.Combine(BridgeRoot(root), "test-results.json");
        if (!File.Exists(path)) return Ok("test results: no test-results.json yet");
        return Ok(File.ReadAllText(path));
    }

    private ToolCallResult GraphOpenOrCreate(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var name = Req(a, "graphName") ?? "DefaultGraph";
        var dir = Path.Combine(root, "Assets", "Graphs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SafeFile(name) + ".json");
        if (!File.Exists(path)) File.WriteAllText(path, "{\n  \"nodes\": [],\n  \"edges\": []\n}\n", Encoding.UTF8);
        return Ok($"graph created or opened: {path}");
    }

    private ToolCallResult GraphConnect(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var name = Req(a, "graphName") ?? "DefaultGraph";
        var path = Path.Combine(root, "Assets", "Graphs", SafeFile(name) + ".json");
        if (!File.Exists(path)) return Err($"Graph not found: {path}");
        var from = Opt(a, "from") ?? "?";
        var to = Opt(a, "to") ?? "?";
        var entry = $"// connect {DateTime.UtcNow:O}: {from}->{to}\n";
        File.AppendAllText(path, entry, Encoding.UTF8);
        return Ok($"manage_graph connect appended: {path}");
    }

    private ToolCallResult GraphEdit(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var name = Req(a, "graphName") ?? "DefaultGraph";
        var path = Path.Combine(root, "Assets", "Graphs", SafeFile(name) + ".json");
        if (!File.Exists(path)) return Err($"Graph not found: {path}");
        var patch = Opt(a, "patch") ?? Opt(a, "text") ?? "// graph edit\n";
        File.AppendAllText(path, patch + Environment.NewLine, Encoding.UTF8);
        return Ok($"manage_graph edit applied: {path}");
    }

    private ToolCallResult GraphValidate(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var name = Req(a, "graphName") ?? "DefaultGraph";
        var path = Path.Combine(root, "Assets", "Graphs", SafeFile(name) + ".json");
        if (!File.Exists(path)) return Err($"Graph not found: {path}");
        var text = File.ReadAllText(path);
        return Ok($"manage_graph validate: exists=true; size={text.Length}");
    }

    private ToolCallResult SceneValidateRefs(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var rel = Req(a, "scenePath");
        if (rel is null) return Err("Missing scenePath");
        var path = InRoot(root, rel); if (path is null) return Err("Path escapes project root");
        if (!File.Exists(path)) return Err($"Scene not found: {path}");
        var text = File.ReadAllText(path);
        return Ok($"scene validation: exists=true; bytes={text.Length}");
    }

    private ToolCallResult PrefabValidate(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var rel = Req(a, "prefabPath");
        if (rel is null) return Err("Missing prefabPath");
        var path = InRoot(root, rel); if (path is null) return Err("Path escapes project root");
        var ok = File.Exists(path);
        return ok ? Ok($"prefab validation: ok {path}") : Err($"Prefab not found: {path}");
    }

    private ToolCallResult ScriptableObjectCreateOrEdit(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var name = Req(a, "name") ?? "GameDataConfig";
        var scriptsDir = Path.Combine(root, "Assets", "Scripts");
        Directory.CreateDirectory(scriptsDir);
        var path = Path.Combine(scriptsDir, Ident(name) + ".cs");
        var content =
            "using UnityEngine;\n\n" +
$"[CreateAssetMenu(menuName = \"xLabMcp/{Ident(name)}\")]\n" +
            $"public sealed class {Ident(name)} : ScriptableObject {{ }}\n";
        File.WriteAllText(path, content, Encoding.UTF8);
        return Ok($"manage_scriptableobject create_or_edit: {path}");
    }

    private ToolCallResult UiCreateOrEdit(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var dir = Path.Combine(root, "Assets", "UI");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "UIPlan.codex.md");
        var body = Opt(a, "text") ?? "- HUD Root\n- Result Panel\n- Objective Tracker\n";
        File.WriteAllText(path, body, Encoding.UTF8);
        return Ok($"ui content saved: {path}");
    }

    private ToolCallResult LocalizationKeyAdd(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var key = Req(a, "key"); if (key is null) return Err("Missing key");
        var value = Opt(a, "value") ?? key;
        var dir = Path.Combine(root, "Assets", "Localization");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "keys.csv");
        File.AppendAllText(path, $"{key},{value}{Environment.NewLine}", Encoding.UTF8);
        return Ok($"manage_localization key_add: {key}");
    }

    private ToolCallResult AssetListModified(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var modified = Directory.GetFiles(Path.Combine(root, "Assets"), "*", SearchOption.AllDirectories)
            .Where(p => File.GetLastWriteTimeUtc(p) > DateTime.UtcNow.AddDays(-1))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\','/'))
            .Take(200);
        return Ok(string.Join("\n", modified));
    }

    private ToolCallResult ChangeSummary(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var gitDir = root;
        if (!Directory.Exists(Path.Combine(gitDir, ".git"))) return Ok("change summary: no git repo");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "status --short")
            {
                WorkingDirectory = gitDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return Err("change summary: failed to start git");
            var outText = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return Ok(string.IsNullOrWhiteSpace(outText) ? "clean" : outText.TrimEnd());
        }
        catch (Exception ex)
        {
            return Err($"change summary error: {ex.Message}");
        }
    }

    private ToolCallResult ProjectDocsUpdate(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var docs = Path.Combine(root, "Docs");
        Directory.CreateDirectory(docs);
        var path = Path.Combine(docs, "MCP_PROGRESS.md");
        var line = Opt(a, "line") ?? $"Updated at {DateTime.UtcNow:O}";
        File.AppendAllText(path, "- " + line + Environment.NewLine, Encoding.UTF8);
        return Ok($"project docs updated: {path}");
    }

    private ToolCallResult ReadConsole(JsonElement a)
    {
        return Bridge("read_console", a);
    }

    private ToolCallResult ManageScene(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "validate_references").ToLowerInvariant();
        var root = ResolveRoot(a);
        if (root is null) return Err("Missing projectRoot");

        return action switch
        {
            "create" => Bridge("manage_scene", InjectArg(InjectArg(a, "projectRoot", root), "action", "create")),
            "open" => Bridge("manage_scene", InjectArg(InjectArg(a, "projectRoot", root), "action", "open")),
            "save" => Bridge("manage_scene", InjectArg(InjectArg(a, "projectRoot", root), "action", "save")),
            "validate_references" => Bridge("manage_scene", InjectArg(InjectArg(a, "projectRoot", root), "scenePath", Opt(a, "path") ?? Opt(a, "scenePath") ?? Opt(a, "scene_path") ?? string.Empty)),
            _ => Err($"Unsupported manage_scene action: {action}")
        };
    }

    private ToolCallResult ManagePrefabs(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "validate_references").ToLowerInvariant();
        var root = ResolveRoot(a);
        if (root is null) return Err("Missing projectRoot");

        return action switch
        {
            "create" => Bridge("manage_prefabs", InjectArg(InjectArg(a, "projectRoot", root), "action", "create")),
            "open" => Bridge("manage_prefabs", InjectArg(InjectArg(a, "projectRoot", root), "action", "open")),
            "save" => Bridge("manage_prefabs", InjectArg(InjectArg(a, "projectRoot", root), "action", "save")),
            "instantiate" => Bridge("manage_prefabs", InjectArg(InjectArg(a, "projectRoot", root), "action", "instantiate")),
            "validate_references" => Bridge("manage_prefabs", InjectArg(InjectArg(a, "projectRoot", root), "prefabPath", Opt(a, "path") ?? Opt(a, "prefabPath") ?? Opt(a, "prefab_path") ?? string.Empty)),
            _ => Err($"Unsupported manage_prefabs action: {action}")
        };
    }

    private ToolCallResult ManageEditor(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "play_mode").ToLowerInvariant();
        var mode = (Opt(a, "mode") ?? "status").ToLowerInvariant();
        return action switch
        {
            "install" => InstallUnityMcpPackage(a),
            "update" => UpdateUnityMcpPackage(a),
            "delete" => DeleteUnityMcpPackage(a),
            "play_mode" => mode switch
            {
                "enter" => Bridge("manage_editor", InjectArg(a, "mode", "enter")),
                "exit" => Bridge("manage_editor", InjectArg(a, "mode", "exit")),
                "status" => Bridge("manage_editor", InjectArg(a, "mode", "status")),
                _ => Err($"Unsupported play_mode mode: {mode}")
            },
            "status" => Bridge("manage_editor", InjectArg(a, "mode", "status")),
            "compile_status" => Bridge("manage_editor", InjectArg(a, "action", "compile_status")),
            _ => Err($"Unsupported manage_editor action: {action}")
        };
    }

    private ToolCallResult InstallUnityMcpPackage(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");

        var resolve = ResolveUnityMcpPackagePaths(root, a);
        if (!resolve.Success)
        {
            return Err(resolve.Error!);
        }

        if (Directory.Exists(resolve.TargetPath!))
        {
            return Err($"Unity MCP package already installed at {resolve.TargetPath}. Use action=update.");
        }

        CopyDirectory(resolve.SourcePath!, resolve.TargetPath!);
        return Ok(PackageLifecyclePayload("install", root, resolve.SourcePath!, resolve.TargetPath!, changed: true));
    }

    private ToolCallResult UpdateUnityMcpPackage(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");

        var resolve = ResolveUnityMcpPackagePaths(root, a);
        if (!resolve.Success)
        {
            return Err(resolve.Error!);
        }

        if (PathsEqual(resolve.SourcePath!, resolve.TargetPath!))
        {
            return Ok(PackageLifecyclePayload("update", root, resolve.SourcePath!, resolve.TargetPath!, changed: false));
        }

        if (Directory.Exists(resolve.TargetPath!))
        {
            Directory.Delete(resolve.TargetPath!, recursive: true);
        }

        CopyDirectory(resolve.SourcePath!, resolve.TargetPath!);
        return Ok(PackageLifecyclePayload("update", root, resolve.SourcePath!, resolve.TargetPath!, changed: true));
    }

    private ToolCallResult DeleteUnityMcpPackage(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        if (!Directory.Exists(root)) return Err($"Project root not found: {root}");

        var targetPath = Path.Combine(root, "Packages", UnityMcpPackageName);
        var changed = Directory.Exists(targetPath);
        if (changed)
        {
            Directory.Delete(targetPath, recursive: true);
        }

        return Ok(PackageLifecyclePayload("delete", root, null, targetPath, changed));
    }

    private ToolCallResult ManageInput(JsonElement a)
    {
        return Bridge("manage_input", a);
    }

    private ToolCallResult ManageCamera(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "screenshot").ToLowerInvariant();
        if (action != "screenshot")
        {
            return Err($"Unsupported manage_camera action: {action}");
        }

        return Bridge("manage_camera", a);
    }

    private ToolCallResult ManageGameObject(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "modify").ToLowerInvariant();
        return action switch
        {
            "create" => Bridge("manage_gameobject", InjectArg(a, "action", "create")),
            "modify" => Bridge("manage_gameobject", InjectArg(a, "action", "modify")),
            "invoke_method" => Bridge("manage_gameobject", InjectArg(a, "action", "invoke_method")),
            _ => Err($"Unsupported manage_gameobject action: {action}")
        };
    }

    private ToolCallResult ManageComponents(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "add").ToLowerInvariant();
        return action switch
        {
            "add" => Bridge("manage_components", InjectArg(a, "action", "add")),
            "set" => Bridge("manage_components", InjectArg(a, "action", "set")),
            "get_serialized" => Bridge("manage_components", InjectArg(a, "action", "get_serialized")),
            "set_serialized" => Bridge("manage_components", InjectArg(a, "action", "set_serialized")),
            _ => Err($"Unsupported manage_components action: {action}")
        };
    }

    private ToolCallResult ManageAsset(JsonElement a)
    {
        return Bridge("manage_asset", a);
    }

    private ToolCallResult ManageHierarchy(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "list").ToLowerInvariant();
        return action switch
        {
            "list" => Bridge("manage_hierarchy", InjectArg(a, "action", "list")),
            "find" => Bridge("manage_hierarchy", InjectArg(a, "action", "find")),
            _ => Err($"Unsupported manage_hierarchy action: {action}")
        };
    }

    private ToolCallResult ManageScript(JsonElement a)
    {
        return Bridge("manage_script", a);
    }

    private ToolCallResult ManageScriptableObject(JsonElement a)
    {
        return Bridge("manage_scriptableobject", a);
    }

    private ToolCallResult ManageGraph(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "open_or_create").ToLowerInvariant();
        return action switch
        {
            "open_or_create" => Bridge("manage_graph", InjectArg(a, "action", "open_or_create")),
            "connect" => Bridge("manage_graph", InjectArg(a, "action", "connect")),
            "edit" => Bridge("manage_graph", InjectArg(a, "action", "edit")),
            "validate" => Bridge("manage_graph", InjectArg(a, "action", "validate")),
            _ => Err($"Unsupported manage_graph action: {action}")
        };
    }

    private ToolCallResult ManageUi(JsonElement a)
    {
        return Bridge("manage_ui", a);
    }

    private ToolCallResult ManageLocalization(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "key_add").ToLowerInvariant();
        if (action == "key_add" && !HasArg(a, "key"))
        {
            return Err("Missing key");
        }
        if (action == "tables")
        {
            return Bridge("manage_localization", InjectArg(a, "action", "tables"));
        }
        return Bridge("manage_localization", a);
    }

    private ToolCallResult ManageGraphics(JsonElement a)
    {
        return Bridge("manage_graphics", a);
    }

    private ToolCallResult ManageProfiler(JsonElement a)
    {
        return Bridge("manage_profiler", a);
    }

    private ToolCallResult ManageBuild(JsonElement a)
    {
        var action = (Opt(a, "action") ?? "profiles").ToLowerInvariant();
        return action switch
        {
            "profiles" => Bridge("manage_build", a),
            "scenes" => Bridge("manage_build", InjectArg(a, "action", "scenes")),
            _ => Err($"Unsupported manage_build action: {action}")
        };
    }

    private ToolCallResult RunTests(JsonElement a)
    {
        var mode = Opt(a, "mode") ?? "EditMode";
        var payload = InjectArg(a, "mode",
            string.Equals(mode, "PlayMode", StringComparison.OrdinalIgnoreCase) ? "PlayMode" :
            string.Equals(mode, "All", StringComparison.OrdinalIgnoreCase) ? "All" : "EditMode");
        return Bridge("run_tests", payload);
    }

    private ToolCallResult GetTestJob(JsonElement a)
    {
        return Bridge("get_test_job", a);
    }

    private string? ResolveRoot(JsonElement a) => Req(a, "projectRoot") ?? _defaultProjectRoot;

    private JsonElement InjectArg(JsonElement source, string key, string value)
    {
        JsonObject obj;
        if (source.ValueKind == JsonValueKind.Object)
        {
            obj = JsonNode.Parse(source.GetRawText()) as JsonObject ?? new JsonObject();
        }
        else
        {
            obj = new JsonObject();
        }
        obj[key] = value;
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string? Req(JsonElement a, string k)
    {
        if (a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString(); return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
    private static string? Opt(JsonElement a, string k)
    {
        if (a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString(); return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
    private static int? IntOpt(JsonElement a, string k)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
        return null;
    }
    private static bool BoolOpt(JsonElement a, string k)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(k, out var v)) return false;
        return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    }
    private static List<string> StrList(JsonElement a, string k)
    {
        var r = new List<string>();
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return r;
        foreach (var it in v.EnumerateArray()) if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString())) r.Add(it.GetString()!.Trim());
        return r;
    }
    private static bool HasArg(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v))
        {
            return false;
        }
        return v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined &&
               !(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()));
    }
    private static bool HasAnyArg(JsonElement a, params string[] keys) => keys.Any(k => HasArg(a, k));
    private static string BridgeRoot(string root) => Path.Combine(root, "Library", "XLabMcpBridge");
    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    private static string? InRoot(string root, string p)
    {
        var n = p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var f = Path.GetFullPath(Path.IsPathRooted(n) ? n : Path.Combine(root, n));
        var rr = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return f.StartsWith(rr, StringComparison.OrdinalIgnoreCase) ? f : null;
    }
    private static void CopyDirectory(string sourcePath, string targetPath)
    {
        var source = new DirectoryInfo(sourcePath);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourcePath}");
        }

        Directory.CreateDirectory(targetPath);
        foreach (var dir in source.GetDirectories("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source.FullName, dir.FullName);
            Directory.CreateDirectory(Path.Combine(targetPath, relative));
        }

        foreach (var file in source.GetFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source.FullName, file.FullName);
            var destination = Path.Combine(targetPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            file.CopyTo(destination, overwrite: true);
        }
    }
    private static (bool Success, string? SourcePath, string? TargetPath, string? Error) ResolveUnityMcpPackagePaths(string root, JsonElement a)
    {
        var sourceArg = Opt(a, "packageSourcePath");
        string? sourcePath = null;
        if (!string.IsNullOrWhiteSpace(sourceArg))
        {
            sourcePath = Path.GetFullPath(sourceArg);
        }
        else
        {
            sourcePath = ResolveUnityMcpPackageSourcePath();
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return (false, null, null, "Unity MCP package source path could not be resolved. Pass packageSourcePath.");
        }

        var packageJson = Path.Combine(sourcePath, "package.json");
        if (!Directory.Exists(sourcePath) || !File.Exists(packageJson))
        {
            return (false, null, null, $"Unity MCP package source not found: {sourcePath}");
        }

        var targetPath = Path.Combine(root, "Packages", UnityMcpPackageName);
        return (true, sourcePath, targetPath, null);
    }
    private static string? ResolveUnityMcpPackageSourcePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "unity", UnityMcpPackageName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "unity", UnityMcpPackageName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "unity", UnityMcpPackageName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dotnet-prototype", "unity", UnityMcpPackageName),
            Path.Combine(Directory.GetCurrentDirectory(), "unity", UnityMcpPackageName),
            Path.Combine(Directory.GetCurrentDirectory(), "dotnet-prototype", "unity", UnityMcpPackageName),
        };

        foreach (var candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }
        }

        return null;
    }
    private static string PackageLifecyclePayload(string action, string projectRoot, string? sourcePath, string targetPath, bool changed)
    {
        var packageVersion = "unknown";
        var versionSource = !string.IsNullOrWhiteSpace(sourcePath) ? Path.Combine(sourcePath, "package.json") : Path.Combine(targetPath, "package.json");
        if (File.Exists(versionSource))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(versionSource));
                packageVersion = doc.RootElement.TryGetProperty("version", out var versionValue)
                    ? versionValue.GetString() ?? "unknown"
                    : "unknown";
            }
            catch
            {
                packageVersion = "unknown";
            }
        }

        var payload = new JsonObject
        {
            ["success"] = true,
            ["tool"] = "manage_editor",
            ["action"] = action,
            ["packageName"] = UnityMcpPackageName,
            ["packageVersion"] = packageVersion,
            ["projectRoot"] = projectRoot,
            ["sourcePath"] = sourcePath,
            ["targetPath"] = targetPath,
            ["changed"] = changed,
            ["installed"] = action != "delete" || Directory.Exists(targetPath),
            ["message"] = $"manage_editor {action}: {UnityMcpPackageName}"
        };
        return payload.ToJsonString();
    }
    private static string Ident(string x)
    {
        var sb = new StringBuilder(); foreach (var c in x.Trim()) if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        var v = sb.Length == 0 ? "NewScript" : sb.ToString(); if (char.IsDigit(v[0])) v = "S_" + v; return v;
    }
    private static string SafeFile(string x)
    {
        var sb = new StringBuilder(); foreach (var c in x) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var v = sb.ToString().Trim('_'); return string.IsNullOrWhiteSpace(v) ? "feature" : v;
    }
    private static string ScriptTpl(string n, string? ns) =>
        string.IsNullOrWhiteSpace(ns)
            ? $"using UnityEngine;\n\npublic sealed class {n} : MonoBehaviour\n{{\n    void Start(){{}}\n    void Update(){{}}\n}}\n"
            : $"using UnityEngine;\n\nnamespace {ns};\n\npublic sealed class {n} : MonoBehaviour\n{{\n    void Start(){{}}\n    void Update(){{}}\n}}\n";

    private static ToolCallResult Ok(string m) => new(new List<TextContent> { new("text", m) });
    private static ToolCallResult Err(string m) => new(new List<TextContent> { new("text", m) }, IsError: true);

    private static Dictionary<string, JsonObject> BuildToolSchemas()
    {
        var contractPath = ResolveContractPath();
        if (contractPath == null)
        {
        throw new InvalidOperationException("xLabMcp contract file not found: contracts/xlabmcp-tools.schema.json");
        }

        var node = JsonNode.Parse(File.ReadAllText(contractPath)) as JsonObject;
        var tools = node?["tools"] as JsonObject;
        if (tools == null)
        {
            throw new InvalidOperationException($"Invalid contract file '{contractPath}': missing tools object.");
        }

        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var kv in tools)
        {
            if (kv.Value is JsonObject schema)
            {
                result[kv.Key] = schema;
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException($"Invalid contract file '{contractPath}': no tool schemas.");
        }
        return result;
    }

    private static string? ResolveContractPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "contracts", "xlabmcp-tools.schema.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "contracts", "xlabmcp-tools.schema.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "xlabmcp-tools.schema.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "contracts", "xlabmcp-tools.schema.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "dotnet-prototype", "contracts", "xlabmcp-tools.schema.json"),
        };

        foreach (var path in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}

public sealed class McpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false };
    private readonly McpRequestDispatcher _dispatcher;
    private readonly Stream _stdin;
    private readonly Stream _stdout;

    public McpServer(McpRequestDispatcher dispatcher) : this(dispatcher, Console.OpenStandardInput(), Console.OpenStandardOutput()) { }
    public McpServer(McpRequestDispatcher dispatcher, Stream stdin, Stream stdout) { _dispatcher = dispatcher; _stdin = stdin; _stdout = stdout; }

    public void Run()
    {
        while (TryReadMessage(out var req)) using (req) HandleRequest(req.RootElement);
    }

    private void HandleRequest(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var me)) return;
        var method = me.GetString() ?? string.Empty;
        if (!root.TryGetProperty("id", out var id)) return;
        try
        {
            switch (method)
            {
                case "initialize": WriteResult(id, _dispatcher.BuildInitializeResult()); break;
                case "tools/list": WriteResult(id, _dispatcher.BuildToolsList()); break;
                case "tools/call": WriteResult(id, _dispatcher.HandleToolCall(root)); break;
                default: WriteError(id, -32601, $"Method not found: {method}"); break;
            }
        }
        catch (Exception ex) { WriteError(id, -32603, ex.Message); }
    }

    private bool TryReadMessage(out JsonDocument req)
    {
        req = default!;
        var len = ReadContentLength(); if (len is null) return false;
        req = JsonDocument.Parse(ReadExactly(len.Value)); return true;
    }

    private int? ReadContentLength()
    {
        var len = -1;
        while (true)
        {
            var line = ReadLineAscii(); if (line is null) return null;
            if (line.Length == 0) break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var raw = line["Content-Length:".Length..].Trim();
                if (!int.TryParse(raw, out len)) throw new InvalidOperationException("Invalid Content-Length header");
            }
        }
        if (len < 0) throw new InvalidOperationException("Missing Content-Length header");
        return len;
    }

    private string? ReadLineAscii()
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var b = _stdin.ReadByte();
            if (b == -1) { if (ms.Length == 0) return null; break; }
            if (b == '\n') break;
            if (b != '\r') ms.WriteByte((byte)b);
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private byte[] ReadExactly(int count)
    {
        var buf = new byte[count]; var off = 0;
        while (off < count)
        {
            var r = _stdin.Read(buf, off, count - off);
            if (r <= 0) throw new EndOfStreamException("Unexpected EOF while reading JSON-RPC body");
            off += r;
        }
        return buf;
    }

    private void WriteResult(JsonElement id, object result) => WriteEnvelope(new { jsonrpc = "2.0", id, result });
    private void WriteError(JsonElement id, int code, string message) => WriteEnvelope(new { jsonrpc = "2.0", id, error = new { code, message } });

    private void WriteEnvelope(object envelope)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        _stdout.Write(header, 0, header.Length);
        _stdout.Write(payload, 0, payload.Length);
        _stdout.Flush();
    }
}
