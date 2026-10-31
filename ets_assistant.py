#!/usr/bin/env pythonw
"""Background engine and WinUI launcher for the ETS helper scripts."""

from __future__ import annotations

import ctypes
import sys

import ets_answer_listener


def dispatch_worker(argv: list[str]) -> int:
    if not argv:
        return -1
    mode, args = argv[0], argv[1:]
    if mode == "--background-listener":
        return ets_answer_listener.main(args)
    if mode == "--answer-watch":
        import ets_auto_answer

        return ets_auto_answer.main(["--watch", *args])
    if mode in {"--converter-watch", "--convert-pack"}:
        if not args:
            return 2
        import exam_pack_to_markdown_pdf

        return exam_pack_to_markdown_pdf.main([args[0], *( ["--watch"] if mode == "--converter-watch" else [])])
    if mode == "--speaking-auto":
        import ets_speaking_auto

        return ets_speaking_auto.main(args)
    return -1


def main(argv: list[str] | None = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    worker_result = dispatch_worker(arguments)
    if worker_result >= 0:
        return worker_result
    if arguments:
        ets_answer_listener.log(f"Unsupported engine mode: {arguments[0]}")
        return 2
    if ets_answer_listener.start_assistant_window():
        return 0
    winui_exe = ets_answer_listener.SCRIPT_ROOT / "_winui" / "ETS-Assistant-WinUI.exe"
    message = (
        "找不到新版助手。请保留完整的 _winui 程序目录。"
        if not winui_exe.is_file()
        else "Windows 未能启动管理员版助手。请在用户帐户控制提示中允许启动；如果没有出现提示，请从完整的 _winui 程序目录启动。"
    )
    ctypes.windll.user32.MessageBoxW(
        None,
        message,
        "ETS 自动监听助手",
        0x10,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
