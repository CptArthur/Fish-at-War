using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System.Collections.Generic;
using System.Text;
using VRage;
using VRage.Game.Components;
using VRage.Utils;
using PEPCO.ObjectBuilders;
// Make sure to reference your ObjectBuilder namespace

namespace PEPCO.Events
{
    // [TEMPLATE INSTRUCTION 3]: Link this to your ObjectBuilder class name
    [MyComponentBuilder(typeof(ObjectBuilder_NetContent))]
    [MyComponentType(typeof(NetContentCheck_EventLogic))]
    [MyEntityDependencyType(typeof(IMyEventControllerBlock))]
    public class NetContentCheck_EventLogic : MyEventProxyEntityComponent, IMyEventComponentWithGui
    {

        private readonly HashSet<IMyTerminalBlock> _monitoredBlocks = new HashSet<IMyTerminalBlock>();

        // A shortcut to talk to the Event Controller block this script is attached to.
        private IMyEventControllerBlock Block => Entity as IMyEventControllerBlock;

        public override string ComponentTypeDebugString => nameof(NetContentCheck_EventLogic);

        // --- UI REGISTRATION ---

        // [TEMPLATE INSTRUCTION 4]: 
        // This ID MUST be a unique random number. If two mods use the same number, the UI breaks.
        public long UniqueSelectionId => 36804848481; // Used the workshop ID and a 1 at the end for good measure, but you can generate your own random long number if you want.

        // [TEMPLATE INSTRUCTION 5]: 
        // The display name of your event in the drop-down menu. 
        // (You can hardcode a string here or use MyStringId for localization)
        public MyStringId EventDisplayName => MyStringId.GetOrCompute("Fishing Net Content");

        // --- NEW TOOLBAR DESCRIPTIONS ---

        // Tooltip shown when hovering over Slot 1 (Condition is TRUE)
        public string YesNoToolbarYesDescription => "Action executed when condition is met";

        // Tooltip shown when hovering over Slot 2 (Condition is FALSE)
        public string YesNoToolbarNoDescription => "Action executed when condition is no longer met";

        // --- EVENT CONTROLLER UI CONFIGURATION ---

        // Set to true if your event checks against a specific number (e.g. "Cargo > 50%")
        public bool IsThresholdUsed => true;

        // Set to true to show the "Equal or Greater / Equal or Less" dropdown
        public bool IsConditionSelectionUsed => true;

        // Set to true if you want players to select specific blocks on the right side of the UI
        public bool IsBlocksListUsed => true;

        // --- STATE MANAGEMENT ---

        private bool _isSelected;
        private bool _previousState; // Tracks what the state was on the last tick to prevent spam
        private int _tickCount;

        // The game automatically calls this when the player selects your event in the dropdown menu.
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;

                if (Block == null) return;

