# xLabMcp

Clean repository for the XLab Unity MCP server prototype.

## What is active

- `dotnet-prototype/` - .NET MCP server, protocol contracts, Unity editor bridge package, and tests.
- `Docs/` - active server backlog and verification contract.
- `Docs/canonical_tools.md` - canonical MCP resources and tools list.
- `Docs/Archive/` - historical planning notes and older prototype documentation.
- `mcp/` - prompts, policies, resources, and validators for the server-side workflow.

## Quick Start

```powershell
dotnet test .\dotnet-prototype\XLab.UnityMcp.sln -c Debug
```

## Documentation

- [Active docs index](Docs/README.md)
- [Archive index](Docs/Archive/README.md)
- [MCP contracts index](mcp/README.md)
