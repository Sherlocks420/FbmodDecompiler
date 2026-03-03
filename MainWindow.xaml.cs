using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using Frosty.Core.Mod;

namespace FbmodDecompiler
{
    public partial class MainWindow : Window
    {
        private string SettingsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gui_settings.txt");

        private string _lastGeneratedProjectPath = string.Empty;

        private const string BlockedReverseMessage = "This mod is illegal to reverse in this tool.";
        private enum UiPhase { Idle, Initializing, ReadingMod, ExtractingEbx, WritingOutput, Done, Error }

        private void ShowBlockedReverse(string message)
        {
            try
            {
                MessageBox.Show(this, message, "Not allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { }
        }

        private UiPhase _phase = UiPhase.Idle;
        public MainWindow()
        {
            InitializeComponent();
            OutputTypeCombo.SelectedIndex = 0;

            try { WriterCombo.SelectedIndex = 0; } catch { }

            try { ChunkLayoutCombo.SelectedIndex = 1; } catch { }

            try { ViewProgressRadio.IsChecked = true; } catch { }

            try
            {
                if (File.Exists(SettingsPath))
                {
                    var lines = File.ReadAllLines(SettingsPath);
                    if (lines.Length > 0) GamePathBox.Text = lines[0];
                    if (lines.Length > 1) OutputBox.Text = lines[1];
                    if (lines.Length > 2) FrostyDirBox.Text = lines[2];
                }
            }
            catch { }

            try
            {
                string pt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "path.txt");
                if (File.Exists(pt))
                {
                    var gp = File.ReadAllText(pt).Trim();
                    if (!string.IsNullOrWhiteSpace(gp) && string.IsNullOrWhiteSpace(GamePathBox.Text))
                        GamePathBox.Text = gp;
                }
            }
            catch { }

            try
            {
                if (string.IsNullOrWhiteSpace(GamePathBox.Text))
                {
                    var auto = GameLocator.TryFindSwbf2InstallDir();
                    if (!string.IsNullOrWhiteSpace(auto))
                        GamePathBox.Text = auto;
                }
            }
            catch { }

            try
            {
                if (MuteButtonIcon != null) MuteButtonIcon.Text = AppState.Audio.GetMuteIcon();
                AppState.Audio.MutedChanged += OnMutedChanged;
            }
            catch { }

            Closed += (_, __) => { try { AppState.Audio.MutedChanged -= OnMutedChanged; } catch { } };

        }

        private void ActivityView_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                bool showDetails = ViewDetailsRadio.IsChecked == true;
                DetailsBox.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
                StatusList.Visibility = showDetails ? Visibility.Collapsed : Visibility.Visible;

                if (showDetails)
                {
                    DetailsBox.Focus();
                    DetailsBox.CaretIndex = DetailsBox.Text?.Length ?? 0;
                    DetailsBox.ScrollToEnd();
                }
            }
            catch { }
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppState.Audio.ToggleMute();
                if (MuteButtonIcon != null)
                    MuteButtonIcon.Text = AppState.Audio.GetMuteIcon();
            }
            catch { }
        }

        private void OnMutedChanged(bool muted)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    try { if (MuteButtonIcon != null) MuteButtonIcon.Text = AppState.Audio.GetMuteIcon(); } catch { }
                });
            }
            catch { }
        }

        private void BrowseCompareProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Frosty Project (*.fbproject)|*.fbproject|All files (*.*)|*.*",
                Title = "Select the REAL .fbproject to compare against"
            };

            if (dlg.ShowDialog() == true)
                CompareProjectTextBox.Text = dlg.FileName;
        }

        private async void CompareProjects_Click(object sender, RoutedEventArgs e)
        {

            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;
            var prevCursor = Cursor;
            try { Cursor = System.Windows.Input.Cursors.Wait; } catch { }

            try
            {
                string real = CompareProjectTextBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(real) || !File.Exists(real))
                {
                    MessageBox.Show(this, "Please select a valid REAL .fbproject in the Compare field.", "Compare Projects", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string gen = _lastGeneratedProjectPath;
                if (string.IsNullOrWhiteSpace(gen) || !File.Exists(gen))
                    gen = OutputBox.Text?.Trim();

                if (string.IsNullOrWhiteSpace(gen) || !File.Exists(gen))
                {
                    MessageBox.Show(this, "Please generate a .fbproject first (or set Output to a valid generated .fbproject path).", "Compare Projects", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                AddStatus("Comparing projects");

                string outPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(gen) ?? "", "diff_report.txt");

                string report = await Task.Run(() =>
                {
                    string r = ProjectCompare.GenerateReport(real, gen);
                    System.IO.File.WriteAllText(outPath, r, Encoding.UTF8);
                    return r;
                }).ConfigureAwait(true);

                DetailsBox.Text = report;
                try { ViewDetailsRadio.IsChecked = true; } catch { }
                AddStatus("Compare complete");
            }
            catch (Exception ex)
            {
                AppendDetails("ERROR: " + ex);
                AddStatus("Error (see Details)");
                MessageBox.Show(this, "Compare failed. Check Details tab for the error.", "Compare Projects", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            finally
            {
                try { Cursor = prevCursor; } catch { }
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;

            var prevCursor = Cursor;
            try { Cursor = System.Windows.Input.Cursors.Wait; } catch { }

            try
            {
                string input = InputBox.Text?.Trim();
                string game = GamePathBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                {
                    MessageBox.Show(this, "Please select a valid .fbmod first.", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
                {
                    MessageBox.Show(this, "Please set a valid game folder first.", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                ClearUi();
                AddStatus("Running diagnostics");

                ReverseBtn.IsEnabled = false;
                Progress.IsIndeterminate = true;
                Progress.Value = 0;

                var oldOut = Console.Out;
                var oldErr = Console.Error;
                var uiWriter = new UiTextWriter(HandleConsoleLine);

                try
                {
                    Console.SetOut(uiWriter);
                    Console.SetError(uiWriter);

                    string optWriter = (WriterCombo.SelectedIndex == 1) ? "frosty" : "custom";
                    string optChunkLayout = null;
                    try { optChunkLayout = ((ComboBoxItem)ChunkLayoutCombo.SelectedItem)?.Content?.ToString()?.Trim(); } catch { optChunkLayout = null; }

                    var opt = new Program.Options
                    {
                        InputFbmod = input,
                        Output = OutputBox.Text?.Trim() ?? "",
                        GamePath = game,
                        OutputType = "fbproject",
                        EbxOnly = (EbxOnlyCheck.IsChecked == true),
                        Verbose = true,
                        KeepResChunk = (KeepResChunkCheck.IsChecked == true),
                        DisableLinked = !(EnableLinkedCheck.IsChecked == true),
                        Writer = optWriter,
                        ChunkLayout = optChunkLayout,
                        FrostyDirOverride = FrostyDirBox.Text?.Trim(),
                        FastInit = false
                    };

                    await Task.Run(() => Program.RunDiagnostics(opt)).ConfigureAwait(true);
                    AddStatus("Diagnostics complete");
                    try { ViewDetailsRadio.IsChecked = true; } catch { }
                }
                finally
                {
                    Console.SetOut(oldOut);
                    Console.SetError(oldErr);
                }
            }
            catch (Exception ex)
            {
                AppendDetails("ERROR: " + ex);
                AddStatus("Error (see Details)");
                MessageBox.Show(this, "Diagnostics failed. Check Details tab.", "Diagnostics", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { Cursor = prevCursor; } catch { }
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                ReverseBtn.IsEnabled = true;
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private void AddStatus(string line)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                StatusList.Items.Add(line);
                StatusList.ScrollIntoView(StatusList.Items[StatusList.Items.Count - 1]);
            });
        }

        private void AppendDetails(string line)
        {
            Dispatcher.Invoke(() =>
            {
                if (line == null) return;
                DetailsBox.AppendText(line + Environment.NewLine);
                DetailsBox.ScrollToEnd();
            });
        }

        private void SetPhase(UiPhase phase)
        {
            if (_phase == phase) return;
            _phase = phase;

            switch (phase)
            {
                case UiPhase.Initializing:
                    AddStatus("Initializing Frosty SDK");
                    break;
                case UiPhase.ReadingMod:
                    AddStatus("Reading mod");
                    break;
                case UiPhase.ExtractingEbx:
                    AddStatus("Extracting EBX");
                    break;
                case UiPhase.WritingOutput:
                    AddStatus("Writing output");
                    break;
                case UiPhase.Done:
                    AddStatus("Done");
                    break;
            }
        }

        private void ClearUi()
        {
            try
            {
                StatusList.Items.Clear();
                DetailsBox.Clear();
                _phase = UiPhase.Idle;
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines(SettingsPath, new[] { GamePathBox.Text ?? "", OutputBox.Text ?? "", FrostyDirBox.Text ?? "" });
            }
            catch { }
        }

        private void BrowseFbmod_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Filter = "Frosty Mod (*.fbmod)|*.fbmod|All files (*.*)|*.*",
                Title = "Select .fbmod"
            };
            if (ofd.ShowDialog(this) == true)
                InputBox.Text = ofd.FileName;
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {

            if (OutputTypeCombo.SelectedIndex == 0)
            {
                var sfd = new SaveFileDialog
                {
                    Filter = "Frosty Project (*.fbproject)|*.fbproject|All files (*.*)|*.*",
                    Title = "Select output .fbproject"
                };
                if (sfd.ShowDialog(this) == true)
                    OutputBox.Text = sfd.FileName;
            }
            else
            {

                using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
                {
                    fbd.Description = "Select output folder for EBX dump";
                    if (Directory.Exists(OutputBox.Text)) fbd.SelectedPath = OutputBox.Text;
                    if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        OutputBox.Text = fbd.SelectedPath;
                }
            }
        }

        private void BrowseGame_Click(object sender, RoutedEventArgs e)
        {
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select STAR WARS Battlefront II install folder";
                if (Directory.Exists(GamePathBox.Text)) fbd.SelectedPath = GamePathBox.Text;
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    GamePathBox.Text = fbd.SelectedPath;
            }
        }

        private void BrowseFrosty_Click(object sender, RoutedEventArgs e)
        {
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                fbd.Description = "Select Frosty Editor/Mod Manager folder (optional)";
                if (Directory.Exists(FrostyDirBox.Text)) fbd.SelectedPath = FrostyDirBox.Text;
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    FrostyDirBox.Text = fbd.SelectedPath;
            }
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string p = OutputBox.Text;
                if (string.IsNullOrWhiteSpace(p)) return;

                string dir = p;
                if (File.Exists(p)) dir = Path.GetDirectoryName(p);
                if (!Directory.Exists(dir)) return;

                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch { }
        }

        private async void Inspect_Click(object sender, RoutedEventArgs e)
        {

            var inspectBtn = sender as Button;
            string candidate = OutputBox.Text?.Trim();

            var ofd = new OpenFileDialog
            {
                Filter = "Frosty Project (*.fbproject)|*.fbproject|Frosty Mod (*.fbmod)|*.fbmod|All files (*.*)|*.*",
                Title = "Select file to inspect"
            };

            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.EndsWith(".fbproject", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(candidate);
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            ofd.InitialDirectory = dir;
                        ofd.FileName = Path.GetFileName(candidate);
                    }
                    catch { }
                }

                if (ofd.ShowDialog(this) != true)
                    return;

                string targetPath = ofd.FileName;
                if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
                {
                    MessageBox.Show(this, "Please select a valid file.", "Missing file", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                bool isProject = targetPath.EndsWith(".fbproject", StringComparison.OrdinalIgnoreCase);
                bool isMod = targetPath.EndsWith(".fbmod", StringComparison.OrdinalIgnoreCase);

                if (isMod)
                {
                    string game = GamePathBox.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
                    {
                        MessageBox.Show(this, "Please set the game folder first (needed to initialize the Frosty profile).", "Missing game path", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                ClearUi();
                AddStatus(isProject ? "Inspecting project" : "Inspecting mod");

                ReverseBtn.IsEnabled = false;
                if (inspectBtn != null) inspectBtn.IsEnabled = false;
                Progress.IsIndeterminate = true;
                Progress.Value = 0;

                var oldOut = Console.Out;
                var oldErr = Console.Error;

                var uiWriter = new UiTextWriter(HandleConsoleLine);

                try
                {
                    Console.SetOut(uiWriter);
                    Console.SetError(uiWriter);

                    await Task.Run(() =>
                    {
                        if (isProject)
                        {
                            Program.InspectProject(targetPath);
                        }
                        else
                        {
                            string frostyDir = FrostyDirBox.Text?.Trim();
                            bool verbose = (VerboseCheck.IsChecked == true);
                            Program.InspectMod(targetPath, GamePathBox.Text.Trim(), frostyDir, verbose);
                        }
                    });
                    AddStatus("Inspect complete");
                }
                catch (Exception ex)
                {
                    AppendDetails("ERROR: " + ex);
                    AddStatus("Error (see Details)");
                }
                finally
                {
                    Console.SetOut(oldOut);
                    Console.SetError(oldErr);
                }
            }
            finally
            {
                Progress.IsIndeterminate = false;
                Progress.Value = 100;
                ReverseBtn.IsEnabled = true;
                if (inspectBtn != null) inspectBtn.IsEnabled = true;
            }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ClearUi();
            }
            catch { }
        }

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = DetailsBox.Text;
                if (string.IsNullOrWhiteSpace(text))
                    text = "(no log yet)";

                Clipboard.SetText(text);
            }
            catch { }
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {

            try
            {
                if (IsInInteractiveControl(e.OriginalSource as DependencyObject))
                    return;

                DragMove();
            }
            catch { }
        }

        private static bool IsInInteractiveControl(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ButtonBase || d is TextBoxBase || d is ComboBox || d is ComboBoxItem || d is ToggleButton || d is ListBoxItem || d is ScrollBar)
                    return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void OutputTypeCombo_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                var cb = sender as ComboBox;
                if (cb == null) return;

                if (IsInPopupOrScrollBar(e.OriginalSource as DependencyObject))
                    return;

                cb.IsDropDownOpen = true;
                e.Handled = true;
            }
            catch { }
        }

        private static bool IsInPopupOrScrollBar(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ScrollBar || d is Popup)
                    return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            string input = InputBox.Text?.Trim();
            string output = OutputBox.Text?.Trim();
            string game = GamePathBox.Text?.Trim();

            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            {
                MessageBox.Show(this, "Please select a valid .fbmod file.", "Missing input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(output))
            {
                MessageBox.Show(this, "Please select an output path.", "Missing output", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
            {
                try
                {
                    var auto = GameLocator.TryFindSwbf2InstallDir();
                    if (!string.IsNullOrWhiteSpace(auto) && Directory.Exists(auto))
                    {
                        game = auto;
                        Dispatcher.Invoke(() => GamePathBox.Text = auto);
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(game))
                {
                    MessageBox.Show(this, "Game install folder not found automatically. Please browse to your SWBF2 install folder.", "Game path", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            try
            {
                if (FrostyModReaderLite.TryReadAuthor(input, out var author))
                {
					if (AuthorProtection.IsBlocked(author))
                    {
                        ShowBlockedReverse(BlockedReverseMessage);
                        return;
                    }
                }
            }
            catch
            {

            }

SaveSettings();

            ReverseBtn.IsEnabled = false;
            Progress.IsIndeterminate = false;
            Progress.Minimum = 0;
            Progress.Maximum = 100;
            Progress.Value = 0;

            ClearUi();

            string outputType = (OutputTypeCombo.SelectedIndex == 1) ? "dump" : "fbproject";

            bool optEbxOnly = (EbxOnlyCheck.IsChecked == true);
            bool optVerbose = (VerboseCheck.IsChecked == true);
            bool optEnableLinked = (EnableLinkedCheck.IsChecked == true);
            bool optKeepResChunk = (KeepResChunkCheck.IsChecked == true);
            string optWriter = (WriterCombo.SelectedIndex == 1) ? "frosty" : "custom";
            string optChunkLayout = null;
            try { optChunkLayout = ((ComboBoxItem)ChunkLayoutCombo.SelectedItem)?.Content?.ToString()?.Trim(); } catch { optChunkLayout = null; }
            string optFrostyDir = FrostyDirBox.Text?.Trim();
            string optCompareProject = CompareProjectTextBox.Text?.Trim();

            string outputPath = output;

            AddStatus($"Input: {System.IO.Path.GetFileName(input)}");
            AddStatus($"Output: {outputPath}");
            AddStatus($"Mode: {(outputType == "fbproject" ? "Generate .fbproject" : "Dump files")}" + (optEbxOnly ? " (EBX-only)" : ""));
            AddStatus($"Writer: {optWriter}" + (!string.IsNullOrWhiteSpace(optChunkLayout) ? $" | Chunk layout: {optChunkLayout}" : ""));
            AddStatus($"Linked assets: {(optEnableLinked ? "enabled" : "disabled")} | Keep RES/CHUNK: {(optKeepResChunk ? "yes" : "no")} | Verbose: {(optVerbose ? "yes" : "no")}");

            var writer = new UiTextWriter(HandleConsoleLine);
            Console.SetOut(writer);
            Console.SetError(writer);
            using (var simCts = new CancellationTokenSource())
            {
                var simTask = SimulateProgress(simCts.Token);

                await Task.Run(() =>
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();

                        if (outputType == "fbproject" && Directory.Exists(outputPath))
                        {
                            outputPath = Path.Combine(outputPath, Path.GetFileNameWithoutExtension(input) + ".fbproject");
                        }

                        AddStatus($"Output: {outputPath}");

                        Thread.Sleep(600);
                        Thread.Sleep(800);
                        Thread.Sleep(900);

                        var opt = new Program.Options
                        {
                            InputFbmod = input,
                            Output = outputPath,
                            GamePath = game,
                            OutputType = outputType,
                            EbxOnly = optEbxOnly,
                            Verbose = optVerbose,
                            KeepResChunk = optKeepResChunk,
                            DisableLinked = !optEnableLinked,
                            Writer = optWriter,
                            ChunkLayout = optChunkLayout,
                            FrostyDirOverride = optFrostyDir
                        };

                        Program.Run(opt);

                        sw.Stop();
                        int remainingMs = 5000 - (int)sw.ElapsedMilliseconds;
                        if (remainingMs > 0)
                        {

                            int step1 = Math.Min(2500, remainingMs);
                            Thread.Sleep(step1);
                            remainingMs -= step1;

                            int step2 = Math.Min(3000, remainingMs);
                            Thread.Sleep(step2);
                            remainingMs -= step2;

                            if (remainingMs > 0)
                            {
                                Thread.Sleep(remainingMs);
                            }
                        }

                        SetPhase(UiPhase.Done);
                    }
                    catch (Exception ex)
                    {
                        _phase = UiPhase.Error;
                        AddStatus("Error (see Details)");
                        AppendDetails("ERROR: " + ex);
                    }
                });

                simCts.Cancel();
                try { await simTask; } catch { }

                await SmoothCompleteProgressAsync();

                Dispatcher.Invoke(() =>
                {
                    _lastGeneratedProjectPath = outputPath;
                    ReverseBtn.IsEnabled = true;
                });
            }
        }

        private Task SmoothCompleteProgressAsync()
        {

            return Dispatcher.InvokeAsync(async () =>
            {
                double start = Progress.Value;
                const double end = 100.0;
                const int durationMs = 650;
                const int stepMs = 25;
                int steps = Math.Max(1, durationMs / stepMs);

                for (int i = 1; i <= steps; i++)
                {
                    double t = (double)i / steps;
                    Progress.Value = start + (end - start) * t;
                    await Task.Delay(stepMs);
                }

                Progress.Value = 100;
            }).Task;
        }

        private Task SimulateProgress(CancellationToken token)
        {

            return Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                const double cap = 90.0;
                const int rampMs = 5000;

                bool s1 = false, s2 = false, s3 = false, s4 = false;

                while (!token.IsCancellationRequested)
                {
                    double t = Math.Min(1.0, sw.ElapsedMilliseconds / (double)rampMs);
                    double v = cap * t;

                    Dispatcher.Invoke(() =>
                    {

                        if (Progress.Value < v)
                            Progress.Value = v;
                    });

                    await Task.Delay(30);
                }
            });
        }

        private void HandleConsoleLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            AppendDetails(line);

            if (line.IndexOf("Initializing Frosty SDK", StringComparison.OrdinalIgnoreCase) >= 0)
                SetPhase(UiPhase.Initializing);
            else if (line.IndexOf("Loading mod file", StringComparison.OrdinalIgnoreCase) >= 0)
                SetPhase(UiPhase.ReadingMod);
            else if (line.IndexOf("Processing resources", StringComparison.OrdinalIgnoreCase) >= 0)
                SetPhase(UiPhase.ExtractingEbx);
            else if (line.IndexOf("Writing", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("fbproject", StringComparison.OrdinalIgnoreCase) >= 0)
                SetPhase(UiPhase.WritingOutput);
            else if (line.IndexOf("=== Success", StringComparison.OrdinalIgnoreCase) >= 0)
                SetPhase(UiPhase.Done);
            else if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                _phase = UiPhase.Error;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            foreach (var f in files)
            {
                if (f.EndsWith(".fbmod", StringComparison.OrdinalIgnoreCase))
                {
                    InputBox.Text = f;
                    return;
                }
            }
        }
    }

    internal sealed class UiTextWriter : TextWriter
    {
        private readonly Action<string> _writeLine;
        private readonly StringBuilder _buffer = new StringBuilder();

        public UiTextWriter(Action<string> writeLine) => _writeLine = writeLine;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = _buffer.ToString().TrimEnd('\r');
                _buffer.Clear();
                _writeLine(line);
            }
            else
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string value)
        {
            if (value == null) return;
            foreach (char c in value) Write(c);
        }
    }
}