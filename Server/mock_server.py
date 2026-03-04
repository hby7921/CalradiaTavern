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
DB_LOCK = threading.Lock()
LAST_NOW_MS = 0

PLAYERS_BY_ID = {}
PLAYER_NAME_INDEX = {}
CHAT_MESSAGES = []
DELIVERIES = []
TRADE_OFFERS = {}
MARKET_LISTINGS = {}
GIFT_REQUESTS = {}
BLOCKED_BY_BLOCKER = {}

MAX_PULL_LIMIT = 500
DEFAULT_ACTIVE_WITHIN_SEC = 120
MAX_MARKET_LISTINGS_PER_SELLER = 5
MARKET_PUBLICITY_MS = 5 * 60 * 1000
MARKET_LISTING_LIFETIME_MS = 2 * 24 * 60 * 60 * 1000
DENAR_DELIVERY_ITEM_ID = "__ctavern_denar__"
TARGET_BLOCKED_YOU_MESSAGE = "该玩家已经拉黑你，无法赠送。"
TARGET_NOT_ON_MAP_MESSAGE = "该玩家不在大地图，物品无法送达"


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
            info = {
                "PlayerId": row[0],
                "PlayerName": row[1],
                "ChannelId": row[2],
                "LastSeenUnixTimeMs": int(row[3] or 0),
                "ClientState": "other",
                "IsTavernActive": False,
            }
            PLAYERS_BY_ID[info["PlayerId"]] = info
            PLAYER_NAME_INDEX[norm_name(info["PlayerName"])] = info["PlayerId"]


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


def upsert_player(
    player_id: str,
    player_name: str,
    channel_id: str,
    client_state: str = None,
    is_tavern_active: bool = None,
):
    player_id = (player_id or "").strip()
    player_name = (player_name or "").strip()
    channel_id = (channel_id or "global").strip()
    if not player_id:
        return None

    previous = PLAYERS_BY_ID.get(player_id) or {}
    state_value = (client_state or previous.get("ClientState") or "other").strip().lower()
    if state_value not in ("map", "mission", "tavern", "other"):
        state_value = "other"
    tavern_active_value = (
        bool(is_tavern_active)
        if is_tavern_active is not None
        else bool(previous.get("IsTavernActive"))
    )

    info = {
        "PlayerId": player_id,
        "PlayerName": (player_name or "Anonymous")[:20],
        "ChannelId": channel_id,
        "LastSeenUnixTimeMs": now_ms(),
        "ClientState": state_value,
        "IsTavernActive": tavern_active_value,
    }
    PLAYERS_BY_ID[player_id] = info
    PLAYER_NAME_INDEX[norm_name(info["PlayerName"])] = player_id
    db_upsert_player(info)
    return info


def append_delivery(
    channel_id: str,
    target_player_id: str,
    from_player_id: str,
    from_player_name: str,
    item_id: str,
    count: int,
    note: str = "",
):
    delivery = {
        "DeliveryId": uuid.uuid4().hex,
        "ChannelId": channel_id,
        "PlayerId": target_player_id,
        "FromPlayerId": from_player_id,
        "FromPlayerName": (from_player_name or "Anonymous")[:20],
        "ItemId": item_id,
        "Count": int(count),
        "Note": note or "",
        "UnixTimeMs": now_ms(),
    }
    DELIVERIES.append(delivery)
    if len(DELIVERIES) > 6000:
        del DELIVERIES[0 : len(DELIVERIES) - 6000]
    return delivery


def is_player_in_map_or_tavern(player_info):
    if not player_info:
        return False
    if bool(player_info.get("IsTavernActive")):
        return True
    state = (player_info.get("ClientState") or "").strip().lower()
    return state in ("map", "tavern")


def add_block_relation(blocker_player_id: str, blocked_player_id: str):
    blocker_id = (blocker_player_id or "").strip()
    blocked_id = (blocked_player_id or "").strip()
    if not blocker_id or not blocked_id:
        return
    blocked_set = BLOCKED_BY_BLOCKER.setdefault(blocker_id, set())
    blocked_set.add(blocked_id)


def is_blocked_by_target(target_player_id: str, from_player_id: str):
    target_id = (target_player_id or "").strip()
    from_id = (from_player_id or "").strip()
    if not target_id or not from_id:
        return False
    blocked_set = BLOCKED_BY_BLOCKER.get(target_id)
    if not blocked_set:
        return False
    return from_id in blocked_set


