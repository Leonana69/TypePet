using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TypePet.Platform.Abstractions;

namespace TypePet.Api.Hub;

/// <summary>
/// Creates a pull request to the hub repo (<c>Leonana69/TypePet-Commands</c>) straight from the app, so a
/// user can publish a command with one click. Auth is the GitHub OAuth <b>device flow</b> (built for desktop
/// apps — only a public <see cref="ClientId"/>, no secret); the resulting user token is stored in the
/// platform <see cref="ISecretStore"/> and reused. With the token it forks the repo (if needed), commits the
/// command under <c>commands/&lt;slug&gt;/</c> in a single clean commit (Git Data API — handles binary
/// assets via base64 blobs), and opens a PR authored by the signed-in user. Nothing is published until the
/// maintainer reviews + merges. The read path (browse/install) does NOT use this — it's anonymous.
///
/// One-time maintainer setup: register a GitHub OAuth App, tick "Enable Device Flow", scope
/// <c>public_repo</c>, and paste its client id into <see cref="ClientId"/>.
/// </summary>
public sealed class GitHubClient
{
    public const string Owner = "Leonana69";
    public const string Repo = "TypePet-Commands";

    /// <summary>The OAuth App's PUBLIC client id (not a secret). A build with the placeholder left in place
    /// reports <see cref="IsConfigured"/> = false and the UI falls back to the export + browser submit path.</summary>
    public const string ClientId = "REPLACE_WITH_OAUTH_APP_CLIENT_ID";

    private const string Scope = "public_repo";
    private const string TokenSecretId = "github-token";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string ApiBase = "https://api.github.com";

    private readonly ISecretStore _secrets;
    private static readonly HttpClient Http = CreateHttp();

    public GitHubClient(ISecretStore secrets) => _secrets = secrets;

    private static HttpClient CreateHttp()
    {
        // GitHub is a fixed public host, so the SafeHttp SSRF gate isn't needed here; set the headers the API
        // requires (a User-Agent is mandatory or GitHub returns 403).
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"TypePet/{AppInfo.Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return c;
    }

    /// <summary>Whether this build has an OAuth client id wired in (else use the export+browser fallback).</summary>
    public bool IsConfigured => !ClientId.StartsWith("REPLACE", StringComparison.Ordinal);

    /// <summary>Whether a user token is already stored (the device flow can be skipped).</summary>
    public bool HasToken => !string.IsNullOrEmpty(_secrets.Get(TokenSecretId));

    /// <summary>Forget the stored token (sign out).</summary>
    public void SignOut() => _secrets.Delete(TokenSecretId);

    // ------------------------------------------------------------------ device flow
    public sealed record DeviceCodeInfo(string DeviceCode, string UserCode, string VerificationUri, int Interval, int ExpiresIn);

