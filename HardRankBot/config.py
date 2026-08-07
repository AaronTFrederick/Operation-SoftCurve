"""
Environment-variable configuration for HardRank Bot.

Reads a .env file (if present) via python-dotenv, then falls back to
whatever's already in the process environment.
"""

import os
import sys

from dotenv import load_dotenv

load_dotenv()

DISCORD_TOKEN = os.getenv("DISCORD_TOKEN", "")
GUILD_ID = os.getenv("GUILD_ID", "")  # optional: sync commands to one server instantly instead of waiting ~1hr for global sync
DB_PATH = os.getenv("DB_PATH", "hardrank.sqlite")
HTTP_HOST = os.getenv("HTTP_HOST", "0.0.0.0")
HTTP_PORT = int(os.getenv("HTTP_PORT", "8000"))

# No shared API_KEY here on purpose: match-report authorization is per-host
# via /hostkey (see db.py's host_keys table), not one secret handed to
# everyone who might host a match.


def validate() -> None:
    missing = []
    if not DISCORD_TOKEN:
        missing.append("DISCORD_TOKEN")
    if missing:
        sys.exit(f"FATAL: missing/invalid required config: {', '.join(missing)}. Check your .env file.")
