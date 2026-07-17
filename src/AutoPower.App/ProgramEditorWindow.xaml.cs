using System.Windows;
using System.IO;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using AutoPower.Core;
using MessageBox = AutoPower.App.LocalizedMessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AutoPower.App;

public partial class ProgramEditorWindow : Window
{
    public ProgramEditorWindow() => InitializeComponent();
    public ProgramDraft? Result { get; private set; }

    private void BrowseProgram_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = AppText.T("프로그램 (*.exe)|*.exe"), CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            PathInput.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(WorkingDirectoryInput.Text))
            {
                WorkingDirectoryInput.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            }
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

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var path = PathInput.Text.Trim();
        var parsedDelay = int.TryParse(DelayInput.Text.Trim(), out var delay);
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !parsedDelay || delay < 0)
        {
            MessageBox.Show("존재하는 EXE 전체 경로와 0 이상의 분 값을 입력하세요.", "후속 프로그램", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var working = WorkingDirectoryInput.Text.Trim();
        if (working.Length > 0 && !Directory.Exists(working))
        {
            MessageBox.Show("작업 폴더가 존재하지 않습니다.", "후속 프로그램", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new ProgramDraft(Guid.NewGuid(), path, delay, ArgumentsInput.Text, working, RunElevatedCheck.IsChecked == true);
        DialogResult = true;
    }
}
