using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using DicomExport.Core;

namespace DicomExtract
{
    internal static class App
    {
        [STAThread]
        private static void Main()
        {
            new System.Windows.Application().Run(new MainWindow());
        }
    }

    /// <summary>UI WPF construida en código (sin XAML). Selección independiente de métricas / objetivos / cada tipo DICOM.</summary>
    internal class MainWindow : Window
    {
        private readonly NetworkConfig _net;
        private readonly ObservableCollection<PacienteRow> _pacientes = new ObservableCollection<PacienteRow>();

        private readonly TextBox _usuario = new TextBox();
        private readonly PasswordBox _password = new PasswordBox();
        private readonly TextBox _salida = new TextBox();
        private readonly ComboBox _sitio = new ComboBox { MinWidth = 110, ItemsSource = new[] { "CyC", "Pelvis", "Prostata" } };
        private readonly CheckBox _metadata = new CheckBox { Content = "Metadata de plan (CSV: estado, técnica, conteo de versiones…)", IsChecked = true };
        // Opción extra (próstata), apagada por defecto: métricas DVH vs constraints con normalización a D95
        private readonly CheckBox _metricas = new CheckBox { Content = "Métricas DVH + normalización D95 (opcional, próstata)", IsChecked = false };
        private readonly ComboBox _constraints = new ComboBox { IsEditable = true, MinWidth = 320 };
        private readonly CheckBox _objetivos = new CheckBox { Content = "Objetivos de optimización" };
        private readonly CheckBox _anon = new CheckBox { Content = "Anonimizar DICOM", IsChecked = true };
        private readonly CheckBox _ct = new CheckBox { Content = "CT" };
        private readonly CheckBox _rs = new CheckBox { Content = "RS (estructuras)" };
        private readonly CheckBox _rp = new CheckBox { Content = "RP (plan)" };
        private readonly CheckBox _rd = new CheckBox { Content = "RD (dosis)" };
        private readonly TextBox _dCt = new TextBox(), _dRs = new TextBox(), _dRp = new TextBox(), _dRd = new TextBox();
        private readonly TextBox _log = new TextBox
        {
            IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        private readonly Button _run = new Button { Content = "Extraer", Padding = new Thickness(24, 6, 24, 6) };
        private readonly Button _cancel = new Button { Content = "Cancelar", Padding = new Thickness(16, 6, 16, 6), IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        private volatile bool _cancelar;

        public MainWindow()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            _net = NetworkConfig.Load(Path.Combine(exeDir, "config.ini"));
            _salida.Text = _net.RootExportPath;

            Title = "DicomExtract";
            Width = 900; Height = 820;

            foreach (string f in Directory.GetFiles(exeDir, "constraints_*.txt")) _constraints.Items.Add(f);
            if (_constraints.Items.Count > 0) _constraints.SelectedIndex = 0;

            Content = ConstruirUi();
            _run.Click += (s, e) => Ejecutar();
            _cancel.Click += (s, e) => { _cancelar = true; _cancel.IsEnabled = false; Log("Cancelando al terminar el paciente actual..."); };

            Log(_net.Found ? "config.ini cargado." : "[WARN] No hay config.ini junto al exe; usando valores por defecto.");
            Log(_net.LocalFound ? $"Override local aplicado: {NetworkConfig.LocalIniPath}" : $"Sin override local ({NetworkConfig.LocalIniPath})");
            Log($"AE local {_net.AeLocal}@{_net.IpLocal}");
            Log($"Daemon {_net.AeAria}@{_net.IpDaemon}:{_net.PortDaemon} → AE destino {_net.AeDestino}; VMSFD = {_net.VmsfdPath}");
            if (_constraints.Items.Count == 0) Log("[WARN] No se encontró constraints_*.txt junto al exe; elija uno con '…' si usa métricas.");
        }

        // ─── Construcción de la UI ────────────────────────────────────────────
        private UIElement ConstruirUi()
        {
            var root = new Grid { Margin = new Thickness(10) };
            for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 0) Eclipse + salida
            var gEcl = new Grid();
            gEcl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gEcl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gEcl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gEcl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddCell(gEcl, new TextBlock { Text = "Usuario Eclipse", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 0, 0);
            AddCell(gEcl, _usuario, 0, 1);
            AddCell(gEcl, new TextBlock { Text = "Contraseña", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) }, 0, 2);
            AddCell(gEcl, _password, 0, 3);
            Fila(root, 0, Grupo("Eclipse", gEcl));

            // 1) Pacientes
            var pnlPac = new DockPanel();
            var btnCsv = new Button { Content = "Cargar CSV…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
            var btnClr = new Button { Content = "Vaciar", Padding = new Thickness(10, 3, 10, 3) };
            btnCsv.Click += (s, e) => CargarCsv();
            btnClr.Click += (s, e) => _pacientes.Clear();
            var barra = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            barra.Children.Add(btnCsv); barra.Children.Add(btnClr);
            barra.Children.Add(new TextBlock { Text = "  (o agregue filas a mano en la grilla: HC, Apellido, Curso, Plan)", VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(barra, Dock.Top);
            var grid = new DataGrid
            {
                ItemsSource = _pacientes, AutoGenerateColumns = false, CanUserAddRows = true, Height = 170
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "HC", Binding = new System.Windows.Data.Binding("Hc"), Width = 130 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Apellido", Binding = new System.Windows.Data.Binding("Apellido"), Width = 180 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Curso", Binding = new System.Windows.Data.Binding("Curso"), Width = 150 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Plan", Binding = new System.Windows.Data.Binding("Plan"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            pnlPac.Children.Add(barra); pnlPac.Children.Add(grid);
            Fila(root, 1, Grupo("Pacientes", pnlPac));

            // 2) Qué extraer
            var pnl = new StackPanel();
            var rowM = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var btnC = new Button { Content = "…", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
            btnC.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Constraints (*.txt)|*.txt|Todos|*.*" };
                if (dlg.ShowDialog() == true) _constraints.Text = dlg.FileName;
            };
            rowM.Children.Add(_metricas); rowM.Children.Add(new TextBlock { Text = "  constraints: ", VerticalAlignment = VerticalAlignment.Center });
            rowM.Children.Add(_constraints); rowM.Children.Add(btnC);
            var rowMeta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            rowMeta.Children.Add(_metadata);
            rowMeta.Children.Add(new TextBlock { Text = "   Sitio de esta sesión: ", VerticalAlignment = VerticalAlignment.Center });
            rowMeta.Children.Add(_sitio);
            pnl.Children.Add(rowMeta);
            pnl.Children.Add(rowM);
            pnl.Children.Add(_objetivos);

            var gD = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            gD.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            gD.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gD.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tipos = new[] { Tuple.Create(_ct, _dCt), Tuple.Create(_rs, _dRs), Tuple.Create(_rp, _dRp), Tuple.Create(_rd, _dRd) };
            for (int i = 0; i < tipos.Length; i++)
            {
                gD.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tb = tipos[i].Item2; tb.Margin = new Thickness(0, 2, 4, 2);
                tb.ToolTip = "Carpeta destino de este tipo. Vacío = {salida}\\{sesión}\\{paciente}\\";
                var b = new Button { Content = "…", Width = 28, Margin = new Thickness(0, 2, 0, 2) };
                b.Click += (s, e) => { var f = ElegirCarpeta(); if (f != null) tb.Text = f; };
                tipos[i].Item1.VerticalAlignment = VerticalAlignment.Center;
                AddCell(gD, tipos[i].Item1, i, 0); AddCell(gD, tb, i, 1); AddCell(gD, b, i, 2);
            }
            var dicomPnl = new StackPanel();
            dicomPnl.Children.Add(new TextBlock { Text = "Carpeta por tipo (opcional — vacío = carpeta de sesión por paciente):", Margin = new Thickness(0, 0, 0, 2) });
            dicomPnl.Children.Add(gD);
            dicomPnl.Children.Add(_anon);
            _anon.Margin = new Thickness(0, 6, 0, 0);
            pnl.Children.Add(new GroupBox { Header = "DICOM (vía daemon)", Content = dicomPnl, Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0) });
            Fila(root, 2, Grupo("Qué extraer", pnl));

            // 3) Salida + botones
            var gS = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            gS.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            gS.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gS.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var btnS = new Button { Content = "…", Width = 28, Margin = new Thickness(4, 0, 0, 0) };
            btnS.Click += (s, e) => { var f = ElegirCarpeta(); if (f != null) _salida.Text = f; };
            AddCell(gS, new TextBlock { Text = "Carpeta de salida  ", VerticalAlignment = VerticalAlignment.Center }, 0, 0);
            AddCell(gS, _salida, 0, 1); AddCell(gS, btnS, 0, 2);
            Fila(root, 3, gS);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            btns.Children.Add(_run); btns.Children.Add(_cancel);
            Fila(root, 4, btns);

            Fila(root, 5, _log);
            return root;
        }

        private static void AddCell(Grid g, UIElement e, int row, int col)
        {
            Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e);
        }

        private static void Fila(Grid g, int row, UIElement e) { Grid.SetRow(e, row); g.Children.Add(e); }

        private static GroupBox Grupo(string titulo, UIElement c)
        {
            return new GroupBox { Header = titulo, Content = c, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 6) };
        }

        private static string ElegirCarpeta()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
                return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }

