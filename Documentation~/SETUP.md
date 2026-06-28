# Setup

1. Install the package through Unity Package Manager.
2. Connect to the network that permits ngrok.
3. Open `Window -> Wasim Development -> Unity MCP Bridge`.
4. Press **Start Companion**.
5. Wait for the detected HTTPS URL.
6. Copy the private ChatGPT MCP URL into the ChatGPT app connection.
7. Refresh the ChatGPT app after package upgrades that add tools.

## Manual companion start

The Unity window is recommended. For diagnosis, locate:

`Library/WasimUnityMcpBridge/Companion/companion-config.json`

Then run the bundled script from PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "PATH_TO_PACKAGE\Companion~\wdmcp-companion.ps1" `
  -ConfigPath "PATH_TO_PROJECT\Library\WasimUnityMcpBridge\Companion\companion-config.json"
```

## Logs

- `companion.log`
- `ngrok.log`
- `companion-status.json`
- `unity-status.json`

All are inside `Library/WasimUnityMcpBridge/Companion/`.
