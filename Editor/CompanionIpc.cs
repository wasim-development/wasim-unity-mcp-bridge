using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    [InitializeOnLoad]
    internal static class CompanionIpc
    {
        private const double HeartbeatIntervalSeconds = 1.0d;
        private const int MaximumRequestsPerUpdate = 3;
        private static double _nextHeartbeatTime;
        private static bool _initialized;

        public static string RootPath => Path.Combine(ProjectSecurity.ProjectRoot, "Library", "WasimUnityMcpBridge", "Companion");
        public static string RequestsPath => Path.Combine(RootPath, "Requests");
        public static string ProcessingPath => Path.Combine(RootPath, "Processing");
        public static string ResponsesPath => Path.Combine(RootPath, "Responses");
        public static string ConfigPath => Path.Combine(RootPath, "companion-config.json");
        public static string CatalogPath => Path.Combine(RootPath, "tool-catalog.json");
        public static string UnityStatusPath => Path.Combine(RootPath, "unity-status.json");
        public static string CompanionStatusPath => Path.Combine(RootPath, "companion-status.json");
        public static string StopFlagPath => Path.Combine(RootPath, "stop.flag");
        public static string CompanionLogPath => Path.Combine(RootPath, "companion.log");
        public static string NgrokLogPath => Path.Combine(RootPath, "ngrok.log");

        static CompanionIpc()
        {
            Initialize();
        }

        public static void Initialize()
        {
            EnsureDirectories();
            RecoverInterruptedRequests();
            WriteCatalog();
            WriteConfig();
            WriteUnityStatus("ready");

            if (_initialized) return;
            _initialized = true;
            EditorApplication.update -= Update;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
        }

        public static void WriteConfig()
        {
            EnsureDirectories();
            JObject config = new JObject
            {
                ["companionVersion"] = BridgeVersion.PackageVersion,
                ["projectRoot"] = ProjectSecurity.ProjectRoot.Replace('\\', '/'),
                ["ipcRoot"] = RootPath.Replace('\\', '/'),
                ["requestsPath"] = RequestsPath.Replace('\\', '/'),
                ["responsesPath"] = ResponsesPath.Replace('\\', '/'),
                ["catalogPath"] = CatalogPath.Replace('\\', '/'),
                ["unityStatusPath"] = UnityStatusPath.Replace('\\', '/'),
                ["companionStatusPath"] = CompanionStatusPath.Replace('\\', '/'),
                ["stopFlagPath"] = StopFlagPath.Replace('\\', '/'),
                ["companionLogPath"] = CompanionLogPath.Replace('\\', '/'),
                ["ngrokLogPath"] = NgrokLogPath.Replace('\\', '/'),
                ["port"] = BridgePreferences.Port,
                ["capabilityToken"] = BridgePreferences.CapabilityToken,
                ["requestTimeoutSeconds"] = BridgePreferences.ToolRequestTimeoutSeconds,
                ["autoStartNgrok"] = BridgePreferences.AutoStartNgrok,
                ["stopNgrokWithCompanion"] = BridgePreferences.StopNgrokWithBridge,
                ["ngrokExecutablePath"] = BridgePreferences.NgrokExecutablePath,
                ["stableNgrokUrl"] = BridgePreferences.StableNgrokUrl
            };
            AtomicWrite(ConfigPath, config.ToString(Formatting.Indented));
        }

        public static void WriteCatalog()
        {
            EnsureDirectories();
            AtomicWrite(CatalogPath, McpToolCatalog.Build().ToString(Formatting.Indented));
        }

        public static void WriteUnityStatus(string lifecycleState)
        {
            EnsureDirectories();
            JObject status = new JObject
            {
                ["bridgeVersion"] = BridgeVersion.PackageVersion,
                ["unityProcessId"] = Process.GetCurrentProcess().Id,
                ["projectName"] = Application.productName,
                ["unityVersion"] = Application.unityVersion,
                ["lifecycleState"] = lifecycleState ?? string.Empty,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["isUpdating"] = EditorApplication.isUpdating,
                ["isPlaying"] = EditorApplication.isPlaying,
                ["selectedObject"] = Selection.activeObject == null ? null : Selection.activeObject.name,
                ["timestampUtc"] = DateTime.UtcNow.ToString("O")
            };
            AtomicWrite(UnityStatusPath, status.ToString(Formatting.None));
        }

        public static bool IsHeartbeatFresh(double maximumAgeSeconds = 4d)
        {
            try
            {
                if (!File.Exists(UnityStatusPath)) return false;
                JObject status = JObject.Parse(File.ReadAllText(UnityStatusPath));
                if (!DateTime.TryParse(status["timestampUtc"]?.Value<string>(), out DateTime time)) return false;
                return (DateTime.UtcNow - time.ToUniversalTime()).TotalSeconds <= maximumAgeSeconds;
            }
            catch { return false; }
        }

        public static void SignalStop()
        {
            EnsureDirectories();
            File.WriteAllText(StopFlagPath, DateTime.UtcNow.ToString("O"), new UTF8Encoding(false));
        }

        public static void ClearStopSignal()
        {
            try { if (File.Exists(StopFlagPath)) File.Delete(StopFlagPath); } catch { }
        }

        private static void Update()
        {
            if (EditorApplication.timeSinceStartup >= _nextHeartbeatTime)
            {
                _nextHeartbeatTime = EditorApplication.timeSinceStartup + HeartbeatIntervalSeconds;
                WriteUnityStatus(EditorApplication.isCompiling ? "compiling" : "ready");
                WriteCatalogIfMissing();
            }
            ProcessPendingRequests();
        }

        private static void BeforeAssemblyReload()
        {
            WriteUnityStatus("reloading");
        }

        private static void ProcessPendingRequests()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            string[] files;
            try
            {
                files = Directory.GetFiles(RequestsPath, "*.json")
                    .OrderBy(File.GetCreationTimeUtc)
                    .Take(MaximumRequestsPerUpdate)
                    .ToArray();
            }
            catch { return; }

            foreach (string requestPath in files)
                ProcessOne(requestPath);
        }

        private static void ProcessOne(string requestPath)
        {
            string fileName = Path.GetFileName(requestPath);
            string processingPath = Path.Combine(ProcessingPath, fileName);
            try
            {
                File.Move(requestPath, processingPath);
            }
            catch { return; }

            string requestId = Path.GetFileNameWithoutExtension(fileName);
            try
            {
                JObject request = JObject.Parse(File.ReadAllText(processingPath));
                requestId = request["id"]?.Value<string>() ?? requestId;
                string toolName = request["name"]?.Value<string>() ?? string.Empty;
                JObject arguments = request["arguments"] as JObject ?? new JObject();
                if (string.IsNullOrWhiteSpace(toolName)) throw new InvalidOperationException("IPC request has no tool name.");

                JToken result = UnityToolExecutor.Execute(toolName, arguments);
                JObject response = new JObject
                {
                    ["id"] = requestId,
                    ["success"] = true,
                    ["result"] = result,
                    ["completedUtc"] = DateTime.UtcNow.ToString("O")
                };
                AtomicWrite(Path.Combine(ResponsesPath, requestId + ".json"), response.ToString(Formatting.None));
                BridgeRequestHistory.Add("tools/call", toolName, true, "Completed through companion IPC");
            }
            catch (Exception ex)
            {
                Exception actual = ex.GetBaseException();
                JObject response = new JObject
                {
                    ["id"] = requestId,
                    ["success"] = false,
                    ["error"] = actual.Message,
                    ["completedUtc"] = DateTime.UtcNow.ToString("O")
                };
                AtomicWrite(Path.Combine(ResponsesPath, requestId + ".json"), response.ToString(Formatting.None));
                BridgeRequestHistory.Add("tools/call", string.Empty, false, actual.Message);
            }
            finally
            {
                try { if (File.Exists(processingPath)) File.Delete(processingPath); } catch { }
            }
        }

        private static void RecoverInterruptedRequests()
        {
            EnsureDirectories();
            string[] files;
            try { files = Directory.GetFiles(ProcessingPath, "*.json"); }
            catch { return; }
            foreach (string file in files)
            {
                try
                {
                    string destination = Path.Combine(RequestsPath, Path.GetFileName(file));
                    if (File.Exists(destination)) File.Delete(file);
                    else File.Move(file, destination);
                }
                catch { }
            }
        }

        private static void WriteCatalogIfMissing()
        {
            if (!File.Exists(CatalogPath)) WriteCatalog();
            if (!File.Exists(ConfigPath)) WriteConfig();
        }

        private static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(RequestsPath);
            Directory.CreateDirectory(ProcessingPath);
            Directory.CreateDirectory(ResponsesPath);
        }

        private static void AtomicWrite(string path, string content)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, content ?? string.Empty, new UTF8Encoding(false));
            try
            {
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
