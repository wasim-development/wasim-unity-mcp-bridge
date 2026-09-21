using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ViewCapture
    {
        public static JObject Capture(JObject args)
        {
            if (Application.isBatchMode) throw new InvalidOperationException("View capture requires an interactive Unity Editor.");
            string view = args["view"]?.Value<string>() ?? "scene";
            int width = ObjectAccess.Limit(args, "width", 960, 64, 1280);
            int height = ObjectAccess.Limit(args, "height", 540, 64, 1280);
            Texture2D texture = null;
            RenderTexture render = null;
            GameObject temporary = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (view == "game")
                {
                    // Pixel copy of the Game View; do not re-render the gameplay camera or invoke its callbacks.
                    Type type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                    UnityEngine.Object[] windows = type == null ? Array.Empty<UnityEngine.Object>() : Resources.FindObjectsOfTypeAll(type);
                    if (windows.Length == 0) throw new InvalidOperationException("Open the Game View first.");
                    var window = (EditorWindow)windows[0];
                    if (EditorWindow.focusedWindow != window) throw new InvalidOperationException("Focus Game View before capturing it.");
                    Rect rect = window.position;
                    int w = Mathf.Clamp(Mathf.RoundToInt(rect.width * EditorGUIUtility.pixelsPerPoint), 1, 4096);
                    int h = Mathf.Clamp(Mathf.RoundToInt(rect.height * EditorGUIUtility.pixelsPerPoint), 1, 4096);
                    Color[] pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(rect.position, w, h);
                    var full = new Texture2D(w, h, TextureFormat.RGB24, false);
                    try
                    {
                        full.SetPixels(pixels); full.Apply();
                        render = RenderTexture.GetTemporary(width, height, 0);
                        Graphics.Blit(full, render); RenderTexture.active = render;
                        texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); texture.Apply();
                    }
                    finally { UnityEngine.Object.DestroyImmediate(full); }
                }
                else if (view == "scene")
                {
                    Camera source = SceneView.lastActiveSceneView?.camera;
                    if (source == null) throw new InvalidOperationException("Open a Scene View first.");
                    temporary = new GameObject("Wasim MCP capture camera") { hideFlags = HideFlags.HideAndDontSave };
                    Camera camera = temporary.AddComponent<Camera>();
                    camera.CopyFrom(source);
                    camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                    camera.enabled = false;
                    render = RenderTexture.GetTemporary(width, height, 24);
                    camera.targetTexture = render; camera.Render(); RenderTexture.active = render;
                    texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); texture.Apply();
                }
                else throw new ArgumentException("view must be scene or game.");
                byte[] data = texture.EncodeToJPG(80);
                if (data.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Image exceeds 2 MB; request a smaller capture.");
                return new JObject { ["_mcpContent"] = new JArray {
                    new JObject { ["type"] = "text", ["text"] = new JObject {
                        ["view"] = view, ["width"] = width, ["height"] = height,
                        ["note"] = view == "scene" ? "Scene camera render; editor gizmos are not included." : "Focused Game View including its editor toolbar.",
                        ["timestampUtc"] = DateTime.UtcNow.ToString("O") }.ToString(Newtonsoft.Json.Formatting.None) },
                    new JObject { ["type"] = "image", ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(data) } } };
            }
            finally
            {
                RenderTexture.active = previous;
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                if (temporary != null) UnityEngine.Object.DestroyImmediate(temporary);
                if (render != null) RenderTexture.ReleaseTemporary(render);
            }
        }
    }
}
