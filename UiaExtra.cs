using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace WinAutomationMcp;

/// <summary>Element query: what the element tools match on.</summary>
sealed record Query(string? Target, string? AutomationId, string? Name, string? NameRegex, string? ControlType, string? Match)
{
    public static Query From(Args a) => new(
        Norm(a.Str("target")), Norm(a.Str("automation_id")), Norm(a.Str("name")), Norm(a.Str("name_regex")),
        Norm(a.Str("control_type")), Norm(a.Str("match")));

    static string? Norm(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public bool IsEmpty => Target == null && AutomationId == null && Name == null && NameRegex == null && ControlType == null;

    public override string ToString()
    {
        var l = new List<string>();
        if (Target != null) l.Add($"target=\"{Target}\"{(string.Equals(Match, "contains", StringComparison.OrdinalIgnoreCase) ? " (contains)" : "")}");
        if (AutomationId != null) l.Add($"automation_id=\"{AutomationId}\"");
        if (Name != null) l.Add($"name=\"{Name}\"");
        if (NameRegex != null) l.Add($"name_regex=/{NameRegex}/");
        if (ControlType != null) l.Add($"control_type={ControlType}");
        return l.Count == 0 ? "(window)" : string.Join(" ", l);
    }
}

static partial class Uia
{
    // ---------- element query with the new precise filters ----------

    /// <summary>Find descendants of <paramref name="scope"/> matching the query. Legacy semantics (target = AutomationId first, then Name)
    /// are unchanged; automation_id / name (exact) and name_regex are applied as additional AND filters in document order.</summary>
    public static List<AutomationElement> FindMatches(AutomationElement scope, Query q, int limit = 500)
    {
        bool precise = q.AutomationId != null || q.Name != null || q.NameRegex != null;
        if (!precise) return FindMatchesLegacy(scope, q.Target, q.ControlType, q.Match, limit);

        Regex? rx = null;
        if (q.NameRegex != null)
        {
            try { rx = new Regex(q.NameRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)); }
            catch (ArgumentException ex) { throw new ArgumentException($"name_regex is not a valid regular expression: {ex.Message}"); }
        }
        var ct = ParseControlType(q.ControlType);
        var all = Retry(() => ct.HasValue ? scope.FindAllDescendants(cf => cf.ByControlType(ct.Value)) : scope.FindAllDescendants());
        bool contains = string.Equals(q.Match, "contains", StringComparison.OrdinalIgnoreCase);
        var res = new List<AutomationElement>();
        foreach (var e in all)
        {
            string id = "", nm = "";
            try { id = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
            try { nm = e.Name ?? ""; } catch { }
            if (q.AutomationId != null && id != q.AutomationId) continue;
            if (q.Name != null && nm != q.Name) continue;
            if (rx != null && !rx.IsMatch(nm)) continue;
            if (q.Target != null)
            {
                bool t = contains
                    ? id.Contains(q.Target, StringComparison.OrdinalIgnoreCase) || nm.Contains(q.Target, StringComparison.OrdinalIgnoreCase)
                    : id == q.Target || nm == q.Target;
                if (!t) continue;
            }
            res.Add(e);
            if (res.Count >= limit) break;
        }
        return res;
    }

    /// <summary>Optional parent scoping: parent (AutomationId or Name, exact) + parent_control_type + parent_nth select an element
    /// under which the search is done. Returns the window itself when no parent is given.</summary>
    public static (AutomationElement? scope, string? err) ResolveScope(AutomationElement win, Args a)
    {
        var parent = a.Str("parent");
        var pct = a.Str("parent_control_type");
        if (string.IsNullOrEmpty(parent) && string.IsNullOrEmpty(pct)) return (win, null);
        var pq = new Query(string.IsNullOrEmpty(parent) ? null : parent, null, null, null, string.IsNullOrEmpty(pct) ? null : pct, null);
        var list = FindMatchesLegacy(win, pq.Target, pq.ControlType, null, 200);
        if (list.Count == 0) return (null, $"ERROR: parent {pq} not found in the window. Candidates:\n{NearbyCandidates(win)}");
        int pn = a.Int("parent_nth") ?? 0;
        if (pn < 0 || pn >= list.Count) return (null, $"ERROR: parent_nth={pn} out of range, {list.Count} parent candidate(s):\n{CandidateRows(list)}");
        return (list[pn], null);
    }

