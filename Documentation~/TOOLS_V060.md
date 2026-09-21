# Tools added in 0.6.0

All 21 v0.5.1 tool names remain. The following 17 names extend the catalog. The bridge remains model-independent: it exposes MCP tools and does not embed an OpenAI API client or require a model API key.

| Tool | Purpose |
| --- | --- |
| unity_inspect_runtime | Selected hierarchy or explicit objectId; live physics, animation, VRChat pickup/owner, requested Udon variables and nested serialized property paths. |
| unity_capture_view | MCP JPEG image: Scene camera render or focused Game View. |
| unity_propose_change_set | Queue up to 50 reviewed operations; no immediate project edit. |
| unity_get_change_sets | Pending/recent sets, status, optional full operations and created-object IDs. |
| unity_propose_change_set_rollback | Queue restoration of an applied set, guarded against subsequent edits. |
| unity_start_trace | Poll Play Mode state and collect explicit diagnostic event logs for up to 120 seconds. |
| unity_read_trace | Incremental trace reading with sequence numbers and overflow visibility. |
| unity_stop_trace | Stop a trace and keep its data available until the next trace/domain reload. |
| unity_validate_vrchat | Bounded hierarchy checks: references, pickup physics, joints, Animator configuration, used-layer collision matrix and compile state. |
| unity_find_asset_references | Paged reverse dependencies under Assets and target direct dependencies. |
| unity_check_runtime | Exact assertions against current runtime component values. |
| unity_start_profiler | Nonblocking 1–30 second Editor counter sampling. |
| unity_read_profiler | Sample count, mean, maximum, p95 and unavailable counters. |
| unity_stop_profiler | Dispose active recorders and return captured statistics. |
| unity_propose_editor_action | Queue Play/Stop/Pause/Resume/Step/Compile or Unity Test Framework execution for local review. |
| unity_get_editor_actions | Read pending/accepted/test action status. |
| unity_get_test_results | Latest bridge-started test run counts and bounded failure details. |

## Inspection and IDs

Call unity_inspect_runtime on the selected GameObject. Each object/component has an objectId. Saved scene objects use GlobalObjectId; dynamic/unsaved objects may use instance:<number>. Change sets require saved IDs. Instance IDs are only useful in the current Editor session.

For actual Play Mode values set includeSerialized=false when serialized configuration is unnecessary. Udon variables are an explicit list of up to 32 names; complex values are summarized, not recursively reflected. The allowlist covers documented Rigidbody/Joint/Collider/Animator and selected VRChat API members. It is not arbitrary property/method invocation.

## Change-set operation shapes

Each object below is one item of operations. Supply summary alongside the array.

- set_property: targetId, propertyPath, value; optional expectedValue. The bridge captures the existing value when omitted and checks it again at approval.
- add_component: targetId, fully qualified componentType; optional alias.
- create_gameobject: name; optional parentId, scenePath, primitive, alias. The active saved scene is used when there is no parent/scenePath. Child and parent must use the same scene.
- create_script: new Assets path and full newContent.
- replace_script: existing Assets path, expectedSha256 from unity_read_script, full newContent.
- save_prefab: targetId of a scene GameObject/component and a new Assets .prefab path.

Aliases begin with $ and may only reference earlier create/add operations in the same set. Review JSON stores scene/script guards alongside the prepared operations. No apply/approve endpoint is remotely advertised.

Example: create an object, attach Rigidbody and disable gravity:

~~~json
{
  "summary": "Create a physics placeholder in the active saved scene",
  "operations": [
    {"kind": "create_gameobject", "name": "ToolPlaceholder", "primitive": "Cube", "alias": "$tool"},
    {"kind": "add_component", "targetId": "$tool", "componentType": "UnityEngine.Rigidbody", "alias": "$body"},
    {"kind": "set_property", "targetId": "$body", "propertyPath": "m_UseGravity", "value": false}
  ]
}
~~~

For an object reference, use value: {"objectId":"<real ID from inspection>"} or explicit null. Vectors use x/y/z/w; colors use r/g/b/a. Enum strings use serialized enum names; enum integers are serialized enum indices. Whole-array resizing, managed references and m_Script replacement are not supported.

Custom components may execute Unity/SDK callbacks during addition/validation. Backups cover declared Assets files and affected scene files; they cannot undo arbitrary external side effects performed by project scripts.

## Runtime traces

Start Play Mode/ClientSim, select the tool root and call unity_start_trace. Read using sessionId and afterSequence; use nextAfterSequence for the next page. The 2,000-event ring buffer reports firstAvailableSequence so clients can detect overwritten events.

sampled-change means a value changed between observations; it does not prove an exact callback order. For real callback/resource event records, add opt-in logs beginning with [WDMCP_EVENT]. See Examples/UdonTraceExample.cs. The bridge does not inject instrumentation into gameplay scripts.

## Tests and Editor actions

runTests uses Unity Test Framework 1.1.33, with testMode EditMode or PlayMode and optional exact testNames. Local approval is required because tests can execute arbitrary code supplied by the project. Save scenes first. The request returns a proposal immediately; execution and callbacks are asynchronous. Poll unity_get_test_results.

Accepted means an Editor command was dispatched, not that the requested transition or compilation succeeded. Check unity_get_status / unity_get_compilation_status after Play/Compile actions. Test runs have Running/Completed status and separate test pass/fail counts.

unity_check_runtime only checks current exposed values. It does not simulate VRChat input, trigger pickup callbacks or emulate another real player. Add project Play Mode tests for automated gameplay scenarios.

## Capture and profiler limits

Scene capture renders the Scene camera without editor gizmos. Game capture reads the currently focused visible Game View including its toolbar; window visibility, desktop scaling, graphics pipeline and platform affect results. Capture requires interactive Unity and returns an MCP image block.

Profiler data is sampled every 100 ms from supported Editor counters: main-thread time, GC allocation, total used memory and draw calls. Unavailable counters are explicitly listed. This is not Quest/device FPS, every rendered frame, a deep profile or attribution to individual scripts. Recordings and traces stop/reset on domain reload.

## Diagnostics limits

VRChat diagnostics validate available local configuration, not network authority across real clients. Network ID allocation/conflict validation and the final world build remain in the SDK. Asset references are direct AssetDatabase dependencies; use paging for a full reverse scan and unity_find_script_references for textual code symbols. These are not a complete semantic C# graph.
