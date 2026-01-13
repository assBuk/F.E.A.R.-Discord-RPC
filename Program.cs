using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using DiscordRPC;
using System.IO;
using System.Globalization;
using System.Security;
using System.ComponentModel;
using System.Management;

namespace UniversalFearRPC
{
    internal class Program
    {
        #region Константы и пути
        // ================= КОНСТАНТЫ КОНФИГУРАЦИИ =================
        private const int MAX_LEVEL_NAME_LENGTH = 128;
        private const int PATTERN_SCAN_RANGE = 0x100000;

        // Пути к файлам конфигурации
        private const string LEVEL_DB_FILE = "LevelDatabase.ini";
        private const string SETTINGS_FILE = "Settings.ini";
        private const string SESSION_FILE = "Session.dat";
        private const string STATS_FILE = "GameStats.log";
        private const string MENU_LABEL = "Меню";
        private const string SEARCHING_GAME_TEXT = "Поиск игры...";
        private const string READ_ERROR_TEXT = "Ошибка чтения";
        #endregion
        private static readonly string[] LEVEL_PATTERNS = { ".World00p", ".World", "Intro", "Docks" };

        #region Структуры данных
        /// <summary>
        /// Информация об уровне: эпизод, название, локация и алиасы.
        /// </summary>
        private class LevelInfo
        {
            public int Episode { get; set; }
            public string EpisodeName { get; set; }
            public string Location { get; set; }
            public string Type { get; set; }
            public string[] Aliases { get; set; }
        }

        /// <summary>
        /// Описание цепочки указателей (base + offsets) для поиска значений в памяти.
        /// </summary>
        private class PointerChain
        {
            public IntPtr BaseAddress { get; set; }
            public int[] Offsets { get; set; }
            public string Description { get; set; }
        }

        /// <summary>
        /// Сериализуемые данные сессии (для авто-восстановления).
        /// </summary>
        private class SessionData
        {
            public DateTime GameStartTime { get; set; }
            public DateTime SessionStartTime { get; set; }
            public int ProcessId { get; set; }
            public string ProcessName { get; set; }
            public string LastLevel { get; set; }
            public int DeathCount { get; set; }
            public int ImageIndex { get; set; }
            public bool IsMultiplayer { get; set; }
            public string GameVersion { get; set; } // FEAR, FEAR2, FEAR3
            public DateTime ProcessStartTime { get; set; }
        }

        /// <summary>
        /// Конфигурация приложения, загружаемая из Settings.ini.
        /// </summary>
        private class AppSettings
        {
            public string DiscordAppId { get; set; } = "1169265821965627492";
            public int ImageChangeInterval { get; set; } = 10;
            public string[] LargeImages { get; set; } = { "fear_menu", "main" };
            public float HealthMin { get; set; } = 0;
            public float HealthMax { get; set; } = 150;
            public int MaxSessionAgeHours { get; set; } = 24;
            public int AutoSaveInterval { get; set; } = 2;
            public int ProcessScanInterval { get; set; } = 2000;

            public Dictionary<string, string> ProcessNames { get; set; } = new Dictionary<string, string>
            {
                { "FEAR", "FEAR.exe" },
                { "FEARMP", "FEARMP.exe" },
                { "FEAR2", "FEAR2.exe" },
                { "FEAR2MP", "FEAR2MP.exe" },
                { "FEAR3", "F3AR.exe" },
                { "Fear3", "Fear3.exe" }
            };

            public Dictionary<string, string> GameImages { get; set; } = new Dictionary<string, string>
            {
                { "MenuImage", "fear_menu" },
                { "MultiplayerImage", "fear_mp" },
                { "Fear2Image", "fear2" },
                { "Fear3Image", "fear3" }
            };

            public Dictionary<string, float> HealthSettings { get; set; } = new Dictionary<string, float>
            {
                { "Fear1Min", 0 },
                { "Fear1Max", 150 },
                { "Fear2Min", 0 },
                { "Fear2Max", 200 },
                { "Fear3Min", 0 },
                { "Fear3Max", 100 }
            };
        }
        #endregion

        // ================= КОНФИГУРАЦИЯ =================
        private static readonly PointerChain[] HEALTH_POINTER_CHAINS =
        {
            new PointerChain
            {
                BaseAddress = IntPtr.Zero,
                Offsets = new[] { 0x928, 0x70, 0, 0, 0x40, 0x17C, 0x604 },
                Description = "Цепочка 1: FEAR.exe+03ACC → +928 → +70 → +0 → +0 → +40 → +17C → +604"
            },
            new PointerChain
            {
                BaseAddress = IntPtr.Zero,
                Offsets = new[] { 0x928, 0x70, 0, 0, 0x40, 0x1BC, 0x604 },
                Description = "Цепочка 2: FEAR.exe+03ACC → +928 → +70 → +0 → +0 → +40 → +1BC → +604"
            },
            new PointerChain
            {
                BaseAddress = IntPtr.Zero,
                Offsets = new[] { 0x778, 0, 0x4, 0, 0x32C, 0x12C },
                Description = "Цепочка 3: FEAR.exe+04654 → +778 → +0 → +4 → +0 → +32C → +12C"
            }
        };

        // ================= СОСТОЯНИЕ ПРОГРАММЫ =================
        private static Dictionary<string, LevelInfo> levelDatabase;
        private static DiscordRpcClient discordClient;
        private static SessionData currentSession;
        private static AppSettings settings = new AppSettings();

        // Состояние процесса игры
        private static IntPtr hProcess = IntPtr.Zero;
        private static Process gameProcess = null;
        private static IntPtr baseAddress = IntPtr.Zero;
        private static IntPtr levelAddress = IntPtr.Zero;
        private static IntPtr healthAddress = IntPtr.Zero;
        private static IntPtr deathCountAddress = IntPtr.Zero;

        // Игровое состояние
        private static string currentLevel = "";
        private static float currentHealth = 100f;
        private static int deathCount = 0;
        private static bool isMultiplayer = false;
        private static bool isMenu = true;
        private static LevelInfo currentLevelInfo = null;
        private static int failedReads = 0;
        private static int currentImageIndex = 0;
        private static DateTime lastImageChangeTime = DateTime.MinValue;
        private static DateTime lastSaveTime = DateTime.MinValue;
        private static DateTime processStartTime = DateTime.MinValue;
        private static string currentGameVersion = "FEAR";

        #region Windows API
        // ================= WINDOWS API =================
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(IntPtr hProcess, out long creationTime,
            out long exitTime, out long kernelTime, out long userTime);

        // Константы для прав доступа
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        #endregion

        #region Main
        // ================= MAIN =================
        static void Main(string[] args)
        {
            Console.Title = "Universal F.E.A.R RPC - Multi-Game Edition";
            Console.OutputEncoding = Encoding.UTF8;

            // Установка обработчика закрытия
            Console.CancelKeyPress += OnConsoleCancel;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            PrintHeader();

            // Загрузка конфигурации
            LoadConfiguration();

            // Восстановление сессии, если она существует
            RestoreSession();

            // Инициализация Discord
            InitializeDiscord();

            // Основной цикл
            MainLoop();
        }

        static void MainLoop()
        {
            while (true)
            {
                try
                {
                    UpdateLoop();
                    Thread.Sleep(settings.ProcessScanInterval);
                }
                catch (Exception ex)
                {
                    LogError($"Критическая ошибка: {ex.Message}");
                    Thread.Sleep(8000);
                }
            }
        }
        #endregion

