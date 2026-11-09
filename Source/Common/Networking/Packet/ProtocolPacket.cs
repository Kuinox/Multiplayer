namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Server_ProtocolOk)]
public record struct ServerProtocolOkPacket(bool hasPassword, bool isStandaloneServer = false) : IPacket
{
    public bool hasPassword = hasPassword;
    public bool isStandaloneServer = isStandaloneServer;
    public float autosaveInterval;
    public AutosaveUnit autosaveUnit;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref hasPassword);
        buf.Bind(ref isStandaloneServer);
        buf.Bind(ref autosaveInterval);
        buf.BindEnum(ref autosaveUnit);
    }
}


[PacketDefinition(Packets.Client_Protocol)]
public record struct ClientProtocolPacket(int protocolVersion, string? gitHash = null) : IPacket
{
    public int protocolVersion = protocolVersion;
    // Optional trailing field: older clients don't send it, so it's only read when data remains.
    public string? gitHash = gitHash;

    public static ClientProtocolPacket Current() => new(MpVersion.Protocol, MpVersion.GitHash);

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref protocolVersion);
        if (buf.isWriting ? gitHash != null : buf.DataRemaining)
            buf.Bind(ref gitHash!);
    }
}
