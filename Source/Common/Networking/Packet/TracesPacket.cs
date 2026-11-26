namespace Multiplayer.Common.Networking.Packet;

[PacketDefinition(Packets.Client_Traces, allowFragmented: true)]
public record struct ClientTracesPacket : IPacket
{
    public int playerId;
    public byte[] rawTraces;
    public byte[] rawJittedMethods;
    // The host's pawn state and pawn float history, to compare with the desynced client's
    public byte[] rawPawnState;
    public byte[] rawPawnFloats;

    public void Bind(PacketBuffer buf)
    {
        buf.Bind(ref playerId);
        buf.BindBytes(ref rawTraces);
        buf.BindBytes(ref rawJittedMethods);
        buf.BindBytes(ref rawPawnState);
        buf.BindBytes(ref rawPawnFloats);
    }
}

[PacketDefinition(Packets.Server_Traces, allowFragmented: true)]
public record struct ServerTracesPacket : IPacket
{
    public enum Mode : byte
    {
        Request, Transfer
    }

    public Mode mode;

    public int tick;
    public int diffAt;
    public int playerId;

    // Used in transfer only
    public byte[] rawTraces;
    public byte[] rawJittedMethods;
    public byte[] rawPawnState;
    public byte[] rawPawnFloats;

    public static ServerTracesPacket Request(int tick, int diffAt, int playerId) => new()
    {
        mode = Mode.Request,
        tick = tick,
        diffAt = diffAt,
        playerId = playerId
    };

    public static ServerTracesPacket Transfer(ClientTracesPacket packet) =>
        new()
        {
            mode = Mode.Transfer,
            rawTraces = packet.rawTraces,
            rawJittedMethods = packet.rawJittedMethods,
            rawPawnState = packet.rawPawnState,
            rawPawnFloats = packet.rawPawnFloats
        };

    public void Bind(PacketBuffer buf)
    {
        buf.BindEnum(ref mode);
        if (mode == Mode.Request)
        {
            buf.Bind(ref tick);
            buf.Bind(ref diffAt);
            buf.Bind(ref playerId);
        }
        else
        {
            buf.BindBytes(ref rawTraces);
            buf.BindBytes(ref rawJittedMethods);
            buf.BindBytes(ref rawPawnState);
            buf.BindBytes(ref rawPawnFloats);
        }
    }
}
