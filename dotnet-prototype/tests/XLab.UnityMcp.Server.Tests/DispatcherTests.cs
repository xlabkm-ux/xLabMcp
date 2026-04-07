using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using XLab.UnityMcp.Protocol;

namespace XLab.UnityMcp.Server.Tests;

public sealed class DispatcherTests
{
    private readonly McpRequestDispatcher _dispatcher = new();

    [Fact]
    public void BuildInitializeResult_ReturnsProtocolAndServerInfo()
    {
        var result = _dispatcher.BuildInitializeResult();

        Assert.Equal(McpProtocol.Version, result.ProtocolVersion);
        Assert.Equal("XLab.UnityMcp.Server", result.ServerInfo.Name);
    }

    [Fact]
    public void BuildToolsList_ContainsExpectedTools()
    {
        var result = _dispatcher.BuildToolsList();
        var expected = new[]
        {
            "project_root.set","project.info","project.health_check","project.capabilities","editor.state","read_console",
            "manage_asset","manage_hierarchy","manage_scene","manage_gameobject","manage_components",
            "manage_script","manage_scriptableobject","manage_prefabs","manage_graph","manage_ui","manage_localization",
            "manage_editor","manage_input","manage_camera","manage_graphics","manage_profiler","manage_build","run_tests","get_test_job",
        };

        foreach (var tool in expected)
        {
            Assert.Contains(result.Tools, t => t.Name == tool);
        }
    }