        // ─── Acciones ─────────────────────────────────────────────────────────
        private void CargarCsv()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV/TXT (*.csv;*.txt)|*.csv;*.txt|Todos|*.*" };
            if (dlg.ShowDialog() != true) return;
            int n = 0;
            string[] lineas = File.ReadAllLines(dlg.FileName);
            if (lineas.Length == 0) return;
            // Separador según el encabezado: ';' (formato viejo) o ',' (export de listas)
            char sep = lineas[0].Count(ch => ch == ';') >= lineas[0].Count(ch => ch == ',') ? ';' : ',';
            string[] head = lineas[0].Split(sep).Select(h => h.Trim().Trim('"')).ToArray();
            // Columnas por nombre; si no están, posiciones del formato viejo (HC, Apellido, _, Curso, Plan)
            Func<string, int, int> col = (name, def) =>
            {
                int i = Array.FindIndex(head, h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : def;
            };
            int iHc = col("HC", 0), iAp = col("Apellido", 1), iCu = col("Curso", 3), iPl = col("Plan", 4);
            int max = new[] { iHc, iAp, iCu, iPl }.Max();
            foreach (string linea in lineas.Skip(1))
            {
                string[] c = linea.Split(sep).Select(x => x.Trim().Trim('"')).ToArray();
                if (c.Length <= max || string.IsNullOrWhiteSpace(c[iHc])) continue;
                _pacientes.Add(new PacienteRow { Hc = c[iHc], Apellido = c[iAp], Curso = c[iCu], Plan = c[iPl] });
                n++;
            }
            Log($"CSV cargado: {n} filas ({Path.GetFileName(dlg.FileName)})");
            _sesion = Path.GetFileNameWithoutExtension(dlg.FileName);
        }

