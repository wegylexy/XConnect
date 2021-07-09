using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlyByWireless.XConnect
{
    public enum StartType
    {
        Invalid,
        ResetLast = 5,
        Specific,
        GeneralArea,
        NearestAirport,
        SnapLoad,
        RampStart,
        TakeoffRunway,
        VisualRunway,
        InstrumentRunway,
        GrassStrip,
        DirtyStrip,
        GravelStrip,
        Seaplane,
        Helipad,
        CarrierCatShot,
        GliderTowed,
        GliderWinched,
        Formation,
        RefuelBoom,
        RefuelBasket,
        B52Drop,
        PiggybackWithShuttle,
        CarrierApproach,
        FrigateApproach,
        OilRigApproach,
        OilPlatformApproach,
        Shuttle1,
        Shuttle2,
        Shuttle3,
        Shuttle4,
        ShuttleGlide
    }

    public sealed class DataRef
    {
        public readonly XConnect XConnect;
        readonly int _id;
        readonly string _path;
        readonly EventHandler<float> _received;
        readonly ManualResetEvent _mres = new(true);

        internal DataRef(XConnect x, int id, string path, EventHandler<float> received) =>
            (XConnect, _id, _path, _received) = (x, id, path, received);

        int _RRefPerSecond;
        public int RRefPerSecond
        {
            get => _RRefPerSecond;
            set
            {
                if (_RRefPerSecond != value)
                {
                    _mres.Set();
                    _ = Task.Run(async () =>
                    {
                        var a = ArrayPool<byte>.Shared.Rent(413);
                        try
                        {
                            _ = Encoding.UTF8.GetBytes("RREF\0\0\0\0\0\0\0\0\0" + _path + '\0', a.AsSpan(0, 413));
                            var written = BitConverter.TryWriteBytes(a.AsSpan(5), value) &&
                                BitConverter.TryWriteBytes(a.AsSpan(9), _id);
                            Debug.Assert(written);
                            for (_mres.Reset(); ;)
                            {
                                var sent = await XConnect.Client.SendAsync(a, 413, XConnect.RemoteEndPoint).ConfigureAwait(false);
                                Debug.Assert(sent == 413);
                                if (_mres.WaitOne(1000 / value + 100))
                                {
                                    break;
                                }
                                Debug.WriteLine($"Missed RREF {_id} @ {value} Hz for {_path}");
                            }
                            _RRefPerSecond = value;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(a);
                        }
                    });
                }
            }
        }

        internal void Receive(float value)
        {
            _mres.Set();
            _received.Invoke(this, value);
        }
    }

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

        internal readonly IPEndPoint RemoteEndPoint;

        internal readonly UdpClient Client;

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

        int _RadRPerSecond;
        public int RadRPerSecond
        {
            get => _RadRPerSecond;
            set
            {
                if (_RadRPerSecond != value)
                {
                    _ = Task.Run(async () =>
                    {
                        var a = ArrayPool<byte>.Shared.Rent(8);
                        try
                        {
                            await Client.SendAsync(a, Encoding.ASCII.GetBytes($"RADR\0{value}\0", a), RemoteEndPoint).ConfigureAwait(false);
                            _RadRPerSecond = value;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(a);
                        }
                    });
                }
            }
        }

        int _DRefId;
        readonly ConcurrentDictionary<int, DataRef> _DRefs = new();

        public event EventHandler<RPos>? RPos;

        public event EventHandler<RadR>? RadR;

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
                        case 0x53_4F_50_52: // RPOS
                            RPos?.Invoke(this, MemoryMarshal.AsRef<RPos>(buffer[5..]));
                            break;
                        case 0x52_44_41_52: // RADR
                            RadR?.Invoke(this, MemoryMarshal.AsRef<RadR>(buffer[5..]));
                            break;
                        case 0x46_45_52_52: // RREF
                            {
                                foreach (var r in MemoryMarshal.Cast<byte, DRef>(buffer[5..]))
                                {
                                    if (_DRefs.TryGetValue(r.Id, out var d))
                                    {
                                        d.Receive(r.Value);
                                    }
                                }
                            }
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

        public void Dispose()
        {
            Client.Dispose();
            _DRefs.Clear();
        }

        public async Task MoveAircraft(int index, double latitude, double longitude, double trueAltitude, float trueHeading, float pitch, float roll)
        {
            var a = ArrayPool<byte>.Shared.Rent(45);
            try
            {
                _ = Encoding.UTF8.GetBytes("VEHX\0", a);
                var written = BitConverter.TryWriteBytes(a.AsSpan(5), index) &&
                    BitConverter.TryWriteBytes(a.AsSpan(9), latitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(17), longitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(25), trueAltitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(33), trueHeading) &&
                    BitConverter.TryWriteBytes(a.AsSpan(37), pitch) &&
                    BitConverter.TryWriteBytes(a.AsSpan(41), roll);
                Debug.Assert(written);
                await Client.SendAsync(a, 45).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }

        public Task ExecuteAsync(string command)
        {
            var b = Encoding.UTF8.GetBytes("CMD\0" + command + '\0');
            return Client.SendAsync(b, b.Length);
        }

        public DataRef RegisterDataRef(string path, EventHandler<float> received, int frequency = default)
        {
            var id = Interlocked.Increment(ref _DRefId);
            var r = new DataRef(this, id, path, received);
            var added = _DRefs.TryAdd(id, r);
            Debug.Assert(added);
            if (frequency != default)
            {
                r.RRefPerSecond = frequency;
            }
            return r;
        }

        public async Task SetDataRefAsync(string path, float value)
        {
            var a = ArrayPool<byte>.Shared.Rent(509);
            try
            {
                _ = Encoding.UTF8.GetBytes("DREF\0\0\0\0\0" + path + '\0', a.AsSpan(0, 509));
                var written = BitConverter.TryWriteBytes(a.AsSpan(5), value);
                Debug.Assert(written);
                await Client.SendAsync(a, 509).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }

        public async Task LoadAircraft(int index, string path, int livery)
        {
            var a = ArrayPool<byte>.Shared.Rent(165);
            try
            {
                _ = Encoding.UTF8.GetBytes("ACFN\0\0\0\0\0" + path + '\0', a.AsSpan(0, 159));
                var written = BitConverter.TryWriteBytes(a.AsSpan(5), index) &&
                    BitConverter.TryWriteBytes(a.AsSpan(161), livery);
                Debug.Assert(written);
                await Client.SendAsync(a, 165).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }

        public async Task RelocateAircraft(StartType start, int index, string airport, int runwayIndex, int runwayDirection, double latitude, double longitude, double trueAltitude, double trueHeading, double groundSpeed)
        {
            var a = ArrayPool<byte>.Shared.Rent(69);
            try
            {
                _ = Encoding.UTF8.GetBytes("PREL\0\0\0\0\0\0\0\0\0" + airport, a.AsSpan(0, 21));
                var written = BitConverter.TryWriteBytes(a.AsSpan(5), (int)start) &&
                    BitConverter.TryWriteBytes(a.AsSpan(9), index) &&
                    BitConverter.TryWriteBytes(a.AsSpan(21), runwayIndex) &&
                    BitConverter.TryWriteBytes(a.AsSpan(25), runwayDirection) &&
                    BitConverter.TryWriteBytes(a.AsSpan(29), latitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(37), longitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(45), trueAltitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(53), trueHeading) &&
                    BitConverter.TryWriteBytes(a.AsSpan(61), groundSpeed);
                Debug.Assert(written);
                await Client.SendAsync(a, 69).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }

        public async Task LoadAndRelocateAircraft(int index, string path, int livery, StartType start, string airport, int runwayIndex, int runwayDirection, double latitude, double longitude, double trueAltitude, double trueHeading, double groundSpeed)
        {
            var a = ArrayPool<byte>.Shared.Rent(229);
            try
            {
                _ = Encoding.UTF8.GetBytes("ACFN\0\0\0\0\0" + path + '\0', a.AsSpan(0, 159));
                _ = Encoding.UTF8.GetBytes(airport, a.AsSpan(173, 8));
                var written = BitConverter.TryWriteBytes(a.AsSpan(5), index) &&
                    BitConverter.TryWriteBytes(a.AsSpan(161), livery) &&
                    BitConverter.TryWriteBytes(a.AsSpan(165), (int)start) &&
                    BitConverter.TryWriteBytes(a.AsSpan(169), index) &&
                    BitConverter.TryWriteBytes(a.AsSpan(181), runwayIndex) &&
                    BitConverter.TryWriteBytes(a.AsSpan(185), runwayDirection) &&
                    BitConverter.TryWriteBytes(a.AsSpan(189), latitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(197), longitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(205), trueAltitude) &&
                    BitConverter.TryWriteBytes(a.AsSpan(213), trueHeading) &&
                    BitConverter.TryWriteBytes(a.AsSpan(221), groundSpeed);
                Debug.Assert(written);
                await Client.SendAsync(a, 229).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }

        async Task SiMoAsync(byte simo, string path)
        {
            var a = ArrayPool<byte>.Shared.Rent(161);
            try
            {
                _ = Encoding.UTF8.GetBytes("SIMO\0\0\0\0\0" + path + '\0', a.AsSpan(0, 159));
                a[5] = simo;
                await Client.SendAsync(a, 161).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }

        public Task SaveSituationAsync(string path) => SiMoAsync(0, path);

        public Task LoadSituationAsync(string path) => SiMoAsync(1, path);

        public Task SaveMoveAsync(string path) => SiMoAsync(2, path);

        public Task LoadMoveAsync(string path) => SiMoAsync(3, path);

        public async Task AlertAsync(string message)
        {
            var a = ArrayPool<byte>.Shared.Rent(965);
            try
            {
                _ = Encoding.UTF8.GetBytes("ALRT\0" + message + '\0', a.AsSpan(0, 965));
                await Client.SendAsync(a, 965).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(a); }
        }

        static readonly byte[] _Rese = new byte[] { 82, 69, 83, 69, 0 };
        public Task ResetFailuresAsync() => Client.SendAsync(_Rese, 5);

        static readonly byte[] _Quit = new byte[] { 81, 85, 73, 84, 0 };
        public Task QuitAsync() => Client.SendAsync(_Quit, 5);
    }
}