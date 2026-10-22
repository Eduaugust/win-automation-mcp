using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace WinAutomationMcp;

static class Program
{
    public const string Version = "0.4.0";
    public static int Port = 8765;
    // Windows UI Automation (COM) isn't safe to call concurrently from multiple threads, so UI tools run one at a time.
    public static readonly object AutomationGate = new();
    static long _connCounter;
    [ThreadStatic] static long _currentConn;
    public static long CurrentConn => _currentConn;
    static readonly DateTime StartedAt = DateTime.Now;

    public static ToolResult ServerInfo()
    {
        var ff = Recorder.FfmpegPath();
        return $"OK: win-automation-mcp {Version}\npid: {Environment.ProcessId}\nport: {Port} (127.0.0.1 only)\nsession_id: {Process.GetCurrentProcess().SessionId}\nuser: {Environment.UserName}\nstarted: {StartedAt:yyyy-MM-dd HH:mm:ss}\nexe: {Environment.ProcessPath}\nlog: {Log.Path}\nffmpeg: {ff ?? "NOT FOUND"}\ntools ({Tools.All.Count}): {string.Join(", ", Tools.All.Keys)}";
    }

    static int Main(string[] argv)
    {
        // Per-monitor DPI awareness: UIA rects, SetCursorPos, CopyFromScreen and gdigrab all use physical pixels.
        try { Native.SetProcessDpiAwarenessContext(Native.DPI_PER_MONITOR_V2); } catch { }

        var envPort = Environment.GetEnvironmentVariable("WIN_MCP_PORT");
        if (int.TryParse(envPort, out var ep)) Port = ep;
        for (int i = 0; i < argv.Length; i++)
        {
            if ((argv[i] == "--port" || argv[i] == "-p") && i + 1 < argv.Length && int.TryParse(argv[i + 1], out var ap)) { Port = ap; i++; }
            else if (argv[i] == "--help" || argv[i] == "-h")
            {
                Console.WriteLine($"win-automation-mcp {Version}\n  --port N   TCP port on 127.0.0.1 (default 8765, or env WIN_MCP_PORT)\n  env WIN_MCP_LOG_DIR  log folder (default <exe folder>\\logs)\n  env WIN_MCP_FFMPEG   full path to ffmpeg.exe");
                return 0;
            }
        }

        var logDir = Environment.GetEnvironmentVariable("WIN_MCP_LOG_DIR") ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Log.Init(logDir, Port);
        Tools.Register();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { Log.Write("process exit"); try { Recorder.StopAll(); } catch { } };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"UNHANDLED EXCEPTION (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write($"unobserved task exception: {e.Exception}"); e.SetObserved(); };

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.ExclusiveAddressUse = true;
            listener.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"FATAL: cannot listen on 127.0.0.1:{Port}: {ex.Message}");
            return 2;
        }
        Log.Write($"win-automation-mcp {Version} pid={Environment.ProcessId} session={Process.GetCurrentProcess().SessionId} user={Environment.UserName} listening on 127.0.0.1:{Port}, {Tools.All.Count} tools, ffmpeg={Recorder.FfmpegPath() ?? "NOT FOUND"}");

        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (Exception ex) { Log.Write($"accept failed: {ex.Message}"); Thread.Sleep(500); continue; }
            long id = Interlocked.Increment(ref _connCounter);
            _ = Task.Run(() => Serve(client, id));
        }
    }

    static void Serve(TcpClient client, long id)
    {
        string remote = "?";
        try { remote = client.Client.RemoteEndPoint?.ToString() ?? "?"; } catch { }
        try
        {
            // Detect dead tunnels (VM sleep, ssh drop) instead of holding a half-open connection forever.
            var sock = client.Client;
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
            sock.NoDelay = true;
        }
        catch (Exception ex) { Log.Write($"conn#{id} could not set keepalive: {ex.Message}"); }
        var sw = Stopwatch.StartNew();
        int requests = 0;
        string reason = "unknown";
        Log.Write($"conn#{id} opened from {remote}");
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                reason = HandleConnection(id, reader, writer, ref requests);
            }
        }
        catch (Exception ex) { reason = $"error: {ex.GetType().Name}: {ex.Message}"; }
        Log.Write($"conn#{id} closed after {sw.Elapsed.TotalSeconds:0.0}s, {requests} request(s). Reason: {reason}");
    }

    static string HandleConnection(long connId, StreamReader reader, StreamWriter stdout, ref int requests)
    {
        string? line;
        while (true)
        {
            try { line = reader.ReadLine(); }
            catch (IOException ex) { return $"read failed (peer reset or tunnel dropped): {ex.Message}"; }
            if (line == null) return "client closed the connection (EOF)";
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? req;
            try { req = JsonNode.Parse(line); }
            catch (Exception ex)
            {
                Log.Write($"conn#{connId} bad JSON ignored: {ex.Message}");
                try { stdout.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "Parse error" } }.ToJsonString()); } catch { }
                continue;
            }
            requests++;
            var method = req?["method"]?.GetValue<string>();
            var idNode = req?["id"];
            var paramsNode = req?["params"];

            if (method != null && method.StartsWith("notifications/")) continue;

            JsonNode? result = null;
            JsonObject? error = null;
            try
            {
                switch (method)
                {
                    case "initialize":
                        result = new JsonObject
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "win-automation-mcp", ["version"] = Version }
                        };
                        break;
                    case "ping":
                        result = new JsonObject();
                        break;
                    case "resources/list": result = new JsonObject { ["resources"] = new JsonArray() }; break;
                    case "prompts/list": result = new JsonObject { ["prompts"] = new JsonArray() }; break;
                    case "tools/list":
                        result = new JsonObject { ["tools"] = Tools.List() };
                        break;
                    case "tools/call":
                        result = CallTool(connId, paramsNode?["name"]?.GetValue<string>() ?? "", paramsNode?["arguments"]);
                        break;
                    default:
                        if (idNode != null) error = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" };
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"conn#{connId} {method} failed: {ex}");
                result = Error($"ERROR: {ex.Message}");
            }

            if (idNode == null) continue;
            var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(idNode.ToJsonString()) };
            if (error != null) response["error"] = error; else response["result"] = result;
            try { stdout.WriteLine(response.ToJsonString()); stdout.Flush(); }
            catch (IOException ex) { return $"write failed (peer gone while sending a response): {ex.Message}"; }
        }
    }

    static JsonObject Error(string msg) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = msg }),
        ["isError"] = true
    };

    static JsonObject CallTool(long connId, string name, JsonNode? callArgs)
    {
        _currentConn = connId;
        var res = RunTool(connId, name, new Args(callArgs));
        var content = new JsonArray();
        foreach (var c in res.Content) content.Add(c);
        var o = new JsonObject { ["content"] = content };
        if (res.IsError) o["isError"] = true;
        return o;
    }

    /// <summary>Run one tool (gate, transient-UIA retry for read-only tools, settle_ms, logging with duration). Also used by run_steps for each step.</summary>
    public static ToolResult RunTool(long connId, string name, Args args)
    {
        if (!Tools.All.TryGetValue(name, out var tool))
        {
            var near = Tools.All.Keys.Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(k, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
            return ToolResult.Text($"ERROR: unknown tool '{name}'." + (near.Count > 0 ? $" Did you mean: {string.Join(", ", near)}?" : "") + " Call server_info for the full tool list.");
        }
        var sw = Stopwatch.StartNew();
        ToolResult res;
        try
        {
            res = Attempt(tool, args);
            if (tool.Idempotent)
            {
                // transient UIA COM errors that escaped inner retries: re-run a read-only tool
                for (int i = 1; i <= 3 && res.IsError && Uia.IsTransientText(res); i++)
                {
                    Log.Write($"conn#{connId} tool {name}: transient UIA error, re-running ({i}/3)");
                    Thread.Sleep(150 * i);
                    res = Attempt(tool, args);
                }
            }
            if (tool.Settle)
            {
                int settle = args.Int("settle_ms") ?? 0;
                if (settle > 0) Thread.Sleep(Math.Min(settle, 60000));
            }
        }
        catch (ArgumentException ex) { res = ToolResult.Text("ERROR: " + ex.Message); }
        catch (Exception ex)
        {
            Log.Write($"conn#{connId} tool {name} threw: {ex}");
            res = ToolResult.Text($"ERROR: {ex.GetType().Name}: {ex.Message}");
        }
        // Log tool name, arg NAMES only (never values: they may be passwords), duration and outcome.
        long ms = sw.ElapsedMilliseconds;
        Log.Write($"conn#{connId} tool={name} args=[{string.Join(",", args.Keys())}] {ms}ms {(res.IsError ? "ERROR" : "ok")}{(ms > 10000 ? " SLOW" : "")}");
        return res;
    }

    static ToolResult Attempt(ToolDef tool, Args args)
    {
        try
        {
            if (tool.NeedsGate) { lock (AutomationGate) return tool.Run(args); }
            return tool.Run(args);
        }
        catch (Exception ex) when (tool.Idempotent && Uia.IsTransient(ex)) { return ToolResult.Text("ERROR: transient UI Automation error: " + ex.Message); }
    }
}
