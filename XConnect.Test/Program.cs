using FlyByWireless.XConnect;
using System;
using System.Threading.Tasks;

TaskCompletionSource tcs;
for (; ; )
{
    tcs = new();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        tcs.TrySetResult();
    };
    Beacon beacon = null!;
    Console.WriteLine("Discovering...");
    await foreach (var b in XConnect.DiscoverAsync())
    {
        if (b.BeaconVersion.Major == 1 &&
            b.ApplicationHostId == ApplicationHostId.XPlane &&
            b.Role == Role.Master)
        {
            beacon = b;
            Console.WriteLine($"Discovered X-Plane {b.VersionString} at {b.RemoteEndPoint.Address}:{b.Port}");
            break;
        }
    }
    using var x = new XConnect(beacon)
    {
        RPosPerSecond = 2
    };
    x.ReceiveTimeout += _ => tcs.TrySetResult();
    x.RPos += (_, rpos) =>
    {
        Console.WriteLine($"Moved to ({rpos.Latitude}, {rpos.Longitude})");
    };
    //await x.AlertAsync("Test");
    await tcs.Task;
}