using System;
using System.IO;

namespace UniversalFearRPC
{
    /// <summary>
    /// Управление сохранением и восстановлением сессии (Session.dat).
    /// </summary>
    public static class SessionManager
    {
        public static (SessionData session, bool wasTooOld) Load(string sessionFile, int maxSessionAgeHours)
        {
            try
            {
                if (!File.Exists(sessionFile))
                {
                    var fresh = new SessionData
                    {
                        GameStartTime = DateTime.UtcNow,
                        SessionStartTime = DateTime.UtcNow,
                        ProcessStartTime = DateTime.UtcNow
                    };
                    return (fresh, false);
                }

                using (var reader = new BinaryReader(File.Open(sessionFile, FileMode.Open)))
                {
                    var sd = new SessionData
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

                    var sessionAge = DateTime.UtcNow - sd.SessionStartTime;
                    if (sessionAge.TotalHours > maxSessionAgeHours)
                    {
                        sd.GameStartTime = DateTime.UtcNow;
                        sd.SessionStartTime = DateTime.UtcNow;
                        sd.ProcessStartTime = DateTime.UtcNow;
                        sd.DeathCount = 0;
                        sd.ImageIndex = 0;
                        return (sd, true);
                    }

                    return (sd, false);
                }
            }
            catch
            {
                var fresh = new SessionData
                {
                    GameStartTime = DateTime.UtcNow,
                    SessionStartTime = DateTime.UtcNow,
                    ProcessStartTime = DateTime.UtcNow
                };
                return (fresh, false);
            }
        }

        public static void Save(string sessionFile, SessionData session)
        {
            try
            {
                using (var writer = new BinaryWriter(File.Open(sessionFile, FileMode.Create)))
                {
                    writer.Write(session.GameStartTime.ToBinary());
                    writer.Write(session.SessionStartTime.ToBinary());
                    writer.Write(session.ProcessId);
                    writer.Write(session.ProcessName ?? "");
                    writer.Write(session.LastLevel ?? "");
                    writer.Write(session.DeathCount);
                    writer.Write(session.ImageIndex);
                    writer.Write(session.IsMultiplayer);
                    writer.Write(session.GameVersion ?? "");
                    writer.Write(session.ProcessStartTime.ToBinary());
                }
            }
            catch
            {
                // swallow - calling code logs
            }
        }
    }
}
