using System;
using System.IO;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    public sealed class MscOrderMessage
    {
        public const int Kind = 8, Catalog = 0, Fleetari = 1;
        public const int Submit = 0, Commit = 1, Work = 2, Arrive = 3, Pay = 4, Done = 5, Sync = 6, Reject = 7;
        public int Type, Op, Ticket, Actor, Revision, Seed;
        public bool Active, Paid;
        public float Cost, Time, Gear;
        public ulong Flags;
        public Color CarColor, RimColor;
        public string[] Items = new string[0];
        public byte[] Pack()
        {
            var w = new NetWriter(Msg.Purchase).U8(Type).U8(Op).I32(Ticket).U8(Actor).I32(Revision).I32(Seed)
                .Bool(Active).Bool(Paid).F32(Cost).F32(Time).F32(Gear).I32((int)Flags).I32((int)(Flags >> 32));
            foreach (float v in new[] { CarColor.r, CarColor.g, CarColor.b, CarColor.a, RimColor.r, RimColor.g, RimColor.b, RimColor.a }) w.F32(v);
            // Mono shipped with MSC does not provide CreateZStream. The native
            // catalogue repeats these tokens; encode them without native zlib.
            w.U8(Items.Length); foreach (string item in Items) w.Str(item.Replace("(Clone)", "\u0001").Replace("wheelset ", "\u0002"));
            byte[] raw = w.ToArray();
            if (raw.Length > Transport.MaxPayload - 8) throw new IOException("MSC order exceeds network payload");
            return raw;
        }
        public static MscOrderMessage Read(byte[] input)
        {
            if (input.Length > Transport.MaxPayload - 8) throw new IOException("MSC order packet too large");
            byte[] raw = input;
            var r = new NetReader(raw, 0, raw.Length); if (r.U8() != (int)Msg.Purchase) throw new IOException("Invalid order header");
            var m = new MscOrderMessage { Type = r.U8(), Op = r.U8(), Ticket = r.I32(), Actor = r.U8(), Revision = r.I32(), Seed = r.I32(),
                Active = r.Bool(), Paid = r.Bool(), Cost = r.F32(), Time = r.F32(), Gear = r.F32() };
            m.Flags = (uint)r.I32() | ((ulong)(uint)r.I32() << 32);
            m.CarColor = new Color(r.F32(), r.F32(), r.F32(), r.F32()); m.RimColor = new Color(r.F32(), r.F32(), r.F32(), r.F32());
            int count = r.U8(); if (count > 96) throw new IOException("Invalid order list length"); m.Items = new string[count];
            for (int i = 0; i < count; i++) { m.Items[i] = r.Str().Replace("\u0001", "(Clone)").Replace("\u0002", "wheelset "); if (m.Items[i].Length > 80) throw new IOException("Invalid order item"); }
            if (r.More || m.Type > Fleetari || m.Op > Reject || m.Ticket < 0 || m.Revision < 0 || m.Cost < 0 || m.Cost > 1000000
                || m.Time < 0 || m.Time > 100000 || m.Gear < 0 || m.Gear > 10) throw new IOException("Invalid MSC order");
            return m;
        }
    }
}
