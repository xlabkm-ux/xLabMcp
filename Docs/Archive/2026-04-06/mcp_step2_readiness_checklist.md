# xLabMcp Step 2 Readiness Checklist Archive

## Goal

Enable the first vertical-slice scene shell through the in-house xLabMcp stack.

## What was missing then

- a server host reachable from the agent runtime
- a Unity Editor bridge inside the project
- project root selection
- editor readiness/status inspection
- scene creation and saving capability
- console inspection for new errors
- deterministic error payloads for busy or invalid-path cases

## Done criteria that were being tracked

1. Select the current project root.
2. Confirm the editor is ready.
3. Create the target scene shell.
4. Verify the scene exists on disk and in the editor.
5. Ensure no new console errors were introduced.
6. Commit the change with a reviewable diff.

## Suggested implementation order

1. Define protocol contracts.
2. Add bridge bootstrap and heartbeat.
3. Implement root selection and editor state.
4. Implement scene creation and validation.
5. Add console inspection.
6. Add a smoke test for the call chain.

