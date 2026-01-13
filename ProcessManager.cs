using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;

namespace UniversalFearRPC
{
    /// <summary>
    /// Утилита для поиска процессов и получения их информации (время запуска, открытие хэндла).
    /// </summary>
    public static class ProcessManager
    {
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long creationTime,
            out long exitTime, out long kernelTime, out long userTime);

        /// <summary>
        /// Находит первый процесс из словаря имен процесса в настройках.
        /// Возвращает найденный Process и связанные метаданные.
        /// </summary>
        public static Process FindTargetProcess(AppSettings settings, out string detectedVersion, out bool detectedMultiplayer, out DateTime processStartTime)
        {
            detectedVersion = "FEAR";
            detectedMultiplayer = false;
            processStartTime = DateTime.UtcNow;

            foreach (var kvp in settings.ProcessNames)
            {
                var processes = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(kvp.Value));
                if (processes.Length > 0)
                {
                    var p = processes[0];
                    detectedVersion = kvp.Key;
                    detectedMultiplayer = kvp.Key.IndexOf("MP", StringComparison.OrdinalIgnoreCase) >= 0;

                    try
                    {
                        processStartTime = GetProcessStartTime(p);
                    }
                    catch
                    {
                        processStartTime = DateTime.UtcNow;
                    }

                    return p;
                }
            }

            return null;
        }

        /// <summary>
        /// Открывает процесс с правами чтения и возвращает хэндл (или IntPtr.Zero).
        /// </summary>
        public static IntPtr OpenProcessForRead(int pid)
        {
            return OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
        }

        /// <summary>
        /// Возвращает время старта процесса, пытаясь несколько методов безопасно.
        /// </summary>
        public static DateTime GetProcessStartTime(Process process)
        {
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is System.Security.SecurityException)
            {
                try
                {
                    return GetProcessStartTimeWMI(process.Id);
                }
                catch
                {
                    try
                    {
                        return GetProcessStartTimeAPI(process.Id);
                    }
                    catch
                    {
                        return DateTime.UtcNow - process.TotalProcessorTime;
                    }
                }
            }
        }

        private static DateTime GetProcessStartTimeWMI(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher($"SELECT CreationDate FROM Win32_Process WHERE ProcessId = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string creationDate = obj["CreationDate"]?.ToString();
                        if (!string.IsNullOrEmpty(creationDate))
                        {
                            return ManagementDateTimeConverter.ToDateTime(creationDate).ToUniversalTime();
                        }
                    }
                }
            }
            catch
            {
            }

            return DateTime.UtcNow;
        }

        private static DateTime GetProcessStartTimeAPI(int processId)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (hProcess == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                long creationTime, exitTime, kernelTime, userTime;
                if (GetProcessTimes(hProcess, out creationTime, out exitTime, out kernelTime, out userTime))
                {
                    return DateTime.FromFileTimeUtc(creationTime);
                }
                else
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                if (hProcess != IntPtr.Zero)
                    CloseHandle(hProcess);
            }
        }
    }
}