    // ---------- describe_ui ----------

    static readonly HashSet<ControlType> InteractiveTypes = new()
    {
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink,
        ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider, ControlType.Spinner,
        ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem, ControlType.Document
    };

    public static ToolResult DescribeUi(Args a)
    {
        var wm = a.Req("window");
        int max = Math.Clamp(a.Int("max_elements") ?? 80, 1, 1000);
        var types = (a.Str("types") ?? "interactive").Trim();
        bool includeText = a.Bool("include_text") ?? false;
        bool includeOffscreen = a.Bool("include_offscreen") ?? false;
        var win = FindWindow(wm);
        if (win == null) return NoWindow(wm);
        var (scope, scopeErr) = ResolveScope(win, a);
        if (scopeErr != null) return scopeErr;
        var h = HwndOf(win);
        var origin = h != IntPtr.Zero ? Native.VisibleRect(h) : Rectangle.Empty;

        HashSet<ControlType>? wanted = null;
        if (!types.Equals("interactive", StringComparison.OrdinalIgnoreCase) && !types.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            wanted = new HashSet<ControlType>();
            foreach (var t in types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                wanted.Add(ParseControlType(t)!.Value);
        }
        bool all = types.Equals("all", StringComparison.OrdinalIgnoreCase);

        var elements = Retry(() => scope!.FindAllDescendants());
        const int scanLimit = 3000;
        var perType = new Dictionary<ControlType, int>();   // index among ALL descendants of this control type (what control_type+nth uses)
        var rows = new List<(ControlType ct, int ctIdx, string id, string nm, Rectangle r, bool en, bool off)>();
        int scanned = 0;
        foreach (var e in elements)
        {
            if (++scanned > scanLimit) break;
            ControlType ct;
            try { ct = e.ControlType; } catch { continue; }
            perType.TryGetValue(ct, out int idx);
            perType[ct] = idx + 1;

            bool want = wanted != null ? wanted.Contains(ct)
                      : all || InteractiveTypes.Contains(ct) || (includeText && ct == ControlType.Text);
            if (!want) continue;
            string id = "", nm = "";
            bool en = true, off = false;
            try { id = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
            try { nm = e.Name ?? ""; } catch { }
            try { en = e.Properties.IsEnabled.ValueOrDefault; } catch { }
            try { off = e.Properties.IsOffscreen.ValueOrDefault; } catch { }
            var r = RectOf(e);
            if (!includeOffscreen && (off || r.Width <= 0 || r.Height <= 0)) continue;
            rows.Add((ct, idx, id, nm, r, en, off));
        }

        var idCount = rows.Where(x => x.id != "").GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.Count());
        var nmCount = rows.Where(x => x.nm != "").GroupBy(x => (x.nm, x.ct)).ToDictionary(g => g.Key, g => g.Count());
        var nameOnlyCount = rows.Where(x => x.nm != "").GroupBy(x => x.nm).ToDictionary(g => g.Key, g => g.Count());

        var sb = new StringBuilder();
        sb.AppendLine($"window \"{win.Name}\" visible_rect=({origin.X},{origin.Y},{origin.Width}x{origin.Height}); rel=(x,y,wxh) is relative to that origin, click=(x,y) is the window-relative centre for click_at / mouse_click relative=true");
        int shown = 0;
        foreach (var x in rows)
        {
            if (shown >= max) break;
            string sel;
            if (x.id != "" && idCount[x.id] == 1 && (!nameOnlyCount.ContainsKey(x.id))) sel = $"automation_id={x.id}";
            else if (x.nm != "" && nmCount[(x.nm, x.ct)] == 1 && nameOnlyCount[x.nm] == 1) sel = $"target={Q(x.nm)}";
            else if (x.nm != "" && nmCount[(x.nm, x.ct)] == 1) sel = $"target={Q(x.nm)} control_type={x.ct}";
            else sel = $"control_type={x.ct} nth={x.ctIdx}";
            int rx = x.r.X - origin.X, ry = x.r.Y - origin.Y;
            sb.AppendLine($"{x.ct} \"{Trunc(x.nm, 60)}\" id=\"{x.id}\" rel=({rx},{ry},{x.r.Width}x{x.r.Height}) click=({rx + x.r.Width / 2},{ry + x.r.Height / 2}){(x.en ? "" : " DISABLED")}{(x.off ? " OFFSCREEN" : "")} sel: {sel}");
            shown++;
        }
        if (rows.Count > shown) sb.AppendLine($"... {rows.Count - shown} more matching elements not shown (raise max_elements, or narrow with types/parent)");
        if (scanned > scanLimit) sb.AppendLine($"... scan stopped after {scanLimit} descendants");
        if (rows.Count == 0) sb.AppendLine("(no matching elements; the app may draw its UI itself. Use types=all or a screenshot plus click_at)");
        return sb.ToString();
    }

    static string Q(string s) => s.Contains(' ') || s.Contains('"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
    static string Trunc(string s, int n) => s.Length <= n ? s.Replace("\n", " ") : s.Substring(0, n).Replace("\n", " ") + "...";

    // ---------- set_value ----------

    /// <summary>Read the control text, polling up to ~1 s until it equals the expected value (apps apply input asynchronously).</summary>
    static string ReadUntil(AutomationElement el, string expected)
    {
        var sw = Stopwatch.StartNew();
        string now = ReadText(el);
        while (!SameOrMasked(now, expected) && sw.ElapsedMilliseconds < 1000) { Thread.Sleep(100); now = ReadText(el); }
        return now;
    }

    /// <summary>Custom password boxes expose their value as bullets; a bullet string of the right length counts as a match (length-only check).</summary>
    static bool IsMasked(string s) => s.Length > 0 && s.All(ch => ch == '\u25CF' || ch == '\u2022' || ch == '*' || ch == '\u00B7');
    static bool SameOrMasked(string now, string expected) => now == expected || (IsMasked(now) && now.Length == expected.Length);
    static string MaskNote(string now, string expected) => now != expected ? $" (the control exposes a masked value of {now.Length} chars, so only the length could be verified)" : "";

    public static ToolResult SetValue(Args a)
    {
        var r = Resolve(a);
        if (r.Err != null) return r.Err;
        var el = r.El!;
        var text = a.Str("text") ?? "";
        string method = (a.Str("method") ?? "auto").ToLowerInvariant();
        int delay = a.Int("delay_ms") ?? 40;
        bool verify = a.Bool("verify") ?? true;
        bool isPassword = false;
        try { isPassword = el.Properties.IsPassword.ValueOrDefault; } catch { }
        if (!Native.Focus(HwndOf(r.Win))) return $"ERROR: could not bring window \"{a.Str("window")}\" to the foreground; nothing set";

        var sb = new StringBuilder(r.Note);
        if (method == "auto" || method == "value")
        {
            try
            {
                var vp = el.Patterns.Value.PatternOrDefault;
                if (vp != null && !vp.IsReadOnly.ValueOrDefault)
                {
                    vp.SetValue(text);
                    if (isPassword || !verify) return sb + $"OK: set value via ValuePattern on {DescribeElement(el)}" + (isPassword ? " (password field: the value cannot be read back, check the length visually or by logging in)" : "");
                    var now = ReadUntil(el, text);
                    if (SameOrMasked(now, text)) return sb + $"OK: set value via ValuePattern and verified ({DescribeElement(el)}){MaskNote(now, text)}";
                    sb.AppendLine($"NOTE: ValuePattern accepted the value but the control reads \"{Trunc(now, 80)}\"; falling back to keystrokes");
                }
                else sb.AppendLine("NOTE: no writable ValuePattern; using keystrokes");
            }
            catch (Exception ex) { sb.AppendLine($"NOTE: ValuePattern failed ({ex.Message}); using keystrokes"); }
            if (method == "value") return sb + $"ERROR: ValuePattern did not set the value of {DescribeElement(el)}";
        }

        // Keyboard path: focus, clear slowly, type slowly, verify.
        try { el.Focus(); } catch { }
        var clear = ClearField(a.With(("method", "auto"), ("delay_ms", delay)));
        if (clear.IsError) return sb + "ERROR: could not clear the field first: " + clear.Content.FirstOrDefault()?["text"];
        try { el.Focus(); } catch { }
        Thread.Sleep(40);
        foreach (var ch in text) { Keyboard.Type(ch); if (delay > 0) Thread.Sleep(delay); }
        if (isPassword || !verify) return sb + $"OK: typed {text.Length} chars with {delay}ms between keys into {DescribeElement(el)}" + (isPassword ? " (password field: cannot be verified)" : "");
        var after = ReadUntil(el, text);
        if (SameOrMasked(after, text)) return sb + $"OK: typed with {delay}ms between keys and verified ({DescribeElement(el)}){MaskNote(after, text)}";
        return sb + $"ERROR: after typing, the control reads \"{Trunc(after, 80)}\" instead of \"{Trunc(text, 80)}\" ({DescribeElement(el)}). Try a larger delay_ms.";
    }

    // ---------- click_at: window-relative click ----------

    public static ToolResult ClickAt(Args a) =>
        MouseClick(string.IsNullOrWhiteSpace(a.Str("window")) ? a : a.WithDefaults(("relative", true)));

    // ---------- waiting for a window (launch helpers) ----------

    public static HashSet<long> SnapshotWindows()
    {
        var set = new HashSet<long>();
        try
        {
            foreach (var w in Retry(() => Automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window))))
            {
                var h = HwndOf(w);
                if (h != IntPtr.Zero) set.Add(h.ToInt64());
            }
        }
        catch { }
        return set;
    }

