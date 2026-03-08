import os
import re
import csv
import json
import asyncio
import argparse
from dataclasses import dataclass, asdict
from typing import List, Optional, Dict, Any

from telethon import TelegramClient, functions, types
from telethon.errors import (
    FloodWaitError,
    SearchQueryEmptyError,
    QueryTooShortError,
    TimeoutError as TgTimeoutError,
)
from dotenv import load_dotenv
load_dotenv()

DEFAULT_QUERIES = [
    # Общие
    "crypto signals",
    "futures signals",
    "binance futures signals",
    # Структурные — ближе к формату сигнала
    "ENTRY1 ENTRY2 SL",
    "entry tp1 tp2 sl",
    "tp1 tp2 tp3 leverage",
    "#BTCUSDT SHORT entry",
    "#ETHUSDT LONG tp1",
    "STATUS LONG SHORT entry sl",
    "USDT 15m entry sl",
    # По паттернам каналов
    "free crypto futures",
    "okx futures signals",
    "bybit signals free",
]

SIGNAL_PATTERNS = {
    "entry":      re.compile(r"\bentry(?:\s*zone)?\b", re.I),
    "sl":         re.compile(r"\bsl\b|\bstop\s*loss\b", re.I),
    "tp1":        re.compile(r"\btp\s*1\b|\btp1\b", re.I),
    "tp2":        re.compile(r"\btp\s*2\b|\btp2\b", re.I),
    "tp3":        re.compile(r"\btp\s*3\b|\btp3\b", re.I),
    "long_short": re.compile(r"\blong\b|\bshort\b", re.I),
    "leverage":   re.compile(r"\b\d{1,3}\s*x\b|\bleverage\b|\bcross\b|\bisolated\b", re.I),
    "pair_usdt":  re.compile(r"\b[A-Z0-9]{2,15}USDT\b", re.I),
    "pair_slash": re.compile(r"\b[A-Z0-9]{2,15}/USDT\b", re.I),
}

BAD_PATTERNS = {
    "vip":            re.compile(r"\bvip\b|\bpremium\b|\bjoin\s+vip\b|\bpaid\b", re.I),
    "referral":       re.compile(r"\bref\b|\breferral\b|\bsign\s*up\b|\bregister\b|\bbonus\b", re.I),
    "analytics_only": re.compile(r"\blooks bullish\b|\blooks bearish\b|\bmarket update\b|\banalysis\b|\bchart\b", re.I),
}

MIN_MESSAGE_LEN = 20


@dataclass
class ChannelScore:
    query: str
    title: str
    username: Optional[str]
    entity_id: int
    members: Optional[int]
    verified: bool
    scam: bool
    fake: bool
    broadcast: bool
    megagroup: bool
    recent_messages_checked: int
    recent_signal_like_messages: int
    signal_ratio: float
    entry_hits: int
    sl_hits: int
    tp1_hits: int
    tp2_hits: int
    tp3_hits: int
    long_short_hits: int
    leverage_hits: int
    pair_hits: int
    vip_hits: int
    referral_hits: int
    analytics_hits: int
    score_raw: float
    score_final: float
    sample_link: Optional[str]


def normalize_text(text: str) -> str:
    text = text.replace("\n", " ").replace("\t", " ")
    return re.sub(r"\s+", " ", text).strip()


def build_public_link(username: Optional[str]) -> Optional[str]:
    return f"https://t.me/{username}" if username else None


