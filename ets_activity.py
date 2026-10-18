"""Local, append-only history of verified assistant answer selections."""

from __future__ import annotations

import html
import json
import re
from datetime import datetime, timezone
from functools import lru_cache
from pathlib import Path
from typing import Any


_last_selection: dict[tuple[str, str, str], str] = {}
_warned = False


def clean_text(value: Any, limit: int = 12000) -> str:
    text = re.sub(r"<[^>]+>", " ", str(value or ""))
    text = re.sub(r"\bets_th\d+\b", "", html.unescape(text))
    return re.sub(r"\s+", " ", text).strip()[:limit]


@lru_cache(maxsize=32)
def _catalog(pack_path: str, signature: tuple[tuple[str, int], ...]) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for relative, _ in signature:
        try:
            data = json.loads((Path(pack_path) / relative).read_text(encoding="utf-8-sig"))
            if data.get("structure_type") != "collector.choose":
                continue
            info = data.get("info") or {}
            content_id = str(info.get("stid") or Path(relative).parent.name.removeprefix("content_"))
            questions = info.get("xtlist") or []
            has_audio = bool(info.get("audio")) or any(q.get("xt_wj") or q.get("xt_wj2") for q in questions)
            for index, question in enumerate(questions, 1):
                question_id = f"{content_id}_{question.get('xt_xh') or index}"
                result[question_id] = {
                    "question_type": "听力选择" if has_audio else "阅读选择",
                    "prompt": clean_text(question.get("xt_nr"), 2000) or f"内容 {content_id} · 第 {index} 小题",
                    "passage": clean_text(question.get("xt_value") or info.get("st_nr")),
                    "explanation": clean_text(question.get("xt_analy"), 6000),
                    "options": [
                        {"letter": str(option.get("xx_mc") or ""), "text": clean_text(option.get("xx_nr"), 1200)}
                        for option in question.get("xxlist") or []
                    ],
                }
        except (OSError, ValueError, TypeError, AttributeError):
            continue
    return result


def record_answer(root: Path, pack_dir: Path, session_id: str, question_id: str,
                  selected: str, correct: str) -> None:
    """Record confirmed clicks only. A history failure never interrupts answering."""
    global _warned
    key = (session_id, pack_dir.name, question_id)
    if _last_selection.get(key) == selected:
        return
    try:
        signature = tuple(sorted(
            (p.relative_to(pack_dir).as_posix(), p.stat().st_mtime_ns)
            for p in pack_dir.glob("content_*/content.json")
        ))
        metadata = _catalog(str(pack_dir), signature).get(question_id, {})
        event = {
            "schema_version": 1,
            "event": "answer_selected",
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "session_id": session_id,
            "pack_id": pack_dir.name,
            "question_id": question_id,
            "question_type": "选择题",
            "prompt": f"题目 {question_id}",
            "passage": "",
            "explanation": "",
            "options": [],
            **metadata,
            "selected_answer": selected,
            "correct_answer": correct,
            "verified": True,
            "source": "assistant",
        }
        folder = root / "_assistant"
        folder.mkdir(parents=True, exist_ok=True)
        with (folder / "answer_history.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(event, ensure_ascii=False, separators=(",", ":")) + "\n")
        _last_selection[key] = selected
        _warned = False
    except (OSError, ValueError, TypeError) as exc:
        if not _warned:
            print(f"Could not save local answer analysis: {exc}", flush=True)
            _warned = True
