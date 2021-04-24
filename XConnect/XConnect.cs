using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlyByWireless.XConnect
{
    public sealed class XConnect : IDisposable
    {
        public static async IAsyncEnumerable<Beacon> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            static unsafe Beacon? Parse(UdpReceiveResult r)
            {
                Span<byte> s = r.Buffer;
                return s.Length > 4 && MemoryMarshal.AsRef<uint>(s) == 0x4E_43_45_42 && s[5] == 1 ?
                    new(r.RemoteEndPoint, s[5..]) :
                    null;
            }

            using UdpClient client = new()
            {
                ExclusiveAddressUse = false
            };
            using var cancel = cancellationToken.Register(() => client.Dispose());
            client.JoinMulticastGroup(new(0x0101FFEF));
            client.Client.Bind(new IPEndPoint(IPAddress.Any, 49707));
            while (!cancellationToken.IsCancellationRequested)
                if (Parse(await client.ReceiveAsync()) is Beacon b)
                    yield return b;
        }

        readonly IPEndPoint RemoteEndPoint;

        readonly UdpClient Client;

        public event Action<XConnect>? ReceiveTimeout;

        int _RPosPerSecond;
        public int RPosPerSecond
        {
            get => _RPosPerSecond;
            set
            {
                if (_RPosPerSecond != value)
                {
                    _ = Task.Run(async () =>
                    {
                        var a = ArrayPool<byte>.Shared.Rent(8);
                        try
                        {
                            await Client.SendAsync(a, Encoding.ASCII.GetBytes($"RPOS\0{value}\0", a), RemoteEndPoint).ConfigureAwait(false);
                            _RPosPerSecond = value;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(a);
                        }
                    });
                }
            }
        }

        public event EventHandler<RPos>? RPos;

        public XConnect(Beacon beacon) : this(new IPEndPoint(beacon.RemoteEndPoint.Address, beacon.Port)) { }

        public XConnect(IPEndPoint remoteEndPoint)
        {
            RemoteEndPoint = remoteEndPoint;
            Client = new(0, remoteEndPoint.AddressFamily);
            Client.Client.Blocking = false;
            Client.Client.UseOnlyOverlappedIO = true;
            void Dispatch(ReadOnlySpan<byte> buffer)
            {
                if (buffer.Length > 4)
                {
                    switch (MemoryMarshal.AsRef<uint>(buffer))
                    {
                        case 0x53_4F_50_52:
                            RPos?.Invoke(this, MemoryMarshal.AsRef<RPos>(buffer[5..]));
                            break;
                    }
                }
            }
            _ = Task.Factory.StartNew(async () =>
            {
                try
                {
                    for (; ; )
                    {
                        var r = Client.ReceiveAsync();
                        if (!await Task.Run(() => r.Wait(2000)).ConfigureAwait(false))
                        {
                            ReceiveTimeout?.Invoke(this);
                            continue;
                        }
                        Dispatch(r.Result.Buffer);
                    }
                }
                catch (ObjectDisposedException) { }
            });
        }

        public void Dispose() => Client.Dispose();

        public async Task AlertAsync(string message)
        {
            var a = ArrayPool<byte>.Shared.Rent(965);
            try
            {
                _ = Encoding.UTF8.GetBytes("ALRT\0" + message, a.AsSpan(0, 965));
                await Client.SendAsync(a, 965).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }
    }
}