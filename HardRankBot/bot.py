"""
HardRank Bot — entry point.

Runs two things in the same process, on the same asyncio event loop:
  1. The Discord bot (slash commands: /leaderboard, /rank, /link, /matches)
  2. A small HTTP listener that receives match reports from the
     HardlineLeaderboard BepInEx plugin (POST /api/match)

Run with: python bot.py
"""

import asyncio
import logging

import discord
from aiohttp import web
from discord import app_commands

import config
import db
from commands import register_commands
from plugin_api import create_app

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("hardrank")


class HardRankClient(discord.Client):
    def __init__(self):
        intents = discord.Intents.default()
        super().__init__(intents=intents)
        self.tree = app_commands.CommandTree(self)

    async def setup_hook(self) -> None:
        register_commands(self.tree)
        if config.GUILD_ID:
            guild = discord.Object(id=int(config.GUILD_ID))
            self.tree.copy_global_to(guild=guild)
            await self.tree.sync(guild=guild)
            log.info("Synced commands to guild %s", config.GUILD_ID)
        else:
            await self.tree.sync()
            log.info("Synced commands globally (can take up to an hour to appear)")

    async def on_ready(self) -> None:
        log.info("Logged in as %s", self.user)


async def run_http_server() -> None:
    app = create_app()
    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, config.HTTP_HOST, config.HTTP_PORT)
    await site.start()
    log.info("Plugin API listening on http://%s:%s/api/match", config.HTTP_HOST, config.HTTP_PORT)


async def main() -> None:
    config.validate()
    db.init(config.DB_PATH)

    client = HardRankClient()
    await run_http_server()
    await client.start(config.DISCORD_TOKEN)


if __name__ == "__main__":
    asyncio.run(main())
