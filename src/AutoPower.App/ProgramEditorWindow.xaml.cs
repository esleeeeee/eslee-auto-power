using System.IO;
using System.Windows;
using AutoPower.App.Controls;
using AutoPower.App.Services;
using AutoPower.Core;
using Forms = System.Windows.Forms;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AutoPower.App;

public partial class ProgramEditorWindow : ThemedWindow
{
    private readonly Guid _id;

    public ProgramEditorWindow(ProgramDraft? existing = null)
    {
        InitializeComponent();
        _id = existing?.Id ?? Guid.NewGuid();
        if (existing is not null)
        {
            Title = AppText.T("후속 프로그램 수정");
            SaveButton.Content = AppText.T("저장");
            PathInput.Text = existing.ExecutablePath;
            DelayInput.Text = existing.DelayMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ArgumentsInput.Text = existing.Arguments ?? string.Empty;
            WorkingDirectoryInput.Text = existing.WorkingDirectory ?? string.Empty;
            RunElevatedCheck.IsChecked = existing.RunElevated;
            AdvancedOptions.IsExpanded = existing.RunElevated ||
                                         !string.IsNullOrWhiteSpace(existing.Arguments) ||
                                         !string.IsNullOrWhiteSpace(existing.WorkingDirectory);
        }
    }

    public ProgramDraft? Result { get; private set; }

    private void BrowseProgram_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = AppText.T("프로그램 (*.exe)|*.exe"), CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            SetProgramPath(dialog.FileName);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = AppText.T("작업 폴더 선택"), UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            WorkingDirectoryInput.Text = dialog.SelectedPath;
        }
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = TryGetDroppedExecutable(e) is null ? System.Windows.DragDropEffects.None : System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (TryGetDroppedExecutable(e) is { } path)
        {
            SetProgramPath(path);
        }
    }

    private static string? TryGetDroppedExecutable(System.Windows.DragEventArgs e) =>
        e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: 1 } files &&
        string.Equals(Path.GetExtension(files[0]), ".exe", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;

    private void SetProgramPath(string path)
    {
        PathInput.Text = path;
        if (string.IsNullOrWhiteSpace(WorkingDirectoryInput.Text))
        {
            WorkingDirectoryInput.Text = Path.GetDirectoryName(path) ?? string.Empty;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var path = PathInput.Text.Trim();
        var parsedDelay = int.TryParse(DelayInput.Text.Trim(), out var delay);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !parsedDelay || delay < 0)
        {
            AppDialog.Warning(this, "존재하는 EXE 전체 경로와 0 이상의 분 값을 입력하세요.", "후속 프로그램");
            return;
        }

        var working = WorkingDirectoryInput.Text.Trim();
        if (working.Length > 0 && !Directory.Exists(working))
        {
            AppDialog.Warning(this, "작업 폴더가 존재하지 않습니다.", "후속 프로그램");
            return;
        }

        Result = new ProgramDraft(_id, path, delay, ArgumentsInput.Text, working, RunElevatedCheck.IsChecked == true);
        DialogResult = true;
    }
}
