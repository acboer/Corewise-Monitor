using System.Globalization;
using System.Management;

namespace HwMon.Core.Collection;

internal static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string DeviceGuard = @"root\Microsoft\Windows\DeviceGuard";

    public static List<ManagementObject> Query(string ns, string wql)
    {
        var scope = new ManagementScope(ns);
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        return searcher.Get().Cast<ManagementObject>().ToList();
    }

    public static List<ManagementObject> Related(ManagementObject obj, string relatedClass)
        => obj.GetRelated(relatedClass).Cast<ManagementObject>().ToList();

    public static T? Get<T>(ManagementBaseObject obj, string property)
    {
        try
        {
            var value = obj[property];
            if (value is null) return default;
            if (value is T typed) return typed;
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch
        {
            return default;
        }
    }

    public static string? Str(ManagementBaseObject obj, string property)
    {
        var s = Get<object>(obj, property)?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
