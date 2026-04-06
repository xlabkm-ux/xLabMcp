using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XLab.UnityMcp.Protocol;

var server = new McpServer(new McpRequestDispatcher());
server.Run();

public sealed class McpRequestDispatcher
{
    private const string Urp = "com.unity.render-pipelines.universal";
    private string? _defaultProjectRoot;

    public InitializeResult BuildInitializeResult() => new(
        McpProtocol.Version,
        new ServerInfo("XLab.UnityMcp.Server", "0.3.0"),
        new { tools = new { listChanged = false } });

    public ToolsListResult BuildToolsList()
    {
        var names = new[]
        {
            "project_root.set","project.info","editor.state","project.health_check",
            "asset.create_folder","asset.exists","asset.refresh","asset.list_modified",
            "scene.create","scene.open","scene.save","scene.validate_refs",
            "hierarchy.list","hierarchy.find",
            "gameobject.create","gameobject.modify",
            "component.add","component.set",
            "prefab.create","prefab.open","prefab.save","prefab.instantiate","prefab.validate",
            "script.create_or_edit","scriptableobject.create_or_edit",
            "graph.open_or_create","graph.connect","graph.edit","graph.validate",
            "editor.compile_status","console.read",
            "screenshot.scene","screenshot.game",
            "tests.run_editmode","tests.run_all","tests.results",
            "build_settings_scenes",
            "playmode.enter","playmode.exit",
            "ui.create_or_edit","localization.key_add",
            "change.summary","project.docs_update"
        };

        var tools = names.Select(n => new ToolDefinition(n, n.Replace('_', ' '), new JsonObject { ["type"] = "object" })).ToList();
        return new ToolsListResult(tools);
    }

    public ToolCallResult HandleToolCall(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("name", out var n)) return Err("Missing params.name");
        var name = n.GetString() ?? string.Empty;
        var a = p.TryGetProperty("arguments", out var args) ? args : default;

