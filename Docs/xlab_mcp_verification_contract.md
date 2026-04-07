# xLabMcp Verification Contract

Date: 2026-04-06
Project: `xLabMcp`
Scope: Unity-side verification for the core xLabMcp gameplay, safety, and release workflows.

## Goal

Define the minimum MCP `resources` and `tools` required for Codex App to execute Unity verification for:

- scene/prefab reference sweep
- combat/perception regression scenarios
- hostage success/fail scenarios
- save/load resilience
- localization key coverage
- Android quick perf pass
- Windows readability/input sanity
- final candidate verification gate

This contract is intentionally aligned to the target xLabMcp style documented in:

- `README.md`
- `Docs/README.md`
- `mcp/README.md`
- `Docs/canonical_tools.md`

The current runtime and Unity bridge expose a target-only public inventory; see
`Docs/runtime_tools.md`.

The contract must preserve the xLabMcp naming model:

- use `xlabmcp://...` resources for read-only state
- use the target canonical tool names listed in `Docs/canonical_tools.md`
- only add new actions where the current Unity contract does not yet cover the required verification workflow

## Contract Principles

1. Resource-first workflow.
   Read `xlabmcp://editor/state` and project/scene resources before mutating Editor state.

2. No parallel contract fork.
   Do not invent a second naming system when the canonical tools document already defines the approved names.

3. Async jobs for long operations.
   Play mode transitions, tests, builds, profiler captures, and save fault-injection flows must return `job_id`.

4. Read-only vs mutating separation.
   Resources expose state; tools perform actions.

5. Verification payloads must be machine-readable.
   Findings must be structured, not only text logs.

6. Unity-native truth first.
   Prefer data gathered from Unity APIs, Test Framework, Localization package, Profiler APIs, Build Profiles/Quality APIs, and serialized object inspection over inferred text parsing.

## Canonical Resources

### `xlabmcp://editor/state`

Status: existing canonical resource.

Required fields:

- `project_path`
- `project_name`
- `unity_version`
- `is_playing`
- `is_paused`
- `is_compiling`
- `is_updating`
- `is_domain_reload_pending`
- `ready_for_tools`
- `blocking_reasons`
- `active_scene`
- `open_scenes`
- `selected_build_target`
- `quality_level`
- `bridgeHealth`
- `bridgeHealth.heartbeatAtUtc`
- `bridgeHealth.heartbeatAgeMs`
- `bridgeHealth.queueDepth`
- `bridgeHealth.oldestCommandAgeMs`
- `bridgeHealth.lastCommand`
- `bridgeHealth.auditLogPath`
- `bridgeHealth.state`
- `bridgeHealth.recommendedAction`
- `recommended_retry_after_ms`



### `xlabmcp://project/info`

Status: existing canonical resource.

Required fields:

- `projectRoot`
- `projectName`
- `unityVersion`
- `platform`
- `renderPipeline`
- `activeInputHandler`
- `packages.ugui`
- `packages.textmeshpro`
- `packages.inputsystem`
- `packages.uiToolkit`
- `packages.screenCapture`
- `packages.localization`
- `packages.testFramework`



### `xlabmcp://scene/hierarchy`

Status: existing shape exposed by the bridge; keep the resource name canonical across docs.

Required fields per item:

- `instanceId`
- `name`
- `path`
- `activeSelf`
- `activeInHierarchy`
- `tag`
- `layer`
- `transform`
- `prefabSourcePath`
- `components`



### `xlabmcp://scene/gameobject/{instanceId}`

Status: existing canonical pattern in the skill docs.

Required fields:

- `instanceId`
- `name`
- `path`
- `transform`
- `activeSelf`
- `activeInHierarchy`
- `prefabSourcePath`
- `components[]`

Each component entry must include:

- `type`
- `enabled`
- `serializedFields`
- `missingReferences[]`



### `xlabmcp://tests/{mode}`

Status: consistent with workflow docs.

Modes:

- `EditMode`
- `PlayMode`

Required fields:

- `assemblies`
- `tests`
- `categories`



### `xlabmcp://localization/tables`

Status: implemented in current runtime.

Required fields:

- `tables[]`
- `locales[]`
- `entryCounts`
- `missingLocaleCounts`



### `xlabmcp://quality/profiles`

Status: new resource recommended.

Required fields:

- `qualityLevels[]`
- `activeQualityLevel`
- `buildProfiles[]`
- `activeBuildProfile`



## Canonical Tools

## 1. Console and validation

### `read_console`

Status: existing canonical tool.

Required support:

- `types=["error","warning","log"]`
- `count`
- `include_stacktrace`
- `format`
- `since_timestamp`

Return shape:

- `messages[]`
- `type`
- `message`
- `stacktrace`
- `timestamp`
- `file`
- `line`



### `project.capabilities`

