using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;

namespace Multiplayer.Common
{
    public class LiteNetManager : INetManager
    {
        public List<(LiteNetEndpoint endpoint, NetManager manager)> netManagers;
        private readonly Dictionary<NetPeer, int> peerSilenceBuckets = [];
        private int diagnosticsTick;

        private LiteNetManager(List<(LiteNetEndpoint endpoint, NetManager manager)> netManagers) =>
            this.netManagers = netManagers;

        public static bool Create(MultiplayerServer server, IPEndPoint[] endpoints, out LiteNetManager manager)
        {
            var success = true;

            var liteNetEndpoints = new Dictionary<int, LiteNetEndpoint>();
            foreach (var endpoint in endpoints)
            {
                if (endpoint.AddressFamily == AddressFamily.InterNetwork)
                    liteNetEndpoints.GetOrAddNew(endpoint.Port).ipv4 = endpoint.Address;
                else if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
                    liteNetEndpoints.GetOrAddNew(endpoint.Port).ipv6 = endpoint.Address;
            }

            List<(LiteNetEndpoint, NetManager)> netManagers = [];
            foreach (var (port, endpoint) in liteNetEndpoints)
            {
                endpoint.port = port;
                netManagers.Add((endpoint, CreateNetManager(endpoint.ipv6 != null)));
            }

            foreach (var (endpoint, man) in netManagers)
            {
                ServerLog.Detail($"Starting NetManager at {endpoint}");
                var started = man.Start(
                    endpoint.ipv4 ?? IPAddress.Any,
                    endpoint.ipv6 ?? IPAddress.IPv6Any,
                    endpoint.port);
                success &= started;
                ServerLog.Log(LiteNetDiagnostics.Next(
                    "server/manager-start",
                    $"endpoint={endpoint} started={started} running={man.IsRunning} localPort={man.LocalPort} " +
                    $"ipv6={man.IPv6Enabled} updateTime={man.UpdateTime}ms pingInterval={man.PingInterval}ms " +
                    $"disconnectTimeout={man.DisconnectTimeout}ms"));
            }

            manager = new LiteNetManager(netManagers);
            return success;

            NetManager CreateNetManager(bool ipv6)
            {
                return new NetManager(new MpServerNetListener(server, false))
                {
                    EnableStatistics = true,
                    IPv6Enabled = ipv6
                };
            }
        }

        public void Tick()
        {
            var managersSnapshot = netManagers.ToArray();
            foreach (var (_, man) in managersSnapshot) man.PollEvents();

            if (++diagnosticsTick % 15 != 0)
                return;

            var connectedPeers = managersSnapshot
                .SelectMany(tuple => tuple.manager.ConnectedPeerList)
                .ToArray();
            var connectedPeerSet = connectedPeers.ToHashSet();

            foreach (var stalePeer in peerSilenceBuckets.Keys.Where(peer => !connectedPeerSet.Contains(peer)).ToArray())
                peerSilenceBuckets.Remove(stalePeer);

            foreach (var peer in connectedPeers)
            {
                if (peer.TimeSinceLastPacket < 1500f)
                {
                    peerSilenceBuckets.Remove(peer);
                    continue;
                }

                var silenceBucket = (int)(peer.TimeSinceLastPacket / 500f);
                if (peerSilenceBuckets.TryGetValue(peer, out var previousBucket) && previousBucket == silenceBucket)
                    continue;

                peerSilenceBuckets[peer] = silenceBucket;
                var conn = peer.Tag as LiteNetConnection;
                ServerLog.Log(LiteNetDiagnostics.Next(
                    "server/peer-silent",
                    $"{LiteNetDiagnostics.Peer(peer)} appState={conn?.State.ToString() ?? "null"} " +
                    $"playerId={conn?.serverPlayer?.id.ToString() ?? "null"} username={conn?.username ?? "null"} " +
                    $"disconnectTimeout={peer.NetManager.DisconnectTimeout}ms"));
            }
        }

