using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal sealed class BridgeWindow : EditorWindow
    {
        private Vector2 _scroll;
        private bool _showSettings = true;
        private bool _showPendingChanges;
        private bool _showHistory = true;
        private string _message = string.Empty;
        private MessageType _messageType = MessageType.None;

        [MenuItem("Window/Wasim Development/Unity MCP Bridge")]
        public static void Open() => GetWindow<BridgeWindow>("Unity MCP Bridge");

        private void OnEnable()
        {
            minSize = new Vector2(620f, 650f);
            CompanionManager.StateChanged += Repaint;
            BridgeRequestHistory.Changed += Repaint;
            BridgeSelfTest.Changed += Repaint;
            ScriptChangeManager.Changed += Repaint;
        }

        private void OnDisable()
        {
            CompanionManager.StateChanged -= Repaint;
            BridgeRequestHistory.Changed -= Repaint;
            BridgeSelfTest.Changed -= Repaint;
            ScriptChangeManager.Changed -= Repaint;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField(BridgeVersion.DisplayName + "  v" + BridgeVersion.PackageVersion, EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "The MCP socket and ngrok now run in a standalone PowerShell companion. Unity only processes project-inspection requests through a Library-based IPC queue, so script compilation and domain reload no longer release or rebind port 38421.",
                MessageType.Info);
            EditorGUILayout.Space(8);
            DrawHealth();
            EditorGUILayout.Space(10);
            DrawControls();
            EditorGUILayout.Space(10);
            DrawEndpoints();
            EditorGUILayout.Space(10);
            DrawSettings();
            EditorGUILayout.Space(10);
            DrawPermissions();
            EditorGUILayout.Space(10);
            DrawPendingChanges();
            EditorGUILayout.Space(10);
            DrawHistory();
            EditorGUILayout.EndScrollView();
        }

        private void DrawHealth()
        {
            CompanionStatus status = CompanionManager.Status;
            EditorGUILayout.LabelField("Connection health", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Standalone companion", CompanionManager.IsRunning ? status.State + " (PID " + status.ProcessId + ")" : status.State);
            EditorGUILayout.LabelField("Companion version", string.IsNullOrWhiteSpace(status.CompanionVersion) ? "—" : status.CompanionVersion);
            EditorGUILayout.LabelField("Unity IPC", status.UnityAvailable && CompanionIpc.IsHeartbeatFresh() ? "Connected" : "Waiting for Unity / reloading");
            EditorGUILayout.LabelField("ngrok", status.NgrokState + (status.NgrokProcessId > 0 ? " (PID " + status.NgrokProcessId + ")" : string.Empty));
            EditorGUILayout.LabelField("Pending script proposals", ScriptChangeManager.PendingCount.ToString());
            if (!string.IsNullOrWhiteSpace(status.LastError)) EditorGUILayout.HelpBox(status.LastError, MessageType.Error);
            if (!string.IsNullOrWhiteSpace(_message)) EditorGUILayout.HelpBox(_message, _messageType);
            EditorGUILayout.EndVertical();
        }

        private void DrawControls()
        {
            EditorGUILayout.LabelField("Companion controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(CompanionManager.IsRunning);
            if (GUILayout.Button("Start Companion", GUILayout.Height(30))) RunAction(BridgeBootstrap.StartRequested, "Companion start requested.");
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(!CompanionManager.IsRunning);
            if (GUILayout.Button("Stop", GUILayout.Height(30))) RunAction(BridgeBootstrap.StopRequested, "Companion stop requested.");
            if (GUILayout.Button("Restart", GUILayout.Height(30))) RunAction(BridgeBootstrap.RestartRequested, "Companion restart requested.");
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Force Stop", GUILayout.Height(30))) RunAction(CompanionManager.ForceStop, "Companion processes were force-stopped.");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(!CompanionManager.IsRunning || BridgeSelfTest.State == BridgeSelfTestState.Running);
            if (GUILayout.Button("Local MCP Self-Test")) BridgeSelfTest.Run();
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("Open Companion Folder")) CompanionManager.OpenCompanionFolder();
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrWhiteSpace(BridgeSelfTest.LastResult))
                EditorGUILayout.HelpBox(BridgeSelfTest.LastResult, BridgeSelfTest.State == BridgeSelfTestState.Passed ? MessageType.Info : MessageType.Warning);
        }

        private static void DrawEndpoints()
        {
            CompanionStatus status = CompanionManager.Status;
            EditorGUILayout.LabelField("Endpoints", EditorStyles.boldLabel);
            DrawCopyableField("Local MCP URL", BridgePreferences.LocalEndpoint);
            DrawCopyableField("Detected HTTPS URL", status.PublicUrl);

            bool reveal = BridgePreferences.RevealPrivateEndpoint;
            bool next = EditorGUILayout.ToggleLeft("Show private ChatGPT MCP URL", reveal);
            if (next != reveal) BridgePreferences.RevealPrivateEndpoint = next;
            string privateEndpoint = CompanionManager.PublicEndpoint;
            DrawCopyableField("ChatGPT MCP URL", reveal ? privateEndpoint : Mask(privateEndpoint));
        }

        private void DrawSettings()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Companion and tunnel settings", true);
            if (!_showSettings) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            bool autoStart = EditorGUILayout.Toggle("Auto-start companion", BridgePreferences.AutoStart);
            bool stopWithUnity = EditorGUILayout.Toggle("Stop companion with Unity", BridgePreferences.StopCompanionWithUnity);
            bool autoNgrok = EditorGUILayout.Toggle("Start ngrok in companion", BridgePreferences.AutoStartNgrok);
            bool stopNgrok = EditorGUILayout.Toggle("Stop owned ngrok", BridgePreferences.StopNgrokWithBridge);
            int port = EditorGUILayout.IntField("MCP port", BridgePreferences.Port);
            int timeout = EditorGUILayout.IntSlider("Tool timeout (seconds)", BridgePreferences.ToolRequestTimeoutSeconds, 10, 120);
            string powershell = EditorGUILayout.TextField("PowerShell executable", BridgePreferences.PowerShellExecutablePath);
            string ngrok = EditorGUILayout.TextField("ngrok executable", BridgePreferences.NgrokExecutablePath);
            string stable = EditorGUILayout.TextField("Optional stable ngrok URL", BridgePreferences.StableNgrokUrl);

            bool changed = autoStart != BridgePreferences.AutoStart
                || stopWithUnity != BridgePreferences.StopCompanionWithUnity
                || autoNgrok != BridgePreferences.AutoStartNgrok
                || stopNgrok != BridgePreferences.StopNgrokWithBridge
                || port != BridgePreferences.Port
                || timeout != BridgePreferences.ToolRequestTimeoutSeconds
                || powershell != BridgePreferences.PowerShellExecutablePath
                || ngrok != BridgePreferences.NgrokExecutablePath
                || stable != BridgePreferences.StableNgrokUrl;

            if (changed)
            {
                BridgePreferences.AutoStart = autoStart;
                BridgePreferences.StopCompanionWithUnity = stopWithUnity;
                BridgePreferences.AutoStartNgrok = autoNgrok;
                BridgePreferences.StopNgrokWithBridge = stopNgrok;
                BridgePreferences.Port = port;
                BridgePreferences.ToolRequestTimeoutSeconds = timeout;
                BridgePreferences.PowerShellExecutablePath = powershell;
                BridgePreferences.NgrokExecutablePath = ngrok;
                BridgePreferences.StableNgrokUrl = stable;
                CompanionIpc.WriteConfig();
                _message = "Settings saved. Restart the companion to apply port or ngrok changes.";
                _messageType = MessageType.Info;
            }
            EditorGUILayout.EndVertical();
        }

        private static void DrawPermissions()
        {
            EditorGUILayout.LabelField("Project permissions", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            bool packageScripts = EditorGUILayout.Toggle("Allow reading package scripts", BridgePreferences.AllowPackageScripts);
            bool proposals = EditorGUILayout.Toggle("Allow script change proposals", BridgePreferences.EnableScriptChangeProposals);
            if (packageScripts != BridgePreferences.AllowPackageScripts) BridgePreferences.AllowPackageScripts = packageScripts;
            if (proposals != BridgePreferences.EnableScriptChangeProposals) BridgePreferences.EnableScriptChangeProposals = proposals;
            EditorGUILayout.HelpBox("Analysis tools remain read-only. Script proposals never write until approved in this Unity window.", MessageType.None);
            EditorGUILayout.EndVertical();
        }

        private void DrawPendingChanges()
        {
            _showPendingChanges = EditorGUILayout.Foldout(_showPendingChanges, "Pending script proposals (" + ScriptChangeManager.PendingCount + ")", true);
            if (!_showPendingChanges) return;
            ScriptChangeProposal[] proposals = ScriptChangeManager.Snapshot().Where(p => p.Status == "Pending").ToArray();
            if (proposals.Length == 0)
            {
                EditorGUILayout.LabelField("No pending proposals.", EditorStyles.miniLabel);
                return;
            }

            foreach (ScriptChangeProposal proposal in proposals)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(proposal.Path, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(proposal.Summary, EditorStyles.wordWrappedLabel);
                EditorGUILayout.TextArea(ScriptChangeManager.GetDiffPreview(proposal), GUILayout.MinHeight(100));
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Approve"))
                {
                    bool ok = ScriptChangeManager.Approve(proposal.Id, out string result);
                    _message = result; _messageType = ok ? MessageType.Info : MessageType.Error;
                }
                if (GUILayout.Button("Reject"))
                {
                    bool ok = ScriptChangeManager.Reject(proposal.Id, out string result);
                    _message = result; _messageType = ok ? MessageType.Info : MessageType.Error;
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawHistory()
        {
            _showHistory = EditorGUILayout.Foldout(_showHistory, "Recent Unity tool requests", true);
            if (!_showHistory) return;
            BridgeRequestRecord[] records = BridgeRequestHistory.Snapshot();
            if (records.Length == 0)
            {
                EditorGUILayout.LabelField("No Unity tool requests in this scripting domain yet.", EditorStyles.miniLabel);
                return;
            }
            foreach (BridgeRequestRecord record in records.Take(30))
            {
                string label = record.TimeUtc.ToLocalTime().ToString("HH:mm:ss") + "  " + (string.IsNullOrEmpty(record.Tool) ? record.Method : record.Tool);
                EditorGUILayout.LabelField(label, (record.Success ? "✓ " : "✗ ") + record.Detail, EditorStyles.miniLabel);
            }
        }

        private void RunAction(Action action, string successMessage)
        {
            try { action(); _message = successMessage; _messageType = MessageType.Info; }
            catch (Exception ex) { _message = ex.GetBaseException().Message; _messageType = MessageType.Error; }
        }

        private static void DrawCopyableField(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);
            EditorGUILayout.SelectableLabel(value ?? string.Empty, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (GUILayout.Button("Copy", GUILayout.Width(52))) EditorGUIUtility.systemCopyBuffer = value ?? string.Empty;
            EditorGUILayout.EndHorizontal();
        }

        private static string Mask(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            int index = value.IndexOf("/mcp", StringComparison.OrdinalIgnoreCase);
            return index > 0 ? value.Substring(0, Math.Min(24, value.Length)) + "/••••••/mcp" : "••••••";
        }
    }
}
