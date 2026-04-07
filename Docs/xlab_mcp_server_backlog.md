# xLabMcp Server Backlog

Date: 2026-04-06
Project: `xLabMcp`
Server: `XLab.UnityMcp.Server`
Bridge target: Unity Editor + `Library/XLabMcpBridge`

## Goal

Convert the current xLabMcp bridge/server into a stable Unity-native automation layer that lets Codex App run gameplay development and verification without manual Editor clicking.

This backlog is based on the legacy notes below, which are now archived:

- `Archive/2026-04-06/mcp_server_gaps.md`
- `Archive/2026-04-06/mcp_step2_readiness_checklist.md`
- `Archive/2026-04-06/mcp_tools_by_step.md`

It remains aligned with the active Unity verification contract and canonical tools list:

- [xlab_mcp_verification_contract.md](xlab_mcp_verification_contract.md)
- [canonical_tools.md](canonical_tools.md)

## Priority Model

- `P0` critical blocker for autonomous verification or reliable day-to-day use
- `P1` high value, needed for the full verification workflow
- `P2` important productivity and robustness work
- `P3` useful polish or future-facing work

## Complexity Model

- `S` small
- `M` medium
- `L` large
- `XL` very large / cross-cutting

## Current State Summary

Already partially or fully present:

- basic editor connection
- scene/object/script mutations
- console reading
- camera screenshots
- test execution primitives

Confirmed weak areas:

- non-deterministic or incomplete test result finalization
- no contract-safe autonomous input injection
- no structured scene/prefab reference validation gate
- no localization coverage tooling
- no build profile / quality profile verification loop
- no controlled save corruption workflow for resilience testing
- tool naming/history drift between older project docs and current Unity-style `manage_*` contract
- tool naming/history drift between older project docs and the canonical tools list

## Backlog

## P0 - Verification Blockers

### XLAB-001 - Runtime tool capability probe

- Priority: `P0`
- Complexity: `M`
- Status: planned
- Goal: add live `tools.list`/capability probe from the bridge, not just server-side declarations
- Why: Codex must know what is actually callable in the connected Unity instance before every step
- Deliverables:
  - capability endpoint or resource with `tool`, `action`, `supported`, `notes`
  - version/build hash of active bridge package
  - readiness flags for optional groups like profiler/localization/tests
- Dependencies: none
- Unlocks:
  - reliable preflight for all steps

### XLAB-002 - Deterministic test job finalization

- Priority: `P0`
- Complexity: `M`
- Status: partially present, needs hardening
- Goal: make `get_test_job` return terminal structured results reliably
- Why: current behavior can stall or return stale/running snapshots
- Deliverables:
  - terminal statuses: `completed`, `failed`, `canceled`, `timeout`
  - totals: passed/failed/skipped/duration
  - failed tests with `fixture`, `message`, `stacktrace`
  - polling semantics documented and stable
- Dependencies:
  - `run_tests`
- Unlocks:
  - the test finalization workflow

### XLAB-003 - Scene reference validation action

- Priority: `P0`
- Complexity: `M`
- Status: planned
- Goal: implement `manage_scene(action="validate_references")`
- Why: the scene validation workflow cannot be closed safely by screenshot/manual inspection alone
- Deliverables:
  - detection of missing scripts
  - detection of missing object references
  - detection of broken prefab instance links
  - structured findings payload
- Dependencies:
  - scene traversal and serialized component inspection
- Unlocks:
  - scene and prefab validation workflows

### XLAB-004 - Prefab reference validation action

- Priority: `P0`
- Complexity: `M`
- Status: planned
- Goal: implement `manage_prefabs(action="validate_references")`
- Why: the prefab validation workflow needs prefab integrity, not only scene integrity
- Deliverables:
  - headless prefab load
  - broken reference scan
  - missing script scan
  - structured report
- Dependencies:
  - prefab inspection support
- Unlocks:
  - scene and prefab validation workflows

### XLAB-005 - Play mode lifecycle control

- Priority: `P0`
- Complexity: `M`
- Status: planned
- Goal: add `manage_editor(action="play_mode")` with `enter`, `exit`, `status`
- Why: play scenario sweeps must be controllable from Codex without manual clicks
- Deliverables:
  - async job-based enter/exit
  - play state polling
  - timeout and domain reload recovery
- Dependencies:
  - editor readiness checks
