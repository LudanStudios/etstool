#!/usr/bin/env python3
"""Drive a speaking-question page: start recording, play the pre-generated TTS
WAV into the virtual cable, stop, and advance to the next question.

The page must be a speaking practice/exam page that uses the native iFlytek
recorder (buttons: .btn-play, .btn-stopRecord, text 下一步 / 练习结束).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

from ets_auto_answer import CDPClient, devtools_targets

ROOT = Path(__file__).resolve().parent
CABLE_PLAYER = next(
    (path for path in (
        ROOT / "tools" / "CablePlayer" / "CablePlayer.exe",
        ROOT / ".build" / "tools" / "cableplayer" / "out" / "CablePlayer.exe",
    ) if path.is_file()),
    ROOT / "tools" / "CablePlayer" / "CablePlayer.exe",
)


def find_session(explicit: str | None) -> Path:
    if explicit:
        return Path(explicit)
    candidates = [
        ROOT / "temp" / "speaking_session.json",
        Path.home() / "AppData" / "Roaming" / "ETS" / "_assistant" / "speaking_session.json",
    ]
    for path in candidates:
        if path.is_file():
            return path
    raise FileNotFoundError("speaking_session.json not found")


def cleanup_temp_audio(root: Path) -> None:
    """Delete generated TTS WAVs and the virtual-mic copy after a finished run."""
    removed = 0
    total = 0
    for folder in (root / "temp" / "tts-test",):
        if not folder.is_dir():
            continue
        for wav in folder.glob("*.wav"):
            try:
                total += wav.stat().st_size
                wav.unlink()
                removed += 1
            except OSError:
                pass
    speaking_input = Path(os.environ.get("APPDATA", "")) / "ETS" / "_assistant" / "speaking_input.wav"
    try:
        if speaking_input.is_file():
            total += speaking_input.stat().st_size
            speaking_input.unlink()
            removed += 1
    except OSError:
        pass
    print(f"cleanup: removed {removed} audio file(s), {total / 1024 / 1024:.1f} MB", flush=True)


def page_state(client: CDPClient) -> dict:
    raw = client.evaluate(
        "JSON.stringify({"
        "text:document.body.innerText,"
        "stop:!!document.querySelector('button.btn-stopRecord'),"
        "play:!!document.querySelector('button.btn-play'),"
        "next:[...document.querySelectorAll('button')].some(b=>b.offsetParent!==null&&b.innerText.trim()==='下一步'&&!b.disabled)"
        "})"
    )
    return json.loads(raw)


def click_next(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const b=[...document.querySelectorAll('button')].find(e=>e.offsetParent!==null&&e.innerText.trim()==='下一步'&&!e.disabled);"
        "if(!b)return false;b.click();return true;})()"
    ) is True


def click_play(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const b=document.querySelector('button.btn-play');if(!b)return false;b.click();return true;})()"
    ) is True


def click_stop(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const b=document.querySelector('button.btn-stopRecord');if(!b)return false;b.click();return true;})()"
    ) is True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", default=None)
    parser.add_argument("--start-index", type=int, default=0)
    parser.add_argument("--player", default=str(CABLE_PLAYER))
    parser.add_argument("--max-question-seconds", type=float, default=90)
    parser.add_argument("--keep-temp", action="store_true", help="keep generated WAVs after the run")
    args = parser.parse_args(argv)

    session = json.loads(find_session(args.session).read_text(encoding="utf-8-sig"))
    wavs = [item["WavPath"] for item in session.get("Items", [])]
    if not wavs:
        print("session has no WAV files", file=sys.stderr)
        return 1
    print(f"session pack={session.get('PackId')} items={len(wavs)} mode={session.get('Mode')}", flush=True)

    targets = devtools_targets()
    if not targets:
        print("ETS DevTools page not found (is ETS running with port 10086?)", file=sys.stderr)
        return 2
    client = CDPClient(targets[0]["webSocketDebuggerUrl"])
    page_url = client.evaluate("location.href")
    match = re.search(r"[?&#]set_id=(\d+)", page_url)
    page_pack = match.group(1) if match else ""
    if page_pack and str(session.get("PackId")) != page_pack:
        print(f"session pack {session.get('PackId')} does not match page pack {page_pack}", file=sys.stderr)
        client.close()
        return 3
    player: subprocess.Popen[str] | None = None
    index = max(0, args.start_index)
    started_at = 0.0
    finished = False

    try:
        while True:
            state = page_state(client)
            text = state["text"]
            if "练习结束" in text or "提交成功" in text:
                print("practice finished", flush=True)
                finished = True
                break
            if state["stop"]:
                if player is None and index < len(wavs):
                    started_at = time.time()
                    print(f"[{index + 1}/{len(wavs)}] recording, playing {Path(wavs[index]).name}", flush=True)
                    player = subprocess.Popen(
                        [args.player, wavs[index]],
                        stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT,
                        text=True,
                        encoding="utf-8",
                        errors="replace",
                    )
                if player is not None and player.poll() is not None:
                    time.sleep(0.8)
                    click_stop(client)
                    print(f"[{index + 1}/{len(wavs)}] stopped", flush=True)
                    player = None
                    index += 1
                    time.sleep(1.5)
                elif player is not None and time.time() - started_at > args.max_question_seconds:
                    player.terminate()
                    player = None
                    click_stop(client)
                    index += 1
                    time.sleep(1.5)
                continue

            # Not recording: if a recording was auto-stopped, close the player.
            if player is not None:
                player.terminate()
                player = None
                index += 1
                time.sleep(1.0)
                continue

            if index >= len(wavs):
                print("all WAV items consumed", flush=True)
                finished = True
                break
            if state["next"]:
                click_next(client)
                time.sleep(1.6)
            elif state["play"]:
                click_play(client)
                time.sleep(2.0)
            else:
                time.sleep(0.5)
    finally:
        if player is not None:
            player.terminate()
        client.close()
    if finished and not args.keep_temp:
        cleanup_temp_audio(ROOT)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
