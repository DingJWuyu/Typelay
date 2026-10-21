"""Read-only TSF experiment prerequisites. Never registers or activates a service."""

import argparse
import ctypes
import json
import os
import platform
import re
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

# Fixed read-only queries. No text, window titles, input simulation or app launch.
POWERSHELL = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$notepad = Join-Path $env:WINDIR 'System32\notepad.exe'
$result = [ordered]@{
    languages = @()
    languageError = $null
    notepadFile = $null
    notepadPackages = @()
    packageError = $null
    notepadProcesses = @()
}
try {
    $result.languages = @(Get-WinUserLanguageList | Select-Object -First 32 | ForEach-Object {
        [ordered]@{ languageTag = $_.LanguageTag; inputMethodTips = @($_.InputMethodTips | Select-Object -First 32) }
    })
} catch { $result.languageError = $_.Exception.GetType().FullName }
if (Test-Path -LiteralPath $notepad -PathType Leaf) {
    $file = Get-Item -LiteralPath $notepad
    $result.notepadFile = [ordered]@{
        path = $file.FullName; version = $file.VersionInfo.FileVersion
        sha256 = (Get-FileHash -LiteralPath $notepad -Algorithm SHA256).Hash
    }
}
try {
    $result.notepadPackages = @(Get-AppxPackage -Name Microsoft.WindowsNotepad | Select-Object -First 8 | ForEach-Object {
        [ordered]@{ name = $_.Name; version = $_.Version.ToString(); architecture = $_.Architecture.ToString() }
    })
} catch { $result.packageError = $_.Exception.GetType().FullName }
$result.notepadProcesses = @(Get-Process -Name notepad -ErrorAction SilentlyContinue | Select-Object -First 16 | ForEach-Object {
    try {
        [ordered]@{ pid = $_.Id; startedUtc = $_.StartTime.ToUniversalTime().ToString('o');
            imageName = [IO.Path]::GetFileName($_.Path); sameSession = ($_.SessionId -eq $sessionId); error = $null }
    } catch {
        [ordered]@{ pid = $_.Id; startedUtc = $null; imageName = $null;
            sameSession = $null; error = $_.Exception.GetType().FullName }
    }
})
$result | ConvertTo-Json -Depth 8 -Compress
"""


def admission(environment, system):
    """Environment labels are necessary, not proof of runner provenance."""
    reasons = []
    for name, expected in (
        ("GITHUB_ACTIONS", "true"),
        ("RUNNER_ENVIRONMENT", "github-hosted"),
        ("RUNNER_OS", "Windows"),
    ):
        if environment.get(name) != expected:
            reasons.append(f"{name}-not-confirmed")
    if system != "Windows":
        reasons.append("windows-required")
    return reasons


def assess(metadata, native):
    """Installation/configuration is not a functioning Chinese IME or TSF contract."""
    languages = metadata.get("languages", [])
    chinese = [row for row in languages if row.get("languageTag", "").lower().startswith("zh-")]
    return {
        "notepadInstallation": "present"
        if metadata.get("notepadFile") or metadata.get("notepadPackages")
        else "unknown",
        "candidateWindowMetadata": "present" if native.get("notepadWindows") else "unknown",
        "notepadWindow": "unknown",
        "targetIdentityVerified": False,
        "chineseProfileConfiguration": "present"
        if any(row.get("inputMethodTips") for row in chinese)
        else "unknown",
        "activeChineseImeInTarget": "unknown",
        "completeCommittedTextAndSelection": "unknown",
        "compositionObservation": "unknown",
        "editNotifications": "unknown",
        "coexistence": "unknown",
        "registrationAllowed": False,
        "writebackAllowed": False,
    }


def native_observation(pids):
    from ctypes import wintypes

    user = ctypes.WinDLL("user32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel.GetCurrentThreadId.restype = wintypes.DWORD
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.LocalFree.argtypes = [wintypes.HLOCAL]
    kernel.LocalFree.restype = wintypes.HLOCAL
    user.GetThreadDesktop.argtypes = [wintypes.DWORD]
    user.GetThreadDesktop.restype = wintypes.HANDLE
    user.GetUserObjectInformationW.argtypes = [
        wintypes.HANDLE,
        ctypes.c_int,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
    ]
    user.GetKeyboardLayout.argtypes = [wintypes.DWORD]
    user.GetKeyboardLayout.restype = wintypes.HANDLE
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user.GetWindowThreadProcessId.restype = wintypes.DWORD
    user.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    user.EnumChildWindows.argtypes = [wintypes.HWND, callback_type, wintypes.LPARAM]
    desktop = user.GetThreadDesktop(kernel.GetCurrentThreadId())
    name = ctypes.create_unicode_buffer(256)
    needed = wintypes.DWORD()
    desktop_ok = user.GetUserObjectInformationW(
        desktop, 2, name, ctypes.sizeof(name), ctypes.byref(needed)
    )
    result = {
        "desktopName": name.value if desktop_ok else None,
        "integrityCategory": "unknown",
        "notepadWindows": [],
        "windowEnumerationTruncated": False,
        "windowEnumerationIncomplete": False,
        "callbackErrorType": None,
        "targetIdentityVerified": False,
    }
    token = wintypes.HANDLE()
    advapi.OpenProcessToken.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.HANDLE),
    ]
    advapi.GetTokenInformation.argtypes = [
        wintypes.HANDLE,
        ctypes.c_int,
        ctypes.c_void_p,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
    ]
    advapi.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
    if advapi.OpenProcessToken(kernel.GetCurrentProcess(), 8, ctypes.byref(token)):
        try:
            advapi.GetTokenInformation(token, 25, None, 0, ctypes.byref(needed))
            if 0 < needed.value < 65536:
                data = ctypes.create_string_buffer(needed.value)
                if advapi.GetTokenInformation(token, 25, data, len(data), ctypes.byref(needed)):
                    sid = ctypes.cast(data, ctypes.POINTER(ctypes.c_void_p))[0]
                    value = wintypes.LPWSTR()
                    if advapi.ConvertSidToStringSidW(sid, ctypes.byref(value)):
                        try:
                            rid = int(value.value.rsplit("-", 1)[-1])
                            result["integrityCategory"] = {
                                0: "untrusted",
                                4096: "low",
                                8192: "medium",
                                8448: "medium-plus",
                                12288: "high",
                                16384: "system",
                                20480: "protected",
                            }.get(rid, "unknown")
                        finally:
                            kernel.LocalFree(ctypes.cast(value, wintypes.HLOCAL))
        finally:
            kernel.CloseHandle(token)

    def describe(hwnd):
        pid = wintypes.DWORD()
        thread = user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value not in pids:
            return True
        if len(result["notepadWindows"]) >= 128:
            result["windowEnumerationTruncated"] = True
            result["windowEnumerationIncomplete"] = True
            return False
        class_name = ctypes.create_unicode_buffer(256)
        count = user.GetClassNameW(hwnd, class_name, len(class_name))
        result["notepadWindows"].append(
            {
                "hwnd": hex(hwnd),
                "pid": pid.value,
                "threadId": thread,
                "class": class_name.value if count else None,
                "keyboardLayout": hex(user.GetKeyboardLayout(thread) or 0),
            }
        )

        return True

    def record_callback_error(error):
        result["callbackErrorType"] = type(error).__name__
        result["windowEnumerationIncomplete"] = True
        return False

    @callback_type
    def child(hwnd, _):
        try:
            return describe(hwnd)
        except Exception as error:
            return record_callback_error(error)

    @callback_type
    def top(hwnd, _):
        try:
            pid = wintypes.DWORD()
            user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value in pids:
                if not describe(hwnd):
                    return False
                user.EnumChildWindows(hwnd, child, 0)
                if result["windowEnumerationIncomplete"]:
                    return False
            return True
        except Exception as error:
            return record_callback_error(error)

    if not user.EnumWindows(top, 0) and not result["windowEnumerationIncomplete"]:
        result["windowEnumerationError"] = ctypes.get_last_error()
        result["windowEnumerationIncomplete"] = True
    return result


MARKER = "TypeLay-TSF-Preflight: true"


def push_eligible(event, head, message, changed_paths, is_ancestor):
    """Admit only this push's explicit request and actual probe source changes."""
    if event.get("ref") != "refs/heads/work/linux-usability" or event.get("deleted"):
        return False
    head_commit = event.get("head_commit") or {}
    event_message = head_commit.get("message", "")
    if MARKER not in event_message:
        return False
    before, after = event.get("before", ""), event.get("after", "")
    if not all(re.fullmatch(r"[0-9a-f]{40}", value) for value in (before, after)):
        raise ValueError("unverified-push-identity")
    if before == "0" * 40 or event.get("forced") or not is_ancestor:
        raise ValueError("unverified-push-ancestry")
    if after != head or head_commit.get("id") != head:
        raise ValueError("unverified-head")
    if event_message.rstrip("\n") != message.rstrip("\n"):
        raise ValueError("unverified-commit-message")
    paragraphs = message.strip("\n").split("\n\n")
    if (
        paragraphs[-1] != MARKER
        or message.count(MARKER) != 1
        or "```" in message
        or "~~~" in message
    ):
        raise ValueError("invalid-opt-in-marker")
    return any(path.startswith("tests/windows/tsf_probe/") for path in changed_paths)


