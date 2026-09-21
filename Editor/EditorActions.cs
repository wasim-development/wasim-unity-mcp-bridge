using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    [InitializeOnLoad]
    internal static class EditorActions
    {
        private const string ActiveKey = "WasimMcp.ActiveTestAction";
        private static string Root => Path.Combine(ProjectSecurity.ProjectRoot, "Library", "WasimUnityMcpBridge");
        private static string Index => Path.Combine(Root, "editor-actions.json");
        private static string ResultsPath => Path.Combine(Root, "test-results.json");
        private static readonly string[] Actions = { "enterPlayMode", "exitPlayMode", "pause", "resume", "step", "compile", "runTests" };
        private static readonly TestCallbacks Callbacks = new TestCallbacks();
        private static TestRunnerApi _api;
        private static JArray _records;

        static EditorActions()
        {
            EditorApplication.delayCall += Register;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (_api != null) { _api.UnregisterCallbacks(Callbacks); UnityEngine.Object.DestroyImmediate(_api); }
            };
        }
        private static void Register()
        {
            if (_api != null) return;
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.hideFlags = HideFlags.HideAndDontSave;
            _api.RegisterCallbacks(Callbacks);
        }
        private static JArray Records => _records ?? (_records = File.Exists(Index) ? JArray.Parse(AtomicFile.ReadText(Index)) : new JArray());
        private static void Save() => AtomicFile.WriteText(Index, Records.ToString(Newtonsoft.Json.Formatting.Indented));
        public static JArray List() => new JArray(Records.Reverse().Take(100).Select(r => r.DeepClone()));
        public static JObject TestResults() => File.Exists(ResultsPath) ? AtomicFile.ReadObject(ResultsPath)
            : new JObject { ["status"] = "No bridge-started test run" };
        private static JObject Find(string id) => Records.OfType<JObject>().FirstOrDefault(r => (string)r["id"] == id)
            ?? throw new ArgumentException("Unknown Editor action.");

        public static JObject Propose(JObject args)
        {
            if (!BridgePreferences.EnableEditorActions) throw new InvalidOperationException("Enable reviewed Editor actions in Unity.");
            string action = (string)args["action"], summary = (string)args["summary"];
            if (!Actions.Contains(action) || string.IsNullOrWhiteSpace(summary) || summary.Length > 2000)
                throw new ArgumentException("Provide a supported action and summary of 1-2000 characters.");
            if (Records.OfType<JObject>().Count(r => (string)r["status"] == "Pending") >= 20)
                throw new InvalidOperationException("Review the pending Editor actions first.");
            if (args["testNames"] is JArray names && (names.Count > 50 || names.Any(n => n.Type != JTokenType.String || ((string)n).Length > 500)))
                throw new ArgumentException("Provide up to 50 test names.");
            string mode = (string)args["testMode"] ?? "EditMode";
            if (mode != "EditMode" && mode != "PlayMode") throw new ArgumentException("Invalid test mode.");
            var record = new JObject { ["id"] = Guid.NewGuid().ToString("N").Substring(0, 12), ["action"] = action,
                ["summary"] = summary, ["testMode"] = mode, ["testNames"] = args["testNames"]?.DeepClone() ?? new JArray(),
                ["status"] = "Pending", ["createdUtc"] = DateTime.UtcNow.ToString("O"), ["requiresUnityApproval"] = true };
            Records.Add(record); Save(); return (JObject)record.DeepClone();
        }
        public static void Reject(string id)
        {
            JObject record = Find(id);
            if ((string)record["status"] != "Pending") throw new InvalidOperationException("Action is not pending.");
            record["status"] = "Rejected"; record["requiresUnityApproval"] = false; Save();
        }
        public static void Approve(string id)
        {
            if (!BridgePreferences.EnableEditorActions) throw new InvalidOperationException("Editor actions are disabled.");
            JObject record = Find(id);
            if ((string)record["status"] != "Pending") throw new InvalidOperationException("Action is not pending.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying)
                throw new InvalidOperationException("Wait for Unity to finish its current transition.");
            string action = (string)record["action"];
            if (action == "runTests" || action == "enterPlayMode")
            {
                if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode first.");
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.isDirty || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save all open scenes before starting Play Mode or tests.");
                }
            }
            if ((action == "pause" || action == "resume" || action == "step" || action == "exitPlayMode") && !EditorApplication.isPlaying)
                throw new InvalidOperationException("This action requires Play Mode.");
            if (action == "step" && !EditorApplication.isPaused) throw new InvalidOperationException("Pause Play Mode before stepping.");
            if (action == "runTests" && !string.IsNullOrEmpty(SessionState.GetString(ActiveKey, "")))
                throw new InvalidOperationException("A bridge-started test run is already active.");
            record["status"] = "Accepted"; record["requiresUnityApproval"] = false; Save();
            // Complete the approval GUI event before operations that can reload the domain.
            EditorApplication.delayCall += () =>
            {
                try
                {
                    switch (action)
                    {
                        case "enterPlayMode": EditorApplication.isPlaying = true; break;
                        case "exitPlayMode": EditorApplication.isPlaying = false; break;
                        case "pause": EditorApplication.isPaused = true; break;
                        case "resume": EditorApplication.isPaused = false; break;
                        case "step": EditorApplication.Step(); break;
                        case "compile": CompilationPipeline.RequestScriptCompilation(); break;
                        case "runTests": StartTests(record); break;
                    }
                }
                catch (Exception ex)
                {
                    record["status"] = "Failed"; record["message"] = ex.GetBaseException().Message;
                    if (action == "runTests") SessionState.EraseString(ActiveKey);
                    Save();
                }
            };
        }
        private static void StartTests(JObject record)
        {
            Register();
            var filter = new Filter { testMode = (string)record["testMode"] == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode };
            string[] names = ((JArray)record["testNames"]).Values<string>().ToArray();
            if (names.Length > 0) filter.testNames = names;
            SessionState.SetString(ActiveKey, (string)record["id"]);
            record["status"] = "Running"; Save();
            AtomicFile.WriteText(ResultsPath, new JObject { ["status"] = "Running", ["actionId"] = record["id"],
                ["mode"] = record["testMode"], ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["failures"] = new JArray() }.ToString());
            string runId = _api.Execute(new ExecutionSettings(filter));
            record["testRunId"] = runId; Save();
        }
        private sealed class TestCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result)
            {
                if (string.IsNullOrEmpty(SessionState.GetString(ActiveKey, "")) || result.HasChildren || result.TestStatus != TestStatus.Failed) return;
                JObject report = TestResults();
                var failures = report["failures"] as JArray ?? new JArray();
                if (failures.Count < 100) failures.Add(new JObject { ["test"] = result.FullName,
                    ["message"] = Trim(result.Message), ["stackTrace"] = Trim(result.StackTrace) });
                report["failures"] = failures;
                AtomicFile.WriteText(ResultsPath, report.ToString());
            }
            public void RunFinished(ITestResultAdaptor result)
            {
                string id = SessionState.GetString(ActiveKey, "");
                if (string.IsNullOrEmpty(id)) return;
                JObject report = TestResults();
                report["status"] = result.TestStatus.ToString();
                report["passed"] = result.PassCount; report["failed"] = result.FailCount;
                report["skipped"] = result.SkipCount; report["inconclusive"] = result.InconclusiveCount;
                report["durationSeconds"] = result.Duration; report["finishedUtc"] = DateTime.UtcNow.ToString("O");
                AtomicFile.WriteText(ResultsPath, report.ToString());
                JObject record = Find(id); record["status"] = "Completed"; record["testStatus"] = result.TestStatus.ToString(); Save();
                SessionState.EraseString(ActiveKey);
            }
            private static string Trim(string value) => value == null ? "" : value.Length > 3000 ? value.Substring(0, 3000) : value;
        }
    }
}
