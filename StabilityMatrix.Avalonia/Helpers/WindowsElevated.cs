﻿using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using StabilityMatrix.Core.Processes;

namespace StabilityMatrix.Avalonia.Helpers;

[SupportedOSPlatform("windows")]
public static class WindowsElevated
{
    /// <summary>
    /// Move a file from source to target using elevated privileges.
    /// </summary>
    /// <param name="sourcePath"></param>
    /// <param name="targetPath"></param>
    public static async Task<ProcessResult> MoveFile(string sourcePath, string targetPath)
    {
        using var process = new Process();
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = $"/c move \"{sourcePath}\" \"{targetPath}\"";
        process.StartInfo.UseShellExecute = true;
        process.StartInfo.Verb = "runas";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        
        process.Start();
        await process.WaitForExitAsync().ConfigureAwait(false);
        
        return new ProcessResult(process.ExitCode, 
            await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false),
            await process.StandardError.ReadToEndAsync().ConfigureAwait(false));
    }
}
