"""
Анализ статистики ENTRY2 из истории Cryptonus_Trade
Запуск: python analyze_entry2.py
"""
import json, re
from collections import defaultdict
from datetime import datetime

data = json.load(open('history_cache.json'))
msgs = data if isinstance(data, list) else list(data.values())
msgs.sort(key=lambda x: x['id'])
print(f"Сообщений в кэше: {len(msgs)}")

# ── Парсеры ──────────────────────────────────────────────────────────────────

def parse_signal(text):
    sym = re.search(r'#(\w+USDT)', text)
    e1  = re.search(r'ENTRY1\s*[:\s]\s*([\d.]+)', text)
    e2  = re.search(r'ENTRY2\s*[:\s]\s*([\d.]+)', text)
    sl  = re.search(r'SL\s*[:\s]\s*([\d.]+)', text)
    tp1 = re.search(r'TP1\s*[:\s]\s*([\d.]+)', text)
    tp2 = re.search(r'TP2\s*[:\s]\s*([\d.]+)', text)
    tp3 = re.search(r'TP3\s*[:\s]\s*([\d.]+)', text)
    direction = 'LONG' if 'LONG' in text else ('SHORT' if 'SHORT' in text else None)
    if not (sym and e1 and sl and tp1 and direction):
        return None
    return {
        'symbol': sym.group(1), 'direction': direction,
        'entry1': float(e1.group(1)), 'entry2': float(e2.group(1)) if e2 else None,
        'sl': float(sl.group(1)), 'tp1': float(tp1.group(1)),
        'tp2': float(tp2.group(1)) if tp2 else None,
        'tp3': float(tp3.group(1)) if tp3 else None,
    }

def parse_entry2_update(text):
    m = re.match(r'#(\w+USDT)\s+OPEN ENTRY2\s+([\d.]+)', text.strip())
    if not m: return None
    tp1 = re.search(r'TP1\s*[:\s]\s*([\d.]+)', text)
    return {'symbol': m.group(1), 'entry2': float(m.group(2)),
            'tp1': float(tp1.group(1)) if tp1 else None}

def classify(text):
    t = text.upper()
    if 'STATUS' in t and 'ENTRY1' in t and 'SL' in t: return 'NEW_SIGNAL'
    if 'OPEN ENTRY2' in t: return 'ENTRY2'
    if '⛔' in text or (' SL ' in t and t.count('#') == 1 and 'ENTRY' not in t): return 'SL_HIT'
    if re.search(r'TP\s*[123].*✅', text): return 'TP_HIT'
    if 'CLOSE' in t and 'FINAL' in t: return 'CLOSE'
    return None

# ── Прогон ───────────────────────────────────────────────────────────────────

open_trades = {}   # symbol → signal dict
stats = {
    'total_new':       0,   # всего новых сигналов
    'had_entry2':      0,   # сигналов где пришёл OPEN ENTRY2
    'e1_only_tp':      0,   # вышли по TP (только E1 заполнился)
    'e1_only_sl':      0,   # вышли по SL (только E1)
    'e2_tp':           0,   # вышли по TP (E2 уже был)
    'e2_sl':           0,   # вышли по SL (E2 уже был)
    # R-статистика
    'e1_only_tp_r':    [],
    'e1_only_sl_r':    [],
    'e2_tp_r':         [],
    'e2_sl_r':         [],
}

for msg in msgs:
    text = msg['text']
    kind = classify(text)

    if kind == 'NEW_SIGNAL':
        sig = parse_signal(text)
        if sig:
            open_trades[sig['symbol']] = {**sig, 'has_e2': False}
            stats['total_new'] += 1

    elif kind == 'ENTRY2':
        upd = parse_entry2_update(text)
        if upd and upd['symbol'] in open_trades:
            open_trades[upd['symbol']]['has_e2'] = True
            open_trades[upd['symbol']]['entry2_actual'] = upd['entry2']
            if upd['tp1']:
                open_trades[upd['symbol']]['tp1_new'] = upd['tp1']
            stats['had_entry2'] += 1

    elif kind in ('TP_HIT', 'SL_HIT', 'CLOSE'):
        sym_m = re.search(r'#(\w+USDT)', text)
        if not sym_m: continue
        sym = sym_m.group(1)
        if sym not in open_trades: continue
        sig = open_trades.pop(sym)

        is_long = sig['direction'] == 'LONG'
        entry   = sig['entry1']
        sl      = sig['sl']
        tp1     = sig.get('tp1_new') or sig['tp1']
        sl_dist = abs(entry - sl)
        if sl_dist == 0: continue

        if kind == 'SL_HIT':
            r = -1.0
            if sig['has_e2']:
                stats['e2_sl'] += 1; stats['e2_sl_r'].append(r)
            else:
                stats['e1_only_sl'] += 1; stats['e1_only_sl_r'].append(r)
        else:  # TP или CLOSE
            r = abs(tp1 - entry) / sl_dist
            if sig['has_e2']:
                stats['e2_tp'] += 1; stats['e2_tp_r'].append(r)
            else:
                stats['e1_only_tp'] += 1; stats['e1_only_tp_r'].append(r)

# ── Вывод ────────────────────────────────────────────────────────────────────

def pf(wins, losses, win_r, loss_r):
    gross_win  = sum(win_r) if win_r else 0
    gross_loss = abs(sum(loss_r)) if loss_r else 0
    return gross_win / gross_loss if gross_loss else float('inf')

def wr(tp, sl): return tp/(tp+sl)*100 if (tp+sl) > 0 else 0

e1_tp = stats['e1_only_tp']; e1_sl = stats['e1_only_sl']
e2_tp = stats['e2_tp'];      e2_sl = stats['e2_sl']

print(f"\n{'='*55}")
print(f"  Всего сигналов:          {stats['total_new']}")
print(f"  Из них с OPEN ENTRY2:    {stats['had_entry2']}  ({stats['had_entry2']/max(stats['total_new'],1)*100:.1f}%)")
print(f"{'='*55}")
print(f"\n  БЕЗ ENTRY2 (вошли только по E1):")
print(f"    TP: {e1_tp}   SL: {e1_sl}   WR: {wr(e1_tp,e1_sl):.1f}%")
print(f"    PF: {pf(e1_tp,e1_sl,stats['e1_only_tp_r'],stats['e1_only_sl_r']):.2f}")
print(f"    Avg R win: {sum(stats['e1_only_tp_r'])/max(e1_tp,1):.2f}R")

print(f"\n  С ENTRY2 (сигнал дошёл до второго входа):")
print(f"    TP: {e2_tp}   SL: {e2_sl}   WR: {wr(e2_tp,e2_sl):.1f}%")
print(f"    PF: {pf(e2_tp,e2_sl,stats['e2_tp_r'],stats['e2_sl_r']):.2f}")
print(f"    Avg R win: {sum(stats['e2_tp_r'])/max(e2_tp,1):.2f}R")

print(f"\n  Вывод:")
pf_e1 = pf(e1_tp,e1_sl,stats['e1_only_tp_r'],stats['e1_only_sl_r'])
pf_e2 = pf(e2_tp,e2_sl,stats['e2_tp_r'],stats['e2_sl_r'])
if pf_e2 > pf_e1:
    print(f"    ✅ Сигналы с ENTRY2 лучше (PF {pf_e2:.2f} vs {pf_e1:.2f})")
    print(f"    → Имеет смысл ждать ENTRY2 перед входом")
else:
    print(f"    ❌ Сигналы без ENTRY2 лучше (PF {pf_e1:.2f} vs {pf_e2:.2f})")
    print(f"    → Ждать ENTRY2 не выгодно, входить сразу на E1")
