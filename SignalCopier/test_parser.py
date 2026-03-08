"""
Тест парсера — запускать без ccxt/telethon
    python test_parser.py
"""

import sys
sys.path.insert(0, ".")

from signal_copier import parse_signal, is_bar_boundary
import time

SAMPLE = """Cryptonus_Trade
#RLCUSDT 15m
STATUS : LONG 🚀
👉 ENTRY1 : 0.3681 👉 ENTRY2 : 0.346
🎯 TP1 : 0.3755 (💲 2%)
🎯 TP2 : 0.3828 (💲 4%)
🎯 TP3 : 0.3902 (💲 6%)
🚫 SL: 0.334971
LEVERAGE: Cross 20x"""

SAMPLE_SHORT = """#BTCUSDT 1h
STATUS:SHORT
ENTRY1 : 65000 ENTRY2 : 65500
TP1 : 64000 TP2 : 63000 TP3 : 62000
SL: 66200
LEVERAGE: Cross 10x"""

def test_long():
    s = parse_signal(SAMPLE)
    assert s is not None, "Parse returned None"
    assert s.symbol    == "RLCUSDT"
    assert s.direction == "LONG"
    assert s.entry1    == 0.3681
    assert s.entry2    == 0.346
    assert s.tp1       == 0.3755
    assert s.tp2       == 0.3828
    assert s.tp3       == 0.3902
    assert s.sl        == 0.334971
    assert s.leverage  == 20
    assert s.okx_symbol == "RLC/USDT:USDT"
    print(f"✅ LONG parse OK  sl_dist={s.sl_dist:.6f}  R_to_TP1={s.r_tp1:.2f}")

def test_short():
    s = parse_signal(SAMPLE_SHORT)
    assert s is not None
    assert s.direction == "SHORT"
    assert s.symbol    == "BTCUSDT"
    assert s.leverage  == 10
    assert s.okx_symbol == "BTC/USDT:USDT"
    print(f"✅ SHORT parse OK  sl_dist={s.sl_dist:.2f}")

def test_invalid():
    s = parse_signal("Hello world, buy now!")
    assert s is None
    print("✅ Invalid message → None OK")

def test_timing_gate():
    now = int(time.time())
    offset = now % 300
    inside = offset < 45
    result = is_bar_boundary()
    assert result == inside, f"TimingGate mismatch: offset={offset}"
    print(f"✅ TimingGate OK  offset={offset}s  result={result}")

if __name__ == "__main__":
    test_long()
    test_short()
    test_invalid()
    test_timing_gate()
    print("\nAll tests passed!")
