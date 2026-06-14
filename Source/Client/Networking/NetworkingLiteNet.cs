using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Verse;

namespace Multiplayer.Client.Networking
{
    public class ClientLiteNetConnection : LiteNetConnection, ITickableConnection
    {
        private readonly NetManager netManager;

        private ClientLiteNetConnection(NetPeer peer, NetManager netManager) : base(peer) =>
            this.netManager = netManager;

        ~ClientLiteNetConnection()
        {
            if (netManager.IsRunning)
            {
                Log.Error("[ClientLiteNetConnection] NetManager did not get stopped");
                netManager.Stop();
            }
        }

        public static ClientLiteNetConnection Connect(string address, int port, string username)
        {
            var netClient = new NetManager(new NetListener(username))
            {
                EnableStatistics = true,
                IPv6Enabled = MpUtil.SupportsIPv6(),
                ReconnectDelay = 300,
                MaxConnectAttempts = 8
            };
            netClient.Start();
            var peer = netClient.Connect(address, port, "");
            var conn = new ClientLiteNetConnection(peer, netClient);
            peer.SetConnection(conn);
            MpLog.Log(LiteNetDiagnostics.Next(
                "client/connect",
                $"target={address}:{port} localPort={netClient.LocalPort} {LiteNetDiagnostics.Peer(peer)}"));
            return conn;
        }

        public void Tick() => netManager.PollEvents();

        public void OnDisconnect(SessionDisconnectInfo disconnectInfo)
        {
            if (State == ConnectionStateEnum.Disconnected) return;
            ConnectionStatusListeners.TryNotifyAll_Disconnected(disconnectInfo);
            Multiplayer.StopMultiplayer();
        }

        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
            MpLog.Log(LiteNetDiagnostics.Next(
                "client/close",
                $"{LiteNetDiagnostics.Peer(peer)} appState={State} " +
                $"goodbyeReason={(goodbye.HasValue ? goodbye.Value.reason.ToString() : "none")} " +
                $"managerRunning={netManager.IsRunning} managerPeers={netManager.ConnectedPeersCount} " +
                $"managerStats={netManager.Statistics.ToDebugString().Replace(Environment.NewLine, " ").Trim()}"));
            base.OnClose(goodbye);
            netManager.Stop();
        }

        private class NetListener(string username) : INetEventListener
        {
            private ClientLiteNetConnection GetConnection(NetPeer peer) =>
                peer.GetConnection() as ClientLiteNetConnection ?? throw new Exception("Can't get connection");

            public void OnPeerConnected(NetPeer peer)
            {
                var conn = GetConnection(peer);
                conn.ChangeState(new ClientJoiningState(conn, username));
                MpLog.Log(LiteNetDiagnostics.Next(
                    "client/connected",
                    $"{LiteNetDiagnostics.Peer(peer)} appState={conn.State} username={username}"));
            }

            public void OnNetworkError(IPEndPoint endPoint, SocketError error)
            {
                MpLog.Warn(LiteNetDiagnostics.Next(
                    "client/network-error",
                    $"endpoint={endPoint} socketError={error}"));
            }

            public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod method)
            {
                byte[] data = reader.GetRemainingBytes();
                GetConnection(peer).HandleReceiveRaw(new ByteReader(data), method == DeliveryMethod.ReliableOrdered);
            }

            public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
            {
                var conn = GetConnection(peer);
                MpLog.Warn(LiteNetDiagnostics.Next(
                    "client/disconnected",
                    $"{LiteNetDiagnostics.Disconnect(peer, info)} appState={conn.State} username={username}"));

                MpDisconnectReason reason;
                ByteReader reader;

                if (info.AdditionalData.EndOfData)
                {
                    if (info.Reason is DisconnectReason.DisconnectPeerCalled or DisconnectReason.RemoteConnectionClose)
                        reason = MpDisconnectReason.Generic;
                    else if (Multiplayer.Client == null)
                        reason = MpDisconnectReason.ConnectingFailed;
                    else
                        reason = MpDisconnectReason.NetFailed;

                    reader = new ByteReader(ByteWriter.GetBytes(info.Reason));
                }
                else
                {
                    reader = new ByteReader(info.AdditionalData.GetRemainingBytes());
                    reason = reader.ReadEnum<MpDisconnectReason>();
                }

                conn.OnDisconnect(SessionDisconnectInfo.From(reason, reader));
                MpLog.Log(LiteNetDiagnostics.Next(
                    "client/disconnect-processed",
                    $"netReason={info.Reason} appReason={reason} appState={conn.State} username={username}"));
            }

            public void OnConnectionRequest(ConnectionRequest request)
            {
            }

            public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
            {
            }

            public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader,
                UnconnectedMessageType messageType)
            {
            }
        }
    }

    public class LiteNetLogger : INetLogger
    {
        public static void Install() => NetDebug.Logger = new LiteNetLogger();

        public void WriteNet(NetLogLevel level, string str, params object[] args)
        {
            string message;
            try
            {
                message = string.Format(str, args);
            }
            catch (FormatException)
            {
                message = $"{str} [formatArgs={string.Join(", ", args)}]";
            }

            message = LiteNetDiagnostics.Next($"library/{level}", message);
            if (level == NetLogLevel.Error)
                ServerLog.Error(message);
            else
                ServerLog.Log(message);
        }
    }

    [HarmonyPatch]
    static class LiteNetPeerNotFoundSendPatch
    {
        // PacketProperty is internal in LiteNetLib 1.3.1. Its stable wire value is 14.
        private const byte PeerNotFoundProperty = 14;

        static MethodBase TargetMethod() =>
            AccessTools.DeclaredMethod(
                typeof(NetManager),
                "SendRaw",
                [typeof(byte[]), typeof(int), typeof(int), typeof(IPEndPoint)]);

        static void Prefix(NetManager __instance, byte[] message, int start, int length, IPEndPoint remoteEndPoint)
        {
            if (message == null || length < 1 || start < 0 || start >= message.Length)
                return;
            if ((message[start] & 0x1F) != PeerNotFoundProperty)
                return;

            var knownPeer = __instance.ConnectedPeerList.Find(peer => peer.Equals(remoteEndPoint));
            ServerLog.Log(LiteNetDiagnostics.Next(
                "server/send-peer-not-found",
                $"destination={remoteEndPoint} packetLength={length} responseFlag=" +
                $"{(length > 1 && start + 1 < message.Length ? message[start + 1].ToString() : "none")} " +
                $"managerRunning={__instance.IsRunning} localPort={__instance.LocalPort} " +
                $"managerPeers={__instance.ConnectedPeersCount} knownPeer={knownPeer != null} " +
                $"peer={(knownPeer != null ? LiteNetDiagnostics.Peer(knownPeer) : "none")} " +
                $"managerStats={__instance.Statistics.ToSingleLineDebugString()}"));
        }
    }
}
