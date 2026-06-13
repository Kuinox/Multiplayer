using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public static class LiteNetDiagnostics
    {
        private static long sequence;

        public static string Next(string source, string message)
        {
            var id = Interlocked.Increment(ref sequence);
            return $"[LNL-DIAG #{id} {DateTime.UtcNow:O} T{Thread.CurrentThread.ManagedThreadId} {source}] {message}";
        }

        public static string Peer(NetPeer peer)
        {
            if (peer == null) return "peer=null";

            try
            {
                var stats = peer.Statistics;
                return $"peerId={peer.Id} remoteId={peer.RemoteId} endpoint={peer} " +
                       $"netState={peer.ConnectionState} ping={peer.Ping}ms rtt={peer.RoundTripTime}ms " +
                       $"lastPacket={peer.TimeSinceLastPacket:F3}ms mtu={peer.Mtu} " +
                       $"recv={stats.PacketsReceived}/{stats.BytesReceived}B " +
                       $"sent={stats.PacketsSent}/{stats.BytesSent}B " +
                       $"loss={stats.PacketLoss}({stats.PacketLossPercent}%)";
            }
            catch (Exception e)
            {
                return $"peer={peer} diagnosticsError={e.GetType().Name}:{e.Message}";
            }
        }

        public static string Disconnect(NetPeer peer, DisconnectInfo info)
        {
            var additionalBytes = info.AdditionalData?.AvailableBytes ?? 0;
            return $"{Peer(peer)} reason={info.Reason} socketError={info.SocketErrorCode} " +
                   $"additionalBytes={additionalBytes}";
        }
    }

    public class MpServerNetListener(MultiplayerServer server, bool arbiter) : INetEventListener
    {
        public void OnConnectionRequest(ConnectionRequest req)
        {
            if (server.playerManager.OnPreConnect(req.RemoteEndPoint.Address) is { } disconnectReason)
            {
                ServerLog.Log(LiteNetDiagnostics.Next(
                    "server/request",
                    $"reject endpoint={req.RemoteEndPoint} reason={disconnectReason} arbiter={arbiter}"));
                req.Reject(new ServerDisconnectPacket { reason = disconnectReason }.Serialize().data);
                return;
            }

            ServerLog.Log(LiteNetDiagnostics.Next(
                "server/request",
                $"accept endpoint={req.RemoteEndPoint} arbiter={arbiter}"));
            req.Accept();
        }

        public void OnPeerConnected(NetPeer peer)
        {
            var conn = new LiteNetConnection(peer);
            conn.ChangeState(ConnectionStateEnum.ServerJoining);
            peer.SetConnection(conn);

            var player = server.playerManager.OnConnected(conn);
            ServerLog.Log(LiteNetDiagnostics.Next(
                "server/connected",
                $"{LiteNetDiagnostics.Peer(peer)} playerId={player.id} arbiter={arbiter}"));
            if (arbiter)
            {
                player.type = PlayerType.Arbiter;
                player.color = new ColorRGB(128, 128, 128);
            }
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            ConnectionBase conn = peer.GetConnection();
            ServerLog.Log(LiteNetDiagnostics.Next(
                "server/disconnected",
                $"{LiteNetDiagnostics.Disconnect(peer, disconnectInfo)} appState={conn.State} " +
                $"playerId={conn.serverPlayer?.id.ToString() ?? "null"} username={conn.username ?? "null"} " +
                $"managerRunning={peer.NetManager.IsRunning} managerPeers={peer.NetManager.ConnectedPeersCount} " +
                $"managerStats={peer.NetManager.Statistics.ToSingleLineDebugString()}"));
            var reason = disconnectInfo.Reason switch
            {
                // we (the server) closed the connection
                DisconnectReason.DisconnectPeerCalled => MpDisconnectReason.ClientLeft,
                // the client closed the connection
                DisconnectReason.RemoteConnectionClose => MpDisconnectReason.ClientLeft,
                _ => MpDisconnectReason.NetFailed
            };
            if (reason != MpDisconnectReason.ClientLeft)
                ServerLog.Log($"Peer {conn} disconnected unexpectedly: " +
                              $"{disconnectInfo.Reason}/{disconnectInfo.SocketErrorCode}");
            server.playerManager.SetDisconnected(conn, reason);
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            peer.GetConnection().Latency = latency;
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod method)
        {
            byte[] data = reader.GetRemainingBytes();
            peer.GetConnection().serverPlayer.HandleReceive(new ByteReader(data), method == DeliveryMethod.ReliableOrdered);
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            ServerLog.Error(LiteNetDiagnostics.Next(
                "server/network-error",
                $"endpoint={endPoint} socketError={socketError} arbiter={arbiter}"));
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    }
}
