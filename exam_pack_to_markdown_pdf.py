#!/usr/bin/env python3
"""Convert a JSON exam-pack directory into one polished Markdown file and PDF.

The converter understands the collector.choose, collector.word, collector.read,
collector.dialogue, and collector.picture structures used by packs like 706477.

PDF generation requires ReportLab and Pillow. If they are not installed, run:
    python3 -m pip install reportlab pillow
"""

from __future__ import annotations

import argparse
import html
import json
import re
import shutil
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


MARKER_RE = re.compile(r"\bets_th\d+\b", re.IGNORECASE)
TAG_RE = re.compile(r"<[^>]+>")
LEADING_NUMBER_RE = re.compile(r"^\s*\d+\s*[.)]\s*")


def load_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def clean_text(value: Any) -> str:
    """Turn the pack's small HTML fragments into readable plain text."""
    if value is None:
        return ""
    text = str(value)
    text = re.sub(r"<\s*/?\s*br\s*/?\s*>", "\n", text, flags=re.IGNORECASE)
    text = re.sub(r"</\s*p\s*>", "\n", text, flags=re.IGNORECASE)
    text = re.sub(r"<\s*p(?:\s+[^>]*)?>", "", text, flags=re.IGNORECASE)
    text = re.sub(r"</\s*li\s*>", "\n", text, flags=re.IGNORECASE)
    text = TAG_RE.sub("", text)
    text = html.unescape(text).replace("\r", "")
    text = text.replace("\u00a0", " ")
    lines = [re.sub(r"[ \t]+", " ", line).strip() for line in text.split("\n")]
    return "\n".join(line for line in lines if line).strip()


def remove_marker(value: Any) -> str:
    return MARKER_RE.sub("", clean_text(value)).strip()


def remove_leading_number(value: str) -> str:
    return LEADING_NUMBER_RE.sub("", value).strip()


def md_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace("|", "\\|")


def quote_lines(text: str) -> list[str]:
    return [f"> {line}" if line else ">" for line in text.splitlines()]


def natural_number(path: Path) -> int:
    match = re.search(r"(\d+)$", path.name)
    return int(match.group(1)) if match else 0


@dataclass
class PackPart:
    number: int
    name: str
    score: str
    count: str
    exam_defs: list[dict[str, Any]]
    content_dirs: list[Path]


class ExamPack:
    def __init__(self, root: Path):
        self.root = root.resolve()
        if not self.root.is_dir():
            raise ValueError(f"Exam pack is not a directory: {self.root}")

        template_dirs = sorted(self.root.glob("template_*"))
        if not template_dirs:
            raise ValueError("No template_* directory was found in the exam pack.")
        self.template_dir = template_dirs[0]
        res_path = self.template_dir / "res.json"
        if not res_path.exists():
            raise ValueError(f"Missing template definition: {res_path}")
        self.definition = load_json(res_path)

        self.content_dirs = sorted(
            (p for p in self.root.glob("content_*") if (p / "content.json").exists()),
            key=natural_number,
        )
        expected = sum(
            len(section.get("exam_list", []))
            for section in self.definition.get("exam_type_list", [])
        )
        if len(self.content_dirs) < expected:
            raise ValueError(
                f"Pack defines {expected} content blocks but only {len(self.content_dirs)} "
                "content_* directories were found."
            )

    @property
    def title(self) -> str:
        return f"Exam Pack {self.root.name}"

    @property
    def total_score(self) -> str:
        return str(self.definition.get("set_score", "")).strip()

    def parts(self) -> list[PackPart]:
        parts: list[PackPart] = []
        cursor = 0
        for fallback_no, section in enumerate(self.definition.get("exam_type_list", []), 1):
            exams = section.get("exam_list", [])
            dirs = self.content_dirs[cursor : cursor + len(exams)]
            cursor += len(exams)
            parts.append(
                PackPart(
                    number=int(section.get("exam_type_order") or fallback_no),
                    name=clean_text(section.get("exam_type_name")) or f"Part {fallback_no}",
                    score=str(section.get("exam_type_score", "")).strip(),
                    count=str(sum(int(exam.get("exam_count") or 0) for exam in exams)),
                    exam_defs=exams,
                    content_dirs=dirs,
                )
            )
        return parts


