using Sandbox.Game;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using VRageRender.Messages;
using static VRage.Game.MyObjectBuilder_BehaviorTreeDecoratorNode;

namespace PEPCO
{
    public class TrawlingNet_TerminalControls
    {

        const string IdPrefix = "FishAtWar_"; // highly recommended to tag your properties/actions like this to avoid colliding with other mods'

        static bool Done = false;
        private AaWFood_Session Session => AaWFood_Session.Instance; // helper to access the session component instance, which is where shared state and the network handler live

        public static void DoOnce(IMyModContext context)
        {
            if (Done)
                return;
            Done = true;

            CreateControls();
            CreateActions(context);
            CreateProperties();
        }

        static bool CustomVisibleCondition(IMyTerminalBlock b)
        {
            // only visible for the blocks having this gamelogic comp
            return b?.GameLogic?.GetAs<FishCollectorComponent>() != null;
        }


        static void CreateControls()
        {
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyFunctionalBlock>(""); // separators don't store the id
                c.SupportsMultipleBlocks = false;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyFunctionalBlock>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyFunctionalBlock>(IdPrefix + "MainLabel");
                c.Label = MyStringId.GetOrCompute("Net Settings");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddControl<IMyFunctionalBlock>(c);
            }


            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyFunctionalBlock>(IdPrefix + "EnableFishing");
                c.Title = MyStringId.GetOrCompute("Deploy fishing net");
                c.Tooltip = MyStringId.GetOrCompute("Allows you to set or haul in the fishing net.");
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;

                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;

                c.Getter = (b) =>
                {
                    var logic = b?.GameLogic?.GetAs<FishCollectorComponent>();
                    if (logic == null)
                        return false; // default to false if no logic is found
                    return logic.EnableFishing;
                };
                c.Setter = (b, v) =>
                {
                    var logic = b?.GameLogic?.GetAs<FishCollectorComponent>();
                    if (logic == null)
                        return;
                    logic.EnableFishing = v;
                };

                MyAPIGateway.TerminalControls.AddControl<IMyFunctionalBlock>(c);
            }

            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyFunctionalBlock>(IdPrefix + "Help");
                c.Title = MyStringId.GetOrCompute("Help");
                c.Tooltip = MyStringId.GetOrCompute("Sends you to the steam help page.");
                c.SupportsMultipleBlocks = false;
                c.Visible = CustomVisibleCondition;

                c.Action = (b) =>
                {

                    MyVisualScriptLogicProvider.OpenSteamOverlayLocal("https://steamcommunity.com/sharedfiles/filedetails/?id=3680484848");
                };

                MyAPIGateway.TerminalControls.AddControl<IMyFunctionalBlock>(c);
            }

        }
        static void CreateActions(IMyModContext context)
        {
            {
                var a = MyAPIGateway.TerminalControls.CreateAction<IMyFunctionalBlock>(IdPrefix + "ToggleEnableFishingAction");

                a.Name = new StringBuilder("Toggle fishing");
                a.ValidForGroups = true;
                a.Icon = @"Textures\GUI\Icons\Actions\CharacterToggle.dds";
                a.Action = (b) => {
                    var logic = b?.GameLogic?.GetAs<FishCollectorComponent>();
                    if (logic == null) return;
                    logic.EnableFishing ^= true; // toggle the show resources setting


                };
                a.Writer = (b, sb) =>
                {
                    var logic = b?.GameLogic?.GetAs<FishCollectorComponent>();
                    if (logic == null) return;
                    sb.Append($"Fishing\n{(logic.EnableFishing ? "On" : "Off")}");
                };
                a.Enabled = CustomVisibleCondition;

                MyAPIGateway.TerminalControls.AddAction<IMyFunctionalBlock>(a);
            }



        }

        static void CreateProperties()
        {
            {
                var p = MyAPIGateway.TerminalControls.CreateProperty<float, IMyFunctionalBlock>(IdPrefix + "NetContent");
                // SupportsMultipleBlocks, Enabled and Visible don't have a use for this, and Title/Tooltip don't exist.

                p.Getter = (b) => {
                    var logic = b?.GameLogic?.GetAs<FishCollectorComponent>();
                    if (logic == null) return -1f;
                    
                    return logic.NetContent;
                };

                p.Setter = (b, v) =>
                {
                };

                MyAPIGateway.TerminalControls.AddControl<IMyFunctionalBlock>(p);


                //a mod or PB can use it like:
                //float netConent = block.GetValue<float>("FishAtWar_NetContent");
            }
        }
    }
}
