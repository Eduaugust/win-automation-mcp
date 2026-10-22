using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WinAutomationMcp;

/// <summary>Screen recording through ffmpeg (gdigrab to H.264 mp4). ffmpeg runs as a child of this server, so it lives in the interactive desktop session.</summary>
static class Recorder
{
    sealed class Rec
    {
        public string Id = "";
        public Process P = null!;
        public string Output = "";
        public string? CaptionFile;
        public DateTime Started;
        public string Region = "";
        public int MaxSeconds;
        public StringBuilder Err = new();
        public string Cmd = "";
    }

    static readonly Dictionary<string, Rec> Active = new();
    static readonly object Lock = new();
    static int _counter;

    public static string? FfmpegPath() => FindTool("ffmpeg.exe", "WIN_MCP_FFMPEG");
    static string? FfprobePath() => FindTool("ffprobe.exe", "WIN_MCP_FFPROBE");

    static string? FindTool(string exe, string envVar)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        // walk up from the exe folder looking for tools\ffmpeg.exe (bin\Debug\net8.0-windows -> project root)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var c = Path.Combine(dir.FullName, "tools", exe);
            if (File.Exists(c)) return c;
            c = Path.Combine(dir.FullName, exe);
            if (File.Exists(c)) return c;
        }
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var c = Path.Combine(p.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    static string DefaultDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "recordings");

    static string FfEscapePath(string p) => p.Replace('\\', '/').Replace(":", "\\:");

    public static ToolResult Start(Args a)
    {
        var ffmpeg = FfmpegPath();
        if (ffmpeg == null) return "ERROR: ffmpeg.exe not found. Put it in <server folder>\\tools\\ffmpeg.exe, or set env WIN_MCP_FFMPEG to its full path.";

        int fps = Math.Clamp(a.Int("fps") ?? 15, 1, 60);
        int maxSeconds = Math.Clamp(a.Int("max_seconds") ?? 300, 1, 3600);
        int maxWidth = a.Int("max_width") ?? 0;
        bool bringToFront = a.Bool("bring_to_front") ?? true;
        var windowMatch = a.Str("window");
        var caption = a.Str("caption");

        Rectangle origin;
        if (!string.IsNullOrWhiteSpace(windowMatch))
        {
            var (win, h, err) = Uia.ResolveWindow(windowMatch);
            if (err != null) return err;
            if (h == IntPtr.Zero) return $"ERROR: window \"{windowMatch}\" has no native handle";
            if (bringToFront || Native.IsIconic(h)) { Native.Focus(h); Thread.Sleep(200); }
            origin = Native.VisibleRect(h);
            // include dropdowns/popups that stick out of the window
            foreach (var pr in Native.PopupRects(h)) origin = Rectangle.Union(origin, pr);
        }
        else origin = Native.VirtualScreen();

        Rectangle rect = origin;
        if (a.Int("region_x").HasValue && a.Int("region_y").HasValue && a.Int("region_w").HasValue && a.Int("region_h").HasValue)
        {
            var baseRect = string.IsNullOrWhiteSpace(windowMatch) ? Native.VirtualScreen() : origin;
            rect = new Rectangle(baseRect.X + a.Int("region_x")!.Value, baseRect.Y + a.Int("region_y")!.Value, a.Int("region_w")!.Value, a.Int("region_h")!.Value);
        }
        rect = Rectangle.Intersect(rect, Native.VirtualScreen());
        int w = rect.Width & ~1, hgt = rect.Height & ~1;
        if (w < 16 || hgt < 16) return $"ERROR: recording area too small or off-screen ({rect.Width}x{rect.Height}). Is the window minimized?";

        var output = a.Str("output");
        if (string.IsNullOrWhiteSpace(output))
            output = Path.Combine(DefaultDir(), $"rec-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        output = Path.GetFullPath(output);
        if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) output += ".mp4";
        if (File.Exists(output) && !(a.Bool("overwrite") ?? false)) return $"ERROR: output file already exists: {output} (pass overwrite=true or another output)";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        string id;
        lock (Lock)
        {
            if (Active.Values.Any(r => string.Equals(r.Output, output, StringComparison.OrdinalIgnoreCase))) return $"ERROR: a recording to {output} is already running";
            id = $"rec{++_counter}";
        }

        string? captionFile = null;
        var filters = new List<string> { $"scale=trunc(iw/2)*2:trunc(ih/2)*2" };
        if (maxWidth > 0 && w > maxWidth) filters.Add($"scale={maxWidth & ~1}:-2");
        if (caption != null)
        {
            captionFile = output + ".caption.txt";
            File.WriteAllText(captionFile, caption.Length == 0 ? " " : caption, new UTF8Encoding(false));
            var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "segoeui.ttf");
            if (!File.Exists(font)) font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf");
            filters.Add($"drawtext=fontfile='{FfEscapePath(font)}':textfile='{FfEscapePath(captionFile)}':reload=1:x=(w-text_w)/2:y=h-text_h-24:fontsize=26:fontcolor=white:box=1:boxcolor=black@0.65:boxborderw=10");
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(output)!,
        };
        void Add(params string[] xs) { foreach (var x in xs) psi.ArgumentList.Add(x); }
        Add("-hide_banner", "-loglevel", "warning", "-y",
            "-f", "gdigrab", "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-draw_mouse", "1", "-show_region", "0",
            "-offset_x", rect.X.ToString(CultureInfo.InvariantCulture), "-offset_y", rect.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{w}x{hgt}", "-i", "desktop",
            "-t", maxSeconds.ToString(CultureInfo.InvariantCulture),
            "-vf", string.Join(",", filters),
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "26", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            output);

        var rec = new Rec { Id = id, Output = output, CaptionFile = captionFile, Started = DateTime.Now, MaxSeconds = maxSeconds, Region = $"{rect.X},{rect.Y},{w}x{hgt}" };
        rec.Cmd = ffmpeg + " " + string.Join(" ", psi.ArgumentList.Select(x => x.Contains(' ') ? "\"" + x + "\"" : x));
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (rec.Err) { if (rec.Err.Length < 8000) rec.Err.AppendLine(e.Data); } };
        p.OutputDataReceived += (_, _) => { };
        rec.P = p;
        try
        {
            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
        }
        catch (Exception ex) { return $"ERROR: could not start ffmpeg: {ex.Message}"; }

        // fail fast if ffmpeg dies immediately (bad args, no desktop access)
        if (p.WaitForExit(1200))
        {
            string e; lock (rec.Err) e = rec.Err.ToString();
            return $"ERROR: ffmpeg exited immediately (code {p.ExitCode}). {e}";
        }
        lock (Lock) Active[id] = rec;
        Log.Write($"recording {id} started pid={p.Id} -> {output} region={rec.Region} fps={fps} max={maxSeconds}s");
        return $"OK: recording started id={id} pid={p.Id}\noutput: {output}\narea: x,y,w,h = {rec.Region} (fixed for the whole recording), fps={fps}, safety cap {maxSeconds}s\ncaption overlay: {(captionFile != null ? "on (change with set_recording_caption)" : "off")}\nCall stop_recording to finish the file. Fetch it from the Mac with: scp win-vm:{output.Replace('\\', '/')} .";
    }

    static Rec? Pick(string? id, out string? err)
    {
        err = null;
        lock (Lock)
        {
            if (!string.IsNullOrEmpty(id))
            {
                if (Active.TryGetValue(id, out var r)) return r;
                err = $"ERROR: no active recording with id \"{id}\". Active: [{string.Join(", ", Active.Keys)}]";
                return null;
            }
            if (Active.Count == 1) return Active.Values.First();
            if (Active.Count == 0) { err = "ERROR: no active recording"; return null; }
            err = $"ERROR: several recordings are active, pass id. Active: [{string.Join(", ", Active.Keys)}]";
            return null;
        }
    }

    public static ToolResult Stop(Args a)
    {
        var rec = Pick(a.Str("id"), out var err);
        if (rec == null) return err!;
        var res = Finish(rec);
        lock (Lock) Active.Remove(rec.Id);
        return res;
    }

    static ToolResult Finish(Rec rec)
    {
        var p = rec.P;
        bool already = p.HasExited;
        bool killed = false;
        if (!already)
        {
            try { p.StandardInput.WriteLine("q"); p.StandardInput.Flush(); } catch { }
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } killed = true; p.WaitForExit(5000); }
        }
        p.WaitForExit(2000);
        var wall = (DateTime.Now - rec.Started).TotalSeconds;
        string errText; lock (rec.Err) errText = string.Join("\n", rec.Err.ToString().Split('\n').Where(l => !l.Contains("not enough frames to estimate rate"))).Trim();
        if (rec.CaptionFile != null) { try { File.Delete(rec.CaptionFile); } catch { } }
        Log.Write($"recording {rec.Id} stopped exit={p.ExitCode} killed={killed} wall={wall:0.0}s");

        if (!File.Exists(rec.Output)) return $"ERROR: no output file was produced ({rec.Output}). ffmpeg exit code {p.ExitCode}. {errText}";
        var size = new FileInfo(rec.Output).Length;
        string probe = Probe(rec.Output);
        var sb = new StringBuilder();
        sb.AppendLine($"OK: recording {rec.Id} stopped{(already ? " (it had already ended, e.g. max_seconds reached)" : "")}{(killed ? " (ffmpeg had to be killed, the mp4 may be incomplete)" : "")}");
        sb.AppendLine($"path: {rec.Output}");
        sb.AppendLine($"size_bytes: {size}");
        sb.AppendLine($"wall_clock_seconds: {wall:0.0}");
        sb.AppendLine(probe);
        sb.AppendLine($"fetch: scp win-vm:{rec.Output.Replace('\\', '/')} .");
        if (errText.Length > 0) sb.AppendLine("ffmpeg stderr: " + errText);
        return sb.ToString();
    }

    static string Probe(string file)
    {
        var ffprobe = FfprobePath();
        if (ffprobe == null) return "duration: (ffprobe not found, file not verified)";
        try
        {
            var psi = new ProcessStartInfo(ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var x in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_name,codec_type,width,height,avg_frame_rate", "-of", "default=nw=1", file }) psi.ArgumentList.Add(x);
            using var pr = Process.Start(psi)!;
            var o = pr.StandardOutput.ReadToEnd();
            var e = pr.StandardError.ReadToEnd();
            pr.WaitForExit(10000);
            var lines = o.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string dur = lines.FirstOrDefault(l => l.StartsWith("duration=")) ?? "duration=?";
            string codec = lines.FirstOrDefault(l => l.StartsWith("codec_name=")) ?? "";
            string wd = lines.FirstOrDefault(l => l.StartsWith("width=")) ?? "";
            string ht = lines.FirstOrDefault(l => l.StartsWith("height=")) ?? "";
            string fr = lines.FirstOrDefault(l => l.StartsWith("avg_frame_rate=")) ?? "";
            return $"duration_seconds: {dur.Substring(dur.IndexOf('=') + 1)}\nstream: {codec} {wd} {ht} {fr}".Trim() + (e.Trim().Length > 0 ? "\nffprobe stderr: " + e.Trim() : "");
        }
        catch (Exception ex) { return "duration: (ffprobe failed: " + ex.Message + ")"; }
    }

    public static ToolResult SetCaption(Args a)
    {
        var rec = Pick(a.Str("id"), out var err);
        if (rec == null) return err!;
        if (rec.CaptionFile == null) return "ERROR: this recording was started without the caption parameter, so no overlay exists. Start it with caption=\"...\" (even an empty string).";
        var text = a.Str("text") ?? "";
        File.WriteAllText(rec.CaptionFile, text.Length == 0 ? " " : text, new UTF8Encoding(false));
        return $"OK: caption of {rec.Id} set (ffmpeg reloads it on the next frames)";
    }

    public static ToolResult Status(Args a)
    {
        lock (Lock)
        {
            if (Active.Count == 0) return "OK: no active recordings. ffmpeg: " + (FfmpegPath() ?? "NOT FOUND");
            var sb = new StringBuilder("OK: active recordings\n");
            foreach (var r in Active.Values)
                sb.AppendLine($"id={r.Id} pid={r.P.Id} running={!r.P.HasExited} elapsed={(DateTime.Now - r.Started).TotalSeconds:0.0}s cap={r.MaxSeconds}s area={r.Region} output={r.Output}");
            return sb.ToString();
        }
    }

    /// <summary>Called on server shutdown so no ffmpeg is left behind.</summary>
    public static void StopAll()
    {
        List<Rec> list; lock (Lock) list = Active.Values.ToList();
        foreach (var r in list) { try { Finish(r); } catch { } }
    }
}
