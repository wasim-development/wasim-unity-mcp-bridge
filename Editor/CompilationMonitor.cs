using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace WasimDevelopment.UnityMcpBridge
{
    [InitializeOnLoad]
    internal static class CompilationMonitor
    {
        private static readonly object Gate = new object();
        private static readonly List<JObject> Messages = new List<JObject>();
        private static DateTime _lastStartedUtc;
        private static DateTime _lastFinishedUtc;

        static CompilationMonitor()
        {
            CompilationPipeline.compilationStarted -= OnStarted;
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.compilationFinished -= OnFinished;
            CompilationPipeline.compilationFinished += OnFinished;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
        }

        public static JObject GetStatus()
        {
            lock (Gate)
            {
                return new JObject
                {
                    ["isCompiling"] = EditorApplication.isCompiling,
                    ["isUpdating"] = EditorApplication.isUpdating,
                    ["lastStartedUtc"] = _lastStartedUtc == default ? null : _lastStartedUtc.ToString("O"),
                    ["lastFinishedUtc"] = _lastFinishedUtc == default ? null : _lastFinishedUtc.ToString("O"),
                    ["messages"] = new JArray(Messages)
                };
            }
        }

        private static void OnStarted(object context)
        {
            lock (Gate)
            {
                _lastStartedUtc = DateTime.UtcNow;
                Messages.Clear();
            }
        }

        private static void OnFinished(object context)
        {
            lock (Gate) _lastFinishedUtc = DateTime.UtcNow;
        }

        private static void OnAssemblyFinished(string assemblyPath, CompilerMessage[] compilerMessages)
        {
            lock (Gate)
            {
                foreach (CompilerMessage message in compilerMessages)
                {
                    Messages.Add(new JObject
                    {
                        ["assembly"] = assemblyPath ?? string.Empty,
                        ["type"] = message.type.ToString(),
                        ["message"] = message.message ?? string.Empty,
                        ["file"] = message.file ?? string.Empty,
                        ["line"] = message.line,
                        ["column"] = message.column
                    });
                    if (Messages.Count > 200) Messages.RemoveAt(0);
                }
            }
        }
    }
}
