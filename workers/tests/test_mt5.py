import MetaTrader5 as mt5
import json

# Путь к вашему терминалу — поменять на свой
TERMINAL_PATH = r"C:\Program Files\Five Percent Online MetaTrader 5\terminal64.exe"

if not mt5.initialize(path=TERMINAL_PATH):
    print(f"FAIL: {mt5.last_error()}")
    quit()

info = mt5.terminal_info()
print(f"Terminal: {info.name}")
print(f"Company: {info.company}")
print(f"Connected: {info.connected}")

acc = mt5.account_info()
print(f"Account: {acc.login}")
print(f"Balance: {acc.balance}")
print(f"Equity: {acc.equity}")

# Проверяем получение баров
rates = mt5.copy_rates_from_pos("EURUSD", mt5.TIMEFRAME_H1, 0, 5)
if rates is not None:
    print(f"Got {len(rates)} bars for EURUSD H1")
    for r in rates:
        print(f"  {r}")
else:
    print(f"FAIL getting rates: {mt5.last_error()}")

# Проверяем symbol_info
si = mt5.symbol_info("EURUSD")
if si:
    print(f"\nEURUSD card:")
    print(f"  digits: {si.digits}")
    print(f"  point: {si.point}")
    print(f"  trade_tick_size: {si.trade_tick_size}")
    print(f"  trade_tick_value: {si.trade_tick_value}")
    print(f"  volume_min: {si.volume_min}")
    print(f"  volume_max: {si.volume_max}")
    print(f"  volume_step: {si.volume_step}")
    print(f"  trade_contract_size: {si.trade_contract_size}")

mt5.shutdown()
print("\nDONE — MT5 connection works")