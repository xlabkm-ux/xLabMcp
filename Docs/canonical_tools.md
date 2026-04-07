# Canonical MCP Tools

This document defines the canonical MCP resources and tools for xLabMcp.
The BREACH project documentation must mirror these names exactly.

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
- `editor.state`
- `project.info`
- `project.health_check`
- `read_console`
- `asset.list_modified`
- `change.summary`
- `project.docs_update`
- `release.preflight`

Scene and object authoring:

- `asset.create_folder`
- `asset.exists`
- `asset.refresh`
- `scene.create`
- `scene.open`
- `scene.save`
- `gameobject.create`
- `gameobject.modify`
- `component.add`
- `component.set`
- `script.create_or_edit`
- `scriptableobject.create_or_edit`
- `prefab.create`
- `prefab.open`
- `prefab.save`
- `prefab.instantiate`
- `graph.open_or_create`
- `graph.connect`
- `graph.edit`
- `graph.validate`
- `ui.create_or_edit`
- `localization.key_add`

Verification and runtime control:

- `manage_scene(action="validate_references")`
- `manage_prefabs(action="validate_references")`
- `manage_editor(action="play_mode")`
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

Validation and policy checks:

- `graph.validate`
- `scriptableobject.validate_schema`
- `save.validate_compatibility`
- `save.validate_schema_versioning`
- `save.validate_autosave_transitions`
- `save.validate_metaprogression_partition`
- `localization.validate_assets`
- `localization.validate_key_coverage`
- `localization.validate_fallback_language`
- `quality.validate_profile_assignment`
- `change.classify_risk`

## Legacy aliases

The following names may appear only in archived history or compatibility notes:

- `console.read`
- `tests.results`
- `playmode.enter`
- `playmode.exit`
- `scene.validate_refs`
- `prefab.validate`
- `tests.run_editmode`
- `tests.run_all`

Do not use legacy aliases in active documentation or new workflows.
