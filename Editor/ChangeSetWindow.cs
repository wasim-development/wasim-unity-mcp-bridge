using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal sealed class ChangeSetWindow : EditorWindow
    {
        private Vector2 _scroll;
        private bool _completed;
        private string _message = "";

        [MenuItem("Window/Wasim Development/Change Sets and Actions")]
        public static void Open() => GetWindow<ChangeSetWindow>("Wasim MCP Reviews");
        private void OnGUI()
        {
            EditorGUILayout.LabelField("Change sets and Editor actions", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Approve only after reviewing every operation. Change sets save affected scenes and retain guarded file backups. Test/Play actions execute project code. No remote approve endpoint exists.", MessageType.Info);
            _completed = EditorGUILayout.Toggle("Show completed change sets", _completed);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            try
            {
                foreach (JObject item in ChangeSets.List(_completed, true)) Draw(item, false);
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Editor actions", EditorStyles.boldLabel);
                foreach (JObject item in EditorActions.List()) if (_completed || (string)item["status"] == "Pending") Draw(item, true);
            }
            catch (Exception ex) { EditorGUILayout.HelpBox(ex.GetBaseException().Message, MessageType.Error); }
            EditorGUILayout.EndScrollView();
            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, MessageType.Info);
            if (GUILayout.Button("Refresh")) Repaint();
        }
        private void Draw(JObject item, bool action)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            string id = (string)item["id"], status = (string)item["status"];
            EditorGUILayout.LabelField(id + " — " + status, EditorStyles.boldLabel);
            EditorGUILayout.LabelField((string)item["summary"] ?? "", EditorStyles.wordWrappedLabel);
            string preview = (action ? item : item["operations"])?.ToString(Newtonsoft.Json.Formatting.Indented) ?? "";
            // Full copyable content; never truncate the material that is being approved.
            EditorGUILayout.TextArea(preview, GUILayout.MinHeight(60), GUILayout.MaxHeight(260));
            if (GUILayout.Button("Copy complete review JSON")) EditorGUIUtility.systemCopyBuffer = item.ToString(Newtonsoft.Json.Formatting.Indented);
            if (item["scenes"] != null) EditorGUILayout.LabelField("Scenes: " + item["scenes"].ToString(Newtonsoft.Json.Formatting.None), EditorStyles.wordWrappedLabel);
            if (status == "Pending")
            {
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
                {
                    if (GUILayout.Button("Approve"))
                        Perform(() => { if (action) EditorActions.Approve(id); else ChangeSets.Approve(id); }, "Approved " + id);
                    if (GUILayout.Button("Reject"))
                        Perform(() => { if (action) EditorActions.Reject(id); else ChangeSets.Reject(id); }, "Rejected " + id);
                }
                EditorGUILayout.EndHorizontal();
            }
            if (!action && status == "Applied" && (string)item["kind"] == "Changes" && GUILayout.Button("Prepare rollback"))
                Perform(() => ChangeSets.ProposeRollback(id, "Restore state before " + id), "Rollback proposed; review it before approval.");
            if (item["message"] != null) EditorGUILayout.LabelField((string)item["message"] ?? "", EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndVertical();
        }
        private void Perform(Action action, string success)
        {
            try { action(); _message = success; }
            catch (Exception ex) { _message = ex.GetBaseException().Message; }
        }
    }
}
