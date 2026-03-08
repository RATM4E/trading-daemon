import json

data = json.load(open('history_cache.json'))
trades = data if isinstance(data, list) else list(data.values())

print(f"Всего: {len(trades)}")
print(f"Тип элемента: {type(trades[0])}")
print(f"Ключи: {list(trades[0].keys()) if isinstance(trades[0], dict) else 'не dict'}")
print(f"\nПервые 3 записи:")
for t in trades[:3]:
    print(json.dumps(t, indent=2, ensure_ascii=False)[:300])
    print("---")