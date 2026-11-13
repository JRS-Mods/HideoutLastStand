using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace HideoutSiege.Fika
{
    public struct SiegeHelloPacket : INetSerializable
    {
        public string Version;
        public bool Welcome;

        public readonly void Serialize(NetDataWriter writer) { writer.Put(Version ?? ""); writer.Put(Welcome); }
        public void Deserialize(NetDataReader reader) { Version = reader.GetString(); Welcome = reader.GetBool(); }
    }

    public struct SiegeStatePacket : INetSerializable
    {
        public int Phase, Wave, TotalWaves, Remaining, Tier, Kills;
        public string Banner;
        public float BannerSeconds, CountdownSeconds;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Phase); writer.Put(Wave); writer.Put(TotalWaves); writer.Put(Remaining); writer.Put(Tier); writer.Put(Kills);
            writer.Put(Banner ?? ""); writer.Put(BannerSeconds); writer.Put(CountdownSeconds);
        }

        public void Deserialize(NetDataReader reader)
        {
            Phase = reader.GetInt(); Wave = reader.GetInt(); TotalWaves = reader.GetInt(); Remaining = reader.GetInt(); Tier = reader.GetInt(); Kills = reader.GetInt();
            Banner = reader.GetString(); BannerSeconds = reader.GetFloat(); CountdownSeconds = reader.GetFloat();
        }
    }

    public struct SiegeDoorPacket : INetSerializable
    {
        public bool Blast, Held;
        public float X, Y, Z;

        public readonly void Serialize(NetDataWriter writer) { writer.Put(Blast); writer.Put(X); writer.Put(Y); writer.Put(Z); writer.Put(Held); }
        public void Deserialize(NetDataReader reader)
        {
            Blast = reader.GetBool(); X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
            Held = reader.AvailableBytes > 0 && reader.GetBool();
        }
    }

    public struct SiegeSandbagPacket : INetSerializable
    {
        public float X, Y, Z, Yaw;

        public readonly void Serialize(NetDataWriter writer) { writer.Put(X); writer.Put(Y); writer.Put(Z); writer.Put(Yaw); }
        public void Deserialize(NetDataReader reader) { X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat(); Yaw = reader.GetFloat(); }
    }

    public struct SiegeTurretPacket : INetSerializable
    {
        public Vector3 From, Aim;
        public int Shots;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(From.x); writer.Put(From.y); writer.Put(From.z);
            writer.Put(Aim.x); writer.Put(Aim.y); writer.Put(Aim.z);
            writer.Put(Shots);
        }

        public void Deserialize(NetDataReader reader)
        {
            From = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            Aim = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            Shots = reader.GetInt();
        }
    }

    public struct SiegeGunPacket : INetSerializable
    {
        public Vector3 Where, AimAt;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Where.x); writer.Put(Where.y); writer.Put(Where.z);
            writer.Put(AimAt.x); writer.Put(AimAt.y); writer.Put(AimAt.z);
        }

        public void Deserialize(NetDataReader reader)
        {
            Where = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
            AimAt = new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }
    }

    public struct SiegeAskPacket : INetSerializable
    {
        // Whose gear is whose (0), a player asking for another's (1), or the answer (2): see SiegeAsk.
        public int Kind;
        public string Player, Other;
        public string[] Items;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Kind); writer.Put(Player ?? ""); writer.Put(Other ?? "");
            writer.Put(Items?.Length ?? 0);
            foreach (string item in Items ?? new string[0]) writer.Put(item ?? "");
        }

        public void Deserialize(NetDataReader reader)
        {
            Kind = reader.GetInt(); Player = reader.GetString(); Other = reader.GetString();
            Items = new string[reader.GetInt()];
            for (int i = 0; i < Items.Length; i++) Items[i] = reader.GetString();
        }
    }

    public struct SiegeCarryPacket : INetSerializable
    {
        public string ProfileId;

        public readonly void Serialize(NetDataWriter writer) { writer.Put(ProfileId ?? ""); }
        public void Deserialize(NetDataReader reader) { ProfileId = reader.GetString(); }
    }

    public struct SiegeGeneratorPacket : INetSerializable
    {
        // Cut: Black Division did it, and it stays off for the rest of the raid.
        public bool Off, Cut;

        public readonly void Serialize(NetDataWriter writer) { writer.Put(Off); writer.Put(Cut); }
        public void Deserialize(NetDataReader reader) { Off = reader.GetBool(); Cut = reader.GetBool(); }
    }

    public struct SiegeLayoutPacket : INetSerializable
    {
        public int[] Types, Levels, SurfaceTypes;
        public string[] SurfaceIds;
        // What other mods add to the host's hideout (Hideout Extensions' rooms, Hideout Interiors' things), by mod.
        public string[] CopyNames, CopySnapshots;
        // Whose hideout it is: the host's, or on a headless the player it's hosting for. Empty from an older host.
        public string OwnerId;
        // The network's strings stop at 65,535 bytes, and a decorated hideout can say more than that.
        private const int Chunk = 16000;

        public readonly void Serialize(NetDataWriter writer)
        {
            writer.Put(Types?.Length ?? 0);
            for (int i = 0; i < (Types?.Length ?? 0); i++) { writer.Put(Types[i]); writer.Put(Levels[i]); }
            writer.Put(SurfaceTypes?.Length ?? 0);
            for (int i = 0; i < (SurfaceTypes?.Length ?? 0); i++) { writer.Put(SurfaceTypes[i]); writer.Put(SurfaceIds[i]); }
            writer.Put(CopyNames?.Length ?? 0);
            for (int i = 0; i < (CopyNames?.Length ?? 0); i++)
            {
                writer.Put(CopyNames[i]);
                string text = CopySnapshots[i] ?? "";
                int chunks = (text.Length + Chunk - 1) / Chunk;
                writer.Put(chunks);
                for (int c = 0; c < chunks; c++) writer.Put(text.Substring(c * Chunk, System.Math.Min(Chunk, text.Length - c * Chunk)));
            }
            writer.Put(OwnerId ?? "");
        }

        public void Deserialize(NetDataReader reader)
        {
            int count = reader.GetInt();
            Types = new int[count];
            Levels = new int[count];
            for (int i = 0; i < count; i++) { Types[i] = reader.GetInt(); Levels[i] = reader.GetInt(); }
            count = reader.GetInt();
            SurfaceTypes = new int[count];
            SurfaceIds = new string[count];
            for (int i = 0; i < count; i++) { SurfaceTypes[i] = reader.GetInt(); SurfaceIds[i] = reader.GetString(); }
            // A host on an older siege sends no more than that.
            if (reader.AvailableBytes < 4) { CopyNames = CopySnapshots = null; return; }
            count = reader.GetInt();
            CopyNames = new string[count];
            CopySnapshots = new string[count];
            for (int i = 0; i < count; i++)
            {
                CopyNames[i] = reader.GetString();
                int chunks = reader.GetInt();
                var text = new System.Text.StringBuilder();
                for (int c = 0; c < chunks; c++) text.Append(reader.GetString());
                CopySnapshots[i] = text.ToString();
            }
            OwnerId = reader.AvailableBytes > 0 ? reader.GetString() : null;
        }
    }

    /// <summary>
    /// The host runs the siege and tells everyone how it's going. Packets only go to players who've said they have this
    /// mod: a game without it can't read them and would log an error for each one.
    /// </summary>
    internal sealed class FikaLink : ISiegeLink
    {
        private readonly HashSet<NetPeer> peers = new HashSet<NetPeer>();
        private IFikaNetworkManager network;
        private FikaServer server;
        private bool host, welcomed, hadPeers;
        private int helloTries;
        private float nextHello;

        public FikaLink()
        {
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e => Safely(() => Created(e)));
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(e => Safely(() => Destroyed(e)));
            FikaEventDispatcher.SubscribeEvent<PeerDisconnectedEvent>(e => Safely(() => { if (e.NetworkManager == network) peers.Remove(e.Peer); }));
            FikaEventDispatcher.SubscribeEvent<GameWorldStartedEvent>(_ => Safely(() => { helloTries = 0; nextHello = 0; if (network != null && !host) SayHello(); }));
        }

        private static void Safely(Action act)
        {
            try { act(); }
            catch (Exception e) { Plugin.Log.LogWarning("Siege sync: " + e); }
        }

        public bool Online
        {
            get
            {
                if (network == null) return false;
                if (host || welcomed) return true;
                SayHello();
                return true;
            }
        }

        public bool IsHost => host;
        public int PeerCount => server != null ? server.NetServer.ConnectedPeersCount : 0;
        public bool HadPeers => hadPeers;

        private void Created(FikaNetworkManagerCreatedEvent e)
        {
            Reset();
            network = e.Manager;
            server = network as FikaServer;
            host = server != null;
            network.RegisterPacket<SiegeHelloPacket, NetPeer>((p, from) => Safely(() => Hello(p, from)));
            network.RegisterPacket<SiegeStatePacket, NetPeer>((p, from) => Safely(() =>
            {
                if (!host) Net.ReceivedState(p.Phase, p.Wave, p.TotalWaves, p.Remaining, p.Tier, p.Kills, p.Banner, p.BannerSeconds, p.CountdownSeconds);
            }));
            network.RegisterPacket<SiegeDoorPacket, NetPeer>((p, from) => Safely(() => { if (!host) Net.ReceivedDoor(p.Blast, new Vector3(p.X, p.Y, p.Z), p.Held); }));
            network.RegisterPacket<SiegeGunPacket, NetPeer>((p, from) => Safely(() => { if (!host) SiegeGun.Set(p.Where, p.AimAt); }));
            network.RegisterPacket<SiegeCarryPacket, NetPeer>((p, from) => Safely(() => { if (!host) Net.ReceivedCarry(p.ProfileId); }));
            network.RegisterPacket<SiegeGeneratorPacket, NetPeer>((p, from) => Safely(() =>
            {
                SiegeGenerator.Set(p.Off, p.Cut);
                // What it really is now: a switch flipped after the cut doesn't turn anything back on.
                if (host) SendGenerator(SiegeLights.PowerOut, SiegeGenerator.Cut);
            }));
            network.RegisterPacket<SiegeSandbagPacket, NetPeer>((p, from) => Safely(() => Net.ReceivedSandbags(new Vector3(p.X, p.Y, p.Z), p.Yaw)));
            network.RegisterPacket<SiegeTurretPacket, NetPeer>((p, from) => Safely(() => { if (!host) SiegeTurret.Heard(p.From, p.Aim, p.Shots); }));
            network.RegisterPacket<SiegeAskPacket, NetPeer>((p, from) => Safely(() => Net.ReceivedAsk(p.Kind, p.Player, p.Other, p.Items)));
            network.RegisterPacket<SiegeLayoutPacket, NetPeer>((p, from) => Safely(() => { if (!host) Net.ReceivedLayout(p.Types, p.Levels, p.SurfaceTypes, p.SurfaceIds, p.CopyNames, p.CopySnapshots, p.OwnerId); }));
        }

        private void Destroyed(FikaNetworkManagerDestroyedEvent e)
        {
            if (e.Manager != network) return;
            network.UnregisterPacket<SiegeHelloPacket>();
            network.UnregisterPacket<SiegeStatePacket>();
            network.UnregisterPacket<SiegeLayoutPacket>();
            network.UnregisterPacket<SiegeSandbagPacket>();
            network.UnregisterPacket<SiegeTurretPacket>();
            network.UnregisterPacket<SiegeAskPacket>();
            network.UnregisterPacket<SiegeDoorPacket>();
            network.UnregisterPacket<SiegeCarryPacket>();
            network.UnregisterPacket<SiegeGunPacket>();
            network.UnregisterPacket<SiegeGeneratorPacket>();
            Reset();
        }

        private void Reset()
        {
            network = null; server = null; host = welcomed = hadPeers = false; helloTries = 0; nextHello = 0;
            peers.Clear();
        }

        private void SayHello()
        {
            if (helloTries >= 5 || Time.time < nextHello) return;
            helloTries++;
            nextHello = Time.time + 4f;
            var hello = new SiegeHelloPacket { Version = Plugin.Version };
            network.SendData(ref hello, DeliveryMethod.ReliableOrdered);
        }

        private void Hello(SiegeHelloPacket packet, NetPeer from)
        {
            if (host)
            {
                if (packet.Version != Plugin.Version) Plugin.Log.LogWarning($"A player has Hideout Siege {packet.Version}, this host {Plugin.Version}.");
                peers.Add(from);
                hadPeers = true;
                var welcome = new SiegeHelloPacket { Version = Plugin.Version, Welcome = true };
                network.SendDataToPeer(ref welcome, DeliveryMethod.ReliableOrdered, from);
                var layout = Layout();
                network.SendDataToPeer(ref layout, DeliveryMethod.ReliableOrdered, from);
                var state = State();
                network.SendDataToPeer(ref state, DeliveryMethod.ReliableOrdered, from);
                var generator = new SiegeGeneratorPacket { Off = SiegeLights.PowerOut, Cut = SiegeGenerator.Cut };
                network.SendDataToPeer(ref generator, DeliveryMethod.ReliableOrdered, from);
                foreach (var (player, items) in SiegeAsk.OwnedSoFar())
                {
                    var owned = new SiegeAskPacket { Kind = SiegeAsk.Owned, Player = player, Other = "", Items = items };
                    network.SendDataToPeer(ref owned, DeliveryMethod.ReliableOrdered, from);
                }
                foreach (var (at, yaw) in SiegeSandbags.Placed)
                {
                    var bags = new SiegeSandbagPacket { X = at.x, Y = at.y, Z = at.z, Yaw = yaw };
                    network.SendDataToPeer(ref bags, DeliveryMethod.ReliableOrdered, from);
                }
            }
            else if (packet.Welcome)
            {
                welcomed = true;
                Plugin.Log.LogInfo("The host has Hideout Siege too.");
            }
        }

        private static SiegeLayoutPacket Layout()
        {
            var levels = HideoutCopy.Levels();
            var packet = new SiegeLayoutPacket { Types = new int[levels.Count], Levels = new int[levels.Count] };
            int i = 0;
            foreach (var pair in levels) { packet.Types[i] = (int)pair.Key; packet.Levels[i] = pair.Value; i++; }
            var pack = HideoutCopy.Pack();
            packet.SurfaceTypes = new int[pack.Count];
            packet.SurfaceIds = new string[pack.Count];
            i = 0;
            foreach (var pair in pack) { packet.SurfaceTypes[i] = (int)pair.Key; packet.SurfaceIds[i] = pair.Value; i++; }
            var copies = HideoutCopy.Copies();
            packet.CopyNames = System.Linq.Enumerable.ToArray(copies.Keys);
            packet.CopySnapshots = System.Linq.Enumerable.ToArray(copies.Values);
            packet.OwnerId = SiegeOwner.ProfileId;
            return packet;
        }

        private static SiegeStatePacket State() => new SiegeStatePacket
        {
            Phase = (int)SiegeState.Phase,
            Wave = SiegeState.Wave,
            TotalWaves = SiegeState.TotalWaves,
            Remaining = SiegeState.Remaining,
            Tier = SiegeState.Tier,
            Kills = SiegeState.Kills,
            Banner = SiegeState.Banner,
            BannerSeconds = Mathf.Max(0f, SiegeState.BannerUntil - Time.time),
            CountdownSeconds = Mathf.Max(0f, SiegeState.CountdownUntil - Time.time),
        };

        public void SendGenerator(bool off, bool cut)
        {
            var packet = new SiegeGeneratorPacket { Off = off, Cut = cut };
            if (!host)
            {
                network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                return;
            }
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }

        public void SendSandbags(Vector3 at, float yaw)
        {
            var packet = new SiegeSandbagPacket { X = at.x, Y = at.y, Z = at.z, Yaw = yaw };
            if (!host)
            {
                network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                return;
            }
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }

        public void SendAsk(int kind, string player, string other, string[] items)
        {
            var packet = new SiegeAskPacket { Kind = kind, Player = player, Other = other, Items = items };
            if (!host)
            {
                network.SendData(ref packet, DeliveryMethod.ReliableOrdered);
                return;
            }
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }

        public void SendTurret(Vector3 from, Vector3 aim, int shots)
        {
            var packet = new SiegeTurretPacket { From = from, Aim = aim, Shots = shots };
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }

        public void SendDoor(bool blast, Vector3 at, bool held)
        {
            var packet = new SiegeDoorPacket { Blast = blast, Held = held, X = at.x, Y = at.y, Z = at.z };
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }

        public void SendGun(Vector3 where, Vector3 aimAt)
        {
            var packet = new SiegeGunPacket { Where = where, AimAt = aimAt };
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }

        public void SendCarry(string profileId)
        {
            var packet = new SiegeCarryPacket { ProfileId = profileId };
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }

        public void SendState()
        {
            var packet = State();
            foreach (NetPeer peer in peers) network.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer);
        }
    }
}
