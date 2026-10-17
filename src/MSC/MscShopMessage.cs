using System.IO;
using MWCoop.Net;
using UnityEngine;
namespace MWCoop
{
    public sealed class MscShopMessage
    {
        public const int Kind = 7, Buy = 0, Take = 1, Deliver = 2, Extract = 3;
        public int Op, Ticket, Sequence, Actor, Index;
        public bool All, Spray;
        public string Bag = "";
        public int[] Quantities = new int[0], Counters = new int[0], Items = new int[0], Cans = new int[0];
        public float Condition;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public void Write(NetWriter w)
        {
            w.U8(Op).I32(Ticket).I32(Sequence).U8(Actor).Str(Bag).Bool(All).Bool(Spray).U8(Index);
            Array(w, Quantities); Array(w, Counters); Array(w, Items); Array(w, Cans);
            w.F32(Condition).Vec(Position).Quat(Rotation);
        }
        static void Array(NetWriter w, int[] values) { w.U8(values.Length); foreach (int value in values) w.I32(value); }
        static int[] Array(NetReader r, int limit, int max)
        {
            int count = r.U8(); if (count > limit) throw new IOException("MSC shop table too large");
            int[] values = new int[count];
            for (int i = 0; i < count; i++) { values[i] = r.I32(); if (values[i] < 0 || values[i] > max) throw new IOException("Invalid MSC shop count"); }
            return values;
        }
        public static MscShopMessage Read(NetReader r)
        {
            var m = new MscShopMessage { Op = r.U8(), Ticket = r.I32(), Sequence = r.I32(), Actor = r.U8(), Bag = r.Str(), All = r.Bool(), Spray = r.Bool(), Index = r.U8() };
            m.Quantities = Array(r, 64, 1000); m.Counters = Array(r, 96, int.MaxValue);
            m.Items = Array(r, 18, 1000); m.Cans = Array(r, 14, 1000);
            m.Condition = r.F32(); m.Position = r.Vec(); m.Rotation = r.Quat();
            if (r.More || m.Op > Extract || m.Ticket <= 0 || m.Sequence < 0 || m.Bag.Length > 80 || m.Condition < 0 || m.Condition > 100
                || (m.Op == Deliver || m.Op == Extract) && m.Sequence == 0
                || (m.Op == Take || m.Op == Extract) && m.Bag.Length == 0
                || m.Op == Extract && (m.Items.Length != 18 || m.Cans.Length < 13 || m.Index >= (m.Spray ? m.Cans.Length : m.Items.Length)
                    || (m.Spray ? m.Cans : m.Items)[m.Index] == 0))
                throw new IOException("Invalid MSC shop operation");
            return m;
        }
        public static bool AcceptSequence(int incoming, int applied) { return incoming > applied; }
    }
}
