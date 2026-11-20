#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    private const int SteelSeriesVendorId = 0x1038;
    private const int CorsairVendorId = 0x1B1C;
    private const int HyperXVendorIdHp = 0x03F0;
    private const int HyperXVendorIdKingston = 0x0951;

    private static (List<DeviceBattery> Devices, string? Error) WindowsVendors()
    {
        var readings = new List<DeviceBattery>();
        var deadline = Environment.TickCount64 + 2000;
        string? lastError = null;

        try
        {
            var guid = HidInterfaceGuid;
            var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return ([], null);

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
                        if (string.IsNullOrEmpty(path)) continue;

                        int vid = 0;
                        if (path.Contains("vid_1038", StringComparison.OrdinalIgnoreCase)) vid = SteelSeriesVendorId;
                        else if (path.Contains("vid_1b1c", StringComparison.OrdinalIgnoreCase)) vid = CorsairVendorId;
                        else if (path.Contains("vid_03f0", StringComparison.OrdinalIgnoreCase)) vid = HyperXVendorIdHp;
                        else if (path.Contains("vid_0951", StringComparison.OrdinalIgnoreCase)) vid = HyperXVendorIdKingston;
                        if (vid == 0) continue;

                        using var handle = CreateFileW(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
                        if (handle.IsInvalid) continue;

                        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(handle, ref attrs) || attrs.VendorID != vid) continue;

                        int pid = attrs.ProductID;
                        string product = GetProductStringFromPath(path);

                        DeviceBattery? dev = null;
                        if (vid == SteelSeriesVendorId)
                        {
                            dev = ReadSteelSeriesDevice(handle, pid, product, deadline);
                        }
                        else if (vid == CorsairVendorId)
                        {
                            dev = ReadCorsairDevice(handle, pid, product, deadline);
                        }
                        else if (vid is HyperXVendorIdHp or HyperXVendorIdKingston)
                        {
                            dev = ReadHyperXDevice(handle, pid, product, deadline);
                        }

                        if (dev is not null)
                        {
                            readings.Add(dev);
                        }
                    }
                    catch (Exception ex) { lastError ??= ex.Message; }
                    finally { Marshal.FreeHGlobal(mem); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch (Exception ex) { lastError ??= ex.Message; }

        return (readings, lastError);
    }

    private static DeviceBattery? ReadSteelSeriesDevice(SafeFileHandle handle, int pid, string product, long deadline)
    {
        // Arctis 1 Wireless: { 0x06, 0x12 }
        // Arctis 7 / Pro: { 0x06, 0x18 }
        // Arctis 9: { 0x00, 0x20 }
        byte[] req = pid is 0x12AD or 0x12B3 ? [0x06, 0x12] : (pid is 0x1252 or 0x1260 or 0x12AD ? [0x06, 0x18] : [0x06, 0x18]);
        if (PaOverlappedIo(isWrite: true, handle, req, 200) <= 0) return null;

        var (resp, fatal) = ReadPaOverlapped(handle, 64, 250);
        if (fatal || resp is null || resp.Length < 3) return null;

        int percent = -1;
        bool? charging = null;

        if (req[1] == 0x12 && resp.Length >= 4)
        {
            if (resp[2] == 0x01) return null; // offline
            percent = Math.Clamp((int)resp[3], 0, 100);
        }
        else if (req[1] == 0x18 && resp.Length >= 3)
        {
            if (resp[2] == 0) return null; // offline
            percent = Math.Clamp((int)resp[2], 0, 100);
        }

        if (percent < 0) return null;
        var name = !string.IsNullOrEmpty(product) ? product : $"SteelSeries Device ({pid:X4})";
        return new DeviceBattery(name, percent, KindOf(name), charging);
    }

    private static DeviceBattery? ReadCorsairDevice(SafeFileHandle handle, int pid, string product, long deadline)
    {
        // Void / HS70 / HS80: { 0xC9, 0x64 }
        byte[] req = [0xC9, 0x64];
        if (PaOverlappedIo(isWrite: true, handle, req, 200) <= 0) return null;

        var (resp, fatal) = ReadPaOverlapped(handle, 64, 300);
        if (fatal || resp is null || resp.Length < 5) return null;

        byte st = resp[4];
        if (st == 0) return null; // offline

        bool charging = st is 4 or 5;
        int level = Math.Clamp(resp[2] & 0x7F, 0, 100);
        var name = !string.IsNullOrEmpty(product) ? product : $"Corsair Headset ({pid:X4})";
        return new DeviceBattery(name, level, "headphones", charging);
    }

    private static DeviceBattery? ReadHyperXDevice(SafeFileHandle handle, int pid, string product, long deadline)
    {
        // Cloud III Wireless / Cloud II Core: 0x66 + cmd
        // 0x82: connection status
        // 0x8A: charging status
        // 0x89: battery level %
        byte[] reqConn = [0x66, 0x82];
        if (PaOverlappedIo(isWrite: true, handle, reqConn, 200) <= 0) return null;

        var (respConn, fatal1) = ReadPaOverlapped(handle, 64, 250);
        if (fatal1 || respConn is null || respConn.Length < 3 || respConn[2] == 0) return null; // not connected

        bool? charging = null;
        byte[] reqChg = [0x66, 0x8A];
        if (PaOverlappedIo(isWrite: true, handle, reqChg, 200) > 0)
        {
            var (respChg, _) = ReadPaOverlapped(handle, 64, 250);
            if (respChg is not null && respChg.Length >= 3)
                charging = respChg[2] == 1;
        }

        byte[] reqBat = [0x66, 0x89];
        if (PaOverlappedIo(isWrite: true, handle, reqBat, 200) <= 0) return null;

        var (respBat, fatal2) = ReadPaOverlapped(handle, 64, 250);
        if (fatal2 || respBat is null || respBat.Length < 5 || respBat[4] > 100) return null;

        int level = respBat[4];
        var name = !string.IsNullOrEmpty(product) ? product : (pid is 0x069F or 0x0995 ? "HyperX Cloud II Core Wireless" : "HyperX Cloud III Wireless");
        return new DeviceBattery(name, level, "headphones", charging);
    }
}
#endif
