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

        [ProtoMember(3)]
        public TrawlingNetContent PacketContent;

        public void Setup(long entityId, TrawlingNetContent packetContent)
        {
            // Ensure you assign ALL the protomember fields here to avoid problems.
            EntityId = entityId;
            PacketContent = packetContent;
        }

        // Alternative way of handling the data elsewhere.
        // Or you can handle it in the Received() method below and remove this event, up to you.
        public static event ReceiveDelegate<TrawlingNetContentPacket> OnReceive;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            OnReceive?.Invoke(this, ref packetInfo, senderSteamId);
        }
    }

    [ProtoContract]
    public class TrawlingNetContent
    {

        /// <summary>
        /// Content of the trawling net
        /// </summary>
        [ProtoMember(1)]
        public float NetContent;

    }

}