- Unlocks:
  - play scenario workflows

### XLAB-006 - Input injection for gameplay verification

- Priority: `P0`
- Complexity: `L`
- Status: planned
- Goal: add `manage_input(action="send")`
- Why: without input synthesis, Codex cannot fully drive combat/hostage/readability scenarios
- Deliverables:
  - key press / key hold
  - mouse position + mouse button events
  - frame-safe dispatch in Play Mode
  - result payload with sent events and frame window
- Dependencies:
  - play mode lifecycle control
- Unlocks:
  - play and input workflows

## P1 - Full Verification Coverage

### XLAB-007 - Serialized component read/write actions

- Priority: `P1`
- Complexity: `M`
- Status: planned
- Goal: add `manage_components(action="get_serialized")` and `manage_components(action="set_serialized")`
- Why: scenario verification needs safe, structured state introspection and targeted state injection
- Deliverables:
  - primitive field reads
  - object reference reads
  - missing reference reporting
  - controlled writes to serialized fields
- Dependencies:
  - component inspection layer
- Unlocks:
  - serialized state workflows

### XLAB-008 - Method invocation on scene objects

- Priority: `P1`
- Complexity: `M`
- Status: planned
- Goal: implement `manage_gameobject(action="invoke_method")`
- Why: test scenarios need to call methods like `SaveNow` or project-specific verification hooks
- Deliverables:
  - public method invocation
  - simple argument support
  - structured return/error payload
- Dependencies:
  - object lookup and reflection helper
- Unlocks:
  - gameplay invocation workflows

### XLAB-009 - Localization tables resource

- Priority: `P1`
- Complexity: `M`
- Status: planned
- Goal: add `xlabmcp://localization/tables`
- Why: localization coverage must be checked via Unity data, not by parsing arbitrary assets blindly
- Deliverables:
  - table list
  - locale list
  - per-table entry counts
  - missing locale counts
- Dependencies:
  - Localization package presence detection
- Unlocks:
  - the localization workflow

### XLAB-010 - Localization key listing and resolving

- Priority: `P1`
- Complexity: `M`
- Status: planned
- Goal: add `manage_asset(action="list_localization_keys")` and `manage_asset(action="resolve_localization_keys")`
- Why: Codex needs deterministic key-coverage checks for `ru` and `en`
- Deliverables:
  - list keys by table
  - resolve values by locale
  - explicit `missing` and `empty` lists
- Dependencies:
  - `XLAB-009`
- Unlocks:
  - the localization workflow

### XLAB-011 - Build profile inspection and switching

- Priority: `P1`
- Complexity: `L`
- Status: planned
- Goal: implement `manage_build(action="profiles")`
- Why: Unity 6 verification must switch between Windows/Android oriented profiles cleanly
- Deliverables:
  - list profiles
  - get active profile
  - set active profile
  - report build target/platform info
- Dependencies:
  - build settings/build profile editor API integration
- Unlocks:
  - platform workflows

### XLAB-012 - Quality level switching for visual verification

- Priority: `P1`
- Complexity: `S`
- Status: planned
- Goal: implement `manage_graphics(action="set_quality_level")`
- Why: runtime readability/perf checks depend on `PC_Default`, `Android_Default`, `Android_Low`
- Deliverables:
  - switch quality level by name
  - report active level after switch
- Dependencies:
  - none
- Unlocks:
  - platform workflows

### XLAB-013 - Profiler counters verification pack

- Priority: `P1`
- Complexity: `M`
- Status: partially present conceptually, needs target counter contract
- Goal: harden `manage_profiler(action="get_counters")` and `get_frame_timing`
- Why: Android/Windows verification needs repeatable performance snapshots
- Deliverables:
  - render counters
  - memory counters
  - frame timing summary
  - stable counter naming in payload
- Dependencies:
  - profiler integration
- Unlocks:
  - profiler and release workflows

### XLAB-014 - Controlled save diagnostics file workflow

- Priority: `P1`
- Complexity: `M`
- Status: planned
- Goal: add controlled project-local text read/write actions for save fault injection
- Why: save resilience must be tested without giving MCP unrestricted OS filesystem access
- Deliverables:
  - `read_text_file`
  - `write_text_file`
  - path allowlist such as `Library/McpDiagnostics`
  - bridge-side validation and clear errors on denied paths
