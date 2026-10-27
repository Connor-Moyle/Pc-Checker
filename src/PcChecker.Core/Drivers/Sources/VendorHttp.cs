using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace PcChecker.Core.Drivers.Sources;

/// <summary>
/// HTTPS access for the vendor lookups and downloads. Redirects are followed by hand so every hop is checked against
/// the caller's allow-list; nothing is fetched from a host outside it. Sends no cookies and only the headers a plain
/// download needs.
/// </summary>
internal static class VendorHttp
{
    private static readonly HttpClient Client = CreateClient();

    /// <summary>Time allowed for one lookup request.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Fetches text from an allow-listed URL. Throws <see cref="HttpRequestException"/> for network and HTTP errors.</summary>
    public static Task<string> GetStringAsync(string url, IReadOnlyList<string> allowedHosts, CancellationToken cancellationToken) =>
        GetStringAsync(url, allowedHosts, null, cancellationToken);

    /// <summary>
    /// Fetches text from an allow-listed URL, sending <paramref name="referrer"/> (HTTPS only) as a browser that came
    /// from that page would. Throws <see cref="HttpRequestException"/> (with the status code) for network and HTTP errors.
    /// </summary>
    public static async Task<string> GetStringAsync(string url, IReadOnlyList<string> allowedHosts, string? referrer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, url, allowedHosts, referrer, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The vendor's server did not answer within " + (int)LookupTimeout.TotalSeconds + " seconds.");
        }
    }

    /// <summary>
    /// Sends a request and follows up to <see cref="VendorDownloadRules.MaxRedirects"/> redirects, each checked against
    /// <paramref name="allowedHosts"/>. The caller disposes the response.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, IReadOnlyList<string> allowedHosts, string? referrer,
        HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var current = new Uri(url);
        for (int hop = 0; hop <= VendorDownloadRules.MaxRedirects; hop++)
        {
            if (!VendorDownloadRules.IsAllowed(current, allowedHosts))
            {
                throw new HttpRequestException($"{current.Host} is not one of the vendor's official download servers, so PC Checker did not connect to it.");
            }

            using var request = new HttpRequestMessage(method, current);
            if (referrer is not null && Uri.TryCreate(referrer, UriKind.Absolute, out var referrerUri) && referrerUri.Scheme == Uri.UriSchemeHttps)
            {
                request.Headers.Referrer = referrerUri;
            }

            var response = await Client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                current = next;
                continue;
            }

            return response;
        }

        throw new HttpRequestException("The download redirected too many times.");
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(VendorHttp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PcChecker", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }
}
