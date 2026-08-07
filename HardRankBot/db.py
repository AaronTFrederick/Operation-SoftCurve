"""
SQLite schema and connection helper for HardRank Bot.

Schema is trimmed from the original HardRank backend (see NOTICE) down to
just the ranked core: players, per-map ELO, and match history. Auth, clans,
challenges, and the item/lootbox economy are intentionally not included —
Discord identity replaces the old account system via the discord_links table.
"""

import sqlite3
from contextlib import contextmanager

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
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                map          TEXT    NOT NULL,
                match_type   TEXT    NOT NULL DEFAULT 'Round',
                winning_team INTEGER NOT NULL,
                team1_wins   INTEGER NOT NULL,
                team2_wins   INTEGER NOT NULL,
                players      TEXT    NOT NULL,
                elo_changes  TEXT    NOT NULL,
                played_at    TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS discord_links (
                discord_id  TEXT PRIMARY KEY,
                player_name TEXT NOT NULL,
                linked_at   TEXT NOT NULL
            );
        """)


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
