#!/usr/bin/env python3
"""Verify full Linux discovery in a verify-typelay private session."""

import argparse
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import time
import unittest
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def save(path, value):
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")
    temporary.replace(path)


def test_ids(suite):
    for test in suite:
        if isinstance(test, unittest.TestSuite):
            yield from test_ids(test)
        else:
            yield test.id()


def discover(start_directory, path):
    report = {"discovered": 0, "testsRun": 0, "successful": False, "tests": {}}
    save(path, report)
    suite = unittest.defaultTestLoader.discover(str(start_directory))
    report["tests"] = {
        name: {"status": "not-run", "reason": "not started"} for name in test_ids(suite)
    }
    report["discovered"] = len(report["tests"])
    save(path, report)

    class Result(unittest.TextTestResult):
        def record(self, test, status, reason=None):
            value = {"status": status}
            if reason is not None:
                value["reason"] = reason
            name = test.id()
            # unittest represents a failed/skipped class or module setup by an
            # _ErrorHolder. Preserve the reason for each discovered affected case.
            if name.startswith(
                ("setUpClass (", "setUpModule (", "tearDownClass (", "tearDownModule (")
            ):
                prefix = name.split("(", 1)[1].removesuffix(")") + "."
                for item in report["tests"]:
                    if item.startswith(prefix):
                        report["tests"][item] = value
            else:
                report["tests"][name] = value
            report["testsRun"] = self.testsRun
            save(path, report)

        def startTest(self, test):
            super().startTest(test)
            self.record(test, "not-run", "started but did not finish")

        def addSuccess(self, test):
            super().addSuccess(test)
            self.record(test, "passed")

        def addSkip(self, test, reason):
            super().addSkip(test, reason)
            self.record(test, "not-run", reason)

        def addError(self, test, error):
            super().addError(test, error)
            self.record(test, "failed", self._exc_info_to_string(error, test))

        def addFailure(self, test, error):
            super().addFailure(test, error)
            self.record(test, "failed", self._exc_info_to_string(error, test))

        def addSubTest(self, test, subtest, error):
            super().addSubTest(test, subtest, error)
            if error is not None:
                self.record(test, "failed", self._exc_info_to_string(error, test))

        def addExpectedFailure(self, test, error):
            super().addExpectedFailure(test, error)
            self.record(
                test, "not-run", "expected failure: " + self._exc_info_to_string(error, test)
            )

        def addUnexpectedSuccess(self, test):
            super().addUnexpectedSuccess(test)
            self.record(test, "failed", "unexpected success")

    result = unittest.TextTestRunner(verbosity=2, resultclass=Result).run(suite)
    report.update(testsRun=result.testsRun, successful=result.wasSuccessful())
    save(path, report)
    return 0 if result.wasSuccessful() else 1


def private_session():
    output = os.environ.get("TYPELAY_VERIFY_OUTPUT")
    if not output:
        raise RuntimeError("requires verify-typelay private session")
    path = Path(output)
    session = json.loads((path / "session.json").read_text())
    for key in (
        "HOME",
        "XDG_RUNTIME_DIR",
        "NIRI_SOCKET",
        "WAYLAND_DISPLAY",
        "DBUS_SESSION_BUS_ADDRESS",
        "TYPELAY_VERIFY_XDISPLAY",
        "TYPELAY_VERIFY_SCRATCH",
    ):
        if not session.get(key) or session[key] != os.environ.get(key):
            raise RuntimeError(f"private desktop identity mismatch: {key}")
    runtime = Path(session["XDG_RUNTIME_DIR"])
    niri = Path(session["NIRI_SOCKET"])
    if not niri.is_relative_to(runtime) or not niri.is_socket():
        raise RuntimeError("private niri socket missing or outside runtime")
    if not (runtime / session["WAYLAND_DISPLAY"]).is_socket():
        raise RuntimeError("private Wayland socket missing")
    if os.environ.get("GDK_BACKEND") != "wayland" or "WAYLAND_SOCKET" in os.environ:
        raise RuntimeError("private session requires its recorded Wayland connection")
    network = json.loads((path / "network.json").read_text())
    if network.get("mode") != "offline" or network.get("namespace") != os.readlink(
        "/proc/self/ns/net"
    ):
        raise RuntimeError("discovery verification requires the recorded offline network namespace")
    return path


