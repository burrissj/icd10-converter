using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ICD10Converter.Core;
using Microsoft.Win32;

namespace ICD10Converter;

public sealed record HistoryItem(string Label, string Path)
{
    public override string ToString() => Label;
}

public partial class MainWindow : Window
{
    const string HistoryPlaceholder = "Saved history...";

    AppSettings settings = AppSettings.Load();
    readonly ObservableCollection<DownloadSource> sources = new();
    readonly ObservableCollection<FdaEndpoint> fdaEndpoints = new();
    CancellationTokenSource? fdaCts;
    string? lastReportPath;
    ValidationReport? shownReport;
    ICollectionView? diffView;
    DiffResult? lastDiff;
    bool busy;
    bool refreshingHistory;

    public MainWindow()
    {
        InitializeComponent();
        dgSources.ItemsSource = sources;
        dgFda.ItemsSource = fdaEndpoints;
        colFdaFormat.ItemsSource = new[] { "ndc", "drugsfda" };
        ApplySettingsToUi();
        RefreshHistory();
        Log("ICD-10 Converter ready. Select a codes file (or download one from CMS) to begin.");
    }

    // ------------------------------------------------------------------ helpers

    void Log(string message)
    {
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        txtLog.ScrollToEnd();
    }

    void SetBusy(bool value, string status = "")
    {
        busy = value;
        btnConvert.IsEnabled = !value;
        btnDownload.IsEnabled = !value;
        progress.Visibility = value ? Visibility.Visible : Visibility.Hidden;
        progress.IsIndeterminate = value;
        txtStatus.Text = status;
        Mouse.OverrideCursor = value ? Cursors.Wait : null;
    }

    void Warn(string message, string title = "Check your input") =>
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    static void OpenInExplorer(string path, bool selectFile)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", selectFile ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* nothing sensible to do */ }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ convert tab

