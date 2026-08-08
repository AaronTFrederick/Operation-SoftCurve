"""
SQLite schema and connection helper for HardRank Bot.

Schema is trimmed from the original HardRank backend (see NOTICE) down to
just the ranked core: players, per-map ELO, and match history. Auth, clans,
challenges, and the item/lootbox economy are intentionally not included —
Discord identity replaces the old account system via the discord_links table.
"""

import json
import secrets
import sqlite3
from contextlib import contextmanager
from datetime import datetime, timezone

_db_path: str | None = None


def init(db_path: str) -> None:
    """Set the database path and create tables if they don't exist yet."""
    global _db_path
    _db_path = db_path

    with get_db() as db:
        db.executescript("""
            CREATE TABLE IF NOT EXISTS players (
                name     TEXT PRIMARY KEY,
                elo      INTEGER NOT NULL DEFAULT 700,
                wins     INTEGER NOT NULL DEFAULT 0,
                losses   INTEGER NOT NULL DEFAULT 0,
                kills    INTEGER NOT NULL DEFAULT 0,
                deaths   INTEGER NOT NULL DEFAULT 0,
                assists  INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS player_map_elo (
                name    TEXT NOT NULL,
                map     TEXT NOT NULL,
                elo     INTEGER NOT NULL DEFAULT 700,
                wins    INTEGER NOT NULL DEFAULT 0,
                losses  INTEGER NOT NULL DEFAULT 0,
                kills   INTEGER NOT NULL DEFAULT 0,
                deaths  INTEGER NOT NULL DEFAULT 0,
                assists INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (name, map)
            );

            CREATE TABLE IF NOT EXISTS matches (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                map             TEXT    NOT NULL,
                match_type      TEXT    NOT NULL DEFAULT 'Round',
                winning_team    INTEGER NOT NULL,
                team1_wins      INTEGER NOT NULL,
                team2_wins      INTEGER NOT NULL,
                players         TEXT    NOT NULL,
                elo_changes     TEXT    NOT NULL,
                map_elo_changes TEXT,
                reported_by     TEXT,
                played_at       TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS discord_links (
                discord_id  TEXT PRIMARY KEY,
                player_name TEXT NOT NULL,
                linked_at   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS host_keys (
                discord_id TEXT PRIMARY KEY,
                api_key    TEXT UNIQUE NOT NULL,
                created_at TEXT NOT NULL
            );
        """)

        # Migration for databases created before reported_by/map_elo_changes existed --
        # CREATE TABLE IF NOT EXISTS above won't add columns to an already-existing table.
        match_cols = {row["name"] for row in db.execute("PRAGMA table_info(matches)")}
        if "reported_by" not in match_cols:
            db.execute("ALTER TABLE matches ADD COLUMN reported_by TEXT")
        if "map_elo_changes" not in match_cols:
            db.execute("ALTER TABLE matches ADD COLUMN map_elo_changes TEXT")


@contextmanager
def get_db():
    if _db_path is None:
        raise RuntimeError("db.init() must be called before get_db()")
    conn = sqlite3.connect(_db_path)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    try:
        yield conn
        conn.commit()
    finally:
        conn.close()


def leaderboard_rows(db: sqlite3.Connection, limit: int = 15) -> list[dict]:
    rows = db.execute("""
        SELECT name, elo, wins, losses, kills, deaths, assists,
               CASE WHEN wins + losses > 0
                    THEN ROUND(wins * 100.0 / (wins + losses), 1)
                    ELSE 0.0
               END AS win_rate
        FROM players
        ORDER BY elo DESC
        LIMIT ?
    """, (limit,)).fetchall()
    return [dict(r) for r in rows]


def map_leaderboard_rows(db: sqlite3.Connection, map_name: str, limit: int = 15) -> list[dict]:
    rows = db.execute("""
        SELECT name, elo, wins, losses, kills, deaths, assists,
               CASE WHEN wins + losses > 0
                    THEN ROUND(wins * 100.0 / (wins + losses), 1)
                    ELSE 0.0
               END AS win_rate
        FROM player_map_elo
        WHERE map = ?
        ORDER BY elo DESC
        LIMIT ?
    """, (map_name, limit)).fetchall()
    return [dict(r) for r in rows]


def player_rank_position(db: sqlite3.Connection, name: str) -> tuple[int, int] | None:
    """Returns (rank, total_players) for a player by overall ELO, or None if not found."""
    row = db.execute("SELECT elo FROM players WHERE LOWER(name) = LOWER(?)", (name,)).fetchone()
    if row is None:
        return None
    higher = db.execute("SELECT COUNT(*) FROM players WHERE elo > ?", (row["elo"],)).fetchone()[0]
    total = db.execute("SELECT COUNT(*) FROM players").fetchone()[0]
    return higher + 1, total


def find_player(db: sqlite3.Connection, name: str) -> dict | None:
    row = db.execute("SELECT * FROM players WHERE LOWER(name) = LOWER(?)", (name,)).fetchone()
    return dict(row) if row else None


def linked_player_name(db: sqlite3.Connection, discord_id: str) -> str | None:
    row = db.execute("SELECT player_name FROM discord_links WHERE discord_id = ?", (discord_id,)).fetchone()
    return row["player_name"] if row else None


def get_or_create_host_key(db: sqlite3.Connection, discord_id: str) -> str:
    """Returns this user's existing personal hosting API key, creating one if needed."""
    row = db.execute("SELECT api_key FROM host_keys WHERE discord_id = ?", (discord_id,)).fetchone()
    if row:
        return row["api_key"]
    new_key = secrets.token_hex(24)
    db.execute(
        "INSERT INTO host_keys (discord_id, api_key, created_at) VALUES (?, ?, ?)",
        (discord_id, new_key, datetime.now(timezone.utc).isoformat()),
    )
    return new_key


