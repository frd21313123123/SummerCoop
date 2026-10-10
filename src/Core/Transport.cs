using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SummerCoop.Core
{
    public enum TransportEventKind { Connected, Data, Disconnected, Error }
    public sealed class TransportEvent
    {
        public TransportEventKind Kind;
        public string Peer = "", Error = "";
        public byte[] Data;
        public bool Reliable;
    }
    public interface ITransport : IDisposable
    {
        string HostPeer { get; }
        bool IsHost { get; }
        IList<string> Peers { get; }
        bool Poll(out TransportEvent item);
        void Send(string peer, byte[] bytes, bool reliable);
        void Disconnect(string peer);
        void Update();
    }
    public sealed class EventQueue
    {
        private readonly Queue<TransportEvent> items = new Queue<TransportEvent>();
        private int byteCount;
        public void Add(TransportEvent e)
        {
            lock (items)
            {
                int size = e.Data == null ? 0 : e.Data.Length;
                if (items.Count >= 4096 || byteCount + size > 8388608)
                {
                    if (e.Kind == TransportEventKind.Data) { if (!e.Reliable) return; throw new IOException("Incoming queue overflow"); }
                    // The disconnect/error notification must never crash a background thread.
                    while (items.Count > 2048 || byteCount + size > 8388608) { TransportEvent old = items.Dequeue(); byteCount -= old.Data == null ? 0 : old.Data.Length; }
                }
                items.Enqueue(e); byteCount += e.Data == null ? 0 : e.Data.Length;
            }
        }
        public bool Poll(out TransportEvent e)
        {
            lock (items)
            {
                if (items.Count == 0) { e = null; return false; }
                e = items.Dequeue(); byteCount -= e.Data == null ? 0 : e.Data.Length; return true;
            }
        }
    }

    public sealed class LanTransport : ITransport
    {
        private sealed class Connection
        {
            public string Id;
            public TcpClient Client;
            public byte[] Token;
            public IPEndPoint DatagramEndpoint;
            public Queue<byte[]> Outbound = new Queue<byte[]>();
            public AutoResetEvent Wake = new AutoResetEvent(false);
            public volatile bool Closed;
            public int QueuedBytes;
        }
        private readonly Dictionary<string, Connection> connections = new Dictionary<string, Connection>();
        private readonly EventQueue events = new EventQueue();
        private readonly object gate = new object();
        private TcpListener listener;
        private UdpClient udp;
        private volatile bool closed;
        private int port;
        public string HostPeer { get { return "host"; } }
        public bool IsHost { get; private set; }
        public IList<string> Peers { get { lock (gate) return new List<string>(connections.Keys); } }
        public static LanTransport Host(int port)
        {
            if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException("port");
            LanTransport t = new LanTransport { IsHost = true, port = port };
            try
            {
                t.udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                t.listener = new TcpListener(IPAddress.Any, port); t.listener.Start(4);
                t.Background(t.Accept); t.Background(t.ReadDatagrams); return t;
            }
            catch { t.Dispose(); throw; }
        }
        public static LanTransport Join(string address, int port)
        {
            if (string.IsNullOrEmpty(address) || port < 1024 || port > 65535) throw new ArgumentException("Invalid endpoint");
            LanTransport t = new LanTransport { port = port };
            t.udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            t.Background(delegate
            {
                TcpClient client = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    IAsyncResult request = client.BeginConnect(address, port, null, null);
                    if (!request.AsyncWaitHandle.WaitOne(10000, false)) throw new IOException("Connection timed out");
                    client.EndConnect(request);
                    if (t.closed) { client.Close(); return; }
                    t.Register(new Connection { Id = "host", Client = client });
                }
                catch (Exception e) { client.Close(); if (!t.closed) t.events.Add(new TransportEvent { Kind = TransportEventKind.Error, Error = e.Message }); }
            });
            t.Background(t.ReadDatagrams); return t;
        }
        private void Background(ThreadStart run)
        {
            Thread thread = new Thread(run); thread.IsBackground = true; thread.Start();
        }
        private void Accept()
        {
            while (!closed)
            {
                try
                {
                    TcpClient client = listener.AcceptTcpClient();
                    lock (gate) { if (closed || connections.Count >= Protocol.MaxPlayers - 1) { client.Close(); continue; } }
                    Register(new Connection { Id = Guid.NewGuid().ToString("N"), Client = client, Token = Guid.NewGuid().ToByteArray() });
                }
                catch (Exception e) { if (!closed) events.Add(new TransportEvent { Kind = TransportEventKind.Error, Error = e.Message }); }
            }
        }
        private void Register(Connection c)
        {
            c.Client.NoDelay = true; c.Client.SendTimeout = 5000;
            lock (gate) { if (closed) { c.Client.Close(); return; } connections.Add(c.Id, c); }
            Background(delegate { WriteStream(c); });
            Background(delegate { ReadStream(c); });
            if (IsHost)
            {
                byte[] negotiation = new byte[17]; negotiation[0] = 255; Array.Copy(c.Token, 0, negotiation, 1, 16);
                Queue(c, negotiation);
                events.Add(new TransportEvent { Kind = TransportEventKind.Connected, Peer = c.Id });
            }
        }
        private static byte[] Exact(NetworkStream stream, int count)
        {
            byte[] bytes = new byte[count]; int offset = 0;
            while (offset < count) { int n = stream.Read(bytes, offset, count - offset); if (n == 0) throw new EndOfStreamException(); offset += n; }
            return bytes;
        }
        private void ReadStream(Connection c)
        {
            try
            {
                NetworkStream stream = c.Client.GetStream();
                while (!closed && !c.Closed)
                {
                    byte[] header = Exact(stream, 4);
                    int count = BitConverter.ToInt32(header, 0);
                    if (count < 15 || count > Protocol.MaxFrame) throw new ProtocolException("Invalid TCP frame length");
                    byte[] bytes = Exact(stream, count);
                    if (!IsHost && c.Token == null)
                    {
                        if (count != 17 || bytes[0] != 255) throw new ProtocolException("Missing transport negotiation");
                        c.Token = new byte[16]; Array.Copy(bytes, 1, c.Token, 0, 16);
                        c.DatagramEndpoint = new IPEndPoint(((IPEndPoint)c.Client.Client.RemoteEndPoint).Address, port);
                        events.Add(new TransportEvent { Kind = TransportEventKind.Connected, Peer = c.Id }); continue;
                    }
                    // Validate before queueing, so invalid input cannot affect game state.
                    Codec.Open(bytes);
                    events.Add(new TransportEvent { Kind = TransportEventKind.Data, Peer = c.Id, Data = bytes, Reliable = true });
                }
            }
            catch (Exception e) { Close(c, e.Message); }
        }
        private void Queue(Connection c, byte[] bytes)
        {
            lock (c.Outbound)
            {
                if (c.Closed) return;
                if (c.QueuedBytes + bytes.Length > 8388608) { Close(c, "Outgoing queue overflow"); return; }
                c.Outbound.Enqueue(bytes); c.QueuedBytes += bytes.Length;
            }
            c.Wake.Set();
        }
        private void WriteStream(Connection c)
        {
            try
            {
                NetworkStream stream = c.Client.GetStream();
                while (!closed && !c.Closed)
                {
                    byte[] bytes = null;
                    lock (c.Outbound) { if (c.Outbound.Count != 0) { bytes = c.Outbound.Dequeue(); c.QueuedBytes -= bytes.Length; } }
                    if (bytes == null) { c.Wake.WaitOne(1000, false); continue; }
                    byte[] prefix = BitConverter.GetBytes(bytes.Length);
                    stream.Write(prefix, 0, prefix.Length); stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception e) { Close(c, e.Message); }
        }
        private void ReadDatagrams()
        {
            while (!closed)
            {
                try
                {
                    IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                    byte[] bytes = udp.Receive(ref sender);
                    if (bytes.Length < 31 || bytes.Length > Protocol.MaxDatagram) continue;
                    Connection found = null;
                    lock (gate)
                    {
                        foreach (Connection c in connections.Values)
                        {
                            if (c.Token == null || c.Closed) continue;
                            if (!((IPEndPoint)c.Client.Client.RemoteEndPoint).Address.Equals(sender.Address)) continue;
                            bool equal = true; for (int i = 0; i < 16; i++) if (c.Token[i] != bytes[i]) equal = false;
                            if (equal) { found = c; break; }
                        }
                        if (found != null && IsHost) found.DatagramEndpoint = sender;
                    }
                    if (found == null) continue;
                    byte[] frame = new byte[bytes.Length - 16]; Array.Copy(bytes, 16, frame, 0, frame.Length);
                    try { Codec.Open(frame); } catch (IOException) { continue; }
                    events.Add(new TransportEvent { Kind = TransportEventKind.Data, Peer = found.Id, Data = frame, Reliable = false });
                }
                catch (Exception e) { if (!closed) events.Add(new TransportEvent { Kind = TransportEventKind.Error, Error = e.Message }); }
            }
        }
        public void Send(string peer, byte[] bytes, bool reliable)
        {
            Connection c; lock (gate) { if (!connections.TryGetValue(peer, out c)) return; }
            if (reliable) { if (bytes.Length > Protocol.MaxFrame) throw new ProtocolException("Frame too large"); Queue(c, bytes); return; }
            if (bytes.Length + 16 > Protocol.MaxDatagram) throw new ProtocolException("Datagram too large");
            if (c.Token == null || c.DatagramEndpoint == null || c.Closed) return;
            byte[] data = new byte[bytes.Length + 16]; Array.Copy(c.Token, data, 16); Array.Copy(bytes, 0, data, 16, bytes.Length);
            try { udp.Send(data, data.Length, c.DatagramEndpoint); } catch (SocketException) { }
        }
        private void Close(Connection c, string reason)
        {
            lock (gate)
            {
                if (c.Closed) return; c.Closed = true; connections.Remove(c.Id);
            }
            try { c.Client.Close(); c.Wake.Set(); } catch (ObjectDisposedException) { }
            if (!closed) events.Add(new TransportEvent { Kind = TransportEventKind.Disconnected, Peer = c.Id, Error = reason });
        }
        public void Disconnect(string peer) { Connection c; lock (gate) { if (!connections.TryGetValue(peer, out c)) return; } Close(c, "Disconnected"); }
        public bool Poll(out TransportEvent item) { return events.Poll(out item); }
        public void Update() { }
        public void Dispose()
        {
            if (closed) return; closed = true;
            if (listener != null) listener.Stop();
            if (udp != null) udp.Close();
            List<Connection> list; lock (gate) list = new List<Connection>(connections.Values);
            foreach (Connection c in list) Close(c, "Stopped");
        }
    }
}
