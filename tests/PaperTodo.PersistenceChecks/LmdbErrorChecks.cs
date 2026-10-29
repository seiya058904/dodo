using System.ComponentModel;
using PaperTodo;

internal static class LmdbErrorChecks
{
    internal static void SafeErrorMessages()
    {
        LmdbNative.Check(0, "successful operation");
        for (var code = -30799; code <= -30779; code++)
            Require(Error(code).Message.Contains(": MDB_", StringComparison.Ordinal),
                "A documented LMDB error lost its static native description.");

        var errno = new Dictionary<int, string>
        {
            [2] = "No such file or directory", [5] = "Input/output error",
            [12] = "Not enough memory", [13] = "Permission denied",
            [16] = "Device or resource busy", [22] = "Invalid argument",
            [28] = "No space left on device"
        };
        foreach (var pair in errno)
            Require(Error(pair.Key).Message.EndsWith(pair.Value, StringComparison.Ordinal),
                "A C errno was interpreted as a different Win32 error.");

        // Sharing violations and disk-full/path failures must use a durable system
        // message, including on a localized Windows installation. The upstream
        // native Windows fallback returns an expired stack pointer for these codes.
        foreach (var code in new[] { 32, 112, 123, int.MaxValue })
            Require(Error(code).Message.EndsWith(new Win32Exception(code).Message, StringComparison.Ordinal),
                "The Win32 error message was lost or corrupted.");
        foreach (var code in new[] { -30800, -30778, -1, int.MinValue })
            Require(Error(code).Message.EndsWith($"Unknown LMDB error {code}", StringComparison.Ordinal),
                "An unknown negative code reached the unsafe native fallback.");
    }

    private static LmdbException Error(int code)
    {
        try { LmdbNative.Check(code, "error-message-check"); }
        catch (LmdbException error)
        {
            Require(error.ErrorCode == code && error.Message.Contains("error-message-check", StringComparison.Ordinal),
                "Diagnostic context or the original error code was lost.");
            return error;
        }
        throw new InvalidOperationException("An LMDB failure was not reported.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
