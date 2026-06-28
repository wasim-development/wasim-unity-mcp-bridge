# Changelog

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
