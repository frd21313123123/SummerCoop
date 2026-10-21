using System.Collections.Generic;
using MWCoop.Net;
using UnityEngine;

namespace MWCoop
{
    // The host remains the source of world state. Discovery never clears
    // hooks or repeats input, and no save or connection has to be reloaded.
    public static class MscResync
    {
        static float nextRequest;
        static readonly Dictionary<int, float> requested = new Dictionary<int, float>();
        sealed class Work { public Peer Peer; public float At; }
        static readonly List<Work> work = new List<Work>();
        public static bool Available { get { return Session.Active && PlayerSync.InGame && Time.realtimeSinceStartup >= nextRequest; } }
        public static int Requests { get; private set; }
        public static void Reset() { nextRequest = 0; requested.Clear(); work.Clear(); Requests = 0; }
        public static string Request()
        {
            if (!Available) return Lang.R("Synchronization is not ready yet.");
            nextRequest = Time.realtimeSinceStartup + 5;
            Refresh();
            if (Session.IsHost)
            { foreach (Peer peer in Session.T.Peers) Schedule(peer); }
            else Session.SendToHost(new NetWriter(Msg.Resync).U8(0), true);
            return Lang.R("Full synchronization requested");
        }
        static void Refresh()
        {
            Requests++;
            MscParts.Resynchronize(); CarDoors.SoonScan(); Jobs.SoonScan();
            WorldFsms.SoonScan(); Props.InvalidateHierarchy();
            MscCd.Resynchronize(); MscStereo.Resynchronize();
            Knobs.Resynchronize(); Radio.Resynchronize();
        }
        static void Schedule(Peer peer)
        {
            PlayerInfo player;
            if (peer == null || !peer.Accepted || !Session.Players.TryGetValue(peer.Id, out player) || player.Level != 1) return;
            float previous;
            if (requested.TryGetValue(peer.Id, out previous) && Time.realtimeSinceStartup - previous < 5) return;
            requested[peer.Id] = Time.realtimeSinceStartup;
            Session.T.SendReliable(peer, new NetWriter(Msg.Resync).U8(1).ToArray());
            work.Add(new Work { Peer = peer, At = Time.realtimeSinceStartup + 2 });
        }
        public static void OnMessage(Peer from, NetReader reader)
        {
            int kind = reader.U8();
            if (reader.More || kind > 1) throw new System.IO.IOException("Invalid resynchronization request");
            if (Session.IsHost && kind == 0) Schedule(from);
            else if (!Session.IsHost && kind == 1 && from != null && from.Id == 0) Refresh();
        }
        public static void Update()
        {
            if (!Session.Active || !PlayerSync.InGame || !Session.IsHost) return;
            for (int i = work.Count - 1; i >= 0; i--)
            {
                Work item = work[i];
                if (!item.Peer.Accepted || !Session.T.Peers.Contains(item.Peer)) { work.RemoveAt(i); continue; }
                if (Time.realtimeSinceStartup < item.At) continue;
                work.RemoveAt(i); Refresh();
                Interactions.ScheduleSnapshot(item.Peer, true);
                Fluids.ScheduleSnapshot(item.Peer, true);
                Consume.ScheduleSnapshot(item.Peer, true);
                WorldFsms.ScheduleSnapshot(item.Peer, true);
                CarDoors.ScheduleSnapshot(item.Peer, true);
                Props.ScheduleSnapshot(true); Jobs.QueueSnapshot(item.Peer);
                Log.Info("MSC full synchronization queued for #" + item.Peer.Id);
            }
            Jobs.PumpSnapshots();
        }
    }
}
