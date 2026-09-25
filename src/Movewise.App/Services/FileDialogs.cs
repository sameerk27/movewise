using Microsoft.Win32;

namespace Movewise.App.Services;

/// <summary>Windows open and save dialogs. Blazor event handlers run on the window's UI thread, so these can be shown directly.</summary>
public static class FileDialogs
{
    const string CsvFilter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";

    public static string? SaveCsv(string title, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = CsvFilter, DefaultExt = ".csv", AddExtension = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? SaveZip(string title, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = "Zip files (*.zip)|*.zip", DefaultExt = ".zip", AddExtension = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? OpenCsv(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = CsvFilter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
