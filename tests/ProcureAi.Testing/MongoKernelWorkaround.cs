namespace ProcureAi.Testing;

/// <summary>
/// MongoDB 8 exits at start on Linux kernel 6.19 and newer ("MongoDB cannot start: Linux kernel versions 6.19 and newer has a known
/// incompatibility", SERVER-121912: its bundled TCMalloc's rseq use). Letting glibc keep rseq makes TCMalloc fall back to per-thread caches
/// (slightly slower, no crash). The AppHost's mongo resource and the platform chart's mongod set the same.
/// </summary>
public static class MongoKernelWorkaround
{
    public const string Variable = "GLIBC_TUNABLES";

    public const string Value = "glibc.pthread.rseq=1";
}
