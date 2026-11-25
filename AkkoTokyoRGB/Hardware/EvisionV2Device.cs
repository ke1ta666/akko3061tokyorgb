using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AkkoTokyoRGB.Hardware;

/// <summary>
/// Safe user-mode USB HID driver for EVision V2 (VID_320F&PID_5025, UsagePage 0xFF1C, Usage 0x0092).
/// Only opens the vendor lighting collection (col04); never opens key-input interfaces.
/// </summary>
public sealed class EvisionV2Device : IDisposable
{
    public const ushort TargetVid = 0x320F;
    public const ushort TargetPid = 0x5025;
    public const ushort TargetUsagePage = 0xFF1C;
    public const ushort TargetUsage = 0x0092;
    public const byte ReportId = 0x04;
    private const int PacketSize = 64;

    private const byte CmdBeginConfigure = 0x01;
    private const byte CmdEndConfigure = 0x02;
    private const byte CmdReadCapabilities = 0x03;
    private const byte CmdReadConfig = 0x05;
    private const byte CmdWriteConfig = 0x06;
    private const byte CmdReadCustomColors = 0x0A;
    private const byte CmdWriteCustomColors = 0x0B;
    private const byte CmdSendDynamicColors = 0x12;
    private const byte CmdEndDynamicColors = 0x13;
    private const byte ModeCustom = 0x14;
    private const ushort ParamCurrentCustomMode = 0x19;

    private readonly object _ioLock = new();
    private IntPtr _handle = InvalidHandleValue;

    public string? DevicePath { get; private set; }
    public int MapSize { get; private set; } = 85;
    public int MacrosSize { get; private set; }
    public bool IsConnected => _handle != InvalidHandleValue;

