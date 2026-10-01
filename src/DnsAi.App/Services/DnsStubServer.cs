using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DnsAi.App.Services;

public class DnsStubServer : IDisposable
{
    private const int DnsPort = 53;
    private readonly DohClient _dohClient;
    private CancellationTokenSource? _cts;

    private Socket? _udpSocketV4;
    private Socket? _udpSocketV6;
    private TcpListener? _tcpListenerV4;
    private TcpListener? _tcpListenerV6;

    public long TotalQueries { get; private set; }
    public long SuccessfulQueries { get; private set; }
    public long FailedQueries { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action? StatsUpdated;

    public DnsStubServer(DohClient dohClient)
    {
        _dohClient = dohClient;
    }

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 1. Setup IPv4 UDP
        try
        {
            _udpSocketV4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udpSocketV4.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpSocketV4.Bind(new IPEndPoint(IPAddress.Loopback, DnsPort));
            _ = RunUdpListenerAsync(_udpSocketV4, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Не удалось запустить локальный DNS UDP сервер на 127.0.0.1:{DnsPort}. Возможно, порт 53 занят другой службой.", ex);
        }

        // 2. Setup IPv4 TCP
        try
        {
            _tcpListenerV4 = new TcpListener(IPAddress.Loopback, DnsPort);
            _tcpListenerV4.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcpListenerV4.Start(100);
            _ = RunTcpListenerAsync(_tcpListenerV4, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Не удалось запустить локальный DNS TCP сервер на 127.0.0.1:{DnsPort}.", ex);
        }

        // 3. Optional IPv6 UDP/TCP (::1)
        try
        {
            _udpSocketV6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            _udpSocketV6.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpSocketV6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, DnsPort));
            _ = RunUdpListenerAsync(_udpSocketV6, ct);

            _tcpListenerV6 = new TcpListener(IPAddress.IPv6Loopback, DnsPort);
            _tcpListenerV6.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcpListenerV6.Start(100);
            _ = RunTcpListenerAsync(_tcpListenerV6, ct);
        }
        catch
        {
            // IPv6 loopback bind is optional; ignore if not supported
        }

        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();

        try { _udpSocketV4?.Close(); } catch { }
        try { _udpSocketV6?.Close(); } catch { }
        try { _tcpListenerV4?.Stop(); } catch { }
        try { _tcpListenerV6?.Stop(); } catch { }

        _udpSocketV4 = null;
        _udpSocketV6 = null;
        _tcpListenerV4 = null;
        _tcpListenerV6 = null;

        IsRunning = false;
    }

    private async Task RunUdpListenerAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp);
                if (result.ReceivedBytes < 12) continue; // Minimum DNS header length

                var queryBytes = new byte[result.ReceivedBytes];
                Array.Copy(buffer, queryBytes, result.ReceivedBytes);
                var clientEndpoint = result.RemoteEndPoint;

                // Handle asynchronously without blocking the loop
                _ = Task.Run(async () =>
                {
                    Interlocked.Increment(ref _totalQueriesField);
                    TotalQueries = _totalQueriesField;

                    byte[]? responseBytes = null;
                    try
                    {
                        using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        queryCts.CancelAfter(TimeSpan.FromSeconds(4));
                        responseBytes = await _dohClient.QueryAsync(queryBytes, queryCts.Token);
                    }
                    catch
                    {
                        // Synthesize SERVFAIL
                        responseBytes = CreateServFailResponse(queryBytes);
                    }

                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        try
                        {
                            await socket.SendToAsync(responseBytes, SocketFlags.None, clientEndpoint);
                            Interlocked.Increment(ref _successQueriesField);
                            SuccessfulQueries = _successQueriesField;
                        }
                        catch
                        {
                            Interlocked.Increment(ref _failedQueriesField);
                            FailedQueries = _failedQueriesField;
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _failedQueriesField);
                        FailedQueries = _failedQueriesField;
                    }

                    StatsUpdated?.Invoke();
                }, ct);
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Socket error or transient read issue
            }
        }
    }

    private async Task RunTcpListenerAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleTcpClientAsync(client, ct);
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Transient accept failure
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var lengthBuffer = new byte[2];

                while (!ct.IsCancellationRequested)
                {
                    int readLen = await stream.ReadAsync(lengthBuffer.AsMemory(0, 2), ct);
                    if (readLen < 2) break;

                    ushort messageLen = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
                    if (messageLen == 0) continue;

                    var queryBuffer = new byte[messageLen];
                    int totalRead = 0;
                    while (totalRead < messageLen)
                    {
                        int r = await stream.ReadAsync(queryBuffer.AsMemory(totalRead, messageLen - totalRead), ct);
                        if (r == 0) break;
                        totalRead += r;
                    }

                    if (totalRead < messageLen) break;

                    Interlocked.Increment(ref _totalQueriesField);
                    TotalQueries = _totalQueriesField;

                    byte[]? responseBytes = null;
                    try
                    {
                        using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        queryCts.CancelAfter(TimeSpan.FromSeconds(4));
                        responseBytes = await _dohClient.QueryAsync(queryBuffer, queryCts.Token);
                    }
                    catch
                    {
                        responseBytes = CreateServFailResponse(queryBuffer);
                    }

                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        var respLen = new byte[2];
                        BinaryPrimitives.WriteUInt16BigEndian(respLen, (ushort)responseBytes.Length);
                        await stream.WriteAsync(respLen, ct);
                        await stream.WriteAsync(responseBytes, ct);

                        Interlocked.Increment(ref _successQueriesField);
                        SuccessfulQueries = _successQueriesField;
                    }
                    else
                    {
                        Interlocked.Increment(ref _failedQueriesField);
                        FailedQueries = _failedQueriesField;
                    }

                    StatsUpdated?.Invoke();
                }
            }
            catch
            {
                // Disconnected or timed out
            }
        }
    }

    private static byte[] CreateServFailResponse(byte[] query)
    {
        if (query.Length < 12) return Array.Empty<byte>();

        // Copy original query (to preserve ID and question section)
        var response = new byte[query.Length];
        Array.Copy(query, response, query.Length);

        // Flags: QR=1 (response), RA=1 (recursion available), RCODE=2 (SERVFAIL) -> 0x8182
        response[2] = 0x81;
        response[3] = 0x82;

        // Answers: 0, Authority: 0, Additional: 0
        response[6] = 0x00; response[7] = 0x00;
        response[8] = 0x00; response[9] = 0x00;
        response[10] = 0x00; response[11] = 0x00;

        return response;
    }

    private long _totalQueriesField;
    private long _successQueriesField;
    private long _failedQueriesField;

    public void Dispose()
    {
        Stop();
    }
}
