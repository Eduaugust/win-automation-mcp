using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace WinAutomationMcp;

static class Log
{
    static readonly object Gate = new();
    static string? _path;
    public static string? Path => _path;

    public static void Init(string dir, int port)
    {
        try
        {
            Directory.CreateDirectory(dir);
            _path = System.IO.Path.Combine(dir, $"win-mcp-{port}.log");
            if (File.Exists(_path) && new FileInfo(_path).Length > 5_000_000)
            {
                var old = _path + ".1";
                try { if (File.Exists(old)) File.Delete(old); File.Move(_path, old); } catch { }
            }
        }
        catch { _path = null; }
    }

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {msg}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { }
            if (_path != null)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false)); } catch { }
            }
        }
    }
}

/// <summary>Tolerant accessor over the tools/call "arguments" object (accepts "5" for ints, "true" for bools).</summary>
sealed class Args
{
    readonly JsonNode? _n;
    public Args(JsonNode? n) { _n = n; }

    public bool Has(string k) => _n?[k] != null;

    public JsonNode? Raw => _n;

    /// <summary>Copy of these arguments with extra/overridden keys (values are strings, numbers or bools).</summary>
    public Args With(params (string key, JsonNode? value)[] kv)
    {
        var o = _n is JsonObject jo ? (JsonObject)JsonNode.Parse(jo.ToJsonString())! : new JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return new Args(o);
    }

    /// <summary>Copy that only sets keys which are not already present (defaults).</summary>
    public Args WithDefaults(params (string key, JsonNode? value)[] kv)
    {
        var o = _n is JsonObject jo ? (JsonObject)JsonNode.Parse(jo.ToJsonString())! : new JsonObject();
        foreach (var (k, v) in kv) if (o[k] == null) o[k] = v;
        return new Args(o);
    }

    public string? Str(string k)
    {
        var v = _n?[k];
        if (v == null) return null;
        if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) return s;
        return v.ToString();
    }
    public string Req(string k) => Str(k) ?? throw new ArgumentException($"{k} is required");

    public int? Int(string k)
    {
        var s = Str(k);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
        throw new ArgumentException($"{k} must be an integer (got \"{s}\")");
    }
    public int ReqInt(string k) => Int(k) ?? throw new ArgumentException($"{k} is required");

    public bool? Bool(string k)
    {
        var s = Str(k);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (bool.TryParse(s, out var b)) return b;
        if (s == "1") return true;
        if (s == "0") return false;
        throw new ArgumentException($"{k} must be true or false (got \"{s}\")");
    }

    public IEnumerable<string> Keys()
    {
        if (_n is JsonObject o) foreach (var kv in o) yield return kv.Key;
    }
}

sealed class ToolResult
{
    public List<JsonObject> Content { get; } = new();
    public bool IsError { get; set; }

    public static ToolResult Text(string s)
    {
        var r = new ToolResult();
        r.Content.Add(new JsonObject { ["type"] = "text", ["text"] = s });
        r.IsError = s.StartsWith("ERROR", StringComparison.Ordinal);
        return r;
    }
    public ToolResult AddText(string s) { Content.Add(new JsonObject { ["type"] = "text", ["text"] = s }); return this; }
    public ToolResult AddImage(byte[] data, string mime)
    {
        Content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mime });
        return this;
    }
    public static implicit operator ToolResult(string s) => Text(s);
}

sealed record P(string Name, string Type, string Desc, bool Required = false, string[]? Enum = null);

sealed class ToolDef
{
    public string Name = "";
    public string Description = "";
    public P[] Params = Array.Empty<P>();
    public Func<Args, ToolResult> Run = _ => "";
    /// <summary>True when the tool touches UI Automation / input and must be serialized across connections.</summary>
    public bool NeedsGate = true;
    /// <summary>True when the tool accepts the generic settle_ms parameter (sleep after the action).</summary>
    public bool Settle;
    /// <summary>True for read-only tools that are safe to re-run when UIA throws a transient COM error (0x80040201).</summary>
    public bool Idempotent;

    public JsonObject ToJson()
    {
        var props = new JsonObject();
        var req = new JsonArray();
        var all = new List<P>(Params);
        if (Settle) all.Add(new P("settle_ms", "integer", "Optional: wait this many ms after the action before returning, so the UI can settle (default 0)."));
        foreach (var p in all)
        {
            var o = new JsonObject { ["type"] = p.Type, ["description"] = p.Desc };
            if (p.Type == "array") o["items"] = new JsonObject { ["type"] = "object" };
            if (p.Enum != null) { var arr = new JsonArray(); foreach (var e in p.Enum) arr.Add(e); o["enum"] = arr; }
            props[p.Name] = o;
            if (p.Required) req.Add(p.Name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (req.Count > 0) schema["required"] = req;
        return new JsonObject { ["name"] = Name, ["description"] = Description, ["inputSchema"] = schema };
    }
}