def list_blocked_players(blocker_player_id: str):
    blocker_id = (blocker_player_id or "").strip()
    if not blocker_id:
        return []
    blocked_set = BLOCKED_BY_BLOCKER.get(blocker_id) or set()
    result = []
    for blocked_id in blocked_set:
        target = PLAYERS_BY_ID.get(blocked_id) or {}
        result.append(
            {
                "PlayerId": blocked_id,
                "PlayerName": (target.get("PlayerName") or blocked_id)[:20],
                "UnixTimeMs": now_ms(),
            }
        )
    result.sort(key=lambda x: (x.get("PlayerName") or "").lower())
    return result


def create_gift_request(
    channel_id: str,
    from_player_id: str,
    from_player_name: str,
    target_player_name: str,
    item_id: str,
    count: int,
):
    target_key = norm_name(target_player_name)
    target_player_id = PLAYER_NAME_INDEX.get(target_key)
    if not target_player_id:
        return None, f"target player not found: {target_player_name}"

    if target_player_id == from_player_id:
        return None, "cannot send to self"

    target_player = PLAYERS_BY_ID.get(target_player_id)
    if not target_player:
        return None, f"target player not found: {target_player_name}"

    if is_blocked_by_target(target_player_id, from_player_id):
        return None, TARGET_BLOCKED_YOU_MESSAGE

    if not is_player_in_map_or_tavern(target_player):
        return None, TARGET_NOT_ON_MAP_MESSAGE

    request = {
        "RequestId": uuid.uuid4().hex,
        "ChannelId": channel_id,
        "FromPlayerId": from_player_id,
        "FromPlayerName": (from_player_name or "Anonymous")[:20],
        "ToPlayerId": target_player_id,
        "ToPlayerName": target_player.get("PlayerName") or "Anonymous",
        "ItemId": item_id,
        "Count": int(count),
        "Status": "pending",
        "Reason": "",
        "UnixTimeMs": now_ms(),
    }
    GIFT_REQUESTS[request["RequestId"]] = request
    return request, ""


def pull_pending_gift_requests(player_id: str):
    pid = (player_id or "").strip()
    if not pid:
        return []
    result = [
        x
        for x in GIFT_REQUESTS.values()
        if x.get("ToPlayerId") == pid and (x.get("Status") or "pending") == "pending"
    ]
    result.sort(key=lambda x: int(x.get("UnixTimeMs") or 0))
    return result


def respond_gift_request(request_id: str, player_id: str, accepted: bool, reason: str):
    rid = (request_id or "").strip()
    pid = (player_id or "").strip()
    if not rid or not pid:
        return None, "requestId/playerId is required", 400

    request = GIFT_REQUESTS.get(rid)
    if not request:
        return None, "gift request not found", 404
    if request.get("ToPlayerId") != pid:
        return None, "forbidden gift response", 403
    if (request.get("Status") or "pending") != "pending":
        return None, "gift request already handled", 400

    now_value = now_ms()
    request["Status"] = "accepted" if accepted else "rejected"
    request["Reason"] = (reason or "").strip()
    request["UnixTimeMs"] = now_value

    delivery_id = ""
    if accepted:
        delivery = append_delivery(
            request["ChannelId"],
            request["ToPlayerId"],
            request["FromPlayerId"],
            request["FromPlayerName"],
            request["ItemId"],
            int(request["Count"]),
            f"gift_accept:{rid}",
        )
        delivery_id = delivery.get("DeliveryId") or ""
    else:
        reject_reason = (request.get("Reason") or "").strip() or "对方拒绝接收"
        delivery = append_delivery(
            request["ChannelId"],
            request["FromPlayerId"],
            request["ToPlayerId"],
            request["ToPlayerName"],
            request["ItemId"],
            int(request["Count"]),
            f"gift_rejected:{reject_reason}",
        )
        delivery_id = delivery.get("DeliveryId") or ""

    return (
        {
            "RequestId": rid,
            "Status": request["Status"],
            "DeliveryId": delivery_id,
            "ItemId": request.get("ItemId") or "",
            "Count": int(request.get("Count") or 0),
            "UnixTimeMs": now_value,
        },
        "",
        200,
    )


