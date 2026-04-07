using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Utils;

namespace PEPCO // Change this to your actual namespace
{
    public class TaskScheduler
    {
        private class ScheduledTask
        {
            public Action Action;
            public int Interval;
            public int Offset;
            public bool ClientOnly;
        }

        private readonly Dictionary<Action, ScheduledTask> _tasks = new Dictionary<Action, ScheduledTask>();
        private int _counter = 0;
        private readonly bool _isDedicated;

        public TaskScheduler(bool isDedicatedServer)
        {
            _isDedicated = isDedicatedServer;
        }

        /// <summary>
        /// Registers a method to run at a specific tick frequency.
        /// Offset is calculated automatically to balance CPU load.
        /// </summary>
        public void Register(Action action, int interval, bool clientOnly = false)
        {
            if (action == null || _tasks.ContainsKey(action)) return;

            // Calculate offset based on existing tasks with the same interval
            int sameIntervalCount = _tasks.Values.Count(t => t.Interval == interval);

            _tasks.Add(action, new ScheduledTask
            {
                Action = action,
                Interval = interval,
                Offset = sameIntervalCount % interval,
                ClientOnly = clientOnly
            });
        }

        public void Unregister(Action action)
        {
            if (action != null) _tasks.Remove(action);
        }

        /// <summary>
        /// Must be called in the block's Update loop.
        /// </summary>
        public void Tick()
        {
            _counter++;

            // Use ToList() to allow tasks to Unregister themselves during execution without crashing
            foreach (var task in _tasks.Values.ToList())
            {
                if (task.ClientOnly && _isDedicated) continue;

                if (_counter % task.Interval == task.Offset)
                {
                    try
                    {
                        task.Action.Invoke();
                    }
                    catch (Exception e)
                    {
                        // In Space Engineers, it's better to catch here so one 
                        // failing task doesn't stop the whole script.
                        // Optional: Log error or Unregister the failing task.
                        Utilities.Log.Error($"Error executing scheduled task: {e.Message}. Tell UZAR");
                    }
                }
            }

            // Reset periodically
            if (_counter > 18000) _counter = 0;
        }

        public void Dispose()
        {
            // Clear the dictionary to break all references to the block's methods
            _tasks.Clear();
        }
    }
}