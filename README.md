# Wasim Unity MCP Bridge 0.5.1

This release uses a two-process design:

```text
ChatGPT -> ngrok -> PowerShell companion on 127.0.0.1:38421
                              |
                              v
               Library-based request/response queue
                              |
                              v
                         Unity Editor
```

The PowerShell companion owns the network socket and ngrok. Unity can recompile and reload every Editor C# assembly without releasing or rebinding the MCP port. The Unity package reconnects by resuming its local queue worker after the reload.

## Requirements

- Windows PowerShell 5.1 or newer
- Unity 2022.3 LTS
- ngrok when a public HTTPS endpoint is required
- ChatGPT Developer Mode / custom MCP app

## Start

Open `Window -> Wasim Development -> Unity MCP Bridge` and press **Start Companion**. The package launches the bundled `Companion~/wdmcp-companion.ps1` in a separate PowerShell process. No visible PowerShell window is required.

The companion configuration and logs are stored under:

`Library/WasimUnityMcpBridge/Companion/`

## Domain reload behavior

When any C# script is saved:

1. Unity writes a `reloading` heartbeat.
2. The companion and ngrok stay running.
3. ChatGPT remains connected to the same MCP endpoint.
4. A tool call during the reload returns a temporary retry message.
5. The new Unity scripting domain resumes queue processing automatically.

## Security

The companion listens only on `127.0.0.1` and requires the private capability path. It can call only tools exported by the Unity package. It cannot execute arbitrary commands received from ChatGPT.
