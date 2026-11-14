#!/usr/bin/env python3
"""Drive a speaking-question page: start recording, play the pre-generated TTS
WAV into the virtual cable, stop, and advance to the next question.

The page must be a speaking practice/exam page that uses the native iFlytek
recorder (buttons: .btn-play, .btn-stopRecord, text 下一步 / 练习结束).
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import subprocess
import sys
import time
import wave
from pathlib import Path

from ets_auto_answer import CDPClient, CDPError, devtools_targets

APP_ROOT = (
    Path(sys.executable).resolve().parent
    if getattr(sys, "frozen", False)
    else Path(__file__).resolve().parent
)
DATA_ROOT = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming")) / "ETS"
LOG_PATH = DATA_ROOT / "ets_speaking_auto.log"
CABLE_PLAYER = next(
    (path for path in (
        APP_ROOT / "tools" / "CablePlayer" / "CablePlayer.exe",
        APP_ROOT / ".build" / "tools" / "cableplayer" / "out" / "CablePlayer.exe",
    ) if path.is_file()),
    APP_ROOT / "tools" / "CablePlayer" / "CablePlayer.exe",
)
PLAYER_SPEED_FACTOR = 1.0


def find_session(explicit: str | None, data_root: Path) -> Path:
    if explicit:
        return Path(explicit)
    candidates = [
        data_root / "_assistant" / "speaking_session.json",
        data_root / "temp" / "speaking_session.json",
    ]
    for path in candidates:
        if path.is_file():
            return path
    raise FileNotFoundError("speaking_session.json not found")


def cleanup_temp_audio(root: Path, session_wavs: list[str]) -> None:
    """Remove only generated files used by this session, never unrelated WAVs."""
    removed = 0
    total = 0
    folders = {(root / "temp" / "tts-test").resolve(), (APP_ROOT / "temp" / "tts-test").resolve()}
    for path in {Path(name).resolve() for name in session_wavs}:
        if path.parent not in folders or not path.name.startswith("aliyun-tts-") or path.suffix.lower() != ".wav":
            continue
        try:
            if path.is_file():
                size = path.stat().st_size
                path.unlink()
                total += size
                removed += 1
        except OSError:
            pass
    log(f"cleanup: removed {removed} session audio file(s), {total / 1024 / 1024:.1f} MB")


def log(message: str) -> None:
    try:
        LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
        with LOG_PATH.open("a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%H:%M:%S')} {message}\n")
    except OSError:
        pass
    if sys.stdout is not None:
        try:
            print(message, flush=True)
        except OSError:
            pass


def wav_duration_seconds(path: str | Path) -> float:
    """Return the actual PCM duration so a detached player process cannot end early."""
    try:
        with wave.open(str(path), "rb") as audio:
            sample_rate = audio.getframerate()
            if sample_rate <= 0:
                return 0.0
            return max(0.0, audio.getnframes() / sample_rate)
    except (OSError, wave.Error):
        return 0.0


MAX_PLAYBACK_SPEED = 1.5


class TimingBudgetError(ValueError):
    pass


def usable_recording_seconds(seconds: float) -> float:
    return max(0.0, seconds - min(1.25, seconds * 0.15))


def playback_speed_factor(path: str | Path, available_seconds: float | None = None) -> float:
    """Smallest 0.01x step that fits; retain natural speed whenever possible."""
    duration = wav_duration_seconds(path)
    if available_seconds is None:
        return PLAYER_SPEED_FACTOR
    usable = usable_recording_seconds(available_seconds)
    if duration <= 0:
        raise TimingBudgetError("Invalid audio")
    if not math.isfinite(usable) or usable <= 0:
        return MAX_PLAYBACK_SPEED
    speed = max(1.0, math.ceil((duration / usable - 1e-9) * 100) / 100)
    return min(speed, MAX_PLAYBACK_SPEED)


def read_recording_limits(data_root: Path, pack_id: str, count: int) -> list[float]:
    if not re.fullmatch(r"[0-9]+", pack_id):
        return []
    candidates = []
    for path in (data_root / pack_id).glob("template_*/ctrl.json"):
        try:
            rows = json.loads(path.read_text(encoding="utf-8-sig"))
            ordered = sorted((r for r in rows if r.get("filetype") == "action.record"),
                             key=lambda r: float(r.get("order", "inf")))
            limits = [float(r["playtime"]) for r in ordered]
            if len(limits) == count and all(math.isfinite(x) and 0 < x <= 600 for x in limits):
                candidates.append(limits)
        except (OSError, ValueError, TypeError, KeyError):
            continue
    return candidates[0] if candidates and all(x == candidates[0] for x in candidates) else []


def recording_budget(state: dict, declared_seconds: float | None) -> float | None:
    remaining = state.get("recording_remaining_seconds")
    if isinstance(remaining, (int, float)) and math.isfinite(remaining) and remaining >= 0:
        return min(remaining, declared_seconds) if declared_seconds is not None else remaining
    return declared_seconds


def request_timing_regeneration(session_path: Path, session: dict, index: int, seconds: float) -> None:
    request_path = Path(str(session_path) + ".timing-request.json")
    temporary = Path(str(request_path) + ".tmp")
    temporary.write_text(json.dumps({"PackId": str(session.get("PackId")), "Index": index,
                                     "RecordingSeconds": seconds}), encoding="utf-8")
    temporary.replace(request_path)
    log(f"[{index + 1}/{len(session['Items'])}] 音频在 1.50x 内仍无法适配 {seconds:.1f}s 录音时间；已请求重新生成更短版本，本轮暂停，不跳题。")


def playback_duration_seconds(path: str | Path, speed_factor: float | None = None) -> float:
    """Return the expected playback time after the player's time stretching."""
    speed = speed_factor if speed_factor is not None else playback_speed_factor(path)
    return wav_duration_seconds(path) / speed