    /// <summary>Begin the device flow: returns the code to show the user and the page to open. The UI then
    /// opens <see cref="DeviceCodeInfo.VerificationUri"/> and calls <see cref="PollForTokenAsync"/>.</summary>
    public async Task<DeviceCodeInfo> StartDeviceFlowAsync(CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        });
        using var resp = await Http.PostAsync(DeviceCodeUrl, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var r = doc.RootElement;
        return new DeviceCodeInfo(
            Str(r, "device_code"), Str(r, "user_code"),
            r.TryGetProperty("verification_uri", out var v) ? v.GetString() ?? "https://github.com/login/device" : "https://github.com/login/device",
            r.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5,
            r.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 900);
    }

    /// <summary>Poll until the user authorizes (then store + return the token), or null if denied/expired/
    /// cancelled. Honors GitHub's <c>interval</c> and <c>slow_down</c> backoff.</summary>
    public async Task<string?> PollForTokenAsync(DeviceCodeInfo info, CancellationToken ct)
    {
        int interval = Math.Max(1, info.Interval);
        var deadline = TimeSpan.FromSeconds(info.ExpiresIn);
        var elapsed = TimeSpan.Zero;
        while (elapsed < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);
            elapsed += TimeSpan.FromSeconds(interval);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = info.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });
            using var resp = await Http.PostAsync(AccessTokenUrl, content, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var r = doc.RootElement;

            if (r.TryGetProperty("access_token", out var tok) && tok.GetString() is { Length: > 0 } token)
            {
                _secrets.Set(TokenSecretId, token);
                return token;
            }
            string err = Str(r, "error");
            if (err == "authorization_pending") continue;
            if (err == "slow_down") { interval += 5; continue; }
            return null; // access_denied / expired_token / unsupported
        }
        return null;
    }

    // ------------------------------------------------------------------ create PR
    public sealed record SubmitResult(bool Ok, string Message, string? PrUrl = null);

    /// <summary>Fork (if needed), commit the command files under <c>commands/&lt;slug&gt;/</c>, and open a PR.
    /// <paramref name="buildFiles"/> is given the verified GitHub login so the caller can stamp it into a
    /// generated <c>hub.meta.json</c>; it returns the relative-path → bytes map to commit. Requires a stored
    /// token (run the device flow first). Never throws — returns a <see cref="SubmitResult"/>.</summary>
    public async Task<SubmitResult> CreatePullRequestAsync(
        string slug, Func<string, IReadOnlyDictionary<string, byte[]>> buildFiles, string title, string body, CancellationToken ct)
    {
        string? token = _secrets.Get(TokenSecretId);
        if (string.IsNullOrEmpty(token)) return new SubmitResult(false, "Not signed in to GitHub.");

        try
        {
            string login = Str(await ApiAsync(HttpMethod.Get, $"{ApiBase}/user", token, null, ct).ConfigureAwait(false), "login");
            if (login.Length == 0) return new SubmitResult(false, "Could not read your GitHub account.");

            string defaultBranch = Str(await ApiAsync(HttpMethod.Get, $"{ApiBase}/repos/{Owner}/{Repo}", token, null, ct).ConfigureAwait(false), "default_branch");
            if (defaultBranch.Length == 0) defaultBranch = "main";

            await EnsureForkAsync(login, token, ct).ConfigureAwait(false);

            // Best-effort: sync the fork's default branch to upstream so the PR is based on current main.
            try { await ApiAsync(HttpMethod.Post, $"{ApiBase}/repos/{login}/{Repo}/merge-upstream", token, Json(new { branch = defaultBranch }), ct).ConfigureAwait(false); }
            catch { /* a brand-new or already-current fork is fine */ }

            // Base commit + tree on the fork.
            var refDoc = await ApiAsync(HttpMethod.Get, $"{ApiBase}/repos/{login}/{Repo}/git/ref/heads/{defaultBranch}", token, null, ct).ConfigureAwait(false);
            string baseCommit = Str(refDoc.RootElement.GetProperty("object"), "sha");
            var commitDoc = await ApiAsync(HttpMethod.Get, $"{ApiBase}/repos/{login}/{Repo}/git/commits/{baseCommit}", token, null, ct).ConfigureAwait(false);
            string baseTree = Str(commitDoc.RootElement.GetProperty("tree"), "sha");

            // Blobs -> tree -> commit.
            var files = buildFiles(login);
            var treeItems = new List<object>();
            foreach (var (rel, bytes) in files)
            {
                var blob = await ApiAsync(HttpMethod.Post, $"{ApiBase}/repos/{login}/{Repo}/git/blobs", token,
                    Json(new { content = Convert.ToBase64String(bytes), encoding = "base64" }), ct).ConfigureAwait(false);
                treeItems.Add(new { path = $"commands/{slug}/{rel}", mode = "100644", type = "blob", sha = Str(blob, "sha") });
            }
            var treeDoc = await ApiAsync(HttpMethod.Post, $"{ApiBase}/repos/{login}/{Repo}/git/trees", token,
                Json(new { base_tree = baseTree, tree = treeItems }), ct).ConfigureAwait(false);
            string newTree = Str(treeDoc, "sha");

            var commitNew = await ApiAsync(HttpMethod.Post, $"{ApiBase}/repos/{login}/{Repo}/git/commits", token,
                Json(new { message = title, tree = newTree, parents = new[] { baseCommit } }), ct).ConfigureAwait(false);
            string newCommit = Str(commitNew, "sha");

            // New branch on the fork, then the PR against upstream.
            string branch = $"submit-{slug}-{Guid.NewGuid().ToString("N")[..6]}";
            await ApiAsync(HttpMethod.Post, $"{ApiBase}/repos/{login}/{Repo}/git/refs", token,
                Json(new { @ref = $"refs/heads/{branch}", sha = newCommit }), ct).ConfigureAwait(false);

            var prDoc = await ApiAsync(HttpMethod.Post, $"{ApiBase}/repos/{Owner}/{Repo}/pulls", token,
                Json(new { title, head = $"{login}:{branch}", @base = defaultBranch, body, maintainer_can_modify = true }), ct).ConfigureAwait(false);
            string prUrl = Str(prDoc, "html_url");
            return new SubmitResult(true, $"Opened a pull request for /{slug}.", prUrl);
        }
        catch (HttpRequestException ex)
        {
            return new SubmitResult(false, $"GitHub request failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new SubmitResult(false, ex.Message);
        }
    }

    /// <summary>Ensure the signed-in user has a fork of the hub repo, creating + waiting for one if absent.</summary>
    private async Task EnsureForkAsync(string login, string token, CancellationToken ct)
    {
        using var probe = await SendAsync(HttpMethod.Get, $"{ApiBase}/repos/{login}/{Repo}", token, null, ct).ConfigureAwait(false);
        if (probe.IsSuccessStatusCode) return;

        await ApiAsync(HttpMethod.Post, $"{ApiBase}/repos/{Owner}/{Repo}/forks", token, null, ct).ConfigureAwait(false);
        // Forking is async on GitHub's side; poll briefly until the fork's API responds.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            using var check = await SendAsync(HttpMethod.Get, $"{ApiBase}/repos/{login}/{Repo}", token, null, ct).ConfigureAwait(false);
            if (check.IsSuccessStatusCode) return;
        }
        throw new HttpRequestException("the fork was not ready in time; try again in a moment.");
    }

    // ------------------------------------------------------------------ HTTP helpers
    private async Task<JsonDocument> ApiAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct)
    {
        using var resp = await SendAsync(method, url, token, content, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(text, 300)}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    private static StringContent Json(object o) =>
        new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static string Str(JsonDocument doc, string prop) => Str(doc.RootElement, prop);

    private static string Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
