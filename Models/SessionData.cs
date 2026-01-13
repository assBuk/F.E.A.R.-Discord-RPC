using System;

namespace UniversalFearRPC
{
    /// <summary>
    /// Сериализуемые данные сессии (для авто-восстановления).
    /// Вынесены из `Program` и сделаны публичными для повторного использования и тестирования.
    /// </summary>
    public class SessionData
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
}
