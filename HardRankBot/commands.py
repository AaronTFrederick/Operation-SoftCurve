"""
Discord slash commands for HardRank Bot.

Discord identity replaces the old website's username/password accounts:
players self-link their Discord account to their in-game name with /link,
then /rank with no argument uses that link. No passwords, no Steam OpenID —
if that turns out to be a problem (name-squatting) it can be added later.
"""

from datetime import datetime, timezone

import discord
from discord import app_commands

from db import get_db, leaderboard_rows, map_leaderboard_rows, player_rank_position, find_player, linked_player_name

REAL_MAPS = ["Compound", "Shipyard", "Fortress", "Lost Island", "Frost"]


def register_commands(tree: app_commands.CommandTree) -> None:

    @tree.command(name="leaderboard", description="Show the top ranked players")
    @app_commands.describe(map="Show a per-map leaderboard instead of overall")
    @app_commands.choices(map=[app_commands.Choice(name=m, value=m) for m in REAL_MAPS])
    async def leaderboard(interaction: discord.Interaction, map: app_commands.Choice[str] | None = None):
        with get_db() as db:
            if map is not None:
                rows = map_leaderboard_rows(db, map.value)
                title = f"HardRank Leaderboard — {map.value}"
            else:
                rows = leaderboard_rows(db)
                title = "HardRank Leaderboard — Overall"

        if not rows:
            await interaction.response.send_message("No ranked matches recorded yet.")
            return

        lines = []
        for i, r in enumerate(rows, start=1):
            lines.append(
                f"**{i}.** {r['name']} — {r['elo']} ELO "
                f"({r['wins']}W-{r['losses']}L, {r['win_rate']}% WR)"
            )

        embed = discord.Embed(title=title, description="\n".join(lines), color=0x6366F1)
        await interaction.response.send_message(embed=embed)

    @tree.command(name="rank", description="Show a player's rank and stats")
    @app_commands.describe(player="In-game player name (leave blank to use your /link'd name)")
    async def rank(interaction: discord.Interaction, player: str | None = None):
        with get_db() as db:
            name = player
            if name is None:
                name = linked_player_name(db, str(interaction.user.id))
                if name is None:
                    await interaction.response.send_message(
                        "You haven't linked your in-game name yet — use `/link <name>` first, "
                        "or specify a player: `/rank player:<name>`.",
                        ephemeral=True,
                    )
                    return

            row = find_player(db, name)
            if row is None:
                await interaction.response.send_message(f"No ranked data found for **{name}**.")
                return

            position = player_rank_position(db, name)

        rank_text = f"#{position[0]} of {position[1]}" if position else "unranked"
        embed = discord.Embed(title=f"{row['name']} — {rank_text}", color=0x6366F1)
        embed.add_field(name="ELO", value=str(row["elo"]))
        embed.add_field(name="Record", value=f"{row['wins']}W-{row['losses']}L")
        embed.add_field(name="K/D/A", value=f"{row['kills']}/{row['deaths']}/{row['assists']}")
        await interaction.response.send_message(embed=embed)

    @tree.command(name="link", description="Link your Discord account to your in-game player name")
    @app_commands.describe(name="Your exact in-game player name")
    async def link(interaction: discord.Interaction, name: str):
        with get_db() as db:
            existing = find_player(db, name)
            if existing is None:
                await interaction.response.send_message(
                    f"**{name}** has no ranked match history yet — play a ranked match first, "
                    "then link once you show up on the leaderboard.",
                    ephemeral=True,
                )
                return

            db.execute(
                """INSERT INTO discord_links (discord_id, player_name, linked_at)
                   VALUES (?, ?, ?)
                   ON CONFLICT(discord_id) DO UPDATE SET player_name = ?, linked_at = ?""",
                (
                    str(interaction.user.id), existing["name"], datetime.now(timezone.utc).isoformat(),
                    existing["name"], datetime.now(timezone.utc).isoformat(),
                ),
            )

        await interaction.response.send_message(
            f"Linked your Discord account to **{existing['name']}**. Try `/rank` now.",
            ephemeral=True,
        )

    @tree.command(name="matches", description="Show recent ranked match history")
    @app_commands.describe(player="Filter to matches involving this player (optional)")
    async def matches(interaction: discord.Interaction, player: str | None = None):
        import json as _json

        with get_db() as db:
            rows = db.execute(
                "SELECT * FROM matches ORDER BY played_at DESC LIMIT 50"
            ).fetchall()

        lines = []
        for row in rows:
            players = _json.loads(row["players"])
            if player and not any(p["name"].lower() == player.lower() for p in players):
                continue
            team1 = ", ".join(p["name"] for p in players if p.get("team") == 1)
            team2 = ", ".join(p["name"] for p in players if p.get("team") == 2)
            winner = "Team 1" if row["winning_team"] == 1 else "Team 2"
            played_at = row["played_at"][:16].replace("T", " ")
            lines.append(f"**{row['map']}** ({played_at} UTC) — {winner} won\n  T1: {team1}\n  T2: {team2}")
            if len(lines) >= 10:
                break

        if not lines:
            await interaction.response.send_message("No matching match history found.")
            return

        embed = discord.Embed(
            title="Recent Matches" if not player else f"Recent Matches — {player}",
            description="\n\n".join(lines),
            color=0x6366F1,
        )
        await interaction.response.send_message(embed=embed)