    sealed record WinCrit(string? Match, int? Pid, string? PkgPrefix, string? TitleRegex, HashSet<long>? Exclude, string? InstallPath = null);

    static (AutomationElement? win, IntPtr h) FindVisibleWindow(WinCrit c)
    {
        Regex? rx = null;
        if (c.TitleRegex != null) rx = new Regex(c.TitleRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        var windows = Retry(() => Automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)));
        bool noCrit = c.Match == null && c.Pid == null && c.PkgPrefix == null && c.TitleRegex == null;
        foreach (var w in windows)
        {
            var h = HwndOf(w);
            if (h == IntPtr.Zero) continue;
            if (c.Exclude != null && c.Exclude.Contains(h.ToInt64())) continue;
            if (!Native.IsWindowVisible(h) || Native.IsCloaked(h)) continue;
            var v = Native.VisibleRect(h);
            if (v.Width < 40 || v.Height < 30) continue;
            int wpid = 0; string title = "";
            try { wpid = w.Properties.ProcessId.ValueOrDefault; } catch { }
            try { title = w.Name ?? ""; } catch { }
            bool ok = noCrit;
            if (c.Pid.HasValue && wpid == c.Pid.Value) ok = true;
            if (!ok && c.PkgPrefix != null)
            {
                var path = Native.ProcessPath((uint)wpid) ?? "";
                if (path.Contains("\\" + c.PkgPrefix + "_", StringComparison.OrdinalIgnoreCase)) ok = true;
                if (!ok && c.InstallPath != null && path.StartsWith(c.InstallPath, StringComparison.OrdinalIgnoreCase)) ok = true;
            }
            if (!ok && c.Match != null)
            {
                string proc = "";
                try { proc = Process.GetProcessById(wpid).ProcessName; } catch { }
                if (proc.Contains(c.Match, StringComparison.OrdinalIgnoreCase) || title.Contains(c.Match, StringComparison.OrdinalIgnoreCase)) ok = true;
            }
            if (!ok && rx != null && rx.IsMatch(title)) ok = true;
            if (ok) return (w, h);
        }
        return (null, IntPtr.Zero);
    }

