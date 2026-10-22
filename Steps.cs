using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WinAutomationMcp;

/// <summary>run_steps: execute a JSON array of tool calls in order in one MCP call (the "actions[]" idea).</summary>
static class Steps
{
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "action", "tool", "optional", "retries", "retry_delay_ms", "label", "allow_nonzero" };
    static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase) { "text", "value", "content", "command", "keys" };
    static readonly HashSet<string> ElementTools = new() { "click_element", "type_text", "set_value", "clear_field", "select_option", "get_element_text" };
    // Tools whose output IS the point (listings, text): shown in full (capped) even when verbose=false.
    static readonly HashSet<string> FullOutput = new() { "describe_ui", "find_elements", "get_element_tree", "get_element_text", "list_windows", "read_file", "clipboard_get", "list_processes", "run_powershell", "run_cmd", "get_window_rect", "launch_app", "launch_packaged_app", "wait_for_window", "list_packaged_apps", "server_info" };
    const int MaxImages = 12;

    static string TextOf(ToolResult r) => string.Join("\n", r.Content.Where(c => c["type"]?.ToString() == "text").Select(c => c["text"]?.ToString() ?? ""));

    static bool Failed(string tool, ToolResult r, bool allowNonzero)
    {
        if (r.IsError) return true;
        var t = TextOf(r);
        if (t.StartsWith("FAIL") || t.StartsWith("TIMEOUT") || t.StartsWith("ERROR")) return true;
        if (!allowNonzero && (tool == "run_powershell" || tool == "run_cmd"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^exit_code: (-?\d+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (m.Success && m.Groups[1].Value != "0") return true;
        }
        return false;
    }

    static string Summarize(string tool, JsonObject step)
    {
        var parts = new List<string>();
        foreach (var kv in step)
        {
            if (Reserved.Contains(kv.Key)) continue;
            if (Hidden.Contains(kv.Key)) { parts.Add($"{kv.Key}=<{(kv.Value?.ToString().Length ?? 0)} chars>"); continue; }
            var v = kv.Value?.ToString() ?? "";
            if (v.Length > 40) v = v.Substring(0, 40) + "...";
            parts.Add($"{kv.Key}={v}");
        }
        return tool + (parts.Count > 0 ? " " + string.Join(" ", parts) : "");
    }

    static string FirstLine(string s, int max)
    {
        var l = s.Split('\n').FirstOrDefault(x => x.Trim().Length > 0)?.Trim() ?? "";
        return l.Length <= max ? l : l.Substring(0, max) + "...";
    }

    public static ToolResult Run(Args a)
    {
        var raw = a.Raw?["steps"];
        if (raw is JsonValue jv && jv.TryGetValue<string>(out var js))
        {
            try { raw = JsonNode.Parse(js); } catch (Exception ex) { return $"ERROR: steps is a string but not valid JSON: {ex.Message}"; }
        }
        if (raw is not JsonArray arr || arr.Count == 0) return "ERROR: steps must be a non-empty JSON array of step objects, e.g. [{\"action\":\"click_element\",\"target\":\"Login\",\"control_type\":\"Button\"}]";
        string? defWindow = a.Str("window");
        bool stopOnError = a.Bool("stop_on_error") ?? true;
        bool shotOnError = a.Bool("screenshot_on_error") ?? true;
        bool verbose = a.Bool("verbose") ?? false;
        bool strict = a.Bool("strict") ?? true;
        int defTimeout = a.Int("default_timeout_ms") ?? 30000;
        long conn = Program.CurrentConn;

        var sw = Stopwatch.StartNew();
        var lines = new List<string>();
        var images = new List<JsonObject>();
        int failedAt = -1;
        string? failText = null; string? failWindow = null;
        int ran = 0, softFailures = 0, failures = 0;

        for (int i = 0; i < arr.Count; i++)
        {
            int no = i + 1;
            if (arr[i] is not JsonObject step) { failedAt = no; failText = "step is not a JSON object"; lines.Add($"[{no}] FAILED: step is not a JSON object"); break; }
            string tool = (step["action"] ?? step["tool"])?.ToString() ?? "";
            if (tool == "") { failedAt = no; failText = "step has no \"action\""; lines.Add($"[{no}] FAILED: step has no \"action\" (tool name)"); break; }
            if (tool == "run_steps") { failedAt = no; failText = "nested run_steps is not allowed"; lines.Add($"[{no}] FAILED: nested run_steps is not allowed"); break; }
            bool optional = step["optional"]?.GetValue<bool>() ?? false;
            int retries = Math.Clamp(int.TryParse(step["retries"]?.ToString(), out var rr) ? rr : 0, 0, 10);
            int retryDelay = int.TryParse(step["retry_delay_ms"]?.ToString(), out var rd) ? rd : 500;
            bool allowNonzero = step["allow_nonzero"]?.GetValue<bool>() ?? false;
            string label = step["label"]?.ToString() ?? "";
            var summary = Summarize(tool, step) + (label != "" ? $" ({label})" : "");
            var stepSw = Stopwatch.StartNew();
            ran++;

            ToolResult? res = null;
            string outcome = "";
            bool failed = false;

            if (tool == "sleep")
            {
                int ms = Math.Clamp(int.TryParse((step["ms"] ?? step["duration_ms"])?.ToString(), out var m) ? m : 500, 0, 120000);
                Thread.Sleep(ms);
                lines.Add($"[{no}] OK sleep {ms}ms");
                continue;
            }

            for (int attempt = 0; ; attempt++)
            {
                var obj = (JsonObject)JsonNode.Parse(step.ToJsonString())!;
                foreach (var k in Reserved) obj.Remove(k);
                var args = new Args(obj);
                if (!string.IsNullOrEmpty(defWindow)) args = args.WithDefaults(("window", defWindow));
                if (strict && ElementTools.Contains(tool)) args = args.WithDefaults(("strict", true));

                int timeout = int.TryParse(step["timeout_ms"]?.ToString(), out var t) ? t : defTimeout;
                int hardTimeout = (tool == "wait_for" || tool == "run_powershell" || tool == "run_cmd" || tool == "wait_for_window" || tool.StartsWith("launch_"))
                    ? Math.Max(timeout, tool.StartsWith("launch_") ? (int.TryParse(step["wait_ms"]?.ToString(), out var wm) ? wm : 60000) : timeout) + 10000
                    : timeout;
                var task = Task.Run(() => Program.RunTool(conn, tool, args));
                if (!task.Wait(hardTimeout))
                {
                    res = ToolResult.Text($"ERROR: step timed out after {hardTimeout}ms (the underlying call may still be running on the VM)");
                    failed = true;
                }
                else
                {
                    res = task.Result;
                    failed = Failed(tool, res, allowNonzero);
                }
                if (!failed || attempt >= retries) break;
                Thread.Sleep(retryDelay);
            }

            var text = TextOf(res!);
            int imgCount = res!.Content.Count(c => c["type"]?.ToString() == "image");
            foreach (var c in res.Content.Where(c => c["type"]?.ToString() == "image"))
                if (images.Count < MaxImages) images.Add((JsonObject)JsonNode.Parse(c.ToJsonString())!);
            if (verbose) outcome = text.TrimEnd();
            else if (FullOutput.Contains(tool))
            {
                var full = text.TrimEnd();
                outcome = (full.Length > 3500 ? full.Substring(0, 3500) + "\n... [output cut, use verbose or a narrower query]" : full);
                if (full.Contains('\n')) outcome = "\n" + string.Join("\n", outcome.Split('\n').Select(l => "      " + l));
            }
            else outcome = FirstLine(text, 200);
            if (imgCount > 0) outcome += $" [+{imgCount} image(s) attached]";

            if (!failed)
            {
                lines.Add($"[{no}] OK {summary} ({stepSw.ElapsedMilliseconds}ms): {outcome}");
                continue;
            }
            if (optional)
            {
                softFailures++;
                lines.Add($"[{no}] FAILED-but-optional {summary} ({stepSw.ElapsedMilliseconds}ms): {FirstLine(text, 300)}");
                continue;
            }
            lines.Add($"[{no}] FAILED {summary} ({stepSw.ElapsedMilliseconds}ms):\n{text.TrimEnd()}");
            if (failedAt < 0) { failedAt = no; failText = text; failWindow = step["window"]?.ToString() ?? defWindow; }
            else failures++;
            if (stopOnError) break;
        }

        var sb = new StringBuilder();
        if (failedAt < 0)
            sb.AppendLine($"OK: all {arr.Count} step(s) passed in {sw.ElapsedMilliseconds}ms" + (softFailures > 0 ? $" ({softFailures} optional step(s) failed)" : ""));
        else
            sb.AppendLine($"FAILED at step {failedAt}/{arr.Count} after {sw.ElapsedMilliseconds}ms ({ran} step(s) executed, {Math.Max(0, arr.Count - ran)} not run{(stopOnError ? "" : $"; stop_on_error=false so later steps were attempted, {failures} more step(s) failed")})");
        foreach (var l in lines) sb.AppendLine(l);

        var result = new ToolResult();
        if (failedAt >= 0)
        {
            // Last UI state: compact listing of the window, or the window list when the window itself is gone.
            try
            {
                var ui = !string.IsNullOrEmpty(failWindow)
                    ? Program.RunTool(conn, "describe_ui", new Args(new JsonObject { ["window"] = failWindow, ["max_elements"] = 40 }))
                    : Program.RunTool(conn, "list_windows", new Args(new JsonObject()));
                sb.AppendLine($"--- last UI state ({(string.IsNullOrEmpty(failWindow) ? "list_windows" : $"describe_ui window=\"{failWindow}\"")}) ---");
                sb.AppendLine(TextOf(ui).TrimEnd());
            }
            catch (Exception ex) { sb.AppendLine($"(could not read the UI state: {ex.Message})"); }
            if (shotOnError)
            {
                try
                {
                    var o = new JsonObject { ["max_width"] = 900, ["format"] = "jpeg", ["quality"] = 60 };
                    if (!string.IsNullOrEmpty(failWindow)) o["window"] = failWindow;
                    var shot = Program.RunTool(conn, "screenshot", new Args(o));
                    int n = 0;
                    foreach (var c in shot.Content.Where(c => c["type"]?.ToString() == "image")) { images.Add((JsonObject)JsonNode.Parse(c.ToJsonString())!); n++; }
                    sb.AppendLine(n > 0 ? "--- on_error screenshot attached (last image) ---" : $"(on_error screenshot failed: {FirstLine(TextOf(shot), 200)})");
                }
                catch (Exception ex) { sb.AppendLine($"(on_error screenshot failed: {ex.Message})"); }
            }
            result.IsError = true;
        }
        result.AddText(sb.ToString());
        foreach (var im in images) result.Content.Add(im);
        return result;
    }
}
