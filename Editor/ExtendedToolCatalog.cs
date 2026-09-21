using Newtonsoft.Json.Linq;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ExtendedToolCatalog
    {
        public static void AppendCatalog(JArray tools)
        {
            tools.Add(Tool("unity_inspect_runtime", "Read bounded live Unity/ClientSim state, stable object IDs and nested serialized fields. Includes Rigidbody, Joint, Collider, Animator and optional VRChat pickup/owner/Udon variables. Does not inspect an external VRChat client.",
                Schema(OptId(), P("maximumObjects", Int(30, 1, 200)), P("maximumProperties", Int(80, 1, 300)),
                    P("includeSerialized", Bool(true)), P("udonVariables", Strings(32)))));
            tools.Add(Tool("unity_capture_view", "Return an MCP image of the Scene camera render or focused Game View. Scene capture excludes editor gizmos. Requires interactive Unity.",
                Schema(P("view", Enum("scene", "game")), P("width", Int(960, 64, 1280)), P("height", Int(540, 64, 1280)))));
            tools.Add(Tool("unity_propose_change_set", "Create a pending set of 1-50 operations. User must enable change sets and approve inside Unity. Saved clean scenes, stable GlobalObjectIds and existing destination folders are required. Operations: set_property, add_component, create_gameobject, create_script, replace_script, save_prefab. New aliases use $name. Existing scripts require expectedSha256. No immediate project mutation.",
                Required(Schema(P("summary", Str()), P("operations", new JObject {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50,
                    ["items"] = Required(Schema(P("kind", Enum("set_property", "add_component", "create_gameobject", "create_script", "replace_script", "save_prefab")),
                        P("targetId", Str()), P("alias", Str()), P("propertyPath", Str()), P("value", Any()), P("expectedValue", Any()),
                        P("componentType", Str()), P("name", Str()), P("primitive", Enum("Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad")),
                        P("parentId", Str()), P("scenePath", Str()), P("path", Str()), P("expectedSha256", Str()), P("newContent", Str())), "kind") })), "summary", "operations"), false));
            tools.Add(Tool("unity_get_change_sets", "List pending/recent change sets and rollback state. includeOperations includes full review content.",
                Schema(P("includeCompleted", Bool(false)), P("includeOperations", Bool(false)))));
            tools.Add(Tool("unity_propose_change_set_rollback", "Prepare a reviewed rollback of an applied change set. Blocks if assets changed after apply; approval remains in Unity.",
                Required(Schema(P("changeSetId", Str()), P("summary", Str())), "changeSetId"), false));
            tools.Add(Tool("unity_start_trace", "Record bounded Play Mode state changes and explicit [WDMCP_EVENT] logs without blocking a request. Sampled changes are not proof that an exact callback fired. One trace retained; read before a domain reload.",
                Schema(OptId(), P("durationSeconds", Int(20, 1, 120)), P("intervalMilliseconds", Int(200, 50, 2000)),
                    P("maximumObjects", Int(5, 1, 20)), P("udonVariables", Strings(32))), false));
            tools.Add(Tool("unity_read_trace", "Read trace events incrementally. firstAvailableSequence reveals dropped entries in the 2000-event ring buffer.",
                Required(Schema(P("sessionId", Str()), P("afterSequence", Int(0, 0, int.MaxValue)), P("maximumEntries", Int(100, 1, 200))), "sessionId")));
            tools.Add(Tool("unity_stop_trace", "Stop the named trace and return its first page of events.", Session(), false));
            tools.Add(Tool("unity_validate_vrchat", "Inspect selected hierarchy for missing references, pickup/physics/Animator configuration and used-layer collisions. Includes compilation status. Does not replace SDK Network ID allocation or multiplayer/build testing.",
                Schema(OptId(), P("maximumObjects", Int(200, 1, 1000)))));
            tools.Add(Tool("unity_find_asset_references", "Page through direct reverse asset dependencies in Assets, plus direct dependencies of the target asset. Save scenes before searching.",
                Schema(P("path", Str()), P("startIndex", Int(0, 0, int.MaxValue)), P("maximumAssets", Int(200, 1, 1000)))));
            tools.Add(Tool("unity_check_runtime", "Evaluate exact assertions against exposed current component runtime values. Requires Play Mode. Does not invoke gameplay callbacks or simulate other players.",
                Required(Schema(P("udonVariables", Strings(32)), P("checks", new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 100,
                    ["items"] = Required(Schema(P("objectId", Str()), P("property", Str()), P("equals", Any())), "objectId", "property", "equals") })), "checks")));
            tools.Add(Tool("unity_start_profiler", "Start 1-30 seconds of nonblocking sampled Editor CPU/memory/render counters. Returns a session ID; not on-device FPS or per-script profiling.",
                Schema(P("durationSeconds", Int(5, 1, 30))), false));
            tools.Add(Tool("unity_read_profiler", "Read profiler session statistics and unavailable counters. Sessions reset on domain reload.", Session()));
            tools.Add(Tool("unity_stop_profiler", "Stop the named profiler capture, release recorders and return statistics.", Session(), false));
            tools.Add(Tool("unity_propose_editor_action", "Request a locally reviewed Editor action: enter/exit Play Mode, pause/resume/step, compile, or run Unity Test Framework tests. These actions can run project code; user approval is required in Unity.",
                Required(Schema(P("action", Enum("enterPlayMode", "exitPlayMode", "pause", "resume", "step", "compile", "runTests")),
                    P("summary", Str()), P("testMode", Enum("EditMode", "PlayMode")), P("testNames", Strings(50))), "action", "summary"), false));
            tools.Add(Tool("unity_get_editor_actions", "List recent pending/completed Editor actions for local review.", Schema()));
            tools.Add(Tool("unity_get_test_results", "Read the latest bridge-started Unity Test Framework run and bounded failure details. Results persist across domain reloads.", Schema()));
        }

        private static JObject Tool(string name, string description, JObject input, bool readOnly = true)
            => new JObject { ["name"] = name, ["title"] = name.Replace("unity_", "Unity ").Replace('_', ' '),
                ["description"] = description, ["inputSchema"] = input, ["annotations"] = new JObject {
                    ["readOnlyHint"] = readOnly, ["destructiveHint"] = false, ["idempotentHint"] = readOnly, ["openWorldHint"] = false } };
        private static JObject Schema(params JProperty[] properties) => new JObject {
            ["type"] = "object", ["properties"] = new JObject(properties), ["additionalProperties"] = false };
        private static JObject Required(JObject schema, params string[] names) { schema["required"] = new JArray(names); return schema; }
        private static JObject Session() => Required(Schema(P("sessionId", Str())), "sessionId");
        private static JProperty OptId() => P("objectId", Str());
        private static JProperty P(string name, JObject schema) => new JProperty(name, schema);
        private static JObject Str() => new JObject { ["type"] = "string" };
        private static JObject Bool(bool fallback) => new JObject { ["type"] = "boolean", ["default"] = fallback };
        private static JObject Int(int fallback, int min, int max) => new JObject { ["type"] = "integer", ["default"] = fallback, ["minimum"] = min, ["maximum"] = max };
        private static JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        private static JObject Strings(int maximum) => new JObject { ["type"] = "array", ["items"] = Str(), ["maxItems"] = maximum };
        private static JObject Any() => new JObject();
    }
}
