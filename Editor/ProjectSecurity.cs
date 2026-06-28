using System;
using System.IO;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ProjectSecurity
    {
        public const long MaxScriptBytes = 256L * 1024L;

        public static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        public static bool TryResolveReadableScript(string relativePath, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "A project-relative script path is required.";
                return false;
            }

            string normalized = relativePath.Replace('\\', '/').TrimStart('/');
            bool inAssets = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            bool inPackages = normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
            if (!inAssets && !(inPackages && BridgePreferences.AllowPackageScripts))
            {
                error = "Only Assets/**/*.cs is readable. Enable package scripts explicitly to read Packages/**/*.cs.";
                return false;
            }

            if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = "Only C# source files are readable in this release.";
                return false;
            }

            string candidate = Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
            string rootWithSeparator = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                       + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                error = "The path escapes the Unity project.";
                return false;
            }

            if (!File.Exists(candidate))
            {
                error = "Script not found: " + normalized;
                return false;
            }

            if (new FileInfo(candidate).Length > MaxScriptBytes)
            {
                error = $"Script exceeds the {MaxScriptBytes / 1024} KB read limit.";
                return false;
            }

            fullPath = candidate;
            return true;
        }


        public static bool TryResolveWritableAssetScript(string relativePath, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "A project-relative script path is required.";
                return false;
            }

            string normalized = relativePath.Replace('\\', '/').TrimStart('/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                error = "Only existing Assets/**/*.cs files can be proposed for replacement.";
                return false;
            }
            if (!normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = "Only C# source files can be proposed for replacement.";
                return false;
            }

            string candidate = Path.GetFullPath(Path.Combine(ProjectRoot, normalized));
            string assetsRoot = Path.GetFullPath(Path.Combine(ProjectRoot, "Assets")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "The path escapes the Assets folder.";
                return false;
            }
            if (!File.Exists(candidate))
            {
                error = "Script not found: " + normalized;
                return false;
            }
            if (new FileInfo(candidate).Length > MaxScriptBytes)
            {
                error = $"Script exceeds the {MaxScriptBytes / 1024} KB limit.";
                return false;
            }

            fullPath = candidate;
            return true;
        }

        public static string ToProjectRelativeOrAbsolute(string fullPath)
        {
            string projectRelative = ToProjectRelative(fullPath);
            return Path.IsPathRooted(projectRelative) ? fullPath.Replace('\\', '/') : projectRelative;
        }

        public static string ToProjectRelative(string fullPath)
        {
            string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return fullPath.Replace('\\', '/');
            return full.Substring(root.Length).Replace('\\', '/');
        }
    }
}
