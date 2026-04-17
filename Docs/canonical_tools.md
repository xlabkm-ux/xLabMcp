# Canonical MCP Tools

This document defines the target MCP resources and tools for xLabMcp.
The current runtime inventory is documented separately in `runtime_tools.md`.

## Canonical resources

- `xlabmcp://editor/state`
- `xlabmcp://project/info`
- `xlabmcp://scene/hierarchy`
- `xlabmcp://scene/gameobject/{instanceId}`
- `xlabmcp://tests/{mode}`
- `xlabmcp://localization/tables`
- `xlabmcp://quality/profiles`

## Canonical tools

Core project and status:

- `project_root.set`
- `project.info`
- `project.health_check`
- `project.capabilities`
- `editor.state`
- `read_console`

Asset and content operations:

- `manage_asset(action="create_folder")`
- `manage_asset(action="exists")`
- `manage_asset(action="refresh")`
- `manage_asset(action="list_modified")`
- `manage_asset(action="change_summary")`
- `manage_asset(action="docs_update")`
- `manage_hierarchy(action="list")`
- `manage_hierarchy(action="find")`
- `manage_scene(action="create")`
- `manage_scene(action="open")`
- `manage_scene(action="save")`
- `manage_gameobject(action="create")`
- `manage_gameobject(action="modify")`
- `manage_components(action="add")`
- `manage_components(action="set")`
- `manage_script(action="create_or_edit")`
- `manage_scriptableobject(action="create_or_edit")`
- `manage_prefabs(action="create")`
- `manage_prefabs(action="open")`
- `manage_prefabs(action="save")`
- `manage_prefabs(action="instantiate")`
- `manage_graph(action="open_or_create")`
- `manage_graph(action="connect")`
- `manage_graph(action="edit")`
- `manage_graph(action="validate")`
- `manage_ui(action="create_or_edit")`
- `manage_localization(action="key_add")`

Verification and runtime control:

- `manage_scene(action="validate_references")`
- `manage_prefabs(action="validate_references")`
- `manage_editor(action="play_mode")`
- `manage_editor(action="status")`
- `manage_editor(action="compile_status")`
- `manage_editor(action="install")`
- `manage_editor(action="update")`
- `manage_editor(action="delete")`
- `manage_gameobject(action="invoke_method")`
- `manage_components(action="get_serialized")`
- `manage_components(action="set_serialized")`
- `manage_input(action="send")`
- `manage_camera(action="screenshot")`
- `run_tests`
- `get_test_job`
- `manage_asset(action="list_localization_keys")`
- `manage_asset(action="resolve_localization_keys")`
- `manage_asset(action="read_text_file")`
- `manage_asset(action="write_text_file")`
- `manage_graphics(action="set_quality_level")`
- `manage_profiler(action="get_counters")`
- `manage_profiler(action="get_frame_timing")`
- `manage_build(action="profiles")`
- `manage_build(action="scenes")`

Validation and policy checks:

- `manage_graph(action="validate")`
- `manage_scriptableobject(action="validate_schema")`
- `manage_asset(action="change_summary")`
- `manage_asset(action="docs_update")`
- `manage_localization(action="validate_assets")`
- `manage_localization(action="validate_key_coverage")`
- `manage_localization(action="validate_fallback_language")`
- `manage_graphics(action="validate_profile_assignment")`
- `manage_asset(action="classify_risk")`
