using LiteNetLib;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class LiteNetConnection(NetPeer peer) : ConnectionBase
    {
        public readonly NetPeer peer = peer;
        private readonly LiteNetDiagnostics.PacketHistory packetHistory = new(25);

        public string PacketHistory => packetHistory.ToDebugString();

        protected override void SendRaw(byte[] raw, bool reliable)
        {
            packetHistory.Record("emitted", raw, 0, raw.Length, reliable);

            if (peer.ConnectionState == ConnectionState.Connected)
                peer.Send(raw, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
            else
                ServerLog.Error($"SendRaw() called with invalid connection state ({peer}): {peer.ConnectionState}");
        }

        public override void HandleReceiveRaw(ByteReader data, bool reliable)
        {
            packetHistory.Record("received", data.GetBuffer(), data.Position, data.Left, reliable);
            base.HandleReceiveRaw(data, reliable);
        }

        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
            ServerLog.Log(LiteNetDiagnostics.Next(
                "connection/close",
                $"{LiteNetDiagnostics.Peer(peer)} appState={State} username={username ?? "null"} " +
                $"goodbyeReason={(goodbye.HasValue ? goodbye.Value.reason.ToString() : "none")} {PacketHistory}"));
            if (goodbye.HasValue)
                peer.Disconnect(goodbye.Value.Serialize().data);
            else
                peer.Disconnect();
        }

        public override void OnKeepAliveArrived(bool idMatched)
        {
            // Latency already handled by LiteNetLib. This can be as low as 0ms because LNL spawns its own thread for
            // receiving packets and immediately processes its own internal keep alive packet (called Ping-Pong).
            // This is handled only on the server-side in MpServerNetListener
        }

        public override string ToString()
        {
            return $"NetConnection ({peer}) ({username})";
        }
    }
}
