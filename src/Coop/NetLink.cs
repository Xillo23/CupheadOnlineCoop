using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CupheadLanCoop
{
    public static class Msg
    {
        public const byte Hello = 1, Welcome = 2, Reject = 3, Start = 4, Input = 5, DataHash = 6, DataFix = 7,
            Hash = 8, Ping = 9, Pong = 10, Bye = 11, Arrive = 12, Snap = 13, Resync = 14, Pending = 15;

        public static byte[] Build(Action<BinaryWriter> write)
        {
            using (MemoryStream m = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(m);
                write(w);
                w.Flush();
                return m.ToArray();
            }
        }

        public static BinaryReader Read(byte[] body) { return new BinaryReader(new MemoryStream(body)); }
    }

    public sealed class NetMessage
    {
        public byte Type;
        public byte[] Body;
    }

    // One TCP peer. Frames: [int32 length][byte type][body]. A background thread
    // fills an unbounded queue (inputs are tiny); only a high safety limit applies.
    public sealed class NetLink
    {
        public const int MaxFrame = 16 * 1024 * 1024;
        const int MaxQueued = 200000;
        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly Queue<NetMessage> inbox = new Queue<NetMessage>();
        readonly object gate = new object(), sendGate = new object();
        volatile bool closed;
        string closeReason = "";
        public readonly string Remote;
        public static Action<string> Log = delegate { };

        NetLink(TcpClient c)
        {
            client = c;
            client.NoDelay = true;
            client.ReceiveBufferSize = 1 << 20;
            client.SendBufferSize = 1 << 20;
            stream = c.GetStream();
            Remote = c.Client.RemoteEndPoint.ToString();
            Thread t = new Thread(ReceiveLoop);
            t.IsBackground = true;
            t.Name = "CupheadLanCoop RX";
            t.Start();
        }

        public static NetLink Wrap(TcpClient c) { return new NetLink(c); }

        public static NetLink Connect(string host, int port, int timeoutMs, out string error)
        {
            error = "";
            TcpClient c = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                IAsyncResult ar = c.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    c.Close();
                    error = "#no_answer001f" + host;
                    return null;
                }
                c.EndConnect(ar);
                return new NetLink(c);
            }
            catch (Exception ex)
            {
                try { c.Close(); } catch { }
                error = "#connect_failed001f" + host + "001e" + ex.Message;
                return null;
            }
        }

        public bool Connected { get { return !closed; } }
        public int Pending { get { lock (gate) return inbox.Count; } }
        public string CloseReason { get { lock (gate) return closeReason; } }

        public void Send(byte type, byte[] body)
        {
            if (closed) return;
            if (body == null) body = new byte[0];
            byte[] frame = new byte[5 + body.Length];
            int len = body.Length + 1;
            frame[0] = (byte)len; frame[1] = (byte)(len >> 8); frame[2] = (byte)(len >> 16); frame[3] = (byte)(len >> 24);
            frame[4] = type;
            Buffer.BlockCopy(body, 0, frame, 5, body.Length);
            try
            {
                lock (sendGate) stream.Write(frame, 0, frame.Length);
            }
            catch (Exception ex) { Close("#conn_broken001e" + ex.Message); }
        }

        public bool TryReceive(out NetMessage message)
        {
            lock (gate)
            {
                if (inbox.Count > 0) { message = inbox.Dequeue(); return true; }
            }
            message = null;
            return false;
        }

        public void Close(string reason)
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                closeReason = reason;
            }
            Log("Link chiuso: " + reason);
            try { client.Close(); } catch { }
        }

        void ReceiveLoop()
        {
            byte[] header = new byte[4];
            try
            {
                while (!closed)
                {
                    if (!ReadExact(header, 4)) { Close("#peer_closed"); return; }
                    int len = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                    if (len < 1 || len > MaxFrame) { Close("#conn_broken001e" + len + " B"); return; }
                    byte[] data = new byte[len];
                    if (!ReadExact(data, len)) { Close("#conn_broken"); return; }
                    NetMessage m = new NetMessage();
                    m.Type = data[0];
                    m.Body = new byte[len - 1];
                    Buffer.BlockCopy(data, 1, m.Body, 0, len - 1);
                    lock (gate)
                    {
                        if (inbox.Count >= MaxQueued) { closed = true; closeReason = "#conn_broken001equeue"; }
                        else inbox.Enqueue(m);
                    }
                }
            }
            catch (Exception ex)
            {
                Close("#conn_broken001e" + ex.Message);
            }
        }

        bool ReadExact(byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }

    public sealed class NetHost
    {
        readonly TcpListener listener;
        public readonly int Port;

        NetHost(TcpListener l, int port) { listener = l; Port = port; }

        public static NetHost Listen(int port, out string error)
        {
            error = "";
            try
            {
                TcpListener l = new TcpListener(IPAddress.Any, port);
                l.Start(1);
                return new NetHost(l, port);
            }
            catch (Exception ex)
            {
                error = "#port_busy001f" + port + "001e" + ex.Message;
                return null;
            }
        }

        public NetLink TryAccept()
        {
            try
            {
                if (!listener.Pending()) return null;
                return NetLink.Wrap(listener.AcceptTcpClient());
            }
            catch (Exception ex)
            {
                NetLink.Log("Accept fallito: " + ex.Message);
                return null;
            }
        }

        public void Stop() { try { listener.Stop(); } catch { } }
    }
}
