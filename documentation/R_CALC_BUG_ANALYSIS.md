# R Calculation Bug — Analysis & Fix

**Status: FIXED** (04.03.2026) — committed to GitHub

## Summary

R result (risk-multiple outcome) считается неправильно в **двух** местах:

| Компонент | Файл | Как считает | Проблема |
|-----------|------|-------------|----------|
| **Backtester** | `BacktestExecutor.cs:320` | `priceMove / |entry - current_SL|` | `current_SL` после trail → sl_dist → 0 → R взрывается |
| **Live Daemon** | `RCalc.cs:49` | SL hit → **всегда -1.0R** | Trail подтянул SL в профит → SL hit = win, но daemon считает -1.0R |
| **VirtualTracker** | `VirtualTracker.cs:165` | Тот же `RCalc.GetRResult()` | Та же проблема |

Правильный R: `(closePrice - entryPrice) / original_sl_dist` (для LONG; зеркально для SHORT).

`original_sl_dist` — зафиксирован при входе, хранится в `signal_data.sl_dist`.

---

## Backtester: как ломается

```
BacktestExecutor.cs, BuildTrade(), line 320:

    double slDistance = Math.Abs(pos.PriceOpen - pos.SL);  // ← BUG
    double priceMove = isBuy ? closePrice - pos.PriceOpen : pos.PriceOpen - closePrice;
    rResult = (priceMove / slDistance) - flatCostR;
```

### Сценарий: XTIUSD FADE LONG

```
Entry:       59.63
Original SL: 58.145    (sl_dist = 1.485)
Trail moves SL → 59.623  (profit lock, 0.007 from entry)

Gap down → closes at 59.49 (SL hit)

BUG calculation:
  slDistance = |59.63 - 59.623| = 0.007   ← trailed SL, not original!
  priceMove = 59.49 - 59.63 = -0.14
  R = -0.14 / 0.007 = -20.0              ← catastrophic

CORRECT calculation:
  slDistance = 1.485                       ← original from signal_data
  priceMove = 59.49 - 59.63 = -0.14
  R = -0.14 / 1.485 = -0.094             ← minor loss
```

Recalculated R из compare скрипта: **-0.094**. Research R: **+0.217**. Разница объяснима spread/slippage.

### Ещё примеры из Dec 2025

| Symbol | R (daemon raw) | R (recalculated) | R (research) | Причина |
|--------|---------------|-----------------|-------------|---------|
| UK100  | -277.30 | ~ -0.1 | N/A (unmatched) | trail SL почти = entry |
| USDCAD | -34.40 | -0.346 | +0.142 | trail lock, small gap |
| BTCUSD | -18.52 | -0.395 | +0.027 | trail lock, gap through |
| EURCAD | -17.15 | -0.046 | +6.327 | trail profit, gap |

### Fix: BacktestExecutor.cs

**1. Добавить поле в `BtPosition`:**

```csharp
public class BtPosition
{
    // ... existing fields ...
    public double OriginalSlDist { get; set; }   // NEW: original SL distance from signal_data
}
```

**2. При открытии позиции — парсить sl_dist из signal_data:**

```csharp
// In OpenPosition() method, after creating BtPosition:
pos.OriginalSlDist = ParseSlDist(action.SignalData) 
                     ?? Math.Abs(pos.PriceOpen - pos.SL);  // fallback to initial SL

private static double ParseSlDist(string? signalDataJson)
{
    if (string.IsNullOrEmpty(signalDataJson)) return 0;
    try
    {
        using var doc = JsonDocument.Parse(signalDataJson);
        if (doc.RootElement.TryGetProperty("sl_dist", out var prop) 
            && prop.ValueKind == JsonValueKind.Number)
            return prop.GetDouble();
    }
    catch { }
    return 0;
}
```

**3. В BuildTrade() — использовать original:**

```csharp
// Line 307 (cost_r):
double slDist = pos.OriginalSlDist > 0 
    ? pos.OriginalSlDist 
    : Math.Abs(pos.PriceOpen - pos.SL);

// Line 320 (r_result):
double slDistance = pos.OriginalSlDist > 0 
    ? pos.OriginalSlDist 
    : Math.Abs(pos.PriceOpen - pos.SL);
```

---

## Live Daemon: как ломается

```
RCalc.cs:

    if (reason == "SL")
    {
        if (protectorFired)
            return protector_lock_r;   // e.g. -0.5
        else
            return -1.0;               // ← HARDCODED!
    }
```

