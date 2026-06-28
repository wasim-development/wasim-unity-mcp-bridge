using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace WasimDevelopment.UnityMcpBridge
{
    internal sealed class CapturedLog
    {
        public DateTime TimeUtc;
        public string Message;
        public string StackTrace;
        public LogType Type;
    }

    [InitializeOnLoad]
    internal static class ConsoleLogCollector
    {
        private const int Capacity = 500;
        private static readonly object Gate = new object();
        private static readonly Queue<CapturedLog> Logs = new Queue<CapturedLog>();

        static ConsoleLogCollector()
        {
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        public static CapturedLog[] Snapshot()
        {
            lock (Gate) return Logs.ToArray();
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                Logs.Enqueue(new CapturedLog
                {
                    TimeUtc = DateTime.UtcNow,
                    Message = condition ?? string.Empty,
                    StackTrace = stackTrace ?? string.Empty,
                    Type = type
                });
                while (Logs.Count > Capacity) Logs.Dequeue();
            }
        }
    }
}
