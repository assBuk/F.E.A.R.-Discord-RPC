using System;
using System.Text;
using System.Runtime.InteropServices;

namespace UniversalFearRPC
{
    /// <summary>
    /// Утилита для чтения памяти из внешнего процесса (обёртка вокруг ReadProcessMemory).
    /// Экземпляр привязан к конкретному hProcess и базовому адресу модуля.
    /// </summary>
    public class MemoryReader : IDisposable
    {
        private IntPtr _hProcess;
        public IntPtr BaseAddress { get; private set; }

        public MemoryReader(IntPtr hProcess, IntPtr baseAddress)
        {
            _hProcess = hProcess;
            BaseAddress = baseAddress;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Читает сырые байты из процесса, возвращает null при ошибке.
        /// </summary>
        public byte[] ReadBytes(IntPtr address, int size)
        {
            if (_hProcess == IntPtr.Zero || address == IntPtr.Zero || size <= 0)
                return null;

            byte[] buffer = new byte[size];
            int bytesRead;

            if (ReadProcessMemory(_hProcess, address, buffer, size, out bytesRead) && bytesRead > 0)
            {
                if (bytesRead == size)
                    return buffer;

                var actual = new byte[bytesRead];
                Array.Copy(buffer, actual, bytesRead);
                return actual;
            }

            return null;
        }

        public string ReadString(IntPtr address, int maxLength, Encoding encoding)
        {
            if (address == IntPtr.Zero)
                return string.Empty;

            var bytes = ReadBytes(address, maxLength);
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            string result = encoding.GetString(bytes, 0, bytes.Length);
            int nullIndex = result.IndexOf('\0');
            return nullIndex >= 0 ? result.Substring(0, nullIndex) : result.TrimEnd('\0');
        }

        public float ReadFloat(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return 0f;

            var bytes = ReadBytes(address, 4);
            if (bytes == null || bytes.Length < 4)
                return 0f;

            return BitConverter.ToSingle(bytes, 0);
        }

        public int ReadInt(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return 0;

            var bytes = ReadBytes(address, 4);
            if (bytes == null || bytes.Length < 4)
                return 0;

            return BitConverter.ToInt32(bytes, 0);
        }

        public IntPtr FollowPointerChain(PointerChain chain)
        {
            if (chain == null || chain.BaseAddress == IntPtr.Zero)
                return IntPtr.Zero;

            try
            {
                IntPtr currentAddress = chain.BaseAddress;
                int pointerSize = IntPtr.Size;

                for (int i = 0; i < chain.Offsets.Length; i++)
                {
                    var buffer = ReadBytes(currentAddress, pointerSize);
                    if (buffer == null || buffer.Length < pointerSize)
                        return IntPtr.Zero;

                    long nextAddr = pointerSize == 8 ? BitConverter.ToInt64(buffer, 0) : BitConverter.ToInt32(buffer, 0);
                    if (nextAddr == 0)
                        return IntPtr.Zero;

                    currentAddress = new IntPtr(nextAddr + chain.Offsets[i]);
                }

                return currentAddress;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Сканирование памяти относительно BaseAddress на наличие заданного паттерна.
        /// Возвращает найденный адрес или IntPtr.Zero.
        /// </summary>
        public IntPtr ScanMemoryForPattern(byte[] pattern, int startOffset, int size)
        {
            if (pattern == null || pattern.Length == 0)
                return IntPtr.Zero;

            byte[] buffer = new byte[size];
            int bytesRead;

            IntPtr startAddr = IntPtr.Add(BaseAddress, startOffset);
            if (!ReadProcessMemory(_hProcess, startAddr, buffer, size, out bytesRead))
                return IntPtr.Zero;

            for (int i = 0; i <= bytesRead - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return IntPtr.Add(startAddr, i);
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Сканирует область памяти от BaseAddress и пытается найти строку уровня по списку паттернов.
        /// </summary>
        public IntPtr ScanForLevelString(string[] patterns, int scanSize = 0x100000)
        {
            if (patterns == null || patterns.Length == 0)
                return IntPtr.Zero;

            byte[] buffer = new byte[scanSize];
            int bytesRead;

            if (!ReadProcessMemory(_hProcess, BaseAddress, buffer, scanSize, out bytesRead))
                return IntPtr.Zero;

            foreach (string pattern in patterns)
            {
                byte[] patternBytes = Encoding.ASCII.GetBytes(pattern);

                for (int i = 0; i <= bytesRead - patternBytes.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < patternBytes.Length; j++)
                    {
                        if (buffer[i + j] != patternBytes[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        int start = i;
                        while (start > 0 && buffer[start - 1] != 0)
                            start--;

                        return IntPtr.Add(BaseAddress, start);
                    }
                }
            }

            return IntPtr.Zero;
        }

        public IntPtr ScanForHealthPattern(float minValue, float maxValue, int scanSize = 0x200000)
        {
            byte[] buffer = new byte[scanSize];
            int bytesRead;

            if (!ReadProcessMemory(_hProcess, BaseAddress, buffer, scanSize, out bytesRead))
                return IntPtr.Zero;

            for (int i = 0; i <= bytesRead - 4; i += 4)
            {
                try
                {
                    float value = BitConverter.ToSingle(buffer, i);

                    if (value >= minValue && value <= maxValue)
                    {
                        IntPtr testAddr = IntPtr.Add(BaseAddress, i);
                        return testAddr;
                    }
                }
                catch
                {
                }
            }

            return IntPtr.Zero;
        }

        public IntPtr ScanForDeathCount(int scanSize = 0x100000)
        {
            byte[] buffer = new byte[scanSize];
            int bytesRead;

            if (!ReadProcessMemory(_hProcess, BaseAddress, buffer, scanSize, out bytesRead))
                return IntPtr.Zero;

            for (int i = 0; i <= bytesRead - 4; i += 4)
            {
                int value = BitConverter.ToInt32(buffer, i);

                if (value >= 0 && value < 1000)
                {
                    IntPtr testAddr = IntPtr.Add(BaseAddress, i);
                    return testAddr;
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_hProcess != IntPtr.Zero)
            {
                CloseHandle(_hProcess);
                _hProcess = IntPtr.Zero;
            }
        }
    }
}