Status: live bridge capability probe.

Purpose:

Return a machine-readable snapshot of what the connected Unity bridge can actually do right now, including readiness flags and supported tool/action combinations.

Required fields:

- `tool`
- `projectRoot`
- `projectName`
- `unityVersion`
- `bridgePackage.name`
- `bridgePackage.version`
- `bridgePackage.buildHash`
- `bridgeHealth`
- `readyForTools`
- `blockingReasons`
- `readinessFlags`
- `capabilities[]`



### `manage_scene(action="validate_references")`

Status: canonical validation action on the existing scene tool family.

Purpose:

Validate a scene for broken references without introducing a new tool family.

Request:

```json
{
  "action": "validate_references",
  "path": "Assets/Scenes/VerticalSlice/VS01_Rescue.unity",
  "include_inactive": true,
  "include_prefab_instances": true
}
```

Response:

```json
{
  "success": true,
  "summary": {
    "missing_scripts": 0,
    "missing_object_references": 0,
    "broken_prefab_links": 0
  },
  "findings": [
    {
      "severity": "error",
      "scenePath": "Assets/Scenes/VerticalSlice/VS01_Rescue.unity",
      "gameObjectPath": "MissionDirector/ResultScreen",
      "componentType": "ResultScreenController",
      "field": "missionStateService",
      "issue": "missing_object_reference"
    }
  ]
}
```


### `manage_prefabs(action="validate_references")`

Status: canonical validation action on the existing prefab tool family.

Request:

```json
{
  "action": "validate_references",
  "prefab_path": "Assets/Prefabs/Gameplay/Enemies/Enemy_Grunt.prefab"
}
```

Response:

Same schema as scene validation, with `prefabPath`.


## 2. Play mode and scenario control

### `manage_editor(action="play_mode")`

Status: canonical play mode controller.

Supported modes:

- `enter`
- `exit`
- `status`

Request examples:

```json
{
  "action": "play_mode",
  "mode": "enter",
  "wait_for_ready": true
}
```

```json
{
  "action": "play_mode",
  "mode": "status"
}
```

Response:

- `job_id` for `enter` and `exit`
- `is_playing`
- `is_paused`
- `frame`
- `time`
- `activeScene`


### `manage_gameobject(action="invoke_method")`

Status: canonical scene-object invocation action.

Purpose:

Call public parameterless or simple-argument methods on MonoBehaviours to drive test scenarios without custom one-off tooling.

Request:

```json
{
  "action": "invoke_method",
  "target": "MissionDirector",
  "component_type": "SaveService",
  "method": "SaveNow",
  "arguments": []
}
```

Response:

- `success`
- `return_value`
- `warnings[]`


### `manage_components(action="get_serialized")`

Status: canonical serialized-read action.

Purpose:

Read serialized values from a component in a structured way.

Request:

```json
{
  "action": "get_serialized",
  "target": "MissionDirector",
  "component_type": "ObjectiveService"
}
```

Response:

- `fields`
- `object_references`
- `missing_references`


### `manage_components(action="set_serialized")`

Status: canonical serialized-write action.

Purpose:

Inject test state into serialized fields for scenario verification.

Request:

```json
{
  "action": "set_serialized",
  "target": "MissionDirector",
  "component_type": "ObjectiveService",
  "properties": {
    "hostageFreed": true,
    "hostageExtracted": false
  }
}
```

Response:

- `success`
- `changed_fields[]`



### `manage_input(action="send")`

Status: canonical input-synthesis action.

Reason:

The current skill/tool examples cover project input configuration but do not expose runtime input injection. For autonomous gameplay verification, Codex needs keyboard/mouse event synthesis during Play Mode.

Request:

```json
{
  "action": "send",
  "keys": ["W", "Tab", "E"],
  "mouse_position": [640, 360],
  "mouse_buttons": ["left"],
  "duration_ms": 120
}
```

Response:

- `success`
- `events_sent`
- `frame_window`



## 3. Screenshots and visual verification

### `manage_camera(action="screenshot")`

Status: canonical screenshot action.

Required support for verification:

- `capture_source="game_view"`
- `capture_source="scene_view"`
- `include_image=true`
- `max_resolution`
- `view_target`
- `scenario`
- `step`
- `label`
- `outputPath`

Required response fields:

- `success`
- `path`
- `scenario`
- `step`
- `label`
- `indexPath`



## 4. Tests

### `run_tests`

Status: canonical test runner.

Required support:

- `mode="EditMode" | "PlayMode"`
- `test_names[]`
- `category_names[]`
- `include_failed_tests=true`



### `get_test_job`

Status: canonical test job result reader.

Required support:

- `job_id`
- `wait_timeout`
- `include_failed_tests=true`
- `include_passed_tests=false`
- `include_stacktrace=true`

Required result payload:

- `status`
- `passed_count`
- `failed_count`
- `skipped_count`
- `duration_ms`
- `failed_tests[]`

Each failed test must include:

- `name`
- `fixture`
- `message`
- `stacktrace`



## 5. Localization verification

### `manage_asset(action="list_localization_keys")`

Status: canonical localization key listing action. Implemented in current runtime.

Purpose:

Read String Table collection assets and return keys by table/locale.

Request:

```json
{
  "action": "list_localization_keys",
  "path": "Assets",
  "table": "DefaultLocalizationTable"
}
```

Response:

- `table`
- `locales[]`
- `keys[]`
- `entriesByLocale`



### `manage_asset(action="resolve_localization_keys")`

Status: canonical localization key resolution action. Implemented in current runtime.

Request:

```json
{
  "action": "resolve_localization_keys",
  "table": "DefaultLocalizationTable",
  "locale": "ru",
  "keys": [
    "ui.result.success.title",
    "ui.result.fail.title",
    "hud.controls"
  ]
}
```

Response:

- `resolved[]`
- `missing[]`
- `empty[]`



## 6. Save/load resilience

### `manage_asset(action="read_text_file")`

Status: canonical controlled text read action.

Purpose:

Read save JSON written under project-approved diagnostic paths. For arbitrary OS paths outside the project root, expose a separate safe editor action instead of broad filesystem access.

Request:

```json
{
  "action": "read_text_file",
  "path": "Library/McpDiagnostics/mission_save_v1.json"
}
```

Response:

- `success`
- `text`
- `size_bytes`

### `manage_asset(action="write_text_file")`

Status: canonical controlled text write action.

Purpose:

Inject corrupted or mismatched save payloads into a controlled diagnostics location.

Request:

```json
{
  "action": "write_text_file",
  "path": "Library/McpDiagnostics/mission_save_v1.json",
  "contents": "{ \"schemaVersion\": 999 }"
}
```

Response:

- `success`
- `size_bytes`



Implementation note:

The game runtime should expose an Editor-safe override for save path during MCP verification, so the bridge only needs project-local file access and does not need unrestricted access to `Application.persistentDataPath`.

## 7. Perf and platform verification

### `manage_graphics(action="set_quality_level")`

Status: canonical quality-level switch action. Implemented in current runtime.

Request:

```json
{
  "action": "set_quality_level",
  "quality_level": "Android_Low",
  "apply_expensive_changes": true
}
```

Response:

- `success`
- `active_quality_level`



### `manage_profiler(action="get_counters")`

Status: canonical profiler counter action. Implemented in current runtime.

Required support:

- category read for `Render`, `Memory`, `Scripts`
- explicit counters for:
  - `Batches Count`
  - `Draw Calls Count`
  - `SetPass Calls Count`
  - `GC Used Memory`
  - `Total Used Memory`



### `manage_profiler(action="get_frame_timing")`

Status: canonical frame-timing action. Implemented in current runtime.

Required fields:

- `cpu_frame_time_ms`
- `gpu_frame_time_ms`
- `cpu_main_thread_ms`
- `cpu_render_thread_ms`
- `frame_count_sampled`



### `manage_build(action="profiles")`

Status: canonical build-profile action. Implemented in current runtime.

Purpose:

List and switch Unity 6 Build Profiles without creating a parallel tool.

Supported modes:

- `list`
- `get_active`
- `set_active`

Request example:

```json
{
  "action": "profiles",
  "mode": "set_active",
  "profile": "Android_Default"
}
```

Response:

- `profiles[]`
- `active_profile`
- `active_build_target`
- `active_build_target_group`



## Explicit Non-Goals

This contract should not add:

- a second custom graph API for this verification phase
- arbitrary filesystem access outside a controlled project-local diagnostic path
- duplicate tools where an existing `manage_*` family can be extended
- text-only logs where structured findings are required

## Recommended Implementation Order

1. `manage_scene(action="validate_references")`
2. `manage_prefabs(action="validate_references")`
3. `manage_editor(action="play_mode")`
4. `get_test_job` result enrichment
5. `manage_input(action="send")`
6. `manage_components(action="get_serialized")`
7. `manage_components(action="set_serialized")`
8. `xlabmcp://localization/tables`
9. `manage_asset(action="list_localization_keys")`
10. `manage_asset(action="resolve_localization_keys")`
11. `manage_build(action="profiles")`
12. `manage_graphics(action="set_quality_level")`
13. controlled save diagnostics file actions

## Acceptance Criteria

The xLabMcp bridge is ready for Codex-driven verification when:

- Codex can run the full verification pass without manual clicking in Unity
- all findings are returned as structured payloads
- test failures include names and stack traces
- platform/profile changes are reversible and observable
- localization coverage can be checked by key and locale
- save corruption fallback can be tested without unrestricted OS file access



