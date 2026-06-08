#if TYPEPET_SCRIPTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using TypePet.Api;
using TypePet.Engine;

namespace TypePet.Api.Chat;

/// <summary>
/// The sandboxed implementation of <see cref="CommandScriptHost"/> for <c>kind:script</c> commands, using
/// the managed Jint JavaScript engine. The sandbox is capability-confined: the script gets a small host API
/// (say / clipboard / image / link / hold + pet expression/action/walk/face, plus <c>rank(…)</c> for the
/// core rank renderer) and the <c>args</c> string. There is NO filesystem, CLR, or process access. Network
/// access is OFF unless the command declares a <c>hosts:</c> allowlist AND the user has approved it
/// (<paramref name="networkApproved"/>); only then are <c>httpGet</c>/<c>httpJson</c> wired in, and every
/// request is still re-checked against that allowlist (https-only, private-IP blocked, size/time capped).
/// Hard resource limits (statement count, memory, recursion, wall-clock, cancellation) stop a runaway or
/// malicious upload from freezing the pet. Runs off the UI thread; pet calls marshal back internally.
/// </summary>
public static partial class CommandScriptHost
{
    private const int MaxStatements = 20_000;
    private const int MaxRecursion = 64;
    private const long MemoryLimitBytes = 4L * 1024 * 1024; // 4 MB
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(2);

    // Relaxed limits for network-enabled scripts: HTML pages + regex need more memory/statements than a tiny
    // synchronous script, and the longer wall clock leaves room for (C#-bounded) blocking fetches. NOTE: Jint's
    // wall clock / cancellation are checked only BETWEEN statements, so they do NOT interrupt a single in-flight
    // httpGet — that call is bounded solely by NetGet's per-request (RequestTimeout) and per-run (NetBudgetTotal)
    // C# timers, which must stay as the authoritative network bound.
    private const int NetMaxStatements = 200_000;
    private const long NetMemoryLimitBytes = 24L * 1024 * 1024; // 24 MB
    private static readonly TimeSpan NetWallClock = TimeSpan.FromSeconds(30);

    // Network safety caps (enforced in C#, independent of the script).
    private const int MaxRequests = 8;                              // httpGet calls per run
    private const long MaxResponseBytes = 4L * 1024 * 1024;          // 4 MB per response
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);   // per request
    private static readonly TimeSpan NetBudgetTotal = TimeSpan.FromSeconds(15);  // total across the run
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 TypePet";
    private static readonly string[] AllowedRequestHeaders = { "User-Agent", "Accept", "Accept-Language", "Referer" };

    // A network httpGet uses synchronous I/O, so each net-enabled run parks its thread-pool thread for up to the
    // per-run budget. Cap how many run concurrently (the async wait itself doesn't park a thread); tiny non-net
    // scripts are ungated.
    private static readonly SemaphoreSlim NetGate = new(4);

    public static partial Task<CommandResult> RunAsync(CommandManifest m, string args, IPetControl? pet,
        bool networkApproved, CancellationToken ct)
    {
        bool netEnabled = networkApproved && m.Hosts.Count > 0;
        return netEnabled
            ? RunGatedAsync(m, args ?? "", pet, ct)
            : Task.Run(() => Execute(m, args ?? "", pet, false, ct), ct);
    }

    private static async Task<CommandResult> RunGatedAsync(CommandManifest m, string args, IPetControl? pet, CancellationToken ct)
    {
        await NetGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => Execute(m, args, pet, true, ct), ct).ConfigureAwait(false); }
        finally { NetGate.Release(); }
    }

    private static CommandResult Execute(CommandManifest m, string args, IPetControl? pet, bool networkApproved, CancellationToken ct)
    {
        string? text = null, clip = null, image = null, linkTitle = null, linkUrl = null;
        double? hold = m.HoldSeconds;
        RankView? rankView = null;

        bool netEnabled = networkApproved && m.Hosts.Count > 0;
        CancellationTokenSource? netCts = null;

        try
        {
            var engine = new Jint.Engine(o =>
            {
                o.LimitRecursion(MaxRecursion);
                o.MaxStatements(netEnabled ? NetMaxStatements : MaxStatements);
                o.LimitMemory(netEnabled ? NetMemoryLimitBytes : MemoryLimitBytes);
                o.TimeoutInterval(netEnabled ? NetWallClock : WallClock);
                o.CancellationToken(ct);
                // Surface host (CLR) errors as JS-catchable exceptions so a script's try/catch around a
                // best-effort httpGet works. The engine's own constraints (timeout/statements/memory/cancel)
                // are NOT delegate calls, so they still abort the run regardless of this.
                o.CatchClrExceptions();
                // No AllowClr() → no .NET type access; no fetch/require/IO globals are exposed. Network, when
                // present, is the curated httpGet below — nothing more.
            });

            engine.SetValue("args", args);
            engine.SetValue("say", new Action<string>(s => text = s));
            engine.SetValue("clipboard", new Action<string>(s => clip = s));
            engine.SetValue("hold", new Action<double>(s => { if (double.IsFinite(s) && s > 0) hold = s; }));
            engine.SetValue("image", new Action<string>(s => { if (IsSafeImage(s)) image = s; }));
            engine.SetValue("link", new Action<string, string>((t, u) => { linkTitle = t; linkUrl = u; }));
            engine.SetValue("expression", new Action<string, double>((n, s) =>
                Fire(() => pet?.Expression(n, double.IsFinite(s) && s > 0 ? s : (double?)null))));
            engine.SetValue("action", new Action<string, string>((n, mode) =>
                Fire(() => pet?.DoAction(n, string.IsNullOrWhiteSpace(mode) ? null : mode))));
            engine.SetValue("walk", new Action<double>(x => { if (double.IsFinite(x)) Fire(() => pet?.WalkTo(x)); }));
            engine.SetValue("face", new Action<string>(d => Fire(() => pet?.Face(d))));
            // Hand structured rank data to the core renderer (RankRenderer) instead of speaking raw text.
            engine.SetValue("rank", new Action<JsValue>(v => { if (v is ObjectInstance o) rankView = ToRankView(o); }));

            if (netEnabled)
            {
                netCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                netCts.CancelAfter(NetBudgetTotal);
                CancellationToken budget = netCts.Token;
                int[] count = { 0 };
                var hosts = m.Hosts;

                var httpGet = new Func<string, JsValue, JsValue>((url, headers) =>
                {
                    if (++count[0] > MaxRequests)
                        throw new InvalidOperationException($"httpGet: too many requests (max {MaxRequests}).");
                    var (status, body) = NetGet(url, hosts, headers, budget);
                    var result = new JsObject(engine);
                    SetProp(result, "status", JsValue.FromObject(engine, status));
                    SetProp(result, "ok", JsValue.FromObject(engine, status is >= 200 and < 300));
                    SetProp(result, "body", JsValue.FromObject(engine, body));
                    return result;
                });
                engine.SetValue("httpGet", httpGet);
                // httpJson(url, headers?) → JSON.parse(httpGet(...).body). Defined in JS so it reuses the
                // engine's native JSON and the same allowlisted httpGet.
                engine.Execute("function httpJson(u, h){ return JSON.parse(httpGet(u, h).body); }");
            }
            else
            {
                string why = m.Hosts.Count == 0
                    ? "it declares no hosts: in its frontmatter"
                    : "its network access isn't approved (enable it in the Commands tab)";
                var blocked = new Func<string, JsValue, JsValue>((_, _) =>
                    throw new InvalidOperationException($"httpGet: /{m.Name} can't use the network — {why}."));
                engine.SetValue("httpGet", blocked);
                engine.SetValue("httpJson", blocked);
            }

            JsValue completion = engine.Evaluate(m.Body);

            // A returned primitive becomes the spoken text when the script didn't call say(…) / rank(…).
            if (rankView is null && string.IsNullOrEmpty(text) && completion is not null &&
                (completion.IsString() || completion.IsNumber() || completion.IsBoolean()))
                text = completion.ToString();
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"/{m.Name} script error: {Shorten(ex.Message)}");
        }
        finally
        {
            netCts?.Dispose();
        }

        // An optional reaction expression applies after the script (like the declarative kinds).
        var (reactExpr, reactSecs) = m.PickReaction();
        if (!string.IsNullOrEmpty(reactExpr))
            Fire(() => pet?.Expression(reactExpr!, reactSecs ?? hold));

        // A rank(…) call wins: the core renderer produces the text + image + profile link.
        if (rankView is { } rv)
            return RenderRank(rv, hold);

        WebSource? link = string.IsNullOrWhiteSpace(linkUrl) ? null
            : new WebSource(string.IsNullOrWhiteSpace(linkTitle) ? linkUrl! : linkTitle!, linkUrl!, "");

        return CommandResult.Ok(
            string.IsNullOrEmpty(text) ? (m.Help ?? "") : text!,
            sources: link is null ? null : new[] { link },
            link: link,
            imageUrl: image,
            clipboardText: clip,
            holdSeconds: hold);
    }

    // ------------------------------------------------------------------ rank rendering (core)
    private static CommandResult RenderRank(RankView v, double? hold)
    {
        string text = RankRenderer.Format(v);
        string? img = IsSafeImage(v.ImageUrl) ? v.ImageUrl : null;
        WebSource? link = string.IsNullOrWhiteSpace(v.InfoUrl) ? null
            : new WebSource(string.IsNullOrWhiteSpace(v.InfoTitle) ? v.InfoUrl! : v.InfoTitle!, v.InfoUrl!, "");
        return CommandResult.Ok(text, sources: link is null ? null : new[] { link },
            link: link, imageUrl: img, holdSeconds: hold);
    }

    private static RankView ToRankView(ObjectInstance o) => new(
        Name: Str(o, "name") ?? "?",
        Level: (int)(Num(o, "level") ?? 0),
        Job: Str(o, "job") ?? Str(o, "class") ?? "?",
        World: Str(o, "world") ?? "?",
        ExpPercent: Num(o, "expPercent"),
        Guild: Str(o, "guild"),
        Rank: NumL(o, "rank"),
        LegionLevel: (int?)NumL(o, "legionLevel"),
        LegionGrade: Str(o, "legionGrade"),
        Fame: (int?)NumL(o, "fame"),
        ImageUrl: Str(o, "imageUrl"),
        InfoTitle: Str(o, "infoTitle"),
        InfoUrl: Str(o, "infoUrl"),
        ServerLabel: Str(o, "serverLabel"));

    private static string? Str(ObjectInstance o, string key)
    {
        var v = o.Get(key);
        if (v.IsUndefined() || v.IsNull()) return null;
        string s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static double? Num(ObjectInstance o, string key)
    {
        var v = o.Get(key);
        return v.IsNumber() && double.IsFinite(v.AsNumber()) ? v.AsNumber() : null;
    }

    private static long? NumL(ObjectInstance o, string key)
    {
        var n = Num(o, key);
        return n is { } d ? (long)d : null;
    }

    private static void SetProp(ObjectInstance o, string name, JsValue value) =>
        o.FastSetProperty(name, new Jint.Runtime.Descriptors.PropertyDescriptor(value, writable: true, enumerable: true, configurable: true));

    // ------------------------------------------------------------------ network (allowlisted, SSRF-safe)
    // A single shared client whose ConnectCallback refuses any private/loopback/link-local target — so even
    // an allowlisted hostname that (re)resolves to an internal IP can't be reached (DNS-rebind safe).
    private static readonly HttpClient NetHttp = CreateNetHttp();

    private static HttpClient CreateNetHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,                  // surface 3xx to the script (e.g. maple.gg "not found")
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            ConnectCallback = SafeConnectAsync,
        };
        return new HttpClient(handler); // no client-wide timeout; each call uses a linked, time-boxed token
    }

    /// <summary>Resolve the target and connect only to a public unicast address — loopback, private,
    /// link-local (incl. the 169.254.169.254 metadata IP), CGNAT and multicast ranges are refused.</summary>
    private static async ValueTask<Stream> SafeConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var addr = addrs.FirstOrDefault(a => !IsBlockedIp(a))
                   ?? throw new IOException($"httpGet: \"{host}\" resolves only to a blocked address.");
        var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addr, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The sole IP-level SSRF gate, and it fails CLOSED. Any IPv4-in-IPv6 form is un-embedded and judged as
    // IPv4; a native IPv6 address is allowed only when it's global unicast (2000::/3) and not a special range.
    // So NAT64 (64:ff9b::/96), IPv4-compatible (::a.b.c.d), 6to4 (2002::/16) and other embeddings can't smuggle
    // an internal IPv4 past the filter (a denylist would have to enumerate every such form).
    private static bool IsBlockedIp(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && TryExtractEmbeddedV4(ip, out var v4))
            return IsBlockedIp(v4);
        return ip.AddressFamily == AddressFamily.InterNetworkV6 ? IsBlockedV6(ip) : IsBlockedV4(ip);
    }

    /// <summary>Un-embed an IPv4 address from any IPv4-in-IPv6 encoding the kernel might route: IPv4-mapped
    /// (<c>::ffff:a.b.c.d</c>), IPv4-compatible (<c>::a.b.c.d</c>, deprecated — also catches <c>::</c>/<c>::1</c>),
    /// NAT64 (<c>64:ff9b::/96</c>) and 6to4 (<c>2002:WWXX:YYZZ::/48</c>). False for a genuine native IPv6.</summary>
    private static bool TryExtractEmbeddedV4(IPAddress ip, out IPAddress v4)
    {
        v4 = IPAddress.None;
        byte[] b = ip.GetAddressBytes(); // 16 bytes, network order
        bool high80Zero = true;
        for (int i = 0; i < 10; i++) if (b[i] != 0) { high80Zero = false; break; }
        if (high80Zero && ((b[10] == 0xff && b[11] == 0xff) || (b[10] == 0 && b[11] == 0)))   // mapped / compatible
        { v4 = new IPAddress(new[] { b[12], b[13], b[14], b[15] }); return true; }
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b                        // NAT64 64:ff9b::/96
            && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        { v4 = new IPAddress(new[] { b[12], b[13], b[14], b[15] }); return true; }
        if (b[0] == 0x20 && b[1] == 0x02)                                                       // 6to4 2002::/16
        { v4 = new IPAddress(new[] { b[2], b[3], b[4], b[5] }); return true; }
        return false;
    }

    /// <summary>A native (non-embedding) IPv6 address: allowed only when it's global unicast (2000::/3) and not
    /// a special range (loopback / unspecified / link-local / site-local / ULA / multicast). Everything else —
    /// including the 0000::/8 and other non-global blocks — fails closed.</summary>
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

    /// <summary>A single allowlisted, time-boxed, size-capped GET. Returns (HTTP status, decoded body);
    /// 3xx bodies come back empty so the script can detect a redirect by status. Throws (surfaced to the
    /// script via CatchClrExceptions) on a disallowed scheme/host, transport failure, timeout, or oversize
    /// response — it never throws on a normal 4xx/5xx, so callers read those from the returned status/body.</summary>
    private static (int status, string body) NetGet(string url, IReadOnlyList<string> hosts, JsValue headers, CancellationToken budget)
    {
        var uri = ParseAndCheck(url, hosts);
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(budget);
        callCts.CancelAfter(RequestTimeout);

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        ApplyHeaders(req, headers);

        HttpResponseMessage resp;
        try
        {
            resp = NetHttp.Send(req, HttpCompletionOption.ResponseHeadersRead, callCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (budget.IsCancellationRequested) throw;  // total budget / user cancel → abort the run
            throw new InvalidOperationException("httpGet: request timed out.");
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;
            string body = status is >= 300 and < 400 ? "" : ReadCapped(resp, callCts.Token);
            return (status, body);
        }
    }

    private static Uri ParseAndCheck(string url, IReadOnlyList<string> hosts)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("httpGet: invalid URL.");
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("httpGet: only https:// URLs are allowed.");
        // IdnHost (punycode/ASCII) is the form actually resolved + sent as Host, and matches the ASCII-only
        // hosts: allowlist — using uri.Host (the Unicode form) would let the checked name differ from the
        // connected one.
        string host = uri.IdnHost.ToLowerInvariant();
        bool ok = hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
        if (!ok)
            throw new InvalidOperationException($"httpGet: host \"{host}\" is not in this command's hosts: allowlist.");
        return uri;
    }

    private static void ApplyHeaders(HttpRequestMessage req, JsValue headers)
    {
        if (headers is not ObjectInstance o) return;
        foreach (var name in AllowedRequestHeaders)
        {
            var v = o.Get(name);
            if (!v.IsString()) continue;
            req.Headers.Remove(name);
            req.Headers.TryAddWithoutValidation(name, v.AsString());
        }
    }

    private static string ReadCapped(HttpResponseMessage resp, CancellationToken ct)
    {
        using var s = resp.Content.ReadAsStream(ct);
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        int read;
        long total = 0;
        while ((read = s.Read(buf, 0, buf.Length)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
                throw new InvalidOperationException("httpGet: response too large.");
            ms.Write(buf, 0, read);
            ct.ThrowIfCancellationRequested();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // ------------------------------------------------------------------ shared helpers
    // A script can only point an image at a bundled or remote URL — never a local path it could probe.
    private static bool IsSafeImage(string? s) =>
        s is not null && (s.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                          || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static void Fire(Func<Task?> act) { try { _ = act(); } catch { /* pet may lack the move; ignore */ } }

    private static string Shorten(string s) => s.Length <= 160 ? s : s[..160] + "…";
}
#endif
