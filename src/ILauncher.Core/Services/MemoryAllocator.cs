using System.Runtime.InteropServices;

namespace ILauncher.Core.Services;

/// <summary>一次自动内存分配的结果，单位 GB。</summary>
public sealed record MemorySuggestion(
    double Gigabytes,
    double AvailableGigabytes,
    double TotalGigabytes,
    double MinimumGb,
    double Target1Gb,
    double Target2Gb,
    double Target3Gb,
    bool LimitedBy32Bit);

/// <summary>
/// 自动分配游戏内存。算法与 PCL 一致：按“可用物理内存”和“模组数量”分阶段给内存，
/// 给得越多，剩余可用内存的折算比例越低，避免把整机内存吃光。
/// </summary>
public static class MemoryAllocator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private const double Gigabyte = 1024d * 1024d * 1024d;

    public static (double TotalGb, double AvailableGb) GetPhysicalMemory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return (0, 0);
            }

            return (Math.Round(status.TotalPhys / Gigabyte, 1), Math.Round(status.AvailPhys / Gigabyte, 1));
        }
        catch
        {
            return (0, 0);
        }
    }

    public static MemorySuggestion Suggest(int modCount, bool modded, bool optiFine, bool is32BitJava = false)
    {
        var (total, available) = GetPhysicalMemory();

        double minimum;
        double target1;
        double target2;
        double target3;
        if (modded)
        {
            // 可装 Mod 的实例：模组越多，需求越高
            minimum = 0.5d + modCount / 150d;
            target1 = 1.5d + modCount / 90d;
            target2 = 2.7d + modCount / 50d;
            target3 = 4.5d + modCount / 25d;
        }
        else if (optiFine)
        {
            minimum = 0.5d;
            target1 = 1.5d;
            target2 = 3d;
            target3 = 5d;
        }
        else
        {
            minimum = 0.5d;
            target1 = 1.5d;
            target2 = 2.5d;
            target3 = 4d;
        }

        var remaining = available;
        var give = 0d;
        (double Delta, double Ratio)[] stages =
        [
            (target1, 1d),
            (target2 - target1, 0.7d),
            (target3 - target2, 0.4d),
            (target3, 0.15d)
        ];

        foreach (var (delta, ratio) in stages)
        {
            if (delta <= 0)
            {
                continue;
            }

            give += Math.Min(remaining * ratio, delta);
            remaining -= delta / ratio;
            if (remaining < 0.1d)
            {
                break;
            }
        }

        give = Math.Round(Math.Max(give, minimum), 1);
        var limited = false;
        if (is32BitJava && give > 1d)
        {
            give = 1d;
            limited = true;
        }

        return new MemorySuggestion(give, available, total, Math.Round(minimum, 1), Math.Round(target1, 1),
            Math.Round(target2, 1), Math.Round(target3, 1), limited);
    }
}
