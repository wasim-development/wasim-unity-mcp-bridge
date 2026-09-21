using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class ProjectSecurity
    {
        public const long MaxScriptBytes = 256L * 1024L;
        public static string ProjectRoot => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        private static StringComparison PathComparison => Application.platform == RuntimePlatform.WindowsEditor
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static string ValidateAssetPath(string path) => PathRules.ValidateAssetPath(path);

        private static void CheckPhysicalPath(string root, string full)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, PathComparison)) throw new ArgumentException("Path escapes its permitted root.");
            for (string current = full; current != null && current.Length >= prefix.Length; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Linked paths below the permitted root are not supported.");
        }

        public static string ResolveAssetFile(string path, bool writable = false)
        {
            string value = ValidateAssetPath(path);
            string root, suffix;
            if (value.StartsWith("Assets/", StringComparison.Ordinal))
            { root = Application.dataPath; suffix = value.Substring(7); }
            else if (!writable && BridgePreferences.AllowPackageScripts && value.StartsWith("Packages/", StringComparison.Ordinal))
            {
                PackageInfo package = PackageInfo.FindForAssetPath(value);
                if (package == null) throw new ArgumentException("Package is not registered: " + value);
                string prefix = "Packages/" + package.name + "/";
                if (!value.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Invalid package path.");
                root = package.resolvedPath; suffix = value.Substring(prefix.Length);
            }
            else throw new ArgumentException(writable ? "Only Assets paths can be modified." : "Enable package scripts in Unity to read Packages paths.");
            string full = Path.GetFullPath(Path.Combine(root, suffix));
            CheckPhysicalPath(root, full);
            return full;
        }

        public static bool TryResolveReadableScript(string path, out string fullPath, out string error)
            => ResolveScript(path, false, out fullPath, out error);
        public static bool TryResolveWritableAssetScript(string path, out string fullPath, out string error)
            => ResolveScript(path, true, out fullPath, out error);

        private static bool ResolveScript(string path, bool writable, out string fullPath, out string error)
        {
            fullPath = ""; error = "";
            try
            {
                if (!string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only C# source files are supported.");
                string full = ResolveAssetFile(path, writable);
                if (!File.Exists(full)) throw new FileNotFoundException("Script not found: " + path);
                if (new FileInfo(full).Length > MaxScriptBytes) throw new ArgumentException("Script exceeds the 256 KB limit.");
                fullPath = full; return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static IEnumerable<string> ScriptAssetPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            var roots = new List<string> { "Assets" };
            if (BridgePreferences.AllowPackageScripts)
                foreach (PackageInfo package in PackageInfo.GetAllRegisteredPackages()) roots.Add("Packages/" + package.name);
            foreach (string guid in AssetDatabase.FindAssets("t:MonoScript", roots.ToArray()))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (paths.Add(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) yield return path;
            }
        }

        public static string ToProjectRelativeOrAbsolute(string fullPath) => ToProjectRelative(fullPath);
        public static string ToProjectRelative(string fullPath)
        {
            string full = Path.GetFullPath(fullPath);
            foreach (PackageInfo package in PackageInfo.GetAllRegisteredPackages())
            {
                string packageRoot = Path.GetFullPath(package.resolvedPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(packageRoot, PathComparison)) return "Packages/" + package.name + "/" + full.Substring(packageRoot.Length).Replace('\\', '/');
            }
            string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, PathComparison) ? full.Substring(root.Length).Replace('\\', '/') : full.Replace('\\', '/');
        }
    }
}
