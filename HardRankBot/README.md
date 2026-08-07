# HardRank Bot

A free Discord-bot alternative to the old HardRank web leaderboard for Project Hardline. Same ELO/MMR math, same match-report contract from the `HardlineLeaderboard` BepInEx plugin — just a Discord bot instead of a paid website+server.

See [NOTICE](NOTICE) for what this reuses from the original HardRank project (Apache 2.0, Matthias Muhl) and what's different.

## What it does

- `/leaderboard [map]` — top ranked players, overall or per-map
- `/rank [player]` — a player's ELO, rank position, W/L, and K/D/A
- `/link <name>` — link your Discord account to your in-game name (so `/rank` with no argument works)
- `/matches [player]` — recent ranked match history

Not included (dropped from the original web system to keep this simple — see [NOTICE](NOTICE)): accounts/passwords, Steam login, clans, 1v1 challenges, the coin/lootbox economy, cosmetics, daily bonuses, admin panel.

## How it fits together

```
BepInEx plugin (HardlineLeaderboard.dll)
        |  POST /api/match  (same JSON + X-Api-Key contract as before)
        v
   bot.py process
     |-- discord.py bot  --> answers /leaderboard, /rank, /link, /matches
     |-- aiohttp listener --> receives match reports, applies ELO, writes to SQLite
        |
        v
   hardrank.sqlite (players, player_map_elo, matches, discord_links)
```

One process, one machine, no separate website or hosting bill.

## Setup

### 1. Create the Discord bot

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) → **New Application**.
2. Under **Bot**, click **Reset Token** to get your bot token (this is `DISCORD_TOKEN`).
3. Under **Bot**, make sure **Public Bot** is off unless you want strangers adding it elsewhere.
4. Under **OAuth2 → URL Generator**, check scopes `bot` and `applications.commands`, then under bot permissions check at least **Send Messages** and **Use Slash Commands**. Open the generated URL to invite the bot to your server.

### 2. Configure

```
cd HardRankBot
cp .env.example .env
```

Edit `.env`:
- `DISCORD_TOKEN` — from step 1.
- `GUILD_ID` — right-click your Discord server icon → Copy Server ID (enable Developer Mode in Discord settings first if you don't see this option). Optional, but without it slash commands can take up to an hour to appear after each restart.
- `API_KEY` — make up a long random string. This must match the `ApiKey` setting in the BepInEx plugin's config (`BepInEx/config/com.fleeter.hardlineleaderboard.cfg` on the game side).

### 3. Install dependencies and run

```
pip install -r requirements.txt
python bot.py
```

You should see `Logged in as ...` and `Plugin API listening on http://0.0.0.0:8000/api/match` in the console.

### 4. Point the game plugin at it

In `BepInEx/config/com.fleeter.hardlineleaderboard.cfg` on whichever machine hosts matches, set:
```
ApiUrl = http://<your-bot's-address>:8000
ApiKey = <same value as API_KEY in .env>
```

If the bot is running on the same machine as the test host, `http://localhost:8000` works. For real community use, it needs to be reachable by whoever is hosting a match, which means the bot needs to run somewhere with a stable, reachable address — see hosting below.

## Hosting (so it runs 24/7 without costing anything)

**Recommended: Oracle Cloud "Always Free" tier.** It's free forever (not a trial), no time limit — a small VM there comfortably runs this bot plus SQLite indefinitely at zero cost. Requires a credit card on file for identity verification, but you won't be charged as long as you stay within the Always Free resource limits, which this project is nowhere near.

1. Sign up at [oracle.com/cloud/free](https://www.oracle.com/cloud/free/) and create an Always Free **Compute** instance (Ampere/ARM shape — the free tier's most generous option; pick the Ubuntu image).
2. Open the instance's public IP to inbound traffic on your chosen `HTTP_PORT` (default 8000) via the instance's attached security list/network security group in the Oracle console, plus the OS firewall if Ubuntu's `ufw` is enabled (`sudo ufw allow 8000`).
3. SSH in, install Python 3.11+, clone/copy this folder over, then follow **Setup** above.
4. Keep it running after you disconnect — either `nohup python bot.py &`, or better, set it up as a systemd service so it restarts automatically on crash/reboot:
   ```
   [Unit]
   Description=HardRank Bot
   After=network.target

   [Service]
   WorkingDirectory=/home/ubuntu/HardRankBot
   ExecStart=/usr/bin/python3 bot.py
   Restart=always
   User=ubuntu

   [Install]
   WantedBy=multi-user.target
   ```
   Save as `/etc/systemd/system/hardrankbot.service`, then `sudo systemctl enable --now hardrankbot`.
5. Set `HTTP_HOST=0.0.0.0` in `.env` (already the default) so it accepts connections from outside the VM, and use the VM's public IP as the plugin's `ApiUrl`.

**Alternative: your own always-on PC or Raspberry Pi.** Zero monthly cost, but uptime depends on your home internet and power staying up, and you'll need port forwarding on your router plus a free dynamic-DNS service like [DuckDNS](https://www.duckdns.org/) if you don't have a static IP (most home internet doesn't).

## Database

SQLite, single file (`hardrank.sqlite` by default). Back it up periodically by just copying that file — no separate database server to run.
