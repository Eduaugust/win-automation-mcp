using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace WinAutomationMcp;

/// <summary>UI Automation based tools: windows, element lookup, clicks, typing, conditions.</summary>
static partial class Uia
{
    public static readonly UIA3Automation Automation = new();

    // ---------- transient error handling (0x80040201 etc.) ----------

    public static bool IsTransient(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            if (e is COMException ce && (uint)ce.HResult == 0x80040201) return true;
            var m = e.Message ?? "";
            if (m.Contains("0x80040201", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("unable to invoke any of the subscribers", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("0x80040200", StringComparison.OrdinalIgnoreCase)) return true;
            if (e.InnerException == null) break;
        }
        return false;
    }

    public static bool IsTransientText(ToolResult r) =>
        r.Content.Any(c => (c["text"]?.ToString() ?? "").Contains("transient UI Automation error", StringComparison.OrdinalIgnoreCase));

    /// <summary>Run a UIA call, retrying a few times on transient COM errors.</summary>
    public static T Retry<T>(Func<T> f, int attempts = 4)
    {
        for (int i = 1; ; i++)
        {
            try { return f(); }
            catch (Exception ex) when (IsTransient(ex) && i < attempts)
            {
                Log.Write($"transient UIA error, retry {i}/{attempts - 1}: {ex.Message}");
                Thread.Sleep(120 * i);
            }
        }
    }

    // ---------- windows ----------

    static string ProcName(AutomationElement w)
    {
        try { return Process.GetProcessById(w.Properties.ProcessId.ValueOrDefault).ProcessName; } catch { return "?"; }
    }

    public static IntPtr HwndOf(AutomationElement w)
    {
        try
        {
            var h = w.Properties.NativeWindowHandle.ValueOrDefault;
            if (h != IntPtr.Zero) return h;
        }
        catch { }
        try { return Process.GetProcessById(w.Properties.ProcessId.ValueOrDefault).MainWindowHandle; } catch { return IntPtr.Zero; }
    }

    static string StateOf(IntPtr h) => Native.IsIconic(h) ? "minimized" : Native.IsZoomed(h) ? "maximized" : "normal";

    public static string ListWindows()
    {
        var windows = Retry(() => Automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)));
        var sb = new StringBuilder();
        foreach (var w in windows)
        {
            var h = HwndOf(w);
            string extra = "";
            if (h != IntPtr.Zero)
            {
                var r = Native.VisibleRect(h);
                extra = $" hwnd={h.ToInt64()} state={StateOf(h)} rect=({r.X},{r.Y},{r.Width}x{r.Height})";
            }
            sb.AppendLine($"title=\"{w.Name}\" process={ProcName(w)}{extra}");
        }
        return sb.Length == 0 ? "(no top-level windows found)" : sb.ToString();
    }

    public static AutomationElement? FindWindow(string windowMatch)
    {
        var windows = Retry(() => Automation.GetDesktop().FindAllChildren(cf => cf.ByControlType(ControlType.Window)));
        AutomationElement? byProcess = null, byTitle = null;
        foreach (var w in windows)
        {
            string proc = "";
            try { proc = Process.GetProcessById(w.Properties.ProcessId.ValueOrDefault).ProcessName; } catch { }
            if (proc.Equals(windowMatch, StringComparison.OrdinalIgnoreCase)) return w;
            if (proc.Contains(windowMatch, StringComparison.OrdinalIgnoreCase)) byProcess ??= w;
            var title = w.Name ?? "";
            if (title.Contains(windowMatch, StringComparison.OrdinalIgnoreCase)) byTitle ??= w;
        }
        return byProcess ?? byTitle;
    }

    static string NoWindow(string m) => $"ERROR: no window matching \"{m}\". Open windows: {ListWindows()}";

    public static (AutomationElement? win, IntPtr hwnd, string? err) ResolveWindow(string match)
    {
        var win = FindWindow(match);
        if (win == null) return (null, IntPtr.Zero, NoWindow(match));
        return (win, HwndOf(win), null);
    }

    /// <summary>Focus the named window before sending input. Returns an ERROR string if it cannot be focused (so input is never sent to the wrong window).</summary>
    public static string? FocusWindowOrError(string? windowMatch)
    {
        if (string.IsNullOrWhiteSpace(windowMatch)) return null;
        var (win, h, err) = ResolveWindow(windowMatch);
        if (err != null) return err;
        if (h == IntPtr.Zero) return $"ERROR: window \"{windowMatch}\" has no native handle; input NOT sent";
        if (!Native.Focus(h)) return $"ERROR: could not bring window \"{windowMatch}\" to the foreground; input NOT sent";
        return null;
    }

    static string WindowInfo(AutomationElement win, IntPtr h)
    {
        var o = Native.OuterRect(h);
        var v = Native.VisibleRect(h);
        bool fg = Native.GetForegroundWindow() == h;
        return $"title=\"{win.Name}\" process={ProcName(win)} pid={win.Properties.ProcessId.ValueOrDefault} hwnd={h.ToInt64()} state={StateOf(h)} foreground={fg.ToString().ToLowerInvariant()}\n" +
               $"visible_rect: x={v.X} y={v.Y} width={v.Width} height={v.Height}  (this is the origin used by screenshot region_* and recording)\n" +
               $"outer_rect: x={o.X} y={o.Y} width={o.Width} height={o.Height}  (what move_window/resize_window set)";
    }

    public static ToolResult GetWindowRect(Args a)
    {
        var (win, h, err) = ResolveWindow(a.Req("window"));
        if (err != null) return err;
        if (h == IntPtr.Zero) return "ERROR: window has no native handle";
        return "OK: " + WindowInfo(win!, h);
    }

    public static ToolResult FocusWindow(Args a)
    {
        var m = a.Req("window");
        var (win, h, err) = ResolveWindow(m);
        if (err != null) return err;
        if (h == IntPtr.Zero) return "ERROR: window has no native handle";
        bool ok = Native.Focus(h);
        return (ok ? "OK: focused\n" : "ERROR: could not bring the window to the foreground (Windows foreground lock). Current state:\n") + WindowInfo(win!, h);
    }

    public static ToolResult MoveWindow(Args a)
    {
        var (win, h, err) = ResolveWindow(a.Req("window"));
        if (err != null) return err;
        int x = a.ReqInt("x"), y = a.ReqInt("y");
        int? w = a.Int("width"), hh = a.Int("height");
        if (Native.IsIconic(h) || Native.IsZoomed(h)) { Native.ShowWindow(h, Native.SW_RESTORE); Thread.Sleep(150); }
        var cur = Native.OuterRect(h);
        uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        if (!w.HasValue || !hh.HasValue) flags |= Native.SWP_NOSIZE;
        Native.SetWindowPos(h, IntPtr.Zero, x, y, w ?? cur.Width, hh ?? cur.Height, flags);
        Thread.Sleep(100);
        return "OK: moved\n" + WindowInfo(win!, h);
    }

    public static ToolResult ResizeWindow(Args a)
    {
        var (win, h, err) = ResolveWindow(a.Req("window"));
        if (err != null) return err;
        int w = a.ReqInt("width"), hh = a.ReqInt("height");
        if (Native.IsIconic(h) || Native.IsZoomed(h)) { Native.ShowWindow(h, Native.SW_RESTORE); Thread.Sleep(150); }
        Native.SetWindowPos(h, IntPtr.Zero, 0, 0, w, hh, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOMOVE);
        Thread.Sleep(100);
        return "OK: resized\n" + WindowInfo(win!, h);
    }

    public static ToolResult SetWindowState(Args a)
    {
        var (win, h, err) = ResolveWindow(a.Req("window"));
        if (err != null) return err;
        var st = a.Req("state").ToLowerInvariant();
        int cmd = st switch { "maximize" or "maximized" => Native.SW_MAXIMIZE, "minimize" or "minimized" => Native.SW_MINIMIZE, "restore" or "normal" => Native.SW_RESTORE, _ => -1 };
        if (cmd < 0) return "ERROR: state must be maximize | minimize | restore";
        Native.ShowWindow(h, cmd);
        Thread.Sleep(250);
        return "OK: " + st + "\n" + WindowInfo(win!, h);
    }

    // ---------- elements ----------

    static string DescribeElement(AutomationElement e)
    {
        string autoId = "", nm = "", ct = "";
        try { autoId = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
        try { nm = e.Name ?? ""; } catch { }
        try { ct = e.ControlType.ToString(); } catch { }
        string rect = "";
        try { var r = e.Properties.BoundingRectangle.ValueOrDefault; rect = $"{r.X},{r.Y},{r.Width}x{r.Height}"; } catch { }
        return $"[{ct}] id=\"{autoId}\" name=\"{nm}\" rect=({rect})";
    }

    static string PatternsOf(AutomationElement el)
    {
        var l = new List<string>();
        var p = el.Patterns;
        void A(string n, Func<bool> f) { try { if (f()) l.Add(n); } catch { } }
        A("Invoke", () => p.Invoke.PatternOrDefault != null);
        A("Value", () => p.Value.PatternOrDefault != null);
        A("ExpandCollapse", () => p.ExpandCollapse.PatternOrDefault != null);
        A("SelectionItem", () => p.SelectionItem.PatternOrDefault != null);
        A("Selection", () => p.Selection.PatternOrDefault != null);
        A("Toggle", () => p.Toggle.PatternOrDefault != null);
        A("Scroll", () => p.Scroll.PatternOrDefault != null);
        A("RangeValue", () => p.RangeValue.PatternOrDefault != null);
        A("Text", () => p.Text.PatternOrDefault != null);
        A("Grid", () => p.Grid.PatternOrDefault != null);
        A("GridItem", () => p.GridItem.PatternOrDefault != null);
        A("Window", () => p.Window.PatternOrDefault != null);
        return string.Join(",", l);
    }

    static Rectangle RectOf(AutomationElement el)
    {
        try { return el.Properties.BoundingRectangle.ValueOrDefault; } catch { return Rectangle.Empty; }
    }

    static bool IsBlind(AutomationElement el, out string why)
    {
        var r = RectOf(el);
        bool off = false;
        try { off = el.Properties.IsOffscreen.ValueOrDefault; } catch { }
        if (r.Width <= 0 || r.Height <= 0) { why = $"empty rect ({r.X},{r.Y},{r.Width}x{r.Height})"; return true; }
        if (off) { why = "IsOffscreen=true"; return true; }
        why = "";
        return false;
    }

    static ControlType? ParseControlType(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (Enum.TryParse<ControlType>(s.Replace(" ", ""), true, out var ct)) return ct;
        throw new ArgumentException($"unknown control_type \"{s}\". Valid: {string.Join(", ", Enum.GetNames<ControlType>())}");
    }

    /// <summary>All elements matching target (AutomationId first, then Name) optionally filtered by control type.
    /// match = exact (default) or contains.</summary>
    static List<AutomationElement> FindMatchesLegacy(AutomationElement win, string? target, string? controlType, string? match = null, int limit = 500)
    {
        var ct = ParseControlType(controlType);
        bool contains = string.Equals(match, "contains", StringComparison.OrdinalIgnoreCase);
        var res = new List<AutomationElement>();

        void AddAll(IEnumerable<AutomationElement> els)
        {
            foreach (var e in els)
            {
                if (res.Count >= limit) return;
                bool dup = false;
                foreach (var x in res) { try { if (x.Equals(e)) { dup = true; break; } } catch { } }
                if (!dup) res.Add(e);
            }
        }

        if (string.IsNullOrEmpty(target))
        {
            AddAll(Retry(() => ct.HasValue ? win.FindAllDescendants(cf => cf.ByControlType(ct.Value)) : win.FindAllDescendants()));
            return res;
        }

        if (!contains)
        {
            AddAll(Retry(() => ct.HasValue
                ? win.FindAllDescendants(cf => cf.ByAutomationId(target).And(cf.ByControlType(ct.Value)))
                : win.FindAllDescendants(cf => cf.ByAutomationId(target))));
            AddAll(Retry(() => ct.HasValue
                ? win.FindAllDescendants(cf => cf.ByName(target).And(cf.ByControlType(ct.Value)))
                : win.FindAllDescendants(cf => cf.ByName(target))));
        }
        else
        {
            var all = Retry(() => ct.HasValue ? win.FindAllDescendants(cf => cf.ByControlType(ct.Value)) : win.FindAllDescendants());
            var ids = new List<AutomationElement>();
            var names = new List<AutomationElement>();
            foreach (var e in all)
            {
                string id = "", nm = "";
                try { id = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
                try { nm = e.Name ?? ""; } catch { }
                if (id.Contains(target, StringComparison.OrdinalIgnoreCase)) ids.Add(e);
                else if (nm.Contains(target, StringComparison.OrdinalIgnoreCase)) names.Add(e);
            }
            AddAll(ids); AddAll(names);
        }
        return res;
    }

    static string NearbyCandidates(AutomationElement window)
    {
        var sb = new StringBuilder();
        int n = 0;
        void Walk(AutomationElement el, int depth)
        {
            if (n >= 40 || depth > 3) return;
            sb.AppendLine(DescribeElement(el));
            n++;
            AutomationElement[] children;
            try { children = el.FindAllChildren(); } catch { return; }
            foreach (var c in children) { if (n >= 40) return; Walk(c, depth + 1); }
        }
        Walk(window, 0);
        return sb.ToString();
    }

    sealed record Resolved(AutomationElement Win, AutomationElement? El, string? Err, string Note);

    /// <summary>Common window + (parent) + target/automation_id/name/name_regex + control_type + nth + match resolution used by every element tool.</summary>
    static Resolved Resolve(Args a, bool needElement = true)
    {
        var wm = a.Req("window");
        var q = Query.From(a);
        int nth = a.Int("nth") ?? 0;
        var win = FindWindow(wm);
        if (win == null) return new Resolved(null!, null, NoWindow(wm), "");
        if (q.IsEmpty)
        {
            if (needElement) return new Resolved(win, null, "ERROR: give at least one of target, automation_id, name, name_regex or control_type", "");
            return new Resolved(win, null, null, "");
        }
        var (scope, scopeErr) = ResolveScope(win, a);
        if (scopeErr != null) return new Resolved(win, null, scopeErr, "");
        var list = FindMatches(scope!, q);
        if (list.Count == 0)
            return new Resolved(win, null, $"ERROR: no element matching {q} in window \"{wm}\"{(scope != win ? " under the given parent" : "")}. Candidates:\n{NearbyCandidates(scope!)}", "");
        if (nth < 0 || nth >= list.Count)
            return new Resolved(win, null, $"ERROR: nth={nth} is out of range, {list.Count} element(s) matched {q}:\n{CandidateRows(list)}", "");
        if (list.Count > 1 && !a.Has("nth"))
        {
            if (a.Bool("strict") ?? false)
                return new Resolved(win, null, $"ERROR: ambiguous: {list.Count} elements matched {q} and no nth was given (strict mode). Candidates:\n{CandidateRows(list)}Pass nth=<index>, add control_type/automation_id/name_regex, or scope with parent.", "");
            return new Resolved(win, list[nth], null, $"NOTE: {list.Count} elements matched; used nth=0. Pass control_type and/or nth to disambiguate (see find_elements), or strict=true to get an error with the candidates.\n");
        }
        return new Resolved(win, list[nth], null, "");
    }

    static string CandidateRows(List<AutomationElement> list)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(list.Count, 20); i++) sb.AppendLine($"  nth={i} {DescribeElement(list[i])}");
        if (list.Count > 20) sb.AppendLine($"  ... {list.Count - 20} more");
        return sb.ToString();
    }

    public static ToolResult FindElements(Args a)
    {
        var wm = a.Req("window");
        var win = FindWindow(wm);
        if (win == null) return NoWindow(wm);
        int max = a.Int("max_results") ?? 50;
        var (scope, scopeErr) = ResolveScope(win, a);
        if (scopeErr != null) return scopeErr;
        var list = FindMatches(scope!, Query.From(a), 1000);
        if (list.Count == 0) return $"OK: 0 elements matched in window \"{wm}\".";
        var sb = new StringBuilder();
        sb.AppendLine($"OK: {list.Count} element(s) matched (showing {Math.Min(max, list.Count)}); nth is the 0-based index to pass to click_element/type_text/etc.");
        for (int i = 0; i < Math.Min(max, list.Count); i++)
        {
            var e = list[i];
            string id = "", nm = "", ct = "";
            bool en = true, off = false;
            try { id = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
            try { nm = e.Name ?? ""; } catch { }
            try { ct = e.ControlType.ToString(); } catch { }
            try { en = e.Properties.IsEnabled.ValueOrDefault; } catch { }
            try { off = e.Properties.IsOffscreen.ValueOrDefault; } catch { }
            var r = RectOf(e);
            sb.AppendLine($"nth={i} [{ct}] id=\"{id}\" name=\"{nm}\" rect=({r.X},{r.Y},{r.Width}x{r.Height}) enabled={en.ToString().ToLowerInvariant()} offscreen={off.ToString().ToLowerInvariant()} patterns={PatternsOf(e)}");
        }
        return sb.ToString();
    }

    public static ToolResult GetElementTree(Args a)
    {
        var wm = a.Req("window");
        int maxDepth = a.Int("max_depth") ?? 3;
        int maxElements = a.Int("max_elements") ?? 200;
        var win = FindWindow(wm);
        if (win == null) return NoWindow(wm);
        var sb = new StringBuilder();
        int count = 0;
        void Walk(AutomationElement el, int depth, string indent)
        {
            if (count >= maxElements || depth > maxDepth) return;
            sb.AppendLine(indent + DescribeElement(el));
            count++;
            AutomationElement[] children;
            try { children = Retry(() => el.FindAllChildren()); } catch { return; }
            foreach (var c in children)
            {
                if (count >= maxElements) { sb.AppendLine(indent + "  ... truncated (max_elements reached)"); return; }
                Walk(c, depth + 1, indent + "  ");
            }
        }
        Walk(win, 0, "");
        return sb.ToString();
    }

    public static ToolResult ClickElement(Args a)
    {
        var r = Resolve(a);
        if (r.Err != null) return r.Err;
        var el = r.El!;
        try { Native.Focus(HwndOf(r.Win)); } catch { }

        string button = (a.Str("button") ?? "left").ToLowerInvariant();
        bool dbl = a.Bool("double") ?? false;
        var sb = new StringBuilder(r.Note);
        bool blind = IsBlind(el, out var why);
        if (blind)
        {
            // Virtualized / offscreen items: coordinates are useless, try the semantic patterns first.
            try { var inv = el.Patterns.Invoke.PatternOrDefault; if (inv != null) { inv.Invoke(); sb.AppendLine($"OK: invoked (InvokePattern) {DescribeElement(el)}"); sb.AppendLine($"WARNING: element has {why}; a coordinate click was NOT possible, used InvokePattern instead."); return sb.ToString(); } } catch { }
            try { var si = el.Patterns.SelectionItem.PatternOrDefault; if (si != null) { si.Select(); sb.AppendLine($"OK: selected (SelectionItemPattern) {DescribeElement(el)}"); sb.AppendLine($"WARNING: element has {why}; used SelectionItemPattern instead of a click."); return sb.ToString(); } } catch { }
        }
        try
        {
            if (button == "right") el.RightClick();
            else if (dbl) el.DoubleClick();
            else el.Click();
        }
        catch (Exception ex)
        {
            if (blind) return $"ERROR: element has {why} and no Invoke/SelectionItem pattern; blind click failed ({ex.Message}). {DescribeElement(el)}";
            try
            {
                var pt = el.GetClickablePoint();
                Mouse.Click(pt);
            }
            catch (Exception ex2) { return $"ERROR: could not click {DescribeElement(el)}: {ex2.Message}"; }
        }
        sb.AppendLine($"OK: clicked {DescribeElement(el)}");
        if (blind) sb.AppendLine($"WARNING: element has {why}; the click was blind and may have landed on nothing or on something else. Verify with screenshot/assert_state.");
        return sb.ToString();
    }

    public static string ReadText(AutomationElement el)
    {
        try { var vp = el.Patterns.Value.PatternOrDefault; if (vp != null) { var v = vp.Value.ValueOrDefault; if (!string.IsNullOrEmpty(v)) return v; } } catch { }
        try { var tp = el.Patterns.Text.PatternOrDefault; if (tp != null) { var t = tp.DocumentRange.GetText(100000); if (!string.IsNullOrEmpty(t)) return t.TrimEnd('\r', '\n'); } } catch { }
        string nm = "";
        try { nm = el.Name ?? ""; } catch { }
        if (!string.IsNullOrEmpty(nm)) return nm;
        // WinUI containers often expose their text only through Text children.
        try
        {
            var kids = el.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            var parts = kids.Take(20).Select(k => { try { return k.Name ?? ""; } catch { return ""; } }).Where(s => s.Length > 0);
            return string.Join(" ", parts);
        }
        catch { return ""; }
    }

    public static ToolResult GetElementText(Args a)
    {
        var r = Resolve(a);
        if (r.Err != null) return r.Err;
        return r.Note + ReadText(r.El!);
    }

    public static ToolResult TypeText(Args a)
    {
        var r = Resolve(a);
        if (r.Err != null) return r.Err;
        var el = r.El!;
        var text = a.Str("text") ?? "";
        try { Native.Focus(HwndOf(r.Win)); } catch { }
        try
        {
            var vp = el.Patterns.Value.PatternOrDefault;
            if (vp != null && !vp.IsReadOnly.ValueOrDefault)
            {
                vp.SetValue(text);
                return r.Note + $"OK: set value via ValuePattern on {DescribeElement(el)}";
            }
        }
        catch { }
        try
        {
            el.Focus();
            Keyboard.Type(text);
            return r.Note + $"OK: typed via keystrokes into {DescribeElement(el)}";
        }
        catch (Exception ex) { return $"ERROR: could not type into {DescribeElement(el)}: {ex.Message}"; }
    }

    public static ToolResult ClearField(Args a)
    {
        var wm = a.Req("window");
        string method = (a.Str("method") ?? "auto").ToLowerInvariant();
        int delay = a.Int("delay_ms") ?? 20;
        var r = Resolve(a, needElement: false);
        if (r.Err != null) return r.Err;
        var el = r.El;
        if (!Native.Focus(HwndOf(r.Win))) return $"ERROR: could not bring window \"{wm}\" to the foreground; nothing cleared";
        string? before = el != null ? ReadText(el) : null;

        if (el != null && (method == "auto" || method == "value"))
        {
            try
            {
                var vp = el.Patterns.Value.PatternOrDefault;
                if (vp != null && !vp.IsReadOnly.ValueOrDefault)
                {
                    vp.SetValue("");
                    if (string.IsNullOrEmpty(ReadText(el)) || ReadText(el) == (el.Name ?? "\u0001"))
                        return r.Note + $"OK: cleared via ValuePattern ({DescribeElement(el)})";
                }
            }
            catch { }
            if (method == "value") return $"ERROR: ValuePattern not available or did not clear the field ({DescribeElement(el)})";
        }

        if (el != null) { try { el.Focus(); } catch { } }
        Thread.Sleep(60);
        if (method != "backspace")
        {
            SendKeys.SendWait("^a");
            Thread.Sleep(Math.Max(30, delay));
            SendKeys.SendWait("{DELETE}");
            Thread.Sleep(Math.Max(30, delay));
            if (el != null)
            {
                var now = ReadText(el);
                if (string.IsNullOrEmpty(now)) return r.Note + $"OK: cleared via CTRL+A, DELETE ({DescribeElement(el)})";
            }
            else if (method == "keys") return "OK: sent CTRL+A, DELETE to the focused control (cannot verify without target)";
        }
        // Slow path for custom controls that lose characters: caret to end, then BACKSPACE one by one.
        int n = Math.Clamp((before?.Length ?? a.Int("max_chars") ?? 50) + 2, 4, 400);
        SendKeys.SendWait("{END}");
        for (int i = 0; i < n; i++) { SendKeys.SendWait("{BACKSPACE}"); if (delay > 0) Thread.Sleep(delay); }
        if (el != null)
        {
            var now = ReadText(el);
            if (!string.IsNullOrEmpty(now) && now != (el.Name ?? "")) return r.Note + $"ERROR: field still contains \"{now}\" after {n} BACKSPACEs ({DescribeElement(el)}). Note: password fields hide their value, so this may be a false alarm.";
        }
        return r.Note + $"OK: cleared via END + {n} BACKSPACE (delay {delay}ms)";
    }

    public static ToolResult SelectOption(Args a)
    {
        var r = Resolve(a);
        if (r.Err != null) return r.Err;
        var el = r.El!;
        var value = a.Req("value");
        try { Native.Focus(HwndOf(r.Win)); } catch { }
        var pats = PatternsOf(el);
        var sb = new StringBuilder(r.Note);

        // 1. Editable/value control (editable combo, text box): set directly.
        var ec = el.Patterns.ExpandCollapse.PatternOrDefault;
        var vp = el.Patterns.Value.PatternOrDefault;
        var selItem = el.Patterns.SelectionItem.PatternOrDefault;
        var sel = el.Patterns.Selection.PatternOrDefault;

        if (ec == null && sel == null && selItem == null)
        {
            if (vp != null && !vp.IsReadOnly.ValueOrDefault)
            {
                vp.SetValue(value);
                return sb + $"OK: set value via ValuePattern on {DescribeElement(el)} (no ExpandCollapse/Selection support, so the text was set directly, which may not trigger a selection change)";
            }
            return $"ERROR: select_option is not supported by {DescribeElement(el)}. Patterns exposed: [{pats}]. Needs ExpandCollapse, Selection or SelectionItem (or a writable Value). Fall back to click_element/key_press, or find the item with find_elements.";
        }

        // 2. The element itself is a list item: just select it if it matches.
        if (ec == null && sel == null && selItem != null)
        {
            if (!string.Equals(el.Name, value, StringComparison.OrdinalIgnoreCase))
                return $"ERROR: element is a single item named \"{el.Name}\"; pass the container (combo/list) as target, or value=\"{el.Name}\"";
            selItem.Select();
            return sb + $"OK: selected {DescribeElement(el)}";
        }

        // 3. Container: expand if needed, locate the item, select it.
        bool expanded = false;
        if (ec != null)
        {
            try
            {
                if (ec.ExpandCollapseState.Value == ExpandCollapseState.Collapsed) { ec.Expand(); expanded = true; Thread.Sleep(250); }
            }
            catch (Exception ex) { sb.AppendLine($"NOTE: Expand failed: {ex.Message}"); }
        }

        AutomationElement? item = FindItem(el, value)
                                  ?? FindItem(r.Win, value)
                                  ?? FindItemInSameProcessPopups(r.Win, value);
        if (item == null)
        {
            var seen = ListItemNames(el);
            if (expanded) { try { ec!.Collapse(); } catch { } }
            return $"ERROR: item \"{value}\" not found under {DescribeElement(el)} after {(expanded ? "expanding" : "looking")}. Items visible to UI Automation: [{string.Join(" | ", seen)}]. The dropdown popup of custom controls (WinUI/Syncfusion) is often not exposed to UIA; in that case use click_element/mouse_click plus screenshot(include_popups=true).";
        }
        try
        {
            var si = item.Patterns.SelectionItem.PatternOrDefault;
            if (si != null) si.Select();
            else
            {
                var inv = item.Patterns.Invoke.PatternOrDefault;
                if (inv != null) inv.Invoke(); else item.Click();
            }
        }
        catch (Exception ex) { return $"ERROR: found item {DescribeElement(item)} but selecting it failed: {ex.Message}"; }
        Thread.Sleep(150);
        try { if (expanded && ec!.ExpandCollapseState.Value == ExpandCollapseState.Expanded) ec.Collapse(); } catch { }
        string now = "";
        try { now = ReadText(el); } catch { }
        return sb + $"OK: selected \"{value}\" in {DescribeElement(el)}; control now reads \"{now}\"";
    }

    static readonly ControlType[] ItemTypes = { ControlType.ListItem, ControlType.DataItem, ControlType.MenuItem, ControlType.TreeItem, ControlType.TabItem, ControlType.Text };

    static AutomationElement? FindItem(AutomationElement root, string value)
    {
        try
        {
            var all = root.FindAllDescendants(cf => cf.ByName(value));
            // prefer real item types over plain text labels
            foreach (var t in ItemTypes.Take(5))
                foreach (var e in all) { try { if (e.ControlType == t) return e; } catch { } }
            foreach (var e in all) { try { if (e.Patterns.SelectionItem.PatternOrDefault != null || e.Patterns.Invoke.PatternOrDefault != null) return e; } catch { } }
        }
        catch { }
        return null;
    }

    static AutomationElement? FindItemInSameProcessPopups(AutomationElement win, string value)
    {
        try
        {
            int pid = win.Properties.ProcessId.ValueOrDefault;
            var tops = Automation.GetDesktop().FindAllChildren();
            foreach (var t in tops)
            {
                try { if (t.Properties.ProcessId.ValueOrDefault != pid || t.Equals(win)) continue; } catch { continue; }
                var hit = FindItem(t, value);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    static List<string> ListItemNames(AutomationElement el)
    {
        var names = new List<string>();
        try
        {
            foreach (var e in el.FindAllDescendants())
            {
                try
                {
                    var ct = e.ControlType;
                    if (ct == ControlType.ListItem || ct == ControlType.DataItem || ct == ControlType.MenuItem || ct == ControlType.TreeItem)
                    { var n = e.Name; if (!string.IsNullOrEmpty(n)) names.Add(n); }
                }
                catch { }
                if (names.Count >= 30) break;
            }
        }
        catch { }
        return names;
    }

    // ---------- conditions ----------

    public static (bool ok, string actual) EvaluateCondition(Args a)
    {
        var windowMatch = a.Req("window");
        var q = Query.From(a);
        var target = a.Str("target");
        var ctype = a.Str("control_type");
        var condition = a.Req("condition").Trim().ToLowerInvariant();
        var value = a.Str("value");
        int nth = a.Int("nth") ?? 0;

        var win = FindWindow(windowMatch);
        if (q.IsEmpty)
        {
            bool windowExists = win != null;
            return condition switch
            {
                "exists" => (windowExists, windowExists ? $"window \"{windowMatch}\" exists" : $"window \"{windowMatch}\" not found. Open windows: {ListWindows()}"),
                "not_exists" => (!windowExists, windowExists ? $"window \"{windowMatch}\" still exists" : $"window \"{windowMatch}\" not found (as expected)"),
                _ => (false, $"condition \"{condition}\" needs a target or control_type; only exists/not_exists work against a whole window")
            };
        }

        string label = q.ToString();
        if (win == null)
        {
            if (condition == "not_exists") return (true, $"window \"{windowMatch}\" not found, so target \"{label}\" doesn't exist either");
            return (false, $"window \"{windowMatch}\" not found. Open windows: {ListWindows()}");
        }

        var (scope, scopeErr) = ResolveScope(win, a);
        if (scopeErr != null) return (false, scopeErr);
        var matches = FindMatches(scope!, q);
        var el = nth >= 0 && nth < matches.Count ? matches[nth] : null;
        bool elExists = el != null;

        switch (condition)
        {
            case "exists":
                return (elExists, elExists ? $"found {DescribeElement(el!)}" : $"no element \"{label}\" (nth={nth}) found in \"{windowMatch}\" ({matches.Count} match(es))");
            case "not_exists":
                return (!elExists, elExists ? $"still found {DescribeElement(el!)}" : $"no element \"{label}\" found (as expected)");
            case "enabled":
            case "disabled":
            {
                if (!elExists) return (false, $"no element \"{label}\" found in \"{windowMatch}\"");
                bool isEnabled = true;
                try { isEnabled = el!.Properties.IsEnabled.ValueOrDefault; } catch { }
                bool want = condition == "enabled";
                return (isEnabled == want, $"element is {(isEnabled ? "enabled" : "disabled")}");
            }
            case "text_equals":
            case "text_contains":
            {
                if (!elExists) return (false, $"no element \"{label}\" found in \"{windowMatch}\"");
                string actualText = ReadText(el!);
                bool textOk = condition == "text_equals"
                    ? string.Equals(actualText, value ?? "", StringComparison.Ordinal)
                    : actualText.Contains(value ?? "", StringComparison.OrdinalIgnoreCase);
                return (textOk, $"actual text: \"{actualText}\"");
            }
            default:
                return (false, $"unknown condition \"{condition}\" (expected exists|not_exists|enabled|disabled|text_equals|text_contains)");
        }
    }

    public static ToolResult AssertState(Args a)
    {
        try
        {
            var (ok, actual) = Retry(() => EvaluateCondition(a));
            return $"{(ok ? "PASS" : "FAIL")}: {actual}";
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex) { return $"FAIL: UI Automation error: {ex.Message}"; }
    }

    public static ToolResult WaitFor(Args a)
    {
        int timeoutMs = a.Int("timeout_ms") ?? 10000;
        int pollMs = a.Int("poll_interval_ms") ?? 250;
        a.Req("window"); a.Req("condition");
        var sw = Stopwatch.StartNew();
        (bool ok, string actual) result;
        int transientCount = 0;
        while (true)
        {
            try { result = EvaluateCondition(a); }
            catch (ArgumentException) { throw; }
            catch (Exception ex)
            {
                // Transient UIA errors (e.g. 0x80040201 "An event was unable to invoke any of the subscribers")
                // must not abort the wait: record and poll again until the timeout.
                transientCount++;
                result = (false, $"UI Automation error ({(IsTransient(ex) ? "transient" : "unexpected")}, retrying): {ex.Message}");
                Thread.Sleep(150);
            }
            if (result.ok) return $"OK: condition met after {sw.ElapsedMilliseconds}ms - {result.actual}" + (transientCount > 0 ? $" ({transientCount} transient UIA error(s) retried)" : "");
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(Math.Max(50, Math.Min(pollMs, timeoutMs - (int)sw.ElapsedMilliseconds)));
        }
        return $"TIMEOUT: condition not met after {sw.ElapsedMilliseconds}ms - last observed: {result.actual}";
    }

    // ---------- mouse / keyboard ----------

    static (int x, int y, string? err) ToScreen(Args a, int x, int y)
    {
        var wm = a.Str("window");
        if (string.IsNullOrWhiteSpace(wm)) return (x, y, null);
        var err = FocusWindowOrError(wm);
        if (err != null) return (x, y, err);
        if (a.Bool("relative") ?? false)
        {
            var (win, h, e2) = ResolveWindow(wm);
            if (e2 != null) return (x, y, e2);
            var v = Native.VisibleRect(h);
            return (v.X + x, v.Y + y, null);
        }
        return (x, y, null);
    }

    public static ToolResult MouseClick(Args a)
    {
        var (x, y, err) = ToScreen(a, a.ReqInt("x"), a.ReqInt("y"));
        if (err != null) return err;
        var button = a.Str("button") ?? "left";
        int clicks = Math.Clamp(a.Int("clicks") ?? 1, 1, 3);
        Native.SetCursorPos(x, y);
        Thread.Sleep(30);
        for (int i = 0; i < clicks; i++)
        {
            Native.MouseButton(true, button);
            Thread.Sleep(30);
            Native.MouseButton(false, button);
            Thread.Sleep(40);
        }
        return $"OK: {button} click x{clicks} at screen ({x},{y})";
    }

    public static ToolResult MouseDrag(Args a)
    {
        var (x1, y1, err) = ToScreen(a, a.ReqInt("x1"), a.ReqInt("y1"));
        if (err != null) return err;
        // second point: same window-relative conversion without re-focusing
        int x2 = a.ReqInt("x2"), y2 = a.ReqInt("y2");
        if (!string.IsNullOrWhiteSpace(a.Str("window")) && (a.Bool("relative") ?? false))
        {
            var (_, h, _) = ResolveWindow(a.Str("window")!);
            var v = Native.VisibleRect(h);
            x2 += v.X; y2 += v.Y;
        }
        var button = a.Str("button") ?? "left";
        Native.SetCursorPos(x1, y1);
        Thread.Sleep(30);
        Native.MouseButton(true, button);
        Thread.Sleep(30);
        const int steps = 24;
        for (int i = 1; i <= steps; i++)
        {
            Native.MouseMoveAbsolute(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            Thread.Sleep(10);
        }
        Native.MouseMoveAbsolute(x2, y2);
        Thread.Sleep(30);
        Native.MouseButton(false, button);
        return $"OK: {button} drag from ({x1},{y1}) to ({x2},{y2})";
    }

    public static ToolResult KeyPress(Args a)
    {
        var keys = a.Req("keys");
        var err = FocusWindowOrError(a.Str("window"));
        if (err != null) return err;
        var sendKeysStr = TranslateKeysToSendKeys(keys);
        try
        {
            SendKeys.SendWait(sendKeysStr);
            var fg = Native.GetForegroundWindow();
            return $"OK: sent \"{keys}\" (SendKeys=\"{sendKeysStr}\") to foreground window \"{Native.TitleOf(fg)}\"";
        }
        catch (Exception ex) { return $"ERROR: could not send keys \"{keys}\": {ex.Message}"; }
    }

    static string TranslateKeysToSendKeys(string keys)
    {
        var namedKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENTER"] = "{ENTER}", ["RETURN"] = "{ENTER}", ["TAB"] = "{TAB}",
            ["ESC"] = "{ESC}", ["ESCAPE"] = "{ESC}",
            ["BACKSPACE"] = "{BACKSPACE}", ["DELETE"] = "{DELETE}", ["DEL"] = "{DELETE}",
            ["UP"] = "{UP}", ["DOWN"] = "{DOWN}", ["LEFT"] = "{LEFT}", ["RIGHT"] = "{RIGHT}",
            ["HOME"] = "{HOME}", ["END"] = "{END}", ["PAGEUP"] = "{PGUP}", ["PAGEDOWN"] = "{PGDN}",
            ["SPACE"] = " ", ["INSERT"] = "{INSERT}",
        };
        for (int i = 1; i <= 12; i++) namedKeys[$"F{i}"] = $"{{F{i}}}";

        var modifiers = new StringBuilder();
        string mainKey = "";
        var parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            var up = p.ToUpperInvariant();
            if (up == "CTRL" || up == "CONTROL") modifiers.Append('^');
            else if (up == "ALT") modifiers.Append('%');
            else if (up == "SHIFT") modifiers.Append('+');
            else if (namedKeys.TryGetValue(up, out var mapped)) mainKey = mapped;
            else if (p.Length == 1)
            {
                char c = p[0];
                mainKey = char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c).ToString() : "{" + c + "}";
            }
            else mainKey = p;
        }
        return modifiers.ToString() + mainKey;
    }

    public static ToolResult TypeTextRaw(Args a)
    {
        var text = a.Str("text") ?? "";
        int delay = a.Int("delay_ms") ?? 0;
        var err = FocusWindowOrError(a.Str("window"));
        if (err != null) return err;
        if (delay <= 0) Keyboard.Type(text);
        else foreach (var ch in text) { Keyboard.Type(ch); Thread.Sleep(delay); }
        var fg = Native.GetForegroundWindow();
        return $"OK: typed {text.Length} chars{(delay > 0 ? $" ({delay}ms apart)" : "")} at current focus (foreground window \"{Native.TitleOf(fg)}\")";
    }
}
