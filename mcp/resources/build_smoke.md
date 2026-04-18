# Build Smoke

Lightweight build-smoke workflow for xLabMcp.

## Windows smoke

Sequence:
1. `project.health_check`
2. `manage_build(action="profiles", mode="get_active")`
3. `manage_build(action="profiles", mode="set_active")`
4. `manage_build(action="scenes")`

Expected outcome:
- the active build target is reported
- switching build targets succeeds when the target is available
- scene inventory is available before a release build

## Android smoke

Sequence:
1. `project.health_check`
2. `manage_build(action="profiles", mode="set_active")`
3. `manage_graphics(action="set_quality_level")`
4. `manage_profiler(action="get_counters")`

Expected outcome:
- the target switches cleanly
- the quality level can be set for platform verification
- profiler counters are available before a heavier build step

## Notes

- Keep this workflow compile-light.
- Use it as a gate before release candidate builds, not as a full production build pipeline.
- Prefer target-only names from `canonical_tools.md`.
