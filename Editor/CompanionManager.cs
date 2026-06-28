using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;

namespace WasimDevelopment.UnityMcpBridge
{
    internal sealed class CompanionStatus
    {
        public string State = "Stopped";
        public string CompanionVersion = string.Empty;
        public int ProcessId;
        public string LocalEndpoint = string.Empty;
        public string PublicUrl = string.Empty;
        public string McpEndpoint = string.Empty;
        public string NgrokState = "Stopped";
        public int NgrokProcessId;
        public string LastError = string.Empty;
        public string LastRequestUtc = string.Empty;
        public string StartedUtc = string.Empty;
        public bool UnityAvailable;
    }

    [InitializeOnLoad]
    internal static class CompanionManager
    {
        private const double PollIntervalSeconds = 0.5d;
        private static double _nextPoll;
        private static DateTime _lastStatusWriteUtc;
        private static CompanionStatus _cachedStatus = new CompanionStatus();

        public static event Action StateChanged;

        static CompanionManager()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            RefreshStatus();
        }

        public static CompanionStatus Status
        {
            get { RefreshStatusIfNeeded(); return _cachedStatus; }
        }

        public static bool IsRunning
        {
            get
            {
                CompanionStatus status = Status;
                return status.ProcessId > 0 && IsProcessAlive(status.ProcessId)
                    && !string.Equals(status.State, "Stopped", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(status.State, "Error", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string PublicUrl => Status.PublicUrl ?? string.Empty;
        public static string PublicEndpoint => !string.IsNullOrWhiteSpace(Status.McpEndpoint)
            ? Status.McpEndpoint
            : BridgePreferences.BuildPublicEndpoint(Status.PublicUrl);

        public static void Start()
        {
            CompanionIpc.Initialize();
            CompanionIpc.WriteConfig();
            CompanionIpc.WriteCatalog();
            CompanionIpc.ClearStopSignal();
            RefreshStatus();
            if (IsRunning) return;

            string scriptPath = GetCompanionScriptPath();
            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("The PowerShell companion script was not found.", scriptPath);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = BridgePreferences.PowerShellExecutablePath,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" -ConfigPath \"" + CompanionIpc.ConfigPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = CompanionIpc.RootPath
            };
            Process process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("PowerShell did not start the companion process.");
            EditorApplication.delayCall += RefreshStatus;
        }

        public static void Stop()
        {
            CompanionIpc.SignalStop();
            EditorApplication.delayCall += RefreshStatus;
        }

        public static void ForceStop()
        {
            CompanionStatus status = Status;
            CompanionIpc.SignalStop();
            TryKill(status.NgrokProcessId);
            TryKill(status.ProcessId);
            RefreshStatus();
        }

        public static void Restart()
        {
            CompanionStatus previous = Status;
            CompanionIpc.SignalStop();
            double startAt = EditorApplication.timeSinceStartup + 0.75d;
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                if (EditorApplication.timeSinceStartup < startAt && IsProcessAlive(previous.ProcessId)) return;
                EditorApplication.update -= callback;
                try { Start(); }
                catch (Exception ex) { UnityEngine.Debug.LogError("WDMCP companion restart failed: " + ex.Message); }
            };
            EditorApplication.update += callback;
        }

        public static void OpenCompanionFolder()
        {
            CompanionIpc.Initialize();
            EditorUtility.RevealInFinder(CompanionIpc.RootPath);
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollIntervalSeconds;
            RefreshStatusIfNeeded();
        }

        private static void RefreshStatusIfNeeded()
        {
            DateTime writeTime = File.Exists(CompanionIpc.CompanionStatusPath)
                ? File.GetLastWriteTimeUtc(CompanionIpc.CompanionStatusPath)
                : DateTime.MinValue;
            if (writeTime != _lastStatusWriteUtc) RefreshStatus();
        }

        private static void RefreshStatus()
        {
            CompanionStatus next = new CompanionStatus();
            try
            {
                if (File.Exists(CompanionIpc.CompanionStatusPath))
                {
                    JObject json = JObject.Parse(File.ReadAllText(CompanionIpc.CompanionStatusPath));
                    next.State = json["state"]?.Value<string>() ?? "Stopped";
                    next.CompanionVersion = json["companionVersion"]?.Value<string>() ?? string.Empty;
                    next.ProcessId = json["processId"]?.Value<int>() ?? 0;
                    next.LocalEndpoint = json["localEndpoint"]?.Value<string>() ?? string.Empty;
                    next.PublicUrl = json["publicUrl"]?.Value<string>() ?? string.Empty;
                    next.McpEndpoint = json["mcpEndpoint"]?.Value<string>() ?? string.Empty;
                    next.NgrokState = json["ngrokState"]?.Value<string>() ?? "Stopped";
                    next.NgrokProcessId = json["ngrokProcessId"]?.Value<int>() ?? 0;
                    next.LastError = json["lastError"]?.Value<string>() ?? string.Empty;
                    next.LastRequestUtc = json["lastRequestUtc"]?.Value<string>() ?? string.Empty;
                    next.StartedUtc = json["startedUtc"]?.Value<string>() ?? string.Empty;
                    next.UnityAvailable = json["unityAvailable"]?.Value<bool>() ?? false;
                    _lastStatusWriteUtc = File.GetLastWriteTimeUtc(CompanionIpc.CompanionStatusPath);
                }
            }
            catch (Exception ex)
            {
                next.State = "Error";
                next.LastError = "Unable to read companion status: " + ex.Message;
            }

            bool changed = next.State != _cachedStatus.State
                || next.ProcessId != _cachedStatus.ProcessId
                || next.PublicUrl != _cachedStatus.PublicUrl
                || next.LastError != _cachedStatus.LastError
                || next.UnityAvailable != _cachedStatus.UnityAvailable
                || next.NgrokState != _cachedStatus.NgrokState;
            _cachedStatus = next;
            if (changed)
            {
                try { StateChanged?.Invoke(); } catch { }
            }
        }

        private static string GetCompanionScriptPath()
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CompanionManager).Assembly);
            if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath))
                throw new InvalidOperationException("Unity Package Manager could not resolve the WDMCP package path.");
            return Path.Combine(package.resolvedPath, "Companion~", "wdmcp-companion.ps1");
        }

        private static bool IsProcessAlive(int processId)
        {
            if (processId <= 0) return false;
            try
            {
                Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch { return false; }
        }

        private static void TryKill(int processId)
        {
            if (processId <= 0) return;
            try
            {
                Process process = Process.GetProcessById(processId);
                if (!process.HasExited) process.Kill();
            }
            catch { }
        }
    }
}
