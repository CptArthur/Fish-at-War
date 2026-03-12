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
        private int _netContentSyncCountdown;
        public const int SETTINGS_CHANGED_COUNTDOWN = (60 * 1) / 10;

        private const float MAX_NET_CONTENT = 1650f;

        // Fish catch random bounds
        private const int CATCH_MIN = 1;
        private const int CATCH_MAX = 55;
        private const int ESCAPE_MIN = 1;
        private const int ESCAPE_MAX = 10;

        // UI Caching Fields
        private float _lastEfficiency = 0f;
        private float _lastCaught = 0f;
        private float _lastSpeedSq = 0f;
        private bool _isAtFishLocation = false;

        private const string SUBPART_NAME = "net";

        public bool EnableFishing
        {
            get { return Settings.EnableFishing; }
            set
            {
                if (Settings.EnableFishing == value) return;

                if (!value)
                {
                    TransferNetContentToInventory();
                }

                Settings.EnableFishing = value;

                // Trigger visibility change immediately when toggled
                RefreshSubpartVisibility();

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

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            try
            {
                Block = (IMyTerminalBlock)Entity;
                if (Block == null) return;

                _fishCollector = Entity as IMyFunctionalBlock;
                NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in Init!\n{e}"); }
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
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in UpdateOnceBeforeFrame!\n{e}"); }
        }

        public override void UpdateAfterSimulation10()
        {
            try
            {
                SyncSettings();
                SyncNetContent();

                // If the block is not functional and the net is extended, all fish are lost
                if (EnableFishing && !_fishCollector.IsFunctional)
                {
                    NetContent = 0;
                    EnableFishing = false;
                }

                // Ensure visibility stays correct (useful after world loads or grid pastes)
                RefreshSubpartVisibility();
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in UpdateAfterSimulation10!\n{e}"); }
        }

        public override void UpdateAfterSimulation100()
        {
            try
            {
                if (MyAPIGateway.Gui.GetCurrentScreen == MyTerminalPageEnum.ControlPanel)
                {
                    Block.RefreshCustomInfo();
                    Block.SetDetailedInfoDirty();
                }

                if (count < 6)
                {
                    count++;
                    return;
                }

                DoFishingTick();
                count = 0;
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in UpdateAfterSimulation100!\n{e}"); }
        }

        private void DoFishingTick()
        {
            try
            {
                if (!_fishCollector.HasInventory || !_fishCollector.IsWorking || !EnableFishing)
                {
                    _lastEfficiency = 0f;
                    _lastCaught = 0f;
                    return;
                }

                Vector3D worldPosition = _fishCollector.PositionComp.GetPosition();
                if ((worldPosition - MapUtilities.agarisCenter).LengthSquared() > (60000d * 60000d)) return;

                var Agaris = MyGamePruningStructure.GetClosestPlanet(worldPosition);
                var temp = WaterAPI.GetClosestSurfacePoint(worldPosition, Agaris);
                double distToSurfaceSq = (temp - worldPosition).LengthSquared();

                // Cache for UI
                _isAtFishLocation = MapUtilities.IsAtFishLocation(worldPosition);

                if (distToSurfaceSq < (50d * 50d) && _isAtFishLocation)
                {
                    float? actualVelocity = _fishCollector.CubeGrid?.LinearVelocity.LengthSquared();
                    if (actualVelocity == null) return;

                    float speedSq = actualVelocity.Value;
                    _lastSpeedSq = speedSq;
                    _lastEfficiency = GetFishEfficiencySquared(speedSq); // Cached
                    float amount = _random.Next(CATCH_MIN, CATCH_MAX);
                    _lastCaught = amount * _lastEfficiency; // Cached

                    NetContent += _lastCaught;
                    WaterAPI.CreateBubble(worldPosition, 2);
                }
                else
                {
                    int escaped = _random.Next(ESCAPE_MIN, ESCAPE_MAX);
                    NetContent = Math.Max(0, NetContent - escaped);
                    _lastSpeedSq = 0f;
                    _lastEfficiency = 0f;
                    _lastCaught = 0f;
                }
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in DoFishingTick!\n{e}"); }
        }

        /// <summary>
        /// Finds the net subpart and sets its visibility based on EnableFishing.
        /// </summary>
        private void RefreshSubpartVisibility()
        {
            try
            {
                // Subparts and rendering only exist on clients.
                if (MyAPIGateway.Session.IsServer && MyAPIGateway.Utilities.IsDedicated)
                    return;

                if (Entity == null)
                    return;

                MyEntitySubpart netSubpart;

                if (Entity.TryGetSubpart(SUBPART_NAME, out netSubpart))
                {
                    // Toggle the visibility based on the fishing state.
                    if (netSubpart.Render.Visible != EnableFishing)
                    {
                        netSubpart.Render.Visible = EnableFishing;
                    }
                }
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in RefreshSubpartVisibility!\n{e}"); }
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
                if (NetContent <= 0) return;

                IMyInventory inventory = _fishCollector.GetInventory();
                if (inventory == null) return;

                var fuelId = new MyDefinitionId(typeof(MyObjectBuilder_ConsumableItem), "Fish");
                var itemDefinition = MyDefinitionManager.Static.GetPhysicalItemDefinition(fuelId);

                // Logic Fix: Keep volume in m3 (0.01) to match inventory API (15.625)
                float volumePerFish = itemDefinition.Volume;

                float availableVolume = (float)(inventory.MaxVolume - inventory.CurrentVolume);
                int roomForFish = (int)Math.Floor(availableVolume / volumePerFish);
                int fishToMove = Math.Min((int)NetContent, roomForFish);

                if (fishToMove > 0)
                {
                    var content = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(fuelId);
                    inventory.AddItems((MyFixedPoint)fishToMove, content);

                    NetContent -= fishToMove;
                }

                NetContent = 0f; // All fish that don't fit are lost - This works as intended
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in TransferNetContentToInventory!\n{e}"); }
        }

        void AppendCustomInfo(IMyTerminalBlock block, StringBuilder info)
        {
            try
            {
                info.AppendLine($"Net Content: {NetContent:F0} / {MAX_NET_CONTENT}");
                info.AppendLine("--- Trawling Status ---");
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
                    if (_isAtFishLocation)
                    {
                        info.AppendLine("Status: Net set");
                        string speedHint = string.Empty;
                        if (_lastEfficiency < 1f)
                        {
                            speedHint = _lastSpeedSq < SpeedLowSq ? " (speed too low)" : " (speed too high)";
                        }
                        info.AppendLine($"Efficiency: {(_lastEfficiency * 100):F0}%{speedHint}");
                        info.AppendLine($"Last Catch: {_lastCaught:F0} fish");
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
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in AppendCustomInfo!\n{e}"); }
        }

        // --- Data Persistence and Sync (Kept Intact) ---
        public void UpdateSettingsFromInput(TrawlingNetSettings loadedSettingssettings)
        {
            try
            {
                if (Settings.EnableFishing != loadedSettingssettings.EnableFishing)
                    Settings.EnableFishing = loadedSettingssettings.EnableFishing;
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in UpdateSettingsFromInput!\n{e}"); }
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
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error loading settings!\n{e}"); }
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
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error loading net content!\n{e}"); }
        }

        void SaveNetContent()
        {
            try
            {
                if (Block?.Storage == null) Block.Storage = new MyModStorageComponent();
                Block.Storage.SetValue(StorageKeys.FISHATWARNETCONTENT, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Content)));
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in SaveNetContent!\n{e}"); }
        }

        void SaveSettings()
        {
            try
            {
                if (Block?.Storage == null) Block.Storage = new MyModStorageComponent();
                Block.Storage.SetValue(StorageKeys.FISHATWARSETTINGS, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Settings)));
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in SaveSettings!\n{e}"); }
        }

        void SettingsChanged() { if (_settingsSyncCountdown == 0) _settingsSyncCountdown = SETTINGS_CHANGED_COUNTDOWN; }
        void NetContentChanged() { if (_netContentSyncCountdown == 0) _netContentSyncCountdown = SETTINGS_CHANGED_COUNTDOWN; }

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
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in SyncSettings!\n{e}"); }
        }

        void SyncNetContent()
        {
            try
            {
                if (_netContentSyncCountdown > 0 && --_netContentSyncCountdown <= 0)
                {
                    SaveNetContent();
                    Session.SendTrawlingNetContentPacketSetting(Block.EntityId, Content);
                }
            }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error in SyncNetContent!\n{e}"); }
        }

        public override bool IsSerialized()
        {
            try { SaveSettings(); SaveNetContent(); }
            catch (Exception e) { MyLog.Default.WriteLineAndConsole($"AQD_LG_TrawlingNet: Error saving settings!\n{e}"); }
            return base.IsSerialized();
        }
    }
}