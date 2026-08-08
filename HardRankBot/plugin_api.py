"""
HTTP listener for the HardlineLeaderboard BepInEx plugin.

Implements the same `POST /api/match` contract the original HardRank backend
used (see NOTICE), so the existing plugin DLL can be pointed at this bot's
address without any changes: same JSON body shape, same `X-Api-Key` header.
"""

import json
import logging
import time
from datetime import datetime, timezone

from aiohttp import web

from elo import calculate_elo_changes, STARTING_ELO
from db import get_db, verify_host_key, is_host_suspended, suspend_host_key

MAP_STARTING_ELO = 700

log = logging.getLogger("hardrank.plugin_api")

# ---------------------------------------------------------------------------
# Burst-abuse detection: a real match takes several minutes to play out, so
# the same host submitting many matches in a short window is a strong signal
# of fabricated/spammed reports rather than a coincidence. On detection, the
# triggering submission is rejected (never recorded) and the key is
# suspended -- an admin decides from there via /recentreports and /banhost
# whether to also undo the earlier matches in the burst.
#
# In-memory only (resets on restart) -- acceptable since a resumed abuse
# pattern re-triggers within a couple of submissions either way.
# ---------------------------------------------------------------------------
ABUSE_WINDOW_SECONDS = 120
ABUSE_MAX_IN_WINDOW = 3  # a 4th submission within the window triggers suspension

_recent_submissions: dict[str, list[float]] = {}


def _is_submission_burst(discord_id: str) -> bool:
    now = time.time()
    timestamps = _recent_submissions.setdefault(discord_id, [])
    timestamps.append(now)
    cutoff = now - ABUSE_WINDOW_SECONDS
    while timestamps and timestamps[0] < cutoff:
        timestamps.pop(0)
    return len(timestamps) > ABUSE_MAX_IN_WINDOW


def _apply_result(db, name: str, delta: int, won: bool, disconnected: bool,
                   kills: int, deaths: int, assists: int) -> None:
    if disconnected:
        db.execute(
            """UPDATE players SET
                   elo     = MAX(100, elo + :delta),
                   losses  = losses + 1,
                   kills   = kills  + :k,
                   deaths  = deaths + :d,
                   assists = assists + :a
               WHERE name = :name""",
            {"delta": delta, "k": kills, "d": deaths, "a": assists, "name": name},
        )
        return
    db.execute(
        """UPDATE players SET
               elo     = elo + :delta,
               wins    = wins    + :won,
               losses  = losses  + :lost,
               kills   = kills   + :k,
               deaths  = deaths  + :d,
               assists = assists + :a
           WHERE name = :name""",
        {"delta": delta, "won": 1 if won else 0, "lost": 0 if won else 1,
         "k": kills, "d": deaths, "a": assists, "name": name},
    )


def _apply_map_result(db, name: str, map_name: str, base_elo: int, delta: int,
                       won: bool, disconnected: bool,
                       kills: int, deaths: int, assists: int) -> None:
    new_elo = max(100, base_elo + delta)
    if disconnected:
        db.execute("""
            INSERT INTO player_map_elo (name, map, elo, wins, losses, kills, deaths, assists)
            VALUES (:name, :map, :elo, 0, 1, :k, :d, :a)
            ON CONFLICT(name, map) DO UPDATE SET
                elo     = :elo,
                losses  = losses + 1,
                kills   = kills  + :k,
                deaths  = deaths + :d,
                assists = assists + :a
        """, {"name": name, "map": map_name, "elo": new_elo, "k": kills, "d": deaths, "a": assists})
        return
    db.execute("""
        INSERT INTO player_map_elo (name, map, elo, wins, losses, kills, deaths, assists)
        VALUES (:name, :map, :elo, :won, :lost, :k, :d, :a)
        ON CONFLICT(name, map) DO UPDATE SET
            elo     = :elo,
            wins    = wins    + :won,
            losses  = losses  + :lost,
            kills   = kills   + :k,
            deaths  = deaths  + :d,
            assists = assists + :a
    """, {"name": name, "map": map_name, "elo": new_elo,
          "won": 1 if won else 0, "lost": 0 if won else 1,
          "k": kills, "d": deaths, "a": assists})