def compute_message_features(text: str) -> Dict[str, int]:
    t = normalize_text(text)
    if len(t) < MIN_MESSAGE_LEN:
        return {k: 0 for k in ["signal_like","entry","sl","tp1","tp2","tp3",
                                "long_short","leverage","pair","vip","referral","analytics_only"]}

    entry      = int(bool(SIGNAL_PATTERNS["entry"].search(t)))
    sl         = int(bool(SIGNAL_PATTERNS["sl"].search(t)))
    tp1        = int(bool(SIGNAL_PATTERNS["tp1"].search(t)))
    tp2        = int(bool(SIGNAL_PATTERNS["tp2"].search(t)))
    tp3        = int(bool(SIGNAL_PATTERNS["tp3"].search(t)))
    long_short = int(bool(SIGNAL_PATTERNS["long_short"].search(t)))
    leverage   = int(bool(SIGNAL_PATTERNS["leverage"].search(t)))
    pair       = int(bool(SIGNAL_PATTERNS["pair_usdt"].search(t) or SIGNAL_PATTERNS["pair_slash"].search(t)))
    vip        = int(bool(BAD_PATTERNS["vip"].search(t)))
    referral   = int(bool(BAD_PATTERNS["referral"].search(t)))
    analytics  = int(bool(BAD_PATTERNS["analytics_only"].search(t)))

    signal_like = int(
        (entry and sl and tp1) or
        (sl and tp1 and (long_short or pair)) or
        (entry and tp1 and (tp2 or tp3))
    )

    return {"signal_like": signal_like, "entry": entry, "sl": sl,
            "tp1": tp1, "tp2": tp2, "tp3": tp3, "long_short": long_short,
            "leverage": leverage, "pair": pair, "vip": vip,
            "referral": referral, "analytics_only": analytics}


async def fetch_members(client, entity) -> Optional[int]:
    try:
        full = await client(functions.channels.GetFullChannelRequest(entity))
        return getattr(full.full_chat, "participants_count", None)
    except Exception:
        return None


async def inspect_channel(client, query, entity, history_limit) -> Optional[ChannelScore]:
    username  = getattr(entity, "username", None)
    title     = getattr(entity, "title", "") or ""
    verified  = bool(getattr(entity, "verified", False))
    scam      = bool(getattr(entity, "scam", False))
    fake      = bool(getattr(entity, "fake", False))
    broadcast = bool(getattr(entity, "broadcast", False))
    megagroup = bool(getattr(entity, "megagroup", False))

    msg_count   = 0
    signal_like = 0
    counters    = {k: 0 for k in ["entry","sl","tp1","tp2","tp3","long_short",
                                   "leverage","pair","vip","referral","analytics_only"]}
    try:
        async for msg in client.iter_messages(entity, limit=history_limit):
            text = msg.raw_text or ""
            if not text:
                continue
            feats = compute_message_features(text)
            msg_count   += 1
            signal_like += feats["signal_like"]
            for k in counters:
                counters[k] += feats[k]
    except FloodWaitError as e:
        print(f"  [FloodWait] {title}: ждём {e.seconds}s")
        await asyncio.sleep(e.seconds + 1)
    except Exception as e:
        print(f"  [WARN] {title}: {e}")
        return None

    if msg_count == 0:
        return None

    signal_ratio = signal_like / msg_count

    score_raw = (
        signal_ratio * 100
        + counters["entry"]      * 0.4
        + counters["sl"]         * 0.5
        + counters["tp1"]        * 0.7
        + counters["tp2"]        * 0.4
        + counters["tp3"]        * 0.4
        + counters["long_short"] * 0.3
        + counters["leverage"]   * 0.2
        + counters["pair"]       * 0.3
        - counters["vip"]        * 0.5
        - counters["referral"]   * 0.7
        - counters["analytics_only"] * 0.3
    )

    penalty = 0.0
    if scam: penalty += 100
    if fake: penalty += 100
    if counters["vip"] > msg_count * 0.5: penalty += 10

    score_final = score_raw - penalty
    members     = await fetch_members(client, entity)

    return ChannelScore(
        query=query, title=title, username=username, entity_id=entity.id,
        members=members, verified=verified, scam=scam, fake=fake,
        broadcast=broadcast, megagroup=megagroup,
        recent_messages_checked=msg_count,
        recent_signal_like_messages=signal_like,
        signal_ratio=round(signal_ratio, 4),
        entry_hits=counters["entry"], sl_hits=counters["sl"],
        tp1_hits=counters["tp1"], tp2_hits=counters["tp2"], tp3_hits=counters["tp3"],
        long_short_hits=counters["long_short"], leverage_hits=counters["leverage"],
        pair_hits=counters["pair"], vip_hits=counters["vip"],
        referral_hits=counters["referral"], analytics_hits=counters["analytics_only"],
        score_raw=round(score_raw, 2), score_final=round(score_final, 2),
        sample_link=build_public_link(username),
    )


