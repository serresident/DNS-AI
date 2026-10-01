using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnsAi.App.Models;

public class EndpointsConfig
{
    public const string DefaultHostname = "dns.dns-ai.ru";
    public const string DefaultDohUrl = "https://dns.dns-ai.ru/dns-query";

    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("serial")]
    public int Serial { get; set; } = 3;

    [JsonPropertyName("updated")]
    public string Updated { get; set; } = "2026-09-27";

    [JsonPropertyName("v4")]
    public List<string> V4 { get; set; } = new()
    {
        "192.144.59.14",   // msk3
        "186.246.49.127",  // spb1
        "185.251.90.181"   // spb3
    };

    [JsonPropertyName("v6")]
    public List<string> V6 { get; set; } = new()
    {
        "2a0d:8480:0:67c::14",
        "2a0a:2b41:0:500d::53"
    };

    [JsonPropertyName("retired")]
    public List<string> Retired { get; set; } = new()
    {
        "217.60.10.20",
        "94.232.43.149"
    };

    [JsonPropertyName("notice")]
    public string Notice { get; set; } = string.Empty;

    public static EndpointsConfig Load(string? basePath = null)
    {
        basePath ??= AppDomain.CurrentDomain.BaseDirectory;
        var filePath = Path.Combine(basePath, "endpoints.json");

        if (File.Exists(filePath))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<EndpointsConfig>(json);
                if (config != null && config.V4.Count > 0)
                {
                    return config;
                }
            }
            catch
            {
                // Fallback to compiled defaults
            }
        }

        return new EndpointsConfig();
    }
}
