using System;
using SummerCoop.Core;
using MWCoop.Net;
using Session = MWCoop.Net.Session;

namespace MWCoop
{
    public static class MscSaveTransfer
    {
        static NativeSaveBundle incoming;
        public static bool Receiving { get { return incoming != null; } }
        public static int Expected { get { return incoming == null ? 0 : incoming.Expected; } }
        public static int Received { get { return incoming == null ? 0 : incoming.Received; } }
        public static void OnMessage(Msg type, NetReader reader)
        {
            if (Session.IsHost) throw new ProtocolException("Host cannot receive a world save");
            try
            {
                switch (type)
                {
                    case Msg.SaveBegin:
                        if (incoming != null || PlayerSync.InGame) throw new ProtocolException("Overlapping MSC save transfer");
                        int count = reader.U8(); if (count > NativeSaveBundle.Names.Length) throw new ProtocolException("MSC save file count exceeded");
                        string[] names = new string[count], hashes = new string[count]; int[] sizes = new int[count];
                        for (int i = 0; i < count; i++) { names[i] = reader.Str(); sizes[i] = reader.I32(); hashes[i] = reader.Str(); }
                        if (reader.More) throw new ProtocolException("Trailing MSC save manifest data");
                        incoming = new NativeSaveBundle(names, sizes, hashes); SaveTransfer.Done = SaveTransfer.Received = false;
                        SaveTransfer.HostHasSave = count > 0; SaveTransfer.Progress = 0;
                        break;
                    case Msg.SaveChunk:
                        if (incoming == null) throw new ProtocolException("MSC save manifest missing");
                        int index = reader.U8(), offset = reader.I32(); byte[] bytes = reader.Bytes();
                        if (reader.More) throw new ProtocolException("Trailing MSC save chunk data");
                        incoming.Append(index, offset, bytes);
                        SaveTransfer.Progress = incoming.Expected > 0 ? (float)incoming.Received / incoming.Expected : 0;
                        break;
                    case Msg.SaveEnd:
                        if (incoming == null || reader.U8() != incoming.Count || reader.More) throw new ProtocolException("Invalid MSC save end");
                        if (!MscProfile.Isolated) throw new ProtocolException("MSC save destination is not isolated");
                        incoming.Commit(MscProfile.SaveDirectory); incoming = null;
                        SaveTransfer.Done = SaveTransfer.Received = true; SaveTransfer.Progress = 1;
                        Log.Info("MSC save received and verified in " + MscProfile.SaveDirectory);
                        break;
                }
            }
            catch { incoming = null; SaveTransfer.Done = SaveTransfer.Received = false; throw; }
        }
        public static void Reset() { incoming = null; SaveTransfer.Done = SaveTransfer.Received = false; SaveTransfer.Progress = 0; }
    }
}
