using System.Collections.Generic;
using MWCoop.Net;
using UnityEngine;
using ProtocolException = SummerCoop.Core.ProtocolException;

namespace MWCoop
{
    public sealed class MscToolCaseSnapshot
    {
        // Header + 32 * (identity, kind, position, quaternion) fits one reliable
        // Transport.MaxPayload packet, including the Home dispatch bytes.
        public const int Limit = 32;
        public sealed class Entry { public int Id, Kind; public Vector3 Position; public Quaternion Rotation; }
        public int Revision;
        public readonly List<Entry> Entries = new List<Entry>();
        public static bool AcceptRevision(int incoming, int current) { return incoming > current; }
        public static MscToolCaseSnapshot Read(NetReader r)
        {
            var result = new MscToolCaseSnapshot { Revision = r.I32() };
            int count = r.U8();
            if (result.Revision < 1 || count < 1 || count > Limit) throw new ProtocolException("Invalid tool case snapshot");
            var ids = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                var entry = new Entry { Id = r.I32(), Kind = r.U8(), Position = r.Vec(), Rotation = r.Quat() };
                if (entry.Id < 0 || entry.Kind > 1 || entry.Id == 0 && entry.Kind != 0 || !ids.Add(entry.Id))
                    throw new ProtocolException("Invalid tool case identity");
                result.Entries.Add(entry);
            }
            if (!ids.Contains(0) || r.More) throw new ProtocolException("Incomplete tool case snapshot");
            return result;
        }
    }
}