        return name switch
        {
            "project_root.set" => ProjectRootSet(a),
            "project.info" => ProjectInfo(a),
            "editor.state" => Bridge("editor.state", a),
            "project.health_check" => Health(a),

            "scene.create" => Bridge("scene.create", a),
            "scene.open" => Bridge("scene.open", a),
            "scene.save" => Bridge("scene.save", a),
            "hierarchy.list" => Bridge("hierarchy.list", a),
            "hierarchy.find" => Bridge("hierarchy.find", a),
            "gameobject.create" => Bridge("gameobject.create", a),
            "gameobject.modify" => Bridge("gameobject.modify", a),
            "component.add" => Bridge("component.add", a),
            "component.set" => Bridge("component.set", a),
            "prefab.create" => Bridge("prefab.create", a),
            "prefab.instantiate" => Bridge("prefab.instantiate", a),
            "prefab.open" => Bridge("prefab.open", a),
            "prefab.save" => Bridge("prefab.save", a),
            "editor.compile_status" => Bridge("editor.compile_status", a),
            "playmode.enter" => Bridge("playmode.enter", a),
            "playmode.exit" => Bridge("playmode.exit", a),
            "console.read" => Bridge("console.read", a),
            "screenshot.scene" => Bridge("screenshot.scene", a),
            "screenshot.game" => Bridge("screenshot.game", a),
            "tests.run_editmode" => Bridge("tests.run_editmode", InjectArg(a, "mode", "EditMode")),
            "tests.run_all" => Bridge("tests.run_all", InjectArg(a, "mode", "All")),
            "asset.refresh" => Bridge("asset.refresh", a),
            "build_settings_scenes" => Bridge("build_settings_scenes", a),

            "script.create_or_edit" => ScriptCreateOrEdit(a),
            "asset.create_folder" => AssetCreateFolder(a),
            "asset.exists" => AssetExists(a),
            "tests.results" => Bridge("tests.results", a),
            "graph.open_or_create" => Bridge("graph.open_or_create", a),
            "graph.connect" => Bridge("graph.connect", a),
            "graph.edit" => Bridge("graph.edit", a),
            "graph.validate" => Bridge("graph.validate", a),
            "scene.validate_refs" => SceneValidateRefs(a),
            "prefab.validate" => PrefabValidate(a),
            "scriptableobject.create_or_edit" => ScriptableObjectCreateOrEdit(a),
            "ui.create_or_edit" => UiCreateOrEdit(a),
            "localization.key_add" => LocalizationKeyAdd(a),
            "asset.list_modified" => AssetListModified(a),
            "change.summary" => ChangeSummary(a),
            "project.docs_update" => ProjectDocsUpdate(a),
            _ => Err($"Unknown tool: {name}")
        };
    }

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
        var obj = new JsonObject { ["id"] = id, ["command"] = cmd, ["arguments"] = JsonNode.Parse(a.GetRawText()), ["createdAtUtc"] = DateTime.UtcNow.ToString("O") };
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
        if (!File.Exists(path)) return Ok("tests.results: no test-results.json yet");
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
        return Ok($"graph.open_or_create: {path}");
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
        return Ok($"graph.connect appended: {path}");
    }

    private ToolCallResult GraphEdit(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var name = Req(a, "graphName") ?? "DefaultGraph";
        var path = Path.Combine(root, "Assets", "Graphs", SafeFile(name) + ".json");
        if (!File.Exists(path)) return Err($"Graph not found: {path}");
        var patch = Opt(a, "patch") ?? Opt(a, "text") ?? "// graph edit\n";
        File.AppendAllText(path, patch + Environment.NewLine, Encoding.UTF8);
        return Ok($"graph.edit applied: {path}");
    }

    private ToolCallResult GraphValidate(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var name = Req(a, "graphName") ?? "DefaultGraph";
        var path = Path.Combine(root, "Assets", "Graphs", SafeFile(name) + ".json");
        if (!File.Exists(path)) return Err($"Graph not found: {path}");
        var text = File.ReadAllText(path);
        return Ok($"graph.validate: exists=true; size={text.Length}");
    }

    private ToolCallResult SceneValidateRefs(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var rel = Req(a, "scenePath");
        if (rel is null) return Err("Missing scenePath");
        var path = InRoot(root, rel); if (path is null) return Err("Path escapes project root");
        if (!File.Exists(path)) return Err($"Scene not found: {path}");
        var text = File.ReadAllText(path);
        return Ok($"scene.validate_refs: exists=true; bytes={text.Length}");
    }

    private ToolCallResult PrefabValidate(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var rel = Req(a, "prefabPath");
        if (rel is null) return Err("Missing prefabPath");
        var path = InRoot(root, rel); if (path is null) return Err("Path escapes project root");
        var ok = File.Exists(path);
        return ok ? Ok($"prefab.validate: ok {path}") : Err($"Prefab not found: {path}");
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
            $"[CreateAssetMenu(menuName = \"Breach/{Ident(name)}\")]\n" +
            $"public sealed class {Ident(name)} : ScriptableObject {{ }}\n";
        File.WriteAllText(path, content, Encoding.UTF8);
        return Ok($"scriptableobject.create_or_edit: {path}");
    }

    private ToolCallResult UiCreateOrEdit(JsonElement a)
    {
        var root = ResolveRoot(a); if (root is null) return Err("Missing projectRoot");
        var dir = Path.Combine(root, "Assets", "UI");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "UIPlan.codex.md");
        var body = Opt(a, "text") ?? "- HUD Root\n- Result Panel\n- Objective Tracker\n";
        File.WriteAllText(path, body, Encoding.UTF8);
        return Ok($"ui.create_or_edit: {path}");
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
        return Ok($"localization.key_add: {key}");
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
        if (!Directory.Exists(Path.Combine(gitDir, ".git"))) return Ok("change.summary: no git repo");
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
            if (proc == null) return Err("change.summary: failed to start git");
            var outText = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return Ok(string.IsNullOrWhiteSpace(outText) ? "clean" : outText.TrimEnd());
        }
        catch (Exception ex)
        {
            return Err($"change.summary error: {ex.Message}");
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
        return Ok($"project.docs_update: {path}");
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
    private static string BridgeRoot(string root) => Path.Combine(root, "Library", "XLabMcpBridge");
    private static string? InRoot(string root, string p)
    {
        var n = p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var f = Path.GetFullPath(Path.IsPathRooted(n) ? n : Path.Combine(root, n));
        var rr = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return f.StartsWith(rr, StringComparison.OrdinalIgnoreCase) ? f : null;
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