- Dependencies:
  - agreed runtime save-path override in game code
- Unlocks:
  - the save/load resilience workflow

## P2 - Robustness and Developer Experience

### XLAB-015 - Bridge watchdog and recovery diagnostics

- Priority: `P2`
- Complexity: `L`
- Status: planned
- Goal: add heartbeat, queue timeout diagnostics, and stale bridge recovery guidance
- Why: current failures can look like random hangs or silent no-op behavior
- Deliverables:
  - heartbeat timestamp
  - queue depth
  - stuck command diagnostics
  - reconnect / restart recommendation payload
- Dependencies:
  - none
- Improves:
  - all steps

### XLAB-016 - Canonical contract cleanup across docs and server help

- Priority: `P2`
- Complexity: `M`
- Status: planned
- Goal: align old project docs/tool names with the canonical tools list
- Why: we still carry historical aliases that must not leak into active documentation
- Deliverables:
  - server help output
  - docs update guidance
  - alias/deprecation map if needed
- Dependencies:
  - confirmation of canonical naming
- Improves:
  - onboarding, prompt reliability, lower confusion

### XLAB-017 - Structured operation audit trail

- Priority: `P2`
- Complexity: `M`
- Status: planned
- Goal: log each MCP operation with timestamp, active scene, tool, action, outcome
- Why: helpful for debugging bridge failures and reproducing verification runs
- Deliverables:
  - rolling editor-side log
  - last command summary in diagnostic resource
- Dependencies:
  - none

### XLAB-018 - Screenshot artifact indexing

- Priority: `P2`
- Complexity: `S`
- Status: planned
- Goal: make screenshots easy to retrieve by step/scenario
- Why: verification should leave inspectable artifacts
- Deliverables:
  - artifact naming convention
  - last screenshot index or resource
- Dependencies:
  - `manage_camera(action="screenshot")`

## P3 - Future-facing Extensions

### XLAB-019 - Graph inspection parity

- Priority: `P3`
- Complexity: `XL`
- Status: planned
- Goal: restore safe graph-level inspection/editing for Visual Scripting
- Why: not required to close the current verification workflow, but needed for full hybrid workflow parity
- Dependencies:
  - graph serialization/editor API work

### XLAB-020 - Scenario macros / reusable verification recipes

- Priority: `P3`
- Complexity: `L`
- Status: planned
- Goal: allow named scenario recipes composed from existing tools
- Why: reduce prompt size and repeatable regression effort
- Dependencies:
  - play mode, input, screenshot, profiler, test result stability

### XLAB-021 - Build smoke automation

- Priority: `P3`
- Complexity: `L`
- Status: planned
- Goal: add compile-only or lightweight build smoke checks for Windows/Android
- Why: useful before release candidate gating
- Dependencies:
  - build profile switching

## Recommended Delivery Waves

### Wave 1 - Unblock autonomous verification

- `XLAB-001`
- `XLAB-002`
- `XLAB-003`
- `XLAB-004`
- `XLAB-005`
- `XLAB-006`

Outcome:

- Codex can reliably preflight, enter Play Mode, validate scene/prefab refs, run tests, and drive gameplay scenarios.

### Wave 2 - Close verification end-to-end

- `XLAB-007`
- `XLAB-008`
- `XLAB-009`
- `XLAB-010`
- `XLAB-011`
- `XLAB-012`
- `XLAB-013`
- `XLAB-014`

Outcome:

- Codex can perform the full verification matrix for `xLabMcp` without manual Unity interaction.

### Wave 3 - Harden the platform

- `XLAB-015`
- `XLAB-016`
- `XLAB-017`
- `XLAB-018`

Outcome:

- higher reliability, better diagnostics, cleaner developer workflow

### Wave 4 - Expand product surface

- `XLAB-019`
- `XLAB-020`
- `XLAB-021`

Outcome:

- broader Unity automation coverage beyond the current vertical slice gate

## Success Criteria

The `xLabMcp` server backlog can be considered successfully delivered for current project goals when:

- Wave 1 and Wave 2 are complete
- Codex can execute the full verification workflow from prompt alone
- final test results are deterministic
- ref-validation findings are structured and actionable
- localization coverage can be checked by locale and key
- perf snapshots can be collected under `PC_Default`, `Android_Default`, and `Android_Low`
- save corruption scenarios can be exercised through a controlled diagnostics path



