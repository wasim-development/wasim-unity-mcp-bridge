using System.Globalization;
#if !WDMCP_OFFLINE
using System.Management.Automation;
using System.Management.Automation.Language;
#endif
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;
using WasimDevelopment.UnityMcpBridge;

internal static class Program
{
    private static int Passed;
    private static void Check(bool value, string description)
    { if (!value) throw new Exception(description); Passed++; Console.WriteLine("PASS " + description); }

    private static async Task Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string temporary = Path.Combine(Path.GetTempPath(), "wasim-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var files = Directory.GetFiles(Path.Combine(root, "Editor"), "*.cs")
                .Concat(Directory.GetFiles(Path.Combine(root, "Tests", "Editor"), "*.cs"))
                .Concat(Directory.GetFiles(Path.Combine(root, "Documentation~", "Examples"), "*.cs")).ToArray();
            foreach (string file in files)
            {
                var parsed = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.CSharp9), file);
                var errors = parsed.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                Check(errors.Length == 0, "C# 9 syntax " + Path.GetFileName(file) + (errors.Length == 0 ? "" : ": " + string.Join("; ", errors.Select(e => e.ToString()))));
            }
#if !WDMCP_OFFLINE
            foreach (string file in Directory.GetFiles(root, "*.ps1", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                Parser.ParseFile(file, out _, out ParseError[] errors);
                Check(errors.Length == 0, "PowerShell syntax " + Path.GetRelativePath(root, file));
            }
#else
            Console.WriteLine("SKIP PowerShell parser/runtime: offline SDK validation mode");
#endif
            JArray catalog = McpToolCatalog.Build();
            if (args.Length > 1 && args[1] == "--export-catalog")
                File.WriteAllText(Path.Combine(root, "Documentation~", "tool-catalog-v0.6.0.json"), catalog.ToString(Newtonsoft.Json.Formatting.Indented));
            string[] names = catalog.Select(t => (string)t["name"]).ToArray();
            Check(names.Length == names.Distinct().Count(), "Tool names are unique");
            var cases = new HashSet<string>();
            foreach (string file in new[] { "UnityToolExecutor.cs", "ExtendedTools.cs" })
                foreach (var item in CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, "Editor", file))).GetRoot().DescendantNodes().OfType<CaseSwitchLabelSyntax>())
                    if (item.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) cases.Add(literal.Token.ValueText);
            Check(names.All(cases.Contains) && cases.All(names.Contains), "Every catalog tool has an executor and every executor is advertised");
            Check(names.Length == 38, "21 legacy + 17 new tools");
            foreach (JObject tool in catalog)
                Check(tool["inputSchema"]?["additionalProperties"]?.Value<bool>() == false, "Bounded object schema " + (string)tool["name"]);

            foreach (string path in new[] { "../a.cs", "Assets/../outside.cs", "/Assets/a.cs", "Assets//a.cs", "Assets/./a.cs",
                "C:\\file.cs", "Assets/a.cs:stream", "Assets/a.cs ", "Assets/a.cs.", "Assets/a?.cs" })
            {
                bool denied = false;
                try { PathRules.ValidateAssetPath(path); } catch (ArgumentException) { denied = true; }
                Check(denied, "Reject invalid path " + path);
            }
            Check(PathRules.ValidateAssetPath("Assets\\Scripts\\Player.cs") == "Assets/Scripts/Player.cs", "Normalize Unity path separators");
            string filePath = Path.Combine(temporary, "heartbeat.json");
            string stamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var oldCulture = CultureInfo.CurrentCulture;
            foreach (string culture in new[] { "en-US", "ms-MY", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                AtomicFile.WriteText(filePath, new JObject { ["timestampUtc"] = stamp }.ToString());
                JObject read = AtomicFile.ReadObject(filePath);
                Check(read["timestampUtc"].Type == JTokenType.String && AtomicFile.TryUtc((string)read["timestampUtc"], out DateTime time)
                    && time.Kind == DateTimeKind.Utc && Math.Abs((DateTime.UtcNow - time).TotalSeconds) < 60, "UTC heartbeat under " + culture);
            }
            CultureInfo.CurrentCulture = oldCulture;
            AtomicFile.WriteText(filePath, "{\"sequence\":0}");
            using (var open = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                AtomicFile.WriteText(filePath, "{\"sequence\":1}");
                Check(AtomicFile.ReadObject(filePath)["sequence"].Value<int>() == 1, "Atomic replace succeeds while a compatible reader holds the old file");
            }
            using (var stop = new CancellationTokenSource())
            {
                var reader = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    { JObject value = AtomicFile.ReadObject(filePath); if (value["sequence"] == null) throw new Exception("Torn JSON"); }
                });
                for (int i = 2; i < 202; i++) AtomicFile.WriteText(filePath, new JObject { ["sequence"] = i }.ToString());
                stop.Cancel(); await reader;
                Check(AtomicFile.ReadObject(filePath)["sequence"].Value<int>() == 201, "200 atomic replacements with a concurrent JSON reader");
            }
            if (OperatingSystem.IsWindows())
            {
                using (var locked = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool failed = false;
                    try { AtomicFile.WriteText(filePath, "{\"sequence\":999}"); } catch (IOException) { failed = true; }
                    Check(failed && AtomicFile.ReadObject(filePath)["sequence"].Value<int>() == 201, "Windows sharing violation preserves original destination");
                }
            }
            else Console.WriteLine("SKIP Windows-only incompatible file-sharing test");

