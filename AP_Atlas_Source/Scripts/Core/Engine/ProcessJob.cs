using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AP_Atlas.Core.EngineSetup
{
    /// <summary>
    /// Ties engine processes to Atlas's lifetime: on Windows they join a job object that closes them when Atlas exits,
    /// even if it crashes, so no Python processes are left running in the background.
    /// </summary>
    public static class ProcessJob
    {
        private static IntPtr _job = IntPtr.Zero;
        private static readonly object _lock = new object();

        // Live engine processes by the engine folder they run from, so setup can make sure none is using files it replaces.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (Process Process, string Root)> _live = new();

        private static string Norm(string root) => string.IsNullOrEmpty(root) ? "" : System.IO.Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();

        /// <summary>Engine processes still running from this engine folder.</summary>
        public static int RunningUnder(string root)
        {
            string key = Norm(root);
            int count = 0;
            foreach (var kv in _live)
            {
                bool exited;
                try { exited = kv.Value.Process.HasExited; } catch { exited = true; }
                if (exited) { _live.TryRemove(kv.Key, out _); continue; }
                if (kv.Value.Root == key) count++;
            }
            return count;
        }

        /// <summary>Kills what's still running from this engine folder. Returns how many.</summary>
        public static int KillAllUnder(string root)
        {
            string key = Norm(root);
            int killed = 0;
            foreach (var kv in _live)
            {
                if (kv.Value.Root != key) continue;
                try
                {
                    if (!kv.Value.Process.HasExited) { kv.Value.Process.Kill(true); killed++; }
                }
                catch { }
                _live.TryRemove(kv.Key, out _);
            }
            return killed;
        }

        /// <summary>Ties a process to Atlas's lifetime and, with engineRoot, records which engine folder it runs from.</summary>
        public static void Track(Process process, string engineRoot = null)
        {
            if (process == null) return;
            if (engineRoot != null)
            {
                try { _live[process.Id] = (process, Norm(engineRoot)); } catch { }
            }
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                lock (_lock)
                {
                    if (_job == IntPtr.Zero) _job = CreateKillOnCloseJob();
                    if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, process.Handle);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Couldn't tie an engine process to Atlas's lifetime: " + ex.Message);
            }
        }

        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(info);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job; // deliberately never closed: the OS closes it (and the processes) when Atlas exits
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