def terminate_player(player: subprocess.Popen[str] | None) -> None:
    if player is None or player.poll() is not None:
        return
    try:
        player.terminate()
        player.wait(timeout=2.0)
    except subprocess.TimeoutExpired:
        try:
            player.kill()
            player.wait(timeout=2.0)
        except (OSError, subprocess.TimeoutExpired):
            pass
    except OSError:
        pass


def finished_player_status(player: subprocess.Popen[str]) -> tuple[bool, str] | None:
    """Check the player's terminal message; elapsed WAV time alone cannot prove output."""
    if player.poll() is None:
        return None
    try:
        output, _ = player.communicate(timeout=0.2)
    except subprocess.TimeoutExpired:
        return None
    output = output or ""
    lowered = output.casefold()
    markers = [
        marker
        for marker in ("PLAY", "DONE", "TIMEOUT", "media failed", "no VB-Audio render device", "missing:")
        if marker.casefold() in lowered
    ]
    error_lines = [
        line.strip()
        for line in output.splitlines()
        if any(marker in line.casefold() for marker in (
            "media failed", "timeout", "no vb-audio render device", "missing:",
            "waveout", "invalid wav", "unsupported wav", "playback error",
        ))
    ]
    failed = (
        player.returncode != 0
        or "DONE" not in output
        or "TIMEOUT" in output
        or "media failed" in lowered
        or "no VB-Audio render device" in lowered
        or "missing:" in lowered
    )
    status = " | ".join(error_lines[-3:]) if failed and error_lines else ",".join(markers)
    return failed, status or f"exit={player.returncode}; no DONE marker"


