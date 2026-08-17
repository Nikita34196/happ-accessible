namespace HappAccessible.Models;

public enum ProxyCoreKind
{
    /// <summary>Pick Xray for Reality/Vision (proxy-only), else sing-box.</summary>
    Auto = 0,
    SingBox = 1,
    Xray = 2
}

public static class ProxyCoreKindParser
{
    public static ProxyCoreKind Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "xray" or "xray-core" => ProxyCoreKind.Xray,
        "sing-box" or "singbox" => ProxyCoreKind.SingBox,
        _ => ProxyCoreKind.Auto
    };

    public static string ToSetting(ProxyCoreKind kind) => kind switch
    {
        ProxyCoreKind.Xray => "xray",
        ProxyCoreKind.SingBox => "sing-box",
        _ => "auto"
    };
}
