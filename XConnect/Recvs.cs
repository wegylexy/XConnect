using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace FlyByWireless.XConnect
{
    public enum ApplicationHostId
    {
        Unknown,
        XPlane,
        PlaneMaker,
        WorldMaker,
        Briefer,
        PartMaker,
        YoungsMod,
        XAuto
    }

    public enum Role
    {
        Invalid,
        Master,
        ExternVisual,
        IOS
    }

    public class Beacon
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal readonly ref struct Becn
        {
            public readonly byte BeaconMajorVersion, BeaconMinorVersion;
            public readonly ApplicationHostId ApplicationHostId;
            public readonly int VersionNumber;
            public readonly Role Role;
            public readonly ushort Port;
            internal readonly sbyte _ComputerName;
        }

        public readonly IPEndPoint RemoteEndPoint;
        public readonly Version BeaconVersion;
        public readonly ApplicationHostId ApplicationHostId;
        public readonly Version Version;
        public readonly Role Role;
        public readonly ushort Port;
        public readonly string ComputerName;

        internal unsafe Beacon(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> buffer)
        {
            fixed (byte* b = buffer)
            {
                var becn = (Becn*)b;
                (RemoteEndPoint, BeaconVersion, ApplicationHostId, Role, Port) =
                    (remoteEndPoint, new(becn->BeaconMajorVersion, becn->BeaconMinorVersion), becn->ApplicationHostId, becn->Role, becn->Port);
                var major = Math.DivRem(becn->VersionNumber, 10000, out var minorRevision);
                var minor = Math.DivRem(minorRevision, 100, out var revision);
                Version = new(major, minor, 0, revision);
                var m = buffer.Length - sizeof(Becn);
                var n = &becn->_ComputerName;
                var length = 0;
                while (length < m && n[length] != 0)
                    ++length;
                ComputerName = new(n, 0, length, Encoding.ASCII);
            }
        }

        public string VersionString => $"{Version.Major}.{Version.Minor}r{Version.Revision}";
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct RPos
    {
        public readonly double Longitude, Latitude, TrueAltitude;
        public readonly float AbsoluteAltitude,
            Pitch, TrueHeading, Roll,
            EastVelocity, UpVelocity, SouthVelocity,
            RollRate, PitchRate, YawRate;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct RadR
    {
        public readonly float Longitude, Latitude, PrecipPercent, StormTop;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    readonly struct DRef
    {
        public readonly int Id;
        public readonly float Value;
    }
}