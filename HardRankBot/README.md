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

## Full setup guide (Oracle Cloud — recommended)

This walks through everything end to end: creating the Discord bot, standing up a genuinely-free-forever VM on Oracle Cloud, and keeping the bot running permanently. If you'd rather run it somewhere else, skip to [Alternative hosting](#alternative-hosting) once you've done the Discord bot step.

### 1. Create the Discord bot

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications) → **New Application**, name it (e.g. "HardRank").
2. **Bot** tab → **Reset Token** → copy it somewhere safe. This is your `DISCORD_TOKEN`. You don't need to enable any of the "Privileged Gateway Intents" toggles — this bot only uses slash commands, it never reads message content.
3. **OAuth2 → URL Generator** → check scopes **bot** and **applications.commands** → under bot permissions check **Send Messages**, **Use Slash Commands**, and **Embed Links**.
4. Copy the generated URL at the bottom, open it in your browser, pick your server, authorize.
5. Get your server's ID for `GUILD_ID` (makes slash commands appear instantly instead of up to an hour later): in Discord, **User Settings → Advanced → Developer Mode** (toggle on), then right-click your server's icon → **Copy Server ID**.

### 2. Create the Oracle Cloud VM

1. Sign up at [oracle.com/cloud/free](https://www.oracle.com/cloud/free/) — needs email + phone + a credit card for identity verification (you will not be charged for Always Free resources).
2. In the Oracle Cloud Console: **☰ menu → Compute → Instances → Create Instance**.
3. Name it (e.g. `hardrankbot`).
4. Under **Image and shape**: click **Edit**, change the image to **Canonical Ubuntu** (22.04 or newer), and change the shape to **Ampere → VM.Standard.A1.Flex** — the generous Always Free ARM shape. 1 OCPU / 6GB is overkill for this bot but stays comfortably within the free limits.
5. Leave networking on its defaults, just confirm **"Assign a public IPv4 address"** is checked.
6. Under SSH keys, let Oracle **generate a key pair for you** and download the private key file — save it somewhere you'll remember (e.g. `C:\Users\you\.ssh\hardrankbot.pem`).
7. Click **Create**, wait until the instance shows **Running**, then note its **Public IP** on the instance's detail page.

### 3. Open the port

Oracle blocks inbound ports by default at two separate layers — both need opening:

1. On the instance's detail page, click the linked **Subnet** → **Security Lists** → the default list → **Add Ingress Rules**: Source CIDR `0.0.0.0/0`, IP Protocol `TCP`, Destination Port Range `8000` (or whatever you set `HTTP_PORT` to). Save.
2. Oracle's Ubuntu images also ship with `iptables` rules that block the port even after that — handled in the next step.

### 4. Connect and set up

From PowerShell (adjust the key path and IP):
```
ssh -i "C:\path\to\hardrankbot.pem" ubuntu@<the-public-ip>
```

Once connected:
```bash
# fix Oracle's default iptables block for our port
sudo iptables -I INPUT -p tcp --dport 8000 -j ACCEPT
sudo netfilter-persistent save

# install Python + git
sudo apt update && sudo apt install -y python3 python3-venv python3-pip git

# grab the code
git clone https://github.com/AaronTFrederick/Operation-SoftCurve.git
cd Operation-SoftCurve/HardRankBot

# set up a virtual environment and dependencies
python3 -m venv venv
source venv/bin/activate
pip install -r requirements.txt

# configure
cp .env.example .env
nano .env
```

In `nano`, fill in:
- `DISCORD_TOKEN` — from step 1.2
- `GUILD_ID` — from step 1.5
- `API_KEY` — generate one in another terminal tab with `openssl rand -hex 32` and paste the result. This must match the `ApiKey` setting in the BepInEx plugin's config on the game side (`BepInEx/config/com.fleeter.hardlineleaderboard.cfg`).
- leave `HTTP_HOST=0.0.0.0` and `HTTP_PORT=8000` as they are

Save (`Ctrl+O`, Enter, `Ctrl+X`), then test it:
```bash
python bot.py
```
You should see `Logged in as ...` and `Plugin API listening on http://0.0.0.0:8000/api/match`. Try `/leaderboard` in Discord — it should reply "No ranked matches recorded yet." `Ctrl+C` to stop once confirmed working.

### 5. Keep it running permanently

```bash
sudo tee /etc/systemd/system/hardrankbot.service > /dev/null <<'EOF'
[Unit]
Description=HardRank Bot
After=network.target

[Service]
WorkingDirectory=/home/ubuntu/Operation-SoftCurve/HardRankBot
ExecStart=/home/ubuntu/Operation-SoftCurve/HardRankBot/venv/bin/python bot.py
Restart=always
User=ubuntu

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now hardrankbot
sudo systemctl status hardrankbot   # should say "active (running)"
```
Watch logs anytime with `journalctl -u hardrankbot -f`. It'll now survive reboots and restart automatically if it ever crashes.

### 6. Point the game plugin at it

On whichever machine(s) host matches, edit `BepInEx/config/com.fleeter.hardlineleaderboard.cfg`:
```
ApiUrl = http://<your-oracle-public-ip>:8000
ApiKey = <the same value you put in .env>
```
Play a match, then check `/matches` or `/leaderboard` in Discord to confirm it recorded.

### Updating later

```bash
cd ~/Operation-SoftCurve
git pull
sudo systemctl restart hardrankbot
```

## Alternative hosting

**Your own always-on PC or Raspberry Pi.** Zero monthly cost, no cloud signup, but uptime depends on your home internet and power staying up, and you'll need port forwarding on your router plus a free dynamic-DNS service like [DuckDNS](https://www.duckdns.org/) since most home internet doesn't have a static IP. Once reachable, the rest is the same as steps 1 and 4-6 above (skip the Oracle-specific parts).

**Testing locally first.** You can always run `python bot.py` on your current PC to try the Discord side out before committing to any hosting — the plugin just won't be able to reach it from other machines until it's running somewhere with a stable public address.

## Database

SQLite, single file (`hardrank.sqlite` by default). Back it up periodically by just copying that file — no separate database server to run.
