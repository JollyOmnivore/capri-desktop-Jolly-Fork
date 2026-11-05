using System;
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

        internal static void Request()
        {
            if (!OperatingSystem.IsMacOS()) return;
            if (!AXIsProcessTrusted()) AccessibilityTrusted(true);
            if (!CGPreflightScreenCaptureAccess()) CGRequestScreenCaptureAccess();
        }
    }
}
