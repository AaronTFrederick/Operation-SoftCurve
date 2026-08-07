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
API_KEY = os.getenv("API_KEY", "")
DB_PATH = os.getenv("DB_PATH", "hardrank.sqlite")
HTTP_HOST = os.getenv("HTTP_HOST", "0.0.0.0")
HTTP_PORT = int(os.getenv("HTTP_PORT", "8000"))


def validate() -> None:
    missing = []
    if not DISCORD_TOKEN:
        missing.append("DISCORD_TOKEN")
    if not API_KEY or API_KEY == "changeme":
        missing.append("API_KEY (must not be blank or 'changeme')")
    if missing:
        sys.exit(f"FATAL: missing/invalid required config: {', '.join(missing)}. Check your .env file.")
