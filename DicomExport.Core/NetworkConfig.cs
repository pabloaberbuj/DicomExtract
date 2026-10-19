using System.IO;
using System.Linq;

namespace DicomExport.Core
{
    /// <summary>Configuración de red y paths, leída de un config.ini (CLAVE=valor, '#' comenta).</summary>
    public class NetworkConfig
    {
        public string AeAria { get; set; } = "VMSDBD1";
        public string AeDestino { get; set; } = "VMSFD";
        public string IpDaemon { get; set; } = "10.0.1.71";
        public int PortDaemon { get; set; } = 10002;
        /// <summary>Carpeta donde el File Service VMSFD deja los .dcm. Desde otra PC: ruta UNC (ej. \\10.0.1.71\VMSFD\).</summary>
        public string VmsfdPath { get; set; } = @"\\10.0.1.71\VMSFD\";
        public string RootExportPath { get; set; } = @"C:\temp\FileService\";
        /// <summary>AE y host con que este equipo se presenta al daemon (solo SCU; no recibe nada).</summary>
        public string AeLocal { get; set; } = "EVIL_SCU";
        public string IpLocal { get; set; } = "127.0.0.1";
        /// <summary>Segundos máx. esperando el lock de VMSFD (otro usuario exportando).</summary>
        public int LockTimeoutSeg { get; set; } = 600;

        /// <summary>true si el ini existía; si no, quedan los valores por defecto.</summary>
        public bool Found { get; private set; }

        /// <summary>Override por PC, opcional: %LOCALAPPDATA%\DicomExtract\config.local.ini (pisa claves del ini compartido).</summary>
        public static string LocalIniPath
        {
            get
            {
                return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "DicomExtract", "config.local.ini");
            }
        }

        /// <summary>true si se aplicó config.local.ini.</summary>
        public bool LocalFound { get; private set; }

        public static NetworkConfig Load(string iniPath)
        {
            var cfg = new NetworkConfig();
            cfg.Found = cfg.Apply(iniPath);
            cfg.LocalFound = cfg.Apply(LocalIniPath);
            return cfg;
        }

        private bool Apply(string iniPath)
        {
            if (!File.Exists(iniPath)) return false;
            var cfg = this;

            foreach (string line in File.ReadAllLines(iniPath))
            {
                string l = line.Trim();
                if (l.StartsWith("#") || !l.Contains("=")) continue;
                int idx = l.IndexOf('=');
                string key = l.Substring(0, idx).Trim().ToUpper();
                string val = l.Substring(idx + 1).Trim();
                int n;
                switch (key)
                {
                    case "AE_ARIA": cfg.AeAria = val; break;
                    case "AE_DESTINO": cfg.AeDestino = val; break;
                    case "IP_DAEMON": cfg.IpDaemon = val; break;
                    case "PORT_DAEMON": if (int.TryParse(val, out n)) cfg.PortDaemon = n; break;
                    case "VMSFD_PATH": cfg.VmsfdPath = val; break;
                    case "ROOT_EXPORT_PATH": cfg.RootExportPath = val; break;
                    case "AE_LOCAL": cfg.AeLocal = val; break;
                    case "IP_LOCAL": cfg.IpLocal = val; break;
                    case "LOCK_TIMEOUT_SEG": if (int.TryParse(val, out n)) cfg.LockTimeoutSeg = n; break;
                }
            }
            return true;
        }
    }
}
