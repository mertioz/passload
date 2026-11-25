using System.Runtime.InteropServices;

namespace Passload;

internal static class WindowsPackage
{
    public static bool HasIdentity { get; } = DetectIdentity();

    private static bool DetectIdentity()
    {
        const int insufficientBuffer = 122;
        uint length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == insufficientBuffer;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
