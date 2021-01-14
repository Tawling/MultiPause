using Harmony;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
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

        public PerScreen<bool> IsPaused = new PerScreen<bool>(() => false);
        public PerScreen<bool> QueryMessageSent = new PerScreen<bool>(() => false);
        public PerScreen<string> PauseMode { get; set; } = new PerScreen<string>(() => string.Empty);
        public PerScreen<bool> InitialPauseState => new PerScreen<bool>(() => PauseMode.Value != PAUSE_ANY);
        private PerScreen<int> prevGameTimeInterval = new PerScreen<int>(() => -1);
        private PerScreen<TimePassState> prevTimePassState = new PerScreen<TimePassState>(() => TimePassState.Pass);
        private PerScreen<bool> patched = new PerScreen<bool>(() => false);

        public static PerScreen<Config> Config { get; private set; } = new PerScreen<Config>(() => new Config());
        private static PerScreen<Dictionary<long, PlayerState>> PlayerStates { get; set; } = new PerScreen<Dictionary<long, PlayerState>>(() => new Dictionary<long, PlayerState>());



        public static PerScreen<bool> ForceSinglePlayerCheck = new PerScreen<bool>(() => false);

        /**********
        ** Public methods
        **********/
        public override void Entry(IModHelper helper)
        {
            Config.Value = helper.ReadConfig<Config>();

            PauseMode.Value = Config.Value.PauseMode_ANY_ALL_AUTO.ToUpper();


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
            if (patched.Value) return;
            Monitor.Log("Patching StardewValley.Game1 - Boolean shouldTimePass()", LogLevel.Debug);
            var harmony = HarmonyInstance.Create("taw.multipause");
            var original = typeof(Game1).GetMethod("shouldTimePass");
            var prefix = typeof(ShouldTimePassPatch).GetMethod("Prefix");
            var transpiler = typeof(ShouldTimePassPatch).GetMethod("Transpiler");
            harmony.Patch(original, prefix: new HarmonyMethod(prefix), transpiler: new HarmonyMethod(transpiler));
            patched.Value = true;
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

            PlayerStates.Value = new Dictionary<long, PlayerState>();
            var state = GetPlayerState(Game1.player.UniqueMultiplayerID);
            state.IsHost = Context.IsMainPlayer;
            state.ConfigPauseMode = PauseMode.Value;
        }

        public void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            foreach (KeyValuePair<long, PlayerState> entry in PlayerStates.Value)
            {
                entry.Value.TotalTimePaused = 0;
            }
        }

        public void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            QueryMessageSent.Value = false;
        }

        public void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (Context.IsWorldReady && Game1.IsMultiplayer)
            {
                // Update states if host player
                foreach (KeyValuePair<long, PlayerState> entry in PlayerStates.Value)
                {
                    if (entry.Value.IsPaused || !entry.Value.IsOnline)
                    {
                        entry.Value.TotalTimePaused++;
                    }
                }

                // Retrieve state as if in single player
                ForceSinglePlayerCheck.Value = true;
                var isPaused = !Game1.shouldTimePass();
                ForceSinglePlayerCheck.Value = false;

                // Check player's pause state and broadcast changes
                if (isPaused != IsPaused.Value)
                {
                    IsPaused.Value = isPaused;
                    var state = GetPlayerState(Game1.player.UniqueMultiplayerID);
                    state.IsPaused = isPaused;
                    state.Ticks = Game1.ticks;

                    if (PauseMode.Value != Config.Value.PauseMode_ANY_ALL_AUTO.ToUpper())
                    {
                        state.ConfigPauseMode = Config.Value.PauseMode_ANY_ALL_AUTO.ToUpper();
                        if (Context.IsMainPlayer)
                            PauseMode.Value = state.ConfigPauseMode;
                    }

                    this.Helper.Multiplayer.SendMessage(state, STPMessage.Update, modIDs: new[] { this.ModManifest.UniqueID });
                }

                TimePassState timePassState = GetTimePassState();
                if (timePassState != TimePassState.Pass)
                {
                    Game1.gameTimeInterval = Game1.gameTimeInterval < prevGameTimeInterval.Value ? 0 : prevGameTimeInterval.Value;
                }
                else
                {
                    prevGameTimeInterval.Value = Game1.gameTimeInterval;
                }

                if (timePassState != prevTimePassState.Value)
                {
                    prevTimePassState.Value = timePassState;
                    Monitor.Log($"Time is now {(timePassState == TimePassState.Pass ? "passing normally" : (timePassState == TimePassState.Freeze ? "frozen" : "paused"))}.", LogLevel.Info);
                    foreach (var state in PlayerStates.Value)
                    {
                        Monitor.Log($"{state.Key} -- {state.Value}");
                    }
                }

                // Send query message if needed
                if (!QueryMessageSent.Value)
                {
                    this.Helper.Multiplayer.SendMessage(true, STPMessage.Query, modIDs: new[] { this.ModManifest.UniqueID });
                    QueryMessageSent.Value = true;
                }
            }
            else
            {
                QueryMessageSent.Value = false;
            }
        }

        public void OnPeerContextReceived(object sender, PeerContextReceivedEventArgs e)
        {
            var state = GetPlayerState(e.Peer.PlayerID);

            state.IsOnline = true;
            state.IsPaused = InitialPauseState.Value;
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
                if (state.IsOnline && messageState.Ticks >= state.Ticks)
                {
                    state.Ticks = messageState.Ticks;
                    state.IsPaused = messageState.IsPaused;
                    state.IsHost = messageState.IsHost;
                    state.ConfigPauseMode = messageState.ConfigPauseMode;
                    if (state.IsHost && PauseMode.Value != state.ConfigPauseMode)
                        PauseMode.Value = state.ConfigPauseMode;
                }
            }
            else if (e.Type == STPMessage.Query && e.FromModID == this.ModManifest.UniqueID)
            {
                var state = GetPlayerState(Game1.player.UniqueMultiplayerID);
                this.Helper.Multiplayer.SendMessage(state, STPMessage.Update, modIDs: new[] { this.ModManifest.UniqueID });
                // Host needs to send all player states so clients can synchronize values such as TotalTimePaused
                if (Context.IsMainPlayer)
                {
                    this.Helper.Multiplayer.SendMessage(PlayerStates.Value, STPMessage.AllPlayerStates, modIDs: new[] { this.ModManifest.UniqueID });
                }
            }
            else if (e.Type == STPMessage.AllPlayerStates && e.FromModID == this.ModManifest.UniqueID)
            {
                var message = e.ReadAs<Dictionary<long, PlayerState>>();
                foreach (var item in message)
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
            Config.Value = Helper.ReadConfig<Config>();
        }

        PlayerState GetPlayerState(long playerID)
        {
            PlayerStates.Value.TryGetValue(playerID, out PlayerState dictState);
            if (dictState == null)
            {
                dictState = new PlayerState(InitialPauseState.Value, 0, totalTimePaused: GetMinTimePaused());
                PlayerStates.Value.Add(playerID, dictState);
            }
            return dictState;
        }

        /**********
        ** Static methods
        **********/

        public static bool GetIsMultiplayerForShouldTimePass()
        {
            return ForceSinglePlayerCheck.Value ? false : Game1.IsMultiplayer;
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

            foreach (KeyValuePair<long, PlayerState> entry in PlayerStates.Value)
            {
                min = Math.Min(min, entry.Value.TotalTimePaused);
            }

            return Math.Max(min, 0);
        }

        public static TimePassState GetTimePassState()
        {
            bool freeze = false;
            bool allPaused = true;
            if (Config.Value.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_ALL)
            {
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.Value.TryGetValue(farmer.UniqueMultiplayerID, out PlayerState state);
                    if (state != null && state.IsOnline && !state.IsPaused)
                    {
                        allPaused = false;
                    }
                }
                freeze = allPaused;
            }
            else if (Config.Value.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_ANY)
            {
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.Value.TryGetValue(farmer.UniqueMultiplayerID, out PlayerState state);
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
            else if (Config.Value.PauseMode_ANY_ALL_AUTO.ToUpper() == PAUSE_AUTO)
            {
                int min = Int32.MaxValue;
                foreach (Farmer farmer in Game1.getOnlineFarmers())
                {
                    PlayerStates.Value.TryGetValue(farmer.UniqueMultiplayerID, out PlayerState state);
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
                if (Game1.IsMultiplayer && !ForceSinglePlayerCheck.Value)
                {
                    __result = GetTimePassState() != TimePassState.Pause;
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