def records(path):
    if not path.exists():
        return []
    # The observer flushes each complete JSON line. A concurrent final partial
    # write is not yet evidence; malformed complete records remain errors.
    return [
        json.loads(line)
        for line in path.read_text().splitlines(keepends=True)
        if line.endswith("\n")
    ]


def wait_for(condition, processes, timeout, description):
    deadline = time.monotonic() + timeout
    while True:
        for process in processes:
            if process.poll() is not None:
                raise RuntimeError(
                    f"process exited before {description}: pid={process.pid}, exit={process.returncode}"
                )
        value = condition()
        if value:
            return value
        if time.monotonic() >= deadline:
            raise TimeoutError(f"deadline waiting for {description}")
        time.sleep(0.02)


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def calibrate_child(backend, path, title):
    # Check private identity before selecting the recorded Xvfb transport.
    private_session()
    if backend == "x11":
        os.environ.update(GDK_BACKEND="x11", DISPLAY=os.environ["TYPELAY_VERIFY_XDISPLAY"])
    import gi

    gi.require_version("Gtk", "3.0")
    from gi.repository import GLib, Gtk

    window = Gtk.Window(title=title)
    window.set_default_size(160, 80)
    window.add(Gtk.Label(label="TypeLay discovery observer calibration"))
    window.show_all()

    def mapped():
        if not window.get_mapped():
            return True
        identity = {"title": title, "backend": backend}
        if backend == "x11":
            gi.require_version("GdkX11", "3.0")
            from gi.repository import GdkX11  # noqa: F401

            identity["id"] = window.get_window().get_xid()
        save(path, identity)
        return False

    def close(*_):
        window.destroy()
        Gtk.main_quit()
        return False

    GLib.idle_add(mapped)
    GLib.io_add_watch(sys.stdin, GLib.IO_IN | GLib.IO_HUP, close)
    Gtk.main()
    return 0


def calibrate(directory, label, observer, events, allowed):
    for backend in ("wayland", "x11"):
        title = "typelay-discovery-" + uuid.uuid4().hex
        identity = directory / f"calibration-{label}-{backend}.json"
        command = [
            sys.executable,
            str(Path(__file__).resolve()),
            "--calibrate-child",
            backend,
            "--result",
            str(identity),
            "--title",
            title,
        ]
        with (directory / f"calibration-{label}-{backend}.log").open("w") as log:
            started = time.monotonic()
            process = subprocess.Popen(
                command, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT, text=True
            )
            try:
                wait_for(identity.exists, [observer, process], 10, f"{backend} calibration mapped")
                owned = json.loads(identity.read_text())

                def detected():
                    for record in records(events):
                        if record["monotonic"] < started:
                            continue
                        if record["kind"] == backend + "-window" and (
                            record.get("title") == title
                            if backend == "wayland"
                            else record["id"] == owned["id"]
                        ):
                            return record
                    return None

                detected_window = wait_for(
                    detected, [observer, process], 10, f"{backend} calibration observed"
                )
                allowed.append(detected_window)
                save(
                    identity,
                    {
                        **owned,
                        "command": command,
                        "observed": detected_window,
                        "zeroWindowCheck": "failed",
                        "reason": "controlled calibration opened a window",
                    },
                )
                process.stdin.write("close\n")
                process.stdin.flush()
                if process.wait(timeout=5) != 0:
                    raise RuntimeError(f"{backend} calibration did not exit successfully")
                save(
                    identity,
                    {
                        **json.loads(identity.read_text()),
                        "exitCode": process.returncode,
                        "windowProcessStopped": True,
                    },
                )
            finally:
                process.stdin.close()
                stop(process)


