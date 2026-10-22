using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace WinAutomationMcp;

static class Tools
{
    public static readonly Dictionary<string, ToolDef> All = new();
    static readonly List<string> Order = new();
    static readonly HashSet<string> SelTools = new() { "find_elements", "click_element", "type_text", "get_element_text", "clear_field", "select_option", "set_value", "assert_state", "wait_for" };
    static readonly HashSet<string> StrictTools = new() { "click_element", "type_text", "get_element_text", "clear_field", "select_option", "set_value" };

    static void Add(string name, string desc, P[] ps, Func<Args, ToolResult> run, bool gate = true, bool settle = false, bool idem = false)
    {
        if (SelTools.Contains(name)) ps = ps.Concat(Sel).Concat(StrictTools.Contains(name) ? new[] { Strict } : new P[0]).ToArray();
        if (name == "describe_ui") ps = ps.Concat(new[] { Parent, ParentCt, ParentNth }).ToArray();
        All[name] = new ToolDef { Name = name, Description = desc, Params = ps, Run = run, NeedsGate = gate, Settle = settle, Idempotent = idem };
        Order.Add(name);
    }

    static P W(string desc = "Window title or process name substring to match", bool req = true) => new("window", "string", desc, req);
    static readonly P Target = new("target", "string", "AutomationId or Name of the element (exact match unless match=contains). Optional when control_type, automation_id, name or name_regex is given.");
    static readonly P CType = new("control_type", "string", "Only consider elements of this UIA control type (Button, Edit, Text, ComboBox, ListItem, DataItem, CheckBox, MenuItem, Pane, Custom, ...). Solves the 'Login title vs Login button' ambiguity.");
    static readonly P Nth = new("nth", "integer", "0-based index among the matching elements (document order, AutomationId matches first). Default 0. See find_elements for the indexes.");
    static readonly P Match = new("match", "string", "exact (default) or contains (substring, case-insensitive) matching of target against AutomationId/Name", false, new[] { "exact", "contains" });
    static readonly P AutoId = new("automation_id", "string", "Exact AutomationId (case-sensitive). Combines (AND) with the other selectors.");
    static readonly P NameExact = new("name", "string", "Exact element Name (case-sensitive). Combines (AND) with the other selectors.");
    static readonly P NameRx = new("name_regex", "string", "Regular expression matched (case-insensitive, unanchored) against the element Name, e.g. \"^Log ?in$\" or \"Order \\d+\".");
    static readonly P Parent = new("parent", "string", "Optional scope: AutomationId or Name of a container element; the search runs only under it (see parent_control_type, parent_nth).");
    static readonly P ParentCt = new("parent_control_type", "string", "Control type of the parent container (narrows `parent`, or selects it alone).");
    static readonly P ParentNth = new("parent_nth", "integer", "0-based index among matching parents (default 0).");
    static readonly P Strict = new("strict", "boolean", "true = if several elements match and nth is not given, return an ERROR listing the candidates (with their nth) instead of silently using the first one. Default false (true inside run_steps).");
    static readonly P[] Sel = { AutoId, NameExact, NameRx, Parent, ParentCt, ParentNth };
    static readonly P Win4Input = new("window", "string", "Optional: window title/process substring that is focused first. If it cannot be focused the input is NOT sent (prevents keystrokes going to the wrong window).");