PAGE_STATE_JS = r"""(()=>{
 const docs=[document];
 try {const d=document.querySelector('iframe.examIframe')?.contentDocument;if(d)docs.push(d);} catch{}
 const visible=e=>!!e&&e.getClientRects().length>0&&e.ownerDocument.defaultView.getComputedStyle(e).visibility!=='hidden';
 const text=docs.map(d=>d.body?.innerText||'').join('\n');
 const phase=/(?:录音中|正在录音|recording)/i;
 let remaining=null;
 for(const doc of docs) {
   for(const e of doc.querySelectorAll('span,div,p,label')) {
     const own=(e.innerText||'').trim();
     if(!visible(e)||own.length>90||!phase.test(own))continue;
     let node=e;
     for(let depth=0;node&&depth<3;node=node.parentElement,depth++) {
       const value=(node.innerText||'').trim();
       if(value.length>200)break;
       const marker=value.match(phase);
       if(!marker)continue;
       const tail=value.slice(marker.index+marker[0].length).trim();
       const clock=tail.match(/^[^\d]{0,12}(\d{1,2}):(\d{2})(?:[^\d]|$)/);
       const secs=tail.match(/^[^\d]{0,12}(\d{1,3}(?:\.\d+)?)(?:\s*(?:秒|s(?:ec(?:onds)?)?)|\s*$)/i);
       const n=clock?Number(clock[1])*60+Number(clock[2]):secs?Number(secs[1]):null;
       if(n!==null&&n>=0&&n<=600){remaining=n;break;}
     }
     if(remaining!==null)break;
   }
   if(remaining!==null)break;
 }
 const buttons=docs.flatMap(d=>[...d.querySelectorAll('button')]);
 return JSON.stringify({text,
   stop:buttons.some(b=>b.matches('.btn-stopRecord')&&visible(b)),
   play:buttons.some(b=>b.matches('.btn-play')&&visible(b)),
   exam:docs.some(d=>!!d.querySelector('.exam-panel,.exam-progress,video.examVideo')),
   recording:/(?:^|\n)\s*(?:录音中|正在录音|recording)(?=\s|\d|$)/im.test(text),
   recording_remaining_seconds:remaining,
   fix:docs.some(d=>[...d.querySelectorAll('button,span')].some(e=>visible(e)&&e.innerText?.trim()==='订正')),
   next:buttons.some(b=>visible(b)&&b.innerText.trim()==='下一步'&&!b.disabled)
 });
})()"""


def page_state(client: CDPClient) -> dict:
    return json.loads(client.evaluate(PAGE_STATE_JS))


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


def click_fix(client: CDPClient) -> bool:
    return client.evaluate(
        "(()=>{const el=[...document.querySelectorAll('button,span')].find(e=>e.offsetParent!==null&&e.innerText&&e.innerText.trim()==='订正'&&e.children.length===0);"
        "if(!el)return false;const b=el.closest('button');(b||el).click();return true;})()"
    ) is True