        // ================= УПРАВЛЕНИЕ КОНФИГУРАЦИЕЙ =================
        static void LoadConfiguration()
        {
            try
            {
                // Загрузка настроек
                var loadedSettings = LoadSettings();
                if (loadedSettings != null)
                {
                    settings = loadedSettings;
                }

                // Загрузка базы данных уровней
                if (File.Exists(LEVEL_DB_FILE))
                {
                    levelDatabase = LoadLevelDatabaseFromIni();
                    LogSuccess($"База данных уровней загружена: {levelDatabase.Count} записей");
                }
                else
                {
                    // Создание файла по умолчанию
                    levelDatabase = CreateDefaultLevelDatabase();
                    SaveLevelDatabaseToIni();
                    LogInfo($"Создан новый файл базы данных: {LEVEL_DB_FILE}");
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка загрузки конфигурации: {ex.Message}");
                levelDatabase = CreateDefaultLevelDatabase();
            }
        }

        static AppSettings LoadSettings()
        {
            if (!File.Exists(SETTINGS_FILE))
            {
                SaveSettings(settings);
                return settings;
            }

            try
            {
                var loadedSettings = new AppSettings();
                var lines = File.ReadAllLines(SETTINGS_FILE);
                string currentSection = "";

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    if (trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                        continue;

                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }

                    var parts = trimmed.Split('=');
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim();
                        var value = parts[1].Trim();

                        switch (currentSection)
                        {
                            case "General":
                                switch (key)
                                {
                                    case "DiscordAppId":
                                        loadedSettings.DiscordAppId = value;
                                        break;
                                    case "ImageChangeInterval":
                                        if (int.TryParse(value, out int interval))
                                            loadedSettings.ImageChangeInterval = interval;
                                        break;
                                    case "LargeImages":
                                        loadedSettings.LargeImages = value.Split(',')
                                            .Select(x => x.Trim())
                                            .Where(x => !string.IsNullOrEmpty(x))
                                            .ToArray();
                                        break;
                                    case "HealthMin":
                                        if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture,
                                                out float min))
                                            loadedSettings.HealthMin = min;
                                        break;
                                    case "HealthMax":
                                        if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture,
                                                out float max))
                                            loadedSettings.HealthMax = max;
                                        break;
                                    case "MaxSessionAgeHours":
                                        if (int.TryParse(value, out int age))
                                            loadedSettings.MaxSessionAgeHours = age;
                                        break;
                                    case "AutoSaveInterval":
                                        if (int.TryParse(value, out int saveInterval))
                                            loadedSettings.AutoSaveInterval = saveInterval;
                                        break;
                                    case "ProcessScanInterval":
                                        if (int.TryParse(value, out int scanInterval))
                                            loadedSettings.ProcessScanInterval = scanInterval;
                                        break;
                                }

                                break;

                            case "Processes":
                                loadedSettings.ProcessNames[key] = value;
                                break;

                            case "Images":
                                loadedSettings.GameImages[key] = value;
                                break;

