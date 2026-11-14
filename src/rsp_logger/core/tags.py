"""config/tags.csv 로딩, 읽기 묶음 계산, 원시값 → 환산값."""
import csv
from dataclasses import dataclass

from ..xgt import protocol as p


@dataclass
class Tag:
    key: str
    name: str
    addr: str
    type: str  # INT16 / INT32 / BIT
    scale: float
    offset: float
    unit: str
    unconfirmed: bool
    note: str
    area: str
    word: int
    bit: int | None


def load_tags(path):
    tags = []
    with open(path, encoding="utf-8-sig", newline="") as f:
        for r in csv.DictReader(f):
            t = r["타입"].strip().upper()
            if t == "BIT":
                area, word, bit = p.bit_addr(r["주소"])
            elif t in ("INT16", "INT32"):
                (area, word), bit = p.word_addr(r["주소"]), None
            else:
                raise ValueError(f"{r['key']}: 모르는 타입 {t}")
            tags.append(Tag(r["key"], r["이름"], r["주소"], t, float(r["배율"]), float(r["오프셋"]),
                            r["단위"], r["확인필요"].strip().upper() == "Y", r["비고"], area, word, bit))
    return tags


def plan_blocks(tags, max_gap=50, max_words=700):
    """필요한 워드를 영역별로 묶어 요청 수를 줄인다 -> [(area, start, count)]"""
    need = {}
    for t in tags:
        need.setdefault(t.area, set()).update(range(t.word, t.word + (2 if t.type == "INT32" else 1)))
    blocks = []
    for area, words in sorted(need.items()):
        start = prev = None
        for w in sorted(words):
            if start is None:
                start = prev = w
            elif w - prev > max_gap or w - start + 1 > max_words:
                blocks.append((area, start, prev - start + 1))
                start = prev = w
            else:
                prev = w
        blocks.append((area, start, prev - start + 1))
    return blocks


def _s16(v):
    return v - 0x10000 if v & 0x8000 else v


def decode(tags, blocks, results):
    """-> (환산값 dict, 원시값 dict)"""
    mem = {}
    for (area, start, _n), words in zip(blocks, results):
        for i, v in enumerate(words):
            mem[(area, start + i)] = v
    values, raws = {}, {}
    for t in tags:
        lo = mem[(t.area, t.word)]
        if t.type == "BIT":
            raw = (lo >> t.bit) & 1
            values[t.key] = raw
        else:
            if t.type == "INT32":
                # TODO(확인필요): 하위 워드가 앞 주소라고 가정 (LS 기본). 현장에서 XG5000 값과 대조
                v = (mem[(t.area, t.word + 1)] << 16) | lo
                raw = v - 0x1_0000_0000 if v & 0x8000_0000 else v
            else:
                raw = _s16(lo)
            values[t.key] = raw * t.scale + t.offset
        raws[t.key] = raw
    return values, raws


def suggest_scale(raw, hmi, offset=0.0):
    """HMI 표시값과 PLC 원시값으로 배율 계산 (HMI = 원시값 × 배율 + 오프셋). 원시값 0이면 계산 불가 -> None"""
    return None if raw == 0 else float(f"{(hmi - offset) / raw:.6g}")


def save_tags_csv(path, header, rows):
    """rows(list of dict)를 tags.csv 로 저장. 먼저 임시 파일로 써서 load_tags 로 검사한 뒤 바꿔 끼운다 —
    잘못된 주소·타입·숫자가 있으면 ValueError 를 내고 원래 파일은 그대로 둔다."""
    from pathlib import Path
    path = Path(path)
    tmp = path.with_suffix(".tmp")
    with open(tmp, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.DictWriter(f, header)
        w.writeheader()
        w.writerows(rows)
    try:
        load_tags(tmp)
    except Exception as e:
        tmp.unlink(missing_ok=True)
        raise ValueError(f"저장 안 함 — 잘못된 값: {e}") from e
    tmp.replace(path)
