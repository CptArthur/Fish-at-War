using ProtoBuf;
using VRageMath;
using Digi.NetworkLib;

namespace PEPCO
{
    [ProtoContract]
    public class TrawlingNet_ContentPacket : PacketBase
    {
        public TrawlingNet_ContentPacket() { } // Empty constructor required for deserialization

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
        public static event ReceiveDelegate<TrawlingNet_ContentPacket> OnReceive;

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


        /// <summary>
        /// The subtype of the content, for example "Fish", in the future I hope Enenra also adds Lobsters 🦞
        /// </summary>
        [ProtoMember(2)]
        public string NetContentSubtypeId;

        /// <summary>
        /// Defaults to false, if true the net is to be emptied and the content needs to be transfered to inventory on server and clients - I think?
        /// </summary>
        [ProtoMember(3)]
        public bool EmptyNet;


        /// <summary>
        /// Whether the net is currently in a fish location, used to determine if it should be catching fish or not
        /// </summary>
        [ProtoMember(4)]
        public bool IsInFishLocation;


        /// <summary>
        /// The current speed of the boat squared for easier comparison with the max speed squared, used to determine catch efficiency
        /// </summary>
        [ProtoMember(5)]
        public float LastSpeedSq;


        /// <summary>
        /// The number of fish caught in the last catch attempt
        /// </summary>
        [ProtoMember(6)]
        public float LastCaught;
    }

}
