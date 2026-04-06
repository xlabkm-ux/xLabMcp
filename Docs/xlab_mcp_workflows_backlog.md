# xLabMcp Workflows Backlog

Date: 2026-04-06
Product: `xLabMcp`
Scope: MCP server + Unity bridge workflows for developing, validating, and operating the automation layer.

## Goal

Organize `xLabMcp` work as product workflows instead of a flat tool list, so server development follows the real operating lifecycle:

- connect to Unity
- inspect and mutate safely
- verify deterministically
- recover reliably
- scale to reusable workflows

This backlog complements:

- [xlab_mcp_server_backlog.md](E:/ИГРЫ/BREACH/Docs/xlab_mcp_server_backlog.md)
- [unity_mcp_verification_contract_41_48.md](E:/ИГРЫ/BREACH/Docs/unity_mcp_verification_contract_41_48.md)

## Workflow Map

1. `Connection & Preflight`
2. `Scene & Prefab Validation`
3. `Play Scenario Automation`
4. `Test Automation`
5. `Localization & Save Verification`
6. `Platform & Performance Verification`
7. `Diagnostics & Recovery`
8. `Workflow Productization`

## Priority Model

- `P0` critical blocker
- `P1` required for full autonomous verification
- `P2` robustness/productivity
- `P3` expansion

## Complexity Model

- `S` small
- `M` medium
- `L` large
- `XL` very large

## Workflow 1 - Connection & Preflight

Purpose:

- guarantee Codex knows whether Unity is ready and which tools/actions are actually available

Items:

### XWF-001 - Live capability probe

- Priority: `P0`
- Complexity: `M`
- Source: `XLAB-001`
- Deliver:
  - live `tools.list` or equivalent bridge capability resource
  - action-level support flags
  - bridge/server version fingerprint

### XWF-002 - Stable editor readiness contract

- Priority: `P0`
- Complexity: `M`
- Source: implicit from verification contract
- Deliver:
  - reliable `xlabmcp://editor/state`
  - compile/domain reload blocking reasons
  - recommended retry timing

### XWF-003 - Canonical naming cleanup

- Priority: `P2`
- Complexity: `M`
- Source: `XLAB-016`
- Deliver:
  - eliminate old drift like `tests.results` vs `get_test_job`
  - unify docs/help output to Unity-style `manage_*`

## Workflow 2 - Scene & Prefab Validation

Purpose:

- verify scene/prefab integrity before and after gameplay mutations

Items:

### XWF-004 - Scene reference validation

- Priority: `P0`
- Complexity: `M`
- Source: `XLAB-003`
- Deliver:
  - `manage_scene(action="validate_references")`
  - missing script / missing ref / broken prefab findings

### XWF-005 - Prefab reference validation

- Priority: `P0`
- Complexity: `M`
- Source: `XLAB-004`
- Deliver:
  - `manage_prefabs(action="validate_references")`
  - structured prefab integrity report

### XWF-006 - Serialized component inspection

- Priority: `P1`
- Complexity: `M`
- Source: part of `XLAB-007`
- Deliver:
  - `manage_components(action="get_serialized")`
  - missing reference visibility

## Workflow 3 - Play Scenario Automation

Purpose:

- drive gameplay scenarios from Codex without manual Unity clicking

Items:

### XWF-007 - Play mode lifecycle

- Priority: `P0`
- Complexity: `M`
- Source: `XLAB-005`
- Deliver:
  - enter/exit/status flow
  - async job semantics

### XWF-008 - Runtime input injection

- Priority: `P0`
- Complexity: `L`
- Source: `XLAB-006`
- Deliver:
  - key/mouse send tool
  - frame-safe dispatch

### XWF-009 - Scene object method invocation

- Priority: `P1`
- Complexity: `M`
- Source: `XLAB-008`
- Deliver:
  - `manage_gameobject(action="invoke_method")`

### XWF-010 - Serialized state injection

- Priority: `P1`
- Complexity: `M`
- Source: part of `XLAB-007`
- Deliver:
  - `manage_components(action="set_serialized")`

## Workflow 4 - Test Automation

Purpose:

- make Unity test runs deterministic and useful as gating signals

Items:

