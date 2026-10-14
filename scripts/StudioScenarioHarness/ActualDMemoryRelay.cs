using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HollowKnightTAS.Diagnostics
{
    // Diagnostic transport only. The packet never enters logs or a file.
    public sealed class ActualDRelayPacket
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public long MovieFrame { get; set; }
        public long NativeFrame { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public static class ActualDMemoryRelay
    {
        private const int MaximumBytes = 32 * 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("HKTASDR1");
        private static void RequireName(string name)
        {
            if (!name.StartsWith("HKTAS-ActualD-", StringComparison.Ordinal)
                || name.Length != 46 || !Guid.TryParseExact(name.Substring(14), "N", out _))
                throw new InvalidDataException("Invalid single-use actual D relay name.");
        }
        public static NamedPipeServerStream CreateServer(string name)
        {
            RequireName(name);
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        public static void Validate(ActualDRelayPacket packet)
        {
            if (packet == null || packet.Data.Length < 1 || packet.Data.Length > MaximumBytes
                || packet.MovieFrame < 0 || packet.NativeFrame < 0 || packet.Sha256.Length != 64)
                throw new InvalidDataException("Actual D relay metadata exceeds its bound.");
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(packet.Data)).Replace("-", "").ToLowerInvariant() != packet.Sha256)
                    throw new InvalidDataException("Actual D relay complete packet digest differs.");
        }
        private static async Task ReadExactly(Stream stream, byte[] bytes, CancellationToken token)
        {
            var offset = 0;
            while (offset < bytes.Length) {
                var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Actual D relay ended before its bounded message.");
                offset += count;
            }
        }
        private static async Task Send(Stream stream, ActualDRelayPacket packet, CancellationToken token)
        {
            Validate(packet);
            byte[] header;
            using (var buffer = new MemoryStream()) {
                using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true)) {
                    writer.Write(Magic); writer.Write(packet.Data.Length); writer.Write(packet.MovieFrame);
                    writer.Write(packet.NativeFrame); writer.Write(Encoding.ASCII.GetBytes(packet.Sha256));
                }
                header = buffer.ToArray();
            }
            await stream.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
            await stream.WriteAsync(packet.Data, 0, packet.Data.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            var ack = new byte[1]; await ReadExactly(stream, ack, token).ConfigureAwait(false);
            if (ack[0] != 1) throw new InvalidDataException("Actual D relay receiver did not validate the packet.");
        }
        private static async Task<ActualDRelayPacket> Receive(Stream stream, CancellationToken token)
        {
            var header = new byte[92]; await ReadExactly(stream, header, token).ConfigureAwait(false);
            ActualDRelayPacket packet;
            using (var reader = new BinaryReader(new MemoryStream(header))) {
                var magic = reader.ReadBytes(8);
                if (Encoding.ASCII.GetString(magic) != Encoding.ASCII.GetString(Magic))
                    throw new InvalidDataException("Unknown actual D relay protocol.");
                var length = reader.ReadInt32();
                if (length < 1 || length > MaximumBytes) throw new InvalidDataException("Actual D relay allocation exceeds its bound.");
                packet = new ActualDRelayPacket { Data = new byte[length], MovieFrame = reader.ReadInt64(),
                    NativeFrame = reader.ReadInt64(), Sha256 = Encoding.ASCII.GetString(reader.ReadBytes(64)) };
            }
            await ReadExactly(stream, packet.Data, token).ConfigureAwait(false); Validate(packet);
            await stream.WriteAsync(new byte[] { 1 }, 0, 1, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false); return packet;
        }
        public static async Task<ActualDRelayPacket> ExchangeServer(NamedPipeServerStream stream,
            bool receive, ActualDRelayPacket packet, int remainingMilliseconds, System.Diagnostics.Process host)
        {
            using (var timeout = new CancellationTokenSource(remainingMilliseconds)) {
                var exchange = ExchangeConnectedServer(stream, receive, packet, timeout.Token);
                if (host != null) {
                    var exited = host.WaitForExitAsync(timeout.Token);
                    if (await Task.WhenAny(exchange, exited).ConfigureAwait(false) == exited && !exchange.IsCompleted) {
                        timeout.Cancel(); throw new IOException("Actual D stage host exited before its memory handoff.");
                    }
                }
                return await exchange.ConfigureAwait(false);
            }
        }
        private static async Task<ActualDRelayPacket> ExchangeConnectedServer(NamedPipeServerStream stream,
            bool receive, ActualDRelayPacket packet, CancellationToken token)
        {
            await stream.WaitForConnectionAsync(token).ConfigureAwait(false);
            if (receive) return await Receive(stream, token).ConfigureAwait(false);
            await Send(stream, packet, token).ConfigureAwait(false); return packet;
        }
        public static async Task<ActualDRelayPacket> ExchangeClient(string name, bool receive, ActualDRelayPacket packet)
        {
            RequireName(name);
            using (var timeout = new CancellationTokenSource(30000))
            using (var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)) {
                await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);
                if (receive) return await Receive(stream, timeout.Token).ConfigureAwait(false);
                await Send(stream, packet, timeout.Token).ConfigureAwait(false); return packet;
            }
        }
    }
}