                            case "Health":
                                if (float.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture,
                                        out float healthValue))
                                    loadedSettings.HealthSettings[key] = healthValue;
                                break;
                        }
                    }
                }

                return loadedSettings;
            }
            catch (Exception ex)
            {
                LogError($"Ошибка загрузки настроек: {ex.Message}");
                return settings;
            }
        }

        static void SaveSettings(AppSettings appSettings)
        {
            try
            {
                var lines = new List<string>
                {
                    "[General]",
                    $"DiscordAppId={appSettings.DiscordAppId}",
                    $"ImageChangeInterval={appSettings.ImageChangeInterval}",
                    $"LargeImages={string.Join(",", appSettings.LargeImages)}",
                    $"HealthMin={appSettings.HealthMin.ToString(CultureInfo.InvariantCulture)}",
                    $"HealthMax={appSettings.HealthMax.ToString(CultureInfo.InvariantCulture)}",
                    $"MaxSessionAgeHours={appSettings.MaxSessionAgeHours}",
                    $"AutoSaveInterval={appSettings.AutoSaveInterval}",
                    $"ProcessScanInterval={appSettings.ProcessScanInterval}",
                    "",
                    "[Processes]"
                };

                foreach (var kvp in appSettings.ProcessNames)
                {
                    lines.Add($"{kvp.Key}={kvp.Value}");
                }

                lines.Add("");
                lines.Add("[Images]");
                foreach (var kvp in appSettings.GameImages)
                {
                    lines.Add($"{kvp.Key}={kvp.Value}");
                }

                lines.Add("");
                lines.Add("[Health]");
                foreach (var kvp in appSettings.HealthSettings)
                {
                    lines.Add($"{kvp.Key}={kvp.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                lines.Add("");
                lines.Add("; Discord RPC Application ID");
                lines.Add("; Изображения меняются каждые ImageChangeInterval секунд");
                lines.Add("; HealthMin/HealthMax - диапазон здоровья игрока");
                lines.Add("; MaxSessionAgeHours - максимальный возраст сессии в часах");
                lines.Add("; AutoSaveInterval - интервал автосохранения в секундах");
                lines.Add("; ProcessScanInterval - интервал сканирования процессов в миллисекундах");

                File.WriteAllLines(SETTINGS_FILE, lines, Encoding.UTF8);
                LogInfo($"Настройки сохранены в {SETTINGS_FILE}");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка сохранения настроек: {ex.Message}");
            }
        }

        static Dictionary<string, LevelInfo> LoadLevelDatabaseFromIni()
        {
            var db = new Dictionary<string, LevelInfo>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(LEVEL_DB_FILE))
                return db;

            var lines = File.ReadAllLines(LEVEL_DB_FILE, Encoding.UTF8);
            string currentKey = null;
            LevelInfo currentInfo = null;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.StartsWith(";") || trimmed.StartsWith("#"))
                    continue;

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    // Сохраняем предыдущую запись
                    if (currentKey != null && currentInfo != null)
                    {
                        db[currentKey] = currentInfo;
                    }

                    // Начинаем новую запись
                    currentKey = trimmed.Substring(1, trimmed.Length - 2);
                    currentInfo = new LevelInfo();
                }
                else if (currentKey != null && currentInfo != null)
                {
                    var parts = trimmed.Split('=');
                    if (parts.Length == 2)
                    {
                        var key = parts[0].Trim();
                        var value = parts[1].Trim();

                        switch (key)
                        {
                            case "Episode":
                                if (int.TryParse(value, out int episode))
                                    currentInfo.Episode = episode;
                                break;
                            case "EpisodeName":
                                currentInfo.EpisodeName = value;
                                break;
                            case "Location":
                                currentInfo.Location = value;
                                break;
                            case "Type":
                                currentInfo.Type = value;
                                break;
                            case "Aliases":
                                currentInfo.Aliases = value.Split(',')
                                    .Select(a => a.Trim())
                                    .Where(a => !string.IsNullOrEmpty(a))
                                    .ToArray();
                                break;
                        }
                    }
                }
            }

            // Добавляем последнюю запись
            if (currentKey != null && currentInfo != null)
            {
                db[currentKey] = currentInfo;
            }

            return db;
        }

        static void SaveLevelDatabaseToIni()
        {
            try
            {
                var lines = new List<string>
                {
                    "; База данных уровней F.E.A.R",
                    "; Формат:",
                    "; [ИмяУровня.World00p]",
                    "; Episode=номер эпизода (0 для не-сюжетных)",
                    "; EpisodeName=Название эпизода",
                    "; Location=Локация/миссия",
                    "; Type=Тип (Сюжет, Демо, Мультиплеер, Тест)",
                    "; Aliases=Алиасы через запятую",
                    "",
                    "[DatabaseVersion]",
                    "Version=2.0",
                    "LastUpdated=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ""
                };

                // Сортируем по номеру эпизода для удобства
                var sortedLevels = levelDatabase
                    .OrderBy(kvp => kvp.Value.Episode)
                    .ThenBy(kvp => kvp.Key);

                foreach (var kvp in sortedLevels)
                {
                    lines.Add($"[{kvp.Key}]");
                    lines.Add($"Episode={kvp.Value.Episode}");
                    lines.Add($"EpisodeName={kvp.Value.EpisodeName}");
                    lines.Add($"Location={kvp.Value.Location}");
                    lines.Add($"Type={kvp.Value.Type}");
                    lines.Add($"Aliases={string.Join(",", kvp.Value.Aliases)}");
                    lines.Add("");
                }

                File.WriteAllLines(LEVEL_DB_FILE, lines, Encoding.UTF8);
                LogSuccess($"База данных сохранена в {LEVEL_DB_FILE}");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка сохранения базы данных: {ex.Message}");
            }
        }

        // ================= ПОЛУЧЕНИЕ ВРЕМЕНИ ПРОЦЕССА =================
        static DateTime GetProcessStartTime(Process process)
        {
            try
            {
                // Метод 1: Используем Process.StartTime (требует меньше прав)
                return process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception ||
                                       ex is SecurityException)
            {
                try
                {
                    // Метод 2: Используем WMI (менее требовательный к правам)
                    return GetProcessStartTimeWMI(process.Id);
                }
                catch
                {
                    try
                    {
                        // Метод 3: Используем API GetProcessTimes (нужны ограниченные права)
                        return GetProcessStartTimeAPI(process.Id);
                    }
                    catch
                    {
                        // Метод 4: Если не получилось, возвращаем текущее время минус TotalProcessorTime
                        return DateTime.UtcNow - process.TotalProcessorTime;
                    }
                }
            }
        }

        static DateTime GetProcessStartTimeWMI(int processId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                           $"SELECT CreationDate FROM Win32_Process WHERE ProcessId = {processId}"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string creationDate = obj["CreationDate"]?.ToString();
                        if (!string.IsNullOrEmpty(creationDate))
                        {
                            // Конвертируем из WMI формата в DateTime
                            return ManagementDateTimeConverter.ToDateTime(creationDate).ToUniversalTime();
                        }
                    }
                }
            }
            catch
            {
                // WMI не доступен
            }

            return DateTime.UtcNow;
        }

        static DateTime GetProcessStartTimeAPI(int processId)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                // Открываем процесс с минимальными правами
                hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
                if (hProcess == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                long creationTime, exitTime, kernelTime, userTime;
                if (GetProcessTimes(hProcess, out creationTime, out exitTime, out kernelTime, out userTime))
                {
                    // Преобразуем Windows FileTime в DateTime
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

        // ================= УПРАВЛЕНИЕ СЕССИЕЙ =================
        static void SaveSession()
        {
            try
            {
                // Проверяем интервал автосохранения
                if (lastSaveTime != DateTime.MinValue &&
                    (DateTime.UtcNow - lastSaveTime).TotalSeconds < settings.AutoSaveInterval)
                {
                    return;
                }

                if (currentSession == null)
                    currentSession = new SessionData();

                currentSession.ProcessId = gameProcess?.Id ?? 0;
                currentSession.ProcessName = gameProcess?.ProcessName ?? "";
                currentSession.LastLevel = currentLevel;
                currentSession.DeathCount = deathCount;
                currentSession.ImageIndex = currentImageIndex;
                currentSession.IsMultiplayer = isMultiplayer;
                currentSession.GameVersion = currentGameVersion;

                if (currentSession.SessionStartTime == DateTime.MinValue)
                    currentSession.SessionStartTime = DateTime.UtcNow;

                // Если игра запущена, сохраняем время начала
                if (gameProcess != null && !gameProcess.HasExited)
                {
                    // Используем время запуска процесса как время начала игры
                    if (processStartTime != DateTime.MinValue)
                    {
                        currentSession.GameStartTime = processStartTime;
                        currentSession.ProcessStartTime = processStartTime;
                    }
                    else if (currentSession.GameStartTime == DateTime.MinValue)
                    {
                        currentSession.GameStartTime = DateTime.UtcNow;
                    }
                }

                using (var writer = new BinaryWriter(File.Open(SESSION_FILE, FileMode.Create)))
                {
                    writer.Write(currentSession.GameStartTime.ToBinary());
                    writer.Write(currentSession.SessionStartTime.ToBinary());
                    writer.Write(currentSession.ProcessId);
                    writer.Write(currentSession.ProcessName ?? "");
                    writer.Write(currentSession.LastLevel ?? "");
                    writer.Write(currentSession.DeathCount);
                    writer.Write(currentSession.ImageIndex);
                    writer.Write(currentSession.IsMultiplayer);
                    writer.Write(currentSession.GameVersion ?? "");
                    writer.Write(currentSession.ProcessStartTime.ToBinary());
                }

                lastSaveTime = DateTime.UtcNow;
                LogDebug("Сессия сохранена");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка сохранения сессии: {ex.Message}");
            }
        }

        static void RestoreSession()
        {
            try
            {
                if (!File.Exists(SESSION_FILE))
                {
                    currentSession = new SessionData
                    {
                        GameStartTime = DateTime.UtcNow,
                        SessionStartTime = DateTime.UtcNow,
                        ProcessStartTime = DateTime.UtcNow
                    };
                    return;
                }

                using (var reader = new BinaryReader(File.Open(SESSION_FILE, FileMode.Open)))
                {
                    currentSession = new SessionData
                    {
                        GameStartTime = DateTime.FromBinary(reader.ReadInt64()),
                        SessionStartTime = DateTime.FromBinary(reader.ReadInt64()),
                        ProcessId = reader.ReadInt32(),
                        ProcessName = reader.ReadString(),
                        LastLevel = reader.ReadString(),
                        DeathCount = reader.ReadInt32(),
                        ImageIndex = reader.ReadInt32(),
                        IsMultiplayer = reader.ReadBoolean(),
                        GameVersion = reader.ReadString(),
                        ProcessStartTime = DateTime.FromBinary(reader.ReadInt64())
                    };

                    // Восстанавливаем данные
                    deathCount = currentSession.DeathCount;
                    currentImageIndex = currentSession.ImageIndex;
                    isMultiplayer = currentSession.IsMultiplayer;
                    currentGameVersion = currentSession.GameVersion;

                    // Проверяем, не слишком ли старая сессия
                    var sessionAge = DateTime.UtcNow - currentSession.SessionStartTime;
                    if (sessionAge.TotalHours > settings.MaxSessionAgeHours)
                    {
                        LogInfo("Сессия слишком старая, начинаем новую");
                        currentSession.GameStartTime = DateTime.UtcNow;
                        currentSession.SessionStartTime = DateTime.UtcNow;
                        currentSession.ProcessStartTime = DateTime.UtcNow;
                        deathCount = 0;
                        currentImageIndex = 0;
                    }
                    else
                    {
                        LogSuccess($"Сессия восстановлена (возраст: {sessionAge.TotalMinutes:F0} минут)");
                        LogInfo($"Версия игры: {currentSession.GameVersion}");
                        LogInfo($"Время запуска процесса: {currentSession.ProcessStartTime:HH:mm:ss}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Ошибка восстановления сессии: {ex.Message}");
                currentSession = new SessionData
                {
                    GameStartTime = DateTime.UtcNow,
                    SessionStartTime = DateTime.UtcNow,
                    ProcessStartTime = DateTime.UtcNow
                };
            }
        }

        // ================= ОСНОВНЫЕ ФУНКЦИИ =================
        static void PrintHeader()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("══════════════════════════════════════════════════════");
            Console.WriteLine("  УНИВЕРСАЛЬНЫЙ F.E.A.R RPC - Multi-Game Edition");
            Console.WriteLine("  Версия 6.0 - Поддержка FEAR 1, 2, 3 и времени процесса");
            Console.WriteLine("══════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine($"Файл конфигурации: {LEVEL_DB_FILE}");
            Console.WriteLine($"Файл настроек: {SETTINGS_FILE}");
            Console.WriteLine($"Файл сессии: {SESSION_FILE}");
            Console.WriteLine($"Автосмена изображений: каждые {settings?.ImageChangeInterval ?? 10} секунд");
            Console.WriteLine($"Сканирование процессов: каждые {settings?.ProcessScanInterval ?? 2000} мс");
            Console.WriteLine("══════════════════════════════════════════════════════\n");
        }

        static void InitializeDiscord()
        {
            try
            {
                discordClient = new DiscordRpcClient(settings.DiscordAppId);

                discordClient.OnReady += (sender, e) => { LogSuccess($"Discord RPC подключен: {e.User.Username}"); };

                discordClient.OnError += (sender, e) => { LogError($"Discord ошибка: {e.Message}"); };

                discordClient.Initialize();
                LogInfo("Discord RPC инициализирован");
            }
            catch (Exception ex)
            {
                LogError($"Ошибка инициализации Discord: {ex.Message}");
            }
        }

        static void UpdateLoop()
        {
            // Автосохранение сессии
            SaveSession();

            // Проверка запуска игры
            if (!FindGameProcess())
            {
                if (hProcess != IntPtr.Zero)
                {
                    Cleanup();
                    UpdateDiscordStatus("", 0, 0);
                    LogInfo("Игра закрыта, ожидание перезапуска...");
                }

                Thread.Sleep(settings.ProcessScanInterval);
                return;
            }

            // Присоединение к процессу
            if (!AttachToProcess())
            {
                Thread.Sleep(settings.ProcessScanInterval);
                return;
            }

            // Автоматическое определение адресов при первом запуске
            if (levelAddress == IntPtr.Zero || healthAddress == IntPtr.Zero)
            {
                UniversalAutoDetect();
            }

            // Чтение данных из памяти игры
            string levelName = ReadLevelName();
            float health = ReadHealth();
            int deaths = ReadDeathCount();

            // Обработка игровых данных
            ProcessGameData(levelName, health, deaths);

            // Обновление Discord статуса
            UpdateDiscordStatus(levelName, health, deaths);

            // Отображение информации в консоли
            DisplayStatus(levelName, health, deaths);

            Thread.Sleep(settings.ProcessScanInterval);
        }


        // ================= ПОИСК И ПРИСОЕДИНЕНИЕ К ПРОЦЕССУ =================
        /// <summary>
        /// Проверяет, запущен ли один из процессов игры (из настроек) и обновляет состояние.
        /// </summary>
        static bool FindGameProcess()
        {
            Process targetProcess = null;
            string detectedVersion = "FEAR";
            bool detectedMultiplayer = false;

            // Ищем все процессы из настроек
            foreach (var kvp in settings.ProcessNames)
            {
                var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(kvp.Value));
                if (processes.Length > 0)
                {
                    targetProcess = processes[0];
                    detectedVersion = kvp.Key;

                    // Исправленная проверка на мультиплеер
                    detectedMultiplayer = kvp.Key.IndexOf("MP", StringComparison.OrdinalIgnoreCase) >= 0;

                    // Получаем время запуска процесса
                    try
                    {
                        processStartTime = GetProcessStartTime(targetProcess);
                        LogInfo($"Процесс {targetProcess.ProcessName} запущен: {processStartTime:HH:mm:ss}");
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Не удалось получить время запуска процесса: {ex.Message}");
                        processStartTime = DateTime.UtcNow;
                    }

                    break;
                }
            }

            if (targetProcess != gameProcess)
            {
                if (gameProcess != null)
                {
                    LogInfo($"Процесс изменен: {gameProcess.ProcessName} → " +
                            $"{(targetProcess?.ProcessName ?? "Нет")}");
                }

                gameProcess = targetProcess;
                currentGameVersion = detectedVersion;
                isMultiplayer = detectedMultiplayer;

                // Сброс адресов при смене процесса
                levelAddress = IntPtr.Zero;
                healthAddress = IntPtr.Zero;
                deathCountAddress = IntPtr.Zero;

                // Обновляем сессию
                if (currentSession != null && gameProcess != null)
                {
                    currentSession.GameVersion = currentGameVersion;
                    currentSession.IsMultiplayer = isMultiplayer;
                    currentSession.ProcessStartTime = processStartTime;
                }
            }

            return gameProcess != null;
        }

        /// <summary>
        /// Пытается открыть хэндл процесса и получить базовый адрес модуля.
        /// </summary>
        static bool AttachToProcess()
        {
            if (gameProcess == null || gameProcess.HasExited)
                return false;

            if (hProcess != IntPtr.Zero)
            {
                // Проверяем, что процесс еще жив
                try
                {
                    var test = Process.GetProcessById(gameProcess.Id);
                    return true;
                }
                catch
                {
                    hProcess = IntPtr.Zero;
                    baseAddress = IntPtr.Zero;
                }
            }

            // Открываем процесс с правами для чтения памяти
            hProcess = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, gameProcess.Id);

            if (hProcess == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                LogError($"Не удалось открыть процесс {gameProcess.ProcessName}. Ошибка: {error}");
                return false;
            }

            // Получаем базовый адрес
            try
            {
                var module = gameProcess.MainModule;
                baseAddress = module.BaseAddress;
                LogSuccess($"Присоединено к {gameProcess.ProcessName} [PID: {gameProcess.Id}]");
                LogInfo($"Базовый адрес: {FormatAddress(baseAddress)}");
                LogInfo($"Версия игры: {currentGameVersion}, Мультиплеер: {isMultiplayer}");

                return true;
            }
            catch (Exception ex)
            {
                LogError($"Ошибка получения модуля: {ex.Message}");
                CloseHandle(hProcess);
                hProcess = IntPtr.Zero;
                return false;
            }
        }]}]}null)```}]}]}]}}]}]}]}]

        // ================= АВТООПРЕДЕЛЕНИЕ АДРЕСОВ =================
        static void UniversalAutoDetect()
        {
            LogInfo("=== УНИВЕРСАЛЬНОЕ ОПРЕДЕЛЕНИЕ АДРЕСОВ ===");

            // 1. Поиск адреса уровня
            levelAddress = UniversalFindLevelAddress();

            // 2. Поиск здоровья через Pointer Chains
            healthAddress = UniversalFindHealthAddress();

            // 3. Поиск счетчика смертей
            deathCountAddress = UniversalFindDeathCountAddress();

            // 4. Если не нашли - сканируем память
            if (healthAddress == IntPtr.Zero)
            {
                LogWarning("Pointer chains не сработали, сканируем память...");
                healthAddress = ScanForHealthPattern();
            }

            LogInfo($"Результаты: Уровень={FormatAddress(levelAddress)}, " +
                    $"Здоровье={FormatAddress(healthAddress)}, " +
                    $"Смерти={FormatAddress(deathCountAddress)}");
        }

        static IntPtr UniversalFindLevelAddress()
        {
            LogInfo("Поиск адреса уровня...");

            // Метод 1: Поиск строки уровня в памяти
            IntPtr found = ScanForLevelString();
            if (found != IntPtr.Zero)
            {
                LogSuccess($"Уровень найден сканированием: {FormatAddress(found)}");
                return found;
            }

            // Метод 2: Поиск по известным сигнатурам
            byte[] patterns = Encoding.ASCII.GetBytes(".World00p");
            found = ScanMemoryForPattern(patterns, 0, PATTERN_SCAN_RANGE);
            if (found != IntPtr.Zero)
            {
                LogSuccess($"Уровень найден по паттерну: {FormatAddress(found)}");
                return found;
            }

            // Метод 3: Хардкод оффсет (последний вариант)
            IntPtr testAddr = IntPtr.Add(baseAddress, 0x16C045);
            string testLevel = ReadString(testAddr, MAX_LEVEL_NAME_LENGTH, Encoding.ASCII);
            if (!string.IsNullOrEmpty(testLevel) && testLevel.Contains(".World"))
            {
                LogSuccess($"Уровень по статическому оффсету 0x16C045: {testLevel}");
                return testAddr;
            }

            LogWarning("Адрес уровня не найден");
            return IntPtr.Zero;
        }

        static IntPtr UniversalFindHealthAddress()
        {
            LogInfo("Поиск здоровья через Pointer Chains...");

            // Инициализируем базовые адреса для цепочек
            foreach (var chain in HEALTH_POINTER_CHAINS)
            {
                // Устанавливаем базовый адрес для этой цепочки (подсказка из описания)
                if (chain.Description.Contains("03ACC"))
                    chain.BaseAddress = IntPtr.Add(baseAddress, 0x03ACC);
                else if (chain.Description.Contains("04654"))
                    chain.BaseAddress = IntPtr.Add(baseAddress, 0x04654);
                else if (chain.Description.Contains("0894C"))
                    chain.BaseAddress = IntPtr.Add(baseAddress, 0x0894C);
                else if (chain.Description.Contains("13628"))
                    chain.BaseAddress = IntPtr.Add(baseAddress, 0x13628);
                else if (chain.Description.Contains("21007F"))
                    chain.BaseAddress = IntPtr.Add(baseAddress, 0x21007F);

                // Пробуем прочитать по цепочке
                IntPtr healthPtr = FollowPointerChain(chain);
                if (healthPtr != IntPtr.Zero)
                {
                    float health = ReadFloat(healthPtr);
                    float healthMin = GetCurrentHealthMin();
                    float healthMax = GetCurrentHealthMax();

                    if (health >= healthMin && health <= healthMax)
                    {
                        LogSuccess($"Здоровье найдено: {health:F1} HP");
                        LogSuccess($"Цепочка: {chain.Description}");
                        return healthPtr;
                    }
                }
            }

            LogWarning("Pointer chains не сработали");
            return IntPtr.Zero;
        }

        static IntPtr FollowPointerChain(PointerChain chain)
        {
            if (chain.BaseAddress == IntPtr.Zero)
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

        static IntPtr UniversalFindDeathCountAddress()
        {
            LogInfo("Поиск счетчика смертей...");

            // Метод 1: Ищем рядом со здоровьем
            if (healthAddress != IntPtr.Zero)
            {
                // Типичные смещения для счетчика смертей
                int[] deathOffsets = { -0x100, -0x80, 0x80, 0x100, 0x120, 0x140, 0x200 };

                foreach (int offset in deathOffsets)
                {
                    IntPtr testAddr = IntPtr.Add(healthAddress, offset);
                    int deaths = ReadInt(testAddr);

                    if (deaths >= 0 && deaths < 1000)
                    {
                        LogSuccess($"Смерти найдены: {deaths} по смещению 0x{offset:X}");
                        return testAddr;
                    }
                }
            }

            // Метод 2: Сканируем область памяти
            return ScanForDeathCount();
        }

        static IntPtr ScanForHealthPattern()
        {
            LogInfo("Сканирование памяти на наличие здоровья...");

            // Сканируем 2MB памяти от базового адреса
            const int SCAN_SIZE = 0x200000;
            byte[] buffer = new byte[SCAN_SIZE];
            int bytesRead;

            if (!ReadProcessMemory(hProcess, baseAddress, buffer, SCAN_SIZE, out bytesRead))
                return IntPtr.Zero;

            float healthMin = GetCurrentHealthMin();
            float healthMax = GetCurrentHealthMax();

            // Ищем значения float в диапазоне здоровья
            for (int i = 0; i <= bytesRead - 4; i += 4)
            {
                try
                {
                    float value = BitConverter.ToSingle(buffer, i);

                    if (value >= healthMin && value <= healthMax)
                    {
                        IntPtr testAddr = IntPtr.Add(baseAddress, i);
                        LogSuccess($"Возможное здоровье: {value:F1} по адресу {FormatAddress(testAddr)}");
                        return testAddr;
                    }
                }
                catch
                {
                }
            }

            return IntPtr.Zero;
        }

        static IntPtr ScanForDeathCount()
        {
            const int SCAN_SIZE = 0x100000;
            byte[] buffer = new byte[SCAN_SIZE];
            int bytesRead;

            if (!ReadProcessMemory(hProcess, baseAddress, buffer, SCAN_SIZE, out bytesRead))
                return IntPtr.Zero;

            // Ищем целые числа (счетчики смертей)
            for (int i = 0; i <= bytesRead - 4; i += 4)
            {
                int value = BitConverter.ToInt32(buffer, i);

                if (value >= 0 && value < 1000)
                {
                    IntPtr testAddr = IntPtr.Add(baseAddress, i);
                    return testAddr;
                }
            }

            return IntPtr.Zero;
        }

        static IntPtr ScanForLevelString()
        {
            const int SCAN_SIZE = 0x100000;
            byte[] buffer = new byte[SCAN_SIZE];
            int bytesRead;

            if (!ReadProcessMemory(hProcess, baseAddress, buffer, SCAN_SIZE, out bytesRead))
                return IntPtr.Zero;

            // Ищем строки с названиями уровней
            foreach (string pattern in LEVEL_PATTERNS)
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
                        // Находим начало строки
                        int start = i;
                        while (start > 0 && buffer[start - 1] != 0)
                            start--;

                        IntPtr foundAddr = IntPtr.Add(baseAddress, start);
                        string foundString = ReadString(foundAddr, 50, Encoding.ASCII);

                        if (!string.IsNullOrEmpty(foundString) && foundString.Contains(".World"))
                        {
                            return foundAddr;
                        }
                    }
                }
            }

            return IntPtr.Zero;
        }

        static IntPtr ScanMemoryForPattern(byte[] pattern, int startOffset, int size)
        {
            byte[] buffer = new byte[size];
            int bytesRead;

            IntPtr startAddr = IntPtr.Add(baseAddress, startOffset);
            if (!ReadProcessMemory(hProcess, startAddr, buffer, size, out bytesRead))
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

        // ================= ЧТЕНИЕ ПАМЯТИ =================
        static string ReadLevelName()
        {
            if (hProcess == IntPtr.Zero || levelAddress == IntPtr.Zero)
                return SEARCHING_GAME_TEXT;

            try
            {
                string level = ReadString(levelAddress, MAX_LEVEL_NAME_LENGTH, Encoding.ASCII);

                if (string.IsNullOrWhiteSpace(level) || level.IndexOf('\0') >= 0)
                    level = MENU_LABEL;

                isMenu = level == MENU_LABEL || !level.EndsWith(".World00p", StringComparison.OrdinalIgnoreCase);
                return level;
            }
            catch
            {
                failedReads++;
                if (failedReads > 5)
                {
                    levelAddress = IntPtr.Zero;
                    failedReads = 0;
                }

                return READ_ERROR_TEXT;
            }
        }

        static float ReadHealth()
        {
            if (hProcess == IntPtr.Zero || healthAddress == IntPtr.Zero || isMultiplayer)
                return 0f;

            try
            {
                float health = ReadFloat(healthAddress);
                float healthMin = GetCurrentHealthMin();
                float healthMax = GetCurrentHealthMax();

                return health < healthMin ? healthMin : (health > healthMax ? healthMax : health);
            }
            catch
            {
                return 0f;
            }
        }

        static int ReadDeathCount()
        {
            if (hProcess == IntPtr.Zero || deathCountAddress == IntPtr.Zero || isMultiplayer)
                return deathCount;

            try
            {
                int deaths = ReadInt(deathCountAddress);
                deathCount = Math.Max(deathCount, deaths);
                return deathCount;
            }
            catch
            {
                return deathCount;
            }
        }

        #region Чтение памяти
        /// <summary>
        /// Читает сырые байты из целевого процесса.
        /// Возвращает null при ошибке.
        /// </summary>
        static byte[] ReadBytes(IntPtr address, int size)
        {
            if (address == IntPtr.Zero || hProcess == IntPtr.Zero || size <= 0)
                return null;

            byte[] buffer = new byte[size];
            int bytesRead;

            if (ReadProcessMemory(hProcess, address, buffer, size, out bytesRead) && bytesRead > 0)
            {
                if (bytesRead == size)
                    return buffer;

                var actual = new byte[bytesRead];
                Array.Copy(buffer, actual, bytesRead);
                return actual;
            }

            return null;
        }

        /// <summary>
        /// Проходит по цепочке указателей (base + offsets) и возвращает конечный адрес.
        /// Метод корректно работает на 32- и 64-битных процессах.
        /// </summary>
        static IntPtr FollowPointerChain(PointerChain chain)
        {
            if (chain.BaseAddress == IntPtr.Zero)
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

        static string ReadString(IntPtr address, int maxLength, Encoding encoding)
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

        static float ReadFloat(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return 0f;

            var bytes = ReadBytes(address, 4);
            if (bytes == null || bytes.Length < 4)
                return 0f;

            return BitConverter.ToSingle(bytes, 0);
        }

        static int ReadInt(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return 0;

            var bytes = ReadBytes(address, 4);
            if (bytes == null || bytes.Length < 4)
                return 0;

            return BitConverter.ToInt32(bytes, 0);
        }
        #endregion

        // ================= ОБРАБОТКА ИГРОВЫХ ДАННЫХ =================
        static string lastLevel = "";
        static float lastHealth = 100f;

        static void ProcessGameData(string levelName, float health, int deaths)
        {
            // Смена уровня
            if (lastLevel != levelName && !isMenu)
            {
                OnLevelChanged(lastLevel, levelName);
                lastLevel = levelName;

                currentLevelInfo = GetLevelInfo(levelName);

                if (currentLevelInfo != null)
                {
                    LogEvent($"Уровень: {currentLevelInfo.EpisodeName} - {currentLevelInfo.Location}");

                    // Запись в статистику
                    LogToStats($"Уровень: {levelName} -> {currentLevelInfo.Location}");
                }
                else
                {
                    LogWarning($"Неизвестный уровень: {levelName}");
                }
            }

            // Смерть игрока
            if (lastHealth > 0 && health <= 0)
            {
                deathCount++;
                OnPlayerDeath(deathCount);

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"☠️  СМЕРТЬ #{deathCount}");
                Console.ResetColor();

                // Обновляем сессию
                if (currentSession != null)
                {
                    currentSession.DeathCount = deathCount;
                }
            }

            // Изменение здоровья
            if (Math.Abs(lastHealth - health) > 1.0f)
            {
                OnHealthChanged(lastHealth, health);
            }

            lastHealth = health;
            currentHealth = health;
        }

        static LevelInfo GetLevelInfo(string levelName)
        {
            // Прямой поиск в базе данных
            if (levelDatabase.TryGetValue(levelName, out var info))
            {
                return info;
            }

            // Поиск по алиасам
            foreach (var kvp in levelDatabase)
            {
                if (kvp.Value.Aliases != null)
                {
                    foreach (var alias in kvp.Value.Aliases)
                    {
                        if (levelName.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return kvp.Value;
                        }
                    }
                }
            }

            // Создаем запись по умолчанию
            return new LevelInfo
            {
                Episode = 0,
                EpisodeName = "Неизвестный",
                Location = Path.GetFileNameWithoutExtension(levelName),
                Type = "Кастомный",
                Aliases = Array.Empty<string>()
            };
        }

        static void OnLevelChanged(string oldLevel, string newLevel)
        {
            LogInfo($"Смена уровня: {oldLevel} → {newLevel}");
        }

        static void OnPlayerDeath(int totalDeaths)
        {
            LogEvent($"Смерть #{totalDeaths}");
            LogToStats($"Смерть #{totalDeaths}");
        }

        static void OnHealthChanged(float oldHealth, float newHealth)
        {
            // Логика для отображения урона/лечения
            if (newHealth < oldHealth)
            {
                float damage = oldHealth - newHealth;
                LogDebug($"Получено урона: {damage:F1}");
            }
            else if (newHealth > oldHealth)
            {
                float heal = newHealth - oldHealth;
                LogDebug($"Восстановлено здоровья: {heal:F1}");
            }
        }

        #region Discord Status
        // ================= DISCORD STATUS =================
        static void UpdateDiscordStatus(string levelName, float health, int deaths)
        {
            if (discordClient == null || !discordClient.IsInitialized)
                return;

            var presence = new RichPresence();
            string largeImage = GetNextLargeImage();

            // Определяем время начала для таймера
            DateTime startTime = DateTime.MinValue;

            if (currentSession != null && currentSession.ProcessStartTime != DateTime.MinValue)
            {
                // Используем время запуска процесса
                startTime = currentSession.ProcessStartTime;
            }
            else if (currentSession != null && currentSession.GameStartTime != DateTime.MinValue)
            {
                // Используем сохраненное время начала игры
                startTime = currentSession.GameStartTime;
            }
            else if (gameProcess != null)
            {
                // Пытаемся получить время запуска процесса сейчас
                try
                {
                    startTime = GetProcessStartTime(gameProcess);
                }
                catch
                {
                    startTime = DateTime.UtcNow;
                }
            }

            string gameTitle = GetGameTitle();
            string healthRange = GetCurrentHealthMax().ToString("F0");

            if (isMultiplayer)
            {
                presence.Details = $"🎮 {gameTitle} Multiplayer";
                presence.State = "Сражение онлайн";
                presence.Assets = new Assets
                {
                    LargeImageKey = settings.GameImages.ContainsKey("MultiplayerImage")
                        ? settings.GameImages["MultiplayerImage"]
                        : "fear_mp",
                    LargeImageText = $"{gameTitle} Combat",
                    SmallImageKey = "online",
                    SmallImageText = "В сети"
                };
            }
            else if (isMenu)
            {
                presence.Details = $"{gameTitle} - В главном меню";
                presence.State = "Выбор уровня";
                presence.Assets = new Assets
                {
                    LargeImageKey = largeImage,
                    LargeImageText = $"{gameTitle} - Меню",
                    SmallImageKey = "menu",
                    SmallImageText = "Меню"
                };
            }
            else
            {
                if (currentLevelInfo != null)
                {
                    if (currentLevelInfo.Episode > 0)
                    {
                        presence.Details = $"{gameTitle} - Эпизод {currentLevelInfo.Episode:00}";
                        presence.State = $"{currentLevelInfo.Location}";
                    }
                    else
                    {
                        presence.Details = $"{gameTitle} - {currentLevelInfo.Location}";
                        presence.State = currentLevelInfo.EpisodeName;
                    }
                }
                else
                {
                    presence.Details = $"{gameTitle} - {levelName}";
                    presence.State = "Исследование";
                }

                if (health > 0)
                {
                    presence.State += $" | ❤️ {health:F0}/{healthRange}";
                }

                if (deaths > 0)
                {
                    presence.State += $" | ☠️ {deaths}";
                }

                string healthIcon = GetHealthIcon(health);

                presence.Assets = new Assets
                {
                    LargeImageKey = largeImage,
                    LargeImageText = $"{gameTitle} Single Player",
                    SmallImageKey = healthIcon,
                    SmallImageText = health > 0 ? $"Здоровье: {health:F0}/{healthRange}" : "Мёртв"
                };
            }

            // Устанавливаем время начала игры
            if (startTime != DateTime.MinValue)
            {
                presence.Timestamps = new Timestamps
                {
                    Start = startTime
                };
            }

            // Добавляем кнопки
            var buttons = new List<Button>();

            if (currentGameVersion == "FEAR" || currentGameVersion == "FEARMP")
            {
                buttons.Add(new Button
                {
                    Label = "Steam Store",
                    Url = "https://store.steampowered.com/app/21090/FEAR/"
                });
            }
            else if (currentGameVersion.Contains("FEAR2"))
            {
                buttons.Add(new Button
                {
                    Label = "Steam Store",
                    Url = "https://store.steampowered.com/app/16450/FEAR_2_Project_Origin/"
                });
            }
            else if (currentGameVersion.Contains("FEAR3"))
            {
                buttons.Add(new Button
                {
                    Label = "Steam Store",
                    Url = "https://store.steampowered.com/app/21100/FEAR_3/"
                });
            }

            if (buttons.Count > 0)
            {
                presence.Buttons = buttons.ToArray();
            }

            discordClient.SetPresence(presence);
        }

        static string GetGameTitle()
        {
            return currentGameVersion switch
            {
                "FEAR" or "FEARMP" => "F.E.A.R",
                "FEAR2" or "FEAR2MP" => "F.E.A.R 2",
                "FEAR3" or "Fear3" => "F.E.A.R 3",
                _ => "F.E.A.R"
            };
        }

        static float GetCurrentHealthMin()
        {
            return currentGameVersion switch
            {
                "FEAR" or "FEARMP" => settings.HealthSettings["Fear1Min"],
                "FEAR2" or "FEAR2MP" => settings.HealthSettings["Fear2Min"],
                "FEAR3" or "Fear3" => settings.HealthSettings["Fear3Min"],
                _ => 0
            };
        }

        static float GetCurrentHealthMax()
        {
            return currentGameVersion switch
            {
                "FEAR" or "FEARMP" => settings.HealthSettings["Fear1Max"],
                "FEAR2" or "FEAR2MP" => settings.HealthSettings["Fear2Max"],
                "FEAR3" or "Fear3" => settings.HealthSettings["Fear3Max"],
                _ => 100
            };
        }

        static string GetHealthIcon(float health)
        {
            float maxHealth = GetCurrentHealthMax();
            float percent = health / maxHealth * 100f;

            return percent > 75 ? "healthy" :
                percent > 30 ? "injured" :
                percent > 0 ? "critical" : "dead";
        }

        static string GetNextLargeImage()
        {
            if (settings.LargeImages == null || settings.LargeImages.Length == 0)
                return "fear_menu";

            if (lastImageChangeTime == DateTime.MinValue ||
                (DateTime.Now - lastImageChangeTime).TotalSeconds >= settings.ImageChangeInterval)
            {
                currentImageIndex = (currentImageIndex + 1) % settings.LargeImages.Length;
                lastImageChangeTime = DateTime.Now;
            }

            // Для разных игр можно использовать разные изображения
            string baseImage = settings.LargeImages[currentImageIndex];

            // Добавляем суффикс в зависимости от игры
            if (currentGameVersion.Contains("2") && settings.GameImages.ContainsKey("Fear2Image"))
                return settings.GameImages["Fear2Image"];
            else if (currentGameVersion.Contains("3") && settings.GameImages.ContainsKey("Fear3Image"))
                return settings.GameImages["Fear3Image"];

            return baseImage;
        }

        #region Console Display
        // ================= ОТОБРАЖЕНИЕ СТАТУСА =================
        static void DisplayStatus(string level, float health, int deaths)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"{"",-60}");
            Console.WriteLine($"┌────────────────────────────────────────────────────────┐");
            Console.Write("│ Игра: ");
            WriteColored($"{GetGameTitle(),-20}", ConsoleColor.Cyan);
            Console.Write(" Уровень: ");
            WriteColored($"{level,-20}", ConsoleColor.White);
            Console.WriteLine(" │");

            if (!isMultiplayer && health > 0)
            {
                Console.Write("│ Здоровье: ");
                WriteColored($"{health,5:F1}/{GetCurrentHealthMax():F0} HP", GetHealthColor(health));
                Console.WriteLine($"{"",35} │");
            }

            if (currentLevelInfo != null && currentLevelInfo.Episode > 0)
            {
                Console.Write($"│ Эпизод: ");
                WriteColored($"{currentLevelInfo.Episode:00} - {currentLevelInfo.EpisodeName}", ConsoleColor.Yellow);
                Console.WriteLine($"{"",30} │");
            }

            Console.Write("│ Режим: ");
            WriteColored($"{(isMultiplayer ? "Мультиплеер" : "Одиночная")}", isMultiplayer ? ConsoleColor.Yellow : ConsoleColor.Green);
            Console.Write($"{"",15} Процесс: ");
            WriteColored($"{gameProcess?.ProcessName ?? "Не найден",-15}", ConsoleColor.Magenta);
            Console.WriteLine(" │");

            // Время игры (с момента запуска процесса)
            if (gameProcess != null && processStartTime != DateTime.MinValue)
            {
                var playTime = DateTime.UtcNow - processStartTime;
                Console.Write($"│ Время игры: ");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"{playTime.Hours:00}:{playTime.Minutes:00}:{playTime.Seconds:00}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"{"",38} │");
            }

            if (deaths > 0)
            {
                Console.Write($"│ Смертей: ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"{deaths}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"{"",48} │");
            }

            Console.WriteLine($"└────────────────────────────────────────────────────────┘");
            Console.ResetColor();
        }

        static ConsoleColor GetHealthColor(float health)
        {
            float maxHealth = GetCurrentHealthMax();
            float percent = health / maxHealth * 100f;

            return percent > 70 ? ConsoleColor.Green :
                percent > 30 ? ConsoleColor.Yellow :
                percent > 0 ? ConsoleColor.Red : ConsoleColor.DarkRed;
        }
        #endregion

        #region Level Database
        // ================= БАЗА ДАННЫХ УРОВНЕЙ (ПО УМОЛЧАНИЮ) =================
        /// <summary>
        /// Создаёт базу уровней по умолчанию для различных эпизодов/демо/тестов.
        /// </summary>
        static Dictionary<string, LevelInfo> CreateDefaultLevelDatabase()
        {
            var db = new Dictionary<string, LevelInfo>(StringComparer.OrdinalIgnoreCase);

            // ================= ЭПИЗОД 01 — ПОСВЯЩЕНИЕ =================
            AddLevel(db, "Intro.World00p", 1, "Посвящение", "Заброшенный дом", "Сюжет",
                new[] { "Intro", "Start", "Beginning", "Training" });

            // ================= ЭПИЗОД 02 — ВСТУПЛЕНИЕ =================
            AddLevel(db, "Docks.World00p", 2, "Вступление", "Происшествие в порту", "Сюжет",
                new[] { "Docks", "Port", "Harbor", "Warehouse" });

            // ================= ЭПИЗОД 03 — РАССЛЕДОВАНИЕ =================
            AddLevel(db, "WTF_Entry.World00p", 3, "Обострение", "Дренажная галерея", "Сюжет",
                new[] { "WTF_Entry", "Psychic_Area", "Alma_Domain", "Distortion" });

            AddLevel(db, "Vault.World00p", 3, "Расследование", "Хранилище данных", "Сюжет",
                new[] { "Vault", "Archive", "Data_Vault", "Secure_Storage" });

            // ================= ЭПИЗОД 04 — ВТОРЖЕНИЕ =================
            AddLevel(db, "Moody.World00p", 4, "Вторжение", "Офисное здание Муди", "Сюжет",
                new[] { "Moody", "Office", "Armacham_Tower", "Corporate" });

            AddLevel(db, "ATC_Roof.World00p", 4, "Вторжение", "ШТУРМ | Крыша ATC", "Сюжет",
                new[] { "ATC_Roof", "MP_Roof" });

            AddLevel(db, "Admin.World00p", 4, "Вторжение", "СТРАЖИ | Административный блок", "Сюжет",
                new[] { "Admin", "Command_Center", "Fettel_Room", "Final_Approach" });

            AddLevel(db, "Facility_Upper.World00p", 4, "Вторжение", "Верхние этажи ATC", "Сюжет",
                new[] { "Facility_Upper", "ATC_Upper", "HQ", "Upper_Floor" });

            AddLevel(db, "Facility_Bypass.World00p", 4, "Вторжение", "Обход объекта", "Сюжет",
                new[] { "Facility_Bypass", "Ventilation", "Service_Tunnel", "Bypass" });

            // ================= ЭПИЗОД 05 — КОНФРОНТАЦИЯ =================
            AddLevel(db, "Bishop_Evac.World00p", 5, "Извлечение", "Неожиданный удар", "Сюжет",
                new[] { "Bishop_Evac", "MP_Evac" });

            AddLevel(db, "Bishop_Rescue.World00p", 5, "Извлечение", "Спасение Бишопа", "Сюжет",
                new[] { "Bishop_Rescue", "MP_Rescue" });

            AddLevel(db, "WTF_Ambush.World00p", 5, "Конфронтация", "Засада в коридорах", "Сюжет",
                new[] { "WTF_Ambush", "Hallway", "Ambush", "Corridor" });

            AddLevel(db, "WTF_Exfil.World00p", 5, "Конфронтация", "Эвакуация", "Сюжет",
                new[] { "WTF_Exfil", "Escape", "Exfiltration", "Exit" });

            // ================= ЭПИЗОД 06 — РАЗВЯЗКА =================
            AddLevel(db, "Mapes_Elevator.World00p", 6, "Пресечение", "Саёнара, удар | Офисы ATC", "Сюжет",
                new[] { "Mapes_Elevator", "Elevator", "Maintenance_Shaft", "Descent" });

            AddLevel(db, "Badge.World00p", 6, "Пресечение", "Неопознанные нарушители", "Сюжет",
                new[] { "Badge", "MP_Badge" });

            AddLevel(db, "Hives.World00p", 6, "Пресечение", "Тень прошлого | Лаборатория «Улья»", "Сюжет",
                new[] { "Hives", "Laboratory", "Clone_Lab", "Experiment" });

            AddLevel(db, "Alma.World00p", 6, "Развязка", "Логово Альмы", "Сюжет",
                new[] { "Alma", "Final_Boss", "Confrontation", "Ending" });

            AddLevel(db, "Aftermath.World00p", 6, "Развязка", "Последствия", "Сюжет",
                new[] { "Aftermath", "Explosion", "Collapse", "Epilogue" });

            // ================= ЭПИЗОД 07 — РАЗВЯЗКА =================
            AddLevel(db, "Alice.World00p", 7, "Изменение", "Элис Вэйд | Эвакуация", "Сюжет",
                new[] { "Alice", "Nursery", "Child_Room", "Vision" });
            AddLevel(db, "Getting_Out.World00p", 7, "Изменение", "Побег | Крыша ATC", "Сюжет",
                new[] { "Getting_Out", "Escape" });

            // ================= ЭПИЗОД 08 — =================
            AddLevel(db, "Wades.World00p", 8, "Опустошение", "Трущобы |Заброшенный дом", "Сюжет",
                new[] { "Wades", "Executive_Office", "Wade_Room", "Armacham_HQ" });

            AddLevel(db, "Factory.World00p", 8, "Опустошение", "Точка входа | Фабрика клонов", "Сюжет",
                new[] { "Factory", "Clone_Factory", "Production_Line", "Replica_Plant" });

            // ================= ДЕМО / МУЛЬТИПЛЕЕР / ТЕСТЫ =================
            AddLevel(db, "FEAR_SP_Demo_Intro.World00p", 0, "Демо", "Вступление (демо)", "Демо",
                new[] { "FEAR_SP_Demo_Intro", "Demo_Start" });

            AddLevel(db, "FEAR_SP_Demo_World00p", 0, "Демо", "Основной уровень (демо)", "Демо",
                new[] { "FEAR_SP_Demo_World00p", "Demo_Main" });

            AddLevel(db, "E3_Demo_2005_Short.World00p", 0, "Демо", "E3 2005 (короткая версия)", "Демо",
                new[] { "E3_Demo_2005_Short", "E3_2005" });

            AddLevel(db, "Dock_005_Short.World00p", 0, "Мультиплеер", "Короткий док", "Мультиплеер",
                new[] { "Dock_005_Short", "MP_Short_Dock" });

            AddLevel(db, "Performance_World00p", 0, "Тест", "Выступление", "Тест",
                new[] { "Performance_World00p", "Test_Performance" });

            AddLevel(db, "Performance_Combat.World00p", 0, "Тест", "Боевое выступление", "Тест",
                new[] { "Performance_Combat", "Test_Combat" });

            return db;
        }

        /// <summary>
        /// Утилита для добавления уровня в базу данных.
        /// </summary>
        static void AddLevel(Dictionary<string, LevelInfo> db, string key, int episode,
            string episodeName, string location, string type, string[] aliases)
        {
            db[key] = new LevelInfo
            {
                Episode = episode,
                EpisodeName = episodeName,
                Location = location,
                Type = type,
                Aliases = aliases
            };
        }
        #endregion

        // ================= УТИЛИТЫ =================
        static string FormatAddress(IntPtr address)
        {
            if (address == IntPtr.Zero)
                return "0x0";

            return $"0x{address.ToInt64():X}";
        }

        // Утилита для удобной цветной печати
        static void WriteColored(string text, ConsoleColor color)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = prev;
        }

        static void WriteLineColored(string text, ConsoleColor color)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = prev;
        }

        static void Cleanup()
        {
            if (hProcess != IntPtr.Zero)
            {
                CloseHandle(hProcess);
                hProcess = IntPtr.Zero;
            }

            gameProcess = null;
            baseAddress = IntPtr.Zero;
            levelAddress = IntPtr.Zero;
            healthAddress = IntPtr.Zero;
            deathCountAddress = IntPtr.Zero;

            currentLevel = "";
            currentHealth = 100f;
            currentLevelInfo = null;
            failedReads = 0;
            currentImageIndex = 0;
            lastImageChangeTime = DateTime.MinValue;
        }

        static void LogToStats(string message)
        {
            try
            {
                string log = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}\n";
                File.AppendAllText(STATS_FILE, log, Encoding.UTF8);
            }
            catch
            {
            }
        }

        // ================= ЛОГИРОВАНИЕ =================
        static void LogInfo(string message)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO: {message}");
            Console.ResetColor();
        }

        static void LogSuccess(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✓ {message}");
            Console.ResetColor();
        }

        static void LogWarning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠ {message}");
            Console.ResetColor();
        }

        static void LogError(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✗ {message}");
            Console.ResetColor();
        }

        static void LogEvent(string message)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ► {message}");
            Console.ResetColor();
        }

        static void LogDebug(string message)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] DEBUG: {message}");
            Console.ResetColor();
        }

        /// <summary>
        /// Печатает информацию об исключении в консоль в едином формате.
        /// </summary>
        static void LogException(Exception ex, string context = "")
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] EXC: {context} {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }

        // ================= ОБРАБОТЧИКИ ЗАКРЫТИЯ =================
        static void OnConsoleCancel(object sender, ConsoleCancelEventArgs e)
        {
            CleanupResources();
        }

        static void OnProcessExit(object sender, EventArgs e)
        {
            CleanupResources();
        }

        static void CleanupResources()
        {
            LogInfo("Завершение работы...");

            // Сохраняем сессию перед выходом
            SaveSession();

            // Очищаем Discord RPC
            if (discordClient != null)
            {
                discordClient.ClearPresence();
                discordClient.Dispose();
            }

            // Закрываем хэндл процесса
            if (hProcess != IntPtr.Zero)
            {
                CloseHandle(hProcess);
            }

            LogInfo("Ресурсы освобождены. До свидания!");
        }
    }
}