import json

data = json.loads(open("history_cache.json", encoding="utf-8").read())

print("=== SL сообщения ===")
for m in data:
    t = m["text"]
    if "SL" in t and "#" in t and "ENTRY" not in t and "STATUS" not in t:
        print(repr(t[:120]))
        print()

print("=== TP 3 сообщения ===")
for m in data:
    t = m["text"]
    if "TP" in t and "3" in t and "#" in t and "ENTRY" not in t and "STATUS" not in t and "TP3" not in t:
        print(repr(t[:120]))
        print()