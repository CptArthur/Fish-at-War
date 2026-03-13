using Jakaria.API;
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
using SpaceEngineers.Game.ModAPI;
using System;
using System.Collections.Generic;
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
using PEPCO.Utilities;
using static PEPCO.ScriptHelpers;

namespace AaWFoodScript
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_FunctionalBlock), false, "AQD_LG_TrawlingNet")]
    public class FishCollectorComponent : MyGameLogicComponent
    {
        private AaWFood_Session Session => AaWFood_Session.Instance;

        public IMyTerminalBlock Block { get; private set; }
        private IMyFunctionalBlock _fishCollector;
        private int count = 0;
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


        private const string SUBPART_NAME = "net";
        private MyEntitySubpart _cachedNetSubpart;

        public bool EnableFishing
        {
            get { return Settings.EnableFishing; }
            set
            {
                if (Settings.EnableFishing == value) return;

                Settings.EnableFishing = value;

                // Trigger visibility change immediately when toggled
                RefreshSubpartVisibility();
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

        public bool IsAtFishLocation // Cached value to avoid repeated checks in the UI
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

                TrawlingNetTerminalControls.DoOnce(ModContext);

                Block.AppendingCustomInfo += AppendCustomInfo;

                NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
                if (MyAPIGateway.Session.IsServer)
                {
                    NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateOnceBeforeFrame!\n{e}"); }
        }

        public override void UpdateAfterSimulation10()
        {
            try
            {
                SyncSettings();
                // SyncNetContent(); // Removed as we now sync net content immediately when it changes to minimize desync issues, and we don't want to sync it every 10 frames regardless of changes.

                // If the block is not functional and the net is extended, all fish are lost
                if (EnableFishing && !_fishCollector.IsFunctional)
                {
                    NetContent = 0;
                    EnableFishing = false;
                }

                //Below part is only relevant for clients
                if (MyAPIGateway.Utilities.IsDedicated) return;

                // Ensure visibility stays correct (useful after world loads or grid pastes)
                RefreshSubpartVisibility();


                if (MyAPIGateway.Gui.GetCurrentScreen == MyTerminalPageEnum.ControlPanel)
                {
                    Block.RefreshCustomInfo();
                    Block.SetDetailedInfoDirty();
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateAfterSimulation10!\n{e}"); }
        }

        public override void UpdateAfterSimulation100()
        {
            try
            {
                if (count == 0) DoFishingTick();

                count = (count + 1) % 6;
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateAfterSimulation100!\n{e}"); }
        }

        private void DoFishingTick()
        {
            try
            {
                if (!_fishCollector.HasInventory || !_fishCollector.IsWorking || !EnableFishing)
                {
                    LastCaught = 0f;
                    return;
                }

                Vector3D worldPosition = _fishCollector.PositionComp.GetPosition();
                if ((worldPosition - MapUtilities.agarisCenter).LengthSquared() > (60000d * 60000d)) return;

                var Agaris = MyGamePruningStructure.GetClosestPlanet(worldPosition);
                var temp = WaterAPI.GetClosestSurfacePoint(worldPosition, Agaris);
                double distToSurfaceSq = (temp - worldPosition).LengthSquared();

                // Cache for UI
                IsAtFishLocation = MapUtilities.IsAtFishLocation(worldPosition);

                if (distToSurfaceSq < (50d * 50d) && IsAtFishLocation)
                {
                    float? actualVelocity = _fishCollector.CubeGrid?.LinearVelocity.LengthSquared();
                    if (actualVelocity == null) return;

                    float speedSq = actualVelocity.Value;
                    LastSpeedSq = speedSq;
                    var lastEfficiency = GetFishEfficiencySquared(speedSq); // Cached
                    float amount = _random.Next(CATCH_MIN, CATCH_MAX);
                    LastCaught = amount * lastEfficiency; // Cached

                    NetContent += LastCaught;

                    // Currently hardcoded for "Fish" in the future this needs to come from the MapUtilities
                    NetContentSubtypeId = "Fish";

                    WaterAPI.CreateBubble(worldPosition, 2);
                }
                else
                {
                    int escaped = _random.Next(ESCAPE_MIN, ESCAPE_MAX);
                    NetContent = Math.Max(0, NetContent - escaped);
                    LastSpeedSq = 0f;
                    LastCaught = 0f;
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in DoFishingTick!\n{e}"); }
        }

        /// <summary>
        /// Finds the net subpart and sets its visibility based on EnableFishing.
        /// </summary>
        private void RefreshSubpartVisibility()
        {
            try
            {
                if (MyAPIGateway.Session.IsServer && MyAPIGateway.Utilities.IsDedicated)
                    return;

                // Try to find the subpart once if we haven't already
                if (_cachedNetSubpart == null)
                {
                    Entity.TryGetSubpart(SUBPART_NAME, out _cachedNetSubpart);
                }

                // If we found it (now or previously), update visibility
                if (_cachedNetSubpart != null)
                {
                    if (_cachedNetSubpart.Render.Visible != EnableFishing)
                    {
                        _cachedNetSubpart.Render.Visible = EnableFishing;
                    }
                }
            }
            catch (Exception e)
            {
                LogError($"AQD_LG_TrawlingNet: Error in RefreshSubpartVisibility!\n{e}");
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
                // Calculate content percentage
                float contentPercentage = (NetContent / MAX_NET_CONTENT) * 100f;
                
                info.AppendLine("--- Trawling Status ---");
                info.AppendLine($"Net Content: ({contentPercentage:F2}%)");
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
                    if (IsAtFishLocation)
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

        public void UpdateNetContentFromInput(TrawlingNetContent loadedNetContent)
        {
            try
            {
                LogDebug($"AQD_LG_TrawlingNet: UpdateNetContentFromInput called; incomingNetContent={loadedNetContent?.NetContent}; incomingEmptyNet={loadedNetContent?.EmptyNet}; incomingSubtype={loadedNetContent?.NetContentSubtypeId}; entId={Entity?.EntityId}");

                if (Content == loadedNetContent)
                {
                    LogDebug($"AQD_LG_TrawlingNet: UpdateNetContentFromInput: content unchanged, skipping; entId={Entity?.EntityId}");
                    return; // No change, no need to update
                }

                // Sync all but the empty net state
                Content.NetContent = MathHelper.Clamp(loadedNetContent.NetContent, 0, MAX_NET_CONTENT);
                Content.NetContentSubtypeId = loadedNetContent.NetContentSubtypeId;
                Content.IsInFishLocation = loadedNetContent.IsInFishLocation;
                Content.LastSpeedSq = loadedNetContent.LastSpeedSq;
                Content.LastCaught = loadedNetContent.LastCaught;

                LogDebug($"AQD_LG_TrawlingNet: UpdateNetContentFromInput: content synced; NetContent={Content.NetContent}; SubtypeId={Content.NetContentSubtypeId}; IsInFishLocation={Content.IsInFishLocation}; LastSpeedSq={Content.LastSpeedSq}; LastCaught={Content.LastCaught}; entId={Entity?.EntityId}");

                if (loadedNetContent.EmptyNet) // Handle the net emptying case with the inventory transfer
                {
                    LogDebug($"AQD_LG_TrawlingNet: UpdateNetContentFromInput: EmptyNet=true, calling TransferNetContentToInventory; entId={Entity?.EntityId}");
                    TransferNetContentToInventory();
                }
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in UpdateNetContentFromInput!\n{e}"); }
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

        void LoadNetContent()
        {
            if (Block.Storage == null) return;
            string rawData;
            if (!Block.Storage.TryGetValue(StorageKeys.FISHATWARNETCONTENT, out rawData)) return;
            try
            {
                var loadedNetContent = MyAPIGateway.Utilities.SerializeFromBinary<TrawlingNetContent>(Convert.FromBase64String(rawData));
                if (loadedNetContent != null) NetContent = loadedNetContent.NetContent;
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error loading net content!\n{e}"); }
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

        void SyncNetContent(bool emptyNet = false)
        {
            try
            {
                LogDebug($"AQD_LG_TrawlingNet: SyncNetContent called; emptyNet={emptyNet}; NetContent={NetContent}; entId={Entity?.EntityId}");
                var currentContent = Content;
                
                currentContent.EmptyNet = emptyNet;
                LogDebug($"AQD_LG_TrawlingNet: SyncNetContent EmptyNet flag set on content; entId={Entity?.EntityId}");

                // Sync net content immediately when it changes, to ensure server always has the most up-to-date content for inventory transfer and to minimize desync issues with clients.
                SaveNetContent();
                LogDebug($"AQD_LG_TrawlingNet: SyncNetContent SaveNetContent complete, sending packet; entId={Entity?.EntityId}");
                Session.SendTrawlingNetContentPacketSetting(Block.EntityId, currentContent);
                LogDebug($"AQD_LG_TrawlingNet: SyncNetContent packet sent; entId={Entity?.EntityId}");
            }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error in SyncNetContent!\n{e}"); }
        }

        public override bool IsSerialized()
        {
            try { SaveSettings(); SaveNetContent(); }
            catch (Exception e) { LogError($"AQD_LG_TrawlingNet: Error saving settings!\n{e}"); }
            return base.IsSerialized();
        }
    }
}