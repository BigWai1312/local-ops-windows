"""Read-only WPF/WebView2 comparison. Requires already installed psutil and pywebview.

Build the benchmark host first; output stays in a new ignored tmp directory.
Only processes started by this harness are stopped. Production state is never loaded.
"""
from __future__ import annotations

import argparse
import copy
import ctypes
from ctypes import wintypes
import csv
import hashlib
import http.server
import importlib.metadata
import json
import os
from pathlib import Path
import queue
import random
import statistics
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request

import psutil

ROOT = Path(__file__).resolve().parents[1]


class JobAccounting:
    """Windows keeps CPU time for exited descendants in the fixture's private job."""
    class Info(ctypes.Structure):
        _fields_ = [(name, ctypes.c_int64) for name in ("user", "kernel", "period_user", "period_kernel")] + [
            (name, wintypes.DWORD) for name in ("faults", "total", "active", "terminated")]

    def __init__(self, child):
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        self.api.CreateJobObjectW.restype = wintypes.HANDLE
        self.api.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        self.api.QueryInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p]
        self.api.IsProcessInJob.argtypes = [wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)]
        self.api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.handle = self.api.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        if not self.api.AssignProcessToJobObject(self.handle, int(child._handle)):
            error = ctypes.get_last_error()
            self.close()
            raise ctypes.WinError(error)
        # Refuse startup races rather than silently omit a process created before assignment.
        for proc in psutil.Process(child.pid).children(recursive=True):
            handle = self.api.OpenProcess(0x1000, False, proc.pid)
            try:
                member = wintypes.BOOL()
                if not handle or not self.api.IsProcessInJob(handle, self.handle, ctypes.byref(member)) or not member.value:
                    self.close()
                    raise RuntimeError("Fixture descendant escaped accounting job")
            finally:
                if handle:
                    self.api.CloseHandle(handle)

    def cpu(self):
        info = self.Info()
        if not self.api.QueryInformationJobObject(self.handle, 1, ctypes.byref(info), ctypes.sizeof(info), None):
            raise ctypes.WinError(ctypes.get_last_error())
        return (info.user + info.kernel) / 10_000_000

    def close(self):
        if self.handle:
            self.api.CloseHandle(self.handle)
            self.handle = None


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    temporary.replace(path)


def html_client(port, ready, profile):
    sys.path.insert(0, str(ROOT))
    os.environ["CONSOLE_DATA_DIR"] = str(profile / "data")
    os.environ["CONSOLE_LOG_DIR"] = str(profile / "logs")
    import desktop
    import webview

    assert desktop.WINDOW_TITLE
    window = webview.create_window(
        "Local Ops - HTML benchmark", f"http://127.0.0.1:{port}/?desktop=1",
        width=1440, height=900, min_size=desktop.WINDOW_MIN_SIZE,
        resizable=True, text_select=True, zoomable=True, background_color="#f5f7fb")

    def loaded():
        try:
            def status(phase):
                value = window.evaluate_js("window.__benchmarkStatus()")
                value["phase"] = phase
                value["host_hidden"] = not window.native.Visible
                write_json(ready, value)
            for _ in range(200):
                value = window.evaluate_js("window.__benchmarkStatus ? window.__benchmarkStatus() : null")
                if value and value.get("rendered"):
                    break
                time.sleep(0.1)
            else:
                raise RuntimeError("HTML fixture did not render")
            status("ready")
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            while True:
                command = opener.open(f"http://127.0.0.1:{port}/bench/command", timeout=300).read().decode()
                if command == "exit":
                    window.destroy()
                    return
                if command == "hide":
                    window.hide()
                elif command == "show":
                    window.show()
                    window.restore()
                else:
                    raise RuntimeError("Unknown benchmark command")
                for _ in range(100):
                    if (not window.native.Visible) == (command == "hide"):
                        break
                    time.sleep(0.05)
                else:
                    raise RuntimeError("HTML host visibility did not change")
                status(command)
        except Exception as exc:
            ready.with_suffix(ready.suffix + ".error").write_text(repr(exc), encoding="utf-8")
            window.destroy()

    window.events.loaded += loaded
    webview.start(gui="edgechromium", debug=False, storage_path=str(profile))


def run(args):
    output = ROOT / "tmp" / args.output
    output.mkdir(parents=True, exist_ok=False)
    host = Path(args.host).resolve()
    state_path = output / "fixture-state.json"
    subprocess.run([str(host), "--export-state", str(state_path)], check=True)
    base = json.loads(state_path.read_text(encoding="utf-8-sig"))
    assert len(base["apps"]) == 6 and len(base["services"]) == 4
    base.update(consolePid=0, platform="windows", capabilities={}, themes=[
        {"id": "ops", "name": "Ops", "css": "/themes/ops.css"}])
    for item in base["apps"]:
        item["glyph"] = "server"
    for item in base["services"]:
        item["origin"] = {"label": "fixture"}
    commands = queue.Queue()
    counters = {"state": 0, "mutations": 0, "not_found": []}
    state = base
    state_started = time.perf_counter()
    dynamic = False

    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *values, **kwargs):
            super().__init__(*values, directory=str(ROOT / "static"), **kwargs)

        def log_message(self, *values):
            pass

        def payload(self, value, content_type):
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(value)))
            self.end_headers()
            self.wfile.write(value)

        def do_GET(self):
            path = urllib.parse.urlsplit(self.path).path
            if path == "/bench/command":
                self.payload(commands.get(timeout=300).encode(), "text/plain")
            elif path == "/api/state":
                counters["state"] += 1
                snapshot = copy.deepcopy(state)
                if dynamic:
                    tick = int((time.perf_counter() - state_started) / 2)
                    for index, service in enumerate(snapshot["services"]):
                        service["cpu"] = ((index + tick) % 40) / 10
                        service["mem"] = ((index + tick) % 20) / 10
                self.payload(json.dumps(snapshot).encode(), "application/json")
            elif path == "/":
                html = (ROOT / "static/index.html").read_text(encoding="utf-8")
                injection = """<script>
                localStorage.setItem('console-view','services');
                window.__benchmarkErrors=[];
                addEventListener('error',e=>window.__benchmarkErrors.push(e.message));
                addEventListener('unhandledrejection',e=>window.__benchmarkErrors.push(String(e.reason)));
                </script><script type="module">
                import {state} from '/js/core.js';
                window.__benchmarkStatus=()=>({apps:state.data?.apps.length,
                  services:state.data?.services.length,view:state.view,
                  width:innerWidth,height:innerHeight,hidden:document.hidden,
                  errors:window.__benchmarkErrors,
                  rows:document.querySelector('#mineList').children.length+
                       document.querySelector('#bgList').children.length,
                  rendered:document.body.textContent.includes('Atlas API')});
                </script>"""
                self.payload(html.replace("</head>", injection + "</head>").encode(), "text/html; charset=utf-8")
            else:
                super().do_GET()

        def do_POST(self):
            counters["mutations"] += 1
            self.send_error(405, "Read-only fixture")

        do_PUT = do_DELETE = do_POST

        def send_error(self, code, message=None, explain=None):
            if code == 404:
                counters["not_found"].append(self.path)
            super().send_error(code, message, explain)

    for _ in range(100):
        port = random.SystemRandom().randrange(10000, 65536)
        try:
            server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
            break
        except OSError:
            continue
    else:
        raise RuntimeError("No free five-digit fixture port")
    threading.Thread(target=server.serve_forever, daemon=True).start()
    rounds, samples = [], []

    def wait_status(child, ready, phase):
        deadline = time.perf_counter() + 60
        while time.perf_counter() < deadline:
            if child.poll() is not None:
                raise RuntimeError(f"Fixture exited {child.returncode}")
            if ready.with_suffix(ready.suffix + ".error").exists():
                raise RuntimeError(ready.with_suffix(ready.suffix + ".error").read_text())
            if ready.exists():
                value = json.loads(ready.read_text(encoding="utf-8-sig"))
                if value["phase"] == phase:
                    assert value["apps"] == len(state["apps"]) and value["services"] == len(state["services"]), value
                    assert value["rendered"] and not value.get("errors"), value
                    assert value.get("rows", value["services"]) == len(state["services"]), value
                    assert value.get("host_hidden", value["hidden"]) == (phase == "hide"), value
                    if "polling" in value:
                        assert value["polling"] == (phase != "hide"), value
                    return value
            time.sleep(0.1)
        raise TimeoutError(f"No fixture status: {phase}")

    def process_totals(root):
        rss = private = cpu = 0
        ids = []
        for proc in [root, *root.children(recursive=True)]:
            try:
                memory = proc.memory_info()
                times = proc.cpu_times()
                rss += memory.rss
                private += memory.private
                cpu += times.user + times.system
                ids.append(proc.pid)
            except psutil.NoSuchProcess:
                pass
        return rss / 1048576, private / 1048576, cpu, set(ids)

    def measure(root, accounting, number, kind, dataset, phase):
        initial = process_totals(root)
        initial_cpu = accounting.cpu()
        begin = time.perf_counter()
        before = counters["state"]
        rows = []
        previous_cpu, previous_time = initial_cpu, begin
        psutil.cpu_percent()
        for index in range(args.seconds):
            time.sleep(1)
            rss, private, cpu, ids = process_totals(root)
            cpu = accounting.cpu()
            now = time.perf_counter()
            assert cpu >= previous_cpu, "Cumulative job CPU must not decrease when a child exits"
            row = dict(round=number, dataset=dataset, kind=kind, phase=phase, sample=index + 1,
                       rss_mib=rss, private_mib=private, process_count=len(ids),
                       cpu_one_core_pct=(cpu-previous_cpu)/(now-previous_time)*100,
                       machine_load_pct=psutil.cpu_percent())
            rows.append(row)
            samples.append(row)
            previous_cpu, previous_time = cpu, now
        elapsed = now - begin
        result = dict(round=number, dataset=dataset, kind=kind, phase=phase, seconds=elapsed,
                      private_mib=statistics.median(r["private_mib"] for r in rows),
                      rss_mib=statistics.median(r["rss_mib"] for r in rows),
                      cpu_one_core_pct=(cpu-initial_cpu)/elapsed*100,
                      cpu_machine_pct=(cpu-initial_cpu)/elapsed/psutil.cpu_count()*100,
                      machine_load_pct=statistics.median(r["machine_load_pct"] for r in rows),
                      process_count=statistics.median(r["process_count"] for r in rows),
                      state_requests=counters["state"]-before)
        if phase == "hidden" and kind == "wpf":
            assert result["state_requests"] == 0, result
        elif phase == "foreground":
            assert result["state_requests"] > 0, result
        rounds.append(result)
        write_json(output / "rounds.json", rounds)
        with (output / "samples.csv").open("w", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=samples[0].keys())
            writer.writeheader()
            writer.writerows(samples)
        print(json.dumps(result), flush=True)

    try:
        order = ["wpf", "html", "html", "wpf", "wpf", "html"] if not args.smoke else ["wpf", "html"]
        number = 0
        datasets = ("small", "large") if args.dataset == "all" else (args.dataset,)
        for dataset in datasets:
            state = copy.deepcopy(base)
            dynamic = dataset == "large"
            if dynamic:
                state["services"] = []
                for index in range(400):
                    service = copy.deepcopy(base["services"][index % 4])
                    service.update(key=f"fixture-{index}", instanceKey=f"fixture-{index}-1000",
                                   pid=1000 + index, port=20000 + index, group="mine", pinned=False)
                    state["services"].append(service)
            for kind in order:
                number += 1
                ready = output / f"ready-{number}.json"
                command = [str(host), str(port), str(ready)] if kind == "wpf" else [
                    sys.executable, str(Path(__file__)), "--html", str(port), str(ready), str(output / f"profile-{number}")]
                with (output / f"client-{number}.log").open("w", encoding="utf-8") as log:
                    child = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=log)
                root = psutil.Process(child.pid)
                started = state_started = time.perf_counter()
                accounting = None
                try:
                    accounting = JobAccounting(child)
                    rendered = wait_status(child, ready, "ready")
                    ready_seconds = time.perf_counter()-started
                    time.sleep(args.warmup)
                    measure(root, accounting, number, kind, dataset, "foreground")
                    rounds[-1].update(ready_seconds=ready_seconds, rendered=rendered)
                    commands.put("hide")
                    hidden = wait_status(child, ready, "hide")
                    time.sleep(3)
                    measure(root, accounting, number, kind, dataset, "hidden")
                    rounds[-1]["rendered"] = hidden
                    before = counters["state"]
                    resumed = time.perf_counter()
                    commands.put("show")
                    wait_status(child, ready, "show")
                    deadline = resumed + 5
                    while counters["state"] == before and time.perf_counter() < deadline:
                        time.sleep(0.05)
                    assert counters["state"] > before, "Restoring did not resume state requests"
                    rounds[-1]["restore_request_seconds"] = time.perf_counter() - resumed
                    commands.put("exit")
                    child.wait(timeout=15)
                finally:
                    if child.poll() is None:
                        owned = [root, *root.children(recursive=True)]
                        for proc in reversed(owned):
                            try:
                                proc.terminate()
                            except psutil.NoSuchProcess:
                                pass
                        psutil.wait_procs(owned, timeout=5)
                        child.wait(timeout=5)
                    while not commands.empty():
                        commands.get_nowait()
                    if accounting is not None:
                        accounting.close()
        assert counters["mutations"] == 0 and not counters["not_found"], counters
        fields = ("private_mib", "rss_mib", "cpu_one_core_pct", "cpu_machine_pct", "process_count", "state_requests")
        report = dict(date=time.strftime("%Y-%m-%d %H:%M:%S"), logical_cpus=psutil.cpu_count(),
                      windows_build=sys.getwindowsversion().build,
                      physical_cpus=psutil.cpu_count(logical=False), memory_gib=psutil.virtual_memory().total/1024**3,
                      python=sys.version.split()[0], pywebview=importlib.metadata.version("pywebview"),
                      client_sha256=hashlib.sha256((host.parent / "LocalOpsWindows.Client.dll").read_bytes()).hexdigest(),
                      html_sha256=hashlib.sha256((ROOT / "static/app.js").read_bytes()).hexdigest(),
                      fixture_port=port, warmup=args.warmup, seconds=args.seconds, counters=counters,
                      rounds=rounds, summary=[])
        for dataset in datasets:
            for phase in ("foreground", "hidden"):
                for kind in ("wpf", "html"):
                    subset = [r for r in rounds if (r["dataset"], r["phase"], r["kind"]) == (dataset, phase, kind)]
                    report["summary"].append(dict(dataset=dataset, phase=phase, kind=kind,
                        **{field: statistics.median(r[field] for r in subset) for field in fields}))
        write_json(output / "results.json", report)
        with (output / "samples.csv").open("w", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=samples[0].keys())
            writer.writeheader()
            writer.writerows(samples)
        print("SUMMARY " + json.dumps(report["summary"]), flush=True)
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--html":
        html_client(int(sys.argv[2]), Path(sys.argv[3]), Path(sys.argv[4]))
    else:
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument("--host", required=True)
        parser.add_argument("--output", default="resource-benchmark-" + time.strftime("%Y%m%d-%H%M%S"))
        parser.add_argument("--seconds", type=int, default=30)
        parser.add_argument("--warmup", type=int, default=10)
        parser.add_argument("--smoke", action="store_true")
        parser.add_argument("--dataset", choices=("all", "small", "large"), default="all")
        options = parser.parse_args()
        if options.seconds < 5 or options.warmup < 1 or Path(options.output).name != options.output:
            parser.error("Use seconds >= 5, warmup >= 1 and a simple output directory name")
        run(options)
