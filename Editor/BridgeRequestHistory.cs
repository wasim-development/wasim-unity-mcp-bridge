using System;
using System.Collections.Generic;

namespace WasimDevelopment.UnityMcpBridge
{
    internal sealed class BridgeRequestRecord
    {
        public DateTime TimeUtc;
        public string Method;
        public string Tool;
        public bool Success;
        public string Detail;
    }

    internal static class BridgeRequestHistory
    {
        private const int Capacity = 100;
        private static readonly object Gate = new object();
        private static readonly List<BridgeRequestRecord> Records = new List<BridgeRequestRecord>();

        public static event Action Changed;

        public static void Add(string method, string tool, bool success, string detail)
        {
            lock (Gate)
            {
                Records.Insert(0, new BridgeRequestRecord
                {
                    TimeUtc = DateTime.UtcNow,
                    Method = method ?? string.Empty,
                    Tool = tool ?? string.Empty,
                    Success = success,
                    Detail = detail ?? string.Empty
                });
                if (Records.Count > Capacity) Records.RemoveRange(Capacity, Records.Count - Capacity);
            }
            MainThreadDispatcher.Post(() =>
            {
                try { Changed?.Invoke(); } catch { }
            });
        }

        public static BridgeRequestRecord[] Snapshot()
        {
            lock (Gate) return Records.ToArray();
        }

        public static DateTime? LastRequestUtc
        {
            get
            {
                lock (Gate) return Records.Count == 0 ? (DateTime?)null : Records[0].TimeUtc;
            }
        }

        public static DateTime? LastSuccessfulToolUtc
        {
            get
            {
                lock (Gate)
                {
                    BridgeRequestRecord record = Records.Find(r => r.Success && !string.IsNullOrEmpty(r.Tool));
                    return record == null ? (DateTime?)null : record.TimeUtc;
                }
            }
        }

        public static void Clear()
        {
            lock (Gate) Records.Clear();
            MainThreadDispatcher.Post(() =>
            {
                try { Changed?.Invoke(); } catch { }
            });
        }
    }
}
