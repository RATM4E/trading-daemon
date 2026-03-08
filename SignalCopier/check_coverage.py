"""
Сравнение покрытия символов канала между OKX и Binance Futures.
Использует только публичное API (ключи не нужны).

Запуск:
    pip install ccxt
    python check_coverage.py
"""

import json
import re
from collections import Counter
from pathlib import Path

import ccxt

CACHE_FILE = "history_cache.json"

def load_symbols_from_cache() -> Counter:
    msgs = json.loads(Path(CACHE_FILE).read_text(encoding="utf-8"))
    counter = Counter()
    for msg in msgs:
        text = msg.get("text", "")
        if "STATUS" in text and "ENTRY1" in text and "SL" in text:
            m = re.search(r"#(\w+USDT)", text)
            if m:
                counter[m.group(1)] += 1
    return counter

def get_okx_futures() -> set[str]:
    ex = ccxt.okx()
    markets = ex.load_markets()
    result = set()
    for symbol, info in markets.items():
        if info.get("type") == "swap" and info.get("quote") == "USDT" and info.get("active"):
            # OKX swap symbol: BTC/USDT:USDT → нам нужен BTCUSDT
            base = info.get("base", "")
            result.add(base + "USDT")
    return result

def get_binance_futures() -> set[str]:
    import urllib.request
    url = "https://fapi.binance.com/fapi/v1/exchangeInfo"
    with urllib.request.urlopen(url, timeout=10) as r:
        data = json.loads(r.read())
    result = set()
    for s in data["symbols"]:
        if s["quoteAsset"] == "USDT" and s["status"] == "TRADING" and s["contractType"] == "PERPETUAL":
            result.add(s["symbol"])
    return result

def main():
    print("Загружаем символы из кэша...")
    channel_symbols = load_symbols_from_cache()
    total_signals = sum(channel_symbols.values())
    unique_symbols = len(channel_symbols)
    print(f"  Уникальных символов : {unique_symbols}")
    print(f"  Всего сигналов      : {total_signals}")

    print("\nЗагружаем рынки OKX...")
    okx = get_okx_futures()
    print(f"  OKX фьючерсы USDT   : {len(okx)} символов")

    print("\nЗагружаем рынки Binance...")
    bnb = get_binance_futures()
    print(f"  Binance фьючерсы    : {len(bnb)} символов")

    # Покрытие по количеству сигналов (взвешенное)
    okx_signals = sum(v for k, v in channel_symbols.items() if k in okx)
    bnb_signals = sum(v for k, v in channel_symbols.items() if k in bnb)

    # Покрытие по уникальным символам
    okx_uniq = {k for k in channel_symbols if k in okx}
    bnb_uniq = {k for k in channel_symbols if k in bnb}
    missing_okx = {k for k in channel_symbols if k not in okx}
    missing_bnb = {k for k in channel_symbols if k not in bnb}

    W = 60
    print(f"\n{'═'*W}")
    print(f"  ПОКРЫТИЕ ПО СИГНАЛАМ (взвешенное)")
    print(f"{'═'*W}")
    print(f"  OKX     : {okx_signals:>5} / {total_signals}  ({okx_signals/total_signals*100:.1f}%)")
    print(f"  Binance : {bnb_signals:>5} / {total_signals}  ({bnb_signals/total_signals*100:.1f}%)")

    print(f"\n{'═'*W}")
    print(f"  ПОКРЫТИЕ ПО УНИКАЛЬНЫМ СИМВОЛАМ")
    print(f"{'═'*W}")
    print(f"  OKX     : {len(okx_uniq):>3} / {unique_symbols}  ({len(okx_uniq)/unique_symbols*100:.1f}%)")
    print(f"  Binance : {len(bnb_uniq):>3} / {unique_symbols}  ({len(bnb_uniq)/unique_symbols*100:.1f}%)")

    # Отсутствующие на OKX — по убыванию сигналов
    print(f"\n{'═'*W}")
    print(f"  СИМВОЛЫ КАНАЛА КОТОРЫХ НЕТ НА OKX ({len(missing_okx)} шт.)")
    print(f"{'─'*W}")
    for sym in sorted(missing_okx, key=lambda s: -channel_symbols[s]):
        on_bnb = "✅ Binance" if sym in bnb else "❌ нет нигде"
        print(f"  {sym:<18} {channel_symbols[sym]:>4} сигн.  {on_bnb}")

    if missing_bnb:
        print(f"\n{'═'*W}")
        print(f"  СИМВОЛЫ КАНАЛА КОТОРЫХ НЕТ НА BINANCE ({len(missing_bnb)} шт.)")
        print(f"{'─'*W}")
        for sym in sorted(missing_bnb, key=lambda s: -channel_symbols[s]):
            on_okx = "✅ OKX" if sym in okx else "❌ нет нигде"
            print(f"  {sym:<18} {channel_symbols[sym]:>4} сигн.  {on_okx}")

    # Есть только на OKX но не на Binance
    only_okx = {k for k in channel_symbols if k in okx and k not in bnb}
    only_bnb = {k for k in channel_symbols if k in bnb and k not in okx}
    if only_okx:
        print(f"\n  Только на OKX (не на Binance): {', '.join(sorted(only_okx))}")
    if only_bnb:
        print(f"  Только на Binance (не на OKX): {', '.join(sorted(only_bnb))}")

if __name__ == "__main__":
    main()
