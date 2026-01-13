using System;
using System.IO;
using NUnit.Framework;

namespace UniversalFearRPC.Tests
{
    [TestFixture]
    public class SessionManagerTests
    {
        [Test]
        public void SaveAndLoad_Roundtrip_PreservesFields()
        {
            var temp = Path.GetTempFileName();
            try
            {
                var session = new SessionData
                {
                    GameStartTime = DateTime.UtcNow,
                    SessionStartTime = DateTime.UtcNow,
                    ProcessId = 123,
                    ProcessName = "TestProcess",
                    LastLevel = "L1",
                    DeathCount = 2,
                    ImageIndex = 1,
                    IsMultiplayer = true,
                    GameVersion = "FEAR",
                    ProcessStartTime = DateTime.UtcNow
                };

                SessionManager.Save(temp, session);
                var (loaded, wasTooOld) = SessionManager.Load(temp, 24);

                Assert.IsFalse(wasTooOld);
                Assert.AreEqual(session.ProcessId, loaded.ProcessId);
                Assert.AreEqual(session.ProcessName, loaded.ProcessName);
                Assert.AreEqual(session.LastLevel, loaded.LastLevel);
                Assert.AreEqual(session.DeathCount, loaded.DeathCount);
                Assert.AreEqual(session.ImageIndex, loaded.ImageIndex);
                Assert.AreEqual(session.IsMultiplayer, loaded.IsMultiplayer);
                Assert.AreEqual(session.GameVersion, loaded.GameVersion);
            }
            finally
            {
                File.Delete(temp);
            }
        }

        [Test]
        public void Load_MissingFile_ReturnsFreshSession()
        {
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var (session, wasTooOld) = SessionManager.Load(temp, 24);

            Assert.IsFalse(wasTooOld);
            Assert.That(session.SessionStartTime, Is.EqualTo(session.GameStartTime).Within(TimeSpan.FromMinutes(1)));
        }
    }
}