    public static void Register()
    {
        if (All.Count > 0) return;

        Add("list_windows", "List top-level open windows (title, process name, hwnd, state, visible rect).", new P[0], a => Uia.ListWindows(), settle: false, idem: true);
        Add("get_element_tree", "Dump the UI Automation element tree (AutomationId, Name, ControlType, bounding rect) for a window, as compact text.",
            new[] { W(), new P("max_depth", "integer", "Max tree depth (default 3)"), new P("max_elements", "integer", "Max elements to list before truncating (default 200)") },
            Uia.GetElementTree, idem: true);
        Add("find_elements", "List candidate elements (nth index, control type, AutomationId, Name, rect, enabled, offscreen, supported UIA patterns) matching target/control_type, so you can pick the right one before click_element. With neither target nor control_type it lists all descendants (up to max_results).",
            new[] { W(), Target, CType, Match, new P("max_results", "integer", "Max rows to print (default 50)") }, Uia.FindElements, idem: true);
        Add("click_element", "Click an element in a window, found by AutomationId or Name (optionally narrowed by control_type and nth). Precise, deterministic; prefer this over mouse_click whenever the app exposes automation elements. Warns (and uses Invoke/SelectionItem when possible) if the element has an empty or offscreen rect.",
            new[] { W(), Target, CType, Nth, Match, new P("button", "string", "left (default) | right", false, new[] { "left", "right" }), new P("double", "boolean", "Double click (default false)") },
            Uia.ClickElement, settle: true);
        Add("type_text", "Set text into a text-like element (AutomationId or Name, optionally control_type/nth). Uses ValuePattern when the element is writable, otherwise focuses it and sends real keystrokes.",
            new[] { W(), Target, CType, Nth, Match, new P("text", "string", "Text to type", true) }, Uia.TypeText, settle: true);
        Add("get_element_text", "Read the current text/value of an element (ValuePattern, then TextPattern, then Name, then the Name of child Text elements).",
            new[] { W(), Target, CType, Nth, Match }, Uia.GetElementText, idem: true);
        Add("clear_field", "Clear a text field. method=auto tries ValuePattern, then CTRL+A+DELETE, then END+BACKSPACE one by one (for custom controls that lose characters). Without target it clears whatever control has focus inside the window.",
            new[] { W(), Target, CType, Nth, Match, new P("method", "string", "auto (default) | value | keys | backspace", false, new[] { "auto", "value", "keys", "backspace" }), new P("delay_ms", "integer", "Delay between key events, default 20"), new P("max_chars", "integer", "Backspace count when the current length is unknown (default 50)") },
            Uia.ClearField, settle: true);
        Add("select_option", "Select an item of a combo box / list by visible text using UIA ExpandCollapse + SelectionItem (or a writable ValuePattern). Returns a clear error listing the exposed patterns/items when the control does not support it (custom WinUI/Syncfusion combos often do not expose their popup).",
            new[] { W(), Target, CType, Nth, Match, new P("value", "string", "Text of the option to select", true) }, Uia.SelectOption, settle: true);

        Add("set_value", "Set the text of a field robustly: uses the UIA ValuePattern when writable (no keystrokes, nothing can be dropped), verifies by reading it back, and otherwise falls back to focus + clear + typing one key at a time (delay_ms) with read-back. Use it for password boxes that lose characters (a password cannot be read back, the result says so).",
            new[] { W(), Target, CType, Nth, Match, new P("text", "string", "Text to set", true), new P("method", "string", "auto (default) | value | keys", false, new[] { "auto", "value", "keys" }), new P("delay_ms", "integer", "Pause between keys on the keyboard path (default 40)"), new P("verify", "boolean", "Read the value back and compare (default true; impossible for password fields)") },
            Uia.SetValue, settle: true);
        Add("describe_ui", "Token-cheap flat listing of the interactive elements of a window: type, name, id, window-relative rect and click point, enabled state and a ready-to-use selector (`sel:`) for click_element/type_text. Far smaller than get_element_tree. Default types=interactive (Button, Edit, ComboBox, CheckBox, ListItem, MenuItem, TabItem, ...); types=all, or a comma list such as Edit,Button. Skips offscreen/empty-rect elements unless include_offscreen=true. Unnamed controls get a `control_type=X nth=N` selector.",
            new[] { W(), new P("max_elements", "integer", "Max rows (default 80, max 1000)"), new P("types", "string", "interactive (default) | all | comma list of control types"), new P("include_text", "boolean", "Also list Text elements (labels), default false"), new P("include_offscreen", "boolean", "Also list offscreen / zero-size elements, default false") },
            Uia.DescribeUi, idem: true);
        Add("screenshot", "Capture a window (or the whole virtual screen when no window is given). Returns the picture as an MCP image content block (not base64 text). Defaults: downscaled to max_width=1280, png. " +
            "region_x/y/w/h are in pixels relative to the top-left of the WINDOW's visible frame when `window` is given, otherwise relative to the top-left of the virtual screen (equal to absolute screen coordinates on a single monitor); they are applied before downscaling. " +
            "The text part reports the screen position of the image and the scale so pixel coordinates can be mapped back to screen coordinates. " +
            "include_popups (default true) grabs the screen rectangle (window plus same-process dropdowns/popups next to it) instead of PrintWindow, so combo lists and menus appear; the window is brought to the front first unless bring_to_front=false. " +
            "SECURITY: the screenshot shows exactly what is on screen, including revealed passwords.",
            new[] {
                W("Window title or process name substring to match. Omit to capture the full virtual screen.", false),
                new P("region_x", "integer", "Crop left offset (window-relative if window given, else virtual-screen-relative)"),
                new P("region_y", "integer", "Crop top offset"), new P("region_w", "integer", "Crop width"), new P("region_h", "integer", "Crop height"),
                new P("max_width", "integer", "Downscale the returned image to at most this width in px (default 1280, 0 = no downscale)"),
                new P("format", "string", "png (default) | jpeg", false, new[] { "png", "jpeg" }),
                new P("quality", "integer", "JPEG quality 1-100 (default 80)"),
                new P("save_to", "string", "Also save the full-resolution (cropped, not downscaled) image to this path ON THE VM (format from extension .png/.jpg). Fetch with scp."),
                new P("return_image", "boolean", "false = only save to save_to, return no image (default true)"),
                new P("include_popups", "boolean", "Include dropdowns/popups of the same process (default true; uses screen capture)"),
                new P("bring_to_front", "boolean", "Bring/restore the window to the foreground before a screen capture (default true)"),
                new P("method", "string", "auto (default) | screen | printwindow. printwindow works for occluded windows but never shows popups.", false, new[] { "auto", "screen", "printwindow" }),
                new P("as_text", "boolean", "Legacy mode: return base64 text instead of an image block (default false)")
            }, Capture.Screenshot);
        Add("mouse_click", "Move the real mouse cursor and click. Coordinates are absolute screen pixels unless relative=true with a window, in which case they are relative to the window's visible top-left. If window is given it is focused first (error and no click if it cannot be focused).",
            new[] { new P("x", "integer", "X (absolute screen, or window-relative when relative=true)", true), new P("y", "integer", "Y", true), new P("button", "string", "left | right | middle (default left)"), Win4Input, new P("relative", "boolean", "Interpret x,y relative to the window (needs window)"), new P("clicks", "integer", "1 (default), 2 = double click") },
            Uia.MouseClick, settle: true);
        Add("click_at", "Click at window-relative coordinates (relative to the window's visible top-left, as reported by get_window_rect / describe_ui click=), so it survives the window moving. Same as mouse_click with relative=true when window is given.",
            new[] { new P("x", "integer", "X relative to the window (absolute screen when no window)", true), new P("y", "integer", "Y", true), W("Window title or process name substring (focused first)", true), new P("button", "string", "left | right | middle (default left)"), new P("clicks", "integer", "1 (default), 2 = double click") },
            Uia.ClickAt, settle: true);
        Add("mouse_drag", "Press the mouse button at (x1,y1), drag to (x2,y2), release, using real SendInput moves. Optional window/relative as in mouse_click.",
            new[] { new P("x1", "integer", "Start X", true), new P("y1", "integer", "Start Y", true), new P("x2", "integer", "End X", true), new P("y2", "integer", "End Y", true), new P("button", "string", "left | right | middle (default left)"), Win4Input, new P("relative", "boolean", "Coordinates relative to the window (needs window)") },
            Uia.MouseDrag, settle: true);
        Add("key_press", "Send a key or key combination, e.g. \"ENTER\", \"CTRL+A\", \"ALT+F4\". Goes to the foreground window; pass `window` to focus a window first (and fail without sending if it cannot be focused).",
            new[] { new P("keys", "string", "Key or +-separated combo (named keys: ENTER, TAB, ESC, BACKSPACE, DELETE, UP/DOWN/LEFT/RIGHT, HOME, END, PAGEUP, PAGEDOWN, SPACE, F1-F12; modifiers: CTRL, ALT, SHIFT)", true), Win4Input },
            Uia.KeyPress, settle: true);
        Add("type_text_raw", "Type literal text wherever the keyboard focus is. Pass `window` to focus a window first; delay_ms adds a pause between keystrokes for controls that drop characters.",
            new[] { new P("text", "string", "Text to type", true), Win4Input, new P("delay_ms", "integer", "Pause between keystrokes in ms (default 0)") },
            Uia.TypeTextRaw, settle: true);

        Add("focus_window", "Bring a window to the foreground (restoring it if minimized) and report its state.", new[] { W() }, Uia.FocusWindow, settle: true);
        Add("get_window_rect", "Return a window's handle, state (normal/minimized/maximized), visible rect (the origin used by screenshot region_* and recordings) and outer rect.", new[] { W() }, Uia.GetWindowRect, idem: true);
        Add("move_window", "Move a window (outer rect). Optionally also resize it by passing width and height. Restores it first if maximized/minimized.",
            new[] { W(), new P("x", "integer", "New left", true), new P("y", "integer", "New top", true), new P("width", "integer", "Optional new width"), new P("height", "integer", "Optional new height") }, Uia.MoveWindow, settle: true);
        Add("resize_window", "Resize a window (outer rect) keeping its position.", new[] { W(), new P("width", "integer", "New width", true), new P("height", "integer", "New height", true) }, Uia.ResizeWindow, settle: true);
        Add("set_window_state", "Maximize, minimize or restore a window.", new[] { W(), new P("state", "string", "maximize | minimize | restore", true, new[] { "maximize", "minimize", "restore" }) }, Uia.SetWindowState, settle: true);

        var waitParams = new[] {
            new P("wait_ms", "integer", "If > 0, wait up to this many ms for a NEW visible top-level window of the app and return its rect (one call for slow startups). Default 0 for launch_app, 60000 for launch_packaged_app."),
            new P("window", "string", "Window title or process name substring identifying the app's window while waiting (needed when the launched process is a stub that starts another one)"),
            new P("title_regex", "string", "Alternative: regular expression for the window title"),
            new P("allow_existing", "boolean", "Accept a window that was already open before the launch (single-instance apps). Default false: only new windows count."),
            new P("poll_interval_ms", "integer", "Polling interval while waiting (default 500)") };
        Add("launch_app", "Start an application by path (or shell-resolvable command). Runs in this server's interactive session, so the app appears on screen. With wait_ms it also waits for the app's window and returns its rect (wait_for_window built in).",
            new[] { new P("path", "string", "Path to the executable (or a shell-resolvable command)", true), new P("args", "string", "Command-line arguments"), new P("cwd", "string", "Working directory") }.Concat(waitParams).ToArray(), SystemTools.LaunchApp, gate: false, settle: true);
        Add("launch_packaged_app", "Start a packaged (MSIX/AppX/UWP/WinUI) app by AUMID (PackageFamilyName!AppId, see list_packaged_apps) and wait for its window (default 60 s) in ONE call. The window is matched by the package install path, pid-less, plus optional window/title_regex. Returns the window rect.",
            new[] { new P("aumid", "string", "Application User Model ID, e.g. Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true) }.Concat(waitParams).ToArray(), SystemTools.LaunchPackagedApp, gate: false, settle: true);
        Add("list_packaged_apps", "List Start-menu apps with their AUMID (Get-StartApps), optionally filtered by a substring of the name or AUMID. Use it to find the aumid for launch_packaged_app.",
            new[] { new P("filter", "string", "Substring of the app name or AUMID") }, SystemTools.ListPackagedApps, gate: false);
        Add("wait_for_window", "Poll until a visible top-level window matching window (title/process substring), title_regex, pid or aumid exists, up to timeout_ms (default 60000), then return its handle and rect. Does not block other sessions while polling.",
            new[] { new P("window", "string", "Title or process name substring"), new P("title_regex", "string", "Regular expression for the title"), new P("pid", "integer", "Process id"), new P("aumid", "string", "Match windows of this packaged app"),
                    new P("timeout_ms", "integer", "Max wait (default 60000)"), new P("poll_interval_ms", "integer", "Default 500"), new P("allow_existing", "boolean", "Default true; false = only windows that appear after this call starts") },
            Uia.WaitForWindow, gate: false);
        Add("open_url", "Open a URL in the default web browser.", new[] { new P("url", "string", "http(s) URL to open", true) }, SystemTools.OpenUrl, gate: false, settle: true);

        var condParams = new[] { W(), Target, CType, Nth, Match,
            new P("condition", "string", "exists | not_exists | enabled | disabled | text_equals | text_contains", true),
            new P("value", "string", "Expected text, required for text_equals/text_contains") };
        Add("assert_state", "Check one condition right now (no waiting) and report PASS/FAIL plus the actual value found. Omit target and control_type to check the window itself (only exists/not_exists).", condParams, Uia.AssertState, idem: true);
        Add("wait_for", "Poll a condition until it is true or a timeout elapses. Transient UI Automation errors (such as 0x80040201) are retried instead of aborting the wait.",
            condParams.Concat(new[] { new P("timeout_ms", "integer", "Max time to wait in ms (default 10000)"), new P("poll_interval_ms", "integer", "Time between checks in ms (default 250)") }).ToArray(), Uia.WaitFor);

        // ---- recording ----
        Add("start_recording", "Start a screen recording (ffmpeg gdigrab, H.264 mp4) of a window (fixed rectangle at start, including its popups) or the whole virtual screen. Returns immediately; call stop_recording to finish. Needs ffmpeg in <server>\\tools. max_seconds is a safety cap after which ffmpeg stops by itself and the file is still valid.",
            new[] { W("Optional: record this window's rectangle (visible frame + nearby popups). Omit for the full virtual screen.", false),
                    new P("fps", "integer", "Frames per second (default 15, max 60)"), new P("max_seconds", "integer", "Safety cap in seconds (default 300, max 3600)"),
                    new P("output", "string", "Output .mp4 path on the VM (default C:\\Users\\<user>\\recordings\\rec-<timestamp>.mp4)"),
                    new P("caption", "string", "Optional on-screen caption text drawn at the bottom; pass an empty string to enable the overlay now and set text later with set_recording_caption"),
                    new P("max_width", "integer", "Downscale the video to at most this width (default: no downscale)"),
                    new P("region_x", "integer", "Optional sub-area, relative to the window (or virtual screen)"), new P("region_y", "integer", "Sub-area top"), new P("region_w", "integer", "Sub-area width"), new P("region_h", "integer", "Sub-area height"),
                    new P("bring_to_front", "boolean", "Bring the window to the foreground first (default true)"), new P("overwrite", "boolean", "Allow overwriting an existing output file (default false)") },
            Recorder.Start, gate: false);
        Add("stop_recording", "Stop a recording and finalize the mp4. Returns the file path, size, duration and codec (verified with ffprobe). Fetch the file to the Mac with scp.",
            new[] { new P("id", "string", "Recording id from start_recording (optional when only one is active)") }, Recorder.Stop, gate: false);
        Add("set_recording_caption", "Change the caption overlay text of a running recording (it must have been started with the caption parameter).",
            new[] { new P("text", "string", "New caption text (empty hides it)", true), new P("id", "string", "Recording id (optional)") }, Recorder.SetCaption, gate: false);
        Add("recording_status", "List active recordings and whether ffmpeg is available.", new P[0], Recorder.Status, gate: false);

        // ---- interactive-session commands ----
        Add("run_powershell", "Run a PowerShell (Windows PowerShell 5.1) script IN THE INTERACTIVE DESKTOP SESSION of this server and return exit code, stdout and stderr. Unlike ssh, this can do things that need the interactive token, e.g. Add-AppxPackage -Register. Killed (process tree) after timeout_ms. " +
            "SECURITY: this is arbitrary code execution as the logged-on user for anyone who can reach the server port (127.0.0.1 only; reachable through the SSH tunnel). Do not expose the port.",
            new[] { new P("command", "string", "PowerShell script text", true), new P("timeout_ms", "integer", "Timeout in ms (default 60000, max 1800000)"), new P("cwd", "string", "Working directory"), new P("max_output_chars", "integer", "Cap per stream (default 20000)") },
            SystemTools.RunPowerShell, gate: false);
        Add("run_cmd", "Run a cmd.exe command line in the interactive desktop session and return exit code, stdout and stderr (same security notes as run_powershell).",
            new[] { new P("command", "string", "cmd.exe command line", true), new P("timeout_ms", "integer", "Timeout in ms (default 60000)"), new P("cwd", "string", "Working directory"), new P("max_output_chars", "integer", "Cap per stream (default 20000)") },
            SystemTools.RunCmd, gate: false);

        // ---- files / processes / clipboard ----
        Add("read_file", "Read a text file on the VM (size capped). Use tail=true for the end of a log, or offset for paging.",
            new[] { new P("path", "string", "File path", true), new P("max_bytes", "integer", "Max bytes to return (default 100000, max 5000000)"), new P("offset", "integer", "Start byte offset"), new P("tail", "boolean", "Return the last max_bytes instead") }, SystemTools.ReadFile, gate: false);
        Add("write_file", "Write (or append) UTF-8 text to a file on the VM, creating folders as needed.",
            new[] { new P("path", "string", "File path", true), new P("content", "string", "Text content", true), new P("append", "boolean", "Append instead of overwrite"), new P("create_dirs", "boolean", "Create missing folders (default true)") }, SystemTools.WriteFile, gate: false);
        Add("list_processes", "List processes with pid, name, session, start time, executable path and main window title.",
            new[] { new P("name_filter", "string", "Substring of the process name"), new P("only_with_windows", "boolean", "Only processes with a main window title"), new P("max_results", "integer", "Default 300") }, SystemTools.ListProcesses, gate: false);
        Add("kill_process", "Kill ONE process. Safety: requires the exact pid AND the expected full executable path; refuses if the actual path differs, cannot be read, or the pid is the server itself. No kill by name.",
            new[] { new P("pid", "integer", "Exact process id", true), new P("expected_path", "string", "Full path of the executable that this pid must be running", true), new P("tree", "boolean", "Also kill child processes (default false)") }, SystemTools.KillProcess, gate: false);
        Add("clipboard_get", "Read the clipboard text. SECURITY: may contain secrets.", new P[0], SystemTools.ClipboardGet, gate: false);
        Add("clipboard_set", "Set the clipboard text (useful to paste secrets with key_press CTRL+V instead of typing them).", new[] { new P("text", "string", "Text", true) }, SystemTools.ClipboardSet, gate: false);
        Add("run_steps", "Run a batch of steps in ONE call (like an actions[] list) and stop at the first failure. `steps` is a JSON array of objects: {\"action\": <any tool name>, ...that tool's parameters}. Extra step keys: timeout_ms (per-step cap, default default_timeout_ms), optional (true = failure does not stop the batch), retries + retry_delay_ms, label. Extra pseudo action: {\"action\":\"sleep\",\"ms\":500}. Typical actions: launch_app, launch_packaged_app, wait_for_window, focus_window, click_element, click_at, set_value, type_text, type_text_raw, key_press, wait_for, assert_state, screenshot, set_recording_caption, run_powershell. A step fails on an error, a FAIL/TIMEOUT result (assert_state, wait_for) or a non-zero exit code of run_powershell/run_cmd (allow_nonzero:true to accept). The result lists every step with timing and output, and on failure the step number, the full error, a compact listing of the window's current UI and (default) a screenshot. Example: [{\"action\":\"click_element\",\"target\":\"Login\",\"control_type\":\"Button\"},{\"action\":\"wait_for\",\"target\":\"Orders\",\"condition\":\"exists\",\"timeout_ms\":15000},{\"action\":\"screenshot\",\"max_width\":800}]",
            new[] { new P("steps", "array", "JSON array of step objects (each has \"action\" plus that tool's parameters)", true),
                    new P("window", "string", "Default `window` for every step that does not set its own"),
                    new P("stop_on_error", "boolean", "Stop at the first failing step (default true)"),
                    new P("screenshot_on_error", "boolean", "Attach a screenshot when a step fails (default true)"),
                    new P("strict", "boolean", "Element steps return an ERROR listing candidates instead of guessing when several elements match and no nth is given (default true)"),
                    new P("default_timeout_ms", "integer", "Per-step hard timeout when a step has no timeout_ms (default 30000). wait_for/run_powershell/launch use their own timeout plus 10 s."),
                    new P("verbose", "boolean", "Show the full output of every step instead of its first line (default false)") },
            Steps.Run, gate: false);
        Add("server_info", "Server version, pid, port, interactive session id, log file and ffmpeg location. Use it to verify which build you are talking to.", new P[0], a => Program.ServerInfo(), gate: false);
    }

    public static JsonArray List()
    {
        var arr = new JsonArray();
        foreach (var n in Order) arr.Add(All[n].ToJson());
        return arr;
    }
}
