using System.Text.Json.Nodes;

namespace XLab.UnityMcp.Protocol;

public static class McpProtocol
{
    public const string Version = "2025-03-26";
}

public sealed record ServerInfo(string Name, string Version);

public sealed record InitializeResult(string ProtocolVersion, ServerInfo ServerInfo, object Capabilities);

public sealed record TextContent(string Type, string Text);

public sealed record ToolCallResult(List<TextContent> Content, bool IsError = false);

public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema);

public sealed record ToolsListResult(List<ToolDefinition> Tools);
