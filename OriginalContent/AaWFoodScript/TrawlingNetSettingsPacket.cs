using ProtoBuf;
using VRageMath;
using Digi.NetworkLib;

namespace AaWFoodScript
{
    [ProtoContract]
    public class TrawlingNetSettingsPacket : PacketBase
    {
        public TrawlingNetSettingsPacket() { } // Empty constructor required for deserialization

        [ProtoMember(1)]
        public long EntityId;

        [ProtoMember(2)]
        public TrawlingNetSettings PacketSettings;

        public void Setup(long entityId, TrawlingNetSettings packetSettings)
        {
            // Ensure you assign ALL the protomember fields here to avoid problems.
            EntityId = entityId;
            PacketSettings = packetSettings;
        }

        // Alternative way of handling the data elsewhere.
        // Or you can handle it in the Received() method below and remove this event, up to you.
        public static event ReceiveDelegate<TrawlingNetSettingsPacket> OnReceive;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            OnReceive?.Invoke(this, ref packetInfo, senderSteamId);
        }
    }

    [ProtoContract]
    public class TrawlingNetSettings
    {

        /// <summary>
        /// Toggle fishing on/off.
        /// </summary>
        [ProtoMember(1)]
        public bool EnableFishing;

    }

    }
