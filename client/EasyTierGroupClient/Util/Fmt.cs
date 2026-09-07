namespace EasyTierGroupClient.Util;

/// <summary>流量/时长显示格式化。</summary>
public static class Fmt
{
    public static string Bytes(ulong b)
    {
        if (b >= 1UL << 30) return $"{b / 1073741824.0:F2} GB";
        if (b >= 1UL << 20) return $"{b / 1048576.0:F2} MB";
        if (b >= 1UL << 10) return $"{b / 1024.0:F1} KB";
        return $"{b} B";
    }

    public static string Rate(double bytesPerSec) => Bytes((ulong)bytesPerSec) + "/s";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒";
        return $"{(int)t.TotalSeconds} 秒";
    }
}
