using Newtonsoft.Json.Linq;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class McpToolCatalog
    {
        public static JArray Build()
        {
            return new JArray
            {
                ReadTool("unity_get_status", "Check whether the Unity Editor bridge is alive and report Editor, tunnel, selection and pending-change state.", ObjectSchema()),
                ReadTool("unity_get_project_info", "Read the Unity project name, path, Editor version, platform and active scene.", ObjectSchema()),
                ReadTool("unity_get_compilation_status", "Read current and most recent Unity script compilation status and messages.", ObjectSchema()),
                ReadTool("unity_read_console", "Read bounded entries from the current Unity Console. Internal Console reflection falls back to logs captured while the bridge is loaded.",
                    ObjectSchema(
                        Prop("maximumEntries", IntegerSchema("Maximum entries to return, from 1 to 200.", 50, 1, 200)),
                        Prop("includeLogs", BooleanSchema("Include normal logs.", false)),
                        Prop("includeWarnings", BooleanSchema("Include warnings.", true)),
                        Prop("includeErrors", BooleanSchema("Include errors and exceptions.", true)))),
                ReadTool("unity_read_editor_log", "Read the tail of Unity's Editor log, bounded to the final 2 MB.",
                    ObjectSchema(Prop("tailLines", IntegerSchema("Maximum trailing lines, from 10 to 1000.", 200, 10, 1000)))),
                ReadTool("unity_search_scripts", "Search C# source under Assets and, when enabled in Unity, Packages. Results are bounded.",
                    Required(ObjectSchema(
                        Prop("query", StringSchema("Case-insensitive text to find.")),
                        Prop("maximumResults", IntegerSchema("Maximum matches, from 1 to 100.", 30, 1, 100))), "query")),
                ReadTool("unity_find_script_references", "Find bounded textual references to a class, method, field or other symbol in C# scripts, with optional case-sensitive matching.",
                    Required(ObjectSchema(
                        Prop("symbol", StringSchema("Symbol or text to find.")),
                        Prop("caseSensitive", BooleanSchema("Use case-sensitive matching.", true)),
                        Prop("maximumResults", IntegerSchema("Maximum matches, from 1 to 200.", 80, 1, 200))), "symbol")),
                ReadTool("unity_read_script", "Read a bounded C# script using a project-relative Assets/ or permitted Packages/ path. The response includes a SHA-256 hash for safe change proposals.",
                    Required(ObjectSchema(
                        Prop("path", StringSchema("Project-relative script path, for example Assets/Scripts/Player.cs.")),
                        Prop("startLine", IntegerSchema("First line, one-based.", 1, 1, 1000000)),
                        Prop("endLine", IntegerSchema("Last line, one-based. Omit or use 0 for up to 400 lines.", 0, 0, 1000000))), "path")),
                ReadTool("unity_get_selected_script", "Read the C# script currently selected in the Unity Project window.", ObjectSchema()),
                ReadTool("unity_get_active_scene", "Read active-scene metadata and a bounded hierarchy snapshot.",
                    ObjectSchema(
                        Prop("maximumObjects", IntegerSchema("Maximum hierarchy objects, from 1 to 1000.", 300, 1, 1000)),
                        Prop("maximumDepth", IntegerSchema("Maximum hierarchy depth, from 0 to 20.", 8, 0, 20)))),
                ReadTool("unity_inspect_selected_object", "Inspect the selected GameObject, its components, prefab information and bounded serialized properties.",
                    ObjectSchema(Prop("maximumPropertiesPerComponent", IntegerSchema("Maximum serialized properties per component, from 1 to 200.", 50, 1, 200)))),
                ReadTool("unity_inspect_selected_hierarchy", "Recursively inspect the selected GameObject and its children, including components, missing scripts and missing object references.",
                    ObjectSchema(
                        Prop("maximumObjects", IntegerSchema("Maximum GameObjects to return, from 1 to 1000.", 300, 1, 1000)),
                        Prop("maximumDepth", IntegerSchema("Maximum child depth, from 0 to 20.", 10, 0, 20)),
                        Prop("maximumPropertiesPerComponent", IntegerSchema("Maximum serialized properties per component, from 1 to 200.", 40, 1, 200)))),
                ReadTool("unity_inspect_selected_prefab", "Recursively inspect the selected prefab asset or nearest selected prefab instance root, including child hierarchy, components, missing scripts, missing object references and bounded overrides.",
                    ObjectSchema(
                        Prop("maximumObjects", IntegerSchema("Maximum prefab GameObjects to return, from 1 to 1000.", 300, 1, 1000)),
                        Prop("maximumDepth", IntegerSchema("Maximum child depth, from 0 to 20.", 10, 0, 20)),
                        Prop("maximumPropertiesPerComponent", IntegerSchema("Maximum serialized properties per component, from 1 to 200.", 40, 1, 200)),
                        Prop("maximumOverrides", IntegerSchema("Maximum prefab property overrides to return, from 0 to 500.", 100, 0, 500)))),
                ReadTool("unity_get_selected_asset_info", "Inspect the selected Project asset, including path, type, GUID, labels and bounded direct or recursive dependencies.",
                    ObjectSchema(
                        Prop("recursiveDependencies", BooleanSchema("Include recursive dependencies instead of only direct dependencies.", false)),
                        Prop("maximumDependencies", IntegerSchema("Maximum dependencies, from 1 to 500.", 100, 1, 500)))),
                ReadTool("unity_analyze_selected_folder", "Analyze the folder currently selected in the Unity Project window. Returns a bounded asset inventory plus script declarations, prefab missing-script/reference findings, Animator Controller summaries, compiler messages in the folder, and dependencies outside the folder.",
                    ObjectSchema(
                        Prop("includeSubfolders", BooleanSchema("Include assets in nested folders.", true)),
                        Prop("analyzeScripts", BooleanSchema("Summarize C# scripts, namespaces, declarations, sizes and hashes.", true)),
                        Prop("analyzePrefabs", BooleanSchema("Inspect bounded prefab hierarchies for components, missing scripts and missing object references.", true)),
                        Prop("analyzeAnimatorControllers", BooleanSchema("Summarize Animator Controller parameters, layers, states, transitions, motions and clips.", true)),
                        Prop("includeExternalDependencies", BooleanSchema("Report direct dependencies that are outside the selected folder.", true)),
                        Prop("maximumAssets", IntegerSchema("Maximum assets to inventory and analyze, from 1 to 2000.", 200, 1, 2000)),
                        Prop("maximumDetailedItemsPerType", IntegerSchema("Maximum scripts, prefabs and Animator Controllers to analyze in detail per type, from 1 to 200.", 30, 1, 200)),
                        Prop("maximumIssues", IntegerSchema("Maximum issue records to return, from 1 to 1000.", 150, 1, 1000)),
                        Prop("maximumDependencies", IntegerSchema("Maximum external dependencies to return, from 0 to 1000.", 100, 0, 1000)))),
                ReadTool("unity_inspect_animator_controller", "Inspect the selected Animator component, Animator Controller asset, or Animator Override Controller asset. Returns controller parameters, layers, nested state machines, states, Any State and Entry transitions, state transitions and conditions, blend trees, motions, animation clips, behaviours, and override mappings.",
                    ObjectSchema(
                        Prop("searchChildren", BooleanSchema("Search children when the selected GameObject has no Animator.", true)),
                        Prop("maximumStates", IntegerSchema("Maximum states across all layers, from 1 to 1000.", 250, 1, 1000)),
                        Prop("maximumTransitions", IntegerSchema("Maximum transitions across all state machines, from 1 to 2000.", 600, 1, 2000)),
                        Prop("maximumMotions", IntegerSchema("Maximum motion and blend-tree nodes, from 1 to 2000.", 500, 1, 2000)))),
                ReadTool("unity_get_animator_info", "Compatibility alias for unity_inspect_animator_controller. Inspect the selected Animator Controller graph, including parameters, layers, states, transitions, conditions, blend trees, motions and animation clips.",
                    ObjectSchema(
                        Prop("searchChildren", BooleanSchema("Search children when the selected GameObject has no Animator.", true)),
                        Prop("maximumStates", IntegerSchema("Maximum states across all layers, from 1 to 1000.", 250, 1, 1000)),
                        Prop("maximumTransitions", IntegerSchema("Maximum transitions across all state machines, from 1 to 2000.", 600, 1, 2000)),
                        Prop("maximumMotions", IntegerSchema("Maximum motion and blend-tree nodes, from 1 to 2000.", 500, 1, 2000)))),
                ReadTool("unity_get_packages", "Read Packages/manifest.json and report its direct package dependencies.", ObjectSchema()),
                ReadTool("unity_get_script_change_proposals", "List pending or recent script replacement proposals. Proposals do not modify Assets until approved in Unity.",
                    ObjectSchema(Prop("includeCompleted", BooleanSchema("Include applied, rejected, failed and superseded proposals.", false)))),
                WriteTool("unity_propose_script_text_patch",
                    "Submit one exact oldText-to-newText patch for an existing Assets/**/*.cs file. The old text must occur exactly once. This creates a pending proposal only; Unity does not modify the script until the user reviews and approves it in the bridge window. Provide the SHA-256 returned by unity_read_script to prevent stale edits.",
                    Required(ObjectSchema(
                        Prop("path", StringSchema("Existing project-relative Assets/**/*.cs path.")),
                        Prop("expectedSha256", StringSchema("Current script SHA-256 from unity_read_script.")),
                        Prop("oldText", StringSchema("Exact existing code block to replace. It must occur exactly once in the current script.")),
                        Prop("newText", StringSchema("Replacement code block. May be empty only when intentionally removing the matched block.")),
                        Prop("summary", StringSchema("Brief explanation of the intended change."))),
                        "path", "expectedSha256", "oldText", "newText", "summary")),
                WriteTool("unity_propose_script_replacement",
                    "Submit a complete replacement for one existing Assets/**/*.cs file. This never edits the script immediately: it creates a pending proposal that must be reviewed and approved in the Unity bridge window. Provide the SHA-256 returned by unity_read_script to prevent stale edits.",
                    Required(ObjectSchema(
                        Prop("path", StringSchema("Existing project-relative Assets/**/*.cs path.")),
                        Prop("expectedSha256", StringSchema("Current script SHA-256 from unity_read_script.")),
                        Prop("newContent", StringSchema("Complete proposed C# file content, not a partial patch.")),
                        Prop("summary", StringSchema("Brief explanation of the intended change."))),
                        "path", "expectedSha256", "newContent", "summary"))
            };
        }

        private static JObject ReadTool(string name, string description, JObject inputSchema)
        {
            return Tool(name, description, inputSchema, true, false, true);
        }

        private static JObject WriteTool(string name, string description, JObject inputSchema)
        {
            return Tool(name, description, inputSchema, false, false, false);
        }

        private static JObject Tool(string name, string description, JObject inputSchema, bool readOnly, bool destructive, bool idempotent)
        {
            return new JObject
            {
                ["name"] = name,
                ["title"] = name.Replace("unity_", "Unity ").Replace('_', ' '),
                ["description"] = description,
                ["inputSchema"] = inputSchema,
                ["annotations"] = new JObject
                {
                    ["readOnlyHint"] = readOnly,
                    ["destructiveHint"] = destructive,
                    ["idempotentHint"] = idempotent,
                    ["openWorldHint"] = false
                }
            };
        }

        private static JObject Required(JObject schema, params string[] names)
        {
            if (names != null && names.Length > 0) schema["required"] = new JArray(names);
            return schema;
        }

        private static JObject ObjectSchema(params JProperty[] properties)
        {
            JObject propertyObject = new JObject();
            if (properties != null)
            {
                foreach (JProperty property in properties) propertyObject.Add(property);
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = propertyObject,
                ["additionalProperties"] = false
            };
        }

        private static JProperty Prop(string name, JObject schema) => new JProperty(name, schema);
        private static JObject StringSchema(string description) => new JObject { ["type"] = "string", ["description"] = description };
        private static JObject BooleanSchema(string description, bool defaultValue) => new JObject { ["type"] = "boolean", ["description"] = description, ["default"] = defaultValue };
        private static JObject IntegerSchema(string description, int defaultValue, int minimum, int maximum) => new JObject
        {
            ["type"] = "integer", ["description"] = description, ["default"] = defaultValue,
            ["minimum"] = minimum, ["maximum"] = maximum
        };
    }
}
