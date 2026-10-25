#!/usr/bin/env python3
"""Fill visible ETS multiple-choice questions from a local exam-pack JSON.

The worker talks only to the ETS page's loopback Chrome DevTools endpoint and
uses the page's own click handlers for choices. It never clicks a final-submit button;
the ETS page may still advance or submit automatically after the last question.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.client
import json
import os
import random
import re
import socket
import struct
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse, urlsplit

from ets_activity import clean_text, record_answer


DEFAULT_ROOT = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming")) / "ETS"
DEFAULT_DEVTOOLS = "http://127.0.0.1:10086/json/list"
DEFAULT_CONFIG = Path(__file__).resolve().parent / "ets_answer_config.yaml"
FREE_LISTENING_WORKER_URL = "https://ets.azursmelt.us.ci"
RESET_REQUEST_FILE = "reset_request.json"
RESET_ACK_FILE = "reset_ack.json"
APP_TITLE = "E听说"
NEXT_CONFIRM_SECONDS = 1.2
MIN_SKIP_CONFIRM_SECONDS = 0.15
WATCH_INTERVAL_SECONDS = 0.15
LAST_NEXT_CLICK_AT = 0.0
QUESTION_JS = r"""(()=>{
 const f=document.querySelector('iframe.examIframe'),d=f?.contentDocument;
 if(!d)return JSON.stringify({ready:false});
 const qs=[...d.querySelectorAll('.question2')].filter(x=>x.getBoundingClientRect().height>0&&/^\d+_\d+$/.test(x.id));
 if(!qs.length)return JSON.stringify({ready:false});
 const fr=f.getBoundingClientRect();
 const questions=qs.map(q=>({id:q.id,options:[...q.querySelectorAll('.choose2')].map(e=>{
   const t=e.querySelector('.choose2_text')||e,r=t.getBoundingClientRect();
   return {id:e.id,cls:e.className,text:t.innerText,rect:{x:fr.left+r.left,y:fr.top+r.top,w:r.width,h:r.height}};
 })}));
 return JSON.stringify({ready:true,questions});
})()"""
CHECK_JS = r"""(id,index)=>{
 const d=document.querySelector('iframe.examIframe')?.contentDocument;
 const q=[...(d?.querySelectorAll('.question2')||[])].find(x=>x.id===id);
 const e=q?.querySelectorAll('.choose2')[index];
 return JSON.stringify({id:q?.id,cls:e?.className,selected:/selected/.test(e?.className||'')});
}"""


class CDPError(RuntimeError):
    pass


class CDPClient:
    """Small standard-library Chrome DevTools WebSocket client."""

    def __init__(self, websocket_url: str, timeout: float = 5.0):
        parsed = urlparse(websocket_url)
        if parsed.scheme != "ws" or parsed.hostname not in {"127.0.0.1", "localhost", "::1"}:
            raise CDPError("Refusing a non-loopback DevTools endpoint")
        self.sock = socket.create_connection((parsed.hostname, parsed.port or 80), timeout)
        self.sock.settimeout(timeout)
        self.next_id = 0
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        path = parsed.path or "/"
        if parsed.query:
            path += "?" + parsed.query
        host = parsed.hostname if parsed.port in (None, 80) else f"{parsed.hostname}:{parsed.port}"
        request = (
            f"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\n"
            f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        ).encode("ascii")
        self.sock.sendall(request)
        response = self._read_headers()
        expected = base64.b64encode(
            hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode("ascii")).digest()
        ).decode("ascii")
        headers = response.decode("latin1", errors="replace").split("\r\n")
        if not headers or " 101 " not in headers[0]:
            self.close()
            raise CDPError("DevTools WebSocket handshake was rejected")
        accept = next((line.split(":", 1)[1].strip() for line in headers[1:] if line.lower().startswith("sec-websocket-accept:")), "")
        if accept != expected:
            self.close()
            raise CDPError("DevTools WebSocket handshake validation failed")

    def _read_exact(self, count: int) -> bytes:
        chunks = bytearray()
        while len(chunks) < count:
            chunk = self.sock.recv(count - len(chunks))
            if not chunk:
                raise CDPError("DevTools WebSocket closed")
            chunks.extend(chunk)
        return bytes(chunks)

    def _read_headers(self) -> bytes:
        data = bytearray()
        while not data.endswith(b"\r\n\r\n"):
            chunk = self.sock.recv(1)
            if not chunk:
                raise CDPError("DevTools handshake closed")
            data.extend(chunk)
            if len(data) > 16384:
                raise CDPError("DevTools handshake headers are too large")
        return bytes(data)

    def _send_frame(self, opcode: int, payload: bytes) -> None:
        first = bytes([0x80 | opcode])
        length = len(payload)
        if length < 126:
            header = first + bytes([0x80 | length])
        elif length <= 0xFFFF:
            header = first + bytes([0x80 | 126]) + struct.pack("!H", length)
        else:
            header = first + bytes([0x80 | 127]) + struct.pack("!Q", length)
        mask = os.urandom(4)
        masked = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
        self.sock.sendall(header + mask + masked)

    def _receive_message(self) -> tuple[int, bytes]:
        payload = bytearray()
        message_opcode = 0
        while True:
            first, second = self._read_exact(2)
            fin, opcode = bool(first & 0x80), first & 0x0F
            masked = bool(second & 0x80)
            length = second & 0x7F
            if length == 126:
                length = struct.unpack("!H", self._read_exact(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self._read_exact(8))[0]
            mask = self._read_exact(4) if masked else b""
            chunk = self._read_exact(length) if length else b""
            if masked:
                chunk = bytes(byte ^ mask[index % 4] for index, byte in enumerate(chunk))
            if opcode == 0x9:  # ping
                self._send_frame(0xA, chunk)
                continue
            if opcode == 0x8:
                raise CDPError("DevTools WebSocket closed")
            if opcode in (0x1, 0x2):
                message_opcode = opcode
            payload.extend(chunk)
            if fin:
                return message_opcode, bytes(payload)

    def call(self, method: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        self.next_id += 1
        request_id = self.next_id
        message = {"id": request_id, "method": method, "params": params or {}}
        self._send_frame(0x1, json.dumps(message, separators=(",", ":")).encode("utf-8"))
        while True:
            opcode, raw = self._receive_message()
            if opcode != 0x1:
                continue
            packet = json.loads(raw.decode("utf-8"))
            if packet.get("id") != request_id:
                continue
            if "error" in packet:
                raise CDPError(f"DevTools command failed: {packet['error'].get('message', 'unknown error')}")
            return packet.get("result", {})

    def evaluate(self, expression: str) -> Any:
        result = self.call("Runtime.evaluate", {
            "expression": expression,
            "returnByValue": True,
            "awaitPromise": True,
        }).get("result", {})
        if result.get("subtype") == "error":
            raise CDPError(result.get("description", "Page evaluation failed"))
        return result.get("value")

    def click(self, x: float, y: float) -> None:
        self.call("Input.dispatchMouseEvent", {
            "type": "mouseMoved", "x": x, "y": y,
        })
        self.call("Input.dispatchMouseEvent", {
            "type": "mousePressed", "x": x, "y": y,
            "button": "left", "buttons": 1, "clickCount": 1,
        })
        self.call("Input.dispatchMouseEvent", {
            "type": "mouseReleased", "x": x, "y": y,
            "button": "left", "buttons": 0, "clickCount": 1,
        })

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


def devtools_targets(endpoint: str = DEFAULT_DEVTOOLS) -> list[dict[str, Any]]:
    with urllib.request.urlopen(endpoint, timeout=2) as response:
        targets = json.loads(response.read().decode("utf-8"))
    return [
        item for item in targets
        if item.get("type") == "page" and item.get("webSocketDebuggerUrl")
        and APP_TITLE in item.get("title", "")
    ]


def pack_id_from_url(page_url: str) -> str | None:
    fragment = urlparse(page_url).fragment
    route_query = urlsplit(fragment).query
    return parse_qs(route_query).get("set_id", [None])[0]


def manual_reset_request(root: Path) -> tuple[float, str] | None:
    """Read the reset request written by the WinUI app, if one is waiting.

    The request lives outside the answer config so a reset never depends on
    restarting the worker (which would lose its in-memory progress anyway).
    """
    path = root / "_assistant" / RESET_REQUEST_FILE
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None
    if not isinstance(data, dict):
        return None
    requested_at = data.get("requested_at")
    if isinstance(requested_at, bool) or not isinstance(requested_at, (int, float)):
        return None
    return float(requested_at), str(data.get("pack_id") or "")


def consume_manual_reset(root: Path, requested_at: float) -> None:
    """Delete the handled request so a restarted worker cannot replay it."""
    path = root / "_assistant" / RESET_REQUEST_FILE
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        if float(data.get("requested_at", -1)) == requested_at:
            path.unlink(missing_ok=True)
    except (OSError, ValueError, TypeError, AttributeError):
        pass


def acknowledge_manual_reset(root: Path, requested_at: float, pack_id: str, status: str) -> None:
    """Tell the WinUI app that the reset was applied (or why it was not)."""
    path = root / "_assistant" / RESET_ACK_FILE
    payload = {
        "schema_version": 1,
        "requested_at": int(round(requested_at)),
        "applied_at": int(time.time()),
        "pack_id": pack_id,
        "status": status,
    }
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(".tmp")
        temporary.write_text(
            json.dumps(payload, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
        os.replace(temporary, path)
    except OSError:
        pass


def load_answers(pack_dir: Path) -> dict[str, str]:
    answers: dict[str, str] = {}
    for content_file in sorted(pack_dir.glob("content_*/content.json")):
        with content_file.open("r", encoding="utf-8-sig") as stream:
            data = json.load(stream)
        if data.get("structure_type") != "collector.choose":
            continue
        info = data.get("info") or {}
        content_id = str(info.get("stid") or content_file.parent.name.removeprefix("content_"))
        for index, question in enumerate(info.get("xtlist") or [], start=1):
            answer = str(question.get("answer") or "").strip().upper()
            if answer in {"A", "B", "C", "D"}:
                question_number = str(question.get("xt_xh") or index)
                answers[f"{content_id}_{question_number}"] = answer
    return answers


def load_free_listening_sections(
    pack_dir: Path,
    known_answers: dict[str, str],
) -> tuple[list[dict[str, Any]], dict[tuple[int, int], str], set[str]]:
    """Build one Worker request from listening groups that contain unanswered questions."""
    sections: list[dict[str, Any]] = []
    answer_ids: dict[tuple[int, int], str] = {}
    missing_ids: set[str] = set()
    for content_file in sorted(pack_dir.glob("content_*/content.json")):
        try:
            with content_file.open("r", encoding="utf-8-sig") as stream:
                data = json.load(stream)
        except (OSError, json.JSONDecodeError):
            continue
        if data.get("structure_type") != "collector.choose":
            continue
        info = data.get("info") or {}
        questions = info.get("xtlist") or []
        if not isinstance(questions, list) or not questions:
            continue
        is_listening = bool(info.get("audio") or info.get("st_audio")) or any(
            question.get("xt_wj") or question.get("xt_wj2")
            for question in questions if isinstance(question, dict)
        )
        if not is_listening:
            continue

        content_id = str(info.get("stid") or content_file.parent.name.removeprefix("content_"))
        question_items: list[tuple[int, str, dict[str, Any]]] = []
        needs_request = False
        for index, question in enumerate(questions, start=1):
            if not isinstance(question, dict):
                continue
            question_number = str(question.get("xt_xh") or index)
            question_id = f"{content_id}_{question_number}"
            if known_answers.get(question_id) not in {"A", "B", "C", "D"}:
                missing_ids.add(question_id)
                needs_request = True
            prompt = clean_text(question.get("xt_nr"), 1000)
            options_raw = question.get("xxlist") or []
            if not prompt or not isinstance(options_raw, list) or not 2 <= len(options_raw) <= 4:
                continue
            options: list[str] = []
            for option_index, option in enumerate(options_raw):
                if not isinstance(option, dict):
                    options = []
                    break
                option_text = clean_text(option.get("xx_nr"), 600)
                if not option_text:
                    options = []
                    break
                label = clean_text(option.get("xx_mc"), 20).strip()
                expected_label = "ABCD"[option_index]
                options.append(f"{label or expected_label}. {option_text}")
            if options:
                question_items.append((index, question_id, {
                    "number": index,
                    "prompt": prompt,
                    "options": options,
                }))

        if not needs_request or not question_items:
            continue
        passage = clean_text(info.get("st_nr"), 12000)
        if len(passage) < 20:
            passage = next((
                clean_text(question.get("xt_value"), 12000)
                for question in questions
                if isinstance(question, dict) and len(clean_text(question.get("xt_value"), 12000)) >= 20
            ), "")
        if len(passage) < 20:
            continue

        section_number = len(sections) + 1
        sections.append({"passage": passage, "questions": [item[2] for item in question_items]})
        for ordinal, question_id, _ in question_items:
            answer_ids[(section_number, ordinal)] = question_id
    return sections, answer_ids, missing_ids


def free_listening_device_id(root: Path) -> str:
    """Return a stable, locally hashed device identifier without sending raw hardware IDs."""
    machine_guid = ""
    if os.name == "nt":
        try:
            import winreg

            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Cryptography") as key:
                machine_guid = str(winreg.QueryValueEx(key, "MachineGuid")[0]).strip()
        except (ImportError, OSError):
            pass
    if machine_guid:
        return hashlib.sha256(f"ets-free-listening-v1:{machine_guid}".encode("utf-8")).hexdigest()

    token_path = root / "_assistant" / "free_listening_device_id"
    try:
        token = token_path.read_text(encoding="ascii").strip()
        if re.fullmatch(r"[a-f0-9]{64}", token):
            return token
    except OSError:
        pass
    token = hashlib.sha256(uuid.uuid4().bytes).hexdigest()
    try:
        token_path.parent.mkdir(parents=True, exist_ok=True)
        token_path.write_text(token, encoding="ascii")
    except OSError as exc:
        raise RuntimeError("无法保存本机设备标识，请检查 ETS 数据目录权限。") from exc
    return token


def request_free_listening_answers(
    pack_dir: Path,
    worker_url: str,
    request_id: str,
    known_answers: dict[str, str],
    root: Path,
) -> tuple[dict[str, str], int | None, str]:
    sections, answer_ids, missing_ids = load_free_listening_sections(pack_dir, known_answers)
    if not missing_ids:
        return {}, None, ""
    if not sections or any(question_id not in answer_ids.values() for question_id in missing_ids):
        return {}, None, "听力原文或选项数据不完整，无法提交整段听力。"
    if (
        len(sections) > 10
        or any(not 1 <= len(section["questions"]) <= 20 for section in sections)
        or sum(len(section["questions"]) for section in sections) > 60
    ):
        return {}, None, "整份听力超过免费服务的单次题量上限。"

    payload = json.dumps({"request_id": request_id, "sections": sections}, ensure_ascii=False).encode("utf-8")
    if len(payload) > 100_000:
        return {}, None, "整份听力文本超过免费服务的单次请求上限。"
    request = urllib.request.Request(
        worker_url + "/v1/listening/solve",
        data=payload,
        headers={
            "Content-Type": "application/json",
            "X-Device-Id": free_listening_device_id(root),
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=150) as response:
            result = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        try:
            details = json.loads(exc.read().decode("utf-8"))
            details = details if isinstance(details, dict) else {}
            error_code = str(details.get("error") or "")
            message = str(details.get("message") or error_code or f"HTTP {exc.code}")
            if error_code:
                message = f"{error_code}: {message}"
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            message = f"HTTP {exc.code}"
        return {}, None, message
    except (urllib.error.URLError, TimeoutError, OSError, UnicodeDecodeError, http.client.HTTPException) as exc:
        return {}, None, f"Worker 网络请求失败：{exc}"
    except json.JSONDecodeError:
        return {}, None, "Worker 返回了无效 JSON。"

    if not isinstance(result, dict):
        return {}, None, "Worker 返回了无效 JSON。"
    answers: dict[str, str] = {}
    raw_answers = result.get("answers", [])
    if not isinstance(raw_answers, list):
        return {}, None, "Worker 返回的答案格式无效。"
    for item in raw_answers:
        try:
            key = (int(item["section"]), int(item["number"]))
            answer = str(item["answer"]).strip().upper()
        except (KeyError, TypeError, ValueError):
            continue
        question_id = answer_ids.get(key)
        if question_id and answer in {"A", "B", "C", "D"}:
            answers[question_id] = answer
    if not missing_ids.issubset(answers):
        return {}, result.get("remaining_today"), "Worker 没有返回全部听力题答案，请稍后重试。"
    remaining = result.get("remaining_today")
    return answers, int(remaining) if isinstance(remaining, int) else None, ""


def load_question_order(pack_dir: Path) -> dict[str, int]:
    """Read the displayed question numbers; content IDs are not ordered IDs."""
    order: dict[str, int] = {}
    for content_file in sorted(pack_dir.glob("content_*/content2.json")):
        try:
            with content_file.open("r", encoding="utf-8-sig") as stream:
                data = json.load(stream)
        except (OSError, json.JSONDecodeError):
            continue
        info = data.get("info") or {}
        content_id = str(info.get("stid") or content_file.parent.name.removeprefix("content_"))
        for index, question in enumerate(info.get("xtlist") or [], start=1):
            label = str(question.get("xt_nr") or "")
            match = re.search(r"\d+", label)
            if match:
                question_number = str(question.get("xt_xh") or index)
                order[f"{content_id}_{question_number}"] = int(match.group())
    return order


def question_order_key(question_id: str, order: dict[str, int]) -> tuple[int, tuple[int, int]]:
    return order.get(question_id, 2**63 - 1), question_sort_key(question_id)


def load_answer_config(path: Path = DEFAULT_CONFIG) -> tuple[float, str, float, bool, bool, bool]:
    """Read the small, flat YAML subset used for QA answer settings."""
    if not path.is_file():
        return 1.0, "default", 0.0, False, False, False
    values: dict[str, str] = {}
    try:
        for raw_line in path.read_text(encoding="utf-8-sig").splitlines():
            line = raw_line.strip()
            if not line or line.startswith("#"):
                continue
            key, separator, value = line.partition(":")
            if not separator:
                raise ValueError(f"Expected key: value in {path.name}: {raw_line}")
            value = value.split("#", 1)[0].strip().strip("\"'")
            values[key.strip()] = value
    except OSError as exc:
        raise ValueError(f"Could not read {path.name}: {exc}") from exc

    unknown = set(values) - {
        "accuracy_rate", "random_seed", "skip_interval_seconds", "free_listening_worker_url",
        "auto_listening_enabled", "experience_mode_enabled", "verbose_logging_enabled"
    }
    if unknown:
        raise ValueError(f"Unknown setting(s) in {path.name}: {', '.join(sorted(unknown))}")
    try:
        accuracy = float(values.get("accuracy_rate", "1.0").removesuffix("%"))
    except ValueError as exc:
        raise ValueError("accuracy_rate must be a number from 0.0 to 1.0") from exc
    if not 0.0 <= accuracy <= 1.0:
        raise ValueError("accuracy_rate must be from 0.0 to 1.0 (for example, 0.8 means 80%)")
    seed = values.get("random_seed", "default") or "default"
    try:
        skip_interval = float(values.get("skip_interval_seconds", "0.0"))
    except ValueError as exc:
        raise ValueError("skip_interval_seconds must be a number from 0.0 to 60.0") from exc
    if not 0.0 <= skip_interval <= 60.0:
        raise ValueError("skip_interval_seconds must be from 0.0 to 60.0")
    # Keep accepting the old config key so existing installations continue to
    # load, but always use the endpoint built into this program.
    auto_listening = values.get("auto_listening_enabled", "false").lower() == "true"
    experience_mode = values.get("experience_mode_enabled", "false").lower() == "true"
    verbose_logging = values.get("verbose_logging_enabled", "false").lower() == "true"
    return accuracy, seed, skip_interval, auto_listening, experience_mode, verbose_logging


def answer_for_accuracy(
    question_id: str,
    correct_answer: str,
    option_count: int,
    accuracy: float,
    seed: str,
    session_key: str,
) -> str:
    """Choose a repeatable QA response; incorrect responses use another visible option."""
    available = "ABCD"[:min(option_count, 4)]
    if correct_answer not in available or accuracy >= 1.0 or len(available) < 2:
        return correct_answer
    digest = hashlib.sha256(
        f"{seed}|{session_key}|{question_id}".encode("utf-8")
    ).digest()
    rng = random.Random(int.from_bytes(digest[:8], "big"))
    if rng.random() < accuracy:
        return correct_answer
    wrong_answers = [letter for letter in available if letter != correct_answer]
    return wrong_answers[rng.randrange(len(wrong_answers))]


def visible_questions(client: CDPClient) -> list[dict[str, Any]]:
    raw = client.evaluate(QUESTION_JS)
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except (TypeError, json.JSONDecodeError):
        return []
    return data.get("questions", []) if data.get("ready") else []


def choose_answer(client: CDPClient, question: dict[str, Any], letter: str) -> bool:
    question_id = str(question.get("id", ""))
    index = "ABCD".find(letter)
    options = question.get("options") or []
    if index < 0 or index >= len(options):
        return False
    if "selected" not in str(options[index].get("cls", "")):
        expression = (
            "(()=>{const d=document.querySelector('iframe.examIframe')?.contentDocument;"
            f"const q=[...(d?.querySelectorAll('.question2')||[])].find(x=>x.id==={json.dumps(question_id)});"
            f"const e=q?.querySelectorAll('.choose2')[{index}];if(!e)return '';e.click();return e.className;}})()"
        )
        try:
            if not client.evaluate(expression):
                return False
        except CDPError:
            return False
    time.sleep(0.25)
    check_expr = f"({CHECK_JS})({json.dumps(question_id)},{index})"
    try:
        state = json.loads(client.evaluate(check_expr) or "{}")
    except (TypeError, json.JSONDecodeError):
        return False
    return state.get("id") == question_id and bool(state.get("selected"))


def click_next(
    client: CDPClient,
    before_ids: set[str] | None = None,
    skip_interval_seconds: float = 0.0,
) -> bool:
    """Send a trusted mouse click and confirm that ETS actually changed questions."""
    global LAST_NEXT_CLICK_AT
    remaining_delay = skip_interval_seconds - (time.monotonic() - LAST_NEXT_CLICK_AT)
    if remaining_delay > 0:
        time.sleep(remaining_delay)
    if before_ids is None:
        before_ids = {str(item.get("id", "")) for item in visible_questions(client)}
    expr = r"(()=>{const b=[...document.querySelectorAll('button')].find(x=>x.querySelector('.icon-nextQuestion'));if(!b||b.disabled||b.getAttribute('aria-disabled')==='true'||b.getClientRects().length===0)return '';const r=b.getBoundingClientRect();return JSON.stringify({x:r.left+r.width/2,y:r.top+r.height/2})})()"
    raw = client.evaluate(expr)
    if not raw:
        return False
    try:
        point = json.loads(raw)
        old_url = client.evaluate("location.href")
        LAST_NEXT_CLICK_AT = time.monotonic()
        client.click(float(point["x"]), float(point["y"]))
    except (TypeError, ValueError, KeyError, json.JSONDecodeError):
        return False
    confirm_seconds = min(
        NEXT_CONFIRM_SECONDS,
        max(MIN_SKIP_CONFIRM_SECONDS, skip_interval_seconds),
    )
    deadline = time.monotonic() + confirm_seconds
    while time.monotonic() < deadline:
        time.sleep(0.1)
        current = visible_questions(client)
        current_ids = {str(item.get("id", "")) for item in current}
        if current_ids and current_ids != before_ids:
            return True
        if not current_ids:
            state = client.evaluate("JSON.stringify({url:location.href,text:document.querySelector('#app')?.innerText||''})")
            try:
                page = json.loads(state or "{}")
            except json.JSONDecodeError:
                page = {}
            if page.get("url") != old_url or re.search(r"提交成功|已完成|答题结果|成绩|本次练习", page.get("text", "")):
                return True
    return False


def question_sort_key(question_id: str) -> tuple[int, int]:
    parts = question_id.split("_", 1)
    try:
        return int(parts[0]), int(parts[1]) if len(parts) > 1 else 0
    except ValueError:
        return (2**63 - 1, 2**63 - 1)


def run_once(
    root: Path,
    endpoint: str = DEFAULT_DEVTOOLS,
    wait_seconds: float = 12.0,
    processed: set[str] | None = None,
    accuracy: float = 1.0,
    random_seed: str = "default",
    session_key: str = "",
    skip_interval_seconds: float = 0.0,
    analysis_session_id: str | None = None,
    expected_pack_id: str | None = None,
    remote_answers: dict[str, str] | None = None,
) -> tuple[str, str | None]:
    analysis_session_id = analysis_session_id or uuid.uuid4().hex
    deadline = time.monotonic() + wait_seconds
    while time.monotonic() < deadline:
        try:
            targets = devtools_targets(endpoint)
        except (OSError, ValueError, json.JSONDecodeError):
            time.sleep(0.5)
            continue
        for target in targets:
            pack_id = pack_id_from_url(str(target.get("url", "")))
            if expected_pack_id is not None and pack_id != expected_pack_id:
                continue
            pack_dir = root / pack_id if pack_id and pack_id.isdigit() else None
            if not pack_dir or not pack_dir.is_dir():
                continue
            try:
                answers = load_answers(pack_dir)
                for question_id, answer in (remote_answers or {}).items():
                    answers.setdefault(question_id, answer)
                if not answers:
                    continue
                question_order = load_question_order(pack_dir)
                client = CDPClient(target["webSocketDebuggerUrl"])
            except (OSError, ValueError, json.JSONDecodeError, CDPError):
                continue
            try:
                questions = visible_questions(client)
                if not questions:
                    continue
                visible_ids = {str(question.get("id", "")) for question in questions}
                if processed is not None and visible_ids:
                    ordered_visible = [question_order.get(question_id) for question_id in visible_ids]
                    known_visible = [number for number in ordered_visible if number is not None]
                    if known_visible:
                        first_visible_number = min(known_visible)
                        processed.update(
                            question_id for question_id in answers
                            if question_id in question_order
                            and question_order[question_id] < first_visible_number
                        )
                pending = [
                    question for question in questions
                    if processed is None or str(question.get("id", "")) not in processed
                ]
                if not pending:
                    remaining = [key for key in answers if key not in (processed or set())]
                    if remaining and click_next(client, visible_ids, skip_interval_seconds):
                        return "advanced", None
                    if not remaining:
                        last_id = max(answers, key=lambda question_id: question_order_key(question_id, question_order))
                        if click_next(client, visible_ids, skip_interval_seconds):
                            print("Advanced past the last local question group; ETS may complete the section automatically.", flush=True)
                            return "complete", last_id
                        print("Next button click was not confirmed; will retry while this ETS page remains open.", flush=True)
                    continue
                selected_ids: set[str] = set()
                for question in pending:
                    question_id = str(question.get("id", ""))
                    answer = answers.get(question_id)
                    if not answer:
                        print(f"No local answer for visible question {question_id}", flush=True)
                        return "waiting", question_id
                    correct_answer = answer
                    answer = answer_for_accuracy(
                        question_id, answer, len(question.get("options") or []),
                        accuracy, random_seed, session_key,
                    )
                    if not choose_answer(client, question, answer):
                        print(f"Could not verify a trusted click on question {question_id}", flush=True)
                        return "error", question_id
                    selected_ids.add(question_id)
                    record_answer(root, pack_dir, analysis_session_id, question_id, answer, correct_answer)
                    print(f"Selected {answer} for question {question_id}", flush=True)
                if processed is not None:
                    processed.update(selected_ids)
                remaining = [key for key in answers if key not in (processed or set()) | selected_ids]
                if remaining:
                    if not click_next(client, visible_ids, skip_interval_seconds):
                        print("Next button did not advance the visible questions; will retry.", flush=True)
                        return "waiting", None
                else:
                    if click_next(client, visible_ids, skip_interval_seconds):
                        print("Advanced past the last local question group; ETS may complete the section automatically.", flush=True)
                        return "complete", sorted(selected_ids, key=lambda question_id: question_order_key(question_id, question_order))[-1]
                    print("Last answers are selected, but ETS did not advance; will retry the next button.", flush=True)
                    return "waiting", None
                return "selected", sorted(selected_ids, key=lambda question_id: question_order_key(question_id, question_order))[-1]
            except (OSError, CDPError, ValueError, json.JSONDecodeError) as exc:
                print(f"Auto-answer paused safely: {exc}", flush=True)
                return "error", None
            finally:
                client.close()
        time.sleep(WATCH_INTERVAL_SECONDS)
    return "waiting", None


def watch(root: Path, endpoint: str, config_path: Path | None = None) -> int:
    handled: set[tuple[str, str]] = set()
    processed: dict[tuple[str, str], set[str]] = {}
    analysis_sessions: dict[tuple[str, str], str] = {}
    free_answer_cache: dict[tuple[str, str], dict[str, str]] = {}
    free_answer_last_attempt: dict[tuple[str, str], float] = {}
    free_answer_quota_blocked: set[tuple[str, str]] = set()
    last_reset_at = 0.0
    settings_path = config_path or (root / DEFAULT_CONFIG.name)
    try:
        accuracy, random_seed, skip_interval, auto_listening_enabled, experience_mode_enabled, verbose_logging_enabled = load_answer_config(settings_path)
    except ValueError as exc:
        print(f"Invalid answer config: {exc}", flush=True)
        return 1
    print(
        f"Auto-answer worker watching ETS on loopback DevTools at port "
        f"{urlparse(endpoint).port}; configured answer accuracy: {accuracy:.0%}; "
        f"skip retry interval: {skip_interval:g}s; auto listening: {auto_listening_enabled}; "
        f"experience mode: {experience_mode_enabled}; verbose logs: {verbose_logging_enabled}.",
        flush=True,
    )
    try:
        config_mtime = settings_path.stat().st_mtime_ns
    except OSError:
        config_mtime = None
    last_config_check = time.monotonic()
    while True:
        try:
            if time.monotonic() - last_config_check >= 1:
                last_config_check = time.monotonic()
                try:
                    current_mtime = settings_path.stat().st_mtime_ns
                    if current_mtime != config_mtime:
                        new_accuracy, new_seed, new_skip, new_auto_listening, new_experience_mode, new_verbose = load_answer_config(settings_path)
                        accuracy, random_seed, skip_interval = new_accuracy, new_seed, new_skip
                        auto_listening_enabled, experience_mode_enabled = new_auto_listening, new_experience_mode
                        verbose_logging_enabled = new_verbose
                        config_mtime = current_mtime
                        print("Answer settings reloaded.", flush=True)
                except (OSError, ValueError) as exc:
                    print(f"Could not reload answer settings: {exc}", flush=True)
            reset_request = manual_reset_request(root)
            if reset_request is not None and reset_request[0] <= last_reset_at:
                reset_request = None
            for target in devtools_targets(endpoint):
                pack_id = pack_id_from_url(str(target.get("url", "")))
                if not pack_id:
                    continue
                # A fresh homework_id indicates a fresh practice session.
                fragment = urlsplit(urlparse(str(target.get("url", ""))).fragment)
                params = parse_qs(fragment.query)
                session_key = (pack_id, params.get("homework_id", [""])[0])
                if reset_request is not None and (not reset_request[1] or reset_request[1] == pack_id):
                    # The user switched ETS back to question 1 and asked for a
                    # fresh pass: forget every cached decision for this pack and
                    # let the normal loop answer the visible questions again.
                    handled.discard(session_key)
                    processed.pop(session_key, None)
                    analysis_sessions.pop(session_key, None)
                    free_answer_cache.pop(session_key, None)
                    free_answer_last_attempt.pop(session_key, None)
                    free_answer_quota_blocked.discard(session_key)
                    last_reset_at = reset_request[0]
                    consume_manual_reset(root, reset_request[0])
                    acknowledge_manual_reset(root, reset_request[0], pack_id, "applied")
                    print(
                        f"Manual reset applied for pack {pack_id}; "
                        "answering again from the visible question.",
                        flush=True,
                    )
                    reset_request = None
                pack_dir = root / pack_id
                if session_key in handled:
                    # ETS can reuse the same homework_id when the user starts
                    # another practice attempt. Treat a return to the first
                    # question as a fresh pass through the same pack.
                    try:
                        answers = load_answers(pack_dir)
                        client = CDPClient(target["webSocketDebuggerUrl"])
                        try:
                            current = visible_questions(client)
                        finally:
                            client.close()
                        # Content IDs are database identifiers, not exercise order.
                        # Detect a restarted attempt from its cleared selections instead
                        # of treating the numerically smallest ID as the first question.
                        has_selection = any(
                            "selected" in str(option.get("cls", ""))
                            for question in current
                            for option in question.get("options", [])
                        )
                        if current and not has_selection:
                            handled.discard(session_key)
                            processed.pop(session_key, None)
                            analysis_sessions.pop(session_key, None)
                            free_answer_cache.pop(session_key, None)
                            free_answer_last_attempt.pop(session_key, None)
                            free_answer_quota_blocked.discard(session_key)
                    except (OSError, ValueError, json.JSONDecodeError, CDPError):
                        pass
                    if session_key in handled:
                        continue
                answered = processed.setdefault(session_key, set())
                analysis_session_id = analysis_sessions.setdefault(session_key, uuid.uuid4().hex)
                key_is_configured = (root / "deepseek_api_key.bin").is_file()
                last_free_attempt = free_answer_last_attempt.get(session_key, float("-inf"))
                should_try_free_worker = (
                    auto_listening_enabled
                    and experience_mode_enabled
                    and not key_is_configured
                    and session_key not in free_answer_quota_blocked
                    and session_key not in free_answer_cache
                    and time.monotonic() - last_free_attempt >= 30
                )
                if should_try_free_worker:
                    try:
                        local_answers = load_answers(pack_dir)
                        _, _, missing_ids = load_free_listening_sections(pack_dir, local_answers)
                        if verbose_logging_enabled and missing_ids:
                            print(f"[verbose] pack {pack_id}: {len(missing_ids)} listening answers missing locally", flush=True)
                        client = CDPClient(target["webSocketDebuggerUrl"])
                        try:
                            visible_ids = {str(question.get("id", "")) for question in visible_questions(client)}
                        finally:
                            client.close()
                        if missing_ids.intersection(visible_ids):
                            free_answer_last_attempt[session_key] = time.monotonic()
                            remote, remaining, free_error = request_free_listening_answers(
                                pack_dir, FREE_LISTENING_WORKER_URL, analysis_session_id,
                                local_answers, root,
                            )
                            if free_error:
                                print(f"Free listening request unavailable for pack {pack_id}: {free_error}", flush=True)
                                if "daily_quota_exceeded" in free_error or "ip_quota_exceeded" in free_error:
                                    free_answer_quota_blocked.add(session_key)
                            else:
                                free_answer_cache[session_key] = remote
                                if remote:
                                    remaining_text = f"; {remaining} free requests left today" if remaining is not None else ""
                                    print(
                                        f"Free listening answers received for pack {pack_id} "
                                        f"({len(remote)} questions{remaining_text}).",
                                        flush=True,
                                    )
                    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as exc:
                        free_answer_last_attempt[session_key] = time.monotonic()
                        print(f"Free listening request unavailable for pack {pack_id}: {exc}", flush=True)
                status, content_id = run_once(
                    root, endpoint, wait_seconds=0.5, processed=answered,
                    accuracy=accuracy, random_seed=random_seed,
                    session_key="|".join(session_key),
                    skip_interval_seconds=skip_interval,
                    analysis_session_id=analysis_session_id,
                    expected_pack_id=pack_id,
                    remote_answers=free_answer_cache.get(session_key),
                )
                if status == "selected" and content_id:
                    answered.add(content_id)
                elif status == "complete":
                    handled.add(session_key)
                    print(f"Answering is complete for pack {pack_id}; keeping the exam pack and converted files.", flush=True)
        except (OSError, ValueError, json.JSONDecodeError):
            pass
        time.sleep(WATCH_INTERVAL_SECONDS)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--config", type=Path, default=None)
    parser.add_argument("--devtools", default=DEFAULT_DEVTOOLS)
    parser.add_argument("--watch", action="store_true", help="wait for an active ETS question")
    args = parser.parse_args(argv)
    try:
        if args.watch:
            return watch(args.root.resolve(), args.devtools, args.config.resolve() if args.config else None)
        return 0 if run_once(args.root.resolve(), args.devtools) else 1
    except KeyboardInterrupt:
        print("Auto-answer worker stopped.", flush=True)
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