Для стратегий с фиксированным SL (без trail) это корректно: SL hit = -1.0R.

Для REMR с trail: SL hit может означать что trail подтянул SL в профит, потом цена развернулась и ударила trailed SL. Реальный R может быть +0.5R, +2.0R — что угодно.

### Примеры

```
REMR FADE LONG, CADCHF:
  Entry:      0.57627
  Orig SL:    0.57437  (sl_dist = 0.0019)
  Trail → SL: 0.57629  (в профит!)
  Close at SL: 0.57629
  
  RCalc says: -1.0R     ← WRONG (position closed in profit!)
  Correct:    (0.57629 - 0.57627) / 0.0019 = +0.01R (tiny profit minus costs)
```

### Fix: RCalc.cs

Для стратегий с trail нужен другой подход. REMR передаёт в `signal_data`:
- `sl_dist` — original SL distance
- `trail_trig_r`, `trail_dist_r` — trail parameters

**Вариант A: R из price + sl_dist (рекомендуемый)**

RCalc нужны: `closePrice`, `entryPrice`, `direction` — которых сейчас нет.

Reconciler (строка 191) вызывает `RCalc.GetRResult(closeReason, protectorFired, signalData)`. Нужно расширить сигнатуру:

```csharp
public static double? GetRResult(
    string? closeReason, 
    bool protectorFired, 
    string? signalDataJson,
    double entryPrice = 0,       // NEW
    double closePrice = 0,       // NEW  
    bool isBuy = true)           // NEW
{
    // Try price-based R first (for trail strategies)
    double slDist = ParseSignalField(signalDataJson, "sl_dist") ?? 0;
    if (slDist > 0 && entryPrice > 0 && closePrice > 0)
    {
        double priceMove = isBuy ? closePrice - entryPrice : entryPrice - closePrice;
        return Math.Round(priceMove / slDist, 4);
    }
    
    // Fallback: fixed R (for non-trail strategies)
    if (reason == "TP") return ParseSignalField(signalDataJson, "tp_r") ?? 1.0;
    if (reason == "SL")
    {
        if (protectorFired)
            return ParseSignalField(signalDataJson, "protector_lock_r") ?? -0.5;
        return -1.0;
    }
    return null;
}
```

**Reconciler.cs** — передать цены:

```csharp
// Line 191, add price data:
var rResult = RCalc.GetRResult(
    closeReason, 
    dbPos.ProtectorFired, 
    dbPos.SignalData,
    dbPos.PriceOpen,
    closePrice,
    dbPos.IsBuy);
```

**VirtualTracker.cs** — аналогично.

**Вариант B: minimal — только для sl_dist-стратегий**

Если signal_data содержит `sl_dist`, использовать price-based R. Иначе — старая логика. Backward-compatible.

---

## Что НЕ сломается от фикса

Стратегии без trail (fx_intraday, pairs_zscore) не отправляют `sl_dist` в signal_data → fallback на старую логику (-1.0R / +tp_r). Для них ничего не меняется.

REMR отправляет `sl_dist` → переключится на price-based R. Обе ветки (backtester и daemon) будут считать одинаково.

---

## Checklist

- [ ] `BacktestExecutor.cs`: добавить `OriginalSlDist` в `BtPosition`
- [ ] `BacktestExecutor.cs`: парсить `sl_dist` из `signal_data` при `OpenPosition()`
- [ ] `BacktestExecutor.cs`: использовать `OriginalSlDist` в `BuildTrade()` (строки 307, 320)
- [ ] `RCalc.cs`: расширить `GetRResult()` — принимать price data
- [ ] `RCalc.cs`: price-based R когда `sl_dist` есть в signal_data
- [ ] `Reconciler.cs`: передать `PriceOpen`, `closePrice`, `IsBuy` в `RCalc`
- [ ] `VirtualTracker.cs`: аналогично Reconciler
- [ ] Тест: daemon BT на Dec 2025 → сравнить с research → R diff << 0.1
- [ ] Тест: existing strategies (fx_intraday) → R не изменился (нет sl_dist → fallback)

---

## Приоритет

**Backtester** — критический. Без фикса бектесты бесполезны для любой trail-стратегии. Все R взорваны.

**RCalc/Daemon** — важный но менее срочный. В live trading R используется только для r_cap gate и dashboard display. r_cap=3 — если daemon считает каждый SL как -1.0R (даже trail wins), он будет слишком рано блокировать торговлю. Но не катастрофически — 3 SL подряд и так редкость.
