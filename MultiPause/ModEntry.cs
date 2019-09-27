using Harmony;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;

namespace MultiPause
{
    class ModEntry : Mod
    {
        /**********
        ** Constants
        **********/
        public const string PAUSE_ANY = "ANY";
        public const string PAUSE_ALL = "ALL";
        public const string PAUSE_AUTO = "AUTO";

        public enum TimePassState
        {
            Pass,
            Freeze,
            Pause
        }

        /**********
        ** Properties
        **********/

        public bool IsPaused = false;
        public bool QueryMessageSent = false;

        public string PauseMode;
        public bool InitialPauseState => PauseMode != PAUSE_ANY;

        int prevGameTimeInterval = -1;
        TimePassState prevTimePassState = TimePassState.Pass;
        bool patched = false;

        public static Config Config { get; private set; }
        static Dictionary<long, PlayerState> PlayerStates { get; set; } = new Dictionary<long, PlayerState>();

        public static bool ForceSinglePlayerCheck = false;

        /**********
        ** Public methods
        **********/
        public override void Entry(IModHelper helper)
        {

            Config = helper.ReadConfig<Config>();

            PauseMode = Config.PauseMode_ANY_ALL_AUTO.ToUpper();

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Multiplayer.PeerContextReceived += this.OnPeerContextReceived;
            helper.Events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
            helper.Events.Multiplayer.PeerDisconnected += this.OnPeerDisconnected;
        }

        void applyPatch()
        {
            if (patched) return;
            Monitor.Log("Patching StardewValley.Game1 - Boolean shouldTimePass()", LogLevel.Debug);
            var harmony = HarmonyInstance.Create("taw.multipause");
            var original = typeof(Game1).GetMethod("shouldTimePass");
            var prefix = typeof(ShouldTimePassPatch).GetMethod("Prefix");
            var transpiler = typeof(ShouldTimePassPatch).GetMethod("Transpiler");
            harmony.Patch(original, prefix: new HarmonyMethod(prefix), transpiler: new HarmonyMethod(transpiler));
            patched = true;
        }

        public void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            Monitor.Log("Game Launched");

            applyPatch();

        }

        public void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            if (Context.IsMultiplayer)
            {
                if (!Context.IsMainPlayer)
                    Monitor.Log("Connected as non-host player. Only the host's PauseMode config setting will be used.", LogLevel.Info);
                else
                    Monitor.Log($"Connected as host player. PauseMode set to \"{PauseMode}\"", LogLevel.Info);
            }

            IsPaused = InitialPauseState;

            PlayerStates = new Dictionary<long, PlayerState>();
            var state = GetPlayerState(Game1.player.UniqueMultiplayerID);
            state.IsHost = Context.IsMainPlayer;
            state.ConfigPauseMode = PauseMode;
        }

        public void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            foreach (KeyValuePair<long, PlayerState> entry in PlayerStates)
            {
                entry.Value.TotalTimePaused = 0;
            }
        }

        public void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            QueryMessageSent = false;
        }

        public void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (Context.IsWorldReady && Game1.IsMultiplayer)
            {
                // Update states if host player
                foreach (KeyValuePair<long, PlayerState> entry in PlayerStates)
                {
                    if (entry.Value.IsPaused || !entry.Value.IsOnline)
                    {
                        entry.Value.TotalTimePaused++;
                    }
                }

                // Retrieve state as if in single player
                ForceSinglePlayerCheck = true;
                var isPaused = !Game1.shouldTimePass();
                ForceSinglePlayerCheck = false;

                // Check player's pause state and broadcast changes
                if (isPaused != IsPaused)
                {
                    IsPaused = isPaused;
                    var state = GetPlayerState(Game1.player.UniqueMultiplayerID);
                    state.IsPaused = isPaused;
                    state.Ticks = Game1.ticks;

                    if (PauseMode != Config.PauseMode_ANY_ALL_AUTO.ToUpper())
                    {
                        state.ConfigPauseMode = Config.PauseMode_ANY_ALL_AUTO.ToUpper();
                        if (Context.IsMainPlayer)
                            PauseMode = state.ConfigPauseMode;
                    }

                    this.Helper.Multiplayer.SendMessage(state, STPMessage.Update, modIDs: new[] { this.ModManifest.UniqueID });
                }

                TimePassState timePassState = GetTimePassState();
                if (timePassState != TimePassState.Pass)
                {
                    Game1.gameTimeInterval = Game1.gameTimeInterval < prevGameTimeInterval ? 0 : prevGameTimeInterval;
                }
                else
                {
                    prevGameTimeInterval = Game1.gameTimeInterval;
                }

                if (timePassState != prevTimePassState)
                {
                    prevTimePassState = timePassState;
                    Monitor.Log($"Time is now {(timePassState == TimePassState.Pass ? "passing normally" : (timePassState == TimePassState.Freeze ? "frozen" : "paused"))}.", LogLevel.Info);
                    foreach (var state in PlayerStates)
                    {
                        Monitor.Log($"{state.Key} -- {state.Value}");
                    }
                }

                // Send query message if needed
                if (!QueryMessageSent)
                {
                    this.Helper.Multiplayer.SendMessage(true, STPMessage.Query, modIDs: new[] { this.ModManifest.UniqueID });
                    QueryMessageSent = true;
                }
            }
            else
            {
                QueryMessageSent = false;
            }
        }

        public void OnPeerContextReceived(object sender, PeerContextReceivedEventArgs e)
        {
            var state = GetPlayerState(e.Peer.PlayerID);

            state.IsOnline = true;
            state.IsPaused = InitialPauseState;
        }

        public void OnPeerDisconnected(object sender, PeerDisconnectedEventArgs e)
        {
            var state = GetPlayerState(e.Peer.PlayerID);
            state.IsOnline = false;
            state.IsPaused = true;
        }

        public void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            if (e.Type == STPMessage.Update && e.FromModID == this.ModManifest.UniqueID)
            {
                // Update player state based on message
                var messageState = e.ReadAs<PlayerState>();
                var state = GetPlayerState(e.FromPlayerID);
                if (state.IsOnline && messageState.Ticks >= state.Ticks) {
                    state.Ticks = messageState.Ticks;
                    state.IsPaused = messageState.IsPaused;
                    state.IsHost = messageState.IsHost;
                    state.ConfigPauseMode = messageState.ConfigPauseMode;
                    if (state.IsHost && PauseMode != state.ConfigPauseMode)
                        PauseMode = state.ConfigPauseMode;
                }
            }
            else if (e.Type == STPMessage.Query && e.FromModID == this.ModManifest.UniqueID)
            {
                var state = GetPlayerState(Game1.player.UniqueMultiplayerID);
                this.Helper.Multiplayer.SendMessage(state, STPMessage.Update, modIDs: new[] { this.ModManifest.UniqueID });
                // Host needs to send all player states so clients can synchronize values such as TotalTimePaused
                if (Context.IsMainPlayer)
                {
                    this.Helper.Multiplayer.SendMessage(PlayerStates, STPMessage.AllPlayerStates, modIDs: new[] { this.ModManifest.UniqueID });
                }
            }
            else if (e.Type == STPMessage.AllPlayerStates && e.FromModID == this.ModManifest.UniqueID)
            {
                var message = e.ReadAs<Dictionary<long, PlayerState>>();
                foreach(var item in message)
                {
                    var state = GetPlayerState(item.Key);
                    state.TotalTimePaused = item.Value.TotalTimePaused;
                    state.IsOnline = item.Value.IsOnline;
                    state.IsHost = item.Value.IsHost;
                    state.ConfigPauseMode = item.Value.ConfigPauseMode;
                }
            }
        }

        /**********
        ** Methods
        **********/

        void ReloadConfig()
        {
            Config = Helper.ReadConfig<Config>();
        }

        PlayerState GetPlayerState(long playerID)
        {
            PlayerStates.TryGetValue(playerID, out PlayerState dictState);
            if (dictState == null)
            {
                dictState = new PlayerState(InitialPauseState, 0, totalTimePaused: GetMinTimePaused());
                PlayerStates.Add(playerID, dictState);
            }
            return dictState;
        }

        /**********
        ** Static methods
        **********/

        public static bool GetIsMultiplayerForShouldTimePass()
        {
            return ForceSinglePlayerCheck ? false : Game1.IsMultiplayer;
        }

        public static bool ShouldTimePassForCurrentPlayer()
        {
            // Vanilla logic minus multiplayer check
            if (Game1.isFestival() || Game1.CurrentEvent != null && Game1.CurrentEvent.isWedding)
                return false;
            if (Game1.paused || Game1.freezeControls || Game1.overlayMenu != null || Game1.activeClickableMenu != null && !(Game1.activeClickableMenu is StardewValley.Menus.BobberBar))
                return false;
            if (!Game1.player.CanMove && !Game1.player.UsingTool)
                return Game1.player.forceTimePass;
            return true;
        }

        public static int GetMinTimePaused()
        {
            int min = Int32.MaxValue;

            foreach (KeyValuePair<long, PlayerState> entry in PlayerStates)
            {
                min = Math.Min(min, entry.Value.TotalTimePaused);
            }

            return Math.Max(min, 0);
        }

        public static TimePassState GetTimePassState()
        {
            bool freeze = false;
            bool allPaused = true;
            if (Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_ALL)
            {
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.TryGetValue(farmer.UniqueMultiplayerID, out PlayerState state);
                    if (state != null && state.IsOnline && !state.IsPaused)
                    {
                        allPaused = false;
                    }
                }
                freeze = allPaused;
            }
            else if (Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_ANY)
            {
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.TryGetValue(farmer.UniqueMultiplayerID, out PlayerState state);
                    if (state != null && state.IsOnline)
                    {
                        if (state.IsPaused)
                        {
                            freeze = true;
                        }
                        else
                        {
                            allPaused = false;
                        }
                    }
                }
            }
            else if (Config.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_AUTO)
            {

                int min = Int32.MaxValue;
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.TryGetValue(farmer.UniqueMultiplayerID, out PlayerState state);
                    if (state != null && state.IsOnline)
                    {
                        if (!state.IsPaused) allPaused = false;

                        if (state.TotalTimePaused == min && state.IsPaused)
                        {
                            freeze = true;
                        }
                        else if (state.TotalTimePaused < min)
                        {
                            min = state.TotalTimePaused;
                            freeze = state.IsPaused;
                        }
                    }
                }
            }
            return freeze ? (allPaused ? TimePassState.Pause : TimePassState.Freeze) : TimePassState.Pass;
        }



        /**********
        ** Classes
        **********/

        class STPMessage
        {
            public static string Query { get; } = "QueryStates";
            public static string Update { get; } = "PlayerStateChanged";
            public static string AllPlayerStates { get; } = "AllPauseTimes";
        }
        class PlayerState
        {
            public bool IsPaused = false;
            public int Ticks = 0;
            public int TotalTimePaused;
            public bool IsOnline;
            public bool IsHost;
            public string ConfigPauseMode;

            public PlayerState(bool isPaused, int ticks, int totalTimePaused = 0, bool isOnline = true, bool isHost = false, string configPauseMode = PAUSE_AUTO)
            {
                IsPaused = isPaused;
                Ticks = ticks;
                TotalTimePaused = totalTimePaused;
                IsOnline = isOnline;
                IsHost = isHost;
                ConfigPauseMode = configPauseMode;
            }

            public override string ToString()
            {
                return $"paused: {IsPaused}, total: {TotalTimePaused}, online: {IsOnline}, host: {IsHost}, mode: {ConfigPauseMode}";
            }
        }


        /**********
        ** Harmony patches
        **********/

        [HarmonyPatch(typeof(Game1))]
        [HarmonyPatch("shouldTimePass")]
        class ShouldTimePassPatch
        {
            public static bool Prefix(ref bool __result)
            {
                if (Game1.IsMultiplayer && !ForceSinglePlayerCheck)
                {
                    __result = GetTimePassState() == TimePassState.Pass;
                    return false;
                }
                return true;
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                List<CodeInstruction> instructionList = instructions.ToList();

                for (int i = 0; i < instructionList.Count; i++)
                {
                    CodeInstruction c = instructionList[i];
                    if (c.opcode == OpCodes.Call && c.operand.ToString().Contains("get_IsMultiplayer"))
                    {
                        instructionList[i] = new CodeInstruction(opcode: OpCodes.Call, operand: typeof(ModEntry).GetMethod("GetIsMultiplayerForShouldTimePass"));
                        instructionList[i].labels = c.labels;
                    }
                }

                return instructionList.AsEnumerable();
            }
        }
    }
}
