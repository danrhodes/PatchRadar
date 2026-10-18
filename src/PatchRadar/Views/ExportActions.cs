using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using PatchRadar.Export;
using PatchRadar.Infra;
using PatchRadar.Scan;

namespace PatchRadar.Views;

/// <summary>UI glue for exporting rows: Save As dialog and clipboard.</summary>
public static class ExportActions
{
    public static void SaveAs(ExportFormat format, IReadOnlyList<UpdateRow> rows, Window? owner, ILog log)
    {
        var ext = Exporters.Extension(format);
        var dlg = new SaveFileDialog
        {
            FileName = $"PatchRadar-{DateTime.Now:yyyyMMdd-HHmm}{ext}",
            Filter = $"{format} (*{ext})|*{ext}|All files|*.*",
            DefaultExt = ext,
            OverwritePrompt = true,
        };
        var ok = owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
        if (ok != true) return;
        try
        {
            // UTF-8 with BOM so Excel opens the CSV with the right encoding.
            File.WriteAllText(dlg.FileName, Exporters.Render(format, rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            log.Info($"Exported {rows.Count} updates to {dlg.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("Export failed", ex);
            MessageBox.Show("Could not write the file: " + ex.Message, "PatchRadar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void CopyMarkdown(IReadOnlyList<UpdateRow> rows, ILog log)
    {
        try { Clipboard.SetText(MarkdownExporter.Render(rows)); }
        catch (Exception ex) { log.Warn("Clipboard failed: " + ex.Message); }
    }
}
