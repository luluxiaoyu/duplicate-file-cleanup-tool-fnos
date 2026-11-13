namespace DuplicateCleanup;
public static class LinuxRuntime
{
    public static bool IsRoot() => OperatingSystem.IsLinux() && LibC.geteuid() == 0;
    public static uint Uid() => LibC.geteuid();
}