    /// <summary>Poll until a matching visible top-level window exists. Takes the automation gate only for each poll, so other sessions are not blocked for a 60 s startup.</summary>
    static ToolResult WaitLoop(WinCrit c, int timeoutMs, int pollMs, string what)
    {
        var sw = Stopwatch.StartNew();
        string last = "no matching window yet";
        while (true)
        {
            try
            {
                (AutomationElement? w, IntPtr h) f;
                lock (Program.AutomationGate) f = FindVisibleWindow(c);
                if (f.w != null)
                {
                    lock (Program.AutomationGate)
                    {
                        var info = WindowInfo(f.w, f.h);
                        return $"OK: {what} window appeared after {sw.ElapsedMilliseconds}ms\n{info}\nUse window=\"{f.w.Name}\" (or the process name) in the other tools.";
                    }
                }
            }
            catch (ArgumentException) { throw; }
            catch (Exception ex) { last = $"UI Automation error ({(IsTransient(ex) ? "transient" : "unexpected")}, retrying): {ex.Message}"; }
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(Math.Max(100, Math.Min(pollMs, timeoutMs - (int)sw.ElapsedMilliseconds)));
        }
        string open;
        lock (Program.AutomationGate) { try { open = ListWindows(); } catch { open = "(could not list windows)"; } }
        return $"TIMEOUT: no visible window for {what} after {sw.ElapsedMilliseconds}ms ({last}). Open windows:\n{open}";
    }