def to_market_listing_dto(listing):
    return {
        "ListingId": listing["ListingId"],
        "ChannelId": listing["ChannelId"],
        "SellerPlayerId": listing["SellerPlayerId"],
        "SellerName": listing["SellerName"],
        "ItemId": listing["ItemId"],
        "ItemName": listing["ItemName"],
        "ItemCount": int(listing["ItemCount"]),
        "PriceDenars": int(listing["PriceDenars"]),
        "Category": listing["Category"],
        "Status": listing["Status"],
        "CreatedUnixTimeMs": int(listing["CreatedUnixTimeMs"]),
        "PublicUnixTimeMs": int(listing["PublicUnixTimeMs"]),
        "UpdatedUnixTimeMs": int(listing["UpdatedUnixTimeMs"]),
        "BuyerPlayerId": listing.get("BuyerPlayerId") or "",
        "BuyerName": listing.get("BuyerName") or "",
    }


def count_open_market_listings_for_seller(seller_player_id: str):
    expire_open_market_listings()
    return sum(
        1
        for x in MARKET_LISTINGS.values()
        if x.get("SellerPlayerId") == seller_player_id and x.get("Status") == "open"
    )


def is_market_listing_expired(listing, now_unix_ms: int):
    if not listing:
        return False
    created_ms = int(listing.get("CreatedUnixTimeMs") or 0)
    if created_ms <= 0:
        return False
    return now_unix_ms >= (created_ms + MARKET_LISTING_LIFETIME_MS)


def expire_open_market_listings(now_unix_ms=None):
    now_value = now_unix_ms if now_unix_ms is not None else now_ms()
    for listing in MARKET_LISTINGS.values():
        if listing.get("Status") != "open":
            continue
        if is_market_listing_expired(listing, now_value):
            listing["Status"] = "expired"
            listing["UpdatedUnixTimeMs"] = now_value


