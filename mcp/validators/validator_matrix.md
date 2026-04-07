# Validator Matrix

## Scene / prefab safety
- `manage_scene(action="validate_references")`
- `manage_prefabs(action="validate_references")`
- `scene.detect_missing_components`
- `scene.detect_reference_regressions`

## Graph safety
- `graph.inspect_asset`
- `graph.validate`
- `graph.validate_reaction_density`
- `graph.export_review_summary`

## Data and save safety
- `scriptableobject.validate_schema`
- `save.validate_compatibility`
- `save.validate_schema_versioning`
- `save.validate_autosave_transitions`
- `save.validate_metaprogression_partition`

## Localization and content
- `localization.validate_assets`
- `localization.validate_key_coverage`
- `localization.validate_fallback_language`

## Release and review
- `release.preflight`
- `quality.validate_profile_assignment`
- `change.summary`
- `change.classify_risk`
