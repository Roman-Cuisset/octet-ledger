using System.Net;

namespace OctetLedger.Cli;

internal static class DashboardHttp
{
    internal static HttpStatusCode Validate(HttpListenerRequest request, Uri address)
    {
        // A DNS name can resolve to loopback. Require the authority that the CLI actually advertised.
        if (request.Headers["Host"] != address.Authority || request.Url?.Authority != address.Authority ||
            request.RemoteEndPoint is not { Address: var remote } || !IPAddress.IsLoopback(remote) ||
            request.RawUrl is not { } target || !target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal))
        {
            return HttpStatusCode.BadRequest;
        }

        if (request.HttpMethod is not ("GET" or "HEAD")) return HttpStatusCode.MethodNotAllowed;
        if (request.HasEntityBody) return HttpStatusCode.BadRequest;

        var origin = request.Headers["Origin"];
        if ((origin is not null && origin != address.GetLeftPart(UriPartial.Authority)) ||
            request.Headers["Sec-Fetch-Site"] is not (null or "none" or "same-origin"))
        {
            return HttpStatusCode.Forbidden;
        }

        return HttpStatusCode.OK;
    }

    internal static void SetHeaders(HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
    }
}
