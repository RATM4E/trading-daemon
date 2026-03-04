# SymbolResolver Integration Plan

## Что заменяем

Три отдельных системы маппинга → один `SymbolResolver`:

| Было | Стало |
|------|-------|
| `ConnectorManager.SymbolMapper` (per-terminal symbol_map) | `SymbolResolver.ToBroker()` / `ToCanonical()` |
| `CostModelLoader.ResolveCanonical()` (alias resolver) | Встроен в SymbolResolver |
| `mt5_worker.py SYMBOL_ALIASES` (хардкод Python) | Убрать. Таблицу слать из C# |

---

## Шаг 1: Program.cs — инициализация

```csharp
// После загрузки config и cost_model:
var symbolResolver = new SymbolResolver();

// 1. Load cost model aliases (global)
if (File.Exists(costModelPath))
    symbolResolver.LoadCostModelAliases(File.ReadAllText(costModelPath));

// 2. Load per-terminal symbol_map (overrides)
foreach (var tc in config.Terminals.Where(t => t.Enabled))
    symbolResolver.LoadTerminalMap(tc.Id, tc.SymbolMap);

// 3. Pass to ConnectorManager (replaces old SymbolMapper + aliasResolver)
connector.SetSymbolResolver(symbolResolver);

// 4. Pass to dashboard (for sizing + backtest)
dashboard.SetSymbolResolver(symbolResolver);
```

---

## Шаг 2: ConnectorManager — замена SymbolMapper

### Убрать:
- `_symbolMappers` dict
- `SymbolMapper` class (или оставить deprecated)
- `_aliasResolver` field
- `SetAliasResolver()` method

### Добавить:
```csharp
private SymbolResolver _resolver = new();

public void SetSymbolResolver(SymbolResolver resolver) => _resolver = resolver;
```

### Заменить Map/Unmap вызовы:
```csharp
// Было:
var brokerSymbol = GetMapper(terminalId).Map(symbol);
// Стало:
var brokerSymbol = _resolver.ToBroker(symbol, terminalId);

// Было:
card.Symbol = GetMapper(terminalId).Unmap(card.Symbol);
// Стало:
card.Symbol = _resolver.ToCanonical(card.Symbol, terminalId);
```

### CheckSymbolsAsync — кэшировать результат:
```csharp
public async Task<...> CheckSymbolsAsync(string terminalId, List<string> canonicalSymbols, ...)
{
    var worker = GetWorker(terminalId);

    // Map via resolver (handles aliases now)
    var toCheck = canonicalSymbols.Select(s => _resolver.ToBroker(s, terminalId)).ToList();

    var (resolved, missing) = await worker.CheckSymbolsAsync(toCheck, ct);

    // Cache resolved symbols for future ToBroker lookups
    _resolver.CacheResolvedSymbols(terminalId, resolved);

    // Unmap back to canonical
    var canonical = new Dictionary<string, string>();
    foreach (var kv in resolved)
        canonical[_resolver.ToCanonical(kv.Key, terminalId)] = kv.Value;

    var missingCanonical = missing.Select(s => _resolver.ToCanonical(s, terminalId)).ToList();
    return (canonical, missingCanonical);
}
```

### При подключении терминала — кэшировать все символы:
```csharp
// После успешного connect, вызвать symbols_get и кэшировать
var allSymbols = await worker.GetAllSymbolNamesAsync(ct);  // новый метод
if (allSymbols != null)
    _resolver.CacheTerminalSymbols(terminalId, allSymbols);
```

---

## Шаг 3: mt5_worker.py — получать алиасы от C#

### Вариант A (рекомендуемый): убрать SYMBOL_ALIASES, добавить команду SET_ALIASES

```python
# Global alias tables (populated from C# daemon)
_canonical_to_variants = {}   # "DE40" → ["DE40", "DAX40", "GER40", ...]
_variant_to_canonical = {}    # "DAX40" → "DE40"

def handle_set_aliases(msg):
    """Receive alias table from daemon at startup."""
    global _canonical_to_variants, _variant_to_canonical
    aliases = msg.get("aliases", {})
    _canonical_to_variants = aliases
    _variant_to_canonical = {}
    for canon, variants in aliases.items():
        for v in variants:
            _variant_to_canonical[v.upper()] = canon
    return _ok(msg, {"loaded": len(aliases)})
```

### CHECK_SYMBOLS — использовать полученные алиасы:
```python
for canonical in requested:
    cup = canonical.upper()

    # 1. Exact match
    if cup in available:
        found = available[cup]

    # 2. Input might be a variant — find canonical first
    elif cup in _variant_to_canonical:
        real_canon = _variant_to_canonical[cup]
        # Try canonical itself
        if real_canon.upper() in available:
            found = available[real_canon.upper()]
        else:
            # Try all variants of that canonical
            for v in _canonical_to_variants.get(real_canon, []):
                if v.upper() in available:
                    found = available[v.upper()]
                    break

    # 3. Input is canonical — try its variants
    elif canonical in _canonical_to_variants:
        for v in _canonical_to_variants[canonical]:
            if v.upper() in available:
                found = available[v.upper()]
                break

    # 4. Suffix fallback
    if not found:
        for sfx in common_suffixes:
            if (cup + sfx.upper()) in available:
                found = available[cup + sfx.upper()]
                break
```

### Добавить GET_ALL_SYMBOLS команду:
```python
def handle_get_all_symbols(msg):
    """Return all symbol names from terminal for caching."""
    all_syms = mt5.symbols_get()
    if not all_syms:
        return _error(msg, "symbols_get failed")
    names = [s.name for s in all_syms]
    return _ok(msg, {"symbols": names, "count": len(names)})
```

---

## Шаг 4: CostModelLoader — упростить

`CostModelLoader.Resolve()` уже работает правильно для бэктестера (принимает InstrumentCards после маппинга). Но нужно что `Resolve()` принимал символы в canonical виде:

```csharp
// В BacktestEngine при загрузке карт:
var cards = new Dictionary<string, InstrumentCard>();
foreach (var symbol in _btConfig.Symbols)
{
    // Получаем карту (connector уже маппит через resolver)
    var card = await _connector.GetSymbolInfoAsync(terminalId, symbol, ct);
    if (card != null) cards[symbol] = card;  // canonical key
}

// CostModelLoader.Resolve() получает canonical keys — работает нормально
```

---

## Шаг 5: Sizing — использовать resolver

В `HandleGetSizing` при проверке доступности:
```csharp
// Вместо CheckSymbolsAsync (тяжелый MT5 вызов), 
// можно использовать кэш resolver:
bool available = _symbolResolver.IsAvailable(s.Symbol, terminal);
```

Или оставить `CheckSymbolsAsync` но с resolver внутри — он уже знает маппинг.

---

## Порядок внедрения

1. ✅ SymbolResolver.cs — создан
2. Program.cs — инициализация
3. ConnectorManager — замена SymbolMapper на SymbolResolver
4. mt5_worker.py — SET_ALIASES + фикс CHECK_SYMBOLS + GET_ALL_SYMBOLS
5. DashboardServer — передать resolver в sizing и backtest
6. BacktestEngine — фильтр по sizing.enabled (bug 2)
7. Убрать SYMBOL_ALIASES из mt5_worker.py
8. Убрать старый SymbolMapper class (или deprecated)
9. Тесты
