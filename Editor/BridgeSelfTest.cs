using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WasimDevelopment.UnityMcpBridge
{
    internal enum BridgeSelfTestState
    {
        Idle,
        Running,
        Passed,
        Failed
    }

    internal static class BridgeSelfTest
    {
        private static readonly object Gate = new object();
        private static BridgeSelfTestState _state;
        private static string _lastResult = string.Empty;

        public static event Action Changed;
        public static BridgeSelfTestState State { get { lock (Gate) return _state; } }
        public static string LastResult { get { lock (Gate) return _lastResult; } }

        public static void Run()
        {
            if (!CompanionManager.IsRunning)
            {
                SetResult(BridgeSelfTestState.Failed, "Start the standalone companion first.");
                return;
            }

            lock (Gate)
            {
                if (_state == BridgeSelfTestState.Running) return;
                _state = BridgeSelfTestState.Running;
                _lastResult = "Testing companion initialize and tools/list…";
            }
            RaiseChanged();

            string endpoint = BridgePreferences.LocalEndpoint;
            Task.Run(() =>
            {
                try
                {
                    JObject initialize = Post(endpoint, new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = 1,
                        ["method"] = "initialize",
                        ["params"] = new JObject
                        {
                            ["protocolVersion"] = "2025-11-25",
                            ["capabilities"] = new JObject(),
                            ["clientInfo"] = new JObject { ["name"] = "unity-local-self-test", ["version"] = BridgeVersion.PackageVersion }
                        }
                    });
                    string protocol = initialize["result"]?["protocolVersion"]?.Value<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(protocol)) throw new InvalidDataException("initialize returned no protocol version.");

                    JObject tools = Post(endpoint, new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = 2,
                        ["method"] = "tools/list",
                        ["params"] = new JObject()
                    });
                    int toolCount = tools["result"]?["tools"] is JArray array ? array.Count : 0;
                    if (toolCount == 0) throw new InvalidDataException("The companion tool catalogue was empty.");
                    SetResult(BridgeSelfTestState.Passed, "Companion MCP test passed. Protocol " + protocol + ", " + toolCount + " tools available.");
                }
                catch (Exception ex)
                {
                    SetResult(BridgeSelfTestState.Failed, "Companion MCP test failed: " + ex.GetBaseException().Message);
                }
            });
        }

        private static JObject Post(string endpoint, JObject body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json, text/event-stream";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;
            request.ContentLength = bytes.Length;
            using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream() ?? Stream.Null, Encoding.UTF8))
            {
                string text = reader.ReadToEnd();
                return JObject.Parse(text);
            }
        }

        private static void SetResult(BridgeSelfTestState state, string result)
        {
            lock (Gate) { _state = state; _lastResult = result ?? string.Empty; }
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            MainThreadDispatcher.Post(() => { try { Changed?.Invoke(); } catch { } });
        }
    }
}