### XWF-011 - Deterministic test finalization

- Priority: `P0`
- Complexity: `M`
- Source: `XLAB-002`
- Deliver:
  - terminal `get_test_job` payload
  - totals + failed stacktraces

### XWF-012 - Test inventory exposure

- Priority: `P1`
- Complexity: `S`
- Source: verification contract
- Deliver:
  - `xlabmcp://tests/{mode}`
  - test/category discovery

## Workflow 5 - Localization & Save Verification

Purpose:

- check localization readiness and save resilience as first-class workflows

Items:

### XWF-013 - Localization table discovery

- Priority: `P1`
- Complexity: `M`
- Source: `XLAB-009`
- Deliver:
  - `xlabmcp://localization/tables`

### XWF-014 - Localization key coverage

- Priority: `P1`
- Complexity: `M`
- Source: `XLAB-010`
- Deliver:
  - list keys by table
  - resolve keys by locale
  - `missing` and `empty` outputs

### XWF-015 - Controlled save diagnostics I/O

- Priority: `P1`
- Complexity: `M`
- Source: `XLAB-014`
- Deliver:
  - project-local save fault injection path
  - safe read/write operations

## Workflow 6 - Platform & Performance Verification

Purpose:

- validate profile-sensitive behavior for Windows and Android

Items:

### XWF-016 - Build profile control

- Priority: `P1`
- Complexity: `L`
- Source: `XLAB-011`
- Deliver:
  - list/get/set active build profile

### XWF-017 - Quality level switching

- Priority: `P1`
- Complexity: `S`
- Source: `XLAB-012`
- Deliver:
  - set quality level by name

### XWF-018 - Profiler verification pack

- Priority: `P1`
- Complexity: `M`
- Source: `XLAB-013`
- Deliver:
  - frame timing
  - render counters
  - memory counters

### XWF-019 - Build smoke automation

- Priority: `P3`
- Complexity: `L`
- Source: `XLAB-021`
- Deliver:
  - compile-only or smoke build automation

## Workflow 7 - Diagnostics & Recovery

Purpose:

- make failures visible and recoverable instead of silent

Items:

### XWF-020 - Bridge watchdog

- Priority: `P2`
- Complexity: `L`
- Source: `XLAB-015`
- Deliver:
  - heartbeat
  - queue timeout diagnostics
  - stale bridge hints

### XWF-021 - Operation audit trail

- Priority: `P2`
- Complexity: `M`
- Source: `XLAB-017`
- Deliver:
  - rolling operation log
  - last command summary resource

### XWF-022 - Screenshot artifact indexing

- Priority: `P2`
- Complexity: `S`
- Source: `XLAB-018`
- Deliver:
  - scenario/step-tagged screenshots

## Workflow 8 - Workflow Productization

Purpose:

- turn tool primitives into reusable development workflows

Items:

### XWF-023 - Scenario macros

- Priority: `P3`
- Complexity: `L`
- Source: `XLAB-020`
- Deliver:
  - named multi-step verification recipes

### XWF-024 - Graph parity recovery

- Priority: `P3`
- Complexity: `XL`
- Source: `XLAB-019`
- Deliver:
  - graph inspection/edit parity for Visual Scripting

## Delivery Waves

### Wave A - Use MCP safely every day

- `XWF-001`
- `XWF-002`
- `XWF-004`
- `XWF-005`
- `XWF-007`
- `XWF-011`

### Wave B - Run full game verification from Codex

- `XWF-006`
- `XWF-008`
- `XWF-009`
- `XWF-010`
- `XWF-013`
- `XWF-014`
- `XWF-015`
- `XWF-016`
- `XWF-017`
- `XWF-018`

### Wave C - Harden and scale the platform

- `XWF-003`
- `XWF-020`
- `XWF-021`
- `XWF-022`
- `XWF-023`
- `XWF-024`

## Done Criteria

The `xLabMcp` workflow backlog is successful when:

- Codex can execute Unity workflows from prompt without manual editor clicking
- test runs are deterministic
- scene/prefab validation is structured
- localization and save verification are tool-driven
- Windows/Android profile checks are repeatable
- failures are diagnosable from MCP payloads, not guesswork