def regenerate_host_key(db: sqlite3.Connection, discord_id: str) -> str:
    """Replaces this user's hosting API key with a new one, invalidating the old one."""
    new_key = secrets.token_hex(24)
    now = datetime.now(timezone.utc).isoformat()
    db.execute(
        """INSERT INTO host_keys (discord_id, api_key, created_at) VALUES (?, ?, ?)
           ON CONFLICT(discord_id) DO UPDATE SET api_key = ?, created_at = ?""",
        (discord_id, new_key, now, new_key, now),
    )
    return new_key


def revoke_host_key(db: sqlite3.Connection, discord_id: str) -> bool:
    """Deletes a user's hosting key. Returns False if they didn't have one."""
    cur = db.execute("DELETE FROM host_keys WHERE discord_id = ?", (discord_id,))
    return cur.rowcount > 0


def verify_host_key(db: sqlite3.Connection, api_key: str) -> str | None:
    """Returns the owning discord_id if the key is valid, else None."""
    if not api_key:
        return None
    row = db.execute("SELECT discord_id FROM host_keys WHERE api_key = ?", (api_key,)).fetchone()
    return row["discord_id"] if row else None


def recent_matches_with_reporter(db: sqlite3.Connection, limit: int = 20) -> list[dict]:
    """For admin triage: who (by discord_id) reported each recent match.
    reported_by is NULL for matches recorded before this tracking existed."""
    rows = db.execute(
        "SELECT id, map, winning_team, reported_by, played_at FROM matches ORDER BY played_at DESC LIMIT ?",
        (limit,),
    ).fetchall()
    return [dict(r) for r in rows]


def matches_by_reporter(db: sqlite3.Connection, discord_id: str) -> list[dict]:
    rows = db.execute(
        "SELECT * FROM matches WHERE reported_by = ? ORDER BY played_at DESC", (discord_id,)
    ).fetchall()
    return [dict(r) for r in rows]


def undo_match(db: sqlite3.Connection, match_id: int) -> dict | None:
    """Reverses a match's effect on players/player_map_elo and deletes it.
    Returns the deleted match's row (as a dict), or None if it didn't exist.

    Note: global ELO/stats reverse exactly (elo_changes is recorded with no
    floor clamp on write). Per-map ELO can drift slightly if the original
    write was clamped at the 100 floor, since that floor isn't reversible
    after the fact -- acceptable imprecision for cleaning up bad matches,
    not for exact bookkeeping.
    """
    row = db.execute("SELECT * FROM matches WHERE id = ?", (match_id,)).fetchone()
    if row is None:
        return None
    match = dict(row)
    players = json.loads(match["players"])
    elo_changes = json.loads(match["elo_changes"])
    map_elo_changes = json.loads(match["map_elo_changes"]) if match["map_elo_changes"] else None
    winning_team = match["winning_team"]
    map_name = match["map"]

    for p in players:
        name = p["name"]
        delta = elo_changes.get(name, 0)
        won = p.get("team") == winning_team
        disconnected = p.get("disconnected", False)
        kills, deaths, assists = p.get("kills", 0), p.get("deaths", 0), p.get("assists", 0)

        if disconnected:
            db.execute(
                """UPDATE players SET
                       elo = elo - :delta, losses = losses - 1,
                       kills = kills - :k, deaths = deaths - :d, assists = assists - :a
                   WHERE name = :name""",
                {"delta": delta, "k": kills, "d": deaths, "a": assists, "name": name},
            )
        else:
            db.execute(
                """UPDATE players SET
                       elo = elo - :delta, wins = wins - :won, losses = losses - :lost,
                       kills = kills - :k, deaths = deaths - :d, assists = assists - :a
                   WHERE name = :name""",
                {"delta": delta, "won": 1 if won else 0, "lost": 0 if won else 1,
                 "k": kills, "d": deaths, "a": assists, "name": name},
            )

        if map_elo_changes is not None:
            map_delta = map_elo_changes.get(name, 0)
            if disconnected:
                db.execute(
                    """UPDATE player_map_elo SET
                           elo = elo - :delta, losses = losses - 1,
                           kills = kills - :k, deaths = deaths - :d, assists = assists - :a
                       WHERE name = :name AND map = :map""",
                    {"delta": map_delta, "k": kills, "d": deaths, "a": assists, "name": name, "map": map_name},
                )
            else:
                db.execute(
                    """UPDATE player_map_elo SET
                           elo = elo - :delta, wins = wins - :won, losses = losses - :lost,
                           kills = kills - :k, deaths = deaths - :d, assists = assists - :a
                       WHERE name = :name AND map = :map""",
                    {"delta": map_delta, "won": 1 if won else 0, "lost": 0 if won else 1,
                     "k": kills, "d": deaths, "a": assists, "name": name, "map": map_name},
                )

    db.execute("DELETE FROM matches WHERE id = ?", (match_id,))
    return match


def undo_matches_by_reporter(db: sqlite3.Connection, discord_id: str) -> tuple[int, set[str]]:
    """Undoes every match reported by this discord_id. Returns (count_undone, affected_player_names)."""
    matches = matches_by_reporter(db, discord_id)
    affected: set[str] = set()
    for m in matches:
        for p in json.loads(m["players"]):
            affected.add(p["name"])
        undo_match(db, m["id"])
    return len(matches), affected