def source_identity(directory):
    def git(*arguments):
        return subprocess.check_output(["git", *arguments], cwd=ROOT)

    (directory / "source.diff").write_bytes(git("diff", "--binary", "HEAD"))
    paths = git("ls-files", "--cached", "--others", "--exclude-standard", "-z").decode().split("\0")
    files = {
        name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
        for name in paths
        if name and (ROOT / name).is_file()
    }
    save(
        directory / "source-identity.json",
        {
            "root": str(ROOT),
            "commit": git("rev-parse", "HEAD").decode().strip(),
            "tree": git("rev-parse", "HEAD^{tree}").decode().strip(),
            "status": git("status", "--porcelain=v1", "--untracked-files=all").decode(),
            "files": files,
        },
    )


def test_windows(events, calibration):
    # X11 can reuse an ID as soon as its previous client disconnects. Exclude
    # exactly the observed calibration events, never all events with that ID.
    return [
        item
        for item in events
        if item["kind"] in ("wayland-window", "x11-window") and item not in calibration
    ]


def run_phase(directory, phase, timeout):
    directory.mkdir()
    result = {
        "status": "failed",
        "phase": phase,
        "exitCode": None,
        "fullDiscovery": True,
        "testsStatus": "not-run",
    }
    events = directory / "windows.jsonl"
    allowed = []
    observer = None
    child = None
    command = [
        sys.executable,
        str(Path(__file__).resolve()),
        "--discover-child",
        "--result",
        str(directory / "tests.json"),
    ]
    result["command"] = command
    environment = os.environ.copy()
    if phase == "outer":
        environment = {
            key: value
            for key, value in environment.items()
            if not key.startswith(("TYPELAY_VERIFY_", "GITHUB_"))
            and key != "TYPELAY_TEST_CI_DISPLAY"
        }
        # Retain real, isolated display sockets, making an unsafe new test visible.
        environment["DISPLAY"] = os.environ["TYPELAY_VERIFY_XDISPLAY"]
    environment["TYPELAY_FINALIZE_EVIDENCE"] = str(directory / "finalize-signals")
    save(
        directory / "admission.json",
        {
            key: environment.get(key)
            for key in (
                "TYPELAY_VERIFY_OUTPUT",
                "GITHUB_ACTIONS",
                "TYPELAY_TEST_CI_DISPLAY",
                "DISPLAY",
                "WAYLAND_DISPLAY",
                "NIRI_SOCKET",
                "GDK_BACKEND",
                "XDG_RUNTIME_DIR",
                "DBUS_SESSION_BUS_ADDRESS",
            )
        },
    )
    save(directory / "result.json", result)
    try:
        with events.open("w") as log, (directory / "observer.log").open("w") as error:
            observer = subprocess.Popen(
                [sys.executable, str(Path(__file__).resolve()), "--observer-child"],
                stdin=subprocess.PIPE,
                stdout=log,
                stderr=error,
                text=True,
            )
            wait_for(
                lambda: any(item["kind"] == "ready" for item in records(events)),
                [observer],
                10,
                "both window observers ready",
            )
            calibrate(directory, "before", observer, events, allowed)
            result["observerReadyBeforeImport"] = True
            result["observerPid"] = observer.pid
            result["discoveryStartedAt"] = time.monotonic()
            save(directory / "result.json", result)
            with (directory / "unittest.log").open("w") as test_log:
                child = subprocess.Popen(
                    command, cwd=ROOT, env=environment, stdout=test_log, stderr=subprocess.STDOUT
                )
                deadline = time.monotonic() + timeout
                while child.poll() is None:
                    if observer.poll() is not None:
                        raise RuntimeError(
                            f"window observer exited during discovery: {observer.returncode}"
                        )
                    if time.monotonic() >= deadline:
                        raise TimeoutError("full discovery deadline")
                    time.sleep(0.02)
                result["exitCode"] = child.returncode
            # The second pair proves both event streams remain live, and gives
            # each stream an ordered event after the discovery process exited.
            calibrate(directory, "after", observer, events, allowed)
            if observer.poll() is not None:
                raise RuntimeError("window observer exited before observation completed")
            observer.stdin.write("stop\n")
            observer.stdin.flush()
            if observer.wait(timeout=5) != 0:
                raise RuntimeError("window observer failed while draining events")
        observed = records(events)
        if not observed or observed[-1]["kind"] != "stopped":
            raise RuntimeError("window observer did not acknowledge completion")
        windows = test_windows(observed, allowed)
        result["windows"] = windows
        tests = json.loads((directory / "tests.json").read_text())
        result["testsStatus"] = "passed" if tests["successful"] else "failed"
        gui = {
            name: item
            for name, item in tests["tests"].items()
            if name.startswith("test_settings_feedback.GtkFeedbackTests.")
        }
        result["guiTests"] = gui
        if child.returncode != 0 or not tests["successful"] or not tests["testsRun"]:
            raise RuntimeError("complete unittest discovery did not pass")
        if phase == "outer":
            if windows:
                raise RuntimeError("simulated outer discovery opened windows")
            if not gui or any(
                item["status"] != "not-run"
                or "requires verify-typelay private session" not in item.get("reason", "")
                for item in gui.values()
            ):
                raise RuntimeError("outer GUI cases must report their private-session requirement")
        elif not gui or any(item["status"] != "passed" for item in gui.values()) or not windows:
            raise RuntimeError("private GUI cases were absent, unrun, failed, or opened no windows")
        result["status"] = "passed"
    except Exception as error:
        result["reason"] = f"{type(error).__name__}: {error}"
        result["timedOut"] = isinstance(error, (TimeoutError, subprocess.TimeoutExpired))
    finally:
        cleanup_errors = []
        for process in (child, observer):
            if process is not None:
                try:
                    stop(process)
                except Exception as error:
                    cleanup_errors.append(
                        {"pid": process.pid, "reason": f"{type(error).__name__}: {error}"}
                    )
        if child is not None:
            result["exitCode"] = child.returncode
            if result["testsStatus"] == "not-run":
                result["testsStatus"] = "failed"
        if observer is not None:
            try:
                observer.stdin.close()
            except Exception as error:
                cleanup_errors.append(
                    {"pid": observer.pid, "reason": f"{type(error).__name__}: {error}"}
                )
            result["observerExitCode"] = observer.returncode
        if cleanup_errors:
            result["status"] = "failed"
            result["cleanupErrors"] = cleanup_errors
            result.setdefault("reason", "owned process cleanup failed")
        result["finishedAt"] = time.monotonic()
        save(directory / "result.json", result)
    return result


