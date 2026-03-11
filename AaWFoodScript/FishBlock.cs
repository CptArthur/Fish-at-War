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
        public IMyTerminalBlock Block { get; private set; } // storing the entity as a block reference to avoid re-casting it every time it's needed, this is the lowest type a block entity can be.
        private IMyFunctionalBlock _fishCollector;
        private int count = 0;
        private Random _random; // Put it here to avoid creating a new instance every time we check for fish andt to avoid seed issues

        private const double SpeedLowSq = 5 * 5;    // 25
        private const double SpeedMidSq = 15 * 15;  // 225
        private const double SpeedHighSq = 25 * 25; // 625


        public readonly TrawlingNetSettings Settings = new TrawlingNetSettings();
        private int _settingsSyncCountdown;
        private int _netContentSyncCountdown;
        public const int SETTINGS_CHANGED_COUNTDOWN = (60 * 1) / 10;

        private float _netContent; // This field will track the current content in the net, it can be used for both display in terminal and for syncing with clients.

        private const float MAX_NET_CONTENT = 1650f; // Currently hardcoded max content, can be made set dynamically based on the block's inventory capacity

        private AaWFood_Session Session => AaWFood_Session.Instance; // helper to access the session component instance, which is where shared state and the network handler live

        public bool EnableFishing
        {
            get { return Settings.EnableFishing; }
            set
            {
                if (Settings.EnableFishing == value) return; // Only trigger if it actually changed

                if (!value)
                {
                    TransferNetContentToInventory(); // Empty the net into content
                }

                Settings.EnableFishing = value;
                SettingsChanged();
            }
        }

        public float NetContent
        {
            get { return _netContent; }
            set
            {
                if (_netContent == value) return; // Only trigger if it actually changed
                _netContent = MathHelper.Clamp(value, 0, MAX_NET_CONTENT); // Ensure content doesn't exceed max or drop below 0
                NetContentChanged();
            }
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyTerminalBlock)Entity;

            // Only proceed if the cast was successful
            if (Block == null) return;

            _fishCollector = Entity as IMyFunctionalBlock;

            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();

            if (Block?.CubeGrid?.Physics == null) // ignore projected and other non-physical grids
                return;

            _random = new Random(); // Initialize the random instance for fish generation

            // Set defaults
            Settings.EnableFishing = false;
            NetContent = 0f; // Default to empty

            // Load saved settings if they exist, otherwise save the defaults for next time.
            LoadSettings();
            SaveSettings();

            // Load net content from storage if it exists, otherwise save the default for next time.
            LoadNetContent();
            SaveNetContent();

            // Initiate the required terminal controls
            TrawlingNetTerminalControls.DoOnce(ModContext);

            // Start the regular update loop to sync settings if they change
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;

            if (MyAPIGateway.Session.IsServer)
            {
                NeedsUpdate |= MyEntityUpdateEnum.EACH_100TH_FRAME;
            }
        }

        public void UpdateSettingsFromInput(TrawlingNetSettings loadedSettingssettings)
        {
            if (Settings.EnableFishing != loadedSettingssettings.EnableFishing)
            {
                Settings.EnableFishing = loadedSettingssettings.EnableFishing;
            }
        }

        void LoadSettings()
        {
            if (Block.Storage == null)
            {
                return;
            }


            string rawData;
            if (!Block.Storage.TryGetValue(StorageKeys.FISHATWARSETTINGS, out rawData))
            {
                return;
            }

            try
            {
                var loadedSettings = MyAPIGateway.Utilities.SerializeFromBinary<TrawlingNetSettings>(Convert.FromBase64String(rawData));

                if (loadedSettings != null)
                {
                    UpdateSettingsFromInput(loadedSettings);

                    return;
                }
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLine($"Error loading settings!\n{e}");
            }
        }

        void LoadNetContent()
        {
            if (Block.Storage == null)
            {
                return;
            }


            string rawData;
            if (!Block.Storage.TryGetValue(StorageKeys.FISHATWARNETCONTENT, out rawData))
            {
                return;
            }

            try
            {
                var loadedNetContent = MyAPIGateway.Utilities.SerializeFromBinary<float>(Convert.FromBase64String(rawData));

                if (loadedNetContent > 0)
                {
                    NetContent = loadedNetContent;

                    return;
                }
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLine($"Error loading settings!\n{e}");
            }
        }

        void SaveNetContent()
        {
            if (Block == null)
                return; // called too soon or after it was already closed, ignore

            if (MyAPIGateway.Utilities == null)
                throw new NullReferenceException($"MyAPIGateway.Utilities == null; entId={Entity?.EntityId}; modInstance={Session != null}");

            if (Block.Storage == null)
                Block.Storage = new MyModStorageComponent();

            Block.Storage.SetValue(StorageKeys.FISHATWARNETCONTENT, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(NetContent)));
        }

        void SettingsChanged()
        {
            if (_settingsSyncCountdown == 0)
                _settingsSyncCountdown = SETTINGS_CHANGED_COUNTDOWN;
        }

        void NetContentChanged()
        {
            if (_netContentSyncCountdown == 0)
                _netContentSyncCountdown = SETTINGS_CHANGED_COUNTDOWN;
        }

        void SyncSettings()
        {
            if (_settingsSyncCountdown > 0 && --_settingsSyncCountdown <= 0)
            {
                SaveSettings();

                Session.SendTrawlingNetSettingsPacketSetting(Block.EntityId, Settings);
            }
        }

        void SyncNetContent()
        {
            if (_netContentSyncCountdown > 0 && --_netContentSyncCountdown <= 0)
            {
                SaveNetContent();
                Session.SendTrawlingNetContentPacketSetting(Block.EntityId, NetContent);
            }
        }

        void SaveSettings()
        {
            if (Block == null)
                return; // called too soon or after it was already closed, ignore

            if (Settings == null)
                throw new NullReferenceException($"Settings == null on entId={Entity?.EntityId}; modInstance={Session != null}");

            if (MyAPIGateway.Utilities == null)
                throw new NullReferenceException($"MyAPIGateway.Utilities == null; entId={Entity?.EntityId}; modInstance={Session != null}");

            if (Block.Storage == null)
                Block.Storage = new MyModStorageComponent();

            Block.Storage.SetValue(StorageKeys.FISHATWARSETTINGS, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Settings)));

        }



        public override bool IsSerialized()
        {
            // called when the game iterates components to check if they should be serialized, before they're actually serialized.
            // this does not only include saving but also streaming and blueprinting.
            // NOTE for this to work reliably the MyModStorageComponent needs to already exist in this block with at least one element.

            try
            {
                SaveSettings();
                SaveNetContent();
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLine($"Error saving settings!\n{e}");
            }

            return base.IsSerialized();
        }

        public override void UpdateAfterSimulation10()
        {
            base.UpdateAfterSimulation();

            SyncSettings();
            SyncNetContent();
        }


        public override void UpdateAfterSimulation100()
        {

            if (count < 6)
            {
                count++;
                return;
            }

            if (!_fishCollector.IsFunctional && NetContent > 0)
            {
                NetContent = 0; // If the block is not functional, we assume the net is destroyed and all fish are lost.
                EnableFishing = false; // Disable fishing to prevent it from trying to catch more fish while broken
            }

            if (!_fishCollector.HasInventory || !_fishCollector.IsWorking || !EnableFishing)
                return;

            Vector3D worldPosition = _fishCollector.PositionComp.GetPosition();

            if ((worldPosition - MapUtilities.agarisCenter).LengthSquared() > (60000d * 60000d)) // A bit lighter math than distance
                return;

            var Agaris = MyGamePruningStructure.GetClosestPlanet(worldPosition);


            //GetClosestSurfacePoint
            var temp = WaterAPI.GetClosestSurfacePoint(worldPosition, Agaris);

            if ((temp-worldPosition).LengthSquared() < (50d * 50d) && MapUtilities.IsAtFishLocation(worldPosition))
            {

                float amount = _random.Next(1, 55);
                NetContent += amount * GetFishEfficiencySquared(_fishCollector.Physics.LinearVelocity.LengthSquared()); // Add a random amount of fish to the net content

                WaterAPI.CreateBubble(worldPosition, 2);

                
            }
            else
            {
                NetContent = Math.Max(0, NetContent - _random.Next(1, 10)); // If we're not at the surface or a fish location, decrease the net content to simulate fish escaping, with a minimum of 0.
            }
            count = 0;
        }

        public float GetFishEfficiencySquared(float speedSq)
        {
            // 1. Outside the operational bounds (0 to 625)
            if (speedSq <= 0 || speedSq >= SpeedHighSq)
                return 0;

            // 2. Maximum efficiency plateau (5m/s to 15m/s)
            if (speedSq >= SpeedLowSq && speedSq <= SpeedMidSq)
                return 1;

            // 3. Ramp up (0m/s to 5m/s)
            if (speedSq < SpeedLowSq)
            {
                // Parabolic ramp up from 0 to 1 as speedSq goes from 0 to SpeedLowSq
                return (float)(speedSq / SpeedLowSq);
            }

            // 4. Ramp down (15m/s to 25m/s)
            // speedSq is between 225 and 625
            float currentSpeed = (float)Math.Sqrt(speedSq);
            float efficiency = 1.0f - (currentSpeed - 15.0f) / 10.0f;

            return MathHelper.Clamp(efficiency, 0f, 1f);
        }

        private void TransferNetContentToInventory()
        {
            if (NetContent <= 0) return;

            IMyInventory inventory = _fishCollector.GetInventory();
            if (inventory == null) return;

            var fuelId = new MyDefinitionId(typeof(MyObjectBuilder_ConsumableItem), "Fish");
            var itemDefinition = MyDefinitionManager.Static.GetPhysicalItemDefinition(fuelId);

            // itemDefinition.Volume is in m3, inventory is in Liters. 
            // We convert m3 to Liters by multiplying by 1000.
            float volumePerFish = itemDefinition.Volume * 1000;

            // Calculate how many fish can actually fit
            MyFixedPoint maxPossibleFish = (MyFixedPoint)NetContent;
            float availableVolume = (float)(inventory.MaxVolume - inventory.CurrentVolume);
            float totalVolumeOfCatch = (float)maxPossibleFish * volumePerFish;

            if (totalVolumeOfCatch > availableVolume)
            {
                // Ensure we don't exceed what the inventory can hold
                maxPossibleFish = (MyFixedPoint)Math.Floor(availableVolume / volumePerFish);
            }

            if (maxPossibleFish > 0)
            {
                var content = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(fuelId);
                inventory.AddItems(maxPossibleFish, content);

                // Subtract only what was actually added from the net
                NetContent -= (float)maxPossibleFish;
            }
        }


    }
}

