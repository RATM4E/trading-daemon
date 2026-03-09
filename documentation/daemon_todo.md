# Daemon — TODO для SQUEEZE_ZZ_ATR

## 1. OCO grouping: заменить совпадение по `signal_data` на поле `oco_group`

**Проблема:**  
`StateManager.Pending.cs` → `GetOcoPendingTickets()` ищет сиблинга по полному
совпадению строки `signal_data`:

```sql
SELECT ticket FROM pending_orders
WHERE terminal_id = @tid AND status = 'open'
  AND signal_data = @sd AND ticket != @excl
```

У стратегии `signal_data` у BUY_STOP и SELL_STOP **разные** — `sl_dist` различается
(long ≠ short). Совпадения не будет → сиблинг не отменится при исполнении одного
из ордеров OCO.

**Решение:**  
Добавить отдельное поле `oco_group` (string) в протокол `ENTER_PENDING` и в БД
`pending_orders`. Группировку вести по `oco_group`, не по `signal_data`.

Стратегия уже передаёт:
```python
"oco_group": f"{sym}_{tf_sec}_{state.active_oco.placed_bar}"
# например: "GBPUSD_1200_4821"
```

**Правки в демоне:**

| Файл | Что изменить |
|------|-------------|
| `pending_orders` (SQLite schema) | Добавить колонку `oco_group TEXT` |
| `StateManager.Pending.cs` | Сохранять `oco_group` при `SavePendingOrder()`; читать в `ReadPendingRecord()` |
| `StateManager.Pending.cs` → `GetOcoPendingTickets()` | Принимать `ocoGroup` вместо `signalData`; WHERE по `oco_group = @og` |
| `PendingOrderManager.cs` | Передавать `rec.OcoGroup` в `GetOcoPendingTickets()` |
| `VirtualTracker.cs` | Аналогично для виртуального трекера |
| `BacktestExecutor.cs` | Аналогично для бэктестера |
| `Protocol / runner.py` | Парсить поле `oco_group` из ENTER_PENDING action |

---

## 2. RCalc: различать `sl_dist` по направлению позиции

**Проблема:**  
`RCalc.cs` читает единственное поле `sl_dist`:

```csharp
double slDist = ParseSignalField(signalDataJson, "sl_dist") ?? 0;
```

У стратегии в `signal_data` уже лежит корректный `sl_dist` для каждого направления
(BUY_STOP → `sl_dist_long`, SELL_STOP → `sl_dist_short`). Поле называется `sl_dist`
в обоих случаях — Reconciler получит правильное значение **автоматически**, так как
`signal_data` копируется в позицию при исполнении ордера.

**Статус: не требует правок** при текущей схеме — каждый pending несёт свой `sl_dist`.

---

## 3. Проверить: копируется ли `signal_data` из pending в position при исполнении

**Проблема:**  
R-расчёт работает только если `signal_data` (с полем `sl_dist`) переносится
из записи `pending_orders` в запись `positions` при срабатывании стоп-ордера.

**Где проверить:**  
`PendingOrderManager.cs` → блок заполнения позиции при `FILLED`:
```csharp
SignalData = rec.SignalData,   // ← должна быть эта строка
```
Если её нет — R-расчёт вернёт 0.

**Статус:** судя по коду (~строка 253) поле присваивается. Стоит верифицировать
в live на первой сделке через dashboard → Positions → signal_data.

---

## Приоритет

| # | Задача | Блокирует |
|---|--------|-----------|
| 1 | OCO group по отдельному полю | Корректную отмену сиблинга при исполнении OCO |
| 3 | Верификация signal_data в position | Корректный R-расчёт в live |
| 2 | RCalc sl_dist | Не блокирует (уже работает) |