#if !WDMCP_OFFLINE
            await Protocol(root, temporary, catalog);
#endif
            Console.WriteLine("RESULT " + Passed + " checks passed. Unity Editor compilation/runtime tests are separate.");
        }
        finally { try { Directory.Delete(temporary, true); } catch { } }
    }

#if !WDMCP_OFFLINE
    private static async Task Protocol(string root, string temporary, JArray catalog)
    {
        string ipc = Path.Combine(temporary, "ipc"), requests = Path.Combine(ipc, "Requests"), responses = Path.Combine(ipc, "Responses");
        Directory.CreateDirectory(requests); Directory.CreateDirectory(responses);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string statusPath = Path.Combine(ipc, "unity-status.json");
        string token = Guid.NewGuid().ToString("N");
        var config = new JObject {
            ["companionVersion"] = "0.6.0", ["port"] = port, ["capabilityToken"] = token,
            ["requestTimeoutSeconds"] = 10, ["autoStartNgrok"] = false, ["stopNgrokWithCompanion"] = false,
            ["ipcRoot"] = ipc, ["requestsPath"] = requests, ["responsesPath"] = responses,
            ["catalogPath"] = Path.Combine(ipc, "tool-catalog.json"), ["unityStatusPath"] = statusPath,
            ["companionStatusPath"] = Path.Combine(ipc, "companion-status.json"), ["stopFlagPath"] = Path.Combine(ipc, "stop.flag"),
            ["companionLogPath"] = Path.Combine(ipc, "companion.log"), ["ngrokLogPath"] = Path.Combine(ipc, "ngrok.log")
        };
        string configPath = Path.Combine(ipc, "config.json");
        AtomicFile.WriteText(configPath, config.ToString());
        AtomicFile.WriteText((string)config["catalogPath"], catalog.ToString());
        AtomicFile.WriteText(statusPath, new JObject { ["timestampUtc"] = DateTime.UtcNow.ToString("O") }.ToString());
        using var simulatorStop = new CancellationTokenSource();
        var simulator = Task.Run(async () =>
        {
            while (!simulatorStop.IsCancellationRequested)
            {
                AtomicFile.WriteText(statusPath, new JObject { ["timestampUtc"] = DateTime.UtcNow.ToString("O") }.ToString());
                foreach (string file in Directory.GetFiles(requests, "*.json"))
                {
                    JObject request = AtomicFile.ReadObject(file);
                    string id = (string)request["id"];
                    string name = (string)request["name"];
                    JToken result = name == "unity_read_console" ? new JArray()
                        : name == "unity_get_script_change_proposals" ? new JArray(new JObject { ["id"] = "single" })
                        : name == "unity_get_packages" ? new JArray(1, 2)
                        : name == "unity_get_selected_script" ? JValue.CreateNull()
                        : new JObject { ["bridgeVersion"] = "0.6.0", ["connected"] = true };
                    var response = new JObject { ["id"] = id, ["success"] = true, ["result"] = result };
                    if (name == "unity_capture_view") response["mcpContent"] = new JArray(new JObject {
                        ["type"] = "image", ["mimeType"] = "image/png", ["data"] = "AA==" });
                    AtomicFile.WriteText(Path.Combine(responses, id + ".json"), response.ToString());
                    File.Delete(file);
                }
                await Task.Delay(20);
            }
        });
        using PowerShell shell = PowerShell.Create();
        shell.AddCommand(Path.Combine(root, "Companion~", "wdmcp-companion.ps1")).AddParameter("ConfigPath", configPath);
        IAsyncResult running = shell.BeginInvoke();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string endpoint = "http://127.0.0.1:" + port + "/" + token + "/mcp";
        try
        {
            JObject init = null;
            for (int retry = 0; retry < 60; retry++)
            {
                if (running.IsCompleted)
                    throw new Exception("Companion exited: " + string.Join("; ", shell.Streams.Error.Select(e => e.ToString())) + "\n" +
                        (File.Exists((string)config["companionLogPath"]) ? File.ReadAllText((string)config["companionLogPath"]) : ""));
                try { init = await Rpc(client, endpoint, "initialize", new JObject { ["protocolVersion"] = "2025-11-25" }); break; }
                catch (HttpRequestException) { await Task.Delay(100); }
            }
            Check(init?["result"]?["serverInfo"]?["version"]?.Value<string>() == "0.6.0", "Live companion initialize over HTTP");
            JObject listed = await Rpc(client, endpoint, "tools/list", new JObject());
            Check((listed["result"]?["tools"] as JArray)?.Count == 38, "Live companion advertises all 38 tools");
            foreach (var pair in new[] { ("unity_read_console", "[]"), ("unity_get_script_change_proposals", "[{\"id\":\"single\"}]"),
                ("unity_get_packages", "[1,2]"), ("unity_get_selected_script", "null") })
            {
                JObject response = await Rpc(client, endpoint, "tools/call", new JObject { ["name"] = pair.Item1, ["arguments"] = new JObject() });
                string text = response["result"]?["content"]?[0]?["text"]?.Value<string>();
                Check(text != null && JToken.DeepEquals(JToken.Parse(text), JToken.Parse(pair.Item2)), "Preserve IPC JSON shape: " + pair.Item2);
            }
            JObject image = await Rpc(client, endpoint, "tools/call", new JObject { ["name"] = "unity_capture_view", ["arguments"] = new JObject() });
            Check((string)image["result"]?["content"]?[0]?["type"] == "image", "Forward native MCP image content");
            JObject invalid = await Rpc(client, endpoint, "tools/call", new JObject { ["name"] = "execute_arbitrary_code", ["arguments"] = new JObject() });
            Check(invalid["error"]?["code"]?.Value<int>() == -32602, "Reject an unadvertised tool");
            var bad = await client.PostAsync("http://127.0.0.1:" + port + "/wrong/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
            Check(bad.StatusCode == HttpStatusCode.NotFound, "Private endpoint path is enforced");
        }
        finally
        {
            File.WriteAllText((string)config["stopFlagPath"], "stop");
            for (int i = 0; i < 100 && !running.IsCompleted; i++) await Task.Delay(50);
            if (!running.IsCompleted) shell.Stop();
            try { shell.EndInvoke(running); } catch { }
            simulatorStop.Cancel(); await simulator;
        }
    }
    private static async Task<JObject> Rpc(HttpClient client, string endpoint, string method, JObject parameters)
    {
        var request = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters };
        using var response = await client.PostAsync(endpoint, new StringContent(request.ToString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }
#endif
}
