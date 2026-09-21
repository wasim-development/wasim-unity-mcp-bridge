using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    [InitializeOnLoad]
    internal static class DiagnosticSessions
    {
        private static readonly List<JObject> Events = new List<JObject>();
        private static string _traceId;
        private static GameObject _traceRoot;
        private static JObject _previous;
        private static double _traceEnd, _nextTrace, _interval;
        private static int _sequence, _traceMaximum;
        private static JArray _variables;
        private static bool _traceRunning;
        private static string _traceReason = "";
        private static string _profileId;
        private static double _profileEnd, _nextProfile;
        private static bool _profileRunning;
        private static readonly List<JObject> Samples = new List<JObject>();
        private static readonly Dictionary<string, ProfilerRecorder> Recorders = new Dictionary<string, ProfilerRecorder>();
        private static readonly List<string> Unavailable = new List<string>();

        static DiagnosticSessions()
        {
            EditorApplication.update += Update;
            Application.logMessageReceived += OnLog;
            AssemblyReloadEvents.beforeAssemblyReload += () => { StopTrace("Assembly reload"); StopProfiler(); };
            EditorApplication.quitting += StopProfiler;
        }

        public static JObject StartTrace(JObject args)
        {
            if (!EditorApplication.isPlaying) throw new InvalidOperationException("Start Play Mode / ClientSim before recording a runtime trace.");
            if (_traceRunning) throw new InvalidOperationException("Stop the existing trace first.");
            _traceRoot = ObjectAccess.SelectedOrId(args);
            _traceMaximum = ObjectAccess.Limit(args, "maximumObjects", 5, 1, 20);
            _variables = args["udonVariables"] as JArray;
            _interval = ObjectAccess.Limit(args, "intervalMilliseconds", 200, 50, 2000) / 1000d;
            _traceEnd = EditorApplication.timeSinceStartup + ObjectAccess.Limit(args, "durationSeconds", 20, 1, 120);
            _traceId = Guid.NewGuid().ToString("N"); _sequence = 0; Events.Clear();
            _previous = null; _nextTrace = 0; _traceReason = ""; _traceRunning = true;
            return new JObject { ["sessionId"] = _traceId, ["running"] = true,
                ["note"] = "Polling detects state changes; exact callbacks require [WDMCP_EVENT] instrumentation. Read incrementally using afterSequence." };
        }

        private static JObject Snapshot()
        {
            var result = new JObject();
            foreach (GameObject go in ObjectAccess.Walk(_traceRoot, _traceMaximum))
            {
                string id = ObjectAccess.Id(go);
                result[id + "/active"] = go.activeInHierarchy;
                try { result[id + "/owner"] = ObjectAccess.Value(ObjectAccess.Owner(go)); } catch { }
                foreach (Component component in go.GetComponents<Component>())
                {
                    if (component == null) continue;
                    string type = component.GetType().FullName ?? "";
                    // Transform is deliberately excluded: continuous movement must not flood pickup event history.
                    if (!(component is Rigidbody) && !(component is Joint) && !(component is Collider)
                        && !type.StartsWith("VRC.", StringComparison.Ordinal)) continue;
                    JObject data = RuntimeInspection.ComponentValues(component, _variables);
                    foreach (JProperty property in data.Properties())
                    {
                        if (property.Name == "velocity" || property.Name == "angularVelocity" || property.Name.StartsWith("bounds", StringComparison.Ordinal)) continue;
                        result[ObjectAccess.Id(component) + "/" + property.Name] = property.Value.DeepClone();
                    }
                }
            }
            return result;
        }
        private static void Event(string kind, string key, JToken before, JToken after)
        {
            Events.Add(new JObject { ["sequence"] = ++_sequence, ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
                ["kind"] = kind, ["key"] = key, ["before"] = before?.DeepClone(), ["after"] = after?.DeepClone() });
            if (Events.Count > 2000) Events.RemoveAt(0);
        }
        private static void OnLog(string message, string stack, LogType type)
        {
            if (!_traceRunning || !message.StartsWith("[WDMCP_EVENT]", StringComparison.Ordinal)) return;
            Event("instrumented-log", type.ToString(), null, message.Length > 2000 ? message.Substring(0, 2000) : message);
        }
        public static JObject ReadTrace(JObject args)
        {
            CheckSession(args, _traceId);
            int after = ObjectAccess.Limit(args, "afterSequence", 0, 0, int.MaxValue);
            int maximum = ObjectAccess.Limit(args, "maximumEntries", 100, 1, 200);
            var selected = Events.Where(e => (int)e["sequence"] > after).Take(maximum).ToArray();
            return new JObject { ["sessionId"] = _traceId, ["running"] = _traceRunning, ["stopReason"] = _traceReason,
                ["firstAvailableSequence"] = Events.Count == 0 ? 0 : (int)Events[0]["sequence"],
                ["lastSequence"] = _sequence, ["nextAfterSequence"] = selected.Length == 0 ? after : (int)selected.Last()["sequence"],
                ["events"] = new JArray(selected.Select(e => e.DeepClone())) };
        }
        public static JObject StopTraceTool(JObject args) { CheckSession(args, _traceId); StopTrace("Requested"); return ReadTrace(args); }
        private static void StopTrace(string reason) { _traceRunning = false; _traceReason = reason; }
        private static void CheckSession(JObject args, string actual)
        {
            if (actual == null || args["sessionId"]?.Value<string>() != actual) throw new ArgumentException("Unknown session (sessions reset on domain reload).");
        }

        public static JObject StartProfiler(JObject args)
        {
            if (_profileRunning) throw new InvalidOperationException("A profiler capture is already running.");
            StopProfiler(); Samples.Clear(); Unavailable.Clear();
            AddRecorder("mainThreadNanoseconds", ProfilerCategory.Internal, "Main Thread");
            AddRecorder("gcAllocatedBytes", ProfilerCategory.Memory, "GC Allocated In Frame");
            AddRecorder("totalUsedMemoryBytes", ProfilerCategory.Memory, "Total Used Memory");
            AddRecorder("drawCalls", ProfilerCategory.Render, "Draw Calls Count");
            if (Recorders.Count == 0) throw new InvalidOperationException("Requested profiler counters are unavailable in this Editor.");
            _profileId = Guid.NewGuid().ToString("N");
            _profileEnd = EditorApplication.timeSinceStartup + ObjectAccess.Limit(args, "durationSeconds", 5, 1, 30);
            _nextProfile = 0; _profileRunning = true;
            return new JObject { ["sessionId"] = _profileId, ["running"] = true, ["unavailableCounters"] = new JArray(Unavailable) };
        }
        private static void AddRecorder(string key, ProfilerCategory category, string name)
        {
            try
            {
                ProfilerRecorder recorder = ProfilerRecorder.StartNew(category, name, 1);
                if (recorder.Valid) Recorders.Add(key, recorder);
                else { recorder.Dispose(); Unavailable.Add(name); }
            }
            catch { Unavailable.Add(name); }
        }
        public static JObject ReadProfiler(JObject args)
        {
            CheckSession(args, _profileId);
            var metrics = new JObject();
            foreach (string key in Samples.SelectMany(s => s.Properties()).Select(p => p.Name).Where(k => k != "timestampUtc").Distinct())
            {
                double[] values = Samples.Where(s => s[key] != null).Select(s => s[key].Value<double>()).OrderBy(x => x).ToArray();
                if (values.Length > 0) metrics[key] = new JObject { ["mean"] = values.Average(), ["maximum"] = values.Last(),
                    ["p95"] = values[Math.Min(values.Length - 1, (int)Math.Floor(values.Length * .95))] };
            }
            return new JObject { ["sessionId"] = _profileId, ["running"] = _profileRunning, ["sampleCount"] = Samples.Count,
                ["samplingIntervalMilliseconds"] = 100, ["metrics"] = metrics, ["unavailableCounters"] = new JArray(Unavailable),
                ["scope"] = "Sampled Unity Editor counters, not device FPS or per-script CPU attribution." };
        }
        public static JObject StopProfilerTool(JObject args) { CheckSession(args, _profileId); StopProfiler(); return ReadProfiler(args); }
        private static void StopProfiler()
        { foreach (ProfilerRecorder recorder in Recorders.Values) recorder.Dispose(); Recorders.Clear(); _profileRunning = false; }

        private static void Update()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_traceRunning)
            {
                if (_traceRoot == null || !EditorApplication.isPlaying || now >= _traceEnd) StopTrace("Duration elapsed, target destroyed or Play Mode ended");
                else if (now >= _nextTrace)
                {
                    _nextTrace = now + _interval;
                    try
                    {
                        JObject current = Snapshot();
                        if (_previous == null) Event("initial-state", "snapshot", null, current);
                        else
                        {
                            foreach (JProperty property in current.Properties())
                                if (!JToken.DeepEquals(_previous[property.Name], property.Value))
                                    Event("sampled-change", property.Name, _previous[property.Name], property.Value);
                            foreach (JProperty property in _previous.Properties())
                                if (current.Property(property.Name) == null) Event("removed", property.Name, property.Value, null);
                        }
                        _previous = current;
                    }
                    catch (Exception ex) { Event("error", "sampling", null, ex.GetBaseException().Message); StopTrace("Sampling error"); }
                }
            }
            if (_profileRunning)
            {
                if (now >= _profileEnd) StopProfiler();
                else if (now >= _nextProfile)
                {
                    _nextProfile = now + .1;
                    var sample = new JObject { ["timestampUtc"] = DateTime.UtcNow.ToString("O") };
                    foreach (var pair in Recorders) if (pair.Value.Valid && pair.Value.Count > 0) sample[pair.Key] = pair.Value.LastValue;
                    Samples.Add(sample);
                }
            }
        }
    }
}
