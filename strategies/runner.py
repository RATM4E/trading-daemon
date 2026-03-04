#!/usr/bin/env python3
"""
Universal Strategy Runner
=========================
Loads any strategy module, connects to daemon via TCP, handles the protocol:
  HELLO → ACK → (TICK → ACTIONS)* → STOP → GOODBYE

Usage:
  python runner.py --port 5600 --strategy bb_mr_v2 --strategy-dir ../strategies --config ../strategies/bb_mr_v2/config.json

The runner:
  1. Imports strategy.py from the strategy folder
  2. Creates Strategy instance with config
  3. Connects to daemon TCP listener
  4. Sends HELLO with requirements
  5. Receives ACK (may include saved state)
  6. Loop: receive TICK → call strategy.on_bars() → send ACTIONS
  7. On STOP: call strategy.save_state() → send GOODBYE → exit

Protocol note:
  The DAEMON initiates heartbeats. Runner is passive — it only speaks
  when spoken to (ACTIONS reply to TICK, GOODBYE reply to STOP,
  HEARTBEAT_ACK reply to HEARTBEAT).  This keeps the protocol strictly
  synchronous and prevents TCP buffer desync.
"""

import argparse
import importlib.util
import json
import os
import socket
import sys
import threading
import time
import traceback
from datetime import datetime


def load_strategy_class(strategy_name: str, strategy_dir: str):
    """Dynamically load strategy.py from the strategy folder."""
    strategy_path = os.path.join(strategy_dir, strategy_name, "strategy.py")
    if not os.path.exists(strategy_path):
        raise FileNotFoundError(f"Strategy file not found: {strategy_path}")

    spec = importlib.util.spec_from_file_location(f"strategy_{strategy_name}", strategy_path)
    module = importlib.util.module_from_spec(spec)

    # Add strategy dir to path so strategy can import local modules
    strat_folder = os.path.dirname(strategy_path)
    if strat_folder not in sys.path:
        sys.path.insert(0, strat_folder)

    spec.loader.exec_module(module)

    # Find the Strategy class
    if hasattr(module, "Strategy"):
        return module.Strategy
    # Try to find any class that has on_bars method
    for name in dir(module):
        obj = getattr(module, name)
        if isinstance(obj, type) and hasattr(obj, "on_bars"):
            return obj

    raise AttributeError(f"No Strategy class found in {strategy_path}")


def send_msg(sock: socket.socket, msg: dict):
    """Send a JSON message terminated by newline."""
    data = json.dumps(msg, default=str) + "\n"
    sock.sendall(data.encode("utf-8"))


class MessageReader:
    """
    Persistent-buffer TCP message reader.

    Keeps leftover bytes between calls so that if two JSON messages
    arrive in a single recv() chunk, both are returned on successive
    read() calls without data loss.
    """

    def __init__(self, sock: socket.socket):
        self._sock = sock
        self._buf = b""

    def read(self, timeout: float = 300.0) -> dict | None:
        """
        Read one newline-delimited JSON message.
        Returns parsed dict, or None on timeout / connection close.
        """
        self._sock.settimeout(timeout)
        try:
            while True:
                # Check if we already have a complete message in the buffer
                if b"\n" in self._buf:
                    line, self._buf = self._buf.split(b"\n", 1)
                    return json.loads(line.decode("utf-8-sig"))

                # Need more data from the socket
                chunk = self._sock.recv(4096)
                if not chunk:
                    return None  # connection closed
                self._buf += chunk

        except socket.timeout:
            return None
        except json.JSONDecodeError as e:
            print(f"[runner] JSON decode error: {e}", file=sys.stderr)
            return None


def log(msg: str):
    """Print timestamped log message."""
    ts = datetime.now().strftime("%H:%M:%S.%f")[:-3]
    print(f"[runner {ts}] {msg}", flush=True)


def main():
    parser = argparse.ArgumentParser(description="Universal Strategy Runner")
    parser.add_argument("--port", type=int, required=True, help="Daemon TCP port to connect to")
    parser.add_argument("--strategy", required=True, help="Strategy folder name")
    parser.add_argument("--strategy-dir", required=True, help="Root strategies directory")
    parser.add_argument("--config", default=None, help="Path to strategy config.json")
    args = parser.parse_args()

    log(f"Starting: strategy={args.strategy}, port={args.port}")

    # 1. Load strategy config
    config = {}
    config_path = args.config
    if config_path is None:
        config_path = os.path.join(args.strategy_dir, args.strategy, "config.json")

    if config_path and os.path.exists(config_path):
        with open(config_path, "r") as f:
            config = json.load(f)
        log(f"Loaded config from {config_path}")
    else:
        log(f"No config file found, using empty config")

    # 1b. Validate "strategy" field (self-documentation convention)
    config_strategy = config.get("strategy")
    if config_strategy is None:
        log(f'WARNING: config.json missing \'strategy\' field — add '
            f'"strategy": "{args.strategy}" for self-documentation')
    elif config_strategy != args.strategy:
        log(f"FATAL: config.json strategy='{config_strategy}' does not match "
            f"folder name '{args.strategy}' — wrong config file?")
        sys.exit(1)
    else:
        log(f"Config strategy field validated: {config_strategy}")

    # 2. Load strategy class and create instance
    try:
        StrategyClass = load_strategy_class(args.strategy, args.strategy_dir)
        strategy = StrategyClass(config)
        log(f"Strategy class loaded: {StrategyClass.__name__}")
    except Exception as e:
        log(f"FATAL: Failed to load strategy: {e}")
        traceback.print_exc()
        sys.exit(1)

    # 3. Get requirements
    requirements = strategy.get_requirements()
    log(f"Requirements: {len(requirements.get('symbols', []))} symbols, "
        f"history_bars={requirements.get('history_bars', 300)}")

    # 4. Connect to daemon
    max_retries = 10
    sock = None
    for attempt in range(max_retries):
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            sock.connect(("127.0.0.1", args.port))
            log(f"Connected to daemon on port {args.port}")
            break
        except ConnectionRefusedError:
            log(f"Connection attempt {attempt + 1}/{max_retries} failed, retrying in 1s...")
            time.sleep(1)
            sock = None

    if sock is None:
        log("FATAL: Could not connect to daemon")
        sys.exit(1)

    # Create persistent-buffer reader
    reader = MessageReader(sock)

    tick_count = 0
    try:
        # 5. Send HELLO
        hello = {
            "type": "HELLO",
            "strategy": args.strategy,
            "version": "1.0",
            "requirements": requirements
        }
        send_msg(sock, hello)
        log("HELLO sent")

        # 6. Receive ACK
        ack = reader.read(timeout=15.0)
        if ack is None or ack.get("type") != "ACK":
            log(f"FATAL: Expected ACK, got: {ack}")
            sys.exit(1)

        terminal_id = ack.get("terminal_id", "?")
        magic = ack.get("magic", 0)
        mode = ack.get("mode", "auto")
        log(f"ACK received: terminal={terminal_id}, magic={magic}, mode={mode}")

        # Restore saved state if provided
        saved_state = ack.get("saved_state")
        if saved_state is not None:
            try:
                strategy.restore_state(saved_state)
                log("State restored from saved state")
            except Exception as e:
                log(f"Warning: Failed to restore state: {e}")

        # 7. Main loop
        # Protocol: daemon sends messages, runner responds.
        # Daemon sends:  TICK → runner replies ACTIONS
        #                HEARTBEAT → runner replies HEARTBEAT_ACK
        #                STOP → runner replies GOODBYE → exit
        #
        # Runner NEVER sends unsolicited messages.
        # Timeout = 300s (5 min) — covers H4 candle gaps.
        # If 300s passes with zero communication, assume daemon is dead.

        tick_count = 0
        consecutive_timeouts = 0
        MAX_TIMEOUTS = 3  # 3 × 300s = 15 min with no comm → exit

        # Delta bar buffer: used in backtest mode when daemon sends is_delta=true.
        # Keys: symbol str → list of bar dicts (rolling window, maxlen=history_bars).
        # In live mode is_delta is never set, so this stays empty and has no overhead.
        _history_bars = requirements.get("history_bars", 300)
        _bar_buffer: dict = {}

        while True:
            msg = reader.read(timeout=300.0)

            if msg is None:
                consecutive_timeouts += 1
                log(f"Timeout #{consecutive_timeouts} (no message in 300s)")
                if consecutive_timeouts >= MAX_TIMEOUTS:
                    log("FATAL: daemon appears dead (no communication for 15 min)")
                    break
                continue

            consecutive_timeouts = 0  # reset on any valid message
            msg_type = msg.get("type", "")

            if msg_type == "TICK":
                tick_count += 1
                tick_id = msg.get("tick_id", 0)
                bars_data = msg.get("bars", {})
                positions = msg.get("positions", [])
                equity = msg.get("equity", 0)
                is_delta = msg.get("is_delta", False)

                if is_delta:
                    # Backtest delta mode: merge new bars into rolling buffer,
                    # pass full window to on_bars() — same view as live trading.
                    for sym, new_bars in bars_data.items():
                        if sym not in _bar_buffer:
                            _bar_buffer[sym] = []
                        buf = _bar_buffer[sym]
                        buf.extend(new_bars)
                        # Keep only the last history_bars entries
                        if len(buf) > _history_bars:
                            del buf[: len(buf) - _history_bars]
                    effective_bars = _bar_buffer
                else:
                    # Full snapshot (live trading or warmup tick).
                    # Also seed the buffer so it's ready if delta ticks follow.
                    for sym, bars in bars_data.items():
                        _bar_buffer[sym] = list(bars[-_history_bars:])
                    effective_bars = bars_data

                try:
                    actions = strategy.on_bars(effective_bars, positions)
                except Exception as e:
                    log(f"ERROR in on_bars: {e}")
                    traceback.print_exc()
                    actions = []

                if actions is None:
                    actions = []

                response = {
                    "type": "ACTIONS",
                    "actions": actions
                }
                send_msg(sock, response)

                if actions:
                    log(f"Tick #{tick_count} (id={tick_id}): {len(actions)} action(s)")

            elif msg_type == "HEARTBEAT":
                # Daemon-initiated heartbeat — reply immediately
                send_msg(sock, {
                    "type": "HEARTBEAT_ACK",
                    "ts": int(time.time())
                })

            elif msg_type == "STOP":
                reason = msg.get("reason", "unknown")
                log(f"STOP received (reason: {reason})")

                state = {}
                try:
                    state = strategy.save_state()
                except Exception as e:
                    log(f"Warning: save_state failed: {e}")

                goodbye = {
                    "type": "GOODBYE",
                    "state": state,
                    "reason": "normal"
                }
                send_msg(sock, goodbye)
                log(f"GOODBYE sent (state={len(json.dumps(state))} bytes)")
                break

            elif msg_type == "HEARTBEAT_ACK":
                # Legacy: in case daemon sends ACK to a leftover heartbeat
                pass

            elif msg_type == "ERROR":
                log(f"ERROR from daemon: {msg.get('message', '?')}")

            else:
                log(f"Unknown message type: {msg_type}")

    except KeyboardInterrupt:
        log("Interrupted by user")
    except Exception as e:
        log(f"FATAL: {e}")
        traceback.print_exc()
    finally:
        try:
            sock.close()
        except:
            pass
        log(f"Runner exiting (processed {tick_count} ticks)")


if __name__ == "__main__":
    main()