    public static ToolResult WaitForWindow(Args a)
    {
        var match = a.Str("window");
        var c = new WinCrit(string.IsNullOrEmpty(match) ? null : match, a.Int("pid"), PkgPrefix(a.Str("aumid")), a.Str("title_regex"),
            (a.Bool("allow_existing") ?? true) ? null : SnapshotWindowsLocked());
        return WaitLoop(c, a.Int("timeout_ms") ?? 60000, a.Int("poll_interval_ms") ?? 500, $"\"{match ?? a.Str("title_regex") ?? a.Str("aumid") ?? "any"}\"");
    }

    static HashSet<long> SnapshotWindowsLocked() { lock (Program.AutomationGate) return SnapshotWindows(); }

    static string? PkgPrefix(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        var fam = aumid.Split('!')[0];           // Publisher.Name_8wekyb3d8bbwe
        var us = fam.LastIndexOf('_');
        return us > 0 ? fam.Substring(0, us) : fam;
    }

    public static ToolResult LaunchAndWait(string what, Func<(int? pid, string? err)> start, Args a, string? aumid, string? installPath = null)
    {
        int waitMs = a.Int("wait_ms") ?? (aumid != null ? 60000 : 0);
        bool allowExisting = a.Bool("allow_existing") ?? false;
        HashSet<long>? before = (waitMs > 0 && !allowExisting) ? SnapshotWindowsLocked() : null;
        var (pid, err) = start();
        if (err != null) return err;
        var head = $"OK: launched {what}{(pid.HasValue ? $" (pid={pid})" : "")}";
        if (waitMs <= 0) return head;
        var match = a.Str("window");
        var c = new WinCrit(string.IsNullOrEmpty(match) ? null : match, pid, PkgPrefix(aumid), a.Str("title_regex"), before, installPath);
        var w = WaitLoop(c, waitMs, a.Int("poll_interval_ms") ?? 500, what);
        var text = w.Content.FirstOrDefault()?["text"]?.ToString() ?? "";
        var res = ToolResult.Text(text.StartsWith("OK") ? head + "\n" + text : "ERROR: " + head.Substring(4) + ", but " + text);
        return res;
    }
}
