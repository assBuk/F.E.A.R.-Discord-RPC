using System;
using DiscordRPC;

namespace UniversalFearRPC
{
    public class DiscordManager : IDisposable
    {
        private DiscordRpcClient _client;
        private string _appId;

        public bool IsInitialized => _client != null && _client.IsInitialized;

        public DiscordManager(string appId)
        {
            _appId = appId;
        }

        public void Initialize()
        {
            if (_client != null)
                return;

            try
            {
                _client = new DiscordRpcClient(_appId);
                _client.OnReady += (sender, e) => { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✓ Discord RPC подключен: {e.User.Username}"); };
                _client.OnError += (sender, e) => { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✗ Discord ошибка: {e.Message}"); };
                _client.Initialize();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO: Discord RPC инициализирован");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✗ Ошибка инициализации Discord: {ex.Message}");
            }
        }

        public void SetPresence(RichPresence presence)
        {
            if (_client == null || !_client.IsInitialized)
                return;

            _client.SetPresence(presence);
        }

        public void ClearAndDispose()
        {
            if (_client != null)
            {
                try
                {
                    _client.ClearPresence();
                    _client.Dispose();
                }
                catch
                {
                }

                _client = null;
            }
        }

        public void Dispose()
        {
            ClearAndDispose();
        }
    }
}
