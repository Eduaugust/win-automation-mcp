// Excalidraw skeletons for the README diagrams. render.mjs turns each one into
// docs/diagrams/<name>.excalidraw (editable at excalidraw.com) and docs/img/<name>.png.

const ink = "#1e1e1e";
const orange = "#e8590c";
const muted = "#868e96";

const box = (id, x, y, w, h, text, o = {}) => ({
  type: "rectangle", id, x, y, width: w, height: h,
  strokeColor: o.stroke ?? ink, backgroundColor: o.bg ?? "transparent",
  fillStyle: "solid", strokeWidth: o.sw ?? 2, roundness: { type: 3 },
  strokeStyle: o.dashed ? "dashed" : "solid",
  ...(text ? { label: { text, fontSize: o.fs ?? 20, strokeColor: o.color ?? ink, verticalAlign: o.va ?? "middle" } } : {}),
});
const text = (x, y, t, o = {}) => ({
  type: "text", x, y, text: t, fontSize: o.fs ?? 20, strokeColor: o.color ?? ink,
  ...(o.align ? { textAlign: o.align } : {}),
});
const arrow = (from, to, o = {}) => ({
  type: "arrow", x: o.x ?? 0, y: o.y ?? 0, width: o.w ?? 100, height: o.h ?? 0,
  strokeColor: o.stroke ?? orange, strokeWidth: 2,
  strokeStyle: o.dashed ? "dashed" : "solid",
  start: { id: from }, end: { id: to },
  ...(o.label ? { label: { text: o.label, fontSize: 16, strokeColor: o.stroke ?? orange } } : {}),
});

export const scenes = {
  architecture: [
    box("mac", 0, 0, 560, 520, "", { stroke: muted, dashed: true }),
    text(20, 16, "Mac (host)", { fs: 24, color: muted }),
    box("client", 60, 70, 440, 100, "MCP client\nClaude Code · Cursor · Copilot", { bg: "#e7f5ff" }),
    box("proxy", 60, 230, 440, 100, "win-demo-mcp (lazy proxy)\nboots the VM on the first call", { bg: "#fff9db" }),
    box("ssh", 60, 390, 440, 90, "ssh -W 127.0.0.1:8765 win-vm\nstdio over SSH, dedicated key", { bg: "#fff4e6", stroke: orange }),
    arrow("client", "proxy", { x: 280, y: 172, w: 0, h: 56, label: "stdio" }),
    arrow("proxy", "ssh", { x: 280, y: 332, w: 0, h: 56 }),

    box("vm", 660, 0, 640, 520, "", { stroke: muted, dashed: true }),
    text(680, 16, "UTM VM · Windows 11 · interactive session 1", { fs: 24, color: muted }),
    box("server", 720, 70, 520, 110, "WinAutomationMcp.exe\nJSON-RPC on 127.0.0.1:8765 only\n42 tools", { bg: "#fff4e6", stroke: orange, sw: 3 }),
    arrow("ssh", "server", { x: 502, y: 430, w: 216, h: -300, label: "TCP inside the guest" }),
    box("apis", 700, 230, 560, 260, "", { stroke: ink, sw: 1 }),
    text(716, 238, "Windows APIs", { fs: 18, color: muted }),
    arrow("server", "apis", { x: 980, y: 182, w: 0, h: 46, stroke: ink }),
    box("uia", 720, 270, 250, 90, "UI Automation\nFlaUI / UIA3", { bg: "#ebfbee", fs: 18 }),
    box("win32", 990, 270, 250, 90, "Win32\nmouse · keys · windows", { bg: "#ebfbee", fs: 18 }),
    box("ps", 720, 380, 250, 90, "PowerShell · cmd\nfiles · processes", { bg: "#ebfbee", fs: 18 }),
    box("ff", 990, 380, 250, 90, "ffmpeg\nrecording + captions", { bg: "#ebfbee", fs: 18 }),
    text(0, 550, "Nothing listens on the network: the only way in is the SSH tunnel.", { fs: 20, color: muted }),
  ],

  "agent-loop": [
    box("rec", 0, 0, 1060, 500, "", { stroke: orange, dashed: true }),
    text(24, 16, "start_recording  ·  set_recording_caption  ·  stop_recording", { fs: 20, color: orange }),
    box("look", 60, 110, 280, 220, "1. Look\n\nlist_windows\ndescribe_ui\nget_element_tree\nscreenshot", { bg: "#e7f5ff" }),
    box("act", 390, 110, 280, 220, "2. Act\n\nlaunch_app\nclick_element\nset_value\nkey_press", { bg: "#fff9db" }),
    box("verify", 720, 110, 280, 220, "3. Verify\n\nwait_for\nassert_state\nget_element_text", { bg: "#ebfbee" }),
    arrow("look", "act", { x: 342, y: 220, w: 46, h: 0 }),
    arrow("act", "verify", { x: 672, y: 220, w: 46, h: 0 }),
    { type: "arrow", x: 860, y: 340, width: 660, height: 50, strokeColor: orange, strokeWidth: 2,
      points: [[0, 0], [0, 50], [-660, 50], [-660, 0]] },
    text(410, 400, "repeat until the goal is met", { fs: 18, color: orange }),
    text(60, 446, "run_steps runs a whole batch of these in one MCP call and stops at the first failure.", { fs: 20, color: muted }),
  ],

  sessions: [
    box("s0", 0, 0, 480, 300, "session 0\n\nWindows services\nplain ssh logins\n\nno desktop: empty window list,\nclicks go nowhere", { bg: "#f1f3f5", stroke: muted }),
    box("s1", 560, 0, 480, 300, "session 1\n\nthe logged-on user's desktop\n\nWinAutomationMcp.exe runs here\nand sees the same windows you do", { bg: "#fff4e6", stroke: orange, sw: 3 }),
    box("task", 560, 380, 480, 90, "Scheduled Task (AtLogOn, Interactive)\ninstall-task.ps1", { bg: "#ebfbee" }),
    arrow("task", "s1", { x: 800, y: 378, w: 0, h: -76, stroke: ink, label: "starts at logon" }),
    text(0, 400, "Do not install it as a Windows service\nor start it from an ssh shell.", { fs: 20, color: muted }),
  ],

  tools: (() => {
    const groups = [
      ["See", "list_windows\ndescribe_ui\nget_element_tree\nfind_elements\nget_element_text\nget_window_rect\nscreenshot", "#e7f5ff"],
      ["Act", "click_element · click_at\ntype_text · type_text_raw\nset_value · clear_field\nselect_option · key_press\nmouse_click · mouse_drag\nclipboard_get · clipboard_set", "#fff9db"],
      ["Windows", "focus_window\nmove_window\nresize_window\nset_window_state\nwait_for_window", "#f3f0ff"],
      ["OS", "launch_app · open_url\nlaunch_packaged_app\nlist_packaged_apps\nrun_powershell · run_cmd\nread_file · write_file\nlist_processes · kill_process", "#ffe3e3"],
      ["Verify", "wait_for\nassert_state", "#ebfbee"],
      ["Record", "start_recording\nset_recording_caption\nrecording_status\nstop_recording", "#fff4e6"],
      ["Batch", "run_steps\nserver_info", "#f1f3f5"],
    ];
    const w = 300, gap = 30, heights = [240, 160];
    return groups.map(([title, body, bg], i) => {
      const col = i % 4, row = Math.floor(i / 4);
      return box(`g${i}`, col * (w + gap), row * (heights[0] + gap), w, heights[row], `${title}\n\n${body}`, { bg, fs: 18, va: "top" });
    });
  })(),
};
