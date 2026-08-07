"""
ELO rating for team matches.

Adapted from the "Hardline Leaderboard" / HardRank project's backend
(Copyright 2026 Matthias Muhl, Apache License 2.0) — see NOTICE. Logic is
unchanged from the original; only this docstring and import path differ.

Each player has an individual rating. The expected outcome is computed
against the *average* ELO of the opposing team, so a player who is much
better than their teammates still benefits/suffers proportionally.

Kill performance is factored in via a bonus/penalty on top of the base
W/L delta: getting more kills than the match average adds up to
KILLS_MAX_BONUS ELO; fewer kills subtracts up to the same amount.
This means high-fraggers are rewarded even on the losing team, and
low-fraggers gain less even when they win.
"""

K_FACTOR = 90         # base multiplier; even match -> raw delta ~= 45
STARTING_ELO = 700
MIN_CHANGE = 35       # every match moves at least this many ELO points
MAX_CHANGE = 50       # base cap before kills bonus
KILLS_WEIGHT = 3.0    # ELO per kill above/below match average
KILLS_MAX_BONUS = 30  # cap on the kills component (+-30)


def _expected(player_elo: float, opponent_avg_elo: float) -> float:
    return 1.0 / (1.0 + 10 ** ((opponent_avg_elo - player_elo) / 400.0))


def _clamp_base(raw: float) -> int:
    """Keep the sign, clamp magnitude to [MIN_CHANGE, MAX_CHANGE]."""
    if raw == 0:
        return MIN_CHANGE   # tie-breaking edge case; shouldn't occur
    magnitude = max(MIN_CHANGE, min(MAX_CHANGE, abs(raw)))
    return round(magnitude) if raw > 0 else -round(magnitude)


def calculate_elo_changes(
    team1_names: list[str],
    team2_names: list[str],
    winning_team: int,          # 1 or 2
    elos: dict[str, int],
    kills: dict[str, int] | None = None,
) -> dict[str, int]:
    """
    Returns a dict {player_name: elo_delta} for every player in both teams.

    Base win/loss delta is clamped to [MIN_CHANGE, MAX_CHANGE].
    A kills bonus in the range [-KILLS_MAX_BONUS, +KILLS_MAX_BONUS] is then
    added on top, based on how each player's kills compare to the match
    average — so high-kill players lose less (or gain more) regardless of
    which side they were on.
    """
    avg1 = (
        sum(elos.get(n, STARTING_ELO) for n in team1_names) / len(team1_names)
        if team1_names else STARTING_ELO
    )
    avg2 = (
        sum(elos.get(n, STARTING_ELO) for n in team2_names) / len(team2_names)
        if team2_names else STARTING_ELO
    )

    changes: dict[str, int] = {}

    for name in team1_names:
        actual = 1.0 if winning_team == 1 else 0.0
        exp = _expected(elos.get(name, STARTING_ELO), avg2)
        changes[name] = _clamp_base(K_FACTOR * (actual - exp))

    for name in team2_names:
        actual = 1.0 if winning_team == 2 else 0.0
        exp = _expected(elos.get(name, STARTING_ELO), avg1)
        changes[name] = _clamp_base(K_FACTOR * (actual - exp))

    # ---------- kills bonus -----------------------------------------------
    if kills:
        all_names = team1_names + team2_names
        kill_values = [kills.get(n, 0) for n in all_names]
        avg_kills = sum(kill_values) / len(kill_values) if kill_values else 0.0

        for name in all_names:
            kill_diff = kills.get(name, 0) - avg_kills
            raw_bonus = kill_diff * KILLS_WEIGHT
            bonus = int(max(-KILLS_MAX_BONUS, min(KILLS_MAX_BONUS, round(raw_bonus))))
            changes[name] = changes[name] + bonus

    return changes
