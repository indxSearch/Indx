using System.Runtime.InteropServices;

namespace IndxServer.Monitor
{
    /// <summary>
    /// The memory of the machine this runs on, every process together: its RAM and how much of it is
    /// in use. The runtime's own ceiling (<see cref="ProcessLine.AvailableMb"/>) is per process, and
    /// on App Service every app and deployment slot of a plan shares one instance: each process there
    /// read "of 12 GB" (75 percent of a P2v4's 16 GB, the GC's default heap limit), so their shares
    /// could add up past 100 percent while none of them showed the instance filling (10 Nov 2026).
    /// In use is what the system's own monitor calls it: Windows total less available
    /// (GlobalMemoryStatusEx), Linux MemTotal less MemAvailable (/proc/meminfo, the host's in a
    /// container), macOS app, wired and compressed memory (host_statistics64), Activity Monitor's
    /// "Memory Used". Null where none can be read.
    /// </summary>
    internal static class InstanceMemory
    {
        public readonly record struct Reading(long TotalBytes, long UsedBytes);

        public static Reading? Read()
        {
            try
            {
                if (OperatingSystem.IsWindows()) return Windows();
                if (OperatingSystem.IsLinux()) return Linux();
                if (OperatingSystem.IsMacOS()) return Mac();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
            {
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        private static Reading? Windows()
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return null;
            return new Reading((long)status.TotalPhys, (long)(status.TotalPhys - status.AvailPhys));
        }

        private static Reading? Linux()
        {
            long? total = null, available = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kb(line);
            }
            return total is > 0 and { } t && available is { } a ? new Reading(t * 1024, (t - a) * 1024) : null;

            static long? Kb(string line) =>
                long.TryParse(line.AsSpan(line.IndexOf(':') + 1).Trim().TrimEnd("kB").Trim(), out var v) ? v : null;
        }

        // vm_statistics64: wire_count at byte 12, purgeable_count at 88, compressor_page_count at
        // 128, internal_page_count at 140; 38 32-bit words in all. HOST_VM_INFO64 is 4.
        private const int HostVmInfo64 = 4;
        private const int VmStatsWords = 38;

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern uint mach_host_self();

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int host_statistics64(uint host, int flavor, IntPtr info, ref int count);

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int sysctlbyname(string name, out long value, ref nint size, IntPtr newValue, nint newSize);

        private static Reading? Mac()
        {
            nint size = sizeof(long);
            if (sysctlbyname("hw.memsize", out long total, ref size, IntPtr.Zero, 0) != 0 || total <= 0) return null;
            var buffer = Marshal.AllocHGlobal(VmStatsWords * 4);
            try
            {
                int count = VmStatsWords;
                if (host_statistics64(mach_host_self(), HostVmInfo64, buffer, ref count) != 0 || count < VmStatsWords) return null;
                long Pages(int offset) => (uint)Marshal.ReadInt32(buffer, offset);
                long pages = Pages(140) - Pages(88) + Pages(12) + Pages(128);
                return new Reading(total, Math.Clamp(pages * Environment.SystemPageSize, 0, total));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