        private string _sesion = "manual";

        private void Ejecutar()
        {
            var filas = _pacientes.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Hc)).ToList();
            var dicom = new ExportOptions
            {
                CT = _ct.IsChecked == true, RS = _rs.IsChecked == true, RP = _rp.IsChecked == true,
                RD = _rd.IsChecked == true, Anonimizar = _anon.IsChecked == true
            };
            bool metricas = _metricas.IsChecked == true;
            bool metadata = _metadata.IsChecked == true;
            string error = null;
            if (filas.Count == 0) error = "No hay pacientes.";
            else if (filas.Any(p => string.IsNullOrWhiteSpace(p.Curso) || string.IsNullOrWhiteSpace(p.Plan))) error = "Todas las filas necesitan Curso y Plan.";
            else if (!metricas && !metadata && _objetivos.IsChecked != true && !dicom.Any) error = "Elija qué extraer.";
            else if (metricas && !File.Exists(_constraints.Text)) error = "Elija un archivo de constraints válido.";
            else if (metadata && _sitio.SelectedItem == null) error = "Elija el sitio de la sesión (CyC, Pelvis o Prostata) para la metadata.";
            else if (string.IsNullOrWhiteSpace(_usuario.Text)) error = "Falta el usuario de Eclipse.";
            if (error != null) { MessageBox.Show(error, "DicomExtract"); return; }

            var o = new RunOptions
            {
                Pacientes = filas, Usuario = _usuario.Text.Trim(), Password = _password.Password,
                ConstraintsPath = _constraints.Text, CarpetaSalida = _salida.Text.Trim(),
                NombreSesion = $"{System.DateTime.Now:yyyyMMdd_HHmm}_{_sesion}",
                Metricas = metricas, Metadata = metadata, Sitio = _sitio.SelectedItem as string, Objetivos = _objetivos.IsChecked == true, Dicom = dicom,
                Destinos = new ExportDestinos { CT = _dCt.Text.Trim(), RS = _dRs.Text.Trim(), RP = _dRp.Text.Trim(), RD = _dRd.Text.Trim() },
                Cancelar = () => _cancelar
            };

            _cancelar = false; _run.IsEnabled = false; _cancel.IsEnabled = true;
            // ESAPI: hilo STA dedicado; la UI no se bloquea.
            var t = new Thread(() =>
            {
                try
                {
                    string sesion = Extractor.Run(_net, o, Log);
                    Log($"\n=== Fin. Salida en: {sesion} ===");
                }
                catch (Exception ex) { Log("FATAL ERROR: " + ex.Message); }
                finally { Dispatcher.BeginInvoke(new Action(() => { _run.IsEnabled = true; _cancel.IsEnabled = false; })); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Thread-safe: puede llamarse desde el hilo de extracción.</summary>
        private void Log(string s)
        {
            Dispatcher.BeginInvoke(new Action(() => { _log.AppendText(s + Environment.NewLine); _log.ScrollToEnd(); }));
        }
    }
}