class MarkdownBuilder:
    def __init__(self, pack: ExamPack, output_md: Path, assets_dir: Path):
        self.pack = pack
        self.output_md = output_md
        self.assets_dir = assets_dir
        self.lines: list[str] = []
        self.copied_assets: dict[Path, Path] = {}

    def add(self, *lines: str) -> None:
        self.lines.extend(lines)

    def copy_asset(self, content_dir: Path, filename: Any) -> str | None:
        name = clean_text(filename)
        if not name:
            return None
        source = content_dir / "material" / Path(name).name
        if not source.is_file() or source.suffix.lower() not in {
            ".png", ".jpg", ".jpeg", ".gif", ".webp"
        }:
            return None
        if source not in self.copied_assets:
            self.assets_dir.mkdir(parents=True, exist_ok=True)
            destination = self.assets_dir / f"{content_dir.name}_{source.name}"
            shutil.copy2(source, destination)
            self.copied_assets[source] = destination
        return self.copied_assets[source].relative_to(self.output_md.parent).as_posix()

    def source_block(self, label: str, text: str) -> None:
        if not text:
            return
        self.add(f"**{label}**", "")
        self.lines.extend(quote_lines(text))
        self.add("")

    def answer(self, text: str, label: str = "Answer") -> None:
        text = text.strip() or "No answer supplied in the pack."
        self.add(f"**{label}:** {text}", "")

    def question_heading(self, label: str, question: str) -> None:
        self.add(f"### {label}. {question}".rstrip(), "")

    def build_choose(
        self,
        part: PackPart,
        exam_def: dict[str, Any],
        content_dir: Path,
        answers: list[tuple[str, str]],
    ) -> None:
        data = load_json(content_dir / "content.json")
        info = data.get("info", {})
        shared_source = clean_text(info.get("st_nr"))
        if shared_source:
            self.source_block("Passage / transcript", shared_source)

        number_values = [
            clean_text(item.get("value")).rstrip(".")
            for item in exam_def.get("exam_info", [])
        ]
        for index, question in enumerate(info.get("xtlist", []), 1):
            label = number_values[index - 1] if index <= len(number_values) else str(index)
            marker_question = remove_marker(question.get("xt_nr"))
            content_value = clean_text(question.get("xt_value"))
            if marker_question:
                prompt = remove_leading_number(marker_question)
                per_question_source = content_value
            else:
                prompt = remove_leading_number(content_value)
                per_question_source = ""
            self.question_heading(label, prompt)
            if per_question_source:
                self.source_block("Transcript", per_question_source)

            image = self.copy_asset(content_dir, question.get("xt_pic"))
            if image:
                self.add(f"![Question {label}]({image})", "")

            choices = question.get("xxlist", [])
            for choice in choices:
                letter = clean_text(choice.get("xx_mc")) or clean_text(choice.get("xx_xh"))
                choice_text = clean_text(choice.get("xx_nr"))
                self.add(f"- **{letter}.** {choice_text}")
            if choices:
                self.add("")
            answer = clean_text(question.get("answer"))
            self.answer(answer)
            answers.append((label, answer))

    def first_standard_answer(self, obj: dict[str, Any]) -> str:
        explicit = clean_text(obj.get("answer"))
        if explicit:
            return explicit
        standards = obj.get("std") or []
        if standards:
            return clean_text(standards[0].get("value") or standards[0].get("ai"))
        return ""

    def build_non_choose(
        self, part: PackPart, exam_def: dict[str, Any], content_dir: Path
    ) -> None:
        data = load_json(content_dir / "content.json")
        structure = data.get("structure_type", "")
        info = data.get("info", {})
        numbers = [
            clean_text(item.get("value")).rstrip(".")
            for item in exam_def.get("exam_info", [])
        ]

        if structure == "collector.word":
            text = remove_marker(info.get("value"))
            label = numbers[0] if numbers else clean_text(info.get("xh")) or "1"
            self.question_heading(label, "Read the sentence aloud")
            self.source_block("Reading text", text)
            self.answer(text, "Reference text")
            return

        if structure == "collector.read":
            self.question_heading("1", "Read the passage aloud")
            text = clean_text(info.get("value"))
            self.source_block("Reading passage", text)
            answer = clean_text(info.get("ai")) or text
            self.answer(answer, "Reference text")
            return

        if structure == "collector.picture":
            topic = clean_text(info.get("topic")) or "Describe the picture"
            self.question_heading("1", topic)
            image = self.copy_asset(content_dir, info.get("image"))
            if image:
                self.add(f"![{md_escape(topic)}]({image})", "")
            keypoints = clean_text(info.get("keypoint"))
            if keypoints:
                self.source_block("Key points", keypoints)
            self.answer(self.first_standard_answer(info), "Model answer")
            return

        # Dialogue also covers situational questions, quick responses, and
        # passage-based free expression.
        shared_source = clean_text(info.get("value"))
        if shared_source:
            self.source_block("Source passage", shared_source)
        questions = info.get("question", [])
        for index, question in enumerate(questions, 1):
            label = numbers[index - 1] if index <= len(numbers) else clean_text(question.get("xh")) or str(index)
            prompt = remove_marker(question.get("ask"))
            self.question_heading(label, prompt)
            image = self.copy_asset(content_dir, question.get("askimg"))
            if image:
                self.add(f"![Question {label}]({image})", "")
            self.answer(self.first_standard_answer(question), "Model answer")

    def answer_card(self, answers: list[tuple[str, str]]) -> None:
        if not answers:
            return
        self.add("### Answer Card", "")
        self.add("| " + " | ".join(["No."] + [md_escape(n) for n, _ in answers]) + " |")
        self.add("| " + " | ".join(["---"] * (len(answers) + 1)) + " |")
        self.add("| " + " | ".join(["Answer"] + [md_escape(a) for _, a in answers]) + " |", "")

    def build(self) -> str:
        self.add(f"# {self.pack.title}", "")
        if self.pack.total_score:
            self.add(f"**Total score:** {self.pack.total_score}", "")
        self.add(
            "This study edition presents every question with its answer. "
            "For speaking tasks, the first accepted response in the source pack is shown as a model answer.",
            "",
        )
        parts = self.pack.parts()
        for part_index, part in enumerate(parts):
            if part_index:
                self.add('<div class="page-break"></div>', "")
            metadata = []
            if part.score:
                metadata.append(f"{part.score} points")
            if part.count:
                metadata.append(f"{part.count} {'item' if part.count == '1' else 'items'}")
            suffix = f" ({' - '.join(metadata)})" if metadata else ""
            self.add(f"## Part {part.number}: {part.name}{suffix}", "")

            part_answers: list[tuple[str, str]] = []
            all_choose = True
            for exam_def, content_dir in zip(part.exam_defs, part.content_dirs):
                data = load_json(content_dir / "content.json")
                if data.get("structure_type") == "collector.choose":
                    self.build_choose(part, exam_def, content_dir, part_answers)
                else:
                    all_choose = False
                    self.build_non_choose(part, exam_def, content_dir)
            if all_choose:
                self.answer_card(part_answers)

        markdown = "\n".join(self.lines).rstrip() + "\n"
        self.output_md.parent.mkdir(parents=True, exist_ok=True)
        self.output_md.write_text(markdown, encoding="utf-8")
        return markdown


