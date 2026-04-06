using System.Text.Json;
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
            "project_root.set","editor.state","asset.create_folder","asset.exists","console.read",
            "scene.create","scene.open","scene.save",
            "gameobject.create","gameobject.modify",
            "component.add","component.set",
            "script.create_or_edit","editor.compile_status","asset.refresh",
            "prefab.create","prefab.open","prefab.save","prefab.instantiate",
            "graph.open_or_create","graph.connect","graph.edit",
            "tests.run_editmode","tests.results",
            "screenshot.scene",
            "scriptableobject.create_or_edit",
            "playmode.enter","playmode.exit",
            "scene.validate_refs","prefab.validate","graph.validate","tests.run_all",
            "asset.list_modified","change.summary","project.docs_update",
            "ui.create_or_edit","localization.key_add",
        };

        foreach (var tool in expected)
        {
            Assert.Contains(result.Tools, t => t.Name == tool);
        }
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
    public void HandleToolCall_AssetCreateFolder_And_AssetExists_Work()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var createDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "asset.create_folder",
                "arguments": {
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Assets/NewFolder/Sub"
                }
              }
            }
            """);
            var createResult = _dispatcher.HandleToolCall(createDoc.RootElement);
            Assert.False(createResult.IsError);

            using var existsDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "asset.exists",
                "arguments": {
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Assets/NewFolder/Sub"
                }
              }
            }
            """);
            var existsResult = _dispatcher.HandleToolCall(existsDoc.RootElement);
            Assert.False(existsResult.IsError);
            Assert.Contains("exists=True", existsResult.Content[0].Text);
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
    public void HandleToolCall_ScriptCreateOrEdit_CreatesAndEditsScript()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Scripts"));

        try
        {
            using var createDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "script.create_or_edit",
                "arguments": {
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "scriptName": "MissionStateService"
                }
              }
            }
            """);
            var createResult = _dispatcher.HandleToolCall(createDoc.RootElement);
            Assert.False(createResult.IsError);

            var path = Path.Combine(root, "Assets", "Scripts", "MissionStateService.cs");
            Assert.True(File.Exists(path));

            using var editDoc = JsonDocument.Parse($$"""
            {
              "params": {
                "name": "script.create_or_edit",
                "arguments": {
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
                  "path": "Assets/Scripts/MissionStateService.cs",
                  "mode": "append",
                  "text": "\n// appended"
                }
              }
            }
            """);
            var editResult = _dispatcher.HandleToolCall(editDoc.RootElement);
            Assert.False(editResult.IsError);
            Assert.Contains("appended", File.ReadAllText(path));
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

    [Fact]
    public void HandleToolCall_SceneCreate_QueuesBridgeCommand()
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var json = $$"""
            {
              "params": {
                "name": "scene.create",
                "arguments": {
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

    [Theory]
    [InlineData("build_settings_scenes")]
    [InlineData("editor.state")]
    [InlineData("scene.open")]
    [InlineData("scene.save")]
    [InlineData("hierarchy.list")]
    [InlineData("hierarchy.find")]
    [InlineData("gameobject.create")]
    [InlineData("gameobject.modify")]
    [InlineData("prefab.create")]
    [InlineData("prefab.open")]
    [InlineData("prefab.save")]
    [InlineData("prefab.instantiate")]
    [InlineData("graph.open_or_create")]
    [InlineData("graph.connect")]
    [InlineData("graph.edit")]
    [InlineData("graph.validate")]
    [InlineData("component.add")]
    [InlineData("component.set")]
    [InlineData("editor.compile_status")]
    [InlineData("asset.refresh")]
    [InlineData("console.read")]
    [InlineData("screenshot.scene")]
    [InlineData("screenshot.game")]
    [InlineData("tests.run_editmode")]
    [InlineData("tests.run_all")]
    [InlineData("tests.results")]
    [InlineData("playmode.enter")]
    [InlineData("playmode.exit")]
    [InlineData("scene.validate_refs")]
    [InlineData("prefab.validate")]
    [InlineData("scriptableobject.create_or_edit")]
    [InlineData("ui.create_or_edit")]
    [InlineData("localization.key_add")]
    [InlineData("asset.list_modified")]
    [InlineData("change.summary")]
    [InlineData("project.docs_update")]
    public void HandleToolCall_BridgeTools_QueueCommand(string toolName)
    {
        var root = Path.Combine(Path.GetTempPath(), "xlab-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var json = $$"""
            {
              "params": {
                "name": "{{toolName}}",
                "arguments": {
                  "projectRoot": "{{root.Replace("\\", "\\\\")}}",
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
}
