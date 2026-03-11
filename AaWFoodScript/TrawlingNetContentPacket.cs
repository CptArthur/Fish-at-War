using ProtoBuf;
using VRageMath;
using Digi.NetworkLib;

namespace AaWFoodScript
{
    [ProtoContract]
    public class TrawlingNetContentPacket : PacketBase
    {
        public TrawlingNetContentPacket() { } // Empty constructor required for deserialization

        [ProtoMember(1)]
        public long EntityId;

        [ProtoMember(2)]
        public float NetContent;

        public void Setup(long entityId, float netContent)
        {
            // Ensure you assign ALL the protomember fields here to avoid problems.
            EntityId = entityId;
            NetContent = netContent;
        }

        // Alternative way of handling the data elsewhere.
        // Or you can handle it in the Received() method below and remove this event, up to you.
        public static event ReceiveDelegate<TrawlingNetContentPacket> OnReceive;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            OnReceive?.Invoke(this, ref packetInfo, senderSteamId);
        }
    }


    }