def verify(timeout):
    session = private_session()
    directory = session / "discovery"
    directory.mkdir()
    for name in ("session.json", "package-identity.json", "network.json", "invocation.json"):
        shutil.copy2(session / name, directory / name)
    source_identity(directory)
    results = {}
    for phase in ("outer", "private"):
        results[phase] = run_phase(directory / phase, phase, timeout)
        print(f"{phase}: {results[phase]['status']}", flush=True)
    save(directory / "result.json", results)
    return 0 if all(item["status"] == "passed" for item in results.values()) else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--discover-child", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument(
        "--start-directory", type=Path, default=ROOT / "tests/linux", help=argparse.SUPPRESS
    )
    parser.add_argument("--result", type=Path, help=argparse.SUPPRESS)
    parser.add_argument("--observer-child", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--calibrate-child", choices=("wayland", "x11"), help=argparse.SUPPRESS)
    parser.add_argument("--title", help=argparse.SUPPRESS)
    parser.add_argument(
        "--timeout", type=float, default=300, help="deadline in seconds for each complete discovery"
    )
    args = parser.parse_args()
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error("timeout must be positive and finite")
    if args.discover_child:
        return discover(args.start_directory, args.result)
    if args.observer_child:
        private_session()
        from window_observation import observe

        observe()
        return 0
    if args.calibrate_child:
        return calibrate_child(args.calibrate_child, args.result, args.title)
    return verify(args.timeout)


if __name__ == "__main__":
    sys.exit(main())
