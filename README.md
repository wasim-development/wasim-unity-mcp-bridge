# Wasim Unity MCP Bridge — 0.6.0

Unity 2022.3 Editor MCP bridge with a standalone Windows PowerShell companion. The companion owns the HTTP endpoint and ngrok while Unity handles bounded inspection and locally reviewed changes.

**38 tools:** all 21 tools from 0.5.1, plus 17 additions for runtime inspection, reviewed change sets/rollback, images, tracing, VRChat configuration checks, asset references, runtime assertions, profiler samples and Editor/Test Framework actions.

This is a source distribution. Read [verification status](Documentation~/VERIFICATION.md) before installing in a working project.

## Upgrade

[Langkah pemasangan Bahasa Melayu](Documentation~/UPGRADE_MS.md)

Replace the entire repository contents, retaining the supplied .meta files. Update the companion as well as Editor scripts. Stop the old companion first, then restart it after Unity imports the new package.

After creating a v0.6.0 tag in your repository, install:

~~~text
https://github.com/wasim-development/wasim-unity-mcp-bridge.git#v0.6.0
~~~

Before creating that tag, test using #main or your upgrade branch. A project pinned to #v0.5.1 will not receive changes committed only to main.

## Requirements

- Unity 2022.3 LTS; target environment 2022.3.22f1.
- Windows PowerShell 5.1+ for the companion.
- Newtonsoft JSON package 3.2.1 and Unity Test Framework 1.1.33 (declared dependencies).
- ngrok for a public HTTPS endpoint.
- An MCP-capable client. No model API key is used by this bridge.
- VRChat/UdonSharp is optional; adapters activate only when the SDK types/APIs exist.

## What changed

- Atomic IPC replacement and compatible shared reads; no delete-before-move gap.
- Heartbeat status does not depend on locale-sensitive JSON date conversion.
- A transient status-write failure no longer prevents the request pump from running.
- Console reflection reads message/condition and correctly classifies Unity 2022.3 flags.
- PowerShell preserves empty/single-item arrays and null as valid MCP text.
- Packages resolve via PackageInfo.resolvedPath; package folder analysis follows the package-read setting.
- Expired requests are rejected and interrupted in-flight requests are not blindly replayed.
- ngrok reuse checks the actual loopback HTTP target port.
- Self-test includes a real tools/call round trip into Unity.
- New features are described in [the tool reference](Documentation~/TOOLS_V060.md).

## Review model

Existing single-script proposals keep their v0.5.1 workflow. New change sets and Editor actions each have a separate opt-in setting. Review and approve them in:

**Window > Wasim Development > Change Sets and Actions**

Remote tools can propose changes but cannot approve them. A change set checks scene/file/property guards, captures file backups, applies object operations with Undo, saves affected scenes and records post-change hashes. Rollback blocks if a later edit would be overwritten.

Changes are limited to existing destination folders under Assets and loaded, saved, clean scene objects. See the tool reference for supported property and operation types. Arbitrary C# execution, direct package edits and destructive scene-object operations are not exposed as MCP commands.

## Validation

- [Verification report and commands](Documentation~/VERIFICATION.md)
- Six Unity Edit Mode integration tests under Tests/Editor.
- PowerShell function regression tests under Tests~/test-companion-functions.ps1.
- .NET 8 portable tests under Tests~/Portable.
- Optional UdonSharp tracing example under Documentation~/Examples.

The Editor assembly is excluded from world/player builds. Runtime inspection observes Unity/ClientSim, not a separate VRChat client; multiplayer behavior still needs real-client testing.

## Architecture

MCP client → ngrok → loopback PowerShell companion → file IPC → Unity Editor.

The companion persists through script domain reloads. Diagnostic sessions are bounded and stop/reset on reload. Proposal/action history and test results are stored under Library/WasimUnityMcpBridge. Keep normal Git backups: deleting Library removes this local history.

Original source baseline: v0.5.1, commit 9061958a372df692daf056ba03b5aff283b6d99d. Existing file GUIDs and license are retained.
