# xLabMcp Project Documentation

Date: 2026-04-07
Project: `xLabMcp`

This document is the single map for the project documentation set.
It explains what each document covers, how to read it, and how the runtime
and server-side workflow are supposed to be used.

## 1. Documentation Set

### Core entry points

- [README.md](../README.md)
  - Project landing page
  - High-level orientation
  - Links to docs and workflow entry points

- [Docs/README.md](README.md)
  - Index of the active documentation
  - Read-first map for server-side work

- [mcp/README.md](../mcp/README.md)
  - Index of MCP contracts, prompts, policies, resources, and validators

### Canonical contract documents

- [Docs/canonical_tools.md](canonical_tools.md)
  - Target contract for the tool and resource naming model
  - Canonical names only

- [Docs/runtime_tools.md](runtime_tools.md)
  - What the current runtime actually exposes today
  - Useful for checking drift between code and target contract

- [Docs/xlab_mcp_verification_contract.md](xlab_mcp_verification_contract.md)
  - Required resources and tools for verification
  - Machine-readable expectations for the bridge and server

- [Docs/xlab_mcp_server_backlog.md](xlab_mcp_server_backlog.md)
  - Remaining server-side work items
  - Priorities, dependencies, and completion status

### MCP workflow assets

- [mcp/prompts/](../mcp/prompts)
  - Reusable prompt templates for safe editing and release checks

- [mcp/policies/](../mcp/policies)
  - Safety and mutation policies

- [mcp/resources/](../mcp/resources)
  - Scenario recipes, release matrices, rules, and smoke workflows

- [mcp/validators/](../mcp/validators)
  - Rule matrix for validating tool and workflow coverage

### Historical archive

- [Docs/Archive/README.md](Archive/README.md)
  - Historical notes and removed prototype docs

## 2. Recommended Reading Order

1. [README.md](../README.md)
2. [Docs/README.md](README.md)
3. [Docs/canonical_tools.md](canonical_tools.md)
4. [Docs/runtime_tools.md](runtime_tools.md)
5. [Docs/xlab_mcp_verification_contract.md](xlab_mcp_verification_contract.md)
6. [Docs/xlab_mcp_server_backlog.md](xlab_mcp_server_backlog.md)
7. [mcp/README.md](../mcp/README.md)
8. [Docs/Archive/README.md](Archive/README.md) only if you need history

## 3. Runtime Snapshot

The current runtime is target-only and already exposes the operational surface
needed for verification.

### Base project and editor state

- `project_root.set`
- `project.info`
- `project.health_check`
- `project.capabilities`
- `editor.state`
- `read_console`

### Content and Unity editing

- `manage_asset`
- `manage_hierarchy`
- `manage_scene`
- `manage_gameobject`
- `manage_components`
- `manage_script`
- `manage_scriptableobject`
- `manage_prefabs`
- `manage_graph`
- `manage_ui`
- `manage_localization`

### Runtime control and verification

- `manage_editor`
- `manage_input`
- `manage_camera`
- `manage_graphics`
- `manage_profiler`
- `manage_build`
- `run_tests`
- `get_test_job`

### Diagnostics and safety

- Controlled diagnostics file access is limited to `Library/McpDiagnostics`
- Health payloads expose heartbeat, queue depth, audit trail, and screenshot
  indexing status
- Screenshot artifacts are indexed under `Library/XLabMcpBridge/screenshots`
- `manage_editor` also owns Unity MCP package lifecycle operations:
  `install`, `update`, and `delete` for the embedded `Packages/com.xlabkm.unity-mcp` package

## 4. Verification Workflow

Use this order when driving xLabMcp:

1. Check `project.health_check`.
2. Read `project.capabilities`.
3. Read `editor.state`.
4. Validate scenes and prefabs.
5. Enter Play Mode and send input if needed.
6. Run tests and poll `get_test_job`.
7. Capture screenshots for visual verification.
8. Check profiler/build/quality information when testing platform behavior.
9. Use controlled diagnostics file actions only for save/load resilience runs.

## 5. Documentation Composition

The project documentation should stay split by responsibility:

### A. Orientation

- `README.md`
- `Docs/README.md`

Purpose:
- explain the repo at a glance
- provide entry points for humans and agents

### B. Contract

- `Docs/canonical_tools.md`
- `Docs/runtime_tools.md`
- `Docs/xlab_mcp_verification_contract.md`

Purpose:
- define what the runtime should do
- show what it actually does now
- document the required verification payloads

### C. Delivery state

- `Docs/xlab_mcp_server_backlog.md`

Purpose:
- track what is done and what remains
- keep the delivery order explicit

### D. Workflow assets

- `mcp/prompts/*`
- `mcp/policies/*`
- `mcp/resources/*`
- `mcp/validators/*`

Purpose:
- capture reusable recipes, guardrails, and release checks

### E. History

- `Docs/Archive/*`

Purpose:
- preserve old prototype notes without mixing them into active guidance

## 6. Status of MCP Server Work

The server backlog is complete:

- `XLAB-001` to `XLAB-021` are marked `completed`
- the active runtime inventory is target-only
- the bridge includes capability, health, audit, diagnostics, screenshot
  indexing, localization validation, build, profiler, save, schema
  validation, quality-profile validation, change-risk workflows, and
  server-side Unity MCP package lifecycle management

## 7. Maintenance Rules

1. Keep active docs in `Docs/`.
2. Keep reusable prompt and workflow assets in `mcp/`.
3. Move obsolete notes into `Docs/Archive/` instead of deleting history.
4. Keep `canonical_tools.md` and `runtime_tools.md` in sync with the code.
5. Update `xlab_mcp_server_backlog.md` when implementation status changes.
6. Update `xlab_mcp_verification_contract.md` whenever runtime payload fields
   change.
7. Keep `bin/` and `obj/` out of version control.

## 8. Suggested Next Document

If this repo grows further, the next useful document would be a short
`Docs/verification_runbook.md` that shows:

- the exact command order for a verification session
- the expected artifacts and where they are written
- common failure states and the right recovery action