                // If selected, we tell the grid to start running our Update() method every frame.
                if (_isSelected)
                    ((MyCubeGrid)Block.CubeGrid).Schedule(MyCubeGrid.UpdateQueue.BeforeSimulation, Update);
                else
                    ((MyCubeGrid)Block.CubeGrid).DeSchedule(MyCubeGrid.UpdateQueue.BeforeSimulation, Update);
            }
        }

        public override void OnBeforeRemovedFromContainer()
        {
            base.OnBeforeRemovedFromContainer();
            if (IsSelected) ((MyCubeGrid)Block.CubeGrid).DeSchedule(MyCubeGrid.UpdateQueue.BeforeSimulation, Update);
        }

        // --- THE ACTUAL LOGIC LOOP ---

        private void Update()
        {
            // [TEMPLATE INSTRUCTION 6]: 
            // Performance throttle. Right now, this logic runs every 60 frames (1 second).
            if (++_tickCount < 60) return;
            _tickCount = 0;

            EvaluateCondition();
        }

        private void EvaluateCondition()
        {
            if (Block == null || !Block.IsWorking) return;

            // If the player hasn't added any blocks to the list yet, don't trigger anything.
            if (_monitoredBlocks.Count == 0) return;

            // Check if the player selected "AND" or "OR" in the Event Controller UI
            bool isAndMode = Block.IsAndModeEnabled;

            // --- NEW: Read the UI settings ---
            // Threshold is a float from 0.0 to 1.0 (representing 0% to 100% on the slider)
            float threshold = Block.Threshold;
            bool isLowerOrEqual = Block.IsLowerOrEqualCondition;

            // If AND mode, we assume true until a block fails. 
            // If OR mode, we assume false until a block succeeds.
            bool currentState = isAndMode;

            // Loop through all the blocks the player selected
            foreach (var block in _monitoredBlocks)
            {
                var fishComp = block.GameLogic?.GetAs<FishCollectorComponent>();
                if (fishComp == null) continue;

                // --- CUSTOM MATH CONDITION ---
                // Grab the current value in percentage (0.0 to 1.0) from the FishCollectorComponent and compare it to the threshold using the selected condition.
                float currentValue = fishComp.NetContentPercentage / 100;

                bool blockMeetsCondition = false;

                if (isLowerOrEqual)
                {
                    // "Equal or Less" selected in UI
                    blockMeetsCondition = currentValue <= threshold;
                }
                else
                {
                    // "Equal or Greater" selected in UI
                    blockMeetsCondition = currentValue >= threshold;
                }

                if (isAndMode)
                {
                    // AND MODE (ALL blocks must meet the condition)
                    // If even one block fails, the whole event fails. We can stop checking.
                    if (!blockMeetsCondition)
                    {
                        currentState = false;
                        break;
                    }
                }
                else
                {
                    // OR MODE (ANY block must meet the condition)
                    // If even one block succeeds, the whole event succeeds. We can stop checking.
                    if (blockMeetsCondition)
                    {
                        currentState = true;
                        break;
                    }
                }
            }

            // Trigger the slots if the state changed
            if (currentState == _previousState) return;

            if (currentState)
            {
                Block.TriggerAction(0); // Condition Met (Slot 1)
            }
            else
            {
                Block.TriggerAction(1); // Condition Not Met (Slot 2)
            }

            _previousState = currentState;
        }

        // --- BLOCK LIST MANAGEMENT (If IsBlocksListUsed = true) ---

        // The game calls this to see if a block is allowed to be added to the right-hand list.
        public bool IsBlockValidForList(IMyTerminalBlock b)
        {
            // only visible for the blocks having this gamelogic comp
            return b?.GameLogic?.GetAs<FishCollectorComponent>() != null;
        }

        public void AddBlocks(List<IMyTerminalBlock> blocks)
        {
            foreach (var block in blocks)
            {
                // HashSet.Add returns true if it was successfully added (not a duplicate)
                if (_monitoredBlocks.Add(block))
                {
                    // IMPORTANT: Unsubscribe if the block is destroyed or ground down
                    block.OnClosing += OnBlockClosing;
                }
            }
        }

        public void RemoveBlocks(IEnumerable<IMyTerminalBlock> blocks)
        {
            foreach (var block in blocks)
            {
                if (_monitoredBlocks.Remove(block))
                {
                    block.OnClosing -= OnBlockClosing;
                }
            }
        }

        private void OnBlockClosing(VRage.ModAPI.IMyEntity entity)
        {
            var block = entity as IMyTerminalBlock;
            if (block != null)
            {
                _monitoredBlocks.Remove(block);
                block.OnClosing -= OnBlockClosing;
            }
        }

        // --- TERMINAL INFO TEXT ---

        // This draws the text in the bottom right corner of the terminal UI.
        public void UpdateDetailedInfo(StringBuilder info, int slot, long entityId, bool value)
        {
            info.AppendLine($"--- {MyTexts.GetString(EventDisplayName)} ---");
            info.AppendLine($"Current State: {(_previousState ? "Triggered" : "Not Triggered")}");
            info.AppendLine($"Action Fired: Slot {slot + 1}");
        }

        // You must include this so the Event Controller knows your UI controls exist, 
        // even if you don't add custom sliders (like the Weather event did).
        public void CreateTerminalInterfaceControls<T>() where T : IMyTerminalBlock { }
        public void NotifyValuesChanged() { }
    }
}