async def handle_match(request: web.Request) -> web.Response:
    api_key = request.headers.get("X-Api-Key", "")

    try:
        body = await request.json()
    except Exception:
        return web.json_response({"detail": "Invalid JSON body"}, status=400)

    players = body.get("players") or []
    if not players:
        return web.json_response({"status": "ok", "elo_changes": {}})

    map_name = body.get("map", "Unknown")
    match_type = body.get("matchType", "Round")
    winning_team = body.get("winningTeam", 0)
    team1_wins = body.get("team1Wins", 0)
    team2_wins = body.get("team2Wins", 0)

    team1 = [p["name"] for p in players if p.get("team") == 1]
    team2 = [p["name"] for p in players if p.get("team") == 2]
    player_kills = {p["name"]: p.get("kills", 0) for p in players}

    with get_db() as db:
        host_discord_id = verify_host_key(db, api_key)
        if host_discord_id is None:
            return web.json_response({"detail": "Invalid API key"}, status=401)

        if is_host_suspended(db, host_discord_id):
            return web.json_response(
                {"detail": "This hosting key is suspended. Contact a server admin."}, status=403
            )

        if _is_submission_burst(host_discord_id):
            suspend_host_key(db, host_discord_id)
            log.warning(
                "Auto-suspended host key for discord_id=%s: more than %d matches within %ds",
                host_discord_id, ABUSE_MAX_IN_WINDOW, ABUSE_WINDOW_SECONDS,
            )
            return web.json_response(
                {"detail": "Too many matches submitted too quickly -- this hosting key has been "
                            "automatically suspended pending admin review."},
                status=403,
            )

        for p in players:
            db.execute("INSERT OR IGNORE INTO players (name) VALUES (?)", (p["name"],))

        placeholders = ",".join("?" * len(players))
        elos = {
            row["name"]: row["elo"]
            for row in db.execute(
                f"SELECT name, elo FROM players WHERE name IN ({placeholders})",
                [p["name"] for p in players],
            )
        }
        for p in players:
            elos.setdefault(p["name"], STARTING_ELO)

        changes = calculate_elo_changes(team1, team2, winning_team, elos, kills=player_kills)

        for p in players:
            name = p["name"]
            disconnected = p.get("disconnected", False)
            delta = changes[name]
            if disconnected:
                delta = min(delta, -abs(delta))
                changes[name] = delta
            _apply_result(
                db, name, delta, p.get("team") == winning_team, disconnected,
                p.get("kills", 0), p.get("deaths", 0), p.get("assists", 0),
            )

        # Per-map ELO, same treatment as overall.
        map_elos = {}
        for p in players:
            row = db.execute(
                "SELECT elo FROM player_map_elo WHERE name=? AND map=?",
                (p["name"], map_name),
            ).fetchone()
            map_elos[p["name"]] = row["elo"] if row else MAP_STARTING_ELO

        map_changes = calculate_elo_changes(team1, team2, winning_team, map_elos, kills=player_kills)

        for p in players:
            name = p["name"]
            disconnected = p.get("disconnected", False)
            map_delta = map_changes[name]
            if disconnected:
                map_delta = min(map_delta, -abs(map_delta))
                map_changes[name] = map_delta
            _apply_map_result(
                db, name, map_name, map_elos[name], map_delta,
                p.get("team") == winning_team, disconnected,
                p.get("kills", 0), p.get("deaths", 0), p.get("assists", 0),
            )

        db.execute(
            """INSERT INTO matches
                   (map, match_type, winning_team, team1_wins, team2_wins, players,
                    elo_changes, map_elo_changes, reported_by, played_at)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (map_name, match_type, winning_team, team1_wins, team2_wins,
             json.dumps(players), json.dumps(changes), json.dumps(map_changes),
             host_discord_id, datetime.now(timezone.utc).isoformat()),
        )

    return web.json_response({"status": "ok", "elo_changes": changes})


def create_app() -> web.Application:
    app = web.Application()
    app.router.add_post("/api/match", handle_match)
    return app
