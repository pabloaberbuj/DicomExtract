using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EvilDICOM.Core;
using EvilDICOM.Core.Element;
using EvilDICOM.Core.Enums;
using EvilDICOM.Core.Helpers;
using EvilDICOM.Network;
using EvilDICOM.Network.DIMSE;
using EvilDICOM.Network.DIMSE.IOD;
using EvilDICOM.Network.Enums;
using EvilDICOM.Network.Messaging;

namespace DicomExport.Core
{
    /// <summary>
    /// Pide objetos al daemon por C-MOVE (destino: File Service VMSFD), los recoge de la carpeta VMSFD
    /// y los entrega a la carpeta de cada tipo (opcionalmente anonimizados).
    /// Un lock de archivo en VMSFD serializa los exports entre procesos/PCs.
    /// Llamar fuera del hilo de UI (bloquea hasta minutos).
    /// </summary>
    public class DicomExporter
    {
        private const int CMOVE_TIMEOUT_MS = 30000;
        private const int ESPERA_ARCHIVOS_SEG = 120;
        private const int ESTABILIDAD_SEG = 5;

        // Tags a anonimizar (estándar DICOM)
        private static readonly string[] TAGS_BORRAR =
        {
            "00100010", "00100020", "00100030", "00080090", "00102000",
            "00101000", "00101001", "00380010", "00400275", "00321070",
            "00401400", "00401004", "00401102", "00401104", "00402001",
            "00402016", "00402017", "00400009", "00400007", "00081048",
            "00081049", "00081070", "00080080", "00080081", "00081040",
            "00102110", "00101040", "00100050", "00102297", "00102299",
            "00380500", "00101010", "00101020", "00101030", "00102155",
            "00102154", "00102150",
        };

        private readonly NetworkConfig _net;
        private readonly Action<string> _log;

        public DicomExporter(NetworkConfig net, Action<string> log = null)
        {
            _net = net;
            _log = log ?? (s => { });
        }

        /// <summary>
        /// Exporta lo pedido en <paramref name="o"/>. <paramref name="anonId"/> solo se usa si o.Anonimizar.
        /// Devuelve las rutas escritas. Lanza excepción si VMSFD no es accesible, falta destino
        /// para algún tipo pedido, o vence el lock.
        /// </summary>
        public List<string> Exportar(PlanUids u, ExportOptions o, ExportDestinos d, string anonId)
        {
            var pedidos = new List<KeyValuePair<string, string>>(); // (level, uid)
            if (Pidio(o, u, "CT")) pedidos.Add(Pedido("SERIES", u.CtSeriesUid, "CT"));
            if (Pidio(o, u, "RTSTRUCT")) pedidos.Add(Pedido("IMAGE", u.RsUid, "RS"));
            if (Pidio(o, u, "RTDOSE")) pedidos.Add(Pedido("IMAGE", u.RdUid, "RD"));
            if (Pidio(o, u, "RTPLAN")) pedidos.Add(Pedido("IMAGE", u.RpUid, "RP"));
            if (Pidio(o, u, "RTIMAGE"))
                foreach (string ri in u.RiUids) pedidos.Add(Pedido("IMAGE", ri, "RI"));

            var escritos = new List<string>();
            if (pedidos.Count == 0) return escritos;

            if (!Directory.Exists(_net.VmsfdPath))
                throw new DirectoryNotFoundException("No se accede a VMSFD_PATH: " + _net.VmsfdPath);
            foreach (string mod in new[] { "CT", "RTSTRUCT", "RTDOSE", "RTPLAN", "RTIMAGE" })
                if (Pidio(o, u, mod) && string.IsNullOrWhiteSpace(d.Resolve(mod)))
                    throw new ArgumentException("Sin carpeta de destino para " + mod);

            using (AdquirirLock())
            {
                // No se limpia VMSFD: solo se toman archivos que aparecen después del C-MOVE.
                var previos = new HashSet<string>(ListarDcm(), StringComparer.OrdinalIgnoreCase);

                foreach (var p in pedidos) DispararCMove(p.Key, p.Value);

                var nuevos = EsperarNuevos(previos);
                _log("   → " + nuevos.Count + " archivos DICOM recibidos");

                foreach (string archivo in nuevos)
                {
                    try
                    {
                        string modality = LeerModality(archivo);
                        string carpeta = d.Resolve(modality);
                        if (string.IsNullOrWhiteSpace(carpeta))
                        { _log("   [WARN] " + Path.GetFileName(archivo) + ": modalidad '" + modality + "' sin destino, se omite"); continue; }

                        Directory.CreateDirectory(carpeta);
                        string destino = Path.Combine(carpeta, Path.GetFileName(archivo));
                        if (File.Exists(destino)) File.Delete(destino);
                        if (o.Anonimizar) { AnonimizarYGuardar(archivo, destino, anonId); File.Delete(archivo); }
                        else File.Move(archivo, destino);
                        escritos.Add(destino);
                    }
                    catch (Exception ex) { _log("   [WARN] " + Path.GetFileName(archivo) + ": " + ex.Message); }
                }
                BorrarSubcarpetasVacias();
            }
            return escritos;
        }

        private static bool Pidio(ExportOptions o, PlanUids u, string modality)
        {
            switch (modality)
            {
                case "CT": return o.CT && !string.IsNullOrEmpty(u.CtSeriesUid);
                case "RTSTRUCT": return o.RS && !string.IsNullOrEmpty(u.RsUid);
                case "RTDOSE": return o.RD && !string.IsNullOrEmpty(u.RdUid);
                case "RTPLAN": return o.RP && !string.IsNullOrEmpty(u.RpUid);
                default: return o.RI && u.RiUids != null && u.RiUids.Count > 0;
            }
        }

