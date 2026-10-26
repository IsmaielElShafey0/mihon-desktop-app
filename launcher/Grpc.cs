using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MihonWindows;

/// <summary>
/// Just enough HTTP/2 to make gRPC calls to the Android emulator: cleartext, prior knowledge, one connection.
/// .NET Framework has no HTTP/2 client, and the emulator only listens on localhost (with -grpc-use-token).
/// Response headers and trailers aren't decoded; a call simply ends when the emulator closes its stream.
/// </summary>
sealed class GrpcConnection : IDisposable
{
    const byte Data = 0, Headers = 1, RstStream = 3, Settings = 4, Ping = 6, GoAway = 7, WindowUpdate = 8;
    const byte EndStream = 0x1, Ack = 0x1, EndHeaders = 0x4, Padded = 0x8;
    const int ReceiveWindow = 16 << 20;

    readonly TcpClient tcp;
    readonly NetworkStream net;
    readonly string authority;
    readonly string token;
    readonly object writeGate = new();
    readonly object flowGate = new();
    readonly Dictionary<int, GrpcCall> calls = new();
    int nextStream = 1;
    long sendWindow = 65535;
    int peerInitialWindow = 65535;
    int peerMaxFrame = 16384;

    public bool Closed { get; private set; }

    public GrpcConnection(int port, string token)
    {
        this.token = token;
        tcp = new TcpClient { NoDelay = true };
        tcp.Connect("127.0.0.1", port);
        net = tcp.GetStream();
        authority = "127.0.0.1:" + port;
        lock (writeGate)
        {
            var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
            net.Write(preface, 0, preface.Length);
            var settings = new byte[12];
            Put16(settings, 0, 0x2);                // ENABLE_PUSH = 0
            Put32(settings, 2, 0);
            Put16(settings, 6, 0x4);                // INITIAL_WINDOW_SIZE
            Put32(settings, 8, ReceiveWindow);
            WriteFrame(Settings, 0, 0, settings, 0, settings.Length);
            var grow = new byte[4];
            Put32(grow, 0, (1 << 30) - 65535);       // connection window: 64 KB -> 1 GB
            WriteFrame(WindowUpdate, 0, 0, grow, 0, 4);
        }
        new Thread(ReadLoop) { IsBackground = true, Name = "gRPC reader" }.Start();
    }

    /// <summary>Starts a call, e.g. "/android.emulation.control.EmulatorController/streamScreenshot".</summary>
    public GrpcCall Start(string method)
    {
        lock (writeGate)
        {
            if (Closed) throw new IOException("The emulator closed the gRPC connection.");
            var call = new GrpcCall(this, nextStream, peerInitialWindow);
            nextStream += 2;
            lock (calls) calls[call.Id] = call;
            var block = new MemoryStream();
            foreach (var (name, value) in new[]
            {
                (":method", "POST"), (":scheme", "http"), (":path", method), (":authority", authority),
                ("content-type", "application/grpc"), ("te", "trailers"), ("authorization", "Bearer " + token),
            })
            {
                // HPACK literal header field without indexing, with a literal name and no Huffman coding.
                block.WriteByte(0);
                WriteHpackString(block, name);
                WriteHpackString(block, value);
            }
            var bytes = block.ToArray();
            WriteFrame(Headers, EndHeaders, call.Id, bytes, 0, bytes.Length);
            return call;
        }
    }

    internal void Send(GrpcCall call, byte[] payload, bool end)
    {
        var offset = 0;
        do
        {
            int chunk;
            lock (flowGate)
            {
                while (true)
                {
                    if (Closed || call.Ended) throw new IOException("The gRPC call has ended.");
                    var room = Math.Min(sendWindow, call.SendWindow);
                    var left = payload.Length - offset;
                    if (left == 0 || room > 0)
                    {
                        chunk = (int)Math.Min(Math.Min(room, peerMaxFrame), left);
                        sendWindow -= chunk;
                        call.SendWindow -= chunk;
                        break;
                    }
                    Monitor.Wait(flowGate, 1000);
                }
            }
            var last = offset + chunk >= payload.Length;
            lock (writeGate) WriteFrame(Data, last && end ? EndStream : (byte)0, call.Id, payload, offset, chunk);
            offset += chunk;
        }
        while (offset < payload.Length);
    }

    void Consumed(GrpcCall call, int bytes)
    {
        // Hand the receive window back in large steps, so long streams never stall.
        call.Unacknowledged += bytes;
        if (call.Unacknowledged < 1 << 20) return;
        var update = new byte[4];
        Put32(update, 0, call.Unacknowledged);
        lock (writeGate)
        {
            WriteFrame(WindowUpdate, 0, 0, update, 0, 4);
            if (!call.Ended) WriteFrame(WindowUpdate, 0, call.Id, update, 0, 4);
        }
        call.Unacknowledged = 0;
    }

    void ReadLoop()
    {
        var header = new byte[9];
        var payload = new byte[1 << 16];
        try
        {
            while (true)
            {
                ReadExactly(header, 9);
                var length = (header[0] << 16) | (header[1] << 8) | header[2];
                var type = header[3];
                var flags = header[4];
                var id = Get32(header, 5) & 0x7FFFFFFF;
                if (payload.Length < length) payload = new byte[length];
                ReadExactly(payload, length);
                GrpcCall call = null;
                if (id != 0) lock (calls) calls.TryGetValue(id, out call);

                switch (type)
                {
                    case Data:
                        if (call != null)
                        {
                            int start = 0, end = length;
                            if ((flags & Padded) != 0) { start = 1; end -= payload[0]; }
                            call.OnData(payload, start, end - start);
                            Consumed(call, length);
                        }
                        if ((flags & EndStream) != 0) call?.OnEnd(null);
                        break;
                    case Headers:
                        if ((flags & EndStream) != 0) call?.OnEnd(null);
                        break;
                    case RstStream:
                        call?.OnEnd("the emulator reset the call (HTTP/2 error " + Get32(payload, 0) + ")");
                        break;
                    case Settings:
                        if ((flags & Ack) != 0) break;
                        for (var i = 0; i + 6 <= length; i += 6)
                        {
                            var key = (payload[i] << 8) | payload[i + 1];
                            var value = Get32(payload, i + 2);
                            if (key == 0x4)
                            {
                                lock (flowGate)
                                {
                                    var delta = value - peerInitialWindow;
                                    peerInitialWindow = value;
                                    lock (calls) foreach (var open in calls.Values) open.SendWindow += delta;
                                    Monitor.PulseAll(flowGate);
                                }
                            }
                            else if (key == 0x5) peerMaxFrame = value;
                        }
                        lock (writeGate) WriteFrame(Settings, Ack, 0, payload, 0, 0);
                        break;
                    case Ping:
                        if ((flags & Ack) == 0) lock (writeGate) WriteFrame(Ping, Ack, 0, payload, 0, length);
                        break;
                    case WindowUpdate:
                        lock (flowGate)
                        {
                            var increment = Get32(payload, 0) & 0x7FFFFFFF;
                            if (id == 0) sendWindow += increment;
                            else if (call != null) call.SendWindow += increment;
                            Monitor.PulseAll(flowGate);
                        }
                        break;
                    case GoAway:
                        throw new IOException("The emulator ended the gRPC connection.");
                }
                if (call is { Ended: true }) lock (calls) calls.Remove(call.Id);
            }
        }
        catch (Exception e)
        {
            Closed = true;
            lock (flowGate) Monitor.PulseAll(flowGate);
            List<GrpcCall> open;
            lock (calls) open = new List<GrpcCall>(calls.Values);
            foreach (var call in open) call.OnEnd(e.Message);
        }
    }

    void ReadExactly(byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = net.Read(buffer, read, count - read);
            if (n <= 0) throw new IOException("The emulator closed the gRPC connection.");
            read += n;
        }
    }

    void WriteFrame(byte type, byte flags, int stream, byte[] payload, int offset, int count)
    {
        var frame = new byte[9 + count];
        frame[0] = (byte)(count >> 16);
        frame[1] = (byte)(count >> 8);
        frame[2] = (byte)count;
        frame[3] = type;
        frame[4] = flags;
        Put32(frame, 5, stream);
        Buffer.BlockCopy(payload, offset, frame, 9, count);
        net.Write(frame, 0, frame.Length);
    }

    static void WriteHpackString(Stream output, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        // String length as an HPACK integer with a 7-bit prefix (the Huffman bit stays clear).
        var length = bytes.Length;
        if (length < 127) output.WriteByte((byte)length);
        else
        {
            output.WriteByte(127);
            for (length -= 127; length >= 128; length /= 128) output.WriteByte((byte)(length % 128 + 128));
            output.WriteByte((byte)length);
        }
        output.Write(bytes, 0, bytes.Length);
    }

    static void Put16(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 8);
        b[at + 1] = (byte)v;
    }

    static void Put32(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    internal static int Get32(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    public void Dispose()
    {
        Closed = true;
        tcp.Close();
    }
}

/// <summary>One gRPC call: requests go out with Send; replies arrive through Message, and End fires once.</summary>
sealed class GrpcCall
{
    readonly GrpcConnection connection;
    readonly MemoryStream partial = new();
    internal long SendWindow;
    internal int Unacknowledged;

    public int Id { get; }
    public bool Ended { get; private set; }
    public event Action<byte[]> Message;
    public event Action<string> End;

    internal GrpcCall(GrpcConnection connection, int id, int window)
    {
        this.connection = connection;
        Id = id;
        SendWindow = window;
    }

    /// <summary>Sends one protobuf message; <paramref name="last"/> also closes the request side.</summary>
    public void Send(byte[] message, bool last = false)
    {
        var framed = new byte[5 + message.Length];
        framed[1] = (byte)(message.Length >> 24);
        framed[2] = (byte)(message.Length >> 16);
        framed[3] = (byte)(message.Length >> 8);
        framed[4] = (byte)message.Length;
        Buffer.BlockCopy(message, 0, framed, 5, message.Length);
        connection.Send(this, framed, last);
    }

    internal void OnData(byte[] buffer, int offset, int count)
    {
        partial.Write(buffer, offset, count);
        var bytes = partial.GetBuffer();
        var length = (int)partial.Length;
        var at = 0;
        while (length - at >= 5)
        {
            var size = GrpcConnection.Get32(bytes, at + 1);
            if (length - at - 5 < size) break;
            var message = new byte[size];
            Buffer.BlockCopy(bytes, at + 5, message, 0, size);
            at += 5 + size;
            Message?.Invoke(message);
        }
        if (at > 0)
        {
            var rest = length - at;
            Buffer.BlockCopy(bytes, at, bytes, 0, rest);
            partial.SetLength(rest);
        }
    }

    internal void OnEnd(string error)
    {
        if (Ended) return;
        Ended = true;
        End?.Invoke(error);
    }
}

/// <summary>Protocol buffers encoding and decoding for the few emulator messages the launcher uses.</summary>
sealed class Pb
{
    readonly MemoryStream output = new();

    public Pb Int(int field, long value)
    {
        if (value == 0) return this;
        Key(field, 0);
        Varint((ulong)value);
        return this;
    }

    public Pb Bytes(int field, byte[] value)
    {
        Key(field, 2);
        Varint((ulong)value.Length);
        output.Write(value, 0, value.Length);
        return this;
    }

    public Pb Text(int field, string value) => value == null ? this : Bytes(field, Encoding.UTF8.GetBytes(value));

    public Pb Message(int field, Pb value) => Bytes(field, value.ToArray());

    public byte[] ToArray() => output.ToArray();

    void Key(int field, int wireType) => Varint((ulong)((field << 3) | wireType));

    void Varint(ulong value)
    {
        for (; value >= 0x80; value >>= 7) output.WriteByte((byte)(value | 0x80));
        output.WriteByte((byte)value);
    }

    /// <summary>A message's fields: varints as Number, length-delimited fields as Bytes.</summary>
    public static IEnumerable<(int Field, ulong Number, ArraySegment<byte> Bytes)> Read(ArraySegment<byte> data)
    {
        var bytes = data.Array;
        var at = data.Offset;
        var end = data.Offset + data.Count;
        while (at < end)
        {
            var key = ReadVarint(bytes, ref at);
            var field = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0:
                    yield return (field, ReadVarint(bytes, ref at), default);
                    break;
                case 1:
                    at += 8;
                    break;
                case 2:
                    var length = (int)ReadVarint(bytes, ref at);
                    yield return (field, 0, new ArraySegment<byte>(bytes, at, length));
                    at += length;
                    break;
                case 5:
                    at += 4;
                    break;
                default:
                    yield break;
            }
        }
    }

    static ulong ReadVarint(byte[] data, ref int at)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) return value;
        }
    }
}
