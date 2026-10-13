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
import json
import os
import random
import re
import socket
import struct
import time
import urllib.request
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse, urlsplit


DEFAULT_ROOT = Path(__file__).resolve().parent
DEFAULT_DEVTOOLS = "http://127.0.0.1:10086/json/list"
APP_TITLE = "E听说"
NEXT_CONFIRM_SECONDS = 1.2
WATCH_INTERVAL_SECONDS = 0.15
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


def click_next(client: CDPClient, before_ids: set[str] | None = None) -> bool:
    """Send a trusted mouse click and confirm that ETS actually changed questions."""
    if before_ids is None:
        before_ids = {str(item.get("id", "")) for item in visible_questions(client)}
    expr = r"(()=>{const b=[...document.querySelectorAll('button')].find(x=>x.querySelector('.icon-nextQuestion'));if(!b||b.disabled||b.getAttribute('aria-disabled')==='true'||b.getClientRects().length===0)return '';const r=b.getBoundingClientRect();return JSON.stringify({x:r.left+r.width/2,y:r.top+r.height/2})})()"
    raw = client.evaluate(expr)
    if not raw:
        return False
    try:
        point = json.loads(raw)
        old_url = client.evaluate("location.href")
        client.click(float(point["x"]), float(point["y"]))
    except (TypeError, ValueError, KeyError, json.JSONDecodeError):
        return False
    deadline = time.monotonic() + NEXT_CONFIRM_SECONDS
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
) -> tuple[str, str | None]:
    deadline = time.monotonic() + wait_seconds
    while time.monotonic() < deadline:
        try:
            targets = devtools_targets(endpoint)
        except (OSError, ValueError, json.JSONDecodeError):
            time.sleep(0.5)
            continue
        for target in targets:
            pack_id = pack_id_from_url(str(target.get("url", "")))
            pack_dir = root / pack_id if pack_id and pack_id.isdigit() else None
            if not pack_dir or not pack_dir.is_dir():
                continue
            try:
                answers = load_answers(pack_dir)
                if not answers:
                    continue
                client = CDPClient(target["webSocketDebuggerUrl"])
            except (OSError, ValueError, json.JSONDecodeError, CDPError):
                continue
            try:
                questions = visible_questions(client)
                if not questions:
                    continue
                visible_ids = {str(question.get("id", "")) for question in questions}
                if processed is not None and visible_ids:
                    first_visible = min(visible_ids, key=question_sort_key)
                    # A watcher may start after ETS has already advanced to a later group.
                    # Earlier IDs are behind the current page and must not keep the final
                    # group in the "more answers remain" branch forever.
                    processed.update(
                        key for key in answers
                        if question_sort_key(key) < question_sort_key(first_visible)
                    )
                pending = [
                    question for question in questions
                    if processed is None or str(question.get("id", "")) not in processed
                ]
                if not pending:
                    remaining = [key for key in answers if key not in (processed or set())]
                    if remaining and click_next(client, visible_ids):
                        return "advanced", None
                    if not remaining:
                        last_id = max(answers, key=question_sort_key)
                        if click_next(client, visible_ids):
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
                    if not choose_answer(client, question, answer):
                        print(f"Could not verify a trusted click on question {question_id}", flush=True)
                        return "error", question_id
                    selected_ids.add(question_id)
                    print(f"Selected {answer} for question {question_id}", flush=True)
                if processed is not None:
                    processed.update(selected_ids)
                remaining = [key for key in answers if key not in (processed or set()) | selected_ids]
                if remaining:
                    if not click_next(client, visible_ids):
                        print("Next button did not advance the visible questions; will retry.", flush=True)
                        return "waiting", None
                else:
                    if click_next(client, visible_ids):
                        print("Advanced past the last local question group; ETS may complete the section automatically.", flush=True)
                        return "complete", sorted(selected_ids, key=question_sort_key)[-1]
                    print("Last answers are selected, but ETS did not advance; will retry the next button.", flush=True)
                    return "waiting", None
                return "selected", sorted(selected_ids, key=question_sort_key)[-1]
            except (OSError, CDPError, ValueError, json.JSONDecodeError) as exc:
                print(f"Auto-answer paused safely: {exc}", flush=True)
                return "error", None
            finally:
                client.close()
        time.sleep(WATCH_INTERVAL_SECONDS)
    return "waiting", None


def watch(root: Path, endpoint: str) -> int:
    handled: set[tuple[str, str]] = set()
    processed: dict[tuple[str, str], set[str]] = {}
    print(f"Auto-answer worker watching ETS on loopback DevTools at port {urlparse(endpoint).port}.", flush=True)
    while True:
        try:
            for target in devtools_targets(endpoint):
                pack_id = pack_id_from_url(str(target.get("url", "")))
                if not pack_id:
                    continue
                # A fresh homework_id indicates a fresh practice session.
                fragment = urlsplit(urlparse(str(target.get("url", ""))).fragment)
                params = parse_qs(fragment.query)
                session_key = (pack_id, params.get("homework_id", [""])[0])
                if session_key in handled:
                    # ETS can reuse the same homework_id when the user starts
                    # another practice attempt. Treat a return to the first
                    # question as a fresh pass through the same pack.
                    try:
                        pack_dir = root / pack_id
                        answers = load_answers(pack_dir)
                        first_id = min(answers, key=question_sort_key) if answers else None
                        client = CDPClient(target["webSocketDebuggerUrl"])
                        try:
                            current = visible_questions(client)
                        finally:
                            client.close()
                        if current and str(current[0].get("id", "")) == first_id:
                            handled.discard(session_key)
                            processed.pop(session_key, None)
                    except (OSError, ValueError, json.JSONDecodeError, CDPError):
                        pass
                    if session_key in handled:
                        continue
                answered = processed.setdefault(session_key, set())
                status, content_id = run_once(
                    root, endpoint, wait_seconds=0.5, processed=answered
                )
                if status == "selected" and content_id:
                    answered.add(content_id)
                elif status == "complete":
                    handled.add(session_key)
        except (OSError, ValueError, json.JSONDecodeError):
            pass
        time.sleep(WATCH_INTERVAL_SECONDS)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--devtools", default=DEFAULT_DEVTOOLS)
    parser.add_argument("--watch", action="store_true", help="wait for an active ETS question")
    args = parser.parse_args()
    try:
        if args.watch:
            return watch(args.root.resolve(), args.devtools)
        return 0 if run_once(args.root.resolve(), args.devtools) else 1
    except KeyboardInterrupt:
        print("Auto-answer worker stopped.", flush=True)
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