async def search_channels(client, query, limit) -> List[types.Channel]:
    try:
        result = await client(functions.contacts.SearchRequest(q=query, limit=limit))
    except (SearchQueryEmptyError, QueryTooShortError, TgTimeoutError) as e:
        print(f"[WARN] Search '{query}': {e}"); return []
    except FloodWaitError as e:
        print(f"[FloodWait] Search '{query}': ждём {e.seconds}s")
        await asyncio.sleep(e.seconds + 1); return []

    uniq = {}
    for chat in result.chats:
        if isinstance(chat, types.Channel):
            uniq[chat.id] = chat
    return list(uniq.values())


def dedupe_scores(scores: List[ChannelScore]) -> List[ChannelScore]:
    best: Dict[int, ChannelScore] = {}
    for s in scores:
        if s.entity_id not in best or s.score_final > best[s.entity_id].score_final:
            best[s.entity_id] = s
    return list(best.values())


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--session",       default="channel_scan_session")
    ap.add_argument("--search-limit",  type=int,   default=30)
    ap.add_argument("--history-limit", type=int,   default=80)
    ap.add_argument("--min-score",     type=float, default=15.0)
    ap.add_argument("--out-prefix",    default="channel_scan")
    args = ap.parse_args()

    api_id   = int(os.environ["TG_API_ID"])
    api_hash = os.environ["TG_API_HASH"]

    client = TelegramClient(args.session, api_id, api_hash)
    await client.start()

    all_scores: List[ChannelScore] = []

    for query in DEFAULT_QUERIES:
        print(f"\n=== ПОИСК: {query} ===")
        channels = await search_channels(client, query, args.search_limit)
        print(f"Найдено каналов: {len(channels)}")

        for i, ch in enumerate(channels, 1):
            title    = getattr(ch, "title", "")
            username = getattr(ch, "username", None)
            print(f"  [{i:02d}/{len(channels):02d}] {title} @{username or '-'}")
            score = await inspect_channel(client, query, ch, args.history_limit)
            if score:
                all_scores.append(score)
            await asyncio.sleep(0.4)

    await client.disconnect()

    uniq   = dedupe_scores(all_scores)
    uniq.sort(key=lambda x: x.score_final, reverse=True)
    result = [x for x in uniq if x.score_final >= args.min_score]

    print(f"\n{'='*80}")
    print(f"  ИТОГО уникальных каналов: {len(uniq)}   с score >= {args.min_score}: {len(result)}")
    print(f"{'='*80}")
    for i, r in enumerate(result[:50], 1):
        print(
            f"{i:02d}. {r.title[:35]:35} "
            f"@{(r.username or '-'):25} "
            f"score={r.score_final:6.1f}  "
            f"sig={r.signal_ratio:4.0%}  "
            f"msgs={r.recent_messages_checked:3d}  "
            f"{'✅' if not r.scam and not r.fake else '❌'}"
        )

    csv_path  = f"{args.out_prefix}.csv"
    json_path = f"{args.out_prefix}.json"

    if result:
        with open(csv_path, "w", newline="", encoding="utf-8-sig") as f:
            w = csv.DictWriter(f, fieldnames=list(asdict(result[0]).keys()))
            w.writeheader()
            for r in result:
                w.writerow(asdict(r))
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump([asdict(r) for r in result], f, ensure_ascii=False, indent=2)

    print(f"\nСохранено: {csv_path}, {json_path}")


if __name__ == "__main__":
    asyncio.run(main())
