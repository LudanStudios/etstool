#!/usr/bin/env pythonw
"""Open a visible answer-monitor terminal whenever the ETS desktop app starts."""

from __future__ import annotations

import ctypes
import os
import shutil
import subprocess
import sys
import time
from ctypes import wintypes
from pathlib import Path


ETS_EXES = {
    "etsshell.exe",
    "ets.exe",
}
ETS_PATHS = {
    os.path.normcase(os.path.normpath(r"C:\Program Files (x86)\ETS\ETSShell.exe")),
    os.path.normcase(os.path.normpath(r"C:\Program Files (x86)\ETS\Ets.exe")),
}
PACK_ROOT = Path(__file__).resolve().parent
CONVERTER = PACK_ROOT / "exam_pack_to_markdown_pdf.py"
AUTO_ANSWER = PACK_ROOT / "ets_auto_answer.py"
LOG_PATH = PACK_ROOT / "ets_answer_listener.log"
TH32CS_SNAPPROCESS = 0x00000002
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", wintypes.WCHAR * 260),
    ]


def log(message: str) -> None:
    try:
        with LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} {message}\n")
    except OSError:
        pass


def ets_is_running() -> bool:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)
    ]
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot == INVALID_HANDLE_VALUE:
        return False
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)
        ok = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.casefold() in ETS_EXES:
                handle = kernel32.OpenProcess(
                    PROCESS_QUERY_LIMITED_INFORMATION, False, entry.th32ProcessID
                )
                if not handle:
                    return True
                try:
                    buffer = ctypes.create_unicode_buffer(32768)
                    length = wintypes.DWORD(len(buffer))
                    if not kernel32.QueryFullProcessImageNameW(
                        handle, 0, buffer, ctypes.byref(length)
                    ):
                        return True
                    path = os.path.normcase(os.path.normpath(buffer.value))
                    if path in ETS_PATHS:
                        return True
                finally:
                    kernel32.CloseHandle(handle)
            ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
        return False
    finally:
        kernel32.CloseHandle(snapshot)


def powershell_executable() -> str:
    for candidate in ("pwsh.exe", "powershell.exe"):
        found = shutil.which(candidate)
        if found:
            return found
    return "powershell.exe"


def console_python() -> str:
    executable = Path(sys.executable)
    if executable.name.casefold() == "pythonw.exe":
        console_version = executable.with_name("python.exe")
        if console_version.exists():
            return str(console_version)
    return str(executable)


def start_answer_terminal() -> subprocess.Popen[bytes]:
    shell = powershell_executable()
    python = console_python()
    command = f'& "{python}" "{CONVERTER}" "{PACK_ROOT}" --watch'
    return subprocess.Popen(
        [shell, "-NoExit", "-Command", command],
        cwd=str(PACK_ROOT),
        creationflags=subprocess.CREATE_NEW_CONSOLE,
    )


def start_auto_answer_worker() -> subprocess.Popen[bytes] | None:
    if not AUTO_ANSWER.is_file():
        log(f"Auto-answer worker is missing: {AUTO_ANSWER.name}")
        return None
    try:
        log_handle = LOG_PATH.open("a", encoding="utf-8")
        process = subprocess.Popen(
            [console_python(), str(AUTO_ANSWER), "--watch"],
            cwd=str(PACK_ROOT),
            stdin=subprocess.DEVNULL,
            stdout=log_handle,
            stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        # Popen inherits this handle; closing the parent's copy does not stop logging.
        log_handle.close()
        log("Auto-answer worker started; it will not click a final-submit button.")
        return process
    except OSError as exc:
        log(f"Could not start auto-answer worker: {exc}")
        return None


def stop_process(process: subprocess.Popen[bytes] | None, label: str, *, tree: bool = False) -> None:
    if process is None or process.poll() is not None:
        return
    try:
        if tree:
            # The visible PowerShell terminal also owns the converter watcher.
            subprocess.run(
                ["taskkill.exe", "/PID", str(process.pid), "/T", "/F"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=5,
                creationflags=subprocess.CREATE_NO_WINDOW,
                check=False,
            )
        else:
            process.terminate()
        process.wait(timeout=3)
    except (OSError, subprocess.TimeoutExpired):
        try:
            process.kill()
            process.wait(timeout=2)
        except (OSError, subprocess.TimeoutExpired):
            log(f"Could not fully stop {label} (PID {process.pid})")
            return
    log(f"Stopped {label} (PID {process.pid})")


def main() -> int:
    if os.name != "nt":
        return 1
    if not CONVERTER.is_file():
        return 1
    log(f"Listener started; watching process names: {', '.join(sorted(ETS_EXES))}")
    was_running = False
    terminal: subprocess.Popen[bytes] | None = None
    worker: subprocess.Popen[bytes] | None = None
    while True:
        running = ets_is_running()
        if running and not was_running and (terminal is None or terminal.poll() is not None):
            try:
                terminal = start_answer_terminal()
                log(f"Opened answer terminal for {', '.join(sorted(ETS_EXES))}")
            except OSError as exc:
                log(f"Could not open answer terminal: {exc}")
                terminal = None
        if running and (worker is None or worker.poll() is not None):
            worker = start_auto_answer_worker()
        if not running and was_running:
            stop_process(worker, "auto-answer listener")
            stop_process(terminal, "answer terminal and converter watcher", tree=True)
            worker = None
            terminal = None
            log("ETS exited; answer processes stopped. Waiting for ETS to start again.")
        was_running = running
        time.sleep(0.5)


if __name__ == "__main__":
    raise SystemExit(main())