    public void Connect()
    {
        lock (_ioLock)
        {
            DisconnectInternal();

            string? path = FindVendorInterfacePath()
                ?? throw new InvalidOperationException("Клавиатура AKKO 3061 (VID_320F&PID_5025, канал 0xFF1C:0x0092) не найдена. Проверьте USB-подключение.");

            IntPtr handle = CreateFile(
                path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOverlapped,
                IntPtr.Zero);

            if (handle == InvalidHandleValue)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Не удалось открыть HID-интерфейс подсветки (код Win32: {err}).");
            }

            _handle = handle;
            DevicePath = path;

            try
            {
                byte[] caps = ReadBlock(CmdReadCapabilities, 0, 7);
                if (caps[0] != 0xAA || caps[1] != 0x55)
                {
                    throw new InvalidOperationException("Устройство не подтвердило сигнатуру EVision V2 (AA 55).");
                }
                MapSize = caps[5] > 0 ? caps[5] : 85;
                MacrosSize = caps[6] * 0x80;
            }
            catch
            {
                DisconnectInternal();
                throw;
            }
        }
    }

    public KeyboardModeConfig ReadCurrentConfig()
    {
        lock (_ioLock)
        {
            EnsureConnected();
            byte[] prof = ReadBlock(CmdReadConfig, 0x0000, 1);
            byte currentProfile = prof[0] > 2 ? (byte)0 : prof[0];
            ushort offset = (ushort)(currentProfile * 0x40 + 0x01);
            byte[] buf = ReadBlock(CmdReadConfig, offset, 18);
            byte ledMode = buf[0x11];
            if (buf[0x00] == ModeCustom)
            {
                byte[] customIdx = ReadBlock(CmdReadConfig, (ushort)(offset + ParamCurrentCustomMode), 1);
                ledMode = customIdx[0];
            }

            return new KeyboardModeConfig
            {
                Profile = currentProfile,
                Mode = buf[0x00],
                Brightness = Math.Min(buf[0x01], (byte)4),
                Speed = Math.Min(buf[0x02], (byte)5),
                Direction = (byte)(buf[0x03] == 0 ? 0 : 1),
                RandomColors = buf[0x04] != 0,
                Red = buf[0x05],
                Green = buf[0x06],
                Blue = buf[0x07],
                LedMode = ledMode,
            };
        }
    }

    public KeyboardModeConfig ApplyHardwareMode(KeyboardModeConfig config)
    {
        lock (_ioLock)
        {
            EnsureConnected();
            // Exit RAM direct mode first if active
            try { Query(CmdEndDynamicColors, 0, null, 0, 0, 500); } catch { /* ignore if not active */ }

            byte[] buffer = new byte[18];
            byte currentProfile = 0;

            Query(CmdBeginConfigure, 0, null, 0, 0);
            try
            {
                byte[] prof = ReadBlock(CmdReadConfig, 0x0000, 1);
                currentProfile = prof[0];
                if (currentProfile > 2)
                {
                    currentProfile = 0;
                    WriteBlock(CmdWriteConfig, 0x0000, new[] { currentProfile });
                }

                ushort offset = (ushort)(currentProfile * 0x40 + 0x01);
                // Preserve firmware-reserved trailing bytes (0x08..0x11)
                byte[] existing = ReadBlock(CmdReadConfig, offset, 18);
                Buffer.BlockCopy(existing, 0, buffer, 0, 18);

                buffer[0x00] = config.Mode;
                buffer[0x01] = Math.Min(config.Brightness, (byte)4);
                buffer[0x02] = Math.Min(config.Speed, (byte)5);
                buffer[0x03] = (byte)(config.Direction == 0 ? 0 : 1);
                buffer[0x04] = config.RandomColors ? (byte)255 : (byte)0;
                buffer[0x05] = config.Red;
                buffer[0x06] = config.Green;
                buffer[0x07] = config.Blue;

                var modeInfo = EvisionModes.FindByCode(config.Mode);
                if (modeInfo != null && modeInfo.IsSurmountHue)
                {
                    buffer[0x11] = EvisionModes.MapSurmountColor(config.Red, config.Green, config.Blue);
                }

                WriteBlock(CmdWriteConfig, offset, buffer);
                if (config.Mode == ModeCustom)
                {
                    byte colorset = Math.Min(config.LedMode, (byte)9);
                    WriteBlock(CmdWriteConfig, (ushort)(offset + ParamCurrentCustomMode), new[] { colorset });
                }
            }
            finally
            {
                Query(CmdEndConfigure, 0, null, 0, 0);
            }

            return ReadCurrentConfig();
        }
    }

    public void SendSolidDirectRam(byte r, byte g, byte b)
    {
        lock (_ioLock)
        {
            EnsureConnected();
            byte[] rgb = new byte[MapSize * 3];
            for (int i = 0; i < MapSize; i++)
            {
                rgb[i * 3 + 0] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }
            WriteBlock(CmdSendDynamicColors, 0, rgb);
        }
    }

    public void SendPerKeyDirectRam(byte[] mapRgb)
    {
        lock (_ioLock)
        {
            EnsureConnected();
            int len = MapSize * 3;
            byte[] buf = new byte[len];
            Buffer.BlockCopy(mapRgb, 0, buf, 0, Math.Min(mapRgb.Length, len));
            WriteBlock(CmdSendDynamicColors, 0, buf);
        }
    }

    public byte[] ReadCustomColorsRaw(byte colorset)
    {
        lock (_ioLock)
        {
            EnsureConnected();
            byte cs = Math.Min(colorset, (byte)9);
            return ReadBlock(CmdReadCustomColors, (ushort)(512 * cs), (ushort)(MapSize * 3));
        }
    }

    public void WriteCustomColorsRaw(byte colorset, byte[] mapRgb)
    {
        lock (_ioLock)
        {
            EnsureConnected();
            byte cs = Math.Min(colorset, (byte)9);
            int len = MapSize * 3;
            byte[] buf = new byte[len];
            Buffer.BlockCopy(mapRgb, 0, buf, 0, Math.Min(mapRgb.Length, len));

            try { Query(CmdEndDynamicColors, 0, null, 0, 0, 500); } catch { }
            Query(CmdBeginConfigure, 0, null, 0, 0);
            try
            {
                WriteBlock(CmdWriteCustomColors, (ushort)(512 * cs), buf);
            }
            finally
            {
                Query(CmdEndConfigure, 0, null, 0, 0);
            }
        }
    }

    public void EndDirectRam()
    {
        lock (_ioLock)
        {
            if (_handle == InvalidHandleValue) return;
            try
            {
                Query(CmdEndDynamicColors, 0, null, 0, 0, 500);
            }
            catch
            {
                // Best-effort restore
            }
        }
    }

    private byte[] ReadBlock(byte cmd, ushort offset, ushort size)
    {
        byte[] output = new byte[size];
        int pos = 0;
        while (pos < size)
        {
            byte chunk = (byte)Math.Min(size - pos, PacketSize - 8);
            byte[] reply = Query(cmd, (ushort)(offset + pos), null, 0, chunk);
            byte actual = reply[4];
            if (actual <= 0 || actual > PacketSize - 8)
                throw new InvalidOperationException($"Некорректный размер ответа: {actual}");
            int copyLen = Math.Min(actual, size - pos);
            Buffer.BlockCopy(reply, 8, output, pos, copyLen);
            pos += copyLen;
        }
        return output;
    }

    private void WriteBlock(byte cmd, ushort offset, byte[] data)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            byte chunk = (byte)Math.Min(data.Length - pos, PacketSize - 8);
            Query(cmd, (ushort)(offset + pos), data, pos, chunk);
            pos += chunk;
        }
    }

    private byte[] Query(byte cmd, ushort offset, byte[]? data, int dataOff, byte size, int timeoutMs = 1000)
    {
        byte[] outBuf = new byte[PacketSize];
        outBuf[0] = ReportId;
        outBuf[3] = cmd;
        outBuf[4] = size;
        outBuf[5] = (byte)(offset & 0xFF);
        outBuf[6] = (byte)((offset >> 8) & 0xFF);

        if (data != null && size > 0)
        {
            Buffer.BlockCopy(data, dataOff, outBuf, 8, size);
        }

        ushort chksum = 0;
        for (int i = 3; i < PacketSize; i++)
        {
            chksum += outBuf[i];
        }
        outBuf[1] = (byte)(chksum & 0xFF);
        outBuf[2] = (byte)((chksum >> 8) & 0xFF);

        IntPtr writeEvt = CreateEvent(IntPtr.Zero, true, false, null);
        IntPtr readEvt = CreateEvent(IntPtr.Zero, true, false, null);
        try
        {
            byte[] inBuf = new byte[PacketSize];
            var ovRead = new NativeOverlapped { EventHandle = readEvt };
            bool readOk = ReadFile(_handle, inBuf, PacketSize, out uint bytesRead, ref ovRead);
            int readErr = Marshal.GetLastWin32Error();
            if (!readOk && readErr != ErrorIoPending)
            {
                throw new InvalidOperationException($"HID ReadFile ошибка: {readErr}");
            }

            var ovWrite = new NativeOverlapped { EventHandle = writeEvt };
            bool writeOk = WriteFile(_handle, outBuf, PacketSize, out uint bytesWritten, ref ovWrite);
            int writeErr = Marshal.GetLastWin32Error();
            if (!writeOk)
            {
                if (writeErr == ErrorIoPending)
                {
                    if (WaitForSingleObject(writeEvt, (uint)timeoutMs) != WaitObject0)
                    {
                        CancelIoEx(_handle, IntPtr.Zero);
                        GetOverlappedResult(_handle, ref ovWrite, out _, true);
                        GetOverlappedResult(_handle, ref ovRead, out _, true);
                        throw new TimeoutException("Таймаут записи HID-пакета.");
                    }
                    GetOverlappedResult(_handle, ref ovWrite, out bytesWritten, false);
                }
                else
                {
                    CancelIoEx(_handle, IntPtr.Zero);
                    GetOverlappedResult(_handle, ref ovRead, out _, true);
                    throw new InvalidOperationException($"HID WriteFile ошибка: {writeErr}");
                }
            }

            if (!readOk)
            {
                if (WaitForSingleObject(readEvt, (uint)timeoutMs) != WaitObject0)
                {
                    CancelIoEx(_handle, IntPtr.Zero);
                    GetOverlappedResult(_handle, ref ovRead, out _, true);
                    throw new TimeoutException($"Клавиатура не ответила на команду 0x{cmd:X2}.");
                }
                if (!GetOverlappedResult(_handle, ref ovRead, out bytesRead, false))
                {
                    throw new InvalidOperationException($"GetOverlappedResult ошибка: {Marshal.GetLastWin32Error()}");
                }
            }

            if (bytesRead < 8 || inBuf[0] != ReportId || inBuf[3] != cmd)
            {
                throw new InvalidOperationException($"Неожиданный ответ HID (cmd=0x{inBuf[3]:X2}).");
            }
            if (inBuf[7] != 0)
            {
                throw new InvalidOperationException($"Контроллер вернул статус ошибки: 0x{inBuf[7]:X2}.");
            }
            return inBuf;
        }
        finally
        {
            CloseHandle(writeEvt);
            CloseHandle(readEvt);
        }
    }

    private void EnsureConnected()
    {
        if (_handle == InvalidHandleValue)
        {
            throw new InvalidOperationException("Клавиатура не подключена.");
        }
    }

    private void DisconnectInternal()
    {
        if (_handle != InvalidHandleValue)
        {
            CancelIoEx(_handle, IntPtr.Zero);
            CloseHandle(_handle);
            _handle = InvalidHandleValue;
            DevicePath = null;
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            EndDirectRam();
            DisconnectInternal();
        }
    }

    private static string? FindVendorInterfacePath()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (devInfo == InvalidHandleValue) return null;

        try
        {
            uint index = 0;
            while (true)
            {
                var ifData = new SpDeviceInterfaceData
                {
                    cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
                };
                if (!SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, index, ref ifData))
                    break;
                index++;

                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize == 0) continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailBuf, reqSize, out _, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrEmpty(path)) continue;

                    string low = path.ToLowerInvariant();
                    if (!low.Contains("vid_320f") || !low.Contains("pid_5025")) continue;

                    // Metadata-only check first (dwDesiredAccess = 0)
                    IntPtr h = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (h == InvalidHandleValue) continue;
                    try
                    {
                        if (HidD_GetPreparsedData(h, out IntPtr pp))
                        {
                            try
                            {
                                HidP_GetCaps(pp, out HidpCaps caps);
                                if (caps.UsagePage == TargetUsagePage && caps.Usage == TargetUsage && caps.OutputReportByteLength == PacketSize)
                                {
                                    return path;
                                }
                            }
                            finally
                            {
                                HidD_FreePreparsedData(pp);
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return null;
    }

    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;
    private const uint WaitObject0 = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    #endregion
}