class TavernHandler(BaseHTTPRequestHandler):
    server_version = "CalradiaTavernMock/0.3"

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

        if path == "/api/v1/trade/gift_requests":
            player_id = (query.get("playerId", [""])[0] or "").strip()
            if not player_id:
                self._send_json(envelope(False, error="playerId is required"), status=400)
                return
            with LOCK:
                data = pull_pending_gift_requests(player_id)
            self._send_json(envelope(True, data=data))
            return

        if path == "/api/v1/trade/list":
            channel_id = (query.get("channelId", ["global"])[0] or "global").strip()
            with LOCK:
                data = [x for x in TRADE_OFFERS.values() if x["ChannelId"] == channel_id]
            data.sort(key=lambda x: x["UnixTimeMs"], reverse=True)
            self._send_json(envelope(True, data=data))
            return

        if path == "/api/v1/market/list":
            channel_id = (query.get("channelId", ["global"])[0] or "global").strip()
            with LOCK:
                expire_open_market_listings()
                data = [
                    to_market_listing_dto(x)
                    for x in MARKET_LISTINGS.values()
                    if x.get("ChannelId") == channel_id and x.get("Status") == "open"
                ]
            data.sort(key=lambda x: int(x.get("CreatedUnixTimeMs") or 0), reverse=True)
            self._send_json(envelope(True, data=data))
            return

        if path == "/api/v1/player/list":
            channel_id = (query.get("channelId", ["global"])[0] or "global").strip()
            active_within_sec = int(
                (query.get("activeWithinSec", [str(DEFAULT_ACTIVE_WITHIN_SEC)])[0] or str(DEFAULT_ACTIVE_WITHIN_SEC)).strip()
                or str(DEFAULT_ACTIVE_WITHIN_SEC)
            )
            active_within_sec = max(5, min(1800, active_within_sec))
            limit = int((query.get("limit", ["80"])[0] or "80").strip() or "80")
            limit = max(1, min(300, limit))
            cutoff_ms = now_ms() - (active_within_sec * 1000)
            with LOCK:
                data = [
                    {
                        "PlayerId": x["PlayerId"],
                        "PlayerName": x["PlayerName"],
                        "ChannelId": x["ChannelId"],
                        "LastSeenUnixTimeMs": int(x.get("LastSeenUnixTimeMs") or 0),
                        "ClientState": x.get("ClientState") or "other",
                        "IsTavernActive": bool(x.get("IsTavernActive")),
                    }
                    for x in PLAYERS_BY_ID.values()
                    if x.get("ChannelId") == channel_id
                    and int(x.get("LastSeenUnixTimeMs") or 0) >= cutoff_ms
                    and bool(x.get("IsTavernActive"))
                ]
            data.sort(key=lambda x: int(x.get("LastSeenUnixTimeMs") or 0), reverse=True)
            self._send_json(envelope(True, data=data[:limit]))
            return

        if path == "/api/v1/player/blocked":
            blocker_player_id = (query.get("blockerPlayerId", [""])[0] or "").strip()
            if not blocker_player_id:
                self._send_json(envelope(False, error="blockerPlayerId is required"), status=400)
                return
            with LOCK:
                data = list_blocked_players(blocker_player_id)
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
            client_state = (payload.get("ClientState") or "other").strip().lower()
            is_tavern_active = bool(payload.get("IsTavernActive"))
            if not player_id:
                self._send_json(envelope(False, error="PlayerId is required"), status=400)
                return
            with LOCK:
                info = upsert_player(
                    player_id,
                    player_name,
                    channel_id,
                    client_state=client_state,
                    is_tavern_active=is_tavern_active,
                )
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

        if path == "/api/v1/player/block":
            channel_id = (payload.get("ChannelId") or "global").strip()
            blocker_player_id = (payload.get("BlockerPlayerId") or "").strip()
            blocker_player_name = (payload.get("BlockerPlayerName") or "Anonymous").strip()
            blocked_player_name = (payload.get("BlockedPlayerName") or "").strip()
            if not blocker_player_id or not blocked_player_name:
                self._send_json(envelope(False, error="invalid block params"), status=400)
                return

            blocked_key = norm_name(blocked_player_name)
            with LOCK:
                upsert_player(blocker_player_id, blocker_player_name, channel_id)
                blocked_player_id = PLAYER_NAME_INDEX.get(blocked_key)
                if not blocked_player_id:
                    self._send_json(
                        envelope(False, error=f"target player not found: {blocked_player_name}"),
                        status=404,
                    )
                    return
                if blocked_player_id == blocker_player_id:
                    self._send_json(envelope(False, error="cannot block self"), status=400)
                    return
                add_block_relation(blocker_player_id, blocked_player_id)
                blocked_info = PLAYERS_BY_ID.get(blocked_player_id) or {}

            self._send_json(
                envelope(
                    True,
                    data={
                        "BlockerPlayerId": blocker_player_id,
                        "BlockedPlayerId": blocked_player_id,
                        "BlockedPlayerName": blocked_info.get("PlayerName")
                        or blocked_player_name,
                        "UnixTimeMs": now_ms(),
                    },
                )
            )
            return

        if path == "/api/v1/chat/send":
            channel_id = (payload.get("ChannelId") or "global").strip()
            player_id = (payload.get("PlayerId") or "").strip()
            player_name = (payload.get("PlayerName") or "Anonymous").strip()
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

        if path == "/api/v1/trade/gift_request/create":
            channel_id = (payload.get("ChannelId") or "global").strip()
            from_player_id = (payload.get("FromPlayerId") or "").strip()
            from_player_name = (payload.get("FromPlayerName") or "Anonymous").strip()
            target_name = (payload.get("TargetPlayerName") or "").strip()
            item_id = (payload.get("ItemId") or "").strip()
            count = int(payload.get("Count") or 0)
            if not from_player_id or not target_name or not item_id or count <= 0:
                self._send_json(envelope(False, error="invalid gift request params"), status=400)
                return

            with LOCK:
                upsert_player(from_player_id, from_player_name, channel_id)
                request, error = create_gift_request(
                    channel_id,
                    from_player_id,
                    from_player_name,
                    target_name,
                    item_id,
                    count,
                )
                if not request:
                    status = 400
                    if error.startswith("target player not found:"):
                        status = 404
                    self._send_json(envelope(False, error=error), status=status)
                    return

            self._send_json(
                envelope(
                    True,
                    data={
                        "RequestId": request["RequestId"],
                        "TargetPlayerId": request["ToPlayerId"],
                        "TargetPlayerName": request["ToPlayerName"],
                        "UnixTimeMs": request["UnixTimeMs"],
                    },
                )
            )
            return

        if path == "/api/v1/trade/gift_request/respond":
            request_id = (payload.get("RequestId") or "").strip()
            player_id = (payload.get("PlayerId") or "").strip()
            accepted = bool(payload.get("Accepted"))
            reason = (payload.get("Reason") or "").strip()

            with LOCK:
                response_data, error, status = respond_gift_request(
                    request_id, player_id, accepted, reason
                )
                if response_data is None:
                    self._send_json(envelope(False, error=error), status=status)
                    return

            self._send_json(envelope(True, data=response_data))
            return

        if path == "/api/v1/trade/direct_send":
            channel_id = (payload.get("ChannelId") or "global").strip()
            from_player_id = (payload.get("FromPlayerId") or "").strip()
            from_player_name = (payload.get("FromPlayerName") or "Anonymous").strip()
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
                    self._send_json(envelope(False, error=f"target player not found: {target_name}"), status=404)
                    return
                if target_player_id == from_player_id:
                    self._send_json(envelope(False, error="cannot send to self"), status=400)
                    return

                target_player = PLAYERS_BY_ID.get(target_player_id)
                if is_blocked_by_target(target_player_id, from_player_id):
                    self._send_json(envelope(False, error=TARGET_BLOCKED_YOU_MESSAGE), status=403)
                    return
                if not is_player_in_map_or_tavern(target_player):
                    self._send_json(envelope(False, error=TARGET_NOT_ON_MAP_MESSAGE), status=400)
                    return
                delivery = append_delivery(
                    channel_id,
                    target_player_id,
                    from_player_id,
                    from_player_name,
                    item_id,
                    count,
                    "",
                )

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
            seller_name = (payload.get("SellerName") or "Anonymous").strip()
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
            buyer_name = (payload.get("BuyerName") or "Anonymous").strip()
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

                append_delivery(
                    offer["ChannelId"],
                    offer["SellerPlayerId"],
                    buyer_id,
                    buyer_name,
                    offer["WantItemId"],
                    int(offer["WantItemCount"]),
                    f"offer:{offer['OfferId']}",
                )

                grant_item = offer["GiveItemId"]
                grant_count = int(offer["GiveItemCount"])

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

        if path == "/api/v1/market/publish":
            channel_id = (payload.get("ChannelId") or "global").strip()
            seller_id = (payload.get("SellerPlayerId") or "").strip()
            seller_name = (payload.get("SellerName") or "Anonymous").strip()
            item_id = (payload.get("ItemId") or "").strip()
            item_name = (payload.get("ItemName") or "").strip()
            item_count = int(payload.get("ItemCount") or 0)
            price_denars = int(payload.get("PriceDenars") or 0)
            category = (payload.get("Category") or "").strip()
            if not seller_id or not item_id or item_count <= 0 or price_denars <= 0:
                self._send_json(envelope(False, error="invalid market publish params"), status=400)
                return

            with LOCK:
                expire_open_market_listings()
                upsert_player(seller_id, seller_name, channel_id)
                if count_open_market_listings_for_seller(seller_id) >= MAX_MARKET_LISTINGS_PER_SELLER:
                    self._send_json(
                        envelope(False, error=f"max {MAX_MARKET_LISTINGS_PER_SELLER} active listings per player"),
                        status=400,
                    )
                    return

                created_ms = now_ms()
                listing = {
                    "ListingId": uuid.uuid4().hex,
                    "ChannelId": channel_id,
                    "SellerPlayerId": seller_id,
                    "SellerName": seller_name[:20] if seller_name else "Anonymous",
                    "ItemId": item_id,
                    "ItemName": item_name or item_id,
                    "ItemCount": max(1, item_count),
                    "PriceDenars": max(1, price_denars),
                    "Category": category or "Unknown",
                    "Status": "open",
                    "CreatedUnixTimeMs": created_ms,
                    "PublicUnixTimeMs": created_ms + MARKET_PUBLICITY_MS,
                    "UpdatedUnixTimeMs": created_ms,
                    "BuyerPlayerId": "",
                    "BuyerName": "",
                }
                MARKET_LISTINGS[listing["ListingId"]] = listing

            self._send_json(
                envelope(
                    True,
                    data={
                        "ListingId": listing["ListingId"],
                        "CreatedUnixTimeMs": int(listing["CreatedUnixTimeMs"]),
                        "PublicUnixTimeMs": int(listing["PublicUnixTimeMs"]),
                    },
                )
            )
            return

        if path == "/api/v1/market/cancel":
            listing_id = (payload.get("ListingId") or "").strip()
            seller_id = (payload.get("SellerPlayerId") or "").strip()
            if not listing_id or not seller_id:
                self._send_json(envelope(False, error="listingId/sellerPlayerId is required"), status=400)
                return

            with LOCK:
                expire_open_market_listings()
                listing = MARKET_LISTINGS.get(listing_id)
                if not listing:
                    self._send_json(envelope(False, error="listing not found"), status=404)
                    return
                if listing.get("Status") != "open":
                    if listing.get("Status") == "expired":
                        self._send_json(envelope(False, error="listing is expired"), status=400)
                        return
                    self._send_json(envelope(False, error="listing is not open"), status=400)
                    return
                if listing.get("SellerPlayerId") != seller_id:
                    self._send_json(envelope(False, error="only seller can cancel listing"), status=403)
                    return

                listing["Status"] = "cancelled"
                listing["UpdatedUnixTimeMs"] = now_ms()
                response_data = {
                    "ListingId": listing_id,
                    "Status": listing["Status"],
                    "ReturnItemId": listing["ItemId"],
                    "ReturnItemCount": int(listing["ItemCount"]),
                    "UnixTimeMs": int(listing["UpdatedUnixTimeMs"]),
                }

            self._send_json(envelope(True, data=response_data))
            return

        if path == "/api/v1/market/buy":
            listing_id = (payload.get("ListingId") or "").strip()
            buyer_id = (payload.get("BuyerPlayerId") or "").strip()
            buyer_name = (payload.get("BuyerName") or "Anonymous").strip()
            channel_id = (payload.get("ChannelId") or "global").strip()
            if not listing_id or not buyer_id:
                self._send_json(envelope(False, error="listingId/buyerPlayerId is required"), status=400)
                return

            with LOCK:
                expire_open_market_listings()
                listing = MARKET_LISTINGS.get(listing_id)
                if not listing:
                    self._send_json(envelope(False, error="listing not found"), status=404)
                    return
                if listing.get("Status") != "open":
                    if listing.get("Status") == "expired":
                        self._send_json(envelope(False, error="listing is expired"), status=400)
                        return
                    self._send_json(envelope(False, error="listing is not open"), status=400)
                    return
                if listing.get("SellerPlayerId") == buyer_id:
                    self._send_json(envelope(False, error="cannot buy own listing"), status=400)
                    return
                if listing.get("ChannelId") != channel_id:
                    self._send_json(envelope(False, error="channel mismatch"), status=400)
                    return
                if now_ms() < int(listing.get("PublicUnixTimeMs") or 0):
                    self._send_json(envelope(False, error="listing is in publicity period"), status=400)
                    return

                upsert_player(buyer_id, buyer_name, channel_id)

                listing["Status"] = "sold"
                listing["BuyerPlayerId"] = buyer_id
                listing["BuyerName"] = buyer_name[:20] if buyer_name else "Anonymous"
                listing["UpdatedUnixTimeMs"] = now_ms()

                append_delivery(
                    channel_id,
                    buyer_id,
                    listing["SellerPlayerId"],
                    listing["SellerName"],
                    listing["ItemId"],
                    int(listing["ItemCount"]),
                    f"market_buy:{listing_id}",
                )
                append_delivery(
                    channel_id,
                    listing["SellerPlayerId"],
                    buyer_id,
                    buyer_name,
                    DENAR_DELIVERY_ITEM_ID,
                    int(listing["PriceDenars"]),
                    f"market_sell:{listing_id}",
                )

                response_data = {
                    "ListingId": listing_id,
                    "ItemId": listing["ItemId"],
                    "ItemName": listing.get("ItemName") or listing["ItemId"],
                    "ItemCount": int(listing["ItemCount"]),
                    "PriceDenars": int(listing["PriceDenars"]),
                    "SellerPlayerId": listing["SellerPlayerId"],
                    "SellerName": listing["SellerName"],
                    "UnixTimeMs": int(listing["UpdatedUnixTimeMs"]),
                }

            self._send_json(envelope(True, data=response_data))
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
