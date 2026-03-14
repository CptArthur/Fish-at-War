using ProtoBuf;
using Digi.NetworkLib;
using AaWFoodScript;


namespace Digi.NetworkLib
{
    [ProtoInclude(10, typeof(TrawlingNetSettingsPacket))]
    [ProtoInclude(11, typeof(TrawlingNetContentPacket))]

    public abstract partial class PacketBase
    {
    }
}