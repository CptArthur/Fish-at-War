
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

        private const double SpeedLowSq = 10 * 10;
        private const double SpeedMidSq = 15 * 15;
        private const double SpeedHighSq = 25 * 25;

        public readonly TrawlingNetSettings Settings = new TrawlingNetSettings();
        public readonly TrawlingNetContent Content = new TrawlingNetContent();
        private int _settingsSyncCountdown;
        public const int SETTINGS_CHANGED_COUNTDOWN = (60 * 1) / 10;

        private const float MAX_NET_CONTENT = 1562f;

        private static readonly Dictionary<string, MyPhysicalItemDefinition> _cache =
            new Dictionary<string, MyPhysicalItemDefinition>();

        private static readonly MyObjectBuilderType ConsumableType = typeof(MyObjectBuilder_ConsumableItem);

        private const int CATCH_MIN = 1;
        private const int CATCH_MAX = 55;
        private const int ESCAPE_MIN = 1;
        private const int ESCAPE_MAX = 10;

        private int _deckFishVisualState = 0;
        private int _deckFishVisibleTicks = 0;

        private const string SUBPART_NAME_NET = "subpart_net";
        private const string SUBPART_NAME_FISH_DECK = "fish";
        private static readonly string[] SUBPART_NAME_FISH_DECK_FISH = { "fish_1", "fish_2", "fish_3", "fish_4", "fish_5" };
        private const string MODEL_PATH = @"Models\Cubes\large\AQD_LG_TrawlingNet_Subpart_Net.mwm";

        private List<string> _currentVisibleFish = new List<string>();

        // Refactor: keep strong ref to the subpart and interface
        private MyEntitySubpart _spawnedNetSubpart;
        private IMyEntity _spawnedNetVisual;
        private Matrix _spawnedNetLocalMatrix;

        // Used for the SFX and bubble effect when fishing
        private bool? lastEnableFishing = null;

        private readonly Dictionary<string, MyEntitySubpart> _cachedSubparts = new Dictionary<string, MyEntitySubpart>();

        public bool EnableFishing
        {
            get { return Settings.EnableFishing; }
            set
            {
                if (Settings.EnableFishing == value) return;

                Settings.EnableFishing = value;

                //SetSubpartVisibility(SUBPART_NAME_NET, value, Block as IMyEntity);
                if (!value)
                {
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

        public bool IsInFishLocation
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

                NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
                if (MyAPIGateway.Session.IsServer)
                {
                    NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateOnceBeforeFrame!\n{e}"); }
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if (!IsDedicatedServer())
                {
                    UpdateNetSubpart();
                }

                counter = (counter + 1) % 600;

                if (counter % 2 == 0) UpdateSubpartVisibility();

                if (counter % 10 == 1) SyncSettings();
                if (counter % 10 == 2) CheckFunctionalSafety();
                if (counter % 10 == 3) UpdateLocationStatus();
                if (counter % 10 == 4) UpdateTerminalUI();
                if (counter % 10 == 5) DoWaterSFX();

                if (counter % 600 == 0) RunMainFishingTick();
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateAfterSimulation!\n{e}"); }
        }

        private void UpdateNetSubpart()
        {
            // 1. Validation: If block is invalid, clean up and exit
            if (Block == null || Block.Closed || !Block.IsFunctional)
            {
                CloseSpawnedNet();
                return;
            }

            Block.NeedsWorldMatrix = true;

            // 2. Creation: Spawn the subpart if it doesn't exist or is closed
            if (_spawnedNetVisual == null || _spawnedNetSubpart == null || _spawnedNetSubpart.Closed)
            {
                string pathToModel;

                if (!TryGetFullModelPath(MODEL_PATH, ModContext, out pathToModel))
                    return;

                MyEntity parent = Block as MyEntity;
                if (parent == null)
                    return;

                // Find the dummy to determine local matrix position
                IMyModelDummy dummy = SafeGetDummy(SUBPART_NAME_NET, parent);
                if (dummy == null)
                    return;

                _spawnedNetLocalMatrix = dummy.Matrix;
                _spawnedNetSubpart = CreateRealSubpart(parent, pathToModel, SUBPART_NAME_NET, _spawnedNetLocalMatrix);
                _spawnedNetVisual = _spawnedNetSubpart as IMyEntity;
            }

            // 3. Update: Handle visibility and rendering
            if (_spawnedNetVisual != null)
            {
                // Sync visibility with fishing state
                if (_spawnedNetVisual.Render != null && EnableFishing != _spawnedNetVisual.Render.Visible)
                {
                    _spawnedNetVisual.Render.Visible = EnableFishing;
                }

                // Force render update to prevent "ghosting" or lag in subpart positioning
                try
                {
                    _spawnedNetSubpart?.Render?.UpdateRenderObject(true);
                }
                catch
                {
                    // Silent fail on render updates to prevent crash loops
                }
            }
        }

        private void CloseSpawnedNet()
        {
            try
            {
                if (_spawnedNetVisual != null)
                    _spawnedNetVisual.Close();
            }
            catch { }

            _spawnedNetVisual = null;
            _spawnedNetSubpart = null;
        }

        private MyEntitySubpart CreateRealSubpart(MyEntity parent, string modelPath, string subpartKey, Matrix localMatrix)
        {
            // Defensive checks first (helps avoid silent weirdness)
            if (parent == null)
            {
                LogDebug("CreateRealSubpart: parent is null");
                return null;
            }

            if (string.IsNullOrEmpty(modelPath))
            {
                LogDebug("CreateRealSubpart: modelPath is null/empty for parent=" + parent.EntityId);
                return null;
            }

            if (string.IsNullOrEmpty(subpartKey))
            {
                LogDebug("CreateRealSubpart: subpartKey is null/empty for parent=" + parent.EntityId);
                return null;
            }

            LogDebug("CreateRealSubpart: START parent=" + parent.EntityId + " modelPath=" + modelPath + " subpartKey=" + subpartKey);

            // 1) Close/remove existing subpart under same key (best effort)
            try
            {
                MyEntitySubpart existing;
                if (parent.Subparts != null && parent.Subparts.TryGetValue(subpartKey, out existing))
                {
                    if (existing != null && !existing.Closed)
                    {
                        LogDebug("CreateRealSubpart: existing subpart found, closing key=" + subpartKey + " id=" + existing.EntityId);
                        existing.Close();
                    }
                }
            }
            catch (Exception e)
            {
                LogDebug("CreateRealSubpart: exception while closing existing subpart: " + e.Message);
            }

            // 2) Normalize orientation basis from the provided local matrix (translation preserved)
            // This is NOT hardcoding rotation; it's just ensuring the dummy/offset matrix is a valid transform basis.
            // If localMatrix contains scale/skew, some SE builds behave oddly without this.
            try
            {
                Vector3 t = localMatrix.Translation;
                Matrix norm = Matrix.Normalize(localMatrix);
                norm.Translation = t;
                localMatrix = norm;
            }
            catch
            {
                // If Normalize is unavailable or throws, keep the original localMatrix.
            }

            // 3) Create and init as a REAL subpart (parent passed to Init)
            MyEntitySubpart child = new MyEntitySubpart();

            try
            {
                child.Init(null, modelPath, parent, null, null);
            }
            catch (Exception e)
            {
                LogDebug("CreateRealSubpart: child.Init failed: " + e);
                try { child.Close(); } catch { }
                return null;
            }

            // 4) Apply local matrix
            try
            {
                child.PositionComp.SetLocalMatrix(ref localMatrix, null, true);
            }
            catch (Exception e)
            {
                LogDebug("CreateRealSubpart: SetLocalMatrix failed: " + e.Message);
            }

            // 5) Register and add to scene
            try
            {
                if (parent.Subparts != null)
                    parent.Subparts[subpartKey] = child;
            }
            catch (Exception e)
            {
                LogDebug("CreateRealSubpart: failed to register in parent.Subparts: " + e.Message);
            }

            try
            {
                child.OnAddedToScene(parent);
            }
            catch (Exception e)
            {
                LogDebug("CreateRealSubpart: OnAddedToScene failed: " + e.Message);
            }

            // 6) Render defaults
            try
            {
                if (child.Render != null)
                    child.Render.Visible = true;
                child.Save = false;
            }
            catch { }

            // 7) Flags (OR, never AND)
            try
            {
                child.Flags |= (EntityFlags.NeedsDrawFromParent | EntityFlags.NeedsWorldMatrix);
            }
            catch { }

            // 8) Useful diagnostics (render parent id often explains "not visually connected" issues)
            try
            {
                uint childPid0 = 0;
                if (child.Render != null && child.Render.ParentIDs != null && child.Render.ParentIDs.Length > 0)
                    childPid0 = child.Render.ParentIDs[0];

                uint parentPid0 = 0;
                if (parent.Render != null && parent.Render.ParentIDs != null && parent.Render.ParentIDs.Length > 0)
                    parentPid0 = parent.Render.ParentIDs[0];

                LogDebug("CreateRealSubpart: END child=" + child.EntityId
                    + " child.Parent=" + (child.Parent == null ? "null" : child.Parent.EntityId.ToString())
                    + " child.Render.ParentIDs[0]=" + childPid0
                    + " parent.Render.ParentIDs[0]=" + parentPid0);
            }
            catch
            {
                LogDebug("CreateRealSubpart: END child=" + child.EntityId
                    + " child.Parent=" + (child.Parent == null ? "null" : child.Parent.EntityId.ToString()));
            }

            return child;
        }

        public override void OnRemovedFromScene()
        {
            base.OnRemovedFromScene();
            CloseSpawnedNet();
        }

        public void UpdateSubpartVisibility()
        {
            try
            {
                if (MyAPIGateway.Utilities.IsDedicated) return;
                UpdateFishDeckVisualState(_deckFishVisualState);
            }
            catch (Exception e) { LogError($"Error in Visibility Update!\n{e}"); }
        }

        private void UpdateFishDeckVisualState(int visualState)
        {
            if (MyAPIGateway.Utilities.IsDedicated) return;

            MyEntitySubpart fishOnDeckSubpart;
            if (!_cachedSubparts.TryGetValue(SUBPART_NAME_FISH_DECK, out fishOnDeckSubpart))
            {
                Block.TryGetSubpart(SUBPART_NAME_FISH_DECK, out fishOnDeckSubpart);
                if (fishOnDeckSubpart != null) _cachedSubparts[SUBPART_NAME_FISH_DECK] = fishOnDeckSubpart;
                else return;
            }

            IMyEntity subpartEntity = fishOnDeckSubpart as IMyEntity;

            if (visualState <= 0 && _currentVisibleFish.Count == 0)
            {
                foreach (var name in SUBPART_NAME_FISH_DECK_FISH)
                {
                    SetSubpartVisibility(name, false, subpartEntity);
                }
                _deckFishVisibleTicks = 0;
                return;
            }

            if (_currentVisibleFish.Count == 0 && visualState > 0)
            {
                _deckFishVisibleTicks = 0;

                foreach (var name in SUBPART_NAME_FISH_DECK_FISH)
                {
                    SetSubpartVisibility(name, false, subpartEntity);
                }

                var pool = new List<string>(SUBPART_NAME_FISH_DECK_FISH);

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

        public void CheckFunctionalSafety()
        {
            if (EnableFishing && !_fishCollector.IsFunctional)
            {
                NetContent = 0;
                EnableFishing = false;
            }
        }

        public void UpdateLocationStatus()
        {
            try
            {
                if (_fishCollector == null) return;

                Vector3D worldPosition = _fishCollector.PositionComp.GetPosition();

                if ((worldPosition - MapUtilities.agarisCenter).LengthSquared() > (60000d * 60000d))
                {
                    IsInFishLocation = false;
                    return;
                }

                IsInFishLocation = MapUtilities.IsAtFishLocation(worldPosition);
            }
            catch (Exception e)
            {
                LogError($"AQD_LG_TrawlingNet: Error in UpdateLocationStatus!\n{e}");
            }
        }

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

        private void SetSubpartVisibility(string subpartName, bool visible, IMyEntity myEntity)
        {
            try
            {
                if (MyAPIGateway.Utilities.IsDedicated)
                    return;

                LogDebug($"AQD_LG_TrawlingNet: SetSubpartVisibility called for subpart '{subpartName}' with visible={visible}; entId={Entity?.EntityId}");

                MyEntitySubpart subpart;
                bool inCache = _cachedSubparts.TryGetValue(subpartName, out subpart);

                if (!inCache || subpart == null || subpart.Closed)
                {
                    myEntity.TryGetSubpart(subpartName, out subpart);

                    if (subpart != null)
                    {
                        _cachedSubparts[subpartName] = subpart;
                        LogDebug($"AQD_LG_TrawlingNet: SetSubpartVisibility: cached subpart '{subpartName}'; entId={Entity?.EntityId}");
                    }
                    else
                    {
                        LogDebug($"AQD_LG_TrawlingNet: SetSubpartVisibility: subpart '{subpartName}' not found, will retry next tick; entId={Entity?.EntityId}");
                        return;
                    }
                }

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

        // --- Everything below here is unchanged from your file snippet ---
        // (TransferNetContentToInventory, GetDefinition, AppendCustomInfo, data sync, etc.)
        // Keep your existing implementations below this point.

        private void TransferNetContentToInventory()
        {
            try
            {
                //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory called; NetContent={NetContent}; SubtypeId={Content.NetContentSubtypeId}; entId={Entity?.EntityId}");

                if (NetContent < 0.01f)
                {
                    //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: NetContent below threshold, skipping; entId={Entity?.EntityId}");
                    return;
                }

                float contentPercentage = (NetContent / MAX_NET_CONTENT) * 100f;

                int newState = (int)Math.Ceiling(contentPercentage / 20f);
                _deckFishVisualState = MathHelper.Clamp(newState, 0, SUBPART_NAME_FISH_DECK_FISH.Length);

                LogDebug($"AQD_LG_TrawlingNet: Visual State calculated: {_deckFishVisualState} ({contentPercentage:F1}%)");

                IMyInventory inventory = _fishCollector.GetInventory();
                if (inventory == null)
                {
                    //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: inventory is null, skipping; entId={Entity?.EntityId}");
                    return;
                }

                var itemDefinition = GetDefinition(Content.NetContentSubtypeId);
                if (itemDefinition == null)
                {
                    LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: itemDefinition not found for SubtypeId='{Content.NetContentSubtypeId}', skipping; entId={Entity?.EntityId}");
                    return;
                }
                //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: itemDefinition found; Id={itemDefinition.Id}; Volume={itemDefinition.Volume}; entId={Entity?.EntityId}");

                float volumePerFish = itemDefinition.Volume;
                if (volumePerFish <= 0) volumePerFish = 0.01f;

                float availableVolume = (float)(inventory.MaxVolume - inventory.CurrentVolume);
                int roomForFish = (int)Math.Floor(availableVolume / volumePerFish);
                int fishToMove = Math.Min((int)Math.Round(NetContent), roomForFish);
                //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: availableVolume={availableVolume:F3}; roomForFish={roomForFish}; fishToMove={fishToMove}; entId={Entity?.EntityId}");

                if (fishToMove > 0)
                {
                    var content = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(itemDefinition.Id);
                    inventory.AddItems((MyFixedPoint)fishToMove, content);
                    //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: added {fishToMove} fish to inventory; entId={Entity?.EntityId}");
                }
                else
                {
                    //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: fishToMove=0, nothing added; entId={Entity?.EntityId}");
                }

                NetContent = 0f;
                //LogDebug($"AQD_LG_TrawlingNet: TransferNetContentToInventory: NetContent reset to 0; entId={Entity?.EntityId}");



            }
            catch (Exception e)
            {
                LogError($"AQD_LG_TrawlingNet: Error in TransferNetContentToInventory!\n{e}");
            }
        }

        private void DoWaterSFX()
        {
            // Early exit for dedicated servers to avoid unnecessary API calls and potential errors with the water mod API, which is client-side only.
            if (IsDedicatedServer()) return;

            // Sidenote: If the lastEnabledFishing is null, it doesn't need an effect, just an update.
            if (lastEnableFishing == null)
            {
                lastEnableFishing = EnableFishing;
                return;
            }
            // Only proceed if the state has actually changed (toggle detected)
            if (lastEnableFishing == EnableFishing) return;

            lastEnableFishing = EnableFishing;

            // Get the blocks current position
            var blockPosition = Block.GetPosition();

            // Get the position 10 meters in front of the block
            var forwardVector = Vector3D.TransformNormal(Vector3D.Forward, Block.WorldMatrix);
            var targetLocation = blockPosition + forwardVector * 10.0;

            targetLocation = WaterAPI.GetClosestSurfacePoint(targetLocation);

            WaterAPI.CreateBubble(targetLocation, 1.5f);

            WaterAPI.CreateSplash(targetLocation, 3.5f, true);


        }

        public static MyPhysicalItemDefinition GetDefinition(string subtypeName)
        {
            if (string.IsNullOrEmpty(subtypeName)) return null;

            MyPhysicalItemDefinition definition;

            if (!_cache.TryGetValue(subtypeName, out definition))
            {
                var id = new MyDefinitionId(ConsumableType, subtypeName);
                definition = MyDefinitionManager.Static.GetPhysicalItemDefinition(id);
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