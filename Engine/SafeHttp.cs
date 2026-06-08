using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TypePet.Engine;

/// <summary>
/// Shared SSRF-safe networking primitives for fetching UNTRUSTED remote resources (hub-distributed command
/// images, the <c>/rank</c> avatar canvas, downloaded command zips). An <see cref="HttpClient"/> built here
/// refuses to connect to any private / loopback / link-local / CGNAT / multicast address — even if an
/// allowlisted hostname (re)resolves to one (DNS-rebind safe) — and callers cap the response size. The IP
/// gate is the same fail-closed logic the Jint script sandbox uses (<c>CommandScriptHost.Jint.cs</c>);
/// centralized here so non-scripting code paths (ImageCache, the hub client) share one audited filter and are
/// safe regardless of the <c>TYPEPET_SCRIPTING</c> build flag.
/// </summary>
public static class SafeHttp
{
    /// <summary>An <see cref="HttpClient"/> whose connections are restricted to public unicast addresses.
    /// Redirects are followed (image CDNs / zip mirrors commonly 3xx) and every hop is still IP-checked by the
    /// connect callback. There is no client-wide timeout — pass a per-request token with a deadline.</summary>
    public static HttpClient CreateClient(string userAgent, bool allowRedirects = true)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirects,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = SafeConnectAsync,
        };
        var c = new HttpClient(handler);
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        return c;
    }

    /// <summary>True for an absolute <c>https://</c> URL whose host is not an IP literal in a blocked range.
    /// <c>http://</c> and non-absolute URLs are rejected — remote command assets must be https to a public
    /// host. (The connect callback re-checks the resolved IP, so this is a cheap pre-filter.)</summary>
    public static bool IsAllowedHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (IPAddress.TryParse(uri.Host, out var ip) && IsBlockedIp(ip)) return false;
        return true;
    }

    /// <summary>Stream a response body with a hard byte cap, aborting once it is exceeded. Returns the bytes,
    /// or throws on transport error / oversize.</summary>
    public static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, long maxBytes, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes) throw new IOException($"response exceeds {maxBytes} bytes");
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ SSRF connect + IP gate (fail closed)
    private static async ValueTask<Stream> SafeConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var addr = addrs.FirstOrDefault(a => !IsBlockedIp(a))
                   ?? throw new IOException($"\"{host}\" resolves only to a blocked address.");
        var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addr, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>The sole IP-level SSRF gate; fails closed. Any IPv4-in-IPv6 form is un-embedded and judged as
    /// IPv4; a native IPv6 address is allowed only when it is global unicast (2000::/3) and not a special
    /// range.</summary>
    public static bool IsBlockedIp(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && TryExtractEmbeddedV4(ip, out var v4))
            return IsBlockedIp(v4);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? IsBlockedV6(ip) : IsBlockedV4(ip);
    }

    /// <summary>Un-embed an IPv4 address from any IPv4-in-IPv6 encoding the kernel might route: IPv4-mapped
    /// (<c>::ffff:a.b.c.d</c>), IPv4-compatible (<c>::a.b.c.d</c>), NAT64 (<c>64:ff9b::/96</c>) and 6to4
    /// (<c>2002::/16</c>). False for a genuine native IPv6.</summary>
    private static bool TryExtractEmbeddedV4(IPAddress ip, out IPAddress v4)
    {
        v4 = IPAddress.None;
        byte[] b = ip.GetAddressBytes(); // 16 bytes, network order
        bool high80Zero = true;
        for (int i = 0; i < 10; i++) if (b[i] != 0) { high80Zero = false; break; }
        if (high80Zero && ((b[10] == 0xff && b[11] == 0xff) || (b[10] == 0 && b[11] == 0)))   // mapped / compatible
        { v4 = new IPAddress(new[] { b[12], b[13], b[14], b[15] }); return true; }
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b                       // NAT64 64:ff9b::/96
            && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        { v4 = new IPAddress(new[] { b[12], b[13], b[14], b[15] }); return true; }
        if (b[0] == 0x20 && b[1] == 0x02)                                                      // 6to4 2002::/16
        { v4 = new IPAddress(new[] { b[2], b[3], b[4], b[5] }); return true; }
        return false;
    }

    private static bool IsBlockedV6(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || IPAddress.IPv6Any.Equals(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return true;
        return (ip.GetAddressBytes()[0] & 0xE0) != 0x20; // block anything outside global unicast 2000::/3
    }

    private static bool IsBlockedV4(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (b[0] == 0 || b[0] == 10 || b[0] == 127) return true;        // 0/8, 10/8, loopback
        if (b[0] == 169 && b[1] == 254) return true;                    // link-local (incl. cloud metadata)
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;        // 172.16/12
        if (b[0] == 192 && b[1] == 168) return true;                    // 192.168/16
        if (b[0] == 192 && b[1] == 0 && b[2] == 0) return true;         // 192.0.0/24 (IETF protocol assignments)
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;       // CGNAT 100.64/10
        if (b[0] == 198 && b[1] is 18 or 19) return true;               // 198.18/15 benchmarking
        if (b[0] >= 224) return true;                                   // multicast / reserved / broadcast
        return false;
    }
}
