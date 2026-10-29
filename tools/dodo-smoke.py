"""Isolated Windows process/MCP smoke test. Never launches a visible paper or edits a real profile.

Generate the image/plugin fixture first:
  dotnet run --project tests/PaperTodo.PersistenceChecks -c Release -- --write-restored-integration-fixture .local/integration-fixture
Run against a build or extracted release (the executable is copied into a fresh sandbox):
  python tools/dodo-smoke.py --app-dir <directory> --fixture .local/integration-fixture --dotnet-root .local/dotnet --baseline .local/baseline
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time
import uuid

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


CURATED = ("sample.clock.native", "sample.focus-timer.native")
HIDDEN = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
STARTUP = subprocess.STARTUPINFO() if os.name == "nt" else None
if STARTUP is not None:
    STARTUP.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    STARTUP.wShowWindow = 0


def check(value, message):
    if not value:
        raise AssertionError(message)


def launch(args, **kwargs):
    return subprocess.Popen(args, creationflags=HIDDEN, startupinfo=STARTUP, **kwargs)


def existing_mutex(name):
    api = ctypes.WinDLL("kernel32", use_last_error=True)
    api.OpenMutexW.argtypes = [ctypes.c_uint, ctypes.c_bool, ctypes.c_wchar_p]
    api.OpenMutexW.restype = ctypes.c_void_p
    handle = api.OpenMutexW(0x100000, False, name)
    if handle:
        api.CloseHandle(ctypes.c_void_p(handle))
    return bool(handle)


def copy_app(source, target, plugin_source):
    target.mkdir(parents=True)
    excluded = {"plugins", "dodo-data", "data.json", "data.backup.json", "note-assets.lmdb"}
    for path in source.iterdir():
        if path.name.lower() in excluded or path.suffix.lower() == ".log":
            continue
        if path.is_dir():
            shutil.copytree(path, target / path.name)
        else:
            shutil.copy2(path, target / path.name)
    for plugin in CURATED:
        path = source / "plugins" / plugin
        if not path.is_dir():
            path = plugin_source / plugin
        check(path.is_dir(), "Missing curated plugin: " + plugin)
        shutil.copytree(path, target / "plugins" / plugin)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def prepare_profile(fixture, target, baseline=False):
    shutil.copytree(fixture, target)
    state = read_json(target / "data.json")
    if not baseline:
        # Preserve the restored profile byte-for-byte. Refuse unsafe fixtures instead of
        # rewriting flags and weakening the backup -> restore -> actual-process test chain.
        check(all(state.get(key) is True for key in ("mcpEnabled", "mcpAllowBlankWrites", "mcpAllowFullWrites")),
              "Dodo fixture must explicitly enable the isolated MCP write checks")
        check(state.get("useCapsuleMode") is False and state.get("useDeepCapsuleMode") is False and
              all(paper.get("isVisible") is False for paper in state["papers"]), "Dodo fixture must be hidden")
        return
    state.update(mcpEnabled=True, mcpAllowBlankWrites=True, mcpAllowFullWrites=True,
                 mcpAllowDeletes=True, telemetryEnabled=False, useCapsuleMode=False,
                 useDeepCapsuleMode=False, enableAnimations=False, uiLanguage="zh-CN")
    for paper in state["papers"]:
        paper["isVisible"] = False
    if baseline:
        state["papers"][0]["title"] = "上游隔离"
        state["papers"][0]["content"] = "PaperTodo isolation sentinel\n" + state["papers"][0]["content"]
    write_json(target / "data.json", state)
    write_json(target / "data.backup.json", state)


class Bridge:
    def __init__(self, executable, env, log_path):
        self.stderr = log_path.with_suffix(".stderr.log").open("w", encoding="utf-8")
        self.process = launch([str(executable), "--mcp"], cwd=executable.parent, env=env,
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr,
                              text=True, encoding="utf-8", bufsize=1)
        self.queue = queue.Queue()
        self.events = []
        self.log_path = log_path
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()
        self.request("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                     "clientInfo": {"name": "Dodo-isolated-smoke", "version": "1.0"}})
        self.notify("notifications/initialized", {})

    def _read(self):
        for line in self.process.stdout:
            try:
                self.queue.put(json.loads(line))
            except Exception:
                self.queue.put({"invalid_stdout": line})
        self.queue.put({"closed": self.process.poll()})

    def notify(self, method, params):
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method, "params": params}) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.next_id += 1
        request = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}
        self.events.append({"request": request})
        self.process.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
        self.process.stdin.flush()
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            response = self.queue.get(timeout=max(.1, deadline - time.monotonic()))
            self.events.append({"response": response})
            check("closed" not in response and "invalid_stdout" not in response, "MCP stdout closed or invalid")
            if response.get("id") != self.next_id:
                continue
            check("error" not in response, "MCP error: " + json.dumps(response, ensure_ascii=False))
            return response["result"]
        raise TimeoutError(method)

    def tool(self, name, **arguments):
        response = self.request("tools/call", {"name": name, "arguments": arguments})
        check(not response.get("isError"), "Tool error: " + json.dumps(response, ensure_ascii=False))
        if "structuredContent" in response:
            return response["structuredContent"]
        return json.loads(next(item["text"] for item in response["content"] if item["type"] == "text"))

    def close(self):
        self.log_path.write_text(json.dumps(self.events, ensure_ascii=False, indent=2), encoding="utf-8")
        if self.process.poll() is None:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.terminate()
                self.process.wait(timeout=5)
        self.stderr.close()


def start_gui(executable, env, log):
    stderr = log.open("w", encoding="utf-8")
    started = time.monotonic()
    process = launch([str(executable), "--hide"], cwd=executable.parent, env=env,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=stderr)
    return process, started, stderr


def wait_ready(bridge, process, started):
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline:
        check(process.poll() is None, "GUI exited during startup")
        try:
            data = bridge.tool("list_papers")
            return data, round((time.monotonic() - started) * 1000)
        except AssertionError as error:
            if "not running" not in str(error) and "exiting" not in str(error):
                raise
            time.sleep(.2)
    raise TimeoutError("GUI MCP readiness")


def stop_gui(executable, process, env):
    if process.poll() is not None:
        check(process.returncode == 0, "GUI exited abnormally")
        return
    command = launch([str(executable), "--exit"], cwd=executable.parent, env=env,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    command.wait(timeout=15)
    process.wait(timeout=20)
    check(process.returncode == 0 and command.returncode == 0, "GUI exit command failed")


def metrics(process):
    class FILETIME(ctypes.Structure):
        _fields_ = [("low", ctypes.c_ulong), ("high", ctypes.c_ulong)]
    class MEMORY(ctypes.Structure):
        _fields_ = [("cb", ctypes.c_ulong), ("faults", ctypes.c_ulong)] + [(name, ctypes.c_size_t) for name in
                    ("peak", "working", "peakPaged", "paged", "peakNonPaged", "nonPaged", "pagefile", "peakPagefile")]
    kernel = ctypes.WinDLL("kernel32")
    psapi = ctypes.WinDLL("psapi")
    handle = ctypes.c_void_p(int(process._handle))
    def cpu():
        values = [FILETIME() for _ in range(4)]
        check(kernel.GetProcessTimes(handle, *(ctypes.byref(v) for v in values)), "GetProcessTimes failed")
        return sum((v.high << 32) + v.low for v in values[2:]) / 10000
    first = cpu()
    started = time.monotonic()
    time.sleep(3)
    elapsed = time.monotonic() - started
    sample = MEMORY()
    sample.cb = ctypes.sizeof(sample)
    check(psapi.GetProcessMemoryInfo(handle, ctypes.byref(sample), sample.cb), "GetProcessMemoryInfo failed")
    return {"idle_sample_seconds": round(elapsed, 3), "idle_cpu_ms": round(cpu() - first, 2),
            "working_set_mib": round(sample.working / 1024 ** 2, 2)}


class WindowObserver:
    """Read-only HWND sampling; never sends input, activates or modifies a window."""
    def __init__(self):
        self.api = ctypes.WinDLL("user32", use_last_error=True)
        self.api.GetForegroundWindow.restype = ctypes.c_void_p
        self.api.MonitorFromWindow.argtypes = [ctypes.c_void_p, ctypes.c_uint]
        self.api.MonitorFromWindow.restype = ctypes.c_void_p
        self.pids = set()
        self.stop_event = threading.Event()
        self.peak = 0
        self.onscreen_peak = 0
        self.foreground_samples = 0
        self.samples = 0
        self.visible_details = {}
        self.before = self.foreground_pid()
        self.thread = threading.Thread(target=self.sample_loop, daemon=True)
        self.thread.start()

    def foreground_pid(self):
        pid = ctypes.c_ulong()
        handle = self.api.GetForegroundWindow()
        self.api.GetWindowThreadProcessId(ctypes.c_void_p(handle), ctypes.byref(pid))
        return pid.value

    def sample_loop(self):
        callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_ssize_t)
        while not self.stop_event.is_set():
            count = 0
            onscreen = 0
            pids = self.pids.copy()
            def visit(window, _):
                nonlocal count, onscreen
                pid = ctypes.c_ulong()
                self.api.GetWindowThreadProcessId(ctypes.c_void_p(window), ctypes.byref(pid))
                if pid.value in pids and self.api.IsWindowVisible(ctypes.c_void_p(window)):
                    count += 1
                    class RECT(ctypes.Structure):
                        _fields_ = [(name, ctypes.c_long) for name in ("left", "top", "right", "bottom")]
                    rect = RECT()
                    self.api.GetWindowRect(ctypes.c_void_p(window), ctypes.byref(rect))
                    title = ctypes.create_unicode_buffer(256)
                    class_name = ctypes.create_unicode_buffer(256)
                    self.api.GetWindowTextW(ctypes.c_void_p(window), title, 256)
                    self.api.GetClassNameW(ctypes.c_void_p(window), class_name, 256)
                    intersects_monitor = bool(self.api.MonitorFromWindow(ctypes.c_void_p(window), 0))
                    if intersects_monitor and rect.right > rect.left and rect.bottom > rect.top:
                        onscreen += 1
                    self.visible_details[str(window)] = {"pid": pid.value, "title": title.value,
                        "class": class_name.value, "intersects_monitor": intersects_monitor,
                        "rect": [rect.left, rect.top, rect.right, rect.bottom]}
                return True
            self.api.EnumWindows(callback_type(visit), 0)
            self.peak = max(self.peak, count)
            self.onscreen_peak = max(self.onscreen_peak, onscreen)
            if self.foreground_pid() in pids:
                self.foreground_samples += 1
            self.samples += 1
            self.stop_event.wait(.025)

    def finish(self):
        self.stop_event.set()
        self.thread.join(timeout=2)
        return {"sampling_interval_ms": 25, "sample_count": self.samples,
                "peak_visible_flag_windows": self.peak, "peak_onscreen_visible_windows": self.onscreen_peak,
                "app_foreground_samples": self.foreground_samples,
                "visible_flag_window_details": list(self.visible_details.values()),
                "foreground_pid_before": self.before, "foreground_pid_after": self.foreground_pid()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-dir", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--dotnet-root", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--output-root", type=Path, default=Path(".local/integration"))
    args = parser.parse_args()
    check(os.name == "nt", "Windows is required")
    check(not existing_mutex("Dodo-SingleInstance-Mutex"), "Close the existing Dodo instance before isolated smoke testing")
    if args.baseline:
        check(not existing_mutex("PaperTodo-SingleInstance-Mutex"), "Close the existing PaperTodo instance before isolated smoke testing")
    source = args.app_dir.resolve()
    fixture = args.fixture.resolve()
    run = args.output_root.resolve() / (time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8])
    run.mkdir(parents=True)
    app = run / "dodo"
    plugins = Path(__file__).resolve().parents[1] / "plugins"
    copy_app(source, app, plugins)
    prepare_profile(fixture, app / "dodo-data")
    image_hash = hashlib.sha256((app / "dodo-data/note-assets.lmdb").read_bytes()).hexdigest()
    env = os.environ.copy()
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    if args.dotnet_root:
        env["DOTNET_ROOT"] = str(args.dotnet_root.resolve())
        env["PATH"] = env["DOTNET_ROOT"] + os.pathsep + env["PATH"]
    chain_evidence = Path(str(fixture) + ".restore-chain.json")
    report = {"directory": str(run), "application_source": str(source), "fixture_source": str(fixture),
              "fixture_restore_chain_evidence": str(chain_evidence) if chain_evidence.is_file() else None,
              "checks": [], "status": "running"}
    bridges = []
    processes = []
    streams = []
    observer = WindowObserver()
    try:
        exe = app / "Dodo.exe"
        gui, started, stderr = start_gui(exe, env, run / "dodo-gui.stderr.log")
        processes.append((exe, gui)); streams.append(stderr)
        observer.pids.add(gui.pid)
        bridge = Bridge(exe, env, run / "dodo-mcp.json")
        bridges.append(bridge)
        observer.pids.add(bridge.process.pid)
        initial, elapsed = wait_ready(bridge, gui, started)
        report["startup_to_mcp_ready_ms"] = elapsed
        report["tools"] = [tool["name"] for tool in bridge.request("tools/list", {})["tools"]]
        report["search"] = "not exposed by this MCP API" if not any("search" in name for name in report["tools"]) else "exposed"
        check(len(initial["papers"]) == 3 and all(not p["is_visible"] for p in initial["papers"]), "fixture was not loaded hidden")
        image_note = bridge.tool("get_paper", paper_id="fixture-image-note")
        check("i:001" in image_note["content"], "fixture image reference missing")
        focus = bridge.tool("get_paper", paper_id="fixture-focus")
        check(json.loads(focus["body_state"]["json"])["completedFocusSessions"] == 7, "plugin state missing")
        report["checks"].append("hidden fixture/image reference/native plugin state loaded through MCP")
        note = bridge.tool("create_note", title="中文笔记", content="# 中文验收\n\n第一段 🐦", show=False)
        note = bridge.tool("write_note", paper_id=note["paper_id"], content="\n\n追加第二段", mode="append")
        check("追加第二段" in note["content"], "Unicode note append failed")
        todo = bridge.tool("create_todo_paper", title="中文待办", todos=[{"text": "检查图片"}, {"text": "完成备份"}], show=False)
        todo_item = todo["todos"][0]
        bridge.tool("update_todo", paper_id=todo["paper_id"], todo_id=todo_item["id"], done=True)
        todo = bridge.tool("get_paper", paper_id=todo["paper_id"])
        check(any(item["done"] and item["text"] == "检查图片" for item in todo["todos"]), "todo completion failed")
        settings = bridge.tool("list_settings")
        check(len(settings["settings"]) > 0, "empty settings catalog")
        bridge.tool("set_setting", id="general.tooltips", value=False)
        setting = bridge.tool("get_setting", id="general.tooltips")
        check(setting["value"] is False, "settings write/read failed")
        report["checks"].append("Chinese Note create/append/read, Todo create/complete/read, settings list/set/get")
        report["idle"] = metrics(gui)
        if args.baseline:
            upstream = run / "papertodo"
            copy_app(args.baseline.resolve(), upstream, plugins)
            # The upstream profile is beside its executable. Copy files rather than profile
            # directory so its data protocol is tested with its original path convention.
            staging = run / "baseline-profile"
            prepare_profile(fixture, staging, baseline=True)
            for item in staging.iterdir():
                if item.name == "plugins":
                    shutil.copytree(item / "data", upstream / "plugins/data")
                elif item.is_file():
                    shutil.copy2(item, upstream / item.name)
            upstream_exe = upstream / "PaperTodo.exe"
            upstream_gui, upstream_started, stderr = start_gui(upstream_exe, env, run / "papertodo-gui.stderr.log")
            processes.append((upstream_exe, upstream_gui)); streams.append(stderr)
            observer.pids.add(upstream_gui.pid)
            upstream_bridge = Bridge(upstream_exe, env, run / "papertodo-mcp.json")
            bridges.append(upstream_bridge)
            observer.pids.add(upstream_bridge.process.pid)
            wait_ready(upstream_bridge, upstream_gui, upstream_started)
            upstream_note = upstream_bridge.tool("get_paper", paper_id="fixture-image-note")
            check("isolation sentinel" in upstream_note["content"], "upstream bridge reached Dodo")
            upstream_bridge.tool("write_note", paper_id="fixture-image-note", content="\n上游独立写入", mode="append")
            check("上游独立写入" not in bridge.tool("get_paper", paper_id="fixture-image-note")["content"], "Dodo shared upstream data")
            check(gui.poll() is None and upstream_gui.poll() is None, "single instance identities collided")
            upstream_bridge.close(); bridges.remove(upstream_bridge)
            stop_gui(upstream_exe, upstream_gui, env)
            processes.remove((upstream_exe, upstream_gui))
            report["checks"].append("Dodo and PaperTodo simultaneously hidden; separate mutex, MCP pipe and saved data")
        bridge.close(); bridges.remove(bridge)
        stop_gui(exe, gui, env); processes.remove((exe, gui))
        gui, started, stderr = start_gui(exe, env, run / "dodo-reopen.stderr.log")
        processes.append((exe, gui)); streams.append(stderr)
        observer.pids.add(gui.pid)
        bridge = Bridge(exe, env, run / "dodo-reopen-mcp.json")
        bridges.append(bridge)
        observer.pids.add(bridge.process.pid)
        reopened, elapsed = wait_ready(bridge, gui, started)
        report["reopen_to_mcp_ready_ms"] = elapsed
        check(all(not paper["is_visible"] for paper in reopened["papers"]), "reopen exposed a paper")
        check(bridge.tool("get_paper", paper_id=note["paper_id"])["content"] == note["content"], "note did not persist")
        check(bridge.tool("get_paper", paper_id=todo["paper_id"])["todos"] == todo["todos"], "todos did not persist")
        check(bridge.tool("get_setting", id="general.tooltips")["value"] is False, "setting did not persist")
        check(json.loads(bridge.tool("get_paper", paper_id="fixture-focus")["body_state"]["json"])["completedFocusSessions"] == 7, "plugin did not persist")
        check("i:001" in bridge.tool("get_paper", paper_id="fixture-image-note")["content"], "image reference did not persist")
        bridge.close(); bridges.remove(bridge)
        stop_gui(exe, gui, env); processes.remove((exe, gui))
        check(hashlib.sha256((app / "dodo-data/note-assets.lmdb").read_bytes()).hexdigest() == image_hash, "image database changed unexpectedly")
        report["checks"].append("actual process exit/reopen preserved Chinese content, completion, setting, image database and native plugin state")
        check(observer.onscreen_peak == 0 and observer.foreground_samples == 0, "a smoke-test process displayed or activated a window")
        report["status"] = "passed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = repr(error)
        raise
    finally:
        for bridge in bridges:
            bridge.close()
        for executable, process in processes:
            try:
                stop_gui(executable, process, env)
            except Exception:
                # Only child processes created by this run are eligible for fallback cleanup.
                if process.poll() is None:
                    process.terminate(); process.wait(timeout=10)
        for stream in streams:
            stream.close()
        report["window_observation"] = observer.finish()
        write_json(run / "report.json", report)
        print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
