# Hardline Leaderboard (plugin)

The BepInEx client plugin that reports match results to [HardRankBot](../HardRankBot/). Install this alongside your other mods if you want matches you host to count toward the ranked leaderboard.

See [NOTICE](NOTICE) for attribution — this is Matthias Muhl's ("fleeter") original plugin from the HardRank project (Apache 2.0), with minimal changes to point it at HardRankBot instead of the original hosted site.

## Installing (pre-built)

Drop `HardlineLeaderboard.dll` into `BepInEx/plugins/`. Launch the game once to generate `BepInEx/config/com.fleeter.hardlineleaderboard.cfg`, then edit it:

```
ApiUrl = <your community's HardRankBot address>
ApiKey = <your personal key from /hostkey in Discord>
```

See [HardRankBot's README](../HardRankBot/README.md#for-players-getting-your-hosting-api-key) for how to get your key. You only need this if you **host** matches — people who just join don't need this plugin at all, since the host's copy reports the whole match.

## Building

Same pattern as the other mods — see the [main README](../README.md#building). One difference: this project targets `netstandard2.1` rather than `net472`, and additionally references `com.rlabrecque.steamworks.net.dll` from `Project Hardline_Data/Managed/`.

**Building on Mac:** the original project this was adapted from was built against a Mac install (`Project Hardline.app/Contents/Resources/Data/Managed/...`). The `.csproj` in this repo has been rewritten with Windows-style paths instead — Mac builders will need to point the `HintPath`s at their own `.app` bundle's Managed folder.

## License

Apache 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE). Different from this repo's mods (MIT) — see the [main README](../README.md#license) for why.
