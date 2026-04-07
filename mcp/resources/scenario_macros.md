# Scenario Macros

Reusable verification recipes for xLabMcp.

## `scene_ref_sanity`

Purpose:
- Validate scene references after a content change.

Sequence:
1. `editor.state`
2. `manage_scene(action="validate_references")`
3. `read_console`

Expected outcome:
- missing scripts or broken references are reported clearly

## `playmode_input_roundtrip`

Purpose:
- Verify that Play Mode transitions and input synthesis still work together.

Sequence:
1. `editor.state`
2. `manage_editor(action="play_mode", mode="enter")`
3. `manage_input(action="send")`
4. `manage_editor(action="play_mode", mode="exit")`

Expected outcome:
- play mode enters and exits cleanly, and input events are accepted during the run

## `save_resilience_fault_injection`

Purpose:
- Exercise controlled save corruption under the diagnostics path.

Sequence:
1. `project.health_check`
2. `manage_asset(action="write_text_file")`
3. `manage_asset(action="read_text_file")`
4. `run_tests`

Expected outcome:
- diagnostics payload is written and read from `Library/McpDiagnostics`
- save/load tests can observe the injected payload

## `visual_capture_verification`

Purpose:
- Capture a screenshot artifact for visual inspection.

Sequence:
1. `editor.state`
2. `manage_camera(action="screenshot")`
3. `read_console`

Expected outcome:
- screenshot artifact is indexed and the last capture is discoverable
