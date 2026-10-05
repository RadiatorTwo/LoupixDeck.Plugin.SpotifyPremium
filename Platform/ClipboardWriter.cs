using System.Diagnostics;

namespace LoupixDeck.Plugin.SpotifyPremium.Platform;

/// <summary>
/// Puts plain text on the system clipboard. The Plugin SDK has no clipboard
/// service and a plugin must not touch Avalonia, so this pipes the text into
/// the platform's own command-line tool: clip.exe on Windows, pbcopy on macOS,
/// wl-copy / xclip / xsel on Linux (whichever is installed).
/// </summary>
internal static class ClipboardWriter
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Returns true when one of the tools accepted the text.</summary>
    public static async Task<bool> TrySetTextAsync(string text)
    {
        foreach ((string fileName, string arguments) in CandidateTools())
        {
            if (await TryRunAsync(fileName, arguments, text).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private static IEnumerable<(string FileName, string Arguments)> CandidateTools()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("clip.exe", string.Empty);
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return ("pbcopy", string.Empty);
            yield break;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            yield return ("wl-copy", string.Empty);

        yield return ("xclip", "-selection clipboard");
        yield return ("xsel", "--clipboard --input");
    }

    private static async Task<bool> TryRunAsync(string fileName, string arguments, string text)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo(fileName, arguments)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start()) return false;

            // No trailing newline: the clipboard must hold exactly the text.
            await process.StandardInput.WriteAsync(text).ConfigureAwait(false);
            process.StandardInput.Close();

            using CancellationTokenSource cts = new(ToolTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* already gone */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            // Tool missing, not executable or hanging — try the next one.
            return false;
        }
    }
}
