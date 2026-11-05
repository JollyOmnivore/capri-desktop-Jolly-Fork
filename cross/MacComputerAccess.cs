using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace CapriDesktop
{
    internal static class MacComputerAccess
    {
        private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string Accessibility = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [DllImport(Accessibility)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
        [DllImport(Accessibility)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
        [DllImport(Graphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
        [DllImport(Graphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
        [DllImport(Foundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
        [DllImport(Foundation)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport(Foundation)] private static extern void CFRelease(IntPtr value);

        internal static string Status()
        {
            if (!OperatingSystem.IsMacOS()) return "Computer use is currently available on Mac.";
            return "Accessibility: " + (AXIsProcessTrusted() ? "allowed" : "not allowed") + " · Screen Recording: " + (CGPreflightScreenCaptureAccess() ? "allowed" : "not allowed");
        }

        internal static bool AccessibilityTrusted(bool prompt)
        {
            if (!OperatingSystem.IsMacOS()) return false;
            IntPtr library = NativeLibrary.Load(Foundation);
            IntPtr key = IntPtr.Zero, options = IntPtr.Zero;
            try
            {
                key = CFStringCreateWithCString(IntPtr.Zero, "AXTrustedCheckOptionPrompt", 0x08000100);
                if (key == IntPtr.Zero) throw new InvalidOperationException("Could not create the Accessibility permission option.");
                options = CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { Marshal.ReadIntPtr(NativeLibrary.GetExport(library, prompt ? "kCFBooleanTrue" : "kCFBooleanFalse")) }, 1,
                    NativeLibrary.GetExport(library, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(library, "kCFTypeDictionaryValueCallBacks"));
                if (options == IntPtr.Zero) throw new InvalidOperationException("Could not create Accessibility permission settings.");
                return AXIsProcessTrustedWithOptions(options);
            }
            finally
            {
                if (options != IntPtr.Zero) CFRelease(options);
                if (key != IntPtr.Zero) CFRelease(key);
                NativeLibrary.Free(library);
            }
        }

        internal static bool ScreenAllowed() { return OperatingSystem.IsMacOS() && CGPreflightScreenCaptureAccess(); }

        internal static void RequestScreen() { if (OperatingSystem.IsMacOS() && !CGPreflightScreenCaptureAccess()) CGRequestScreenCaptureAccess(); }

        internal static async Task<string> Microphone(bool request)
        {
            string helper = Path.Combine(AppContext.BaseDirectory, "capri-mic");
            if (!OperatingSystem.IsMacOS() || !File.Exists(helper)) return "helper unavailable";
            using Process process = new Process { StartInfo = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add(request ? "--request-permission" : "--permission-status");
            process.Start();
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(request ? 125 : 5));
                string status = (await output).Trim();
                await errors;
                return process.ExitCode == 0 && Array.IndexOf(new[] { "allowed", "denied", "restricted", "not requested", "unknown" }, status) >= 0 ? status : "could not check";
            }
            catch { try { if (!process.HasExited) process.Kill(true); } catch (Exception) { } throw; }
        }

        internal static async Task<string> DriverPermissions(DshOptions options, string home)
        {
            string helper = Path.Combine(AppContext.BaseDirectory, "dsh-permissions.mjs");
            using Process process = new Process { StartInfo = DshBrain.StartInfo(options, helper, Path.Combine(home, "diagnostics")) };
            process.Start();
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                string text = await output;
                string error = await errors;
                if (process.ExitCode != 0) throw new InvalidOperationException("Driver permission check failed: " + Brain.Redacted(error.Length > 1600 ? error.Substring(0, 1600) : error));
                using JsonDocument parsed = JsonDocument.Parse(text);
                using DshTrace trace = new DshTrace(home, true);
                var report = new { host = Status(), driver = parsed.RootElement.Clone() };
                trace.Write("permissions.check", report);
                return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception error) { using DshTrace trace = new DshTrace(home, true); trace.Write("permissions.failure", new { error = error.Message }); try { if (!process.HasExited) process.Kill(true); } catch (Exception) { } throw; }
        }

        internal static void OpenPrivacy(string pane)
        {
            if (Array.IndexOf(new[] { "Accessibility", "ScreenCapture", "Microphone", "Automation", "FilesAndFolders" }, pane) < 0) throw new ArgumentException("Unknown privacy pane.");
            ProcessStartInfo open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            open.ArgumentList.Add("x-apple.systempreferences:com.apple.preference.security?Privacy_" + pane);
            Process.Start(open)?.Dispose();
        }

        internal static void Request()
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (!AXIsProcessTrusted()) AccessibilityTrusted(true);
            if (!CGPreflightScreenCaptureAccess()) CGRequestScreenCaptureAccess();
        }
    }
}