def check_push():
    if os.environ.get("GITHUB_EVENT_NAME") != "push":
        raise ValueError("push-required")
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))

    def git(*arguments):
        return subprocess.run(
            ["git", *arguments],
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=10,
            check=True,
        ).stdout

    head = git("rev-parse", "HEAD").strip()
    message = git("show", "-s", "--format=%B", "HEAD")
    before, after = event.get("before", ""), event.get("after", "")
    if not all(re.fullmatch(r"[0-9a-f]{40}", value) for value in (before, after)):
        raise ValueError("unverified-push-identity")
    ancestry = (
        subprocess.run(
            ["git", "merge-base", "--is-ancestor", before, head], timeout=10, check=False
        ).returncode
        == 0
    )
    paths = git("diff", "--name-only", "--no-renames", "-z", before, after).split("\0")
    eligible = push_eligible(event, head, message, paths, ancestry)
    with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as output:
        output.write(f"eligible={str(eligible).lower()}\n")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    # Reserve before any observation; do not overwrite prior evidence.
    with args.output.open("x", encoding="utf-8") as output:
        result = {
            "schemaVersion": 1,
            "kind": "tsf-readonly-prerequisites",
            "runId": str(uuid.uuid4()),
            "observedAtUtc": datetime.now(timezone.utc).isoformat(),
            "status": "blocked",
            "productAcceptance": False,
            "registrationAttempts": 0,
            "activationAttempts": 0,
            "inputChanges": 0,
            "targetApplicationStarts": 0,
            "sourceTextReads": 0,
            "runner": {
                key: os.environ.get(key)
                for key in (
                    "GITHUB_RUN_ID",
                    "GITHUB_RUN_ATTEMPT",
                    "GITHUB_SHA",
                    "RUNNER_ENVIRONMENT",
                    "RUNNER_OS",
                    "RUNNER_ARCH",
                )
            },
            "blockers": admission(os.environ, platform.system()),
        }
        if not result["blockers"]:
            try:
                shell = (
                    Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
                )
                completed = subprocess.run(
                    [str(shell), "-NoProfile", "-NonInteractive", "-Command", POWERSHELL],
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="strict",
                    timeout=40,
                    check=True,
                )
                metadata = json.loads(completed.stdout.lstrip("\ufeff"))
                native = native_observation(
                    {
                        row["pid"]
                        for row in metadata["notepadProcesses"]
                        if row.get("sameSession") is True
                    }
                )
                result.update(
                    metadata=metadata,
                    native=native,
                    capabilities=assess(metadata, native),
                    osVersion=platform.version(),
                    osMachine=platform.machine(),
                )
                result["status"] = "observed-prerequisites-only"
                result["blockers"] = [
                    "active-Chinese-IME-not-demonstrated",
                    "TSF-observation-not-demonstrated",
                    "registration-requires-separate-review",
                ]
            except (
                OSError,
                ValueError,
                KeyError,
                TypeError,
                AttributeError,
                subprocess.SubprocessError,
            ) as error:
                result["blockers"] = [type(error).__name__]
        json.dump(result, output, ensure_ascii=False, indent=2)
        output.write("\n")
    # 2 intentionally means no permission to proceed; collection is not acceptance.
    return 2


if __name__ == "__main__":
    sys.exit(check_push() if sys.argv[1:] == ["--check-push"] else main())
