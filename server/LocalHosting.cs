// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

internal sealed record LocalHosting(IPAddress? Address, int Port)
{
    internal static LocalHosting Resolve(IConfiguration configuration, ProfileMode mode)
    {
        var container = Environment.GetEnvironmentVariable("INVOICEBRIDGE_CONTAINER") == "1";
        if (mode == ProfileMode.LocalReal && (container ||
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Der Modus local-real ist nur lokal außerhalb des Containers verfügbar.");
        if (configuration.GetSection("Kestrel:Endpoints").Exists())
            throw new InvalidDataException("Bitte die lokale HTTP-Adresse mit applicationUrl oder ASPNETCORE_URLS setzen.");
        if (container) return new(IPAddress.Any, 8080);
        var value = configuration["urls"];
        if (string.IsNullOrWhiteSpace(value)) return new(IPAddress.Loopback, 8080);
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.Trim(),
                @"^http://(?:localhost|127(?:\.[0-9]{1,3}){3}|\[::1\]):[0-9]{1,5}/?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.Port == 0 || !uri.IsLoopback)
            throw new InvalidDataException("applicationUrl muss genau eine HTTP-Loopback-Adresse mit Port sein.");
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return new(null, uri.Port);
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
            throw new InvalidDataException("applicationUrl muss auf Loopback zeigen.");
        return new(address, uri.Port);
    }

    internal void Configure(KestrelServerOptions options)
    {
        if (Address == null) options.ListenLocalhost(Port, listen => listen.Protocols = HttpProtocols.Http1);
        else options.Listen(Address, Port, listen => listen.Protocols = HttpProtocols.Http1);
    }
}
