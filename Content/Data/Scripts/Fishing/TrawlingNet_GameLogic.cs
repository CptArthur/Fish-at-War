using Jakaria.API;
using PEPCO.Utilities;
using ProtoBuf;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using Sandbox.ModAPI.Interfaces.Terminal;
using SISK.LoadLocalization;
using SpaceEngineers.Game.ModAPI;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Serialization;
using VRage;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Library.Utils;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Serialization;
using VRage.Utils;
using VRageMath;
using static PEPCO.ScriptHelpers;

namespace PEPCO
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_FunctionalBlock), false, "AQD_LG_TrawlingNet")]
    public class FishCollectorComponent : MyGameLogicComponent
    {
        private AaWFood_Session Session => AaWFood_Session.Instance;

        public IMyTerminalBlock Block { get; private set; }
        private IMyFunctionalBlock _fishCollector;
        private int counter = 0;
        
        private Random _random;

        private const double SpeedLowSq = 10 * 10;    // 100
        private const double SpeedMidSq = 15 * 15;  // 225
        private const double SpeedHighSq = 25 * 25; // 625

        public readonly TrawlingNetSettings Settings = new TrawlingNetSettings();
        public readonly TrawlingNetContent Content = new TrawlingNetContent();
        private int _settingsSyncCountdown;
        // private int _netContentSyncCountdown; // Removed as we now sync net content immediately when it changes to minimize desync issues, and we don't want to wait for a sync as with the settings.
        public const int SETTINGS_CHANGED_COUNTDOWN = (60 * 1) / 10;

        private const float MAX_NET_CONTENT = 1562f; // 10 l per fish at 15.625 volume -> will be set dynamically in the future when we have more fish types

        private static readonly Dictionary<string, MyPhysicalItemDefinition> _cache =
            new Dictionary<string, MyPhysicalItemDefinition>();

        private static readonly MyObjectBuilderType ConsumableType = typeof(MyObjectBuilder_ConsumableItem);

        // Fish catch random bounds
        private const int CATCH_MIN = 1;
        private const int CATCH_MAX = 55;
        private const int ESCAPE_MIN = 1;
        private const int ESCAPE_MAX = 10;

        private int _deckFishVisualState = 0; // 0-4 for the 5 visual states of the fish deck subpart, which will change based on how full the net is. For now we will just set it to 0 and not implement the visual states until later when we have more fish types and a better idea of how we want to visualize them.
        private int _deckFishVisibleTicks = 0; // Counter to keep the fish on deck visible for a short time after hauling in the net, to give the player some visual feedback of what they caught before it disappears. Will be reset to a certain value (e.g. 100 ticks = ~1.5 seconds) whenever we haul in the net, and will count down every tick in UpdateSubpartVisibility, hiding the fish deck subpart again when it reaches 0.

        private const string SUBPART_NAME_NET = "subpart_net";
        private const string SUBPART_NAME_FISH_DECK = "fish";
        private static readonly string[] SUBPART_NAME_FISH_DECK_FISH = { "fish_1", "fish_2", "fish_3", "fish_4", "fish_5" }; // Example subpart names for different fish deck visual states, will need to be updated based on the actual models
        private List<string> _currentVisibleFish = new List<string>();
        private IMyEntity _spawnedNetVisual;
        private Matrix _spawnedNetLocalMatrix;

        private readonly Dictionary<string, MyEntitySubpart> _cachedSubparts = new Dictionary<string, MyEntitySubpart>();

        public bool EnableFishing
        {
            get { return Settings.EnableFishing; }
            set
            {
                if (Settings.EnableFishing == value) return;

                Settings.EnableFishing = value;

                // Trigger visibility change immediately when toggled
                SetSubpartVisibility(SUBPART_NAME_NET, value, Block as IMyEntity);
                if (!value)
                {
                    // If we're hauling in the net, we want to transfer the content to inventory immediately, so we trigger a net content sync with the emptyNet flag set to true, which will cause the inventory transfer
                    LogDebug($"AQD_LG_TrawlingNet: EnableFishing set to false, calling SyncNetContent(emptyNet:true); NetContent={NetContent}; entId={Entity?.EntityId}");
                    SyncNetContent(emptyNet: true);
                }

                SettingsChanged();
            }
        }

        public float NetContent
        {
            get { return Content.NetContent; }
            set
            {
                if (Content.NetContent == value) return;
                Content.NetContent = MathHelper.Clamp(value, 0, MAX_NET_CONTENT);
                NetContentChanged();
            }
        }

        public string NetContentSubtypeId
        {
            get { return Content.NetContentSubtypeId; }
            set
            {
                if (Content.NetContentSubtypeId == value) return;
                Content.NetContentSubtypeId = value;
                NetContentChanged();
            }
        }

        public bool IsInFishLocation // Cached value to avoid repeated checks in the UI
        {
            get { return Content.IsInFishLocation; }
            set
            {
                if (Content.IsInFishLocation == value) return;
                Content.IsInFishLocation = value;
                NetContentChanged();
            }
        }

        public float LastSpeedSq
        {
            get { return Content.LastSpeedSq; }
            set 
            { 
             if (Content.LastSpeedSq == value) return;
                Content.LastSpeedSq = value;
                NetContentChanged();
            }
        }

        public float LastCaught
        {
            get { return Content.LastCaught; }
            set
            {
                if (Content.LastCaught == value) return;
                Content.LastCaught = value;
                NetContentChanged();
            }
        }


        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            try
            {
                Block = (IMyTerminalBlock)Entity;
                if (Block == null) return;

                _fishCollector = Entity as IMyFunctionalBlock;
                NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in Init!\n{e}"); }
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                base.UpdateOnceBeforeFrame();

                if (Block?.CubeGrid?.Physics == null) return;

                _random = new Random();
                Settings.EnableFishing = false;
                NetContent = 0f;

                LoadSettings();
                SaveSettings();
                LoadNetContent();
                SaveNetContent();

                TrawlingNet_TerminalControls.DoOnce(ModContext);

                Block.AppendingCustomInfo += AppendCustomInfo;




                //NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
                NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
                if (MyAPIGateway.Session.IsServer)
                {
                    NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateOnceBeforeFrame!\n{e}"); }
        }

        /// <summary>
        /// Main update loop running every simulation frame.
        /// Distributes tasks across different frames to prevent performance spikes (Load Balancing).
        /// </summary>
        public override void UpdateAfterSimulation()
        {
            try
            {
                if (!IsDedicatedServer())
                {
                    // Update the net visual every tick
                    UpdateNetMatrixManual();
                }

                counter = (counter + 1) % 600;

                // --- HIGH FREQUENCY ---
                if (counter % 2 == 0) UpdateSubpartVisibility();

                // --- MID FREQUENCY (Staggered) ---
                if (counter % 10 == 1) SyncSettings();           // Frame 1, 11...
                if (counter % 10 == 2) CheckFunctionalSafety();  // Frame 2, 12... (The "Function Check")

                // NEW: Runs after function check, before UI and Fishing Tick
                if (counter % 10 == 3) UpdateLocationStatus();   // Frame 3, 13...

                if (counter % 10 == 4) UpdateTerminalUI();       // Frame 4, 14... (The "UI Update")

                // --- LOW FREQUENCY ---
                if (counter % 600 == 0) RunMainFishingTick();    // The "DoFishingTick"
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateAfterSimulation!\n{e}"); }
            
        }

        private void UpdateNetMatrixManual()
        {
            // 1. Handle Cleanup & Early Exit
            if (Block == null || Block.Closed || !Block.IsFunctional)
            {
                if (_spawnedNetVisual != null)
                {
                    _spawnedNetVisual.Close();
                    _spawnedNetVisual = null;
                }
                return;
            }

            // 2. Ensure Visual Exists (Initialization)
            if (_spawnedNetVisual == null)
            {
                string pathToModel;
                const string MODEL_PATH = "Models\\Cubes\\large\\AQD_LG_TrawlingNet_Subpart_Net.mwm";

                if (GetFullModPathSafe(MODEL_PATH, ModContext, out pathToModel))
                {
                    LogDebug($"AQD_LG_TrawlingNet: Model path resolved: {pathToModel}");
                    _spawnedNetVisual = AddModelToDummy(Block as IMyEntity, SUBPART_NAME_NET, pathToModel);
                }
                else
                {
                    LogError($"AQD_LG_TrawlingNet: Failed to resolve path: {MODEL_PATH}");
                    return; // Exit if we failed to create it
                }
            }

            // update net entity visual
            if (EnableFishing != _spawnedNetVisual.Render.Visible)
            {
                _spawnedNetVisual.Render.Visible = EnableFishing;
            }

            // Use Multiply for clarity; MatrixD * MatrixD is fine in VRage
            _spawnedNetVisual.WorldMatrix = _spawnedNetLocalMatrix * Block.WorldMatrix;
        }

        public override void OnRemovedFromScene()
        {
            base.OnRemovedFromScene();
            if (_spawnedNetVisual != null)
            {
                _spawnedNetVisual.Close();
                _spawnedNetVisual = null;
            }
        }

        /// <summary>
        /// Updates visual subparts. Frequency: Every 2 ticks.
        /// Note: On tick 0, this runs alongside the 10-second fishing tick.
        /// </summary>
        public void UpdateSubpartVisibility()
        {
            try
            {
                if (MyAPIGateway.Utilities.IsDedicated) return;

                // Keep managing the net based on fishing state
                //SetSubpartVisibility(SUBPART_NAME_NET, EnableFishing, Block as IMyEntity);

                // We no longer toggle SUBPART_NAME_FISH_DECK ("fish") here.
                // Instead, we let the child manager handle everything.
                UpdateFishDeckVisualState(_deckFishVisualState);
            }
            catch (Exception e) { LogError($"Error in Visibility Update!\n{e}"); }
        }



        private IMyEntity AddModelToDummy(IMyEntity parent, string dummyName, string modelPath)
        {
            // 1. Find the dummy
            Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
            parent.Model.GetDummies(dummies);

            IMyModelDummy targetDummy;
            if (!dummies.TryGetValue(dummyName, out targetDummy))
                return null;

            // 2. Spawn the entity as a standalone object
            MyEntity childEntity = new MyEntity();
            // Use null for the parent to keep it independent of engine parenting logic
            childEntity.Init(null, modelPath, null, null, null);

            IMyEntity childInterface = childEntity as IMyEntity;
            childInterface.Render.Visible = true;
            childInterface.Save = false;

            // We only need the base NeedsDraw flag; we handle the position ourselves
            childInterface.Flags |= EntityFlags.NeedsDraw;
            childInterface.PersistentFlags |= MyPersistentEntityFlags2.InScene;

            // 3. Store the Dummy's matrix as our fixed local offset
            _spawnedNetLocalMatrix = targetDummy.Matrix;

            // 4. Register with the engine
            MyEntities.Add(childEntity);
            _spawnedNetVisual = childInterface;

            return childInterface;
        }


        /// <summary>
        /// Manages the visual representation of fish on the deck. 
        /// Shuffles and populates fish subparts based on net content, then randomly 
        /// despawns them one by one over time until the deck is clear.
        /// </summary>
        private void UpdateFishDeckVisualState(int visualState)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            MyEntitySubpart fishOnDeckSubpart;
            // If we can't find the parent subpart yet, we can't do anything.
            if (!_cachedSubparts.TryGetValue(SUBPART_NAME_FISH_DECK, out fishOnDeckSubpart))
            {
                // Try to cache it for the next tick
                Block.TryGetSubpart(SUBPART_NAME_FISH_DECK, out fishOnDeckSubpart);
                if (fishOnDeckSubpart != null) _cachedSubparts[SUBPART_NAME_FISH_DECK] = fishOnDeckSubpart;
                else return;
            }

            IMyEntity subpartEntity = fishOnDeckSubpart as IMyEntity;

            // --- CASE 1: IDLE / STARTUP CLEANUP ---
            // Because _deckFishVisualState starts at 0 and _currentVisibleFish is empty,
            // this block runs immediately on load to force-hide any subparts that default to visible.
            if (visualState <= 0 && _currentVisibleFish.Count == 0)
            {
                foreach (var name in SUBPART_NAME_FISH_DECK_FISH)
                {
                    SetSubpartVisibility(name, false, subpartEntity);
                }
                _deckFishVisibleTicks = 0;
                return;
            }

            // --- CASE 2: INITIALIZATION (Spawning Fish) ---
            if (_currentVisibleFish.Count == 0 && visualState > 0)
            {
                _deckFishVisibleTicks = 0;

                // Force-hide all before picking new ones (The "Clean Slate" fix)
                foreach (var name in SUBPART_NAME_FISH_DECK_FISH)
                {
                    SetSubpartVisibility(name, false, subpartEntity);
                }

                var pool = new List<string>(SUBPART_NAME_FISH_DECK_FISH);

                // Fisher-Yates shuffle
                int n = pool.Count;
                while (n > 1)
                {
                    n--;
                    int k = _random.Next(n + 1);
                    string value = pool[k];
                    pool[k] = pool[n];
                    pool[n] = value;
                }

                int fishToShow = MathHelper.Clamp(visualState, 0, pool.Count);
                for (int i = 0; i < fishToShow; i++)
                {
                    SetSubpartVisibility(pool[i], true, subpartEntity);
                    _currentVisibleFish.Add(pool[i]);
                }
                LogDebug($"AQD_LG_TrawlingNet: Visuals Initialized. State: {visualState}, Fish Spawned: {_currentVisibleFish.Count}");
            }

            // --- CASE 3: DESPAWN ANIMATION ---
            if (_currentVisibleFish.Count > 0)
            {
                _deckFishVisibleTicks++;
                if (_deckFishVisibleTicks >= 102)
                {
                    int randomIndex = _random.Next(0, _currentVisibleFish.Count);
                    string fishToHide = _currentVisibleFish[randomIndex];

                    SetSubpartVisibility(fishToHide, false, subpartEntity);
                    _currentVisibleFish.RemoveAt(randomIndex);

                    if (_deckFishVisualState > 0) _deckFishVisualState--;
                    _deckFishVisibleTicks = 0;

                    LogDebug($"AQD_LG_TrawlingNet: Random Despawn: {fishToHide}. Remaining: {_currentVisibleFish.Count}");
                }
            }
        }


        /// <summary>
        /// Validates if the block is still functional. Frequency: Every 10 ticks (Offset 2).
        /// Runs exactly 1 frame after SyncSettings.
        /// </summary>
        public void CheckFunctionalSafety()
        {
            if (EnableFishing && !_fishCollector.IsFunctional)
            {
                NetContent = 0;
                EnableFishing = false;
            }
        }

        /// <summary>
        /// Checks if the net is currently in a valid fishing zone.
        /// Frequency: Every 10 ticks.
        /// </summary>
        public void UpdateLocationStatus()
        {
            try
            {
                if (_fishCollector == null) return;

                Vector3D worldPosition = _fishCollector.PositionComp.GetPosition();

                // Check if we are within the general Agaris region (60km radius)
                if ((worldPosition - MapUtilities.agarisCenter).LengthSquared() > (60000d * 60000d))
                {
                    IsInFishLocation = false;
                    return;
                }

                // Perform the detailed location check from MapUtilities
                IsInFishLocation = MapUtilities.IsAtFishLocation(worldPosition);
            }
            catch (Exception e)
            {
                LogError($"AQD_LG_TrawlingNet: Error in UpdateLocationStatus!\n{e}");
            }
        }

        /// <summary>
        /// Refreshes the Control Panel UI if the player is looking at it. 
        /// Frequency: Every 10 ticks (Offset 3). Runs exactly 1 frame after Safety Check.
        /// </summary>
        public void UpdateTerminalUI()
        {
            if (MyAPIGateway.Gui.GetCurrentScreen == MyTerminalPageEnum.ControlPanel)
            {
                Block.RefreshCustomInfo();
                Block.SetDetailedInfoDirty();
            }
        }

        /// <summary>
        /// The heavy lifting fishing logic. Frequency: Every 600 ticks (approx 10 seconds).
        /// </summary>
        private void RunMainFishingTick()
        {
            try
            {
                // 1. Requirement Check
                if (!_fishCollector.HasInventory || !_fishCollector.IsWorking || !EnableFishing)
                {
                    LastCaught = 0f;
                    return;
                }

                // 2. Logic uses the value already updated by UpdateLocationStatus()
                if (IsInFishLocation)
                {
                    Vector3D worldPosition = _fishCollector.PositionComp.GetPosition();
                    var Agaris = MyGamePruningStructure.GetClosestPlanet(worldPosition);
                    var surfacePoint = WaterAPI.GetClosestSurfacePoint(worldPosition, Agaris);
                    double distToSurfaceSq = (surfacePoint - worldPosition).LengthSquared();

                    // Only fish if near the surface
                    if (distToSurfaceSq < (50d * 50d))
                    {
                        float? actualVelocity = _fishCollector.CubeGrid?.LinearVelocity.LengthSquared();
                        if (actualVelocity == null) return;

                        float speedSq = actualVelocity.Value;
                        LastSpeedSq = speedSq;

                        float efficiency = GetFishEfficiencySquared(speedSq);

                        // --- Box-Muller Standard Distribution Implementation ---
                        // We use two uniform random numbers [0, 1) to generate a normal distribution
                        double u1 = 1.0 - _random.NextDouble(); // 1.0 - [0,1) to ensure we don't get 0 for the Log
                        double u2 = 1.0 - _random.NextDouble();

                        // Standard Normal Distribution (Mean = 0, Stdev = 1)
                        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

                        // Calculate mean and stdev to fit your CATCH_MIN and CATCH_MAX
                        // We assume CATCH_MAX is roughly 3 standard deviations from the mean
                        float mean = (CATCH_MIN + CATCH_MAX) / 2.0f;
                        float stdDev = (CATCH_MAX - CATCH_MIN) / 6.0f;

                        // Shift and scale the normal distribution, then clamp to your min/max bounds
                        float amount = (float)(mean + stdDev * randStdNormal);
                        amount = MathHelper.Clamp(amount, CATCH_MIN, CATCH_MAX);
                        // -------------------------------------------------------

                        LastCaught = amount * efficiency;
                        NetContent += LastCaught;
                        NetContentSubtypeId = "Fish";

                        WaterAPI.CreateBubble(worldPosition, 2);
                    }
                }
                else
                {
                    // If not in a fish location, fish escape the net
                    int escaped = _random.Next(ESCAPE_MIN, ESCAPE_MAX);
                    NetContent = Math.Max(0, NetContent - escaped);
                    LastSpeedSq = 0f;
                    LastCaught = 0f;
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in DoFishingTick!\n{e}"); }
        }

        /// <summary>
        /// Finds a named subpart and sets its visibility. Caches the result to avoid repeated lookups.
        /// Note: Null entries are NOT cached, so subparts that appear late (e.g. after a grid paste) will still be found.
        /// </summary>
        private void SetSubpartVisibility(string subpartName, bool visible, IMyEntity myEntity )
        {
            try
            {
                if (MyAPIGateway.Utilities.IsDedicated)
                    return;

                // Debug log to track subpart visibility changes and lookups
                LogDebug($"AQD_LG_TrawlingNet: SetSubpartVisibility called for subpart '{subpartName}' with visible={visible}; entId={Entity?.EntityId}");

                // Try to find the subpart once if we haven't already
                MyEntitySubpart subpart;
                bool inCache = _cachedSubparts.TryGetValue(subpartName, out subpart);

                if (!inCache || subpart == null || subpart.Closed)
                {
                    myEntity.TryGetSubpart(subpartName, out subpart);

                    if (subpart != null)
                    {
                        // Only cache on success so we keep retrying if the subpart isn't ready yet
                        _cachedSubparts[subpartName] = subpart;
                        LogDebug($"AQD_LG_TrawlingNet: SetSubpartVisibility: cached subpart '{subpartName}'; entId={Entity?.EntityId}");
                    }
                    else
                    {
                        LogDebug($"AQD_LG_TrawlingNet: SetSubpartVisibility: subpart '{subpartName}' not found, will retry next tick; entId={Entity?.EntityId}");
                        return;
                    }
                }

                //Entity.TryGetSubpart(subpartName, out subpart);

                // If we found it (now or previously), update visibility
                if (subpart != null && subpart.Render.Visible != visible)
                {
                    subpart.Render.Visible = visible;
                    LogDebug($"AQD_LG_TrawlingNet: SetSubpartVisibility: '{subpartName}' visibility set to {visible}; entId={Entity?.EntityId}");
                }
            }
            catch (Exception e)
            {
                LogError($"AQD_LG_TrawlingNet: Error in SetSubpartVisibility (subpart={subpartName})!\n{e}");
            }
        }


        public float GetFishEfficiencySquared(float speedSq)
        {
            if (speedSq <= 0 || speedSq >= SpeedHighSq) return 0;
            if (speedSq >= SpeedLowSq && speedSq <= SpeedMidSq) return 1;
            if (speedSq < SpeedLowSq) return (float)(speedSq / SpeedLowSq);

            float currentSpeed = (float)Math.Sqrt(speedSq);
            float efficiency = 1.0f - (currentSpeed - 15.0f) / 10.0f;
            return MathHelper.Clamp(efficiency, 0f, 1f);
        }

        private void TransferNetContentToInventory()
        {
            try
            {
                LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory called; NetContent={NetContent}; SubtypeId={Content.NetContentSubtypeId}; entId={Entity?.EntityId}");

                // Use a small epsilon to catch floating point remnants
                if (NetContent < 0.01f)
                {
                    LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: NetContent below threshold, skipping; entId={Entity?.EntityId}");
                    return;
                }

                // Calculate content percentage based on what was in the net BEFORE resetting it
                float contentPercentage = (NetContent / MAX_NET_CONTENT) * 100f;

                // 0% = 0, 1-20% = 1, 21-40% = 2, 41-60% = 3, 61-80% = 4, 81-100% = 5
                int newState = (int)Math.Ceiling(contentPercentage / 20f);
                _deckFishVisualState = MathHelper.Clamp(newState, 0, SUBPART_NAME_FISH_DECK_FISH.Length);

                // Log it so you can see the math in the debugger
                LogDebug($"AQD_LG_TrawlingNet: Visual State calculated: {_deckFishVisualState} ({contentPercentage:F1}%)");

                IMyInventory inventory = _fishCollector.GetInventory();
                if (inventory == null)
                {
                    LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: inventory is null, skipping; entId={Entity?.EntityId}");
                    return;
                }

                // Ensure we are using the internal Subtype name, not a UI string
                var itemDefinition = GetDefinition(Content.NetContentSubtypeId);
                if (itemDefinition == null)
                {
                    LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: itemDefinition not found for SubtypeId='{Content.NetContentSubtypeId}', skipping; entId={Entity?.EntityId}");
                    return;
                }
                LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: itemDefinition found; Id={itemDefinition.Id}; Volume={itemDefinition.Volume}; entId={Entity?.EntityId}");

                float volumePerFish = itemDefinition.Volume;

                // Safety check for framework: prevent division by zero
                if (volumePerFish <= 0) volumePerFish = 0.01f;

                float availableVolume = (float)(inventory.MaxVolume - inventory.CurrentVolume);
                int roomForFish = (int)Math.Floor(availableVolume / volumePerFish);
                int fishToMove = Math.Min((int)Math.Round(NetContent), roomForFish);
                LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: availableVolume={availableVolume:F3}; roomForFish={roomForFish}; fishToMove={fishToMove}; entId={Entity?.EntityId}");

                if (fishToMove > 0)
                {
                    var content = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(itemDefinition.Id);
                    inventory.AddItems((MyFixedPoint)fishToMove, content);
                    LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: added {fishToMove} fish to inventory; entId={Entity?.EntityId}");
                }
                else
                {
                    LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: fishToMove=0, nothing added; entId={Entity?.EntityId}");
                }

                NetContent = 0f;
                LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: NetContent reset to 0; entId={Entity?.EntityId}");
            }
            catch (Exception e)
            {
                LogError($"AQD_LG_TrawlingNet: Error in TransferNetContentToInventory!\n{e}");
            }
        }

        public static MyPhysicalItemDefinition GetDefinition(string subtypeName)
        {
            if (string.IsNullOrEmpty(subtypeName)) return null;

            MyPhysicalItemDefinition definition;
            
            if (!_cache.TryGetValue(subtypeName, out definition)) // Already in the cache?
            {
                // Create the ID and fetch from SE
                var id = new MyDefinitionId(ConsumableType, subtypeName);
                definition = MyDefinitionManager.Static.GetPhysicalItemDefinition(id);

                // Store it (even if null, to avoid repeated failed lookups) -> AI can be smart!
                _cache[subtypeName] = definition;
            }

            return definition;
        }

        public static string GetDisplayName(string subtypeName)
        {
            var definition = GetDefinition(subtypeName);
            return definition?.DisplayNameText;
        }

        void AppendCustomInfo(IMyTerminalBlock block, StringBuilder info)
        {
            try
            {
                string spinner = GetSpinner();
                // Calculate content percentage
                float contentPercentage = (NetContent / MAX_NET_CONTENT) * 100f;

                info.AppendLine($"--- Trawling Status {spinner} ---"); // Spinner in the header
                info.AppendLine($"Net Content: {contentPercentage.ToString("00.00")}%");
                if (!_fishCollector.IsFunctional)
                {
                    info.AppendLine("Status: Net damaged");
                    return;
                }
                else if (!_fishCollector.IsWorking)
                {
                    info.AppendLine("Status: Net unpowered");
                    return;
                }
                if (EnableFishing)
                {
                    if (IsInFishLocation)
                    {
                        info.AppendLine("Status: Net set");
                        info.AppendLine($"Current catch: {GetDisplayName(Content.NetContentSubtypeId) ?? Content.NetContentSubtypeId}");
                        string speedHint = string.Empty;
                        var _lastEfficiency = GetFishEfficiencySquared(LastSpeedSq);
                        if (_lastEfficiency < 1f)
                        {
                            speedHint = LastSpeedSq < SpeedLowSq ? " (speed too low)" : " (speed too high)";
                        }
                        info.AppendLine($"Efficiency: {(_lastEfficiency * 100):F0}%{speedHint}");
                        if (ModParameter.IsDebug()) info.AppendLine($"Last Catch: {LastCaught:F0} fish");
                    }
                    else
                    {
                        info.AppendLine("Status: No fish in this area.");
                    }
                }
                else
                {
                    info.AppendLine("Status: Net hauled in");
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in AppendCustomInfo!\n{e}"); }
        }

        private static readonly string[] SpinnerFrames = {
            "|....",
            ".|...",
            "..|..",
            "...|.",
            "....|",
            "...|.",
            "..|..",
            ".|..."
        };

        private string GetSpinner()
        {
            // Cycle every 10 ticks (approx 6 times a second)
            int frame = (MyAPIGateway.Session.GameplayFrameCounter / 10) % SpinnerFrames.Length;
            return SpinnerFrames[frame];
        }

        // --- Data Persistence and Sync (Kept Intact) ---

        public void UpdateSettingsFromInput(TrawlingNetSettings loadedSettingssettings)
        {
            try
            {
                if (Settings != loadedSettingssettings)
                    Settings.EnableFishing = loadedSettingssettings.EnableFishing;
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateSettingsFromInput!\n{e}"); }
        }

        /// <summary>
        /// Synchronizes net content across the network. 
        /// Creates a new content object for the packet to prevent modifying the block's persistent state.
        /// </summary>
        void SyncNetContent(bool emptyNet = false)
        {
            try
            {
                LogDebug($"AQD_LG_TrawlingNet: SyncNetContent sending; emptyNet={emptyNet}; NetContent={NetContent}; subtype={NetContentSubtypeId}");

                // BUG FIX: Instantiate a NEW object for the packet. 
                // Using the persistent 'Content' reference was causing the 'EmptyNet' flag to get stuck on True locally.
                var packetContent = new TrawlingNetContent
                {
                    NetContent = this.NetContent,
                    NetContentSubtypeId = this.NetContentSubtypeId,
                    IsInFishLocation = this.IsInFishLocation,
                    LastSpeedSq = this.LastSpeedSq,
                    LastCaught = this.LastCaught,
                    EmptyNet = emptyNet
                };

                SaveNetContent();
                Session.SendTrawlingNetContentPacketSetting(Block.EntityId, packetContent);
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in SyncNetContent!\n{e}"); }
        }

        public void UpdateNetContentFromInput(TrawlingNetContent loadedNetContent)
        {
            try
            {
                if (loadedNetContent == null) return;

                // BUG FIX: Only sync SubtypeId if the incoming one is valid.
                // This prevents 'Fish' being overwritten by an empty string during network jitter.
                if (!string.IsNullOrWhiteSpace(loadedNetContent.NetContentSubtypeId))
                {
                    Content.NetContentSubtypeId = loadedNetContent.NetContentSubtypeId;
                }

                Content.NetContent = MathHelper.Clamp(loadedNetContent.NetContent, 0, MAX_NET_CONTENT);
                Content.IsInFishLocation = loadedNetContent.IsInFishLocation;
                Content.LastSpeedSq = loadedNetContent.LastSpeedSq;
                Content.LastCaught = loadedNetContent.LastCaught;

                if (loadedNetContent.EmptyNet)
                {
                    LogDebug($"AQD_LG_TrawlingNet: Received EmptyNet flag, triggering transfer; entId={Entity?.EntityId}");
                    TransferNetContentToInventory();
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateNetContentFromInput!\n{e}"); }
        }

        void LoadNetContent()
        {
            if (Block.Storage == null) return;
            string rawData;
            if (!Block.Storage.TryGetValue(StorageKeys.FISHATWARNETCONTENT, out rawData)) return;
            try
            {
                var loadedNetContent = MyAPIGateway.Utilities.SerializeFromBinary<TrawlingNetContent>(Convert.FromBase64String(rawData));
                if (loadedNetContent != null)
                {
                    // BUG FIX: Load the entire content object instead of just the float value
                    Content.NetContent = loadedNetContent.NetContent;
                    Content.NetContentSubtypeId = loadedNetContent.NetContentSubtypeId;
                    Content.LastCaught = loadedNetContent.LastCaught;
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error loading net content!\n{e}"); }
        }

        void LoadSettings()
        {
            if (Block.Storage == null) return;
            string rawData;
            if (!Block.Storage.TryGetValue(StorageKeys.FISHATWARSETTINGS, out rawData)) return;
            try
            {
                var loadedSettings = MyAPIGateway.Utilities.SerializeFromBinary<TrawlingNetSettings>(Convert.FromBase64String(rawData));
                if (loadedSettings != null) UpdateSettingsFromInput(loadedSettings);
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error loading settings!\n{e}"); }
        }


        void SaveNetContent()
        {
            try
            {
                if (Block?.Storage == null) Block.Storage = new MyModStorageComponent();
                Block.Storage.SetValue(StorageKeys.FISHATWARNETCONTENT, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Content)));
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in SaveNetContent!\n{e}"); }
        }

        void SaveSettings()
        {
            try
            {
                if (Block?.Storage == null) Block.Storage = new MyModStorageComponent();
                Block.Storage.SetValue(StorageKeys.FISHATWARSETTINGS, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Settings)));
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in SaveSettings!\n{e}"); }
        }

        void SettingsChanged() { if (_settingsSyncCountdown == 0) _settingsSyncCountdown = SETTINGS_CHANGED_COUNTDOWN; }
        void NetContentChanged() { SyncNetContent(); } // Will keep it like this for now, not sure how to optimize it further without risking desync issues with the net content, which would be a bad player experience.

        void SyncSettings()
        {
            try
            {
                if (_settingsSyncCountdown > 0 && --_settingsSyncCountdown <= 0)
                {
                    SaveSettings();
                    Session.SendTrawlingNetSettingsPacketSetting(Block.EntityId, Settings);
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in SyncSettings!\n{e}"); }
        }

        public override bool IsSerialized()
        {
            try { SaveSettings(); SaveNetContent(); }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error saving settings!\n{e}"); }
            return base.IsSerialized();
        }
    }
}