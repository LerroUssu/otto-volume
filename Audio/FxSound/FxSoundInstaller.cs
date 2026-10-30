using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Otto.Audio.FxSound;

/// <summary>
/// Gets FxSound from its official GitHub releases (fxsound2/fxsound-app, "latest"), checks the installer is
/// validly signed by FxSound, LLC, and runs it. The installer asks Windows for admin rights itself (it adds
/// FxSound's audio driver); Otto does not.
/// </summary>
public static class FxSoundInstaller
{
    private const string Url = "https://github.com/fxsound2/fxsound-app/releases/download/latest/";
    private const string Signer = "O=\"FxSound, LLC\"";

    /// <summary>true when the installer ran to the end and FxSound is now installed.</summary>
    public static async Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        string file = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "fxsound_setup.arm64.exe" : "fxsound_setup.exe";
        string dir = Path.Combine(Path.GetTempPath(), "Otto-fxsound-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, file);
        Directory.CreateDirectory(dir);
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Otto-volume");
                using var resp = await http.GetAsync(Url + file, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(path);
                var buf = new byte[81920];
                long done = 0; int n, lastPct = -1;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct != lastPct) { lastPct = pct; progress.Report(pct >= 0 ? $"Downloading FxSound… {pct} %" : $"Downloading FxSound… {done / 1048576} MB"); }
                }
            }

            if (!SignedByFxSound(path, out string why))
            {
                Log.Write($"[fxsound] installer rejected: {why}");
                progress.Report("The download isn't signed by FxSound. Not installed.");
                return false;
            }

            progress.Report("Installing FxSound…");
            using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })!;
            await p.WaitForExitAsync(ct);
            Log.Write($"[fxsound] installer exited {p.ExitCode}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("fxsound install", ex);
            progress.Report(ex is System.ComponentModel.Win32Exception ? "Installation cancelled." : "Couldn't download FxSound.");
            return false;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A valid Authenticode signature (WinVerifyTrust) whose signer is FxSound, LLC.</summary>
    public static bool SignedByFxSound(string path, out string why)
    {
        int hr = Verify(path);
        if (hr != 0) { why = $"WinVerifyTrust 0x{hr:X8}"; return false; }
#pragma warning disable SYSLIB0057
        var subject = X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
        why = subject;
        return subject.Contains(Signer, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo { public uint cbStruct; [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath; public IntPtr hFile, pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    private static int Verify(string path)
    {
        var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0,
                dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFile, dwStateAction = 0, dwProvFlags = 0,
            };
            return WinVerifyTrust(IntPtr.Zero, new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE") /* WINTRUST_ACTION_GENERIC_VERIFY_V2 */, ref data);
        }
        finally { Marshal.DestroyStructure<WinTrustFileInfo>(pFile); Marshal.FreeHGlobal(pFile); }
    }
}
