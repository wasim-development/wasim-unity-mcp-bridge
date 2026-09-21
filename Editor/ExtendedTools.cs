using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ExtendedTools
    {
        public static JToken Execute(string name, JObject args)
        {
            switch (name)
            {
                case "unity_inspect_runtime": return RuntimeInspection.Inspect(args);
                case "unity_capture_view": return ViewCapture.Capture(args);
                case "unity_propose_change_set": return ChangeSets.Propose(args);
                case "unity_get_change_sets": return ChangeSets.List(args["includeCompleted"]?.Value<bool>() ?? false, args["includeOperations"]?.Value<bool>() ?? false);
                case "unity_propose_change_set_rollback": return ChangeSets.ProposeRollback((string)args["changeSetId"], (string)args["summary"]);
                case "unity_start_trace": return DiagnosticSessions.StartTrace(args);
                case "unity_read_trace": return DiagnosticSessions.ReadTrace(args);
                case "unity_stop_trace": return DiagnosticSessions.StopTraceTool(args);
                case "unity_validate_vrchat": return ProjectDiagnostics.Validate(args);
                case "unity_find_asset_references": return ProjectDiagnostics.References(args);
                case "unity_check_runtime": return ProjectDiagnostics.RuntimeChecks(args);
                case "unity_start_profiler": return DiagnosticSessions.StartProfiler(args);
                case "unity_read_profiler": return DiagnosticSessions.ReadProfiler(args);
                case "unity_stop_profiler": return DiagnosticSessions.StopProfilerTool(args);
                case "unity_propose_editor_action": return EditorActions.Propose(args);
                case "unity_get_editor_actions": return EditorActions.List();
                case "unity_get_test_results": return EditorActions.TestResults();
                default: throw new ArgumentException("Unknown Unity MCP tool: " + name);
            }
        }

    }
}
