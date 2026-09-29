using System;
using System.ServiceProcess;
using LayoutParserLib;

namespace LayoutParserDecrypt
{
    /// <summary>Wrapper <see cref="ServiceBase"/> em volta de <see cref="DecryptServer"/>.</summary>
    public sealed class DecryptWindowsService : ServiceBase
    {
        public const string Name = "LayoutParserDecrypt";
        private DecryptServer _server;

        public DecryptWindowsService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                var server = new DecryptServer(ServiceOptions.FromEnvironment());
                server.Start(); // falha de bind/urlacl lança aqui → o SCM reporta falha de início
                _server = server;
            }
            catch (Exception ex)
            {
                try { RollingFileLogger.Log("ERR", "Falha ao iniciar o serviço", ex); } catch { }
                EventLog.WriteEntry("Falha ao iniciar: " + ex.Message, System.Diagnostics.EventLogEntryType.Error);
                throw;
            }
        }

        protected override void OnStop()
        {
            RequestAdditionalTime((int)ServiceOptions.FromEnvironment().ShutdownTimeout.TotalMilliseconds + 5000);
            Shutdown();
        }
        protected override void OnShutdown() { Shutdown(); }

        private void Shutdown()
        {
            var s = _server;
            _server = null;
            if (s != null) s.Stop();
        }
    }
}
