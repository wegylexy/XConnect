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
    {
        var m = 0f;
        x.RegisterDataRef("sim/cockpit2/radios/actuators/audio_com_selection_man", (_, c) =>
        {
            if (m != c)
            {
                m = c;
                Console.WriteLine(@$"Selected COM {c switch
                {
                    6 => 1,
                    7 => 2,
                    _ => default
                }}");
            }
        }, 5);
    }
    //await x.AlertAsync("Test");
    await tcs.Task;
}