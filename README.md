# xLabMcp

Clean repository for the XLab Unity MCP server prototype.

## What is active

- `dotnet-prototype/` - .NET MCP server, protocol contracts, Unity editor bridge package, and tests.
- `Docs/` - active server backlog and verification contract.
- `Docs/canonical_tools.md` - canonical MCP resources and tools list.
- `Docs/runtime_tools.md` - actual tool names exposed by the current server runtime.
- `Docs/Archive/` - historical planning notes and older prototype documentation.
- `mcp/` - prompts, policies, resources, and validators for the server-side workflow.

## Quick Start

```powershell
dotnet test .\dotnet-prototype\XLab.UnityMcp.sln -c Debug
```

## Documentation

- [Active docs index](Docs/README.md)
- [Workspace index](Docs/workspace_index.md)
- [Short docs index](Docs/index.md)
- [Project documentation map](Docs/project_documentation.md)
- [Short MCP index](mcp/index.md)
- [Archive index](Docs/Archive/README.md)
- [MCP contracts index](mcp/README.md)
