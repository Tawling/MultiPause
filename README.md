## MultiPause
MultiPause is a mod that adds mechanisms for time to be paused in multi-player when players are in menus or other situations that would normally pause time in single-player mode.

For the purposes of this documentation, a state which would normally pause time in single player will be referred to as a **single-player-paused state**. Examples of
single-player-paused states include:

* Having the inventory/menu open
* Talking to NPCs
* Shopping
* The animation of harvesting/foraging
* Walking between areas or in/out of buildings

Depending on your config settings, if some or all players are in a single-player-paused state, time will pause for everyone.

**Only the host's config settings will be used**, however all players should have the mod installed.

The config file allows for three different pause modes: `"ANY"`, `"ALL"`, and `"AUTO"`. Default mode is `"AUTO"`.

With the exception of the `"ANY"` mode, this mod is designed to provide ways to allow time to pause in multiplayer without giving players extra time "for free."
See detailed descriptions of each pause mode below.

***WARNING: This mod uses [Harmony](https://github.com/pardeike/Harmony) to edit the game's `shouldTimePass()` method. This has the potential to conflict with other mods that may attempt to modify this same function.***


### `"ANY"` mode
The game will pause if **any** player is in a single-player-paused state. This will result in days being longer than usual, as anybody pausing for any reason will cause time to
pause for everyone.

_**NOTE:** This can be easily abused by having one person pause while others continue playing. Additionally, one player can intentionally delay or prevent other players from
performing time-based tasks by keeping their game paused and not letting time pass._

### `"ALL"` mode
The game will pause if **all** players are in a single-player-paused state. This will result in days still being shorter than they would in single-player mode because time would
only pause when all players are single-player-paused at once.

This mode is a simple solution that allows players to recover a little bit of the time they would otherwise lose in multi-player without having potential for abuse.

### `"AUTO"` mode
The game will pause if **the player who has paused for the least amount of time so far in the current day** is in a single-player-paused state.

This results in behavior similar to `"ALL"` mode, but it also allows for those small time losses from short single-player-paused states (such as walking between areas) to be
evened out over the course of the day.

As with `"ALL"` mode, it is still impossible to gain extra time "for free" with this mode, however the automatic balancing allows for all players to experience as much active play
time as possible depending on the actions of their fellow players.

If a player with the lowest cumulative pause time enters a single-player-paused state for long enough that they no longer have the lowest time, the game will automatically look
for the new lowest-time player, and pause/unpause time based on their single-player-paused state instead.

_**NOTE:** For players who do not have the lowest cumulative pause time it may appear that time pauses at arbitrary times when it would otherwise not pause.
This may be frustrating if you are waiting for a time-based event such as a shop opening. In this case, kindly get mad at your fellow players and tell them to unpause so you can
do the thing. Gosh. If this really gets too annoying, consider using `"ALL"` mode instead._