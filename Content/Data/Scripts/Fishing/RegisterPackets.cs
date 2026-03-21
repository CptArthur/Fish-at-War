using ProtoBuf;
using Digi.NetworkLib;
using PEPCO;


namespace Digi.NetworkLib
{
    [ProtoInclude(10, typeof(TrawlingNet_SettingsPacket))]
    [ProtoInclude(11, typeof(TrawlingNet_ContentPacket))]

    public abstract partial class PacketBase
    {
    }
}