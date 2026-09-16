using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using JellyfinHuePlugin.Configuration;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    public class HueService : IDisposable
    {
        /// <summary>Names a PEM file whose certificate is trusted as an extra bridge root. Test-only.</summary>
        public const string ExtraRootEnvironmentVariable = "JELLYFIN_HUE_EXTRA_ROOT_PEM";

        /// <summary>Bridge software from which CLIP v2 is available.</summary>
        internal const long MinimumV2SoftwareVersion = 1948086000;

        /// <summary>The admin-facing reason for a bridge failure nothing in the plugin anticipated. The one surviving reference to the log.</summary>
        internal const string UnhandledReason = "Something went wrong; the Jellyfin log names the reason.";

        /// <summary>The admin-facing reason for a response the plugin couldn't parse or didn't recognize the shape of.</summary>
        private const string UnreadableResponseReason = "The bridge sent a response the plugin couldn't read.";

        /// <summary>The admin-facing reason for an HTML response where JSON was expected -- the commonest wrong-address mistake (e.g. a router's own web page instead of the bridge). Shared by GetBridgeInfoAsync and AuthenticateAsync so the sentence exists once.</summary>
        private const string NotABridgeReason = "Something answered at that address, but it isn't a Hue bridge.";

        /// <summary>
        /// Present on every bridge request sent over <see cref="_httpClient"/>. Its value is the
        /// bridge id the certificate must name. The one call that has no id yet -- the unauthenticated
        /// GetBridgeInfoAsync -- never runs on this client (it uses <see cref="_bridgeInfoClient"/>
        /// instead), so an empty value here should never happen in real use; the callback treats it
        /// as a hard failure rather than silently degrading to chain-only trust. Absent on requests to
        /// anything that is not a bridge (e.g. cloud discovery), which get ordinary system trust.
        /// </summary>
        internal static readonly HttpRequestOptionsKey<string?> ExpectedBridgeIdOption = new("hue.expectedBridgeId");

        /// <summary>
        /// Set by <see cref="ShouldAcceptBridgeCertificate"/> on the failing request, but only when
        /// the request carried a real pin (a non-empty ExpectedBridgeIdOption) and the presented
        /// certificate did not satisfy it -- never for the chain-only bridge-info client, which has
        /// no stored certificate to compare against yet. SendWithRetryAsync reads it back off that
        /// same request after the handshake throws (Fact 9 of the design spec, proven in
        /// HueServiceCertificateTests.PinnedRequest_RealHandshakeRejectsTheCertificate_ReasonNamesTheStoredCertificate)
        /// and copies it onto the exception so every catch-all several frames up can tell a rejected
        /// certificate apart from an ordinary connection failure without re-deriving it.
        /// </summary>
        private static readonly HttpRequestOptionsKey<bool> CertificateRejectionOption = new("hue.certificateRejected");

        /// <summary>Key into Exception.Data carrying the CertificateRejectionOption verdict past the throw, for DescribeThrownRequest to read.</summary>
        private const string CertificateRejectedDataKey = "hue.certificateRejected";

        private const string ApplicationKeyHeader = "hue-application-key";
        private const string DeviceType = "jellyfin_hue_plugin";

        private readonly ILogger<HueService> _logger;
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Dedicated connection pool for the single unauthenticated call (GET /api/0/config) that
        /// learns a bridge's id before any pinning is possible.
        /// ServerCertificateCustomValidationCallback fires once per TLS handshake, not per request,
        /// so if this call shared a connection pool with authenticated CLIP requests, a connection
        /// opened here under chain-only trust would let a later request carrying the application key
        /// reuse it and skip subject-id pinning entirely. A separate HttpClient (and therefore a
        /// separate connection pool) forces a fresh handshake -- and a fresh callback invocation --
        /// for every authenticated request. Do not merge this back into _httpClient.
        /// </summary>
        private readonly HttpClient _bridgeInfoClient;

        /// <summary>Local discovery; null in the test constructor unless a test supplies one, which keeps the existing tests cloud-only.</summary>
        private readonly MdnsBridgeDiscovery? _mdns;

        /// <summary>How long DiscoverBridgesAsync listens for mDNS answers. A real bridge answers within milliseconds.</summary>
        internal static readonly TimeSpan MdnsWindow = TimeSpan.FromSeconds(2);

        /// <summary>Bridge ids learned from /api/0/config for bridges whose configuration has none yet.</summary>
        private readonly ConcurrentDictionary<string, string> _bridgeIdsByHost = new(StringComparer.OrdinalIgnoreCase);

        // Web defaults (camelCase, which the v2 keys already are), nulls omitted.
        private static readonly JsonSerializerOptions HueJsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public HueService(ILogger<HueService> logger, MdnsBridgeDiscovery mdns)
        {
            _logger = logger;
            var roots = BridgeCertificateValidator.LoadRoots(Environment.GetEnvironmentVariable(ExtraRootEnvironmentVariable), logger);
            _httpClient = new HttpClient(CreateDefaultHandler(logger, roots));
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
            _bridgeInfoClient = new HttpClient(CreateBridgeInfoHandler(logger, roots));
            _bridgeInfoClient.Timeout = TimeSpan.FromSeconds(5);
            _mdns = mdns;
        }

        // Test seam: lets tests capture requests and feed canned bridge responses. Both clients route
        // through the one supplied handler, so existing tests see every request through one mock.
        public HueService(ILogger<HueService> logger, HttpMessageHandler handler, MdnsBridgeDiscovery? mdns = null)
        {
            _logger = logger;
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(5);
            _bridgeInfoClient = _httpClient;
            _mdns = mdns;
        }

        // Judged by the extracted, independently-testable decision function; anything that isn't a
        // bridge request (the cloud discovery endpoint) needs system trust.
        private static HttpMessageHandler CreateDefaultHandler(ILogger logger, IReadOnlyList<X509Certificate2> roots) => new HttpClientHandler
        {
            AllowAutoRedirect = false, // Prevent HTTP->HTTPS redirects
            ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                ShouldAcceptBridgeCertificate(request, certificate, errors, roots, logger)
        };

        // This client only ever sends the one unauthenticated /api/0/config request that learns a
        // bridge's id, so there is no subject to pin yet: chain-only trust, unconditionally,
        // regardless of whatever NewRequest happened to store in the option. Routed through the
        // same decision function as the main handler so there is exactly one place that decides
        // whether a bridge certificate is acceptable.
        private static HttpMessageHandler CreateBridgeInfoHandler(ILogger logger, IReadOnlyList<X509Certificate2> roots) => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
                ShouldAcceptBridgeCertificate(request, certificate, errors, roots, logger, chainOnly: true)
        };

        /// <summary>
        /// The single certificate decision for both clients. Extracted out of the HttpClientHandler
        /// lambda so it can be unit tested directly instead of only through an actual TLS handshake
        /// (which is also why the sentinel and pooling bugs both lived here undetected).
        ///
        /// <paramref name="chainOnly"/> is false for the main client: no option at all (a non-bridge
        /// request, e.g. cloud discovery) falls back to system trust; a present-but-empty id is a
        /// hard failure, since every real caller on that client resolves a real id before a request
        /// is built, so an empty one means a pinned request lost its id somewhere; otherwise the
        /// chain must be trusted and the subject must name the expected bridge. A rejection reached
        /// with a real (non-empty) pin in play is recorded on <paramref name="request"/> via
        /// <see cref="CertificateRejectionOption"/> -- see that field's comment.
        ///
        /// <paramref name="chainOnly"/> is true for the bridge-info client, whose one call has no id
        /// to pin yet: it ignores the option entirely (present, absent or empty, it does not matter)
        /// and just requires a trusted chain, with no subject check.
        /// </summary>
        internal static bool ShouldAcceptBridgeCertificate(HttpRequestMessage request, X509Certificate2? certificate, SslPolicyErrors errors, IReadOnlyList<X509Certificate2> roots, ILogger logger, bool chainOnly = false)
        {
            string? expectedBridgeId = null;
            var pinned = false;
            if (!chainOnly)
            {
                if (!request.Options.TryGetValue(ExpectedBridgeIdOption, out expectedBridgeId))
                {
                    return errors == SslPolicyErrors.None;
                }

                if (string.IsNullOrEmpty(expectedBridgeId))
                {
                    logger.LogWarning("Bridge request to {Host} has no expected bridge id pinned; rejecting", request.RequestUri?.Host);
                    return false;
                }

                pinned = true;
            }

            if (certificate == null)
            {
                if (pinned)
                {
                    request.Options.Set(CertificateRejectionOption, true);
                }

                return false;
            }

            var verdict = BridgeCertificateValidator.Validate(certificate, expectedBridgeId, roots);
            if (verdict != BridgeCertificateValidator.Verdict.Trusted)
            {
                logger.LogWarning("Bridge certificate rejected for {Host}: {Verdict} (subject {Subject}, expected {BridgeId})",
                    request.RequestUri?.Host, verdict, BridgeCertificateValidator.SubjectCommonName(certificate), expectedBridgeId ?? "any");

                if (pinned)
                {
                    request.Options.Set(CertificateRejectionOption, true);
                }
            }

            return verdict == BridgeCertificateValidator.Verdict.Trusted;
        }

        // ---------------------------------------------------------------------------------
        // CLIP v2
        // ---------------------------------------------------------------------------------

        private HttpRequestMessage NewRequest(HttpMethod method, Uri uri, string? applicationKey, string? expectedBridgeId, object? body)
        {
            var request = new HttpRequestMessage(method, uri);
            // HttpRequestOptions.TryGetValue<string?> reports "absent" whenever the stored value is a
            // literal null (it pattern-matches the boxed value against TValue, which never matches null
            // for a reference type) -- indistinguishable from a request that never called NewRequest at
            // all. Store an empty sentinel instead so every bridge request is still recognized as one;
            // ShouldAcceptBridgeCertificate treats a present-but-empty id as a hard failure (defense in
            // depth -- every real caller on this client always resolves a real id before getting here).
            request.Options.Set(ExpectedBridgeIdOption, expectedBridgeId ?? string.Empty);
            if (applicationKey != null)
            {
                request.Headers.TryAddWithoutValidation(ApplicationKeyHeader, applicationKey);
            }

            if (body != null)
            {
                request.Content = JsonContent.Create(body, options: HueJsonOptions);
            }

            return request;
        }

        /// <summary>The reason mapped from a non-success HTTP status: a rejected key for 401/403, the bare status and phrase otherwise. The phrase is omitted when absent (e.g. HTTP/2, which carries none) rather than leaving a stray space before the period.</summary>
        private static string StatusReason(HttpResponseMessage response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return "The bridge rejected the API key. Authenticate again.";
            }

            var status = (int)response.StatusCode;
            return string.IsNullOrEmpty(response.ReasonPhrase)
                ? $"The bridge answered {status}."
                : $"The bridge answered {status} {response.ReasonPhrase}.";
        }

        /// <summary>Bridge software {version} doesn't support CLIP v2 -- shared by ResolveBridgeIdAsync and HueController.VerifyConnection so the wording cannot drift between the two sites that both discover the same fact.</summary>
        internal static string UnsupportedVersionReason(string softwareVersion) =>
            $"Bridge software {softwareVersion} doesn't support the v2 API; {MinimumV2SoftwareVersion} or newer is required.";

        /// <summary>
        /// The reason for a request that never got a response: the certificate-mismatch sentence
        /// when SendWithRetryAsync tagged the exception with CertificateRejectionOption's verdict
        /// (Fact 9), the generic "didn't answer" reason otherwise -- a timeout, a refused connection,
        /// a DNS failure, or any other transport error that never reached the bridge at all.
        /// </summary>
        private static string DescribeThrownRequest(Exception ex, string address) =>
            ex.Data.Contains(CertificateRejectedDataKey)
                ? "The bridge's certificate doesn't match the stored one. Authenticate again to store the new one."
                : $"The bridge at {address} didn't answer.";

        /// <summary>Validates the bridge's address and key and pins the bridge id.</summary>
        private async Task<(string? Host, string? BridgeId, string? Reason)> PrepareAsync(HueBridge bridge, CancellationToken cancellationToken)
        {
            if (!BridgeUri.TryParseHost(bridge.IpAddress, out var host))
            {
                _logger.LogWarning("Bridge address {Address} is not a valid host", bridge.IpAddress);
                return (null, null, $"\"{bridge.IpAddress}\" isn't a valid address.");
            }

            if (!BridgeUri.IsValidSegment(bridge.Username))
            {
                _logger.LogWarning("Bridge {Host} API key is not valid", host);
                return (null, null, "That API key isn't in the format a Hue bridge issues.");
            }

            var (bridgeId, reason) = await ResolveBridgeIdAsync(host, bridge.HardwareId, cancellationToken);
            return bridgeId == null ? (null, null, reason) : (host, bridgeId, null);
        }

        private async Task<(string? BridgeId, string? Reason)> ResolveBridgeIdAsync(string host, string? configuredBridgeId, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(configuredBridgeId))
            {
                return (configuredBridgeId.Trim().ToLowerInvariant(), null);
            }

            if (_bridgeIdsByHost.TryGetValue(host, out var cached))
            {
                return (cached, null);
            }

            var info = await GetBridgeInfoAsync(host, cancellationToken);
            if (!info.Ok)
            {
                return (null, info.Reason);
            }

            var bridgeInfo = info.Value!;
            if (!bridgeInfo.SupportsV2)
            {
                _logger.LogWarning("Bridge {Host} is not a supported v2 bridge (software {Version})", host, bridgeInfo.SoftwareVersion);
                return (null, UnsupportedVersionReason(bridgeInfo.SoftwareVersion));
            }

            _bridgeIdsByHost[host] = bridgeInfo.HardwareId;
            return (bridgeInfo.HardwareId, null);
        }

        /// <summary>
        /// Drops the bridge id learned for an address, so the next pinned request to it reads
        /// /api/0/config again. Called when a bridge's address or key changes on the plugin page.
        /// An address that does not parse is ignored. Never throws, never logs.
        /// </summary>
        public virtual void ForgetHost(string address)
        {
            if (BridgeUri.TryParseHost(address, out var host))
            {
                _bridgeIdsByHost.TryRemove(host, out _);
            }
        }

        private static string? GetString(JsonElement element, params string[] path)
        {
            var current = element;
            foreach (var name in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }

        private static string? FindService(JsonElement resource, string rtype)
        {
            if (!resource.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var service in services.EnumerateArray())
            {
                if (GetString(service, "rtype") == rtype)
                {
                    return GetString(service, "rid");
                }
            }

            return null;
        }

        /// <summary>
        /// A content-free summary of a JSON value for logging: its kind, the original text length,
        /// and either its top-level property names (object, sanitized -- see
        /// <see cref="DescribeProperties"/>) or element count (array). Used only on the
        /// authentication path, where the response can carry a freshly issued application key -- no
        /// value from the document itself is ever included.
        /// </summary>
        private static string DescribeShape(JsonElement element, int contentLength)
        {
            var detail = element.ValueKind switch
            {
                JsonValueKind.Object => DescribeProperties(element),
                JsonValueKind.Array => $"{element.GetArrayLength()} element(s)",
                _ => null
            };

            return detail == null
                ? $"{element.ValueKind}, {contentLength} chars"
                : $"{element.ValueKind}, {contentLength} chars, {detail}";
        }

        /// <summary>
        /// Property names are attacker-chosen text on this path, not content we control -- sanitize
        /// to a safe character set (ASCII letters, digits, '.', '_', '-'; everything else becomes
        /// '_') and cap both the length of each name and how many are listed, so a hostile responder
        /// cannot forge log lines (e.g. with embedded newlines) or blow up the log with a huge object.
        /// </summary>
        private static string DescribeProperties(JsonElement element)
        {
            const int maxNames = 10;
            const int maxNameLength = 32;

            var names = element.EnumerateObject().Select(p => p.Name).ToList();
            var shown = names.Take(maxNames).Select(name =>
            {
                var truncated = name.Length > maxNameLength ? name[..maxNameLength] : name;
                return new string(truncated.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray());
            });
            var more = names.Count > maxNames ? $", +{names.Count - maxNames} more" : string.Empty;

            return "properties: [" + string.Join(", ", shown) + "]" + more;
        }

        /// <summary>
        /// Reads the v2 envelope. Returns the data array (cloned, so the document can be disposed)
        /// and a null reason when the status is 2xx and errors is empty; logs every error
        /// description and returns null data with the mapped reason otherwise.
        /// </summary>
        private async Task<(JsonElement? Data, string? Reason)> ReadEnvelopeAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            // A wrong IP can serve a whole web page; keep log lines bounded.
            var excerpt = content.Length > 500 ? content[..500] + "…" : content;

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Bridge returned {Status} for {Operation}: {Content}", response.StatusCode, operation, excerpt);
                return (null, StatusReason(response));
            }

            try
            {
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", operation, excerpt);
                    return (null, UnreadableResponseReason);
                }

                var failed = false;
                string? firstDescription = null;
                if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var error in errors.EnumerateArray())
                    {
                        failed = true;
                        var description = GetString(error, "description") ?? "?";
                        firstDescription ??= description;
                        _logger.LogWarning("Bridge error for {Operation}: {Description}", operation, description);
                    }
                }

                if (failed)
                {
                    return (null, $"The bridge refused the request: {firstDescription}.");
                }

                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", operation, excerpt);
                    return (null, UnreadableResponseReason);
                }

                return (data.Clone(), null);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse bridge response for {Operation}: {Content}", operation, excerpt);
                return (null, UnreadableResponseReason);
            }
        }

        private async Task<(JsonElement? Data, string? Reason)> GetResourcesAsync(HueBridge bridge, string resourceType, CancellationToken cancellationToken)
        {
            var (host, bridgeId, reason) = await PrepareAsync(bridge, cancellationToken);
            if (host == null)
            {
                return (null, reason);
            }

            var uri = BridgeUri.Build(host, "clip", "v2", "resource", resourceType);
            var response = await SendWithRetryAsync(_httpClient,
                () => NewRequest(HttpMethod.Get, uri, bridge.Username, bridgeId, null), cancellationToken);
            return await ReadEnvelopeAsync(response, $"get {resourceType}", cancellationToken);
        }

        /// <summary>Unauthenticated facts about a bridge; the certificate is checked against the root only, since the id is what this call learns.</summary>
        public virtual async Task<HueResult<HueBridgeInfo>> GetBridgeInfoAsync(string bridgeIp, CancellationToken cancellationToken = default)
        {
            if (!BridgeUri.TryParseHost(bridgeIp, out var host))
            {
                _logger.LogWarning("Bridge address {Address} is not a valid host", bridgeIp);
                return HueResult<HueBridgeInfo>.Failure($"\"{bridgeIp}\" isn't a valid address.");
            }

            try
            {
                var uri = BridgeUri.Build(host, "api", "0", "config");
                // Its own connection pool: see the comment on _bridgeInfoClient.
                var response = await SendWithRetryAsync(_bridgeInfoClient,
                    () => NewRequest(HttpMethod.Get, uri, null, null, null), cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var excerpt = content.Length > 500 ? content[..500] + "…" : content;

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Bridge returned {Status} for {Operation}: {Content}", response.StatusCode, "bridge config", excerpt);
                    return HueResult<HueBridgeInfo>.Failure(StatusReason(response));
                }

                // The commonest wrong-address mistake: a router's own web page answering at that IP. No key
                // exists on this call's path (it is unauthenticated), so unlike AuthenticateAsync's HTML
                // branch there is nothing to protect by omitting it -- the excerpt is logged like every
                // other warning in this method.
                if (content.TrimStart().StartsWith('<'))
                {
                    _logger.LogWarning("Bridge returned an HTML response for {Operation}: {Content}", "bridge config", excerpt);
                    return HueResult<HueBridgeInfo>.Failure(NotABridgeReason);
                }

                using var doc = JsonDocument.Parse(content);
                var bridgeId = GetString(doc.RootElement, "bridgeid")?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(bridgeId))
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Content}", "bridge config", excerpt);
                    return HueResult<HueBridgeInfo>.Failure(UnreadableResponseReason);
                }

                return HueResult<HueBridgeInfo>.Success(new HueBridgeInfo(
                    bridgeId,
                    GetString(doc.RootElement, "swversion") ?? string.Empty,
                    GetString(doc.RootElement, "apiversion") ?? string.Empty,
                    GetString(doc.RootElement, "modelid") ?? string.Empty));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse bridge response for {Operation}", "bridge config");
                return HueResult<HueBridgeInfo>.Failure(UnreadableResponseReason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading bridge configuration from {Host}", host);
                return HueResult<HueBridgeInfo>.Failure(DescribeThrownRequest(ex, host));
            }
        }

        /// <summary>Creates an application key. Requires the link button. The raw response is never logged on success.</summary>
        public virtual async Task<HueResult<AuthenticationOutcome>> AuthenticateAsync(string bridgeIp, CancellationToken cancellationToken = default)
        {
            var infoResult = await GetBridgeInfoAsync(bridgeIp, cancellationToken);
            if (!infoResult.Ok)
            {
                return HueResult<AuthenticationOutcome>.Failure(infoResult.Reason!);
            }

            var info = infoResult.Value!;
            BridgeUri.TryParseHost(bridgeIp, out var host);
            if (!info.SupportsV2)
            {
                _logger.LogWarning("Bridge {Host} is not a supported v2 bridge (software {Version})", host, info.SoftwareVersion);
                return HueResult<AuthenticationOutcome>.Failure(UnsupportedVersionReason(info.SoftwareVersion));
            }

            try
            {
                _logger.LogInformation("Attempting to authenticate with bridge {HardwareId} at {Host}", info.HardwareId, host);

                var uri = BridgeUri.Build(host, "api");
                // No generateclientkey: bridge software 2071476020 rejects the value false
                // ("invalid value, false, for parameter, generateclientkey", type 7), and the
                // plugin never uses the entertainment client key. Omitting it is what 3.x sent.
                var body = new { devicetype = DeviceType };
                var response = await SendWithRetryAsync(_httpClient,
                    () => NewRequest(HttpMethod.Post, uri, null, info.HardwareId, body), cancellationToken);
                var content = await response.Content.ReadAsStringAsync(cancellationToken);

                if (content.TrimStart().StartsWith('<'))
                {
                    // No excerpt: this path can carry a freshly issued application key, and an
                    // HTML-shaped body is exactly the kind of thing a hostile or misconfigured
                    // responder could still wrap a real success payload inside.
                    _logger.LogError("Received an HTML response instead of JSON from {Host} ({Length} chars); the bridge may not be accessible at that address",
                        host, content.Length);
                    return HueResult<AuthenticationOutcome>.Failure(NotABridgeReason);
                }

                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Unexpected bridge response for {Operation}: {Shape}", "authenticate", DescribeShape(doc.RootElement, content.Length));
                    return HueResult<AuthenticationOutcome>.Failure(UnreadableResponseReason);
                }

                var first = doc.RootElement[0];
                var username = GetString(first, "success", "username");
                if (!string.IsNullOrEmpty(username))
                {
                    _bridgeIdsByHost[host] = info.HardwareId;
                    _logger.LogInformation("Successfully authenticated with bridge {HardwareId}", info.HardwareId);
                    return HueResult<AuthenticationOutcome>.Success(new AuthenticationOutcome(username, info.HardwareId));
                }

                if (first.TryGetProperty("error", out var error))
                {
                    var type = error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                    var description = GetString(error, "description");
                    // Only the extracted description/type, never the raw body: a later element in
                    // the same array could carry a real success/username if the bridge (or a
                    // man-in-the-middle) sent a mixed response, and the key must never reach a log.
                    _logger.LogWarning("Authentication failed - Type: {Type}, Description: {Error}", type, description);
                    return HueResult<AuthenticationOutcome>.Failure(type == 101
                        ? "The link button wasn't pressed. Press it on the bridge, then try again."
                        : $"The bridge refused the request: {description ?? "?"}.");
                }

                // Describe element [0] itself, not the whole array: that's the one that was
                // actually rejected, and the fact worth putting in a bug report.
                _logger.LogWarning("Unexpected bridge response for {Operation}: {Shape}", "authenticate", DescribeShape(first, content.Length));
                return HueResult<AuthenticationOutcome>.Failure(UnreadableResponseReason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse bridge response for {Operation}", "authenticate");
                return HueResult<AuthenticationOutcome>.Failure(UnreadableResponseReason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during authentication");
                return HueResult<AuthenticationOutcome>.Failure(DescribeThrownRequest(ex, host));
            }
        }

        /// <summary>
        /// The groups of the types the caller names (<c>room</c>, <c>zone</c>, <c>bridge_home</c>),
        /// read in that order, each with its grouped_light service. The bridge home is named
        /// "All Lights". Fails when any type's read fails.
        /// </summary>
        public virtual async Task<HueResult<IReadOnlyList<HueGroupResource>>> GetGroupsAsync(HueBridge bridge, IReadOnlyList<string> types, CancellationToken cancellationToken = default)
        {
            try
            {
                var groups = new List<HueGroupResource>();
                foreach (var type in types)
                {
                    var (data, reason) = await GetResourcesAsync(bridge, type, cancellationToken);
                    if (data == null)
                    {
                        return HueResult<IReadOnlyList<HueGroupResource>>.Failure(reason ?? UnhandledReason);
                    }

                    foreach (var item in data.Value.EnumerateArray())
                    {
                        var groupedLightId = FindService(item, "grouped_light");
                        var id = GetString(item, "id");
                        if (groupedLightId == null || id == null)
                        {
                            continue;
                        }

                        var name = type == "bridge_home" ? "All Lights" : (GetString(item, "metadata", "name") ?? type);
                        groups.Add(new HueGroupResource(id, groupedLightId, name, type));
                    }
                }

                return HueResult<IReadOnlyList<HueGroupResource>>.Success(groups);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting groups");
                return HueResult<IReadOnlyList<HueGroupResource>>.Failure(DescribeThrownRequest(ex, bridge.IpAddress));
            }
        }

        public virtual async Task<HueResult<IReadOnlyList<HueSceneResource>>> GetScenesAsync(HueBridge bridge, CancellationToken cancellationToken = default)
        {
            try
            {
                var (data, reason) = await GetResourcesAsync(bridge, "scene", cancellationToken);
                if (data == null)
                {
                    return HueResult<IReadOnlyList<HueSceneResource>>.Failure(reason ?? UnhandledReason);
                }

                var scenes = new List<HueSceneResource>();
                foreach (var item in data.Value.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    var groupId = GetString(item, "group", "rid");
                    if (id == null || groupId == null)
                    {
                        continue;
                    }

                    scenes.Add(new HueSceneResource(id, GetString(item, "metadata", "name") ?? id, groupId));
                }

                return HueResult<IReadOnlyList<HueSceneResource>>.Success(scenes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting scenes");
                return HueResult<IReadOnlyList<HueSceneResource>>.Failure(DescribeThrownRequest(ex, bridge.IpAddress));
            }
        }

        public virtual async Task<HueResult<IReadOnlyList<HueLightResource>>> GetLightsAsync(HueBridge bridge, CancellationToken cancellationToken = default)
        {
            try
            {
                var (data, reason) = await GetResourcesAsync(bridge, "light", cancellationToken);
                if (data == null)
                {
                    return HueResult<IReadOnlyList<HueLightResource>>.Failure(reason ?? UnhandledReason);
                }

                var lights = new List<HueLightResource>();
                foreach (var item in data.Value.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    if (id == null)
                    {
                        continue;
                    }

                    var on = item.TryGetProperty("on", out var onElement) && onElement.TryGetProperty("on", out var onValue) && onValue.ValueKind == JsonValueKind.True;
                    double? brightness = item.TryGetProperty("dimming", out var dimming) && dimming.TryGetProperty("brightness", out var b) && b.ValueKind == JsonValueKind.Number
                        ? b.GetDouble()
                        : null;
                    lights.Add(new HueLightResource(id, GetString(item, "metadata", "name") ?? id, on, brightness));
                }

                return HueResult<IReadOnlyList<HueLightResource>>.Success(lights);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lights");
                return HueResult<IReadOnlyList<HueLightResource>>.Failure(DescribeThrownRequest(ex, bridge.IpAddress));
            }
        }

        /// <summary>PUT /clip/v2/resource/grouped_light/{id}. Ok when the bridge reports no error.</summary>
        public virtual async Task<HueOutcome> SetGroupedLightAsync(HueBridge bridge, string groupedLightId, GroupedLightState state, CancellationToken cancellationToken = default)
        {
            if (!BridgeUri.IsValidSegment(groupedLightId))
            {
                _logger.LogWarning("Bridge {Host} {Field} '{Value}' is not a valid id", bridge.IpAddress, "grouped light", groupedLightId);
                return HueOutcome.Failure(UnhandledReason);
            }

            try
            {
                var (host, bridgeId, prepareReason) = await PrepareAsync(bridge, cancellationToken);
                if (host == null)
                {
                    return HueOutcome.Failure(prepareReason ?? UnhandledReason);
                }

                var uri = BridgeUri.Build(host, "clip", "v2", "resource", "grouped_light", groupedLightId);
                var response = await SendWithRetryAsync(_httpClient,
                    () => NewRequest(HttpMethod.Put, uri, bridge.Username, bridgeId, state.ToRequestBody()), cancellationToken);
                var (data, reason) = await ReadEnvelopeAsync(response, $"grouped_light {groupedLightId}", cancellationToken);
                return data != null ? HueOutcome.Success : HueOutcome.Failure(reason ?? UnhandledReason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting group state");
                return HueOutcome.Failure(DescribeThrownRequest(ex, bridge.IpAddress));
            }
        }

        /// <summary>PUT /clip/v2/resource/scene/{id} with recall.action = active and an optional transition.</summary>
        public virtual async Task<HueOutcome> RecallSceneAsync(HueBridge bridge, string sceneId, int? durationMs, CancellationToken cancellationToken = default)
        {
            if (!BridgeUri.IsValidSegment(sceneId))
            {
                _logger.LogWarning("Bridge {Host} {Field} '{Value}' is not a valid id", bridge.IpAddress, "scene", sceneId);
                return HueOutcome.Failure(UnhandledReason);
            }

            try
            {
                var (host, bridgeId, prepareReason) = await PrepareAsync(bridge, cancellationToken);
                if (host == null)
                {
                    return HueOutcome.Failure(prepareReason ?? UnhandledReason);
                }

                var recall = new Dictionary<string, object> { ["action"] = "active" };
                if (durationMs.HasValue)
                {
                    recall["duration"] = durationMs.Value;
                }

                var uri = BridgeUri.Build(host, "clip", "v2", "resource", "scene", sceneId);
                var response = await SendWithRetryAsync(_httpClient,
                    () => NewRequest(HttpMethod.Put, uri, bridge.Username, bridgeId, new { recall }), cancellationToken);
                var (data, reason) = await ReadEnvelopeAsync(response, $"scene {sceneId}", cancellationToken);
                return data != null ? HueOutcome.Success : HueOutcome.Failure(reason ?? UnhandledReason);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error activating scene {SceneId}", sceneId);
                return HueOutcome.Failure(DescribeThrownRequest(ex, bridge.IpAddress));
            }
        }

        // Retry once on transient connection failures (stale pooled connections to Hue bridge).
        // Never retries once cancellation has been requested: cancelling a request can itself
        // surface as an HttpRequestException wrapping an IOException rather than a clean
        // OperationCanceledException, and retrying that would spend an extra network attempt on a
        // call the caller has already given up on.
        //
        // Builds the request itself (rather than taking a built HttpRequestMessage) because an
        // HttpRequestMessage can only be sent once: a retry needs a fresh instance from
        // requestFactory, and every attempt -- first or retried -- keeps its own request in scope
        // long enough to read CertificateRejectionOption off it if the send throws, tagging the
        // exception via CertificateRejectedDataKey before it propagates. That is what lets a catch
        // several frames away (DescribeThrownRequest) tell a rejected certificate apart from a
        // connection that simply never answered.
        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient client, Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
        {
            var request = requestFactory();
            try
            {
                return await client.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                TagCertificateRejection(request, ex);

                if (ex is HttpRequestException { InnerException: IOException } && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug("Connection reset, retrying request");
                    request = requestFactory();
                    try
                    {
                        return await client.SendAsync(request, cancellationToken);
                    }
                    catch (Exception retryEx)
                    {
                        TagCertificateRejection(request, retryEx);
                        throw;
                    }
                }

                throw;
            }
        }

        private static void TagCertificateRejection(HttpRequestMessage request, Exception ex)
        {
            if (request.Options.TryGetValue(CertificateRejectionOption, out var rejected) && rejected)
            {
                ex.Data[CertificateRejectedDataKey] = true;
            }
        }

        /// <summary>mDNS first; the Philips cloud endpoint only when no bridge answers locally (e.g. Jellyfin in Docker bridge networking).</summary>
        public virtual async Task<List<HueBridgeDiscovery>> DiscoverBridgesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Discovering Hue bridges...");

                if (_mdns != null)
                {
                    var local = await _mdns.DiscoverAsync(MdnsWindow, cancellationToken);
                    if (local.Count > 0)
                    {
                        foreach (var bridge in local)
                        {
                            _logger.LogInformation("Found bridge {Id} at {Ip}", bridge.Id, bridge.InternalIpAddress);
                        }

                        _logger.LogInformation("Found {Count} Hue bridge(s) via mDNS", local.Count);
                        return local;
                    }

                    _logger.LogInformation("No Hue bridge answered mDNS; trying cloud discovery");
                }

                // Use Philips Hue discovery endpoint
                var response = await _httpClient.GetAsync("https://discovery.meethue.com/", cancellationToken);
                response.EnsureSuccessStatusCode();

                var bridges = await response.Content.ReadFromJsonAsync<List<HueBridgeDiscovery>>(cancellationToken);

                if (bridges != null)
                {
                    // Normalize all bridge IPs
                    foreach (var bridge in bridges)
                    {
                        bridge.InternalIpAddress = BridgeUri.StripSchemeAndPath(bridge.InternalIpAddress);
                        _logger.LogInformation("Found bridge {Id} at {Ip}", bridge.Id, bridge.InternalIpAddress);
                    }
                }

                _logger.LogInformation("Found {Count} Hue bridge(s)", bridges?.Count ?? 0);
                return bridges ?? new List<HueBridgeDiscovery>();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discovering Hue bridges");
                return new List<HueBridgeDiscovery>();
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            if (!ReferenceEquals(_bridgeInfoClient, _httpClient))
            {
                _bridgeInfoClient.Dispose();
            }
        }
    }

}