        private KeyValuePair<string, string> Pedido(string level, string uid, string nombre)
        {
            _log("   → Solicitando " + nombre + "...");
            return new KeyValuePair<string, string>(level, uid);
        }

        // ─── Lock entre procesos/PCs ──────────────────────────────────────────
        private IDisposable AdquirirLock()
        {
            string path = Path.Combine(_net.VmsfdPath, ".export.lock");
            var limite = System.DateTime.UtcNow.AddSeconds(_net.LockTimeoutSeg);
            bool avisado = false;
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                catch (IOException)
                {
                    if (System.DateTime.UtcNow > limite)
                        throw new TimeoutException("Otro usuario está exportando; venció la espera del lock (" + _net.LockTimeoutSeg + "s).");
                    if (!avisado) { _log("   … otro usuario está exportando, esperando turno"); avisado = true; }
                    Thread.Sleep(2000);
                }
            }
        }

        // ─── C-MOVE ───────────────────────────────────────────────────────────
        private void DispararCMove(string level, string uid)
        {
            try
            {
                var iod = new CMoveIOD();
                iod.Elements.Add(new CodeString(TagHelper.QueryRetrieveLevel, level));
                switch (level)
                {
                    case "IMAGE": iod.Elements.Add(new UniqueIdentifier(TagHelper.SOPInstanceUID, uid)); break;
                    case "SERIES": iod.Elements.Add(new UniqueIdentifier(TagHelper.SeriesInstanceUID, uid)); break;
                }
                var request = new CMoveRequest(iod, _net.AeDestino, Root.STUDY, Priority.MEDIUM, 1);
                var localEntity = new Entity(_net.AeLocal, _net.IpLocal, 0);
                var remoteAria = new Entity(_net.AeAria, _net.IpDaemon, _net.PortDaemon);
                var scu = new DICOMSCU(localEntity);

                // SendMessage bloquea indefinidamente si ARIA no responde → Task con timeout.
                SendStatus response = null;
                var task = System.Threading.Tasks.Task.Run(() => { response = scu.SendMessage(request, remoteAria); });
                if (!task.Wait(CMOVE_TIMEOUT_MS))
                { _log("   [WARN] C-MOVE timeout (" + CMOVE_TIMEOUT_MS / 1000 + "s) para " + level + ":" + uid); return; }

                if (response != null && !response.WasAccepted)
                    _log("   [WARN] C-MOVE no aceptado para " + level + ":" + uid);
            }
            catch (Exception ex) { _log("   [WARN] C-MOVE falló para " + level + ":" + uid + " → " + ex.Message); }
        }

        // ─── Espera de archivos ───────────────────────────────────────────────
        private string[] ListarDcm()
        {
            return Directory.GetFiles(_net.VmsfdPath, "*.dcm", SearchOption.AllDirectories);
        }

        private List<string> NuevosDcm(HashSet<string> previos)
        {
            return ListarDcm().Where(f => !previos.Contains(f)).ToList();
        }

        private List<string> EsperarNuevos(HashSet<string> previos)
        {
            int espera = 0;
            while (espera < ESPERA_ARCHIVOS_SEG && NuevosDcm(previos).Count == 0) { Thread.Sleep(1000); espera++; }
            if (espera >= ESPERA_ARCHIVOS_SEG) { _log("   [TIMEOUT] no llegaron archivos"); return new List<string>(); }

            int anterior = 0, estable = 0;
            while (estable < ESTABILIDAD_SEG)
            {
                int n = NuevosDcm(previos).Count;
                if (n > anterior) { anterior = n; estable = 0; } else estable++;
                Thread.Sleep(1000);
            }
            return NuevosDcm(previos);
        }

        private void BorrarSubcarpetasVacias()
        {
            foreach (string dir in Directory.GetDirectories(_net.VmsfdPath))
                try
                {
                    if (!Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Any()) Directory.Delete(dir, true);
                }
                catch { }
        }

        // ─── DICOM helpers ────────────────────────────────────────────────────
        private static string LeerModality(string archivo)
        {
            var elem = DICOMObject.Read(archivo).FindFirst("00080060");
            return elem == null || elem.DData == null ? null : elem.DData.ToString().Trim();
        }

        private static void AnonimizarYGuardar(string origen, string destino, string anonId)
        {
            var dcm = DICOMObject.Read(origen);
            foreach (string tagHex in TAGS_BORRAR)
            {
                var elem = dcm.FindFirst(tagHex);
                if (elem == null) continue;
                if (tagHex == "00100030" || tagHex == "00101010" || tagHex == "00101020" || tagHex == "00101030")
                { dcm.Remove(elem.Tag); continue; }
                if (elem is LongString) ((LongString)elem).Data = anonId;
                else if (elem is ShortString) ((ShortString)elem).Data = anonId;
                else if (elem is PersonName) ((PersonName)elem).Data = anonId;
                else if (elem is LongText) ((LongText)elem).Data = anonId;
                else if (elem is ShortText) ((ShortText)elem).Data = anonId;
                else dcm.Remove(elem.Tag);
            }
            var patName = dcm.FindFirst("00100010") as PersonName;
            if (patName != null) patName.Data = anonId;
            var patId = dcm.FindFirst("00100020") as LongString;
            if (patId != null) patId.Data = anonId;
            dcm.Write(destino);
        }
    }
}
