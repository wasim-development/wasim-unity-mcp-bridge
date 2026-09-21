using System;
using System.IO;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class PathRules
    {
        public static string ValidateAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new ArgumentException("Use a project-relative path.");
            string value = path.Replace('\\', '/');
            foreach (string part in value.Split('/'))
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(new[] { ':', '\0', '*', '?', '\"', '<', '>', '|' }) >= 0
                    || part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal))
                    throw new ArgumentException("Invalid, empty, traversal or drive path segment.");
            return value;
        }
    }
}
