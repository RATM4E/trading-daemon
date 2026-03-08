"""
Скачивает последние N сообщений из канала и выводит сырой текст.
Использование:
    python dump_raw.py --channel cryptoninjas_trading_ann --limit 50
"""
import asyncio
import json
import os
import argparse
from telethon import TelegramClient
from dotenv import load_dotenv
load_dotenv()

async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--channel",  required=True)
    ap.add_argument("--session",  default="scan_session")
    ap.add_argument("--limit",    type=int, default=50)
    args = ap.parse_args()

    client = TelegramClient(args.session, int(os.environ["TG_API_ID"]), os.environ["TG_API_HASH"])
    await client.start()

    msgs = []
    async for msg in client.iter_messages(args.channel, limit=args.limit):
        if msg.raw_text:
            msgs.append({"id": msg.id, "date": str(msg.date), "text": msg.raw_text})

    await client.disconnect()

    # Выводим от старых к новым
    for m in reversed(msgs):
        print(f"\n{'='*60}")
        print(f"ID={m['id']}  {m['date']}")
        print(m['text'])

asyncio.run(main())
