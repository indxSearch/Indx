using System.Runtime.InteropServices;

namespace IndxServer.Monitor
{
    /// <summary>
    /// The memory this process holds, wherever the system keeps it: in RAM, compressed, or swapped
    /// out. The working set counts only what is in RAM this moment, and an operating system packs an
    /// idle process away: on a Mac with large datasets loaded and nobody searching, the working set
    /// read 193 MB while the process held 3.9 GB, 3.7 GB of it compressed or swapped (5 Nov 2026).
    /// Each system has its own figure for what Activity Monitor or Task Manager calls the memory:
    /// macOS the physical footprint (task_info), Linux resident plus swap (/proc/self/status),
    /// Windows the private bytes. Null where none can be read, and the caller falls back to the
    /// working set.
    /// </summary>
    internal static class ProcessFootprint
    {
        public static long? Bytes(System.Diagnostics.Process process)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) return MacFootprint();
                if (OperatingSystem.IsLinux()) return LinuxRssAndSwap();
                if (OperatingSystem.IsWindows()) return process.PrivateMemorySize64 is > 0 and var p ? p : null;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
            {
            }
            return null;
        }

        // task_vm_info_data_t: phys_footprint is the 19th 64-bit field after virtual_size and the
        // two 32-bit counts, at byte 144. TASK_VM_INFO is 22; the count is in 32-bit words.
        private const int TaskVmInfo = 22;
        private const int PhysFootprintOffset = 144;
        private const int InfoWords = 96;

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int task_info(uint task, int flavor, IntPtr info, ref int count);

        private static long? MacFootprint()
        {
            // mach_task_self() is a macro over the exported variable mach_task_self_.
            var lib = NativeLibrary.Load("/usr/lib/libSystem.dylib");
            try
            {
                uint self = (uint)Marshal.ReadInt32(NativeLibrary.GetExport(lib, "mach_task_self_"));
                var buffer = Marshal.AllocHGlobal(InfoWords * 4);
                try
                {
                    int count = InfoWords;
                    if (task_info(self, TaskVmInfo, buffer, ref count) != 0 || count * 4 < PhysFootprintOffset + 8) return null;
                    return Marshal.ReadInt64(buffer, PhysFootprintOffset);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { NativeLibrary.Free(lib); }
        }

        private static long? LinuxRssAndSwap()
        {
            long? rss = null, swap = 0;
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kb(line);
                else if (line.StartsWith("VmSwap:", StringComparison.Ordinal)) swap = Kb(line);
            }
            return rss is { } r ? (r + (swap ?? 0)) * 1024 : null;

            static long? Kb(string line) =>
                long.TryParse(line.AsSpan(line.IndexOf(':') + 1).Trim().TrimEnd("kB").Trim(), out var v) ? v : null;
        }
    }
}