def _main(argv: list[str] | None = None) -> int:
    global DATA_ROOT, LOG_PATH
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", default=None)
    parser.add_argument("--data-root", type=Path, default=DATA_ROOT)
    parser.add_argument("--log", type=Path, default=None)
    parser.add_argument("--start-index", type=int, default=0)
    parser.add_argument("--player", default=str(CABLE_PLAYER))
    parser.add_argument("--max-question-seconds", type=float, default=90)
    parser.add_argument("--keep-temp", action="store_true", help="keep generated WAVs after the run")
    args = parser.parse_args(argv)

    DATA_ROOT = args.data_root.resolve()
    LOG_PATH = args.log.resolve() if args.log else DATA_ROOT / "ets_speaking_auto.log"
    session_path = find_session(args.session, DATA_ROOT)
    session = json.loads(session_path.read_text(encoding="utf-8-sig"))
    wavs = [item["WavPath"] for item in session.get("Items", [])]
    if not wavs:
        log("session has no WAV files")
        return 1
    if not Path(args.player).is_file():
        log(f"口语音频播放器不存在：{Path(args.player).name}；已在开始录音前中止。")
        return 6
    invalid_wavs = [
        (index + 1, Path(path).name)
        for index, path in enumerate(wavs)
        if not Path(path).is_file() or wav_duration_seconds(path) <= 0
    ]
    if invalid_wavs:
        details = ", ".join(f"第 {index} 题 ({name})" for index, name in invalid_wavs[:5])
        if len(invalid_wavs) > 5:
            details += f" 等 {len(invalid_wavs)} 题"
        log(f"口语音频文件已缺失或无效：{details}；已在 ETS 开始录音前中止，没有跳题。请重新生成音频。")
        return 6
    limits = read_recording_limits(DATA_ROOT, str(session.get("PackId")), len(wavs))
    budgets = []
    for i, item in enumerate(session["Items"]):
        declared = limits[i] if limits else item.get("RecordingSeconds")
        budget = float(declared) if isinstance(declared, (int, float)) and 0 < declared <= 600 else None
        budgets.append(budget)
        try:
            playback_speed_factor(wavs[i], budget)
        except TimingBudgetError:
            request_timing_regeneration(session_path, session, i, budget or 0)
            return 10
    log(f"session pack={session.get('PackId')} items={len(wavs)} mode={session.get('Mode')} start={max(0, args.start_index)}")

    targets = devtools_targets()
    if not targets:
        log("ETS DevTools page not found (is ETS running with port 10086?)")
        return 2
    client = CDPClient(targets[0]["webSocketDebuggerUrl"])
    page_url = client.evaluate("location.href")
    match = re.search(r"[?&#]set_id=(\d+)", page_url)
    page_pack = match.group(1) if match else ""
    mock_exam = re.search(r"#/modelTestMode(?:[/?#]|$)", page_url, re.IGNORECASE) is not None
    if page_pack and str(session.get("PackId")) != page_pack:
        log(f"session pack {session.get('PackId')} does not match page pack {page_pack}")
        client.close()
        return 3
    player: subprocess.Popen[str] | None = None
    index = max(0, args.start_index)
    started_at = 0.0
    expected_playback_end = 0.0
    current_speed_factor = PLAYER_SPEED_FACTOR
    playback_timeout_at = 0.0
    playback_wait_logged = False
    player_done_logged = False
    finished = False
    played = 0
    fix_attempts = 0
    mock_waiting_for_recording_end = False
    recording_missing_since = None

    initial = page_state(client)
    log(f"page={page_url[:160]}")
    log(f"mock={mock_exam} exam={initial['exam']} play={initial['play']} fix={initial['fix']} text={initial['text'][:120]!r}")
    if not mock_exam and not initial["exam"] and not initial["fix"]:
        log("当前页面不是口语练习/答题页，未播放音频，保留 WAV。请先打开一道口语题再点开始自动口语。")
        client.close()
        return 4

    try:
        while True:
            state = page_state(client)
            text = state["text"]
            active_recording = state["recording"] if mock_exam else state["stop"]
            if player is not None and player.poll() is None and not active_recording:
                if recording_missing_since is None:
                    recording_missing_since = time.monotonic()
                if time.monotonic() - recording_missing_since < 0.5:
                    time.sleep(0.1)
                    continue
            else:
                recording_missing_since = None
            player_status = finished_player_status(player) if player is not None else None
            if player_status is not None and player_status[0]:
                return_code = player.returncode
                if not mock_exam and state["stop"]:
                    click_stop(client)
                log(
                    f"音频播放器未确认成功输出 (code={return_code}, status={player_status[1]})；"
                    "已中止自动口语，不会推进下一题。"
                )
                return 7
            if player_status is not None and not player_done_logged:
                remaining = expected_playback_end - time.monotonic()
                if remaining > 0.75:
                    if not mock_exam and state["stop"]:
                        click_stop(client)
                    log(
                        f"音频播放器在 WAV 预计时长前退出约 {remaining:.1f}s "
                        f"(code={player.returncode}, status={player_status[1]})；"
                        "已中止自动口语，不推进下一题。"
                    )
                    return 7
                log(f"CablePlayer completed successfully (status={player_status[1]})")
                player_done_logged = True
            audio_finished = (
                player is None
                or (time.monotonic() >= expected_playback_end and player.poll() is not None)
            )
            if audio_finished and ("练习结束" in text or "提交成功" in text) and (
                not mock_exam or (played > 0 and not state["recording"])
            ):
                if state["fix"] and fix_attempts < 2:
                    fix_attempts += 1
                    log("练习已结束，点击“订正”重新开始。")
                    click_fix(client)
                    time.sleep(2.0)
                    continue
                if index < len(wavs):
                    log(f"Practice ended before all speaking items were played ({index}/{len(wavs)}); retaining session audio.")
                    return 11
                log("practice finished")
                finished = True
                break
            if mock_exam:
                # Simulation exams control the recorder themselves. They have no
                # .btn-play/.btn-stopRecord buttons, so wait for their visible
                # recording phase and let ETS advance after its timer expires.
                if state["recording"]:
                    if player is None and not mock_waiting_for_recording_end and index < len(wavs):
                        started_at = time.monotonic()
                        duration = wav_duration_seconds(wavs[index])
                        budget = recording_budget(state, budgets[index])
                        try:
                            current_speed_factor = playback_speed_factor(wavs[index], budget)
                        except TimingBudgetError:
                            request_timing_regeneration(session_path, session, index, budget or 0)
                            return 10
                        playback_duration = duration / current_speed_factor
                        expected_playback_end = started_at + playback_duration + 0.25
                        playback_timeout_at = started_at + max(args.max_question_seconds, playback_duration + 10.0)
                        playback_wait_logged = False
                        player_done_logged = False
                        played += 1
                        log(
                            f"[{index + 1}/{len(wavs)}] mock recording, playing {Path(wavs[index]).name} "
                            f"({duration:.1f}s source, {playback_duration:.1f}s at {current_speed_factor:.2f}x; recording budget={budget}s)"
                        )
                        player = subprocess.Popen(
                            [args.player, "--speed", f"{current_speed_factor:.2f}", wavs[index]],
                            stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT,
                            text=True,
                            encoding="utf-8",
                            errors="replace",
                        )
                    elif (
                        player is not None
                        and player.poll() is not None
                        and time.monotonic() >= expected_playback_end
                    ):
                        log(f"[{index + 1}/{len(wavs)}] playback ended; waiting for ETS recording to end")
                        player = None
                        mock_waiting_for_recording_end = True
                    elif player is not None and time.monotonic() > playback_timeout_at:
                        terminate_player(player)
                        log(f"[{index + 1}/{len(wavs)}] 播放超时；已终止本轮自动口语，不推进下一题。")
                        return 8
                    time.sleep(0.2)
                    continue
                if player is not None:
                    now = time.monotonic()
                    playback_complete = now >= expected_playback_end and player.poll() is not None
                    if playback_complete:
                        log(f"[{index + 1}/{len(wavs)}] playback ended; ETS recording already ended")
                        player = None
                        mock_waiting_for_recording_end = True
                    else:
                        elapsed = max(0.0, now - started_at)
                        terminate_player(player)
                        log(
                            f"[{index + 1}/{len(wavs)}] ETS 录音窗口在音频结束前关闭 "
                            f"(已播放约 {elapsed:.1f}/{playback_duration_seconds(wavs[index], current_speed_factor):.1f}s，"
                            f"原始音频 {wav_duration_seconds(wavs[index]):.1f}s，{current_speed_factor:.2f}x)；"
                            "录音已结束，停止播放并继续。"
                        )
                        player = None
                        mock_waiting_for_recording_end = True
                if mock_waiting_for_recording_end:
                    index += 1
                    mock_waiting_for_recording_end = False
                    log(f"[{index}/{len(wavs)}] mock recording ended")
                if index >= len(wavs):
                    log("all WAV items consumed")
                    finished = True
                    break
                time.sleep(0.3)
                continue
            if state["stop"]:
                if player is None and index < len(wavs):
                    started_at = time.monotonic()
                    duration = wav_duration_seconds(wavs[index])
                    budget = recording_budget(state, budgets[index])
                    try:
                        current_speed_factor = playback_speed_factor(wavs[index], budget)
                    except TimingBudgetError:
                        request_timing_regeneration(session_path, session, index, budget or 0)
                        return 10
                    playback_duration = duration / current_speed_factor
                    expected_playback_end = started_at + playback_duration + 0.25
                    playback_timeout_at = started_at + max(args.max_question_seconds, playback_duration + 10.0)
                    playback_wait_logged = False
                    player_done_logged = False
                    played += 1
                    log(
                        f"[{index + 1}/{len(wavs)}] recording, playing {Path(wavs[index]).name} "
                        f"({duration:.1f}s source, {playback_duration:.1f}s at {current_speed_factor:.2f}x; recording budget={budget}s)"
                    )
                    player = subprocess.Popen(
                        [args.player, "--speed", f"{current_speed_factor:.2f}", wavs[index]],
                        stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT,
                        text=True,
                        encoding="utf-8",
                        errors="replace",
                    )
                if (
                    player is not None
                    and player.poll() is not None
                    and time.monotonic() >= expected_playback_end
                ):
                    time.sleep(0.8)
                    click_stop(client)
                    log(f"[{index + 1}/{len(wavs)}] stopped")
                    player = None
                    index += 1
                    time.sleep(1.5)
                elif player is not None and time.monotonic() > playback_timeout_at:
                    terminate_player(player)
                    click_stop(client)
                    log(f"[{index + 1}/{len(wavs)}] 播放超时；已中止本轮自动口语，不推进下一题。")
                    return 8
                time.sleep(0.1)
                continue

            # Not recording: if a recording was auto-stopped, close the player.
            if player is not None:
                now = time.monotonic()
                if now >= expected_playback_end and player.poll() is not None:
                    log(f"[{index + 1}/{len(wavs)}] playback ended; ETS recording already ended")
                    player = None
                    index += 1
                    time.sleep(1.0)
                    continue
                elapsed = max(0.0, now - started_at)
                terminate_player(player)
                log(
                    f"[{index + 1}/{len(wavs)}] ETS 录音窗口在音频结束前关闭 "
                    f"(已播放约 {elapsed:.1f}/{playback_duration_seconds(wavs[index], current_speed_factor):.1f}s，"
                    f"原始音频 {wav_duration_seconds(wavs[index]):.1f}s，{current_speed_factor:.2f}x)；"
                    "录音已结束，停止播放并继续。"
                )
                player = None
                log(f"[{index + 1}/{len(wavs)}] recording ended")
                index += 1
                time.sleep(1.0)
                continue

            if index >= len(wavs):
                log("all WAV items consumed")
                finished = True
                break
            if state["next"]:
                click_next(client)
                time.sleep(1.6)
            elif state["play"]:
                click_play(client)
                time.sleep(0.1)
            else:
                time.sleep(0.5)
    finally:
        terminate_player(player)
        client.close()
    if finished and played > 0 and not args.keep_temp:
        cleanup_temp_audio(DATA_ROOT, wavs)
    elif played == 0:
        log("本次没有实际播放音频，保留 temp WAV 以便重试。")
    return 0


def main(argv: list[str] | None = None) -> int:
    try:
        return _main(argv)
    except (OSError, CDPError, ValueError, TypeError) as exc:
        log(f"自动口语已停止：ETS 页面连接中断或状态无效 ({exc})。可以返回 ETS 后重新开始。")
        return 5


if __name__ == "__main__":
    raise SystemExit(main())