        public void Stop()
        {
            foreach (var (endpoint, man) in netManagers)
            {
                var peers = man.ConnectedPeerList.ToArray();
                ServerLog.Log(LiteNetDiagnostics.Next(
                    "server/manager-stop",
                    $"endpoint={endpoint} running={man.IsRunning} localPort={man.LocalPort} peers={peers.Length} " +
                    $"stats={man.Statistics.ToSingleLineDebugString()}"));
                foreach (var peer in peers)
                    ServerLog.Log(LiteNetDiagnostics.Next(
                        "server/manager-stop-peer",
                        LiteNetDiagnostics.Peer(peer)));
                man.Stop();
            }

            peerSilenceBuckets.Clear();
            netManagers.Clear();
        }

        public string GetDiagnosticsName() => "Server (LiteNet)";
        public string GetDiagnosticsInfo()
        {
            var text = new StringBuilder();
            foreach (var (endpoint, man) in netManagers)
            {
                text.AppendLine($"{endpoint}");
                text.AppendLine(man.Statistics.ToDebugString());
            }
            return text.ToString();
        }
    }

    public class LiteNetArbiterManager : INetManager
    {
        private NetManager arbiter;
        public int Port => arbiter.LocalPort;

        private LiteNetArbiterManager(NetManager arbiter) => this.arbiter = arbiter;

        public static LiteNetArbiterManager? Create(MultiplayerServer server)
        {
            var arbiter = new NetManager(new MpServerNetListener(server, true)) { IPv6Enabled = false };
            if (!arbiter.Start(IPAddress.Loopback, IPAddress.IPv6Any, 0)) return null;
            return new LiteNetArbiterManager(arbiter);
        }

        public void Tick() => arbiter.PollEvents();

        public void Stop() => arbiter.Stop();

        public string GetDiagnosticsName() => $"Arbiter (LiteNet) {IPAddress.Loopback}:{Port}";
        public string? GetDiagnosticsInfo() => null;
    }

    public class LiteNetLanManager : INetManager
    {
        private NetManager lanManager;
        private readonly IPAddress lanAddress;
        private int broadcastTimer;

        private LiteNetLanManager(NetManager lanManager, IPAddress lanAddress)
        {
            this.lanManager = lanManager;
            this.lanAddress = lanAddress;
        }

        public static LiteNetLanManager? Create(MultiplayerServer server, IPAddress addr)
        {
            var lanManager = new NetManager(new MpServerNetListener(server, false))
            {
                EnableStatistics = true,
                IPv6Enabled = false
            };
            if (!lanManager.Start(addr, IPAddress.IPv6Any, 0)) return null;
            return new LiteNetLanManager(lanManager, addr);
        }

        public void Tick()
        {
            lanManager.PollEvents();
            if (broadcastTimer++ % 60 == 0)
                lanManager.SendBroadcast(Encoding.UTF8.GetBytes(MultiplayerServer.LanBroadcastName),
                    MultiplayerServer.LanBroadcastPort);
        }

        public void Stop() => lanManager.Stop();

        public string GetDiagnosticsName() => $"Lan (LiteNet) {lanAddress}:{lanManager.LocalPort}";
        public string GetDiagnosticsInfo() => lanManager.Statistics.ToDebugString();
    }

    public class LiteNetEndpoint
    {
        public IPAddress? ipv4;
        public IPAddress? ipv6;
        public int port;

        public override string ToString()
        {
            return
                ipv4 == null ? $"{ipv6}:{port}" :
                ipv6 == null ? $"{ipv4}:{port}" :
                $"{ipv4}:{port} / {ipv6}:{port}";
        }
    }

    public static class LiteNetExtensions
    {
        public static string ToDebugString(this NetStatistics stats)
        {
            var text = new StringBuilder();
            text.AppendLine($"Recv: {stats.BytesReceived}B  {stats.PacketsReceived} packets");
            text.AppendLine($"Sent: {stats.BytesSent}B  {stats.PacketsSent} packets");
            text.AppendLine($"Loss: {stats.PacketLoss} packets ({stats.PacketLossPercent}%)");
            return text.ToString();
        }

        public static string ToSingleLineDebugString(this NetStatistics stats) =>
            $"recv={stats.PacketsReceived}/{stats.BytesReceived}B " +
            $"sent={stats.PacketsSent}/{stats.BytesSent}B " +
            $"loss={stats.PacketLoss}({stats.PacketLossPercent}%)";
    }
}
