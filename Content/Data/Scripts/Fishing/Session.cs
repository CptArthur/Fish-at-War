using Jakaria.API;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using System;
using VRage.Game;
using VRage.Game.Components;
using VRage.Utils;
using Digi.NetworkLib;
using static PEPCO.ScriptHelpers;
using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;
using VRage.ModAPI; // required for MyTransparentGeometry/MySimpleObjectDraw to be able to set blend type.

namespace PEPCO
{
    // This object is always present, from the world load to world unload.
    // NOTE: all clients and server run mod scripts, keep that in mind.
    // NOTE: this and gamelogic comp's update methods run on the main game thread, don't do too much in a tick or you'll lower sim speed.
    // NOTE: also mind allocations, avoid realtime allocations, re-use collections/ref-objects (except value types like structs, integers, etc).
    //
    // The MyUpdateOrder arg determines what update overrides are actually called.
    // Remove any method that you don't need, none of them are required, they're only there to show what you can use.
    // Also remove all comments you've read to avoid the overload of comments that is this file.
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class AaWFood_Session : MySessionComponentBase
    {
        public static AaWFood_Session Instance; // the only way to access session comp from other classes and the only accepted static field.

        // Network and network packets
        public const ushort NETWORK_ID = (ushort)(3680484848 % ushort.MaxValue); // Using the prod workshopId of the mod, which is 3680484848
        public Network Net;
        private TrawlingNet_SettingsPacket _settingsPacket;
        private TrawlingNet_ContentPacket _contentPacket;


        public override void LoadData()
        {
            // amogst the earliest execution points, but not everything is available at this point.

            // These can be used anywhere, not just in this method/class:
            // MyAPIGateway. - main entry point for the API
            // MyDefinitionManager.Static. - reading/editing definitions
            // MyGamePruningStructure. - fast way of finding entities in an area
            // MyTransparentGeometry. and MySimpleObjectDraw. - to draw sprites (from TransparentMaterials.sbc) in world (they usually live a single tick)
            // MyVisualScriptLogicProvider. - mainly designed for VST but has its uses, use as a last resort.
            // System.Diagnostics.Stopwatch - for measuring code execution time.
            // ...and many more things, ask in #programming-modding in keen's discord for what you want to do to be pointed at the available things to use.

            Instance = this;

            WaterAPI.LoadData();

            SetupNetworkAndPackets(); 
        }

        private void SetupNetworkAndPackets()
        {
            Net = new Network(NETWORK_ID, ModContext.ModName);
            Net.ExceptionHandler = (e) => MyLog.Default.WriteLine($"Network exception: {e}");
            Net.ErrorHandler = (msg) => MyLog.Default.WriteLine($"Network error: {msg}");

            Net.SerializeTest = true;

            _settingsPacket = new TrawlingNet_SettingsPacket();
            _contentPacket = new TrawlingNet_ContentPacket();

            TrawlingNet_SettingsPacket.OnReceive += TrawlingNetSettingsPacketReceived;
            TrawlingNet_ContentPacket.OnReceive += TrawlingNetContentPacketReceived;
        }

        // Used by the logic components to sync settings to the server
        public void SendTrawlingNetSettingsPacketSetting(long entityId, TrawlingNetSettings settings)
        {
            _settingsPacket.Setup(entityId, settings);
            Net.SendToServer(_settingsPacket);
        }

        public void SendTrawlingNetContentPacketSetting(long entityId, TrawlingNetContent content)
        {
            _contentPacket.Setup(entityId, content);
            Net.SendToServer(_contentPacket);
        }


        void TrawlingNetSettingsPacketReceived(TrawlingNet_SettingsPacket packet, ref PacketInfo packetInfo, ulong senderSteamId)
        {
            IMyEntity ent = MyEntities.GetEntityById(packet.EntityId);
            if (ent == null)
            {
                // log some error if this is unexpected, but do remember that clients do NOT have all entities available to them, only server does.
                return;
            }

            // from here if you have a gamelogic component on that entity you can do something like:
            var logic = ent.GameLogic?.GetAs<FishCollectorComponent>();
            if (logic == null)
            {
                return;
            }

            // Set the settings on the gamelogic here
            logic.UpdateSettingsFromInput(packet.PacketSettings);

            // to see how this works in practice, try it in both singleplayer (you're the server) and as a MP client in a dedicated server (you can start one from steam tools).
            packetInfo.Relay = RelayMode.ToEveryone;

        }

        void TrawlingNetContentPacketReceived(TrawlingNet_ContentPacket packet, ref PacketInfo packetInfo, ulong senderSteamId)
        {
            LogDebug($"AQD_LG_TrawlingNet Session: TrawlingNetContentPacketReceived; EntityId={packet.EntityId}; NetContent={packet.PacketContent?.NetContent}; EmptyNet={packet.PacketContent?.EmptyNet}; SubtypeId={packet.PacketContent?.NetContentSubtypeId}; sender={senderSteamId}");

            IMyEntity ent = MyEntities.GetEntityById(packet.EntityId);
            if (ent == null)
            {
                // LogDebug($"AQD_LG_TrawlingNet Session: TrawlingNetContentPacketReceived: entity not found for EntityId={packet.EntityId}");
                // log some error if this is unexpected, but do remember that clients do NOT have all entities available to them, only server does.
                return;
            }
            var logic = ent.GameLogic?.GetAs<FishCollectorComponent>();
            if (logic == null)
            {
                LogDebug($"AQD_LG_TrawlingNet Session: TrawlingNetContentPacketReceived: FishCollectorComponent not found on entity EntityId={packet.EntityId}");
                return;
            }

            LogDebug($"AQD_LG_TrawlingNet Session: TrawlingNetContentPacketReceived: calling UpdateNetContentFromInput; EntityId={packet.EntityId}");
            // Set the content on the gamelogic here
            logic.UpdateNetContentFromInput(packet.PacketContent);

            packetInfo.Relay = RelayMode.ToEveryone;
        }

        public override void BeforeStart()
        {
            // executed before the world starts updating
            
        }

        protected override void UnloadData()
        {
            // always catch errors here because throwing them will NOT crash the game and instead prevent other mods from unloading properly, causing all sorts of hidden issues...
            try
            {
                // executed when world is exited to unregister events and stuff.
            }
            catch (Exception e)
            {
                MyLog.Default.Error(e.ToString());
            }
            finally
            {
                Instance = null; // important for avoiding this instance and all its references to remain allocated in memory
            }
        }

        public override void HandleInput()
        {
            // gets called 60 times a second before all other update methods, regardless of framerate, game pause or MyUpdateOrder.
        }

        public override void UpdateBeforeSimulation()
        {
            // executed every tick, 60 times a second, before physics simulation and only if game is not paused.
        }

        public override void Simulate()
        {
            // executed every tick, 60 times a second, during physics simulation and only if game is not paused.
            // NOTE in this example this won't actually be called because of the lack of MyUpdateOrder.Simulation argument in MySessionComponentDescriptor
        }

        public override void UpdateAfterSimulation()
        {
            // executed every tick, 60 times a second, after physics simulation and only if game is not paused.

            try // example try-catch for catching errors and notifying player, use only for non-critical code!
            {
                // ...
            }
            catch (Exception e) // NOTE: never use try-catch for code flow or to ignore errors! catching has a noticeable performance impact.
            {
                MyLog.Default.WriteLineAndConsole(e.ToString());

                if (MyAPIGateway.Session?.Player != null)
                    MyAPIGateway.Utilities.ShowNotification($"[ ERROR: {GetType().FullName}: {e.Message} | Send SpaceEngineers.Log to mod author ]", 10000, MyFontEnum.Red);
            }
        }

        public override void Draw()
        {
            // gets called 60 times a second after all other update methods, regardless of framerate, game pause or MyUpdateOrder.
            // NOTE: this is the only place where the camera matrix (MyAPIGateway.Session.Camera.WorldMatrix) is accurate, everywhere else it's 1 frame behind.
        }

        public override void SaveData()
        {
            // executed AFTER world was saved
        }

        public override MyObjectBuilder_SessionComponent GetObjectBuilder()
        {
            // executed during world save, most likely before entities.

            return base.GetObjectBuilder(); // leave as-is.
        }

        public override void UpdatingStopped()
        {
            // executed when game is paused
        }
    }
}
