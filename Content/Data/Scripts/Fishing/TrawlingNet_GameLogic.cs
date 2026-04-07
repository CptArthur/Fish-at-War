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

        private TaskScheduler _scheduler;
        private bool _isInit = false;

        public IMyTerminalBlock Block { get; private set; }
        private IMyFunctionalBlock _fishCollector;

        private Random _random;

        private const double SpeedLowSq = 5 * 5;
        private const double SpeedMidSq = 10 * 10;
        private const double SpeedHighSq = 15 * 15;

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

        private const string SUBPART_NAME_ROLL = "subpart_roll";
        private const string SUBPART_NAME_LINE = "subpart_line";
        private const string SUBPART_NAME_NET = "subpart_net";
        private const string SUBPART_NAME_FISH_DECK = "fish";
        private static readonly string[] SUBPART_NAME_FISH_DECK_FISH = { "fish_1", "fish_2", "fish_3", "fish_4", "fish_5" };
        private const string MODEL_PATH_LINE = @"Models\Cubes\large\AQD_LG_TrawlingNet_Subpart_Line.mwm";
        private const string MODEL_PATH_NET = @"Models\Cubes\large\AQD_LG_TrawlingNet_Subpart_Net.mwm";

        private List<string> _currentVisibleFish = new List<string>();

        private Matrix _spawnedNetLocalMatrix;

        // Used for the SFX and bubble effect when fishing
        private bool? lastEnableFishing = null;

        private NetStatusEvaluator _evaluator = new NetStatusEvaluator();

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

        public float NetContentPercentage => NetContent / MAX_NET_CONTENT;

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

                // Tie in the evaluator here
                if (_evaluator != null)
                    _evaluator.IsInLocation = value;

                NetContentChanged();
            }
        }

        public float LastSpeedSq
        {
            get { return Content.LastSpeedSq; }
            set
            {
                if (Content.LastSpeedSq == value) return;

                // Tie in the evaluator here
                if (_evaluator != null)
                    _evaluator.Efficiency = GetFishEfficiencySquared(value);

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

                if (!_isInit)
                {
                    // Instantiate the scheduler and pass the server status
                    _scheduler = new TaskScheduler(IsDedicatedServer());

                    // Register all your modular methods
                    _scheduler.Register(SyncSettings, 10);
                    _scheduler.Register(CheckFunctionalSafety, 10);
                    _scheduler.Register(CheckBlockOrientation, 10);
                    _scheduler.Register(CheckNetSubmersion, 10);
                    _scheduler.Register(UpdateLocationStatus, 10);
                    _scheduler.Register(UpdateTerminalUI, 10);
                    _scheduler.Register(DoWaterSFX, 10, true); // Client only

                    //_scheduler.Register(UpdateSubpartVisibility, 2, true); // Client only -> Taken out for now since no fish dummys in the model, but will be needed when we add them back in.
                    _scheduler.Register(RunMainFishingTick, 600);

                    _isInit = true;
                }

                if (!IsDedicatedServer())
                {
                    UpdateSubparts();
                }

                // 3. Let the scheduler handle the rest
                _scheduler.Tick();
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateAfterSimulation!\n{e}"); }
        }

        #region Subpart Management

        private void UpdateSubparts()
        {
            // 1. Validation: If block is invalid, clean up and exit
            if (Block == null || Block.Closed || !Block.IsFunctional)
            {
                LogDebug($"UpdateSubparts: Block invalid (null={Block == null}, closed={Block?.Closed}, functional={Block?.IsFunctional}), calling CloseSubparts; entId={Entity?.EntityId}");
                CloseSubparts();
                return;
            }

            Block.NeedsWorldMatrix = true;

            // Three subparts: roll, line, net. Roll is hidden when fishing, line and net are hidden when not fishing.

            // First handle the roll subpart, which is a model subpart and only needs caching and visibility toggling

            // Check if already cached; if not, try to get and cache it
            MyEntitySubpart rollSubpart;
            if (!_cachedSubparts.TryGetValue(SUBPART_NAME_ROLL, out rollSubpart))
            {
                Block.TryGetSubpart("roll", out rollSubpart); // Good for now
                if (rollSubpart != null)
                {
                    _cachedSubparts[SUBPART_NAME_ROLL] = rollSubpart;
                    LogDebug($"UpdateSubparts: Cached roll subpart; entId={Entity?.EntityId}");
                }
                else
                {
                    LogDebug($"UpdateSubparts: Roll subpart not found, will retry next tick; entId={Entity?.EntityId}");
                }
            }

            if (rollSubpart != null)
            {
                bool shouldBeVisible = !EnableFishing;
                if (rollSubpart.Render.Visible != shouldBeVisible)
                {
                    rollSubpart.Render.Visible = shouldBeVisible;
                    LogDebug($"UpdateSubparts: Set roll subpart visibility to {shouldBeVisible}; entId={Entity?.EntityId}");
                }
            }

            // Second handle the line subpart, it's local matrix is the same as the roll subpart, so we can reuse that. It's only visible when fishing.

            // 2. Creation: Spawn the subpart if it doesn't exist or is closed
            if (!_cachedSubparts.ContainsKey(SUBPART_NAME_LINE))
            {
                LogDebug($"UpdateSubparts: '{SUBPART_NAME_LINE}' not in cache, attempting creation; entId={Entity?.EntityId}");

                string pathToModel;
                if (!TryGetFullModelPath(MODEL_PATH_LINE, ModContext, out pathToModel))
                {
                    LogDebug($"UpdateSubparts: TryGetFullModelPath failed for '{MODEL_PATH_LINE}'; entId={Entity?.EntityId}");
                    return;
                }

                LogDebug($"UpdateSubparts: Model path resolved: '{pathToModel}'; entId={Entity?.EntityId}");

                MyEntity parent = Block as MyEntity;
                if (parent == null)
                {
                    LogDebug($"UpdateSubparts: Block could not be cast to MyEntity; entId={Entity?.EntityId}");
                    return;
                }

                // Find the roll subpart to determine local matrix position !!! Not the line subpart
                if (rollSubpart == null)
                {
                    LogDebug($"UpdateSubparts: Roll subpart is null, cannot determine local matrix for line subpart; entId={Entity?.EntityId}");
                    return;
                }

                _spawnedNetLocalMatrix = rollSubpart.PositionComp.LocalMatrixRef;
                LogDebug($"UpdateSubparts: Captured local matrix Translation={_spawnedNetLocalMatrix.Translation}; entId={Entity?.EntityId}");

                var lineSubpart = CreateRealSubpart(parent, pathToModel, SUBPART_NAME_LINE, _spawnedNetLocalMatrix);
                if (lineSubpart == null)
                {
                    LogDebug($"UpdateSubparts: CreateRealSubpart returned null for '{SUBPART_NAME_LINE}'; entId={Entity?.EntityId}");
                    return;
                }

                _cachedSubparts.Add(SUBPART_NAME_LINE, lineSubpart);
                LogDebug($"UpdateSubparts: '{SUBPART_NAME_LINE}' created and cached (id={lineSubpart.EntityId}); entId={Entity?.EntityId}");
            }

            // 3. Update: Handle visibility and rendering
            if (_cachedSubparts.ContainsKey(SUBPART_NAME_LINE))
            {
                var lineSubpartCached = _cachedSubparts[SUBPART_NAME_LINE];

                // Sync visibility with fishing state
                if (lineSubpartCached.Render != null && EnableFishing != lineSubpartCached.Render.Visible)
                {
                    LogDebug($"UpdateSubparts: Syncing '{SUBPART_NAME_LINE}' visibility to {EnableFishing}; entId={Entity?.EntityId}");
                    lineSubpartCached.Render.Visible = EnableFishing;
                }
            }
            else
            {
                LogDebug($"UpdateSubparts: '{SUBPART_NAME_LINE}' still not in cache after creation attempt, skipping update; entId={Entity?.EntityId}");
            }

            // Manage the net subpart, which is also only visible when fishing. It is a subpart of the line subpart, so we want to adjust it'S local matrix to make the boyues float on the water surface instead of being underwater


            // Check if already cached; if not, try to get and cache it
            MyEntitySubpart netSubpart;
            if (!_cachedSubparts.TryGetValue(SUBPART_NAME_NET, out netSubpart))
            {
                var lineSubpartCached = _cachedSubparts[SUBPART_NAME_LINE];
                lineSubpartCached.TryGetSubpart("net", out netSubpart); // Yeah, this is correctly named
                if (netSubpart != null)
                {
                    _cachedSubparts[SUBPART_NAME_NET] = netSubpart;
                    LogDebug($"UpdateSubparts: Cached net subpart; entId={Entity?.EntityId}");
                }
                else
                {
                    LogDebug($"UpdateSubparts: Net subpart not found, will retry next tick; entId={Entity?.EntityId}");
                }
            }

            // THis is a placeholder for now, here I will do the checking of the submersion and adjustment

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
            }
            catch { }

            // 7) Flags - not sure they are needed
            try
            {
                child.Flags |= (EntityFlags.NeedsDrawFromParent | EntityFlags.NeedsWorldMatrix);
            }
            catch { }

            return child;
        }


        #endregion

        #region Render Visibility Helpers

        public void UpdateSubpartVisibility()
        {
            try
            {
                if (MyAPIGateway.Utilities.IsDedicated) return;
                UpdateFishDeckVisualState(_deckFishVisualState);
            }
            catch (Exception e) { LogError($"Error in Visibility Update!\n{e}"); }
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

        #endregion

        #region Functional Safety and State Checks

        public void CheckFunctionalSafety()
        {
            _evaluator.IsFunctional = _fishCollector.IsFunctional;
            _evaluator.IsWorking = _fishCollector.IsWorking;
            _evaluator.IsEnabled = EnableFishing;

            if (_evaluator.IsEnabled && !_evaluator.IsFunctional)
            {
                NetContent = 0;
                EnableFishing = false;
            }
        }

        public void CheckBlockOrientation()
        {
            if (!_evaluator.IsFunctional ||
                !_evaluator.IsWorking ||
                !_evaluator.IsEnabled ||
                _fishCollector?.CubeGrid?.Physics == null)
            {
                _evaluator.IsOriented = false;
                return;
            }

            // Get the up vector of the block in world space and the gravity vector from the physics component
            Vector3D blockUp = Vector3D.TransformNormal(Vector3D.Up, _fishCollector.WorldMatrix);
            Vector3D? gravity = _fishCollector.CubeGrid.Physics.Gravity;



            // if the angle between gravity and block up is less than 45 degrees, we are oriented correctly for fishing
            if (!gravity.HasValue) return;
            double angle = Vector3D.Angle(blockUp, gravity.Value * -1);
            if (angle < MathHelper.ToRadians(45))
            {
                //LogDebug($"AQD_LG_TrawlingNet: Oriented for fishing (angle={MathHelper.ToDegrees(angle):F1} degrees)");
                _evaluator.IsOriented = true;
            }
            else
            {
                //LogDebug($"AQD_LG_TrawlingNet: NOT Oriented for fishing (angle={MathHelper.ToDegrees(angle):F1} degrees), NetContent reset to 0");
                _evaluator.IsOriented = false;
                NetContent = 0;
            }

        }
        private void CheckNetSubmersion()
        {
            if (!_evaluator.IsFunctional || !_evaluator.IsWorking || !_evaluator.IsEnabled || !_evaluator.IsOriented)
            {
                _evaluator.IsSubmerged = false;
                return;
            }

            // 1. Validation
            MyEntitySubpart netSubpart;
            if (!_cachedSubparts.TryGetValue(SUBPART_NAME_NET, out netSubpart) || netSubpart == null || netSubpart.Closed)
            {
                LogDebug($"CheckNetSubmersion: Net subpart not found or invalid, cannot check submersion; entId={Entity?.EntityId}");
                _evaluator.IsSubmerged = false; // Assume not submerged if we can't find the subpart to check, to avoid false positives
                return;
            }

            var model = netSubpart.Model as IMyModel;
            Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
            model.GetDummies(dummies);

            IMyModelDummy dLeft, dCenter, dRight;
            if (!dummies.TryGetValue("net_bounds_left", out dLeft) ||
                !dummies.TryGetValue("net_bounds_center", out dCenter) ||
                !dummies.TryGetValue("net_bounds_right", out dRight))
            {
                LogDebug($"CheckNetSubmersion: One or more dummies not found in model; entId={Entity?.EntityId}");
                _evaluator.IsSubmerged = false; // Assume not submerged if we can't find the dummies to check, to avoid false positives
                return;
            }

            // Calculate the "Geometric Center" of the dummies in Local Space
            Vector3D localDummyCenter = ((Vector3D)dLeft.Matrix.Translation +
                                         (Vector3D)dCenter.Matrix.Translation +
                                         (Vector3D)dRight.Matrix.Translation) / 3.0;

            // We transform the dummy center by the subparts worldmatrix
            Vector3D worldDummyPos = LocalPositionToGlobal(localDummyCenter, netSubpart.PositionComp.WorldMatrixRef);

            // 4. Get the Water Surface at that stable world position
            _evaluator.IsSubmerged = WaterAPI.IsUnderwater(worldDummyPos);

            LogDebug($"CheckNetSubmersion: IsSubmerged={_evaluator.IsSubmerged}");
        }
        public void UpdateLocationStatus()
        {
            try
            {
                if (!_evaluator.IsFunctional ||
                    !_evaluator.IsWorking)
                {
                    IsInFishLocation = false;
                    return;
                }

                Vector3D worldPosition = _fishCollector.PositionComp.GetPosition();

                if ((worldPosition - MapUtilities.agarisCenter).LengthSquared() > (60000d * 60000d)) // If we're very far from the planet, skip the expensive pixel check and just say we're not in a fish location
                {
                    IsInFishLocation = false;
                }
                else
                {
                    IsInFishLocation = MapUtilities.IsAtFishLocation(worldPosition);
                }

            }
            catch (Exception e)
            {
                LogError($"AQD_LG_TrawlingNet: Error in UpdateLocationStatus!\n{e}");
            }
        }

        public class NetStatusEvaluator
        {
            // Physical/Functional States
            public bool IsFunctional { internal set; get; }
            public bool IsWorking { internal set; get; }
            public bool IsEnabled { internal set; get; }


            // Environmental States
            public bool IsOriented { internal set; get; }
            public bool IsSubmerged { internal set; get; }

            public bool IsInLocation { internal set; get; }

            // Efficiency
            public float Efficiency { internal set; get; }

            // The "Master" Logic: Can we actually pull fish?
            public bool CanFish => IsFunctional && IsWorking && IsEnabled && IsOriented && IsInLocation && IsSubmerged;

            public string GetStatusMessage()
            {
                if (!IsFunctional) return "Net Damaged";
                if (!IsWorking) return "Net Unpowered";
                if (!IsEnabled) return "Net Hauled In";
                if (!IsOriented) return "Wrong Orientation";
                if (!IsSubmerged) return "Not Submerged";
                if (!IsInLocation) return "No fish, no catch :)";
                // If we can fish, but efficiency is 0, we still show the speed warning
                if (Efficiency <= 0) return "Invalid Speed";
                return "Fishing";
            }
        }

        #endregion

        #region Fishing Logic

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


        public float GetFishEfficiencySquared(float speedSq)
        {
            // 1. Out of bounds (Too slow or Too fast)
            if (speedSq <= 0 || speedSq >= SpeedHighSq) return 0;

            // 2. The Sweet Spot (100% Efficiency between 10m/s and 15m/s)
            if (speedSq >= SpeedLowSq && speedSq <= SpeedMidSq) return 1;

            // We need the raw linear speed to calculate both the linear and exponential curves
            float currentSpeed = (float)Math.Sqrt(speedSq);

            // 3. Ramp-up zone (Perfectly Linear)
            if (speedSq < SpeedLowSq)
            {
                float lowSpeed = (float)Math.Sqrt(SpeedLowSq);
                return currentSpeed / lowSpeed; // e.g., 2.5 / 5.0 = 0.5 (50%)
            }

            // 4. Penalty zone (Exponential drop-off)
            float midSpeed = (float)Math.Sqrt(SpeedMidSq);
            float highSpeed = (float)Math.Sqrt(SpeedHighSq);

            // Calculate how far into the penalty zone we are (0.0 to 1.0 ratio)
            float penaltyRatio = (currentSpeed - midSpeed) / (highSpeed - midSpeed);

            // Squaring the penalty ratio creates an exponential curve.
            // Example: At 12.5 m/s (0.5 ratio), 0.5^2 = 0.25 penalty.
            // This makes the net very forgiving at 11 m/s, but efficiency completely crashes as you approach 15 m/s.
            float efficiency = 1.0f - (float)Math.Pow(penaltyRatio, 2);

            return MathHelper.Clamp(efficiency, 0f, 1f);
        }

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

        #endregion

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


        #region Custom Info and Terminal UI
        void AppendCustomInfo(IMyTerminalBlock block, StringBuilder info)
        {
            try
            {
                float contentPercentage = (NetContent / MAX_NET_CONTENT) * 100f;
                info.AppendLine($"--- Trawling Status {GetSpinner()} ---");
                info.AppendLine($"Net Content: {contentPercentage:00.00}%");
                string locationDetail = _evaluator.IsInLocation ? "Yes" : "No";
                info.AppendLine($"In Fishing Location: {locationDetail}");
                info.AppendLine($"Status: {_evaluator.GetStatusMessage()}");

                if (_evaluator.CanFish)
                {
                    string speedHint = _evaluator.Efficiency < 1f ? (LastSpeedSq < SpeedLowSq ? " (too slow)" : " (too fast)") : "";
                    info.AppendLine($"Efficiency: {(_evaluator.Efficiency * 100):F0}%{speedHint}");
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

        public void UpdateTerminalUI()
        {
            if (MyAPIGateway.Gui.GetCurrentScreen == MyTerminalPageEnum.ControlPanel)
            {
                Block.RefreshCustomInfo();
                Block.SetDetailedInfoDirty();
            }
        }
        #endregion

        #region Sync and stuff
        // --- Data Persistence and Sync 

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

        #endregion


        public override void OnRemovedFromScene()
        {
            base.OnRemovedFromScene();
            CloseSubparts();
        }

        public override void Close()
        {
            base.Close();
            try
            {
                if (Block == null)
                    return;

                _scheduler?.Dispose();
                _scheduler = null;

                Block = null;

                CloseSubparts();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
        }

        private void CloseSubparts()
        {
            if (_cachedSubparts.Count == 0) return;
            try
            {
                foreach (var subpart in _cachedSubparts)
                {
                    if (subpart.Value != null && !subpart.Value.Closed)
                    {
                        LogDebug($"CloseSubparts: Closing subpart '{subpart.Key}' id={subpart.Value.EntityId}; entId={Entity?.EntityId}");
                        subpart.Value.Close();
                    }
                }
                _cachedSubparts.Clear();
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in CloseSubparts!\n{e}"); }
        }

        #region Misc
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

        #endregion

    }
}