def strip_markdown_inline(text: str) -> str:
    text = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", text)
    text = re.sub(r"`(.+?)`", r"<font name='Courier'>\1</font>", text)
    return text


def render_markdown_pdf(markdown_path: Path, pdf_path: Path) -> None:
    """Render the converter's Markdown subset with ReportLab."""
    try:
        from PIL import Image as PILImage
        from reportlab.lib import colors
        from reportlab.lib.enums import TA_CENTER
        from reportlab.lib.pagesizes import A4
        from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
        from reportlab.lib.units import mm
        from reportlab.pdfbase import pdfmetrics
        from reportlab.pdfbase.ttfonts import TTFont
        from reportlab.platypus import (
            HRFlowable,
            Image,
            KeepTogether,
            PageBreak,
            Paragraph,
            SimpleDocTemplate,
            Spacer,
            Table,
            TableStyle,
        )
    except ImportError as exc:
        raise RuntimeError(
            "PDF generation needs ReportLab and Pillow. Install them with: "
            "python3 -m pip install reportlab pillow"
        ) from exc

    font_name = "Helvetica"
    bold_name = "Helvetica-Bold"
    font_candidates = [
        Path("/System/Library/Fonts/Hiragino Sans GB.ttc"),
        Path("/System/Library/Fonts/STHeiti Medium.ttc"),
        Path("/System/Library/Fonts/Supplemental/Arial Unicode.ttf"),
        Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"),
        Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"),
    ]
    for candidate in font_candidates:
        if candidate.exists():
            try:
                pdfmetrics.registerFont(TTFont("ExamPackUnicode", str(candidate), subfontIndex=0))
                font_name = "ExamPackUnicode"
                bold_name = "ExamPackUnicode"
                break
            except Exception:
                continue

    navy = colors.HexColor("#18324A")
    teal = colors.HexColor("#187F7A")
    pale = colors.HexColor("#F2F7F7")
    ink = colors.HexColor("#26343D")
    muted = colors.HexColor("#667680")
    line = colors.HexColor("#CFDADD")

    styles = getSampleStyleSheet()
    base = ParagraphStyle(
        "ExamBody", parent=styles["BodyText"], fontName=font_name,
        fontSize=9.6, leading=14.2, textColor=ink, spaceAfter=5 * mm,
    )
    h1 = ParagraphStyle(
        "ExamTitle", parent=base, fontName=bold_name, fontSize=25, leading=31,
        textColor=navy, alignment=TA_CENTER, spaceBefore=30 * mm, spaceAfter=8 * mm,
    )
    h2 = ParagraphStyle(
        "ExamPart", parent=base, fontName=bold_name, fontSize=17, leading=22,
        textColor=navy, spaceBefore=2 * mm, spaceAfter=7 * mm,
    )
    h3 = ParagraphStyle(
        "ExamQuestion", parent=base, fontName=bold_name, fontSize=11.2, leading=16,
        textColor=navy, spaceBefore=3 * mm, spaceAfter=3 * mm,
    )
    quote = ParagraphStyle(
        "ExamQuote", parent=base, fontSize=8.8, leading=13.3,
        leftIndent=5 * mm, rightIndent=3 * mm, borderColor=teal,
        borderWidth=0, borderPadding=0, backColor=pale, spaceAfter=1.5 * mm,
    )
    bullet = ParagraphStyle(
        "ExamBullet", parent=base, leftIndent=5 * mm, firstLineIndent=-3.5 * mm,
        bulletIndent=1 * mm, spaceAfter=1.4 * mm,
    )
    answer_style = ParagraphStyle(
        "ExamAnswer", parent=base, leftIndent=4 * mm, rightIndent=3 * mm,
        borderColor=teal, borderWidth=0.7, borderPadding=5, backColor=colors.HexColor("#F7FBFA"),
        spaceBefore=1.5 * mm, spaceAfter=5 * mm,
    )

    def safe(text: str) -> str:
        # Preserve inline tags deliberately emitted by strip_markdown_inline.
        escaped = html.escape(text, quote=False)
        escaped = escaped.replace("&lt;b&gt;", "<b>").replace("&lt;/b&gt;", "</b>")
        escaped = escaped.replace("&lt;br/&gt;", "<br/>")
        escaped = escaped.replace("&lt;font name='Courier'&gt;", "<font name='Courier'>")
        escaped = escaped.replace("&lt;/font&gt;", "</font>")
        return escaped

    lines = markdown_path.read_text(encoding="utf-8").splitlines()
    story: list[Any] = []
    index = 0
    pending_answer_heading: Any | None = None
    while index < len(lines):
        raw = lines[index].rstrip()
        line_text = raw.strip()
        if not line_text:
            index += 1
            continue
        if line_text == '<div class="page-break"></div>':
            story.append(PageBreak())
        elif line_text.startswith("# "):
            story.append(Paragraph(safe(strip_markdown_inline(line_text[2:])), h1))
            story.append(HRFlowable(width="38%", thickness=1.2, color=teal, spaceAfter=10 * mm))
        elif line_text.startswith("## "):
            story.append(Paragraph(safe(strip_markdown_inline(line_text[3:])), h2))
        elif line_text.startswith("### "):
            heading = Paragraph(safe(strip_markdown_inline(line_text[4:])), h3)
            if line_text[4:].strip() == "Answer Card":
                pending_answer_heading = heading
            else:
                story.append(heading)
        elif line_text.startswith(">"):
            block: list[str] = []
            while index < len(lines) and lines[index].strip().startswith(">"):
                block.append(lines[index].strip()[1:].strip())
                index += 1
            index -= 1
            story.append(Paragraph(safe("<br/>".join(block)), quote))
        elif line_text.startswith("- "):
            story.append(Paragraph(safe(strip_markdown_inline(line_text[2:])), bullet, bulletText="•"))
        elif re.match(r"^!\[[^]]*]\([^)]+\)$", line_text):
            match = re.match(r"^!\[([^]]*)]\(([^)]+)\)$", line_text)
            assert match
            image_path = (markdown_path.parent / match.group(2)).resolve()
            if image_path.exists():
                with PILImage.open(image_path) as bitmap:
                    width_px, height_px = bitmap.size
                max_width, max_height = 160 * mm, 105 * mm
                scale = min(max_width / width_px, max_height / height_px, 1.0)
                story.append(Image(str(image_path), width=width_px * scale, height=height_px * scale))
                story.append(Spacer(1, 4 * mm))
        elif line_text.startswith("|"):
            rows: list[list[str]] = []
            while index < len(lines) and lines[index].strip().startswith("|"):
                cells = [cell.strip() for cell in lines[index].strip().strip("|").split("|")]
                rows.append(cells)
                index += 1
            index -= 1
            if len(rows) >= 3:
                rows.pop(1)  # Markdown separator row.
                table_data = []
                for row_index, row in enumerate(rows):
                    cell_style = ParagraphStyle(
                        f"Cell{row_index}", parent=base, fontSize=7.7, leading=9.5,
                        alignment=TA_CENTER, spaceAfter=0,
                        textColor=colors.white if row_index == 0 else ink,
                    )
                    table_data.append([
                        Paragraph(safe(strip_markdown_inline(cell)), cell_style) for cell in row
                    ])
                available = 170 * mm
                first_width = 17 * mm
                other = (available - first_width) / max(1, len(table_data[0]) - 1)
                table = Table(table_data, colWidths=[first_width] + [other] * (len(table_data[0]) - 1))
                table.setStyle(TableStyle([
                    ("BACKGROUND", (0, 0), (-1, 0), navy),
                    ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
                    ("BACKGROUND", (0, 1), (0, -1), pale),
                    ("GRID", (0, 0), (-1, -1), 0.5, line),
                    ("VALIGN", (0, 0), (-1, -1), "MIDDLE"),
                    ("TOPPADDING", (0, 0), (-1, -1), 5),
                    ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
                ]))
                table_group: list[Any] = []
                if pending_answer_heading is not None:
                    table_group.append(pending_answer_heading)
                    pending_answer_heading = None
                table_group.extend([table, Spacer(1, 6 * mm)])
                story.append(KeepTogether(table_group))
        else:
            rendered = safe(strip_markdown_inline(line_text))
            style = answer_style if line_text.startswith("**") and ":**" in line_text else base
            story.append(Paragraph(rendered, style))
        index += 1

    def decorate_page(canvas: Any, document: Any) -> None:
        canvas.saveState()
        width, height = A4
        canvas.setStrokeColor(line)
        canvas.setLineWidth(0.4)
        canvas.line(20 * mm, 15 * mm, width - 20 * mm, 15 * mm)
        canvas.setFillColor(muted)
        canvas.setFont(font_name, 7.5)
        canvas.drawString(20 * mm, 9.5 * mm, markdown_path.stem)
        canvas.drawRightString(width - 20 * mm, 9.5 * mm, f"Page {document.page}")
        canvas.restoreState()

    pdf_path.parent.mkdir(parents=True, exist_ok=True)
    document = SimpleDocTemplate(
        str(pdf_path), pagesize=A4, rightMargin=20 * mm, leftMargin=20 * mm,
        topMargin=18 * mm, bottomMargin=20 * mm,
        title=markdown_path.stem, author="Exam Pack Converter",
    )
    document.build(story, onFirstPage=decorate_page, onLaterPages=decorate_page)


def display_answer_results(markdown: str) -> None:
    """Print question headings and the answers already assembled in Markdown."""
    current_question = ""
    results: list[tuple[str, str]] = []
    for line in markdown.splitlines():
        if line.startswith("### ") and line[4:].strip() != "Answer Card":
            current_question = line[4:].strip()
        match = re.match(r"\*\*(?:Answer|Model answer|Reference text):\*\*\s*(.*)", line)
        if match:
            results.append((current_question, match.group(1).strip()))
    if not results:
        return
    for _, answer in results:
        print(answer)


def directory_signature(path: Path) -> tuple[tuple[str, int], ...]:
    """Return a stable snapshot of files and sizes under a pack directory."""
    snapshot: list[tuple[str, int]] = []
    for item in path.rglob("*"):
        try:
            if item.is_file():
                snapshot.append((item.relative_to(path).as_posix(), item.stat().st_size))
        except OSError:
            continue
    return tuple(sorted(snapshot))


def watch_for_new_packs(root: Path, output_dir: Path | None, name: str | None,
                        markdown_only: bool) -> int:
    """Poll root for newly created numeric pack directories and convert them."""
    root = root.resolve()
    if not root.is_dir():
        raise ValueError(f"Watch root is not a directory: {root}")
    seen = {p.name for p in root.iterdir() if p.is_dir() and re.match(r"^\d", p.name)}
    print(f"Watching {root} for new numeric exam-pack folders. Existing folders are skipped.")
    candidates: dict[str, tuple[tuple[tuple[str, int], ...], int]] = {}
    while True:
        current = {
            p.name: p for p in root.iterdir()
            if p.is_dir() and re.match(r"^\d", p.name)
        }
        for folder_name in list(candidates):
            if folder_name not in current:
                candidates.pop(folder_name, None)
        for folder_name, folder in current.items():
            if folder_name in seen:
                continue
            try:
                signature = directory_signature(folder)
            except OSError:
                continue
            previous, stable_count = candidates.get(folder_name, ((), 0))
            stable_count = stable_count + 1 if signature == previous and signature else 0
            candidates[folder_name] = (signature, stable_count)
            if stable_count < 2:
                continue
            try:
                pack = ExamPack(folder)
                actual_output = output_dir or (pack.root / "converted")
                base_name = name or f"exam_pack_{pack.root.name}"
                markdown_path = actual_output / f"{base_name}.md"
                pdf_path = actual_output / f"{base_name}.pdf"
                assets_dir = actual_output / f"{base_name}_assets"
                builder = MarkdownBuilder(pack, markdown_path, assets_dir)
                markdown = builder.build()
                # Markdown and answer extraction are complete; do not rediscover
                # this folder if an optional PDF dependency is unavailable.
                seen.add(folder_name)
                candidates.pop(folder_name, None)
                print(f"\nDetected and converted: {folder}")
                print(f"Markdown: {markdown_path}")
                if not markdown_only:
                    try:
                        render_markdown_pdf(markdown_path, pdf_path)
                        print(f"PDF:      {pdf_path}")
                    except RuntimeError as exc:
                        print(f"PDF skipped: {exc}", file=sys.stderr)
                display_answer_results(markdown)
            except (OSError, ValueError, json.JSONDecodeError, RuntimeError) as exc:
                # A newly copied folder may still be incomplete; retry on the next scan.
                print(f"Waiting for {folder_name} to become readable: {exc}", file=sys.stderr)
        time.sleep(2)


def parse_args(argv: Iterable[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Convert a JSON exam-pack directory into Markdown and PDF."
    )
    parser.add_argument("pack", type=Path, help="Path to the exam-pack directory")
    parser.add_argument(
        "-o", "--output-dir", type=Path,
        help="Output directory (default: <pack>/converted)",
    )
    parser.add_argument("--name", help="Base output filename (default: exam_pack_<pack id>)")
    parser.add_argument("--markdown-only", action="store_true", help="Skip PDF generation")
    parser.add_argument(
        "--watch", action="store_true",
        help="Watch the supplied directory for newly created numeric exam-pack folders",
    )
    return parser.parse_args(argv)


def main(argv: Iterable[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        if args.watch:
            watch_for_new_packs(args.pack, args.output_dir, args.name, args.markdown_only)
            return 0
        pack = ExamPack(args.pack)
        output_dir = (args.output_dir or (pack.root / "converted")).resolve()
        base_name = args.name or f"exam_pack_{pack.root.name}"
        markdown_path = output_dir / f"{base_name}.md"
        pdf_path = output_dir / f"{base_name}.pdf"
        assets_dir = output_dir / f"{base_name}_assets"

        builder = MarkdownBuilder(pack, markdown_path, assets_dir)
        markdown = builder.build()
        print(f"Markdown: {markdown_path}")
        if not args.markdown_only:
            render_markdown_pdf(markdown_path, pdf_path)
            print(f"PDF:      {pdf_path}")
        display_answer_results(markdown)
        return 0
    except KeyboardInterrupt:
        print("\nStopped.")
        return 0
    except (OSError, ValueError, json.JSONDecodeError, RuntimeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
