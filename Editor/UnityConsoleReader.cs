using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal static class UnityConsoleReader
    {
        public static JArray Read(int maximumEntries, bool includeLogs, bool includeWarnings, bool includeErrors)
        {
            maximumEntries = Mathf.Clamp(maximumEntries, 1, 200);
            try
            {
                JArray reflected = ReadUsingEditorInternals(maximumEntries, includeLogs, includeWarnings, includeErrors);
                if (reflected.Count > 0) return reflected;
            }
            catch
            {
                // Unity's internal Console API can differ between patch releases.
            }

            return ReadCapturedFallback(maximumEntries, includeLogs, includeWarnings, includeErrors);
        }

        private static JArray ReadUsingEditorInternals(int maximumEntries, bool includeLogs, bool includeWarnings, bool includeErrors)
        {
            Type entriesType = FindType("UnityEditor.LogEntries");
            Type entryType = FindType("UnityEditor.LogEntry");
            if (entriesType == null || entryType == null) return new JArray();

            BindingFlags staticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo getCount = entriesType.GetMethod("GetCount", staticFlags);
            MethodInfo getEntry = entriesType.GetMethod("GetEntryInternal", staticFlags);
            MethodInfo start = entriesType.GetMethod("StartGettingEntries", staticFlags);
            MethodInfo end = entriesType.GetMethod("EndGettingEntries", staticFlags);
            if (getCount == null || getEntry == null) return new JArray();

            var output = new List<JObject>();
            start?.Invoke(null, null);
            try
            {
                int count = Convert.ToInt32(getCount.Invoke(null, null));
                int first = Math.Max(0, count - Math.Max(maximumEntries * 4, maximumEntries));
                for (int i = first; i < count; i++)
                {
                    object entry = Activator.CreateInstance(entryType, true);
                    object success = getEntry.Invoke(null, new[] { (object)i, entry });
                    if (success is bool ok && !ok) continue;

                    int mode = ReadInt(entryType, entry, "mode");
                    string condition = ReadString(entryType, entry, "condition");
                    string file = ReadString(entryType, entry, "file");
                    int line = ReadInt(entryType, entry, "line");
                    string type = ClassifyMode(mode);
                    if (!Allowed(type, includeLogs, includeWarnings, includeErrors)) continue;

                    output.Add(new JObject
                    {
                        ["type"] = type,
                        ["message"] = condition,
                        ["file"] = file,
                        ["line"] = line,
                        ["source"] = "Unity Console"
                    });
                }
            }
            finally
            {
                end?.Invoke(null, null);
            }

            int skip = Math.Max(0, output.Count - maximumEntries);
            return new JArray(output.GetRange(skip, output.Count - skip));
        }

        private static JArray ReadCapturedFallback(int maximumEntries, bool includeLogs, bool includeWarnings, bool includeErrors)
        {
            CapturedLog[] logs = ConsoleLogCollector.Snapshot();
            var output = new List<JObject>();
            int first = Math.Max(0, logs.Length - Math.Max(maximumEntries * 4, maximumEntries));
            for (int i = first; i < logs.Length; i++)
            {
                CapturedLog log = logs[i];
                string type = log.Type == LogType.Warning ? "Warning" :
                    (log.Type == LogType.Error || log.Type == LogType.Exception || log.Type == LogType.Assert ? "Error" : "Log");
                if (!Allowed(type, includeLogs, includeWarnings, includeErrors)) continue;
                output.Add(new JObject
                {
                    ["timeUtc"] = log.TimeUtc.ToString("O"),
                    ["type"] = type,
                    ["message"] = log.Message,
                    ["stackTrace"] = log.StackTrace,
                    ["source"] = "Live collector fallback"
                });
            }
            int skip = Math.Max(0, output.Count - maximumEntries);
            return new JArray(output.GetRange(skip, output.Count - skip));
        }

        private static bool Allowed(string type, bool logs, bool warnings, bool errors)
        {
            return (type == "Log" && logs) || (type == "Warning" && warnings) || (type == "Error" && errors);
        }

        private static string ClassifyMode(int mode)
        {
            // Internal mode flags vary, but error/assert/exception flags occupy the low error bits.
            if ((mode & 1) != 0 || (mode & 2) != 0 || (mode & 16) != 0 || (mode & 256) != 0) return "Error";
            if ((mode & 4) != 0 || (mode & 64) != 0) return "Warning";
            return "Log";
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static string ReadString(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return Convert.ToString(field.GetValue(instance)) ?? string.Empty;
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? string.Empty : Convert.ToString(property.GetValue(instance, null)) ?? string.Empty;
        }

        private static int ReadInt(Type type, object instance, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return Convert.ToInt32(field.GetValue(instance));
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? 0 : Convert.ToInt32(property.GetValue(instance, null));
        }
    }
}
