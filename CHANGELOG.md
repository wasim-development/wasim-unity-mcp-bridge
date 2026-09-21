# Changelog

## 0.6.0

### Reliability
- Replace IPC files atomically with bounded retries and compatible shared readers.
- Keep heartbeat failures out of the request pump; use UTC/invariant timestamps and in-process freshness.
- Reject expired requests and do not replay interrupted in-flight operations after reload.
- Preserve empty arrays, single-item arrays and null in PowerShell MCP responses.
- Forward native MCP image content and check the ngrok upstream host/port before reusing a tunnel.
- Read the Unity Console message field with legacy fallback and correct 2022.3 error/warning flags.
- Resolve registered package locations and support package-folder inspection when enabled.
- Validate Assets/package paths without traversal or links below the permitted root.
- Extend the self-test with an actual Unity tools/call round trip.

### New tools and review UI
- Add 17 tools, retaining all 21 original tool names (38 total).
- Runtime physics/pickup/Animator/Udon-variable inspection with object IDs and nested serialized properties.
- Reviewed multi-operation change sets for scene properties, objects, components, scripts and new prefabs.
- Hash-guarded backup/rollback; stale and dirty scene checks; interrupted applies report RecoveryRequired.
- Optional UdonSharp editor adapters for proxy creation and variable synchronization.
- Scene/Game View image capture, state-change tracing and explicit diagnostic event collection.
- VRChat configuration checks, paged asset reverse references and runtime assertions.
- Bounded asynchronous Editor profiler sampling.
- Locally approved Play/Pause/Step/Compile and Unity Test Framework actions, with persisted test results.
- Separate opt-in settings for change sets and Editor actions; no remote approval endpoint.

### Tests and documentation
- Add six Unity integration tests, portable .NET/companion regression tests and Windows validation helpers.
- Add upgrade instructions, exact feature limits, source provenance and verification status.
- Preserve original .meta GUIDs and license.
- Add Unity Test Framework 1.1.33 dependency; the production bridge remains Editor-only.

## 0.5.1

- Fixed Unity 2022.3 compiler ambiguity between `UnityEditor.PackageInfo` and `UnityEditor.PackageManager.PackageInfo` in `CompanionManager`.
- No protocol, tool, port, or companion behavior changes.

## 0.5.0

- Moved the MCP TCP listener out of Unity into a standalone PowerShell companion process.
- Moved ngrok ownership to the companion so both remain alive during Unity script compilation and domain reload.
- Added file-based IPC under `Library/WasimUnityMcpBridge/Companion` for Unity tool requests and responses.
- Added Unity heartbeat and graceful `Unity is compiling/reloading` tool errors without disconnecting ChatGPT.
- Added companion start, stop, restart, force-stop, status, logs and self-test controls in the Unity window.
- Removed the reloadable Unity-hosted MCP socket and its port-rebind lifecycle.
- Preserved the v0.4 tool catalogue and selected-folder analyzer.