    void SuggestOutput(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(txtOutput.Text))
            txtOutput.Text = Path.ChangeExtension(inputPath, ".json");
    }

    void SetInput(string path)
    {
        txtInput.Text = path;
        Log($"Selected input file: {path}");
        SuggestOutput(path);
    }

    void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Select ICD-10 input file" };
        if (dlg.ShowDialog(this) == true) SetInput(dlg.FileName);
    }

    void OnInputDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) SetInput(files[0]);
    }

    void OnBrowseAddenda(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*", Title = "Select addenda file" };
        if (dlg.ShowDialog(this) == true)
        {
            txtAddenda.Text = dlg.FileName;
            Log($"Selected addenda file: {dlg.FileName}");
        }
    }

    void OnClearAddenda(object sender, RoutedEventArgs e)
    {
        txtAddenda.Text = "";
        Log("Cleared addenda file");
    }

    void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Title = "Save output file as", DefaultExt = ".json" };
        if (dlg.ShowDialog(this) == true)
        {
            txtOutput.Text = dlg.FileName;
            Log($"Set output file: {dlg.FileName}");
        }
    }

    void OnSplitToggled(object sender, RoutedEventArgs e)
    {
        if (txtMax != null) txtMax.IsEnabled = chkSplit.IsChecked == true;
    }

    void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string bytes })
        {
            txtMax.Text = bytes;
            chkSplit.IsChecked = true;
        }
    }

    async void OnConvert(object sender, RoutedEventArgs e)
    {
        if (busy) return;

        var input = txtInput.Text.Trim().Trim('"');
        var output = txtOutput.Text.Trim().Trim('"');
        var addenda = txtAddenda.Text.Trim().Trim('"');

        if (input.Length == 0) { Warn("Please select an input file."); return; }
        if (!File.Exists(input)) { Warn($"Input file not found:\n{input}"); return; }
        if (addenda.Length > 0 && !File.Exists(addenda)) { Warn($"Addenda file not found:\n{addenda}"); return; }
        if (output.Length == 0) { Warn("Please select an output file."); return; }

        bool split = chkSplit.IsChecked == true;
        long maxBytes = 0;
        if (split && (!long.TryParse(txtMax.Text.Trim(), out maxBytes) || maxBytes < 10_000))
        {
            Warn("Please enter a valid max file size in bytes (at least 10,000), e.g. 4194304 for 4 MB.");
            return;
        }

        var kind = rbCodes.IsChecked == true ? FileKind.Codes : rbOrder.IsChecked == true ? FileKind.Order : FileKind.Auto;
        var opts = new ConvertOptions(input, addenda.Length > 0 ? addenda : null, output, kind,
            chkPretty.IsChecked == true, split, maxBytes, settings.EffectiveVersion, settings.KeepHistory, settings.WriteReports);

        SetBusy(true, "Converting...");
        Log("=== Starting conversion ===");
        try
        {
            IProgress<string> p = new Progress<string>(Log);
            var result = await Task.Run(() => Pipeline.Run(opts, p.Report));
            Log($"=== Conversion complete in {result.Elapsed.TotalSeconds:0.0} s ===");
            RefreshHistory();

            lastReportPath = result.ReportPath;
            btnOpenReport.IsEnabled = lastReportPath != null;
            ShowReport(result.Report);

            string msg = (result.IsFolder
                ? $"Conversion completed in {result.Elapsed.TotalSeconds:0.0} s.\n\n{result.CodeCount:N0} codes were split into {result.Parts} files."
                : $"Conversion completed in {result.Elapsed.TotalSeconds:0.0} s.\n\n{result.CodeCount:N0} codes saved to:\n{result.OpenPath}")
                + $"\n\nValidation: {result.Report.Headline}"
                + (result.Report.Passed ? "" : "\nThe output has problems. Open the validation report before using it.")
                + "\n\nOpen the folder?";
            if (MessageBox.Show(this, msg, result.Report.Passed ? "Success" : "Completed with problems", MessageBoxButton.YesNo,
                    result.Report.Passed ? MessageBoxImage.Information : MessageBoxImage.Warning) == MessageBoxResult.Yes)
                OpenInExplorer(result.OpenPath, !result.IsFolder);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Conversion failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    void OnClearAll(object sender, RoutedEventArgs e)
    {
        txtInput.Text = txtAddenda.Text = txtOutput.Text = txtLog.Text = "";
        rbAuto.IsChecked = true;
        chkPretty.IsChecked = settings.PrettyPrint;
        chkSplit.IsChecked = settings.SplitEnabled;
        txtMax.Text = settings.MaxSizeBytes.ToString();
        Log("All fields cleared");
    }

    async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (!TryReadSettingsFromUi(out var error)) { Warn(error); tabs.SelectedItem = tabSettings; return; }

        string folder = settings.DownloadFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            var dlg = new OpenFolderDialog { Title = "Select folder to save downloaded ICD-10-CM files" };
            if (dlg.ShowDialog(this) != true) { Log("Download cancelled"); return; }
            folder = dlg.FolderName;
        }

        SetBusy(true, "Downloading...");
        Log($"=== Downloading ICD-10-CM {settings.Year} files ===");
        Log($"Download location: {folder}");
        try
        {
            IProgress<string> logP = new Progress<string>(Log);
            IProgress<string> statusP = new Progress<string>(s => txtStatus.Text = s);
            var snapshot = settings;
            var res = await Task.Run(() => Downloader.RunAsync(snapshot, folder, logP.Report, statusP.Report));
            Log($"=== Download finished: {res.Succeeded} succeeded, {res.Failed} failed ===");

            if (res.Succeeded == 0)
            {
                MessageBox.Show(this, "Nothing could be downloaded. See the log for details, and check the URLs on the Settings tab.",
                    "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Log($"Files extracted to: {res.ExtractPath}");
            string text = res.Failed > 0
                ? $"{res.Succeeded} file(s) downloaded, {res.Failed} failed (see the log).\n\nLoad the codes file now?"
                : "Files downloaded successfully!\n\nLoad the codes file now?";
            if (MessageBox.Show(this, text, "Download complete", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var (codes, addenda) = Downloader.FindFiles(res.ExtractPath);
                if (codes != null)
                {
                    txtInput.Text = codes;
                    Log($"Loaded codes file: {codes}");
                    if (addenda != null)
                    {
                        txtAddenda.Text = addenda;
                        Log($"Loaded addenda file: {addenda}");
                    }
                    txtOutput.Text = Path.Combine(folder, $"icd10cm-{settings.Year}-converted.json");
                }
                else Log("No codes file was found in the downloaded files.");
            }
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Download error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    void OnOpenReport(object sender, RoutedEventArgs e)
    {
        if (lastReportPath != null && File.Exists(lastReportPath))
            Process.Start(new ProcessStartInfo(lastReportPath) { UseShellExecute = true });
        else Warn("The report file isn't there any more. Convert again to regenerate it.");
    }

    // ------------------------------------------------------------------ validate tab

    void ShowReport(ValidationReport report)
    {
        shownReport = report;
        txtValReport.Text = report.ToText();
        txtValHeadline.Text = report.Headline;
        txtValHeadline.Foreground = report.Errors > 0 ? System.Windows.Media.Brushes.Firebrick
            : report.Warnings > 0 ? System.Windows.Media.Brushes.DarkOrange
            : System.Windows.Media.Brushes.SeaGreen;
    }

    void OnValPickFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*", Title = "Select file(s) to validate", Multiselect = true };
        if (dlg.ShowDialog(this) == true) txtValPath.Text = string.Join(";", dlg.FileNames);
    }

    void OnValPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder of generated JSON files" };
        if (dlg.ShowDialog(this) == true) txtValPath.Text = dlg.FolderName;
    }

    async void OnValidate(object sender, RoutedEventArgs e)
    {
        var spec = txtValPath.Text.Trim().Trim('"');
        if (spec.Length == 0) { Warn("Choose the JSON file(s) or folder to validate."); return; }

        btnValidate.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        txtValHeadline.Text = "Validating...";
        txtValHeadline.Foreground = System.Windows.Media.Brushes.Gray;
        try
        {
            var report = await Task.Run(() => Validator.Run(spec));
            ShowReport(report);
        }
        catch (Exception ex)
        {
            txtValHeadline.Text = "Validation could not run.";
            MessageBox.Show(this, ex.Message, "Validate failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnValidate.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
    }

    void OnValSave(object sender, RoutedEventArgs e)
    {
        if (shownReport == null) { Warn("Run a validation first."); return; }
        var dlg = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt", DefaultExt = ".txt", FileName = "validation-report.txt" };
        if (dlg.ShowDialog(this) != true) return;
        try { shownReport.Save(dlg.FileName); OpenInExplorer(dlg.FileName, true); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save report", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ------------------------------------------------------------------ compare tab

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != tabs) return;
        if (tabs.SelectedItem == tabCompare) RefreshHistory();
        else if (tabs.SelectedItem == tabFda) RefreshFdaCombo();
    }

    void RefreshHistory()
    {
        if (cmbOld == null) return;
        refreshingHistory = true;
        var items = new List<object> { HistoryPlaceholder };
        try
        {
            if (Directory.Exists(AppPaths.History))
                foreach (var f in Directory.GetFiles(AppPaths.History, "*.json").OrderByDescending(File.GetLastWriteTime))
                    items.Add(new HistoryItem(Path.GetFileNameWithoutExtension(f), f));
        }
        catch { /* ignore */ }
        cmbOld.ItemsSource = items.ToList();
        cmbNew.ItemsSource = items.ToList();
        cmbOld.SelectedIndex = 0;
        cmbNew.SelectedIndex = 0;
        refreshingHistory = false;
    }

    void OnHistoryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingHistory || sender is not ComboBox cmb || cmb.SelectedItem is not HistoryItem item) return;
        (cmb.Tag as string == "old" ? txtOld : txtNew).Text = item.Path;
        refreshingHistory = true;
        cmb.SelectedIndex = 0;
        refreshingHistory = false;
    }

    TextBox CompareBox(object sender) => (sender as FrameworkElement)?.Tag as string == "old" ? txtOld : txtNew;

    void OnPickFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ICD-10 files (*.json;*.txt)|*.json;*.txt|All files (*.*)|*.*",
            Title = "Select version (select all parts if it was split)",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) CompareBox(sender).Text = string.Join(";", dlg.FileNames);
    }

    void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select a folder of split JSON parts, or an extracted CMS folder" };
        if (dlg.ShowDialog(this) == true) CompareBox(sender).Text = dlg.FolderName;
    }

    void OnCompareDrop(object sender, DragEventArgs e)
    {
        if (sender is TextBox tb && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            tb.Text = string.Join(";", files);
    }

    void OnSwap(object sender, RoutedEventArgs e) => (txtOld.Text, txtNew.Text) = (txtNew.Text, txtOld.Text);

    async void OnCompare(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var oldSpec = txtOld.Text.Trim().Trim('"');
        var newSpec = txtNew.Text.Trim().Trim('"');
        if (oldSpec.Length == 0 || newSpec.Length == 0) { Warn("Please choose both an older and a newer version."); return; }

        btnCompare.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        txtSummary.Text = "Comparing...";
        try
        {
            var sw = Stopwatch.StartNew();
            var tOld = Task.Run(() => CodeSetLoader.Load(oldSpec));
            var tNew = Task.Run(() => CodeSetLoader.Load(newSpec));
            await Task.WhenAll(tOld, tNew);
            var diff = await Task.Run(() => CodeDiff.Compare(tOld.Result, tNew.Result));

            lastDiff = diff;
            diffView = CollectionViewSource.GetDefaultView(diff.Rows);
            diffView.Filter = MatchesFilter;
            dgDiff.ItemsSource = diffView;
            UpdateSummary();
            Log($"Compared {oldSpec} -> {newSpec}: {diff.Added} added, {diff.Removed} removed, {diff.Changed} changed ({sw.Elapsed.TotalSeconds:0.0} s)");
        }
        catch (Exception ex)
        {
            txtSummary.Text = "Comparison failed.";
            MessageBox.Show(this, ex.Message, "Compare failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnCompare.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
    }

    bool MatchesFilter(object o)
    {
        var r = (DiffRow)o;
        bool statusOk = r.Status switch
        {
            "Added" => chkAdded.IsChecked == true,
            "Removed" => chkRemoved.IsChecked == true,
            _ => chkChanged.IsChecked == true,
        };
        if (!statusOk) return false;
        var q = txtSearch.Text.Trim();
        return q.Length == 0
            || r.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.OldDescription.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.NewDescription.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (diffView == null) return;
        diffView.Refresh();
        UpdateSummary();
    }

    void UpdateSummary()
    {
        if (lastDiff == null || diffView == null) return;
        int shown = diffView.Cast<object>().Count();
        txtSummary.Text =
            $"{lastDiff.Added:N0} added   |   {lastDiff.Removed:N0} removed   |   {lastDiff.Changed:N0} changed   |   {lastDiff.Unchanged:N0} unchanged" +
            $"      (older: {lastDiff.OldCount:N0} codes, newer: {lastDiff.NewCount:N0} codes)      showing {shown:N0}";
    }

    void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (diffView == null || lastDiff == null) { Warn("Run a comparison first."); return; }
        var dlg = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv", FileName = "icd10-changes.csv", Title = "Export the rows currently shown" };
        if (dlg.ShowDialog(this) != true) return;

        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder("Status,Code,Older description,Newer description\r\n");
        foreach (DiffRow r in diffView)
            sb.Append(r.Status).Append(',').Append(Q(r.Code)).Append(',').Append(Q(r.OldDescription)).Append(',').Append(Q(r.NewDescription)).Append("\r\n");
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Log($"Exported comparison to {dlg.FileName}");
            OpenInExplorer(dlg.FileName, true);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    // ------------------------------------------------------------------ FDA medications tab

    void FdaLog(string message)
    {
        txtFdaLog.AppendText($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
        txtFdaLog.ScrollToEnd();
    }

    void RefreshFdaCombo()
    {
        if (cmbFdaEndpoint == null) return;
        dgFda.CommitEdit(DataGridEditingUnit.Row, true);
        string wanted = (cmbFdaEndpoint.SelectedItem as FdaEndpoint)?.Name ?? settings.FdaSelectedEndpoint;
        var list = fdaEndpoints.ToList();
        cmbFdaEndpoint.ItemsSource = list;
        cmbFdaEndpoint.SelectedItem = list.FirstOrDefault(f => f.Name == wanted) ?? list.FirstOrDefault();
    }

    bool TryReadFdaEndpoints(out string error)
    {
        error = "";
        dgFda.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var f in fdaEndpoints)
        {
            if (!Uri.TryCreate((f.Url ?? "").Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                error = $"The URL for OpenFDA endpoint \"{f.Name}\" is not a valid http(s) address:\n{f.Url}";
                return false;
            }
        }
        settings.FdaEndpoints = fdaEndpoints
            .Select(f => new FdaEndpoint { Name = f.Name.Trim(), Url = f.Url.Trim(), Format = string.IsNullOrWhiteSpace(f.Format) ? "ndc" : f.Format })
            .ToList();
        return true;
    }

    bool TryReadFdaFields(out string error)
    {
        error = "";
        if (!int.TryParse(txtFdaCount.Text.Trim(), out int count) || count <= 0)
        {
            error = "Enter a valid number of medications (a positive whole number).";
            return false;
        }
        if (!int.TryParse(txtFdaPerFile.Text.Trim(), out int perFile) || perFile <= 0)
        {
            error = "Enter a valid medications-per-file value (a positive whole number).";
            return false;
        }
        var folder = txtFdaFolder.Text.Trim().Trim('"');
        if (folder.Length == 0)
        {
            error = "Please choose an output folder.";
            return false;
        }
        settings.FdaRecordCount = count;
        settings.FdaPerFile = perFile;
        settings.FdaOutputFolder = folder;
        settings.FdaRemoveDuplicates = chkFdaDupes.IsChecked == true;
        return true;
    }

    void OnFdaBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select output folder for JSON files" };
        if (dlg.ShowDialog(this) == true)
        {
            txtFdaFolder.Text = dlg.FolderName;
            FdaLog($"Output path set to: {dlg.FolderName}");
        }
    }

    void OnFdaOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = txtFdaFolder.Text.Trim().Trim('"');
        if (Directory.Exists(folder)) OpenInExplorer(folder, false);
        else Warn("That folder doesn't exist yet. It is created when you fetch.");
    }

    void OnFdaStop(object sender, RoutedEventArgs e)
    {
        fdaCts?.Cancel();
        btnFdaStop.IsEnabled = false;
        FdaLog("Stop requested by user.");
    }

    async void OnFdaFetch(object sender, RoutedEventArgs e)
    {
        if (fdaCts != null) return;
        if (!TryReadFdaEndpoints(out var error) || !TryReadFdaFields(out error)) { Warn(error); return; }
        if (cmbFdaEndpoint.SelectedItem is not FdaEndpoint ep) { Warn("Please choose an API endpoint."); return; }

        settings.FdaSelectedEndpoint = ep.Name;
        try { settings.Save(); } catch { /* remembering the choices is a convenience only */ }

        var opts = new FdaOptions(ep, settings.FdaRecordCount, settings.FdaPerFile,
            settings.FdaRemoveDuplicates, chkFdaDebug.IsChecked == true, settings.FdaOutputFolder, settings.WriteReports);

        btnFdaFetch.IsEnabled = false;
        btnFdaStop.IsEnabled = true;
        fdaProgress.Value = 0;
        fdaCts = new CancellationTokenSource();
        var token = fdaCts.Token;

        IProgress<string> logP = new Progress<string>(FdaLog);
        IProgress<(int Pct, string Msg)> progP = new Progress<(int Pct, string Msg)>(t =>
        {
            fdaProgress.Value = t.Pct;
            txtFdaProgress.Text = t.Msg;
        });

        try
        {
            var r = await Task.Run(() => OpenFda.RunAsync(opts, logP.Report, (p, m) => progP.Report((p, m)), token));
            if (r.Medications == 0)
            {
                txtFdaProgress.Text = "No medications collected";
                MessageBox.Show(this,
                    "No medications collected.\n\nTip: enable Debug mode and run again. The log will show the raw field names the API returns, and you can check the endpoint's response format on the Settings tab.",
                    "Nothing collected", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                if (r.Report != null) ShowReport(r.Report);
                string check = r.Report == null ? "" : $"\n\nValidation: {r.Report.Headline}";
                bool ok = r.Report?.Passed != false;
                if (MessageBox.Show(this,
                        $"{(r.Stopped ? "Stopped early. " : "")}Done!\n\nMedications: {r.Medications:N0}\nFiles created: {r.Files}\nOutput folder: {r.Folder}{check}\n\nOpen the folder?",
                        ok ? "Success" : "Completed with problems", MessageBoxButton.YesNo,
                        ok ? MessageBoxImage.Information : MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    OpenInExplorer(r.Folder, false);
            }
        }
        catch (Exception ex)
        {
            FdaLog("ERROR: " + ex.Message);
            txtFdaProgress.Text = "Failed";
            MessageBox.Show(this, ex.Message, "OpenFDA fetch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            fdaCts?.Dispose();
            fdaCts = null;
            btnFdaFetch.IsEnabled = true;
            btnFdaStop.IsEnabled = false;
        }
    }

    void OnAddFda(object sender, RoutedEventArgs e)
    {
        dgFda.CommitEdit(DataGridEditingUnit.Row, true);
        var f = new FdaEndpoint { Name = "New endpoint", Url = "https://api.fda.gov/drug/", Format = "ndc" };
        fdaEndpoints.Add(f);
        dgFda.SelectedItem = f;
        dgFda.ScrollIntoView(f);
    }

    void OnRemoveFda(object sender, RoutedEventArgs e)
    {
        if (fdaEndpoints.Count <= 1) { Warn("Keep at least one endpoint."); return; }
        if (dgFda.SelectedItem is FdaEndpoint f) fdaEndpoints.Remove(f);
    }

    void OnResetFda(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace the list with the default OpenFDA endpoints?", "Reset endpoints",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        fdaEndpoints.Clear();
        foreach (var f in OpenFda.DefaultEndpoints()) fdaEndpoints.Add(f);
    }

    // ------------------------------------------------------------------ settings tab

    void ApplySettingsToUi()
    {
        txtYear.Text = settings.Year.ToString();
        txtDlFolder.Text = settings.DownloadFolder;
        txtVersion.Text = settings.VersionLabel;
        chkDefPretty.IsChecked = settings.PrettyPrint;
        chkDefSplit.IsChecked = settings.SplitEnabled;
        txtDefMax.Text = settings.MaxSizeBytes.ToString();
        chkKeepHistory.IsChecked = settings.KeepHistory;
        chkWriteReports.IsChecked = settings.WriteReports;

        sources.Clear();
        foreach (var s in settings.Sources)
            sources.Add(new DownloadSource { Enabled = s.Enabled, Name = s.Name, Url = s.Url, FileName = s.FileName });

        chkPretty.IsChecked = settings.PrettyPrint;
        chkSplit.IsChecked = settings.SplitEnabled;
        txtMax.Text = settings.MaxSizeBytes.ToString();
        txtMax.IsEnabled = settings.SplitEnabled;

        fdaEndpoints.Clear();
        foreach (var f in settings.FdaEndpoints)
            fdaEndpoints.Add(new FdaEndpoint { Name = f.Name, Url = f.Url, Format = f.Format });
        txtFdaCount.Text = settings.FdaRecordCount.ToString();
        txtFdaPerFile.Text = settings.FdaPerFile.ToString();
        txtFdaFolder.Text = settings.FdaOutputFolder;
        chkFdaDupes.IsChecked = settings.FdaRemoveDuplicates;
        RefreshFdaCombo();
    }

    /// <summary>Validates the Settings tab and copies it into <see cref="settings"/> (without saving to disk).</summary>
    bool TryReadSettingsFromUi(out string error)
    {
        error = "";
        dgSources.CommitEdit(DataGridEditingUnit.Row, true);

        if (!int.TryParse(txtYear.Text.Trim(), out int year) || year < 2000 || year > 2100)
        {
            error = "Release year must be a number such as 2026.";
            return false;
        }
        if (!long.TryParse(txtDefMax.Text.Trim(), out long max) || max < 10_000)
        {
            error = "Default max file size must be a number of bytes (at least 10,000).";
            return false;
        }
        foreach (var s in sources.Where(s => s.Enabled))
        {
            var url = s.Url.Replace("{year}", year.ToString()).Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                error = $"The URL for \"{s.Name}\" is not a valid http(s) address:\n{url}";
                return false;
            }
        }

        var folder = txtDlFolder.Text.Trim().Trim('"');
        settings.Year = year;
        settings.DownloadFolder = folder;
        settings.VersionLabel = txtVersion.Text.Trim();
        settings.PrettyPrint = chkDefPretty.IsChecked == true;
        settings.SplitEnabled = chkDefSplit.IsChecked == true;
        settings.MaxSizeBytes = max;
        settings.KeepHistory = chkKeepHistory.IsChecked == true;
        settings.WriteReports = chkWriteReports.IsChecked == true;
        settings.Sources = sources
            .Select(s => new DownloadSource { Enabled = s.Enabled, Name = s.Name.Trim(), Url = s.Url.Trim(), FileName = s.FileName.Trim() })
            .ToList();
        return true;
    }

    void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        if (!TryReadSettingsFromUi(out var error) || !TryReadFdaEndpoints(out error) || !TryReadFdaFields(out error))
        {
            Warn(error);
            return;
        }
        RefreshFdaCombo();
        try
        {
            settings.Save();
            chkPretty.IsChecked = settings.PrettyPrint;
            chkSplit.IsChecked = settings.SplitEnabled;
            txtMax.Text = settings.MaxSizeBytes.ToString();
            txtSettingsStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
            Log("Settings saved");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void OnBrowseDlFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Default folder for CMS downloads" };
        if (dlg.ShowDialog(this) == true) txtDlFolder.Text = dlg.FolderName;
    }

    void OnClearDlFolder(object sender, RoutedEventArgs e) => txtDlFolder.Text = "";

    void OnAddSource(object sender, RoutedEventArgs e)
    {
        dgSources.CommitEdit(DataGridEditingUnit.Row, true);
        var s = new DownloadSource { Name = "New source", Url = "https://", FileName = "" };
        sources.Add(s);
        dgSources.SelectedItem = s;
        dgSources.ScrollIntoView(s);
    }

    void OnRemoveSource(object sender, RoutedEventArgs e)
    {
        if (dgSources.SelectedItem is DownloadSource s) sources.Remove(s);
    }

    void OnResetSources(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Replace the list with the default CMS sources?", "Reset sources",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        sources.Clear();
        foreach (var s in AppSettings.DefaultSources()) sources.Add(s);
    }

    void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.History);
        OpenInExplorer(AppPaths.History, false);
    }

    void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        OpenInExplorer(AppPaths.Root, false);
    }
}
