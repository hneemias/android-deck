#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    internal const int LogitechVendorId = 0x046D;
    internal const byte LogitechSwId = 0x0D;

    private static readonly int[] LogitechLiIonVolts = [4186, 4067, 3989, 3922, 3859, 3811, 3778, 3751, 3717, 3671, 3646, 3579, 3500];
    private static readonly int[] LogitechLiIonPercents = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 5, 2, 0];

    private static readonly int[] LogitechG533Volts = [4200, 3850, 3790, 3750, 3680, 3330], LogitechG533Percents = [100, 50, 30, 20, 5, 0];
    private static readonly int[] LogitechG535Volts = [4175, 3817, 3766, 3730, 3664, 3310], LogitechG535Percents = [100, 50, 30, 20, 5, 0];
    private static readonly int[] LogitechG633Volts = [4100, 3950, 3850, 3750, 3650, 3500, 3300, 3150], LogitechG633Percents = [100, 80, 60, 40, 20, 10, 5, 0];
    private static readonly int[] LogitechGProVolts = [4150, 3830, 3780, 3740, 3670, 3320], LogitechGProPercents = [100, 50, 30, 20, 5, 0];
    private static readonly int[] LogitechDefHeadsetVolts = [4100, 3850, 3780, 3730, 3650, 3300], LogitechDefHeadsetPercents = [100, 50, 30, 20, 5, 0];

    private static int InterpolateCurve(int val, int[] v, int[] p)
    {
        if (val >= v[0]) return p[0];
        if (val <= v[^1]) return p[^1];
        for (int i = 0; i < v.Length - 1; i++)
        {
            if (val <= v[i] && val >= v[i + 1])
            {
                if (v[i] == v[i + 1]) return p[i];
                return p[i] + (val - v[i]) * (p[i + 1] - p[i]) / (v[i + 1] - v[i]);
            }
        }
        return p[^1];
    }

    private static int LogitechHeadsetPercent(int pid, int mv) => pid switch
    {
        0x0A66 => InterpolateCurve(mv, LogitechG533Volts, LogitechG533Percents),
        0x0AC4 => InterpolateCurve(mv, LogitechG535Volts, LogitechG535Percents),
        0x0A5C or 0x0A89 or 0x0A5B or 0x0A87 or 0x0AB5 or 0x0AFE => InterpolateCurve(mv, LogitechG633Volts, LogitechG633Percents),
        0x0AA7 or 0x0AAA or 0x0ABA or 0x0AFB => InterpolateCurve(mv, LogitechGProVolts, LogitechGProPercents),
        _ => InterpolateCurve(mv, LogitechDefHeadsetVolts, LogitechDefHeadsetPercents)
    };

    private static string LogitechKindFromType(byte t) => t switch
    {
        0x00 or 0x02 => "keyboard",
        0x03 or 0x04 or 0x05 => "mouse",
        0x08 => "headphones",
        0x0B or 0x0C => "controller",
        _ => "other"
    };

    private sealed class LogitechHidppClient : IDisposable
    {
        private readonly SafeFileHandle? _shortHandle;
        private readonly SafeFileHandle? _longHandle;

        public LogitechHidppClient(SafeFileHandle? shortHandle, SafeFileHandle? longHandle)
        {
            _shortHandle = shortHandle;
            _longHandle = longHandle;
        }

        public bool HasLong => _longHandle is { IsInvalid: false };
        public bool HasShort => _shortHandle is { IsInvalid: false };
        public bool IsValid => HasLong || HasShort;

        public void Dispose()
        {
            _shortHandle?.Dispose();
            _longHandle?.Dispose();
        }

        public int Request(byte idx, byte b2, byte b3, ReadOnlySpan<byte> parameters, out byte[] replyParams, out byte errorCode, bool forceShort = false, int timeoutMs = 350)
        {
            replyParams = [];
            errorCode = 0;
            bool useLong = HasLong && !forceShort;
            var handle = useLong ? _longHandle : _shortHandle;
            if (handle is null || handle.IsInvalid) return -1;

            int len = useLong ? 20 : 7;
            byte[] msg = new byte[len];
            msg[0] = useLong ? (byte)0x11 : (byte)0x10;
            msg[1] = idx;
            msg[2] = b2;
            msg[3] = b3;
            for (int i = 0; i < parameters.Length && i + 4 < len; i++)
                msg[i + 4] = parameters[i];

            if (PaOverlappedIo(isWrite: true, handle, msg, timeoutMs) != len) return -1;

            var deadline = Environment.TickCount64 + timeoutMs;
            var handles = new[] { _longHandle, _shortHandle }.Where(h => h is { IsInvalid: false }).ToArray();

            while (Environment.TickCount64 < deadline)
            {
                foreach (var h in handles)
                {
                    var (resp, fatal) = ReadPaOverlapped(h!, 64, 40);
                    if (fatal || resp is null || resp.Length < 7) continue;

                    if ((resp[0] == 0x10 || resp[0] == 0x11) && resp[1] == idx)
                    {
                        if (resp[2] == b2 && resp[3] == b3)
                        {
                            replyParams = resp[4..];
                            return 1;
                        }
                        if ((resp[2] == 0xFF || resp[2] == 0x8F) && resp[3] == b2 && resp[4] == b3)
                        {
                            errorCode = resp.Length > 5 ? resp[5] : (byte)0;
                            return 0;
                        }
                    }
                }
            }

            return -1;
        }

        public int Call(byte idx, byte feature, byte fn, ReadOnlySpan<byte> parameters, out byte[] replyParams)
        {
            return Request(idx, feature, (byte)((fn << 4) | LogitechSwId), parameters, out replyParams, out _, forceShort: false, timeoutMs: 350) == 1 ? 1 : 0;
        }

        public byte FeatureIndex(byte idx, ushort featureId)
        {
            Span<byte> p = stackalloc byte[2] { (byte)(featureId >> 8), (byte)(featureId & 0xFF) };
            if (Call(idx, 0x00, 0, p, out var resp) == 1 && resp.Length > 0)
                return resp[0];
            return 0;
        }
    }

    private static (List<DeviceBattery> Devices, string? Error) WindowsLogitech()
    {
        var readings = new List<DeviceBattery>();
        var deadline = Environment.TickCount64 + 2500;
        string? lastError = null;

        try
        {
            var guid = HidInterfaceGuid;
            var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return ([], null);

            var collections = new List<(string Path, int Pid, ushort UsagePage, ushort Usage, ushort OutLen)>();

            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (uint i = 0; Environment.TickCount64 < deadline && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
                {
                    SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var req, IntPtr.Zero);
                    if (req == 0) continue;

                    var mem = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, mem, req, out _, IntPtr.Zero)) continue;
                        var path = Marshal.PtrToStringUni(IntPtr.Add(mem, 4));
                        if (string.IsNullOrEmpty(path) || !path.Contains("vid_046d", StringComparison.OrdinalIgnoreCase)) continue;

                        using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (handle.IsInvalid) continue;

                        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != LogitechVendorId) continue;

                        if (TryGetHidCaps(handle, out var caps))
                        {
                            collections.Add((path, attrs.ProductID, caps.UsagePage, caps.Usage, caps.OutputReportByteLength));
                        }
                    }
                    catch (Exception ex) { lastError ??= ex.Message; }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            var groups = collections.GroupBy(c => ExtractPhysicalId(c.Path));
            foreach (var group in groups)
            {
                if (Environment.TickCount64 >= deadline) break;

                var shorts = group.Where(c => c.UsagePage == 0xFF00 && c.Usage == 0x0001 && c.OutLen == 7).ToList();
                var longs = group.Where(c => ((c.UsagePage == 0xFF00 && c.Usage == 0x0002) || (c.UsagePage == 0xFF43 && c.Usage == 0x0202)) && c.OutLen == 20).ToList();

                if (shorts.Count == 0 && longs.Count == 0) continue;

                var shortHandle = shorts.Count > 0 ? CreateFileW(shorts[0].Path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero) : null;
                var longHandle = longs.Count > 0 ? CreateFileW(longs[0].Path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero) : null;

                using var client = new LogitechHidppClient(shortHandle, longHandle);
                if (!client.IsValid) continue;

                int pid = (longs.Count > 0 ? longs[0] : shorts[0]).Pid;
                string product = GetProductStringFromPath((longs.Count > 0 ? longs[0] : shorts[0]).Path);
                bool isReceiver = product.Contains("Receiver", StringComparison.OrdinalIgnoreCase);

                byte[] pingParams = [0, 0, 0x5A];
                byte[] indices;
                if (pid == 0xC54F) // PRO X3 Superstrike
                {
                    indices = [1];
                }
                else if (!isReceiver && client.Request(0xFF, 0x00, (byte)(0x10 | LogitechSwId), pingParams, out var r, out _, timeoutMs: 300) == 1 && r.Length >= 3 && r[0] >= 2)
                {
                    indices = [0xFF];
                }
                else
                {
                    indices = [1, 2, 3, 4, 5, 6];
                }

                foreach (var idx in indices)
                {
                    if (Environment.TickCount64 >= deadline) break;

                    int pingResult = client.Request(idx, 0x00, (byte)(0x10 | LogitechSwId), pingParams, out var pingResp, out var pingErr, timeoutMs: 250);
                    if (pingResult == 1 && pingResp.Length >= 3 && pingResp[0] >= 2)
                    {
                        var name = ReadLogitechName(client, idx) ?? (idx == 0xFF && !string.IsNullOrEmpty(product) ? product : "Logitech Device");
                        var kind = ReadLogitechKind(client, idx) ?? KindOf(name);
                        var (level, charging) = ReadLogitechBattery20(client, idx, pid);

                        if (level is not null)
                        {
                            readings.Add(new DeviceBattery(name, Math.Clamp(level.Value, 0, 100), kind, charging));
                        }
                    }
                    else if (pingResult == 0 && pingErr == 0x01)
                    {
                        var (level, charging) = ReadLogitechBattery10(client, idx);
                        if (level is not null)
                        {
                            var name = idx == 0xFF && !string.IsNullOrEmpty(product) ? product : $"Logitech Device ({idx})";
                            readings.Add(new DeviceBattery(name, Math.Clamp(level.Value, 0, 100), KindOf(name), charging));
                        }
                    }
                }
            }
        }
        catch (Exception ex) { lastError ??= ex.Message; }

        return (readings, lastError);
    }

    private static string ExtractPhysicalId(string path)
    {
        var lower = path.ToLowerInvariant();
        var a = lower.IndexOf('#');
        var b = a >= 0 ? lower.IndexOf('#', a + 1) : -1;
        var c = b >= 0 ? lower.IndexOf('#', b + 1) : -1;
        if (c < 0) return lower;
        var hw = lower[(a + 1)..b];
        var inst = lower[(b + 1)..c];
        var colIdx = hw.IndexOf("&col", StringComparison.Ordinal);
        if (colIdx >= 0 && colIdx + 6 <= hw.Length) hw = hw.Remove(colIdx, 6);
        var amp = inst.LastIndexOf('&');
        if (amp >= 0) inst = inst[..amp];
        return hw + "#" + inst;
    }

    private static string GetProductStringFromPath(string path)
    {
        try
        {
            using var handle = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                var sb = new StringBuilder(256);
                if (HidD_GetProductString(handle, sb, (uint)(sb.Capacity * 2)) && sb.Length > 0)
                    return sb.ToString().Trim();
            }
        }
        catch { }
        return "";
    }

    private static string? ReadLogitechName(LogitechHidppClient client, byte idx)
    {
        byte f = client.FeatureIndex(idx, 0x0005); // DEVICE_NAME
        if (f == 0) return null;

        if (client.Call(idx, f, 0, [], out var p0) == 1 && p0.Length > 0)
        {
            int len = p0[0];
            var bytes = new List<byte>();
            byte[] offsetParam = [0];
            while (bytes.Count < len)
            {
                offsetParam[0] = (byte)bytes.Count;
                if (client.Call(idx, f, 1, offsetParam, out var chunk) != 1 || chunk.Length == 0) break;
                int added = 0;
                foreach (var b in chunk)
                {
                    if (bytes.Count >= len || b == 0) break;
                    bytes.Add(b);
                    added++;
                }
                if (added == 0) break;
            }
            if (bytes.Count > 0)
            {
                var str = Encoding.UTF8.GetString(bytes.ToArray()).Trim();
                if (!string.IsNullOrEmpty(str))
                {
                    if (str.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase) ||
                        str.StartsWith("G ", StringComparison.OrdinalIgnoreCase) ||
                        str.StartsWith("MX", StringComparison.OrdinalIgnoreCase))
                        return str;
                    return "Logitech " + str;
                }
            }
        }
        return null;
    }

    private static string? ReadLogitechKind(LogitechHidppClient client, byte idx)
    {
        byte f = client.FeatureIndex(idx, 0x0005);
        if (f != 0 && client.Call(idx, f, 2, [], out var p2) == 1 && p2.Length > 0)
        {
            return LogitechKindFromType(p2[0]);
        }
        return null;
    }

    private static (int? Level, bool? Charging) ReadLogitechBattery20(LogitechHidppClient client, byte idx, int pid)
    {
        // 0x1004: UNIFIED_BATTERY
        byte fUnified = client.FeatureIndex(idx, 0x1004);
        if (fUnified != 0 && client.Call(idx, fUnified, 1, [], out var pUnified) == 1 && pUnified.Length >= 3)
        {
            int soc = pUnified[0];
            int level = pUnified[1];
            int st = pUnified[2];
            bool? charging = st is 1 or 2 or 3;
            if (soc > 0 && soc <= 100) return (soc, charging);
            if (level != 0)
            {
                int mapped = (level & 8) != 0 ? 100 : (level & 4) != 0 ? 60 : (level & 2) != 0 ? 20 : 5;
                return (mapped, charging);
            }
        }

        // 0x1000: BATTERY_STATUS
        byte fStatus = client.FeatureIndex(idx, 0x1000);
        if (fStatus != 0 && client.Call(idx, fStatus, 0, [], out var pStatus) == 1 && pStatus.Length >= 3)
        {
            int st = pStatus[2];
            bool? charging = st >= 1 && st <= 4;
            int pct = pStatus[0] <= 100 ? pStatus[0] : -1;
            if (pct == 0 && st == 3) pct = 100;
            if (pct >= 0) return (pct, charging);
        }

        // 0x1001: BATTERY_VOLTAGE
        byte fVoltage = client.FeatureIndex(idx, 0x1001);
        if (fVoltage != 0 && client.Call(idx, fVoltage, 0, [], out var pVolt) == 1 && pVolt.Length >= 3)
        {
            int mv = (pVolt[0] << 8) | pVolt[1];
            bool? charging = (pVolt[2] & 0x80) != 0;
            if (mv > 0) return (InterpolateCurve(mv, LogitechLiIonVolts, LogitechLiIonPercents), charging);
        }

        // 0x1F20: ADC_MEASUREMENT (Headsets)
        byte fAdc = client.FeatureIndex(idx, 0x1F20);
        if (fAdc != 0 && client.Call(idx, fAdc, 0, [], out var pAdc) == 1 && pAdc.Length >= 3)
        {
            int mv = (pAdc[0] << 8) | pAdc[1];
            bool? charging = pAdc[2] == 0x03;
            if (mv > 0) return (LogitechHeadsetPercent(pid, mv), charging);
        }

        return (null, null);
    }

    private static (int? Level, bool? Charging) ReadLogitechBattery10(LogitechHidppClient client, byte idx)
    {
        // Register 0x0D (battery %)
        if (client.Request(idx, 0x81, 0x0D, [], out var p0D, out _, forceShort: true) == 1 && p0D.Length >= 3 && p0D[0] > 0 && p0D[0] <= 100)
        {
            bool? charging = p0D[2] >= 0x21 && p0D[2] <= 0x26;
            return (p0D[0], charging);
        }

        // Register 0x07 (battery level 1..7)
        if (client.Request(idx, 0x81, 0x07, [], out var p07, out _, forceShort: true) == 1 && p07.Length >= 2 && p07[0] > 0)
        {
            int[] pctMap = [0, 5, 10, 20, 35, 50, 75, 100];
            int levelIdx = p07[0] > 7 ? 7 : p07[0];
            bool? charging = p07[1] >= 0x21 && p07[1] <= 0x26;
            return (pctMap[levelIdx], charging);
        }

        return (null, null);
    }
}
#endif
