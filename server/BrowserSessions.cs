// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

internal sealed class BrowserSessions(Profile[] profiles, TimeSpan lifetime, TimeProvider clock)
{
    const string Cookie = "invoicebridge_session";
    const int Capacity = 256;
    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly object gate = new();
    sealed record Entry(Profile Profile, DateTimeOffset Expires);
    sealed record Login(string? ApiKey);

    internal static TimeSpan LocalLifetime()
    {
        var value = Environment.GetEnvironmentVariable("INVOICEBRIDGE_SESSION_SECONDS");
        if (value == null) return TimeSpan.FromMinutes(30);
        if (int.TryParse(value, out int seconds) && seconds is >= 1 and <= 86400) return TimeSpan.FromSeconds(seconds);
        throw new InvalidDataException("Ungültige Sitzungsdauer.");
    }

    internal Profile? Authorize(HttpContext ctx, out IResult denied)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        // An explicit machine credential always follows the existing API contract.
        if (ctx.Request.Headers.TryGetValue("X-Api-Key", out var keys))
        {
            var profile = Profiles.Authenticate(profiles, keys.Count == 1 ? keys[0] : null);
            denied = profile == null ? KeyRequired(ctx) : Results.Empty;
            return profile;
        }
        if (ctx.Request.Cookies.ContainsKey(Cookie))
        {
            if (!SameOrigin(ctx))
            {
                denied = CrossSite();
                return null;
            }
            var profile = Current(ctx);
            denied = Problem(401, "session_required", "Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
            if (profile == null) ctx.Response.Cookies.Delete(Cookie, Options(ctx));
            return profile;
        }
        denied = KeyRequired(ctx);
        return null;
    }

    internal void Map(WebApplication app)
    {
        var group = app.MapGroup("/v1/session");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsPost(http.Request.Method) && !SameOrigin(http)) return CrossSite();
            return await next(context);
        });
        group.MapGet("", (HttpContext ctx) =>
        {
            var profile = Current(ctx);
            if (profile != null) return Results.Json(new { tenant = profile.Id });
            if (ctx.Request.Cookies.ContainsKey(Cookie))
            {
                ctx.Response.Cookies.Delete(Cookie, Options(ctx));
                return Problem(401, "session_expired", "Die Sitzung ist abgelaufen. Bitte erneut anmelden.");
            }
            return Problem(401, "session_required", "Bitte anmelden.");
        });
        group.MapPost("/login", (Func<HttpContext, Task<IResult>>)SignIn);
        group.MapPost("/logout", (HttpContext ctx) =>
        {
            End(ctx);
            ctx.Response.Cookies.Delete(Cookie, Options(ctx));
            return Results.NoContent();
        });
    }

    async Task<IResult> SignIn(HttpContext ctx)
    {
        if (!ctx.Request.HasJsonContentType()) return Problem(415, "login_json_required", "Bitte den Schlüssel als JSON übermitteln.");
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = 1024;
        Profile? profile;
        try
        {
            if (ctx.Request.ContentLength > 1024) return InvalidLogin();
            var body = await ctx.Request.ReadFromJsonAsync<Login>(ctx.RequestAborted);
            profile = Profiles.Authenticate(profiles, body?.ApiKey);
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        { return InvalidLogin(); }
        if (profile == null) return InvalidLogin();

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        lock (gate)
        {
            foreach (var expired in entries.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                entries.Remove(expired);
            if (CookieHash(ctx) is { } previous) entries.Remove(previous);
            if (entries.Count >= Capacity) return Problem(429, "session_capacity", "Alle Anmeldesitzungen sind belegt. Bitte später erneut versuchen.");
            entries.Add(Hash(id), new(profile, now + lifetime));
        }
        // A session cookie is retained on reload; the server enforces the absolute lifetime.
        ctx.Response.Cookies.Append(Cookie, id, Options(ctx));
        return Results.Json(new { tenant = profile.Id });
    }

    Profile? Current(HttpContext ctx)
    {
        if (CookieHash(ctx) is not { } id) return null;
        lock (gate)
        {
            if (!entries.TryGetValue(id, out var session)) return null;
            if (session.Expires > clock.GetUtcNow()) return session.Profile;
            entries.Remove(id);
            return null;
        }
    }

    void End(HttpContext ctx)
    {
        if (CookieHash(ctx) is not { } id) return;
        lock (gate) entries.Remove(id);
    }

    static bool SameOrigin(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin;
        if (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0
            || !Uri.TryCreate(ctx.Request.Scheme + "://" + ctx.Request.Host.Value, UriKind.Absolute, out var target))
            return false;
        var site = ctx.Request.Headers["Sec-Fetch-Site"];
        if (site.Count > 0 && (site.Count != 1 || site[0] != "same-origin")) return false;
        return (ctx.Request.IsHttps || target.IsLoopback) && uri.Scheme == target.Scheme
            && string.Equals(uri.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == target.Port;
    }

    static CookieOptions Options(HttpContext ctx) => new() {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = "/"
    };
    static string? CookieHash(HttpContext ctx) =>
        ctx.Request.Cookies.TryGetValue(Cookie, out var id) && id.Length == 64 && id.All(char.IsAsciiHexDigit) ? Hash(id) : null;
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));
    static IResult CrossSite() => Problem(403, "cross_site_request", "Nur Anfragen von dieser InvoiceBridge-Seite sind erlaubt.");
    static IResult InvalidLogin() => Problem(401, "invalid_login", "Der API-Schlüssel ist ungültig.");
    static IResult KeyRequired(HttpContext ctx)
    {
        ctx.Response.Headers.WWWAuthenticate = "ApiKey";
        return Problem(401, "api_key_required", "API-Key fehlt oder ist ungültig.");
    }
    static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
