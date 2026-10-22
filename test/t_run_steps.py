"""0.4.0 smoke test: run_steps against Calculator (launch, wait for UI, 7*6=42, assert, screenshot).
Usage: PORT=8766 python3 t_run_steps.py   (needs ssh -W access to the VM as in mcp_client.py)"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
os.makedirs("/tmp/winmcp/out", exist_ok=True)
from mcp_client import C

c = C()
c.call("server_info")
steps = [
    {"action": "launch_app", "path": "calc.exe", "window": "Calculator", "wait_ms": 30000},
    {"action": "wait_for", "automation_id": "num7Button", "condition": "exists", "timeout_ms": 15000},
    {"action": "describe_ui", "max_elements": 8},
    {"action": "click_element", "automation_id": "clearButton", "optional": True},
    {"action": "click_element", "automation_id": "num7Button"},
    {"action": "click_element", "automation_id": "multiplyButton"},
    {"action": "click_element", "automation_id": "num6Button"},
    {"action": "click_element", "automation_id": "equalButton"},
    {"action": "assert_state", "automation_id": "CalculatorResults", "condition": "text_contains", "value": "42"},
    {"action": "screenshot", "max_width": 400},
]
c.call("run_steps", {"steps": steps, "window": "Calculator"})
c.close()
