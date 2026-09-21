# Verification status — v0.6.0

Prepared on 2026-09-21. Source baseline: wasim-development/wasim-unity-mcp-bridge v0.5.1, commit 9061958a372df692daf056ba03b5aff283b6d99d.

This archive contains source code. A full Unity compilation and the Unity integration tests have not been executed in the preparation environment.

## Executed checks

- Verified all 50 original files against their Git blob SHA-1 before editing.
- Retained every original file path and all original Unity .meta contents.
- Compiled the actual AtomicFile, PathRules, McpToolCatalog and ExtendedToolCatalog source using .NET SDK 8.0.425.
- Parsed 32 C# files using Roslyn in C# 9 mode: 30 Editor scripts, the integration test fixture and the optional UdonSharp example. Parsing checks syntax; it does not validate Unity API bindings.
- Checked 38 unique advertised tool names against their dispatch cases, including all 21 legacy tools and 17 additions.
- Checked the root object schema of each advertised tool.
- Checked path normalization and rejection of traversal, absolute paths, drive paths and invalid path characters.
- Checked UTC heartbeat parsing under en-US, ms-MY and ar-SA cultures.
- Checked atomic replacement while a compatible reader held the previous file, and 200 replacements with a concurrent JSON reader.
- Validated JSON/assembly definitions, unique Unity GUIDs and ZIP file integrity when packaging.

The portable suite completed **89 checks**. Its console output is included as portable-validation.log. Counts include syntax/schema assertions as well as behavioral checks; these are not 89 Unity tests.

## Not executed here

- Full Unity 2022.3.22f1 compilation or the six Unity Edit Mode integration tests.
- PowerShell function tests, PowerShell parsing through its SDK, or the companion HTTP harness. Neither Windows PowerShell nor pwsh was installed, and the optional NuGet restore failed with NU1301.
- Windows-specific incompatible file-sharing behavior. The Linux run explicitly skipped that check.
- Actual scene/game image capture, profiler-counter availability, UdonSharp adapter execution or multiplayer behavior in external VRChat clients.

No precompiled Unity DLL, passing Unity test run, successful VRChat world build or GitHub publication is implied by this archive.

## Run on the target Windows PC

First import the package into a backup/test Unity project, allow Package Manager to resolve dependencies, and check the Console for compilation errors. Stop and restart the companion to load the upgraded PowerShell source.

From the extracted repository root, run the companion regression tests:

~~~powershell
& '.\Tests~\test-companion-functions.ps1'
~~~

Use the bridge window's self-test for initialize, tools/list and a real unity_get_status IPC round trip. The optional endpoint smoke script is:

~~~powershell
& '.\Documentation~\test-mcp.ps1' -Endpoint '<private endpoint copied from Unity>'
~~~

### Unity integration tests

Merge this entry into the test project's Packages/manifest.json, preserving its other entries:

~~~json
"testables": ["com.wasimdevelopment.unity-mcp-bridge"]
~~~

Open Window > General > Test Runner > EditMode and run BridgeIntegrationTests. The six tests cover approved property changes and rollback, stale-scene rejection, aliases and object creation, recovery after a failed operation, joint/Rigidbody inspection, and registered-package path resolution.

Alternatively, close that test project in Unity and run:

~~~powershell
& '.\Tests~\run-unity-tests.ps1' -UnityEditor 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe' -ProjectPath 'C:\Path\To\TestProject'
~~~

The launcher retains the Unity log and NUnit XML result. It requires at least six passing tests and rejects a failed run.

### Portable suite

With .NET 8 and NuGet access, run from the repository root:

~~~powershell
dotnet run --project 'Tests~/Portable/Bridge.PortableTests.csproj' -- .
~~~

The full mode also uses the PowerShell SDK for parser checks and an HTTP/IPC harness with simulated Unity responses. That harness does not replace testing against a real Unity Editor.

The offline mode used during preparation references the Newtonsoft.Json and Roslyn assemblies shipped with the .NET 8 SDK:

~~~powershell
dotnet restore 'Tests~/Portable/Bridge.PortableTests.csproj' -p:OfflineSdkValidation=true --configfile 'Tests~/Portable/NuGet.Offline.Config'
dotnet run --no-restore --project 'Tests~/Portable/Bridge.PortableTests.csproj' -p:OfflineSdkValidation=true -- .
~~~

## Manual acceptance checks

1. Confirm bridge/companion version 0.6.0 and 38 tools.
2. Inspect both pickups during ClientSim Play Mode; compare Rigidbody kinematic state, joint connectedBody and ownership before/after pickup. Explicit callback logs require the optional tracing example or equivalent instrumentation.
3. Enable change sets, review a component change locally, apply it, then review and apply its rollback. Verify a later manual edit blocks rollback.
4. Capture a Scene view and a focused visible Game view; verify the images match the requested view.
5. Sample the profiler and inspect which counters are available. These samples are not a device FPS benchmark.
6. Run the integration tests and a bridge-proposed Test Framework action; inspect unity_get_test_results.
7. Test ownership transfer and resource awards with two actual VRChat clients before relying on multiplayer behavior.

## Primary API references consulted

- [Unity GlobalObjectId](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/GlobalObjectId.html)
- [Unity ProfilerRecorder](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Unity.Profiling.ProfilerRecorder.html)
- [Unity Test Framework 1.1 TestRunnerApi](https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/api/UnityEditor.TestTools.TestRunner.Api.TestRunnerApi.html)
- [Unity Test Framework 1.1 ICallbacks](https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/api/UnityEditor.TestTools.TestRunner.Api.ICallbacks.html)
- [UnityCsReference 2022.3 Console bindings](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Editor/Mono/LogEntries.bindings.cs)
