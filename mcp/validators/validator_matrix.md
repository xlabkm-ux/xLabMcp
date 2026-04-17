# Validator Matrix

## Scene / prefab safety
- `manage_scene(action="validate_references")`
- `manage_prefabs(action="validate_references")`
- `scene.detect_missing_components`
- `scene.detect_reference_regressions`

## Graph safety
- `manage_graph(action="validate")`
- `manage_graph(action="inspect_asset")`
- `manage_graph(action="validate_reaction_density")`
- `manage_graph(action="export_review_summary")`

## Data and save safety
- `manage_scriptableobject(action="validate_schema")`
- `save.validate_compatibility`
- `save.validate_schema_versioning`
- `save.validate_autosave_transitions`
- `save.validate_metaprogression_partition`

## Localization and content
- `manage_localization(action="validate_assets")`
- `manage_localization(action="validate_key_coverage")`
- `manage_localization(action="validate_fallback_language")`

## Release and review
- `release.preflight`
- `manage_graphics(action="validate_profile_assignment")`
- `manage_asset(action="change_summary")`
- `manage_asset(action="classify_risk")`