    [Fact]
    public void BuildToolsList_MatchesContractToolSet()
    {
        var result = _dispatcher.BuildToolsList();
        var runtime = result.Tools.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var expected = new[]
        {
            "project_root.set","project.info","project.health_check","project.capabilities","editor.state","read_console",
            "manage_asset","manage_hierarchy","manage_scene","manage_gameobject","manage_components",
            "manage_script","manage_scriptableobject","manage_prefabs","manage_graph","manage_ui","manage_localization",
            "manage_editor","manage_input","manage_camera","manage_graphics","manage_profiler","manage_build","run_tests","get_test_job",
        }.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, runtime);
    }

    [Fact]
    public void HandleToolCall_ProjectRootSet_AllowsSubsequentAliasCallsWithoutProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var setDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "project_root.set",
                "arguments": { "projectRoot": "{{root.Replace("\\", "\\\\")}}" }
              }
            }
            """);
            var setResult = _dispatcher.HandleToolCall(setDoc.RootElement);
            Assert.False(setResult.IsError);

            using var stateDoc = JsonDocument.Parse("""
            {
              "params": { "name": "editor.state", "arguments": { "waitMs": 0 } }
            }
            """);
            var stateResult = _dispatcher.HandleToolCall(stateDoc.RootElement);
            Assert.False(stateResult.IsError);
            Assert.Contains("queued:editor.state", stateResult.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageAsset_CreateFolder_And_AssetExists_Work()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var createDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_asset",
                "arguments": {
                  "action": "create_folder",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Assets/NewFolder/Sub"
                }
              }
            }
            """);
            var createResult = _dispatcher.HandleToolCall(createDoc.RootElement);
            Assert.False(createResult.IsError);
            Assert.Contains("queued:manage_asset", createResult.Content[0].Text);

            using var existsDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_asset",
                "arguments": {
                  "action": "exists",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Assets/NewFolder/Sub"
                }
              }
            }
            """);
            var existsResult = _dispatcher.HandleToolCall(existsDoc.RootElement);
            Assert.False(existsResult.IsError);
            Assert.Contains("queued:manage_asset", existsResult.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageScript_CreateOrEdit_CreatesAndEditsScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Scripts"));

        try
        {
            using var createDoc = JsonDocument.Parse($$"""
            {
                "params": {
                "name": "manage_script",
                "arguments": {
                  "action": "create_or_edit",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "scriptName": "MissionStateService",
                  "waitMs": 0
                }
              }
            }
            """);
            var createResult = _dispatcher.HandleToolCall(createDoc.RootElement);
            Assert.False(createResult.IsError);
            Assert.Contains("queued:manage_script", createResult.Content[0].Text);

            using var editDoc = JsonDocument.Parse($$"""
            {
                "params": {
                "name": "manage_script",
                "arguments": {
                  "action": "create_or_edit",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Assets/Scripts/MissionStateService.cs",
                  "mode": "append",
                  "text": "\n// appended",
                  "waitMs": 0
                }
              }
            }
            """);
            var editResult = _dispatcher.HandleToolCall(editDoc.RootElement);
            Assert.False(editResult.IsError);
            Assert.Contains("queued:manage_script", editResult.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ProjectHealthCheck_MissingProjectRoot_ReturnsError()
    {
        using var doc = JsonDocument.Parse("""
            {
              "params": {
                "name": "project.health_check",
                "arguments": {}
              }
            }
            """);

        var result = _dispatcher.HandleToolCall(doc.RootElement);

        Assert.True(result.IsError);
        Assert.Contains("projectRoot", result.Content[0].Text);
    }


    [Fact]
    public void HandleToolCall_UnknownTool_ReturnsError()
    {
        using var doc = JsonDocument.Parse("""
            {
              "params": { "name": "does_not_exist" }
            }
            """);

        var result = _dispatcher.HandleToolCall(doc.RootElement);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Content[0].Text);
    }

    [Theory]
    [InlineData("unknown.tool.01")]
    [InlineData("unknown.tool.02")]
    [InlineData("unknown.tool.03")]
    [InlineData("unknown.tool.04")]
    [InlineData("unknown.tool.05")]
    [InlineData("unknown.tool.06")]
    [InlineData("unknown.tool.07")]
    [InlineData("unknown.tool.08")]
    [InlineData("unknown.tool.09")]
    [InlineData("unknown.tool.10")]
    [InlineData("unknown.tool.11")]
    [InlineData("unknown.tool.12")]
    [InlineData("unknown.tool.13")]
    [InlineData("unknown.tool.14")]
    [InlineData("unknown.tool.15")]
    [InlineData("unknown.tool.16")]
    [InlineData("unknown.tool.17")]
    [InlineData("unknown.tool.18")]
    [InlineData("unknown.tool.19")]
    [InlineData("unknown.tool.20")]
    [InlineData("unknown.tool.21")]
    [InlineData("unknown.tool.22")]
    [InlineData("unknown.tool.23")]
    [InlineData("unknown.tool.24")]
    [InlineData("unknown.tool.25")]
    [InlineData("unknown.tool.26")]
    [InlineData("unknown.tool.27")]
    [InlineData("unknown.tool.28")]
    [InlineData("unknown.tool.29")]
    [InlineData("unknown.tool.30")]
    [InlineData("unknown.tool.31")]
    [InlineData("unknown.tool.32")]
    [InlineData("unknown.tool.33")]
    [InlineData("unknown.tool.34")]
    [InlineData("unknown.tool.35")]
    [InlineData("unknown.tool.36")]
    [InlineData("unknown.tool.37")]
    public void HandleToolCall_UnsupportedToolNames_ReturnUnknownTool(string toolName)
    {
        using var doc = JsonDocument.Parse($$"""
            {
              "params": { "name": "{{toolName}}" }
            }
            """);

        var result = _dispatcher.HandleToolCall(doc.RootElement);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Content[0].Text);
    }

    [Fact]
    public void HandleToolCall_ManageScene_Create_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var json = $$"""
            {
              "params": {
                "name": "manage_scene",
                "arguments": {
                  "action": "create",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "sceneName": "MainScene",
                  "waitMs": 0
                }
              }
            }
            """;

            using var doc = JsonDocument.Parse(json);
            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageAsset_ReadWriteTextFile_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var writeDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_asset",
                "arguments": {
                  "action": "write_text_file",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Library/McpDiagnostics/mission_save_v1.json",
                  "contents": "{ \"schemaVersion\": 999 }",
                  "waitMs": 0
                }
              }
            }
            """);
            var writeResult = _dispatcher.HandleToolCall(writeDoc.RootElement);
            Assert.False(writeResult.IsError);
            Assert.Contains("queued:manage_asset", writeResult.Content[0].Text);

            using var readDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_asset",
                "arguments": {
                  "action": "read_text_file",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Library/McpDiagnostics/mission_save_v1.json",
                  "waitMs": 0
                }
              }
            }
            """);
            var readResult = _dispatcher.HandleToolCall(readDoc.RootElement);
            Assert.False(readResult.IsError);
            Assert.Contains("queued:manage_asset", readResult.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageGameObject_InvokeMethod_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_gameobject",
                "arguments": {
                  "action": "invoke_method",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "targetPath": "MissionDirector",
                  "component_type": "SaveService",
                  "method": "SaveNow",
                  "arguments": [],
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_gameobject", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageComponents_GetSerialized_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_components",
                "arguments": {
                  "action": "get_serialized",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "targetPath": "MissionDirector",
                  "component_type": "ObjectiveService",
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_components", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageComponents_SetSerialized_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_components",
                "arguments": {
                  "action": "set_serialized",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "targetPath": "MissionDirector",
                  "component_type": "ObjectiveService",
                  "properties": {
                    "hostageFreed": true,
                    "hostageExtracted": false
                  },
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_components", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageLocalization_Tables_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_localization",
                "arguments": {
                  "action": "tables",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_localization", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageAsset_ListLocalizationKeys_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_asset",
                "arguments": {
                  "action": "list_localization_keys",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "table": "DefaultLocalizationTable",
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_asset", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageAsset_ResolveLocalizationKeys_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_asset",
                "arguments": {
                  "action": "resolve_localization_keys",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "table": "DefaultLocalizationTable",
                  "locale": "ru",
                  "keys": ["ui.result.success.title", "ui.result.fail.title", "hud.controls"],
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_asset", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageBuild_GetActive_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_build",
                "arguments": {
                  "action": "profiles",
                  "mode": "get_active",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_build", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ManageBuild_SetActive_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "manage_build",
                "arguments": {
                  "action": "profiles",
                  "mode": "set_active",
                  "profile": "Android",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "waitMs": 0
                }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains("queued:manage_build", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("project.capabilities")]
    [InlineData("editor.state")]
    [InlineData("read_console")]
    [InlineData("manage_scene")]
    [InlineData("manage_prefabs")]
    [InlineData("manage_editor")]
    [InlineData("manage_input")]
    [InlineData("manage_camera")]
    [InlineData("manage_gameobject")]
    [InlineData("manage_components")]
    [InlineData("manage_asset")]
    [InlineData("manage_graphics")]
    [InlineData("manage_profiler")]
    [InlineData("manage_build")]
    [InlineData("run_tests")]
    [InlineData("get_test_job")]
    [InlineData("manage_hierarchy")]
    [InlineData("manage_script")]
    [InlineData("manage_scriptableobject")]
    [InlineData("manage_ui")]
    [InlineData("manage_localization")]
    public void HandleToolCall_BridgeTools_QueueCommand(string toolName)
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var extra = toolName switch
            {
                "read_console" => "\"count\": 20,",
                "project.capabilities" => "",
                "manage_scene" => "\"action\": \"create\", \"sceneName\": \"MainScene\",",
                "manage_prefabs" => "\"action\": \"create\", \"sourceObjectPath\": \"Operative_Player_A_Source\", \"prefabPath\": \"Assets/Prefabs/OperativeA.prefab\",",
                "manage_editor" => "\"action\": \"play_mode\", \"mode\": \"status\",",
                "manage_input" => "\"action\": \"send\", \"keys\": [\"W\"], \"duration_ms\": 0,",
                "manage_camera" => "\"action\": \"screenshot\", \"capture_source\": \"scene_view\",",
                "manage_gameobject" => "\"action\": \"modify\", \"targetPath\": \"Root/Obj\", \"operation\": \"rename\",",
                "manage_components" => "\"action\": \"add\", \"targetPath\": \"Root/Obj\", \"componentType\": \"BoxCollider\",",
                "manage_asset" => "\"action\": \"refresh\", \"path\": \"Assets/Placeholder.txt\",",
                "manage_graphics" => "\"action\": \"set_quality_level\", \"quality_level\": \"Low\",",
                "manage_profiler" => "\"action\": \"get_counters\",",
                "manage_build" => "\"action\": \"profiles\", \"mode\": \"get_active\",",
                "run_tests" => "\"mode\": \"EditMode\",",
                "get_test_job" => "\"jobId\": \"job-123\",",
                "manage_hierarchy" => "\"action\": \"find\", \"query\": \"Operative\",",
                "manage_script" => "\"action\": \"create_or_edit\", \"scriptName\": \"MissionStateService\",",
                "manage_scriptableobject" => "\"action\": \"create_or_edit\", \"name\": \"MissionConfig\",",
                "manage_ui" => "\"action\": \"create_or_edit\", \"name\": \"MissionHud\",",
                "manage_localization" => "\"action\": \"key_add\", \"key\": \"ui.mission.start\",",
                _ => ""
            };

            var json = $$"""
            {
              "params": {
                "name": "{{toolName}}",
                "arguments": {
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  {{extra}}
                  "waitMs": 0
                }
              }
            }
            """;

            using var doc = JsonDocument.Parse(json);
            var result = _dispatcher.HandleToolCall(doc.RootElement);

            var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
            Assert.False(result.IsError);
            Assert.Contains($"queued:{toolName}", result.Content[0].Text);
            Assert.True(Directory.Exists(cmdDir));
            Assert.NotEmpty(Directory.GetFiles(cmdDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ProjectInfo_ReturnsProjectMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "project.info",
                "arguments": { "projectRoot": "{{root.Replace("\\", "\\\\")}}" }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            Assert.False(result.IsError);
            using var payload = JsonDocument.Parse(result.Content[0].Text);
            Assert.Equal(root, payload.RootElement.GetProperty("projectRoot").GetString());
            Assert.Equal(Path.GetFileName(root), payload.RootElement.GetProperty("projectName").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ProjectHealthCheck_ReturnsHealthSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{\"dependencies\":{}}");

        try
        {
            using var doc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "project.health_check",
                "arguments": { "projectRoot": "{{root.Replace("\\", "\\\\")}}" }
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);

            Assert.False(result.IsError);
            Assert.Contains("health=", result.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_BridgeToolWithoutArguments_DoesNotCrash()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var setDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "project_root.set",
                "arguments": { "projectRoot": "{{root.Replace("\\", "\\\\")}}" }
              }
            }
            """);
            var setResult = _dispatcher.HandleToolCall(setDoc.RootElement);
            Assert.False(setResult.IsError);

            using var doc = JsonDocument.Parse("""
            {
              "params": {
                "name": "editor.state"
              }
            }
            """);

            var result = _dispatcher.HandleToolCall(doc.RootElement);
            Assert.False(result.IsError);
            Assert.Contains("queued:editor.state", result.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void HandleToolCall_ValidationError_ForMissingRequiredProperty()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var json = $$"""
            {
              "params": {
                "name": "manage_localization",
                "arguments": {
                  "action": "key_add",
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "waitMs": 0
                }
              }
            }
            """;
            using var doc = JsonDocument.Parse(json);
            var result = _dispatcher.HandleToolCall(doc.RootElement);
            Assert.True(result.IsError);
            Assert.Contains("Missing key", result.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
    [Fact]
    public async Task HandleToolCall_BridgeRoundtrip_ReturnsBridgeResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var setDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "project_root.set",
                "arguments": { "projectRoot": "{{root.Replace("\\", "\\\\")}}" }
              }
            }
            """);
            var setResult = _dispatcher.HandleToolCall(setDoc.RootElement);
            Assert.False(setResult.IsError);

            using var callDoc = JsonDocument.Parse("""
            {
              "params": {
                "name": "editor.state",
                "arguments": {
                  "waitMs": 1200
                }
              }
            }
            """);

            var bridgeTask = Task.Run(async () =>
            {
                var cmdDir = Path.Combine(root, "Library", "XLabMcpBridge", "commands");
                var rspDir = Path.Combine(root, "Library", "XLabMcpBridge", "responses");
                Directory.CreateDirectory(cmdDir);
                Directory.CreateDirectory(rspDir);

                string? cmdFile = null;
                for (var i = 0; i < 30 && cmdFile == null; i++)
                {
                    cmdFile = Directory.GetFiles(cmdDir, "*.json").FirstOrDefault();
                    if (cmdFile == null) await Task.Delay(40);
                }
                Assert.NotNull(cmdFile);

                var payload = JsonNode.Parse(File.ReadAllText(cmdFile!)) as JsonObject;
                Assert.NotNull(payload);
                var id = payload!["id"]?.GetValue<string>();
                Assert.False(string.IsNullOrWhiteSpace(id));

                var response = new JsonObject
                {
                    ["id"] = id,
                    ["success"] = true,
                    ["message"] = "bridge-ok"
                };
                var responsePath = Path.Combine(rspDir, Path.GetFileName(cmdFile!));
                File.WriteAllText(responsePath, response.ToJsonString());
            });

            var result = _dispatcher.HandleToolCall(callDoc.RootElement);
            await bridgeTask;

            Assert.False(result.IsError);
            Assert.Contains("bridge-ok", result.Content[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}



