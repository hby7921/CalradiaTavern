#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import os
import sqlite3
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

LOCK = threading.Lock()
TIME_LOCK = threading.Lock()
LAST_NOW_MS = 0
DB_LOCK = threading.Lock()

PLAYERS_BY_ID = {}
PLAYER_NAME_INDEX = {}

CHAT_MESSAGES = []
DELIVERIES = []

# Legacy offer-based trades are still supported.
TRADE_OFFERS = {}
MAX_PULL_LIMIT = 500


def resolve_db_path():
    configured = (os.environ.get("CTAVERN_DB_PATH") or "").strip()
    if configured:
        return configured
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "calradia_tavern.db")


DB_PATH = resolve_db_path()


def db_connect():
    return sqlite3.connect(DB_PATH, timeout=10.0)


def init_db():
    with DB_LOCK:
        with db_connect() as conn:
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS players (
                    player_id TEXT PRIMARY KEY,
                    player_name TEXT NOT NULL,
                    channel_id TEXT NOT NULL,
                    last_seen_unix_ms INTEGER NOT NULL
                )
                """
            )
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS chat_messages (
                    message_id TEXT PRIMARY KEY,
                    channel_id TEXT NOT NULL,
                    player_id TEXT NOT NULL,
                    player_name TEXT NOT NULL,
                    text TEXT NOT NULL,
                    unix_time_ms INTEGER NOT NULL
                )
                """
            )
            conn.execute(
                "CREATE INDEX IF NOT EXISTS idx_chat_channel_time ON chat_messages(channel_id, unix_time_ms)"
            )
            conn.commit()


def load_players_from_db():
    with DB_LOCK:
        with db_connect() as conn:
            rows = conn.execute(
                """
                SELECT player_id, player_name, channel_id, last_seen_unix_ms
                FROM players
                """
            ).fetchall()

    with LOCK:
        PLAYERS_BY_ID.clear()
        PLAYER_NAME_INDEX.clear()
        for row in rows:
            player_id = row[0]
            player_name = row[1]
            channel_id = row[2]
            last_seen = int(row[3] or 0)
            info = {
                "PlayerId": player_id,
                "PlayerName": player_name,
                "ChannelId": channel_id,
                "LastSeenUnixTimeMs": last_seen,
            }
            PLAYERS_BY_ID[player_id] = info
            PLAYER_NAME_INDEX[norm_name(player_name)] = player_id


def db_upsert_player(info):
    with DB_LOCK:
        with db_connect() as conn:
            conn.execute(
                """
                INSERT INTO players(player_id, player_name, channel_id, last_seen_unix_ms)
                VALUES(?, ?, ?, ?)
                ON CONFLICT(player_id) DO UPDATE SET
                    player_name=excluded.player_name,
                    channel_id=excluded.channel_id,
                    last_seen_unix_ms=excluded.last_seen_unix_ms
                """,
                (
                    info["PlayerId"],
                    info["PlayerName"],
                    info["ChannelId"],
                    int(info["LastSeenUnixTimeMs"]),
                ),
            )
            conn.commit()


def db_insert_chat(msg):
    with DB_LOCK:
        with db_connect() as conn:
            conn.execute(
                """
                INSERT OR REPLACE INTO chat_messages
                (message_id, channel_id, player_id, player_name, text, unix_time_ms)
                VALUES (?, ?, ?, ?, ?, ?)
                """,
                (
                    msg["MessageId"],
                    msg["ChannelId"],
                    msg["PlayerId"],
                    msg["PlayerName"],
                    msg["Text"],
                    int(msg["UnixTimeMs"]),
                ),
            )
            conn.commit()


def db_pull_chat(channel_id, after_ms, limit):
    safe_limit = max(1, min(MAX_PULL_LIMIT, int(limit)))
    with DB_LOCK:
        with db_connect() as conn:
            rows = conn.execute(
                """
                SELECT message_id, channel_id, player_id, player_name, text, unix_time_ms
                FROM chat_messages
                WHERE channel_id = ? AND unix_time_ms > ?
                ORDER BY unix_time_ms ASC
                LIMIT ?
                """,
                (channel_id, int(after_ms), safe_limit),
            ).fetchall()

    return [
        {
            "MessageId": row[0],
            "ChannelId": row[1],
            "PlayerId": row[2],
            "PlayerName": row[3],
            "Text": row[4],
            "UnixTimeMs": int(row[5]),
        }
        for row in rows
    ]


def now_ms():
    global LAST_NOW_MS
    raw = int(time.time() * 1000)
    with TIME_LOCK:
        if raw <= LAST_NOW_MS:
            raw = LAST_NOW_MS + 1
        LAST_NOW_MS = raw
        return raw


def envelope(ok, data=None, error=""):
    return {"Ok": bool(ok), "Data": data, "Error": error}


def norm_name(name: str) -> str:
    return (name or "").strip().lower()


def upsert_player(player_id: str, player_name: str, channel_id: str):
    player_id = (player_id or "").strip()
    player_name = (player_name or "").strip()
    channel_id = (channel_id or "global").strip()
    if not player_id:
        return None
    if not player_name:
        player_name = "匿名旅人"
    info = {
        "PlayerId": player_id,
        "PlayerName": player_name[:20],
        "ChannelId": channel_id,
        "LastSeenUnixTimeMs": now_ms(),
    }
    PLAYERS_BY_ID[player_id] = info
    PLAYER_NAME_INDEX[norm_name(info["PlayerName"])] = player_id
    db_upsert_player(info)
    return info


class TavernHandler(BaseHTTPRequestHandler):
    server_version = "CalradiaTavernMock/0.2"

    def _read_json(self):
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length > 0 else b"{}"
        try:
            return json.loads(raw.decode("utf-8"))
        except Exception:
            return {}

    def _send_json(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        return

    def do_GET(self):
        parsed = urlparse(self.path)
        path = parsed.path
        query = parse_qs(parsed.query)

        if path == "/api/v1/chat/pull":
            channel_id = (query.get("channelId", ["global"])[0] or "global").strip()
            after_ms = int((query.get("afterUnixMs", ["0"])[0] or "0").strip() or "0")
            limit = int((query.get("limit", [str(MAX_PULL_LIMIT)])[0] or str(MAX_PULL_LIMIT)).strip() or str(MAX_PULL_LIMIT))
            data = db_pull_chat(channel_id, after_ms, limit)
            self._send_json(envelope(True, data=data))
            return

        if path == "/api/v1/trade/deliveries":
            player_id = (query.get("playerId", [""])[0] or "").strip()
            after_ms = int((query.get("afterUnixMs", ["0"])[0] or "0").strip() or "0")
            if not player_id:
                self._send_json(envelope(False, error="playerId is required"), status=400)
                return
            with LOCK:
                data = [
                    x
                    for x in DELIVERIES
                    if x["PlayerId"] == player_id and int(x["UnixTimeMs"]) > after_ms
                ]
            self._send_json(envelope(True, data=data))
            return

        if path == "/api/v1/trade/list":
            channel_id = (query.get("channelId", ["global"])[0] or "global").strip()
            with LOCK:
                data = [x for x in TRADE_OFFERS.values() if x["ChannelId"] == channel_id]
            data.sort(key=lambda x: x["UnixTimeMs"], reverse=True)
            self._send_json(envelope(True, data=data))
            return

        self._send_json(envelope(False, error="Not found"), status=404)

    def do_POST(self):
        parsed = urlparse(self.path)
        path = parsed.path
        payload = self._read_json()

        if path == "/api/v1/player/upsert":
            player_id = (payload.get("PlayerId") or "").strip()
            player_name = (payload.get("PlayerName") or "").strip()
            channel_id = (payload.get("ChannelId") or "global").strip()
            if not player_id:
                self._send_json(envelope(False, error="PlayerId is required"), status=400)
                return
            with LOCK:
                info = upsert_player(player_id, player_name, channel_id)
            self._send_json(
                envelope(
                    True,
                    data={
                        "PlayerId": info["PlayerId"],
                        "PlayerName": info["PlayerName"],
                        "UnixTimeMs": now_ms(),
                    },
                )
            )
            return

        if path == "/api/v1/chat/send":
            channel_id = (payload.get("ChannelId") or "global").strip()
            player_id = (payload.get("PlayerId") or "").strip()
            player_name = (payload.get("PlayerName") or "匿名").strip()
            text = (payload.get("Text") or "").strip()
            if not player_id or not text:
                self._send_json(envelope(False, error="playerId/text is required"), status=400)
                return
            text = text[:180]
            msg = {
                "MessageId": uuid.uuid4().hex,
                "ChannelId": channel_id,
                "PlayerId": player_id,
                "PlayerName": player_name,
                "Text": text,
                "UnixTimeMs": now_ms(),
            }
            with LOCK:
                upsert_player(player_id, player_name, channel_id)
                CHAT_MESSAGES.append(msg)
                if len(CHAT_MESSAGES) > 3000:
                    del CHAT_MESSAGES[0 : len(CHAT_MESSAGES) - 3000]
            db_insert_chat(msg)
            self._send_json(
                envelope(True, data={"MessageId": msg["MessageId"], "UnixTimeMs": msg["UnixTimeMs"]})
            )
            return

        if path == "/api/v1/trade/direct_send":
            channel_id = (payload.get("ChannelId") or "global").strip()
            from_player_id = (payload.get("FromPlayerId") or "").strip()
            from_player_name = (payload.get("FromPlayerName") or "匿名").strip()
            target_name = (payload.get("TargetPlayerName") or "").strip()
            item_id = (payload.get("ItemId") or "").strip()
            count = int(payload.get("Count") or 0)

            if not from_player_id or not target_name or not item_id or count <= 0:
                self._send_json(envelope(False, error="invalid direct_send params"), status=400)
                return

            target_key = norm_name(target_name)
            with LOCK:
                upsert_player(from_player_id, from_player_name, channel_id)
                target_player_id = PLAYER_NAME_INDEX.get(target_key)
                if not target_player_id:
                    self._send_json(envelope(False, error=f"目标玩家不存在: {target_name}"), status=404)
                    return
                if target_player_id == from_player_id:
                    self._send_json(envelope(False, error="cannot send to self"), status=400)
                    return

                target_player = PLAYERS_BY_ID.get(target_player_id)
                delivery = {
                    "DeliveryId": uuid.uuid4().hex,
                    "ChannelId": channel_id,
                    "PlayerId": target_player_id,
                    "FromPlayerId": from_player_id,
                    "FromPlayerName": from_player_name,
                    "ItemId": item_id,
                    "Count": count,
                    "Note": "",
                    "UnixTimeMs": now_ms(),
                }
                DELIVERIES.append(delivery)
                if len(DELIVERIES) > 6000:
                    del DELIVERIES[0 : len(DELIVERIES) - 6000]

            self._send_json(
                envelope(
                    True,
                    data={
                        "DeliveryId": delivery["DeliveryId"],
                        "TargetPlayerId": target_player_id,
                        "TargetPlayerName": target_player["PlayerName"],
                        "UnixTimeMs": now_ms(),
                    },
                )
            )
            return

        if path == "/api/v1/trade/publish":
            channel_id = (payload.get("ChannelId") or "global").strip()
            seller_id = (payload.get("SellerPlayerId") or "").strip()
            seller_name = (payload.get("SellerName") or "匿名").strip()
            give_item = (payload.get("GiveItemId") or "").strip()
            want_item = (payload.get("WantItemId") or "").strip()
            give_count = int(payload.get("GiveItemCount") or 0)
            want_count = int(payload.get("WantItemCount") or 0)
            if not seller_id or not give_item or not want_item or give_count <= 0 or want_count <= 0:
                self._send_json(envelope(False, error="invalid publish params"), status=400)
                return
            offer = {
                "OfferId": uuid.uuid4().hex,
                "ChannelId": channel_id,
                "SellerPlayerId": seller_id,
                "SellerName": seller_name,
                "GiveItemId": give_item,
                "GiveItemCount": give_count,
                "WantItemId": want_item,
                "WantItemCount": want_count,
                "Status": "open",
                "UnixTimeMs": now_ms(),
            }
            with LOCK:
                TRADE_OFFERS[offer["OfferId"]] = offer
            self._send_json(
                envelope(True, data={"OfferId": offer["OfferId"], "UnixTimeMs": offer["UnixTimeMs"]})
            )
            return

        if path == "/api/v1/trade/accept":
            offer_id = (payload.get("OfferId") or "").strip()
            buyer_id = (payload.get("BuyerPlayerId") or "").strip()
            buyer_name = (payload.get("BuyerName") or "匿名").strip()
            if not offer_id or not buyer_id:
                self._send_json(envelope(False, error="offerId/buyerPlayerId is required"), status=400)
                return
            with LOCK:
                offer = TRADE_OFFERS.get(offer_id)
                if not offer:
                    self._send_json(envelope(False, error="offer not found"), status=404)
                    return
                if offer["Status"] != "open":
                    self._send_json(envelope(False, error="offer is not open"), status=400)
                    return
                if offer["SellerPlayerId"] == buyer_id:
                    self._send_json(envelope(False, error="cannot accept own offer"), status=400)
                    return

                offer["Status"] = "filled"
                offer["BuyerPlayerId"] = buyer_id
                offer["BuyerName"] = buyer_name
                offer["FilledUnixTimeMs"] = now_ms()

                DELIVERIES.append(
                    {
                        "DeliveryId": uuid.uuid4().hex,
                        "ChannelId": offer["ChannelId"],
                        "PlayerId": offer["SellerPlayerId"],
                        "FromPlayerId": buyer_id,
                        "FromPlayerName": buyer_name,
                        "ItemId": offer["WantItemId"],
                        "Count": offer["WantItemCount"],
                        "Note": f"offer:{offer['OfferId']}",
                        "UnixTimeMs": now_ms(),
                    }
                )

                grant_item = offer["GiveItemId"]
                grant_count = offer["GiveItemCount"]

            self._send_json(
                envelope(
                    True,
                    data={
                        "OfferId": offer_id,
                        "GrantedItemId": grant_item,
                        "GrantedItemCount": grant_count,
                        "UnixTimeMs": now_ms(),
                    },
                )
            )
            return

        self._send_json(envelope(False, error="Not found"), status=404)


def main():
    host = "0.0.0.0"
    port = 18080
    init_db()
    load_players_from_db()
    server = ThreadingHTTPServer((host, port), TavernHandler)
    print(f"Calradia Tavern mock server started at http://{host}:{port} db={DB_PATH}")
    server.serve_forever()


if __name__ == "__main__":
    main()
