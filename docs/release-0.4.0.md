# win-automation-mcp 0.4.0

Windows UI automation MCP server (C#, net8.0-windows, FlaUI/UIA3). It listens on 127.0.0.1 only, line-delimited JSON-RPC over TCP, and must run inside the interactive desktop session (not over plain ssh, which is session 0).

0.4.0 adds batching (`run_steps`), sharper element targeting, launch helpers that wait for the window, and a compact UI listing (`describe_ui`). Everything from 0.3.0 keeps working. 42 tools in total (`server_info` lists them).

The production server on port 8765 is untouched until you deploy (see "Deploy 0.4.0"). This build was tested as a separate instance on port 8766 from `C:\Users\Eduardo\WinAutomationMcp_dev`.

## What is new in 0.4.0

| Area | Tool / change |
|---|---|
| Batching | `run_steps`: run a JSON array of tool calls in one MCP call, stop at the first failure, per-step result and timing, last UI state and an on_error screenshot on failure. |
| Targeting | `automation_id`, `name`, `name_regex`, `parent` (+ `parent_control_type`, `parent_nth`) on `find_elements`, `click_element`, `type_text`, `get_element_text`, `clear_field`, `select_option`, `set_value`, `assert_state`, `wait_for`. `strict=true` returns an error that lists the candidates (with their `nth`) instead of guessing. |
| Values | `set_value`: ValuePattern first (nothing can be dropped), read-back verification, otherwise slow key-by-key typing with verification. Custom password boxes that expose bullets are verified by length. |
| Window-relative | `click_at` (window-relative click, survives the window moving). `describe_ui` prints a window-relative `click=(x,y)` for every element. `get_window_rect` from 0.3.0 gives the origin. |
| Launch | `launch_app` and `launch_packaged_app` (AUMID) take `wait_ms` and wait for the new window in the same call (returns hwnd and rect). `wait_for_window` polls on its own. `list_packaged_apps` finds the AUMID. |
| Compact UI | `describe_ui`: flat listing of interactive elements (type, name, id, window-relative rect, click point, enabled, ready-to-use selector) with `max_elements`. |
| Robustness | TCP keepalive on every connection, `server_info` lists version and all tools, unknown tool names suggest near matches, read-only tools re-run on a transient UIA error (0x80040201), `wait_for` never aborts on it (0.3.0), `resources/list` and `prompts/list` answered, every tool call logged with duration (SLOW flag above 10 s). |

### run_steps

```json
{"window": "TruScript", "steps": [
  {"action": "launch_packaged_app", "aumid": "01d055ef-a6b2-4a57-b9dc-54732a6f5091_tz0f82bc539pg!App", "wait_ms": 90000},
  {"action": "wait_for", "automation_id": "PasswordTextBox", "condition": "exists", "timeout_ms": 20000},
  {"action": "set_value", "control_type": "Edit", "nth": 0, "text": "someuser"},
  {"action": "set_value", "automation_id": "PasswordTextBox", "text": "..."},
  {"action": "click_element", "target": "Login", "control_type": "Button"},
  {"action": "wait_for", "target": "Orders", "condition": "exists", "timeout_ms": 30000},
  {"action": "screenshot", "max_width": 800}
]}
```

- `action` is any tool name (not `run_steps` itself) and the other keys are that tool's parameters. `sleep` is an extra pseudo action: `{"action":"sleep","ms":500}`.
- Top-level options: `window` (default for every step), `stop_on_error` (default true), `screenshot_on_error` (default true), `strict` (default true: element steps refuse to guess between several matches), `default_timeout_ms` (30000, hard cap per step; `wait_for`, `run_powershell`, `run_cmd` and `launch_*` use their own timeout plus 10 s), `verbose`.
- Per-step options: `timeout_ms`, `optional` (failure does not stop the batch), `retries` + `retry_delay_ms`, `label`, `allow_nonzero` (for `run_powershell`/`run_cmd`).
- A step fails on an error, on a `FAIL`/`TIMEOUT` result (`assert_state`, `wait_for`) or on a non-zero exit code of `run_powershell`/`run_cmd`.
- The result lists each step with timing and output (listing tools such as `describe_ui` are shown in full, other tools show their first line unless `verbose`). On failure it adds the full error, `describe_ui` of the step's window (or `list_windows` when there is no window) and a JPEG screenshot. Images from `screenshot` steps are attached to the result.
- Typed text, passwords and commands are never echoed in the step summary (only their length) and the server log records argument names only.
- Known limit: a step that exceeds its hard timeout is reported as failed, but the underlying call cannot be aborted and may still finish on the VM.

### Element targeting

All selectors combine with AND: `target` (AutomationId or Name, as before), `automation_id` (exact), `name` (exact), `name_regex` (case-insensitive, unanchored), `control_type`, `match=contains`. `parent` selects a container (AutomationId or Name) that scopes the search, `parent_control_type`/`parent_nth` narrow it. `nth` picks among the matches (document order). Use `describe_ui` or `find_elements` to see candidates.

```
click_element window=TruScript target=Login control_type=Button
click_element window=Notepad name_regex="^(File|Edit|View)$" nth=1 strict=true
find_elements window=App control_type=Edit parent=SettingsPanel
```

### describe_ui

`describe_ui window=TruScript` prints one line per interactive element, for example:

```
Edit "" id="PasswordTextBox" rel=(93,266,500x32) click=(343,282) sel: automation_id=PasswordTextBox
Button "Login" id="" rel=(93,337,110x50) click=(148,362) sel: target=Login control_type=Button
Edit "" id="" rel=(93,204,502x32) click=(344,220) sel: control_type=Edit nth=0
```

`sel:` is a selector that resolves to exactly that element (unnamed controls get `control_type=X nth=N`), `click=` is the window-relative centre for `click_at`. Options: `types` (`interactive` default, `all`, or a comma list), `include_text`, `include_offscreen`, `max_elements`, `parent`.

### Launching and the 60 s startup

`launch_packaged_app aumid=... wait_ms=90000` returns as soon as a new visible top-level window of that package appears (matched by the package install folder, also for loose or dev-registered packages, plus optional `window`/`title_regex`). `launch_app path=... wait_ms=30000 window=Calculator` does the same for executables (use `window` when the launched process is only a stub). Only windows that were not open before the launch count, unless `allow_existing=true` (single-instance apps). The window can appear before its UI tree is ready, so follow with a `wait_for` on a known element.

### Security notes added in 0.4.0

- `describe_ui`, `get_element_text` and screenshots show what the app shows. A revealed password box (eye button) or an app that auto-fills saved credentials ends up in the output. Keep such runs out of shared transcripts.
- `set_value` on a password box never echoes the text; for custom boxes it can only verify the length.

## Features added in 0.3.0 (still present)

| Area | Change |
|---|---|
| `screenshot` | Returns an MCP `image` content block (not base64 text) plus a text part with size, method and the screen origin/scale. New params: `save_to` (full-res file on the VM), `max_width` (default 1280, 0 = off), `format` png/jpeg, `quality`, `include_popups` (default true), `bring_to_front` (default true), `method` auto/screen/printwindow, `return_image`, `as_text` (legacy base64 text output). `region_*` is now explicit: relative to the window's visible frame when `window` is given, otherwise relative to the virtual screen top-left; applied before downscaling. |
| Popups | `include_popups` uses a screen grab of the window rect plus same-process popup windows (combo lists, menus) near it, instead of PrintWindow. Verified with a ComboBox dropdown. |
| Recording | New `start_recording`, `stop_recording`, `set_recording_caption`, `recording_status`. ffmpeg gdigrab to H.264 mp4. |
| Commands | New `run_powershell`, `run_cmd`: run in the interactive session, return exit code, stdout, stderr, timeout kills the process tree. |
| Elements | `click_element`, `type_text`, `get_element_text`, `assert_state`, `wait_for` accept `control_type`, `nth`, `match` (exact/contains). New `find_elements`, `select_option`, `clear_field`. Click on an element with an empty or offscreen rect now warns (and uses Invoke/SelectionItem if available). A note is returned when several elements matched and `nth` was not given. |
| Focus/windows | New `focus_window`, `get_window_rect`, `move_window`, `resize_window`, `set_window_state`. `key_press`, `type_text_raw`, `mouse_click`, `mouse_drag` accept `window` (focused first; if focus fails nothing is sent). `mouse_click`/`mouse_drag` accept `relative` (window-relative coordinates); `mouse_click` accepts `clicks`. `type_text_raw`/`clear_field` accept `delay_ms`. Every action tool accepts `settle_ms`. |
| Files/processes | New `read_file`, `write_file`, `list_processes`, `kill_process` (needs exact pid AND expected full exe path, refuses on mismatch), `clipboard_get`, `clipboard_set`, `server_info`. |
| Reliability | Transient UIA errors (0x80040201) are retried inside UIA calls and never abort `wait_for`. Log file with connection open/close reasons. Port via `--port` or `WIN_MCP_PORT`. Per-monitor DPI awareness. `ping` supported. Multiple simultaneous connections still work (one thread per connection, UIA calls serialized). |

`list_windows` lines now also carry `hwnd`, `state`, `rect` (old fields unchanged, extra fields appended). All other 0.2.0 tools and parameters keep working. The one deliberate behavior change: `screenshot` no longer returns base64 text by default; pass `as_text=true` for the old style.

## Files

- `Program.cs` server, JSON-RPC, logging, tool dispatch. `Tools.cs` tool list and JSON schemas. `Uia.cs` + `UiaExtra.cs` UIA tools (targeting, describe_ui, set_value, window waits). `Steps.cs` run_steps. `Capture.cs` screenshot. `Recorder.cs` recording. `SystemTools.cs` commands/files/processes/clipboard. `Win32.cs` P/Invoke. `Infra.cs` helpers.
- `tools\ffmpeg.exe`, `tools\ffprobe.exe`: BtbN FFmpeg master win-arm64 GPL build (gdigrab, libx264). No libvpx, so mp4 only.
- `install-task.ps1` registers or removes a logon Scheduled Task in the interactive session.
- `win-mcp-proxy.py` (Mac side) reconnecting stdio bridge.
- `test\` test client (`mcp_client.py`, `t_ui.py`) and a tiny WinForms test app (`test\TestApp`).
- `_orig\Program.v0.2.0.cs.txt` the previous source for reference.

## Build

```powershell
cd C:\Users\Eduardo\WinAutomationMcp_040src
dotnet build -c Release -nologo
# output: bin\Release\net8.0-windows\WinAutomationMcp.exe
```

## Run on port 8766 (test instance, does not touch 8765)

It must be started in the interactive session. Over ssh use the scheduled task helper (a task with an Interactive principal runs in the logged-on user's session):

```powershell
.\install-task.ps1 -ExePath C:\Users\Eduardo\WinAutomationMcp_040src\bin\Release\net8.0-windows\WinAutomationMcp.exe -Port 8766 -TaskName WinAutomationMcpTest -StartNow
# remove when done
.\install-task.ps1 -TaskName WinAutomationMcpTest -Remove
```

From the Mac, talk to it through the tunnel: `ssh -W 127.0.0.1:8766 win-vm`.

## Test

```bash
cd test
PORT=8766 python3 -c "from mcp_client import C; c=C(); c.call('server_info'); c.call('list_windows'); c.close()"
```

`t_run_steps.py` is the 0.4.0 smoke test (Calculator through run_steps). `t_ui.py` drives the TestApp (build it with `dotnet build -c Release` in `test\TestApp`, then adjust the exe path at the top of the script). It covers launch, find_elements, control_type/nth, type_text, clear_field, select_option, assert_state, wait_for.

Quick recording check on the VM after a recording:

```powershell
.\tools\ffprobe.exe -v error -show_entries stream=codec_name,width,height,nb_frames,duration -of default=nw=1 C:\Users\Eduardo\recordings\<file>.mp4
```

## Recording notes

- `start_recording window=...` records the rectangle of the window (visible frame plus nearby popups) as it was at start. If the window moves or resizes during the recording the area does not follow. Without `window` it records the whole virtual screen.
- `max_seconds` (default 300) is enforced by ffmpeg itself; when it expires the file is closed properly and `stop_recording` still reports it.
- Caption: pass `caption` at start (an empty string enables the overlay), change it live with `set_recording_caption`.
- Default output folder `C:\Users\Eduardo\recordings\`. To fetch: `scp win-vm:C:/Users/Eduardo/recordings/<file>.mp4 .`
- Recordings are not stopped when the MCP connection drops; they stop at `max_seconds` or when the server exits.

## Security

- The server binds to 127.0.0.1 only, but anyone who can reach that port (for example anyone with the SSH tunnel, or any local process) gets arbitrary code execution as the logged-on user through `run_powershell`, `run_cmd`, `launch_app`, `write_file`, `kill_process`. There is no authentication. Do not forward the port beyond the ssh tunnel.
- Screenshots, recordings, `get_element_text` and `clipboard_get` show whatever is on screen or in the clipboard, including passwords that have been revealed (eye button) and PHI. Keep recordings out of git and delete them from the VM when done.
- The server log never records argument values (only argument names), because typed text may be a password.
- `kill_process` is deliberately strict (exact pid plus exact exe path) so one agent cannot kill another agent's app by mistake.

## Reliability, startup and keepalive

- Log: `<exe folder>\logs\win-mcp-<port>.log` (rotates at 5 MB, one `.1` backup). Each connection logs open time, remote endpoint, request count and the close reason (client EOF, peer reset or tunnel drop, write failure).
- Two connections at once are supported (verified). UI tools are serialized; commands, files and recordings run in parallel.
- Survive logon: `install-task.ps1 -Port 8765 -TaskName WinAutomationMcp -StartNow` creates an at-logon Interactive task (restarts up to 5 times, 1 minute apart). The VM user must be logged in (enable auto-logon, otherwise after a reboot there is no desktop session and no UI to automate).
- Tunnel keepalive: the client side should use `ServerAliveInterval 15` and `ServerAliveCountMax 4` (already set in the Mac ssh config). Better, use `win-mcp-proxy.py` as the MCP command so a dropped tunnel is re-established automatically and in-flight calls fail fast instead of hanging: `claude mcp add windows-automation -- python3 /path/to/win-mcp-proxy.py --host win-vm --port 8765`. Tested by killing the ssh child mid-session: the next call succeeded on a new tunnel.

## Deploy 0.4.0 (only after approval)

State before deploy (checked 2026-10-09): live server is `C:\Users\Eduardo\WinAutomationMcp_live\WinAutomationMcp.exe` (0.3.0) on 8765, run by the Scheduled Task `WinAutomationMcp` (interactive session). 0.4.0 is built in `C:\Users\Eduardo\WinAutomationMcp_dev` (source in `C:\Users\Eduardo\WinAutomationMcp_040src`). Warn other sessions first: stopping the server drops their connections (Claude Code needs `/mcp` > Reconnect, unless they go through `win-mcp-proxy.py`).

Run in PowerShell on the VM (ssh is fine, the task restarts the server in the interactive session):

```powershell
# 1. Backup the running 0.3.0 folder (this is the rollback copy)
robocopy C:\Users\Eduardo\WinAutomationMcp_live C:\Users\Eduardo\WinAutomationMcp_live_0.3.0 /MIR /NFL /NDL /NJH /NJS

# 2. Stop the live server through its task (kills only the process the task started)
Stop-ScheduledTask -TaskName WinAutomationMcp
Start-Sleep 2

# 3. Copy 0.4.0 over the live folder (logs are kept)
robocopy C:\Users\Eduardo\WinAutomationMcp_dev C:\Users\Eduardo\WinAutomationMcp_live /MIR /XD logs /NFL /NDL /NJH /NJS

# 4. Start it again
Start-ScheduledTask -TaskName WinAutomationMcp

# 5. Verify (from the Mac, through the tunnel): server_info must say 0.4.0, port 8765, session_id 1
```

If the live exe is not started by the task (for example it was started by hand in a terminal, as in the older setup), stop that exact PID first (check its path with `Get-Process WinAutomationMcp | Select Id,Path`) and register the task once with `install-task.ps1 -ExePath C:\Users\Eduardo\WinAutomationMcp_live\WinAutomationMcp.exe -Port 8765 -TaskName WinAutomationMcp -StartNow`.

### Rollback to 0.3.0

```powershell
Stop-ScheduledTask -TaskName WinAutomationMcp
Start-Sleep 2
robocopy C:\Users\Eduardo\WinAutomationMcp_live_0.3.0 C:\Users\Eduardo\WinAutomationMcp_live /MIR /XD logs /NFL /NDL /NJH /NJS
Start-ScheduledTask -TaskName WinAutomationMcp
```

Older rollback (0.2.0): `C:\Users\Eduardo\WinAutomationMcp\bin\Debug\net8.0-windows\WinAutomationMcp.exe` is untouched; it ignores `--port` and listens on 8765, so register it with `install-task.ps1 -ExePath <that exe> -Port 8765 -TaskName WinAutomationMcpOld -StartNow` after removing the new task.

### The 8766 test instance

During development a second task `WinAutomationMcpDev` (port 8766, exe in `WinAutomationMcp_dev`) was registered and left running. It does not touch 8765. Remove it when no longer needed (this also stops it):

```powershell
.\install-task.ps1 -TaskName WinAutomationMcpDev -Remove
```

Rebuild 0.4.0 from source: `cd C:\Users\Eduardo\WinAutomationMcp_040src; dotnet build -c Release -nologo`, then copy `bin\Release\net8.0-windows` plus `tools` to the target folder (stop the instance that uses it first, the exe is locked while it runs).
