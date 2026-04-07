# xLabMcp Prototype Archive

This file preserves an early prototype snapshot of the xLabMcp stack.
The active documentation now lives in the root `README.md`, `Docs/README.md`, and the target contract docs.

## Historical architecture

- `.NET server host` for dispatcher and validation
- `Unity Editor bridge` for editor-side execution
- shared contract schema for tool names and argument validation
- bridge queue for asynchronous editor operations

## Historical capability areas

- project and editor status
- asset and content operations
- scene and object manipulation
- script and scriptable object generation
- prefab and graph workflows
- localization, UI, graphics, profiling, and build checks
- test execution and verification

## Historical notes

This prototype was a stepping stone toward the current target-only contract.
The archive keeps the original shape of the work, but active docs now describe the current runtime and canonical tools separately.

## Quick start snapshot

1. Install the .NET SDK.
2. Build the solution.
3. Run the server host.
4. Run tests.
5. Connect the Unity package.
6. Start the editor-side bridge.

## Next steps at the time

- finalize the transport/client split
- harden editor-side execution
- expand integration coverage

