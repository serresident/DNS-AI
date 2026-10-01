using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DnsAi.App.Models;

namespace DnsAi.App.Services;

public class DohClient : IDisposable
{
    private readonly EndpointsConfig _config;
    private readonly HttpClient _httpClient;
    private readonly List<IPAddress> _ipPool = new();
    private int _currentIpIndex = 0;
    private readonly object _lock = new();

    public string CurrentIpString => _ipPool.Count > 0 ? _ipPool[_currentIpIndex % _ipPool.Count].ToString() : "N/A";
    public int LastLatencyMs { get; private set; } = -1;

    public DohClient(EndpointsConfig? config = null)
    {
        _config = config ?? EndpointsConfig.Load();

        // Load valid IP addresses from config
        foreach (var ipStr in _config.V4)
        {
            if (IPAddress.TryParse(ipStr, out var parsed))
            {
                _ipPool.Add(parsed);
            }
        }

        if (_ipPool.Count == 0)
        {
            _ipPool.Add(IPAddress.Parse("192.144.59.14"));
            _ipPool.Add(IPAddress.Parse("186.246.49.127"));
        }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 64,
            EnableMultipleHttp2Connections = true,
            // Custom connection logic to avoid system DNS loop (Bootstrap Loop invariant)
            ConnectCallback = async (context, cancellationToken) =>
            {
                IPAddress targetIp;
                lock (_lock)
                {
                    targetIp = _ipPool[_currentIpIndex % _ipPool.Count];
                }

                var socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    // On connect failure, advance to next IP in pool
                    lock (_lock)
                    {
                        _currentIpIndex = (_currentIpIndex + 1) % _ipPool.Count;
                    }
                    throw;
                }
            }
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
    }

    /// <summary>
    /// Forwards raw RFC 8484 wire-format DNS query to upstream DoH server
    /// </summary>
    public async Task<byte[]> QueryAsync(byte[] rawDnsQuery, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EndpointsConfig.DefaultDohUrl)
        {
            Content = new ByteArrayContent(rawDnsQuery)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

        var sw = Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadAsByteArrayAsync(ct);
        sw.Stop();

        LastLatencyMs = (int)sw.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// Pings the fastest endpoint with a standard DNS query and records latency
    /// </summary>
    public async Task<(bool Success, int LatencyMs, string Ip)> ProbeLatencyAsync(CancellationToken ct = default)
    {
        // Minimal standard query for "dns.dns-ai.ru" A record
        byte[] probeQuery = new byte[]
        {
            0xAA, 0xBB, // ID
            0x01, 0x00, // Standard query, RD=1
            0x00, 0x01, // QDCOUNT = 1
            0x00, 0x00, // ANCOUNT = 0
            0x00, 0x00, // NSCOUNT = 0
            0x00, 0x00, // ARCOUNT = 0
            // QNAME: 3dns6dns-ai2ru0
            0x03, 0x64, 0x6e, 0x73,
            0x06, 0x64, 0x6e, 0x73, 0x2d, 0x61, 0x69,
            0x02, 0x72, 0x75,
            0x00,
            0x00, 0x01, // QTYPE = A
            0x00, 0x01  // QCLASS = IN
        };

        try
        {
            var sw = Stopwatch.StartNew();
            var response = await QueryAsync(probeQuery, ct);
            sw.Stop();

            if (response != null && response.Length > 12)
            {
                LastLatencyMs = (int)sw.ElapsedMilliseconds;
                return (true, LastLatencyMs, CurrentIpString);
            }
        }
        catch
        {
            // Try next node if current failed
            lock (_lock)
            {
                _currentIpIndex = (_currentIpIndex + 1) % _ipPool.Count;
            }
        }

        return (false, -1, CurrentIpString);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
