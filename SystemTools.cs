using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WinAutomationMcp;

/// <summary>Commands, files, processes, clipboard. None of these touch UI Automation, so they run outside the automation gate.</summary>
static class SystemTools
{
    static string Cap(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max) + $"\n... [truncated, {s.Length - max} more chars]";
    }

    static ToolResult RunProcess(ProcessStartInfo psi, int timeoutMs, int maxChars, string? cleanupFile = null)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        var so = new StringBuilder();
        var se = new StringBuilder();
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        try { p.Start(); }
        catch (Exception ex) { if (cleanupFile != null) try { File.Delete(cleanupFile); } catch { } return $"ERROR: could not start {psi.FileName}: {ex.Message}"; }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { p.StandardInput.Close(); } catch { }
        bool timedOut = false;
        if (!p.WaitForExit(timeoutMs))
        {
            timedOut = true;
            try { p.Kill(true); } catch { }
            p.WaitForExit(5000);
        }
        else p.WaitForExit(); // flush async readers
        if (cleanupFile != null) try { File.Delete(cleanupFile); } catch { }
        string o, e2; lock (so) o = so.ToString(); lock (se) e2 = se.ToString();
        var sb = new StringBuilder();
        sb.AppendLine(timedOut ? $"ERROR: timed out after {timeoutMs}ms, process tree killed" : "OK: finished");
        sb.AppendLine($"exit_code: {(timedOut ? "(killed)" : p.ExitCode.ToString())}");
        sb.AppendLine($"duration_ms: {sw.ElapsedMilliseconds}");
        sb.AppendLine($"session_id: {Process.GetCurrentProcess().SessionId} (interactive desktop session when 1 or higher)");
        sb.AppendLine("--- stdout ---");
        sb.AppendLine(Cap(o.TrimEnd(), maxChars));
        sb.AppendLine("--- stderr ---");
        sb.AppendLine(Cap(e2.TrimEnd(), maxChars));
        var res = ToolResult.Text(sb.ToString());
        // A non-zero exit code is a normal result the caller must inspect, not a tool failure, except for timeouts.
        res.IsError = timedOut;
        return res;
    }

    public static ToolResult RunPowerShell(Args a)
    {
        var command = a.Req("command");
        int timeout = Math.Clamp(a.Int("timeout_ms") ?? 60000, 1000, 1_800_000);
        int maxChars = Math.Clamp(a.Int("max_output_chars") ?? 20000, 100, 1_000_000);
        var cwd = a.Str("cwd");
        // Script goes through a temp .ps1 (with BOM for unicode) so stderr stays plain text and exit codes are right.
        var tmp = Path.Combine(Path.GetTempPath(), $"winmcp-{Guid.NewGuid():N}.ps1");
        var script = "$ProgressPreference='SilentlyContinue'; $global:LASTEXITCODE=0; [Console]::OutputEncoding=[Text.Encoding]::UTF8\r\n"
                   + command
                   + "\r\nif (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE } else { exit 1 } } elseif ($LASTEXITCODE) { exit $LASTEXITCODE }\r\n";
        File.WriteAllText(tmp, script, new UTF8Encoding(true));
        var psi = new ProcessStartInfo { FileName = "powershell.exe" };
        foreach (var x in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", tmp }) psi.ArgumentList.Add(x);
        if (!string.IsNullOrWhiteSpace(cwd)) psi.WorkingDirectory = cwd;
        return RunProcess(psi, timeout, maxChars, tmp);
    }

    public static ToolResult RunCmd(Args a)
    {
        var command = a.Req("command");
        int timeout = Math.Clamp(a.Int("timeout_ms") ?? 60000, 1000, 1_800_000);
        int maxChars = Math.Clamp(a.Int("max_output_chars") ?? 20000, 100, 1_000_000);
        var cwd = a.Str("cwd");
        var psi = new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/d /s /c \"" + command + "\"" };
        if (!string.IsNullOrWhiteSpace(cwd)) psi.WorkingDirectory = cwd;
        return RunProcess(psi, timeout, maxChars);
    }

    // ---------- launch / url ----------

    public static ToolResult LaunchApp(Args a)
    {
        var path = a.Req("path");
        var args = a.Str("args") ?? "";
        var cwd = a.Str("cwd");
        return Uia.LaunchAndWait($"\"{path}\"", () =>
        {
            try
            {
                var psi = new ProcessStartInfo { FileName = path, Arguments = args, UseShellExecute = true };
                if (!string.IsNullOrWhiteSpace(cwd)) psi.WorkingDirectory = cwd;
                var p = Process.Start(psi);
                return (p?.Id, null);
            }
            catch (Exception ex) { return (null, $"ERROR: could not launch \"{path}\": {ex.Message}"); }
        }, a, null);
    }

    public static ToolResult LaunchPackagedApp(Args a)
    {
        var aumid = a.Req("aumid").Trim();
        if (aumid.Contains(' ') || !aumid.Contains('!')) return "ERROR: aumid must look like PackageFamilyName!AppId (see list_packaged_apps)";
        string? install = null;
        if ((a.Int("wait_ms") ?? 60000) > 0)
        {
            // Resolve the package install folder so its windows can be recognised by process path even for loose/dev-registered packages.
            var fam = aumid.Split('!')[0].Replace("'", "''");
            var r = RunPowerShell(a.With(("command", $"(Get-AppxPackage | Where-Object PackageFamilyName -eq '{fam}' | Select-Object -First 1).InstallLocation"), ("timeout_ms", 30000)));
            var txt = string.Join("\n", r.Content.Select(c => c["text"]?.ToString() ?? ""));
            var m = System.Text.RegularExpressions.Regex.Match(txt, @"--- stdout ---\r?\n(.+?)\r?\n--- stderr", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success && m.Groups[1].Value.Trim().Length > 3) install = m.Groups[1].Value.Trim();
        }
        return Uia.LaunchAndWait($"packaged app {aumid}", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = true });
                return (null, null);
            }
            catch (Exception ex) { return (null, $"ERROR: could not launch {aumid}: {ex.Message}"); }
        }, a, aumid, install);
    }

    public static ToolResult ListPackagedApps(Args a)
    {
        var f = (a.Str("filter") ?? "").Replace("'", "''");
        var cmd = $"Get-StartApps | Where-Object {{ $_.Name -like '*{f}*' -or $_.AppID -like '*{f}*' }} | Select-Object -First 60 | ForEach-Object {{ '{{0}}  |  {{1}}' -f $_.Name, $_.AppID }}";
        return RunPowerShell(a.With(("command", cmd), ("timeout_ms", 30000)));
    }

    public static ToolResult OpenUrl(Args a)
    {
        var url = a.Req("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return $"ERROR: \"{url}\" doesn't look like a valid http(s) URL";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return $"OK: opened \"{url}\" in default browser"; }
        catch (Exception ex) { return $"ERROR: could not open \"{url}\": {ex.Message}"; }
    }

    // ---------- files ----------

    public static ToolResult ReadFile(Args a)
    {
        var path = a.Req("path");
        int max = Math.Clamp(a.Int("max_bytes") ?? 100_000, 1, 5_000_000);
        long offset = Math.Max(0, a.Int("offset") ?? 0);
        bool tail = a.Bool("tail") ?? false;
        if (!File.Exists(path)) return Directory.Exists(path) ? $"ERROR: \"{path}\" is a directory. Entries: {string.Join(", ", Directory.EnumerateFileSystemEntries(path).Take(100).Select(Path.GetFileName))}" : $"ERROR: file not found: {path}";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        long start = tail ? Math.Max(0, len - max) : Math.Min(offset, len);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[Math.Min(max, len - start)];
        int read = 0;
        while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
        int nulls = buf.Take(Math.Min(read, 4096)).Count(b => b == 0);
        if (nulls > 10) return $"ERROR: \"{path}\" looks binary ({len} bytes); read_file only returns text";
        var text = new UTF8Encoding(false).GetString(buf, 0, read);
        bool truncated = start + read < len || start > 0;
        return $"OK: {path} ({len} bytes total, returned bytes {start}-{start + read}{(truncated ? ", PARTIAL: use offset/tail/max_bytes for more" : "")})\n{text}";
    }

    public static ToolResult WriteFile(Args a)
    {
        var path = Path.GetFullPath(a.Req("path"));
        var content = a.Str("content") ?? "";
        bool append = a.Bool("append") ?? false;
        bool mkdir = a.Bool("create_dirs") ?? true;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            if (!mkdir) return $"ERROR: directory does not exist: {dir}";
            Directory.CreateDirectory(dir);
        }
        var enc = new UTF8Encoding(false);
        if (append) File.AppendAllText(path, content, enc); else File.WriteAllText(path, content, enc);
        return $"OK: wrote {enc.GetByteCount(content)} bytes to {path}{(append ? " (appended)" : "")}";
    }

    // ---------- processes ----------

    public static ToolResult ListProcesses(Args a)
    {
        var filter = a.Str("name_filter");
        bool onlyWindows = a.Bool("only_with_windows") ?? false;
        int max = a.Int("max_results") ?? 300;
        var sb = new StringBuilder();
        int n = 0;
        foreach (var p in Process.GetProcesses().OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id))
        {
            try
            {
                if (!string.IsNullOrEmpty(filter) && !p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                string title = "";
                try { title = p.MainWindowTitle; } catch { }
                if (onlyWindows && string.IsNullOrEmpty(title)) continue;
                var path = Native.ProcessPath((uint)p.Id) ?? "(no access)";
                string start = "";
                try { start = p.StartTime.ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
                sb.AppendLine($"pid={p.Id} name={p.ProcessName} session={p.SessionId} start={start} path={path}{(title.Length > 0 ? $" title=\"{title}\"" : "")}");
                if (++n >= max) { sb.AppendLine("... truncated (max_results)"); break; }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return n == 0 ? "OK: no matching processes" : $"OK: {n} process(es)\n{sb}";
    }

    public static ToolResult KillProcess(Args a)
    {
        int pid = a.ReqInt("pid");
        var expected = a.Str("expected_path");
        if (string.IsNullOrWhiteSpace(expected)) return "ERROR: expected_path is required (full path of the executable). kill_process refuses to act without it, so one agent cannot kill another agent's app by PID alone.";
        if (pid <= 4) return "ERROR: refusing to kill a system PID";
        if (pid == Environment.ProcessId) return "ERROR: refusing to kill the automation server itself";
        var actual = Native.ProcessPath((uint)pid);
        if (actual == null) return $"ERROR: cannot determine the executable path of pid {pid} (process gone or access denied); refusing to kill";
        string Norm(string s) { try { return Path.GetFullPath(s).TrimEnd('\\'); } catch { return s; } }
        if (!string.Equals(Norm(actual), Norm(expected), StringComparison.OrdinalIgnoreCase))
            return $"ERROR: path mismatch, refusing to kill. pid {pid} is \"{actual}\" but expected_path was \"{expected}\"";
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(a.Bool("tree") ?? false);
            bool exited = p.WaitForExit(5000);
            Log.Write($"kill_process pid={pid} path={actual} exited={exited}");
            return exited ? $"OK: killed pid {pid} ({actual})" : $"ERROR: kill signal sent to pid {pid} but it has not exited after 5s";
        }
        catch (Exception ex) { return $"ERROR: could not kill pid {pid}: {ex.Message}"; }
    }

    // ---------- clipboard ----------

    static T Sta<T>(Func<T> f)
    {
        T r = default!;
        Exception? ex = null;
        var t = new Thread(() => { try { r = f(); } catch (Exception e) { ex = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (ex != null) throw ex;
        return r;
    }

    public static ToolResult ClipboardGet(Args a)
    {
        var text = Sta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (text == null) return "OK: clipboard holds no text";
        return $"OK: clipboard text ({text.Length} chars)\n{text}";
    }

    public static ToolResult ClipboardSet(Args a)
    {
        var text = a.Req("text");
        for (int i = 0; ; i++)
        {
            try { Sta(() => { Clipboard.SetDataObject(text, true, 5, 100); return 0; }); break; }
            catch (Exception) when (i < 3) { Thread.Sleep(150); }
        }
        return $"OK: clipboard set ({text.Length} chars). Paste with key_press CTRL+V in the target window.";
    }
}
