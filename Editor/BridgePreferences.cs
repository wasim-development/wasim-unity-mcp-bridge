using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class BridgePreferences
    {
        private const int DefaultPort = 38421;
        private static readonly string Prefix = "WasimUnityMcpBridge." + ProjectId + ".";

        public static int Port
        {
            get => Mathf.Clamp(EditorPrefs.GetInt(Prefix + "Port", DefaultPort), 1024, 65535);
            set => EditorPrefs.SetInt(Prefix + "Port", Mathf.Clamp(value, 1024, 65535));
        }

        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(Prefix + "AutoStartCompanion", false);
            set => EditorPrefs.SetBool(Prefix + "AutoStartCompanion", value);
        }

        public static bool StopCompanionWithUnity
        {
            get => EditorPrefs.GetBool(Prefix + "StopCompanionWithUnity", true);
            set => EditorPrefs.SetBool(Prefix + "StopCompanionWithUnity", value);
        }

        public static bool AutoStartNgrok
        {
            get => EditorPrefs.GetBool(Prefix + "AutoStartNgrok", true);
            set => EditorPrefs.SetBool(Prefix + "AutoStartNgrok", value);
        }

        public static bool StopNgrokWithBridge
        {
            get => EditorPrefs.GetBool(Prefix + "StopNgrokWithBridge", true);
            set => EditorPrefs.SetBool(Prefix + "StopNgrokWithBridge", value);
        }

        public static string PowerShellExecutablePath
        {
            get => EditorPrefs.GetString(Prefix + "PowerShellExecutablePath", "powershell.exe");
            set => EditorPrefs.SetString(Prefix + "PowerShellExecutablePath", string.IsNullOrWhiteSpace(value) ? "powershell.exe" : value.Trim());
        }

        public static string NgrokExecutablePath
        {
            get => EditorPrefs.GetString(Prefix + "NgrokExecutablePath", "ngrok");
            set => EditorPrefs.SetString(Prefix + "NgrokExecutablePath", string.IsNullOrWhiteSpace(value) ? "ngrok" : value.Trim());
        }

        public static string NgrokPublicUrl
        {
            get => EditorPrefs.GetString(Prefix + "NgrokPublicUrl", string.Empty);
            set => EditorPrefs.SetString(Prefix + "NgrokPublicUrl", NormalizePublicUrl(value));
        }

        public static bool AllowPackageScripts
        {
            get => EditorPrefs.GetBool(Prefix + "AllowPackageScripts", false);
            set => EditorPrefs.SetBool(Prefix + "AllowPackageScripts", value);
        }

        public static bool EnableScriptChangeProposals
        {
            get => EditorPrefs.GetBool(Prefix + "EnableScriptChangeProposals", false);
            set => EditorPrefs.SetBool(Prefix + "EnableScriptChangeProposals", value);
        }

        public static bool RevealPrivateEndpoint
        {
            get => EditorPrefs.GetBool(Prefix + "RevealEndpoint", false);
            set => EditorPrefs.SetBool(Prefix + "RevealEndpoint", value);
        }

        public static string StableNgrokUrl
        {
            get => EditorPrefs.GetString(Prefix + "StableNgrokUrl", string.Empty);
            set => EditorPrefs.SetString(Prefix + "StableNgrokUrl", NormalizePublicUrl(value));
        }

        public static int ToolRequestTimeoutSeconds
        {
            get => Mathf.Clamp(EditorPrefs.GetInt(Prefix + "ToolRequestTimeoutSeconds", 45), 10, 120);
            set => EditorPrefs.SetInt(Prefix + "ToolRequestTimeoutSeconds", Mathf.Clamp(value, 10, 120));
        }

        public static string CapabilityToken
        {
            get
            {
                string token = EditorPrefs.GetString(Prefix + "CapabilityToken", string.Empty);
                if (string.IsNullOrWhiteSpace(token))
                {
                    token = CreateToken();
                    EditorPrefs.SetString(Prefix + "CapabilityToken", token);
                }
                return token;
            }
        }

        public static void RegenerateCapabilityToken()
        {
            EditorPrefs.SetString(Prefix + "CapabilityToken", CreateToken());
        }

        public static string LocalEndpoint =>
            $"http://127.0.0.1:{Port}/{CapabilityToken}/mcp";

        public static string BuildPublicEndpoint(string publicUrl)
        {
            string normalized = NormalizePublicUrl(publicUrl);
            return string.IsNullOrWhiteSpace(normalized)
                ? string.Empty
                : normalized.TrimEnd('/') + "/" + CapabilityToken + "/mcp";
        }

        private static string ProjectId
        {
            get
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
                    return BitConverter.ToString(bytes, 0, 8).Replace("-", string.Empty);
                }
            }
        }

        private static string NormalizePublicUrl(string value)
        {
            string trimmed = (value ?? string.Empty).Trim().TrimEnd('/');
            if (trimmed.Length == 0) return string.Empty;
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }
            return trimmed;
        }

        private static string CreateToken()
        {
            byte[] bytes = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
