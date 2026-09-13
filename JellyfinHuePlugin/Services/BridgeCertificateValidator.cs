using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace JellyfinHuePlugin.Services
{
    /// <summary>
    /// Decides whether a bridge's TLS certificate is acceptable: it must chain to Signify's
    /// root-bridge CA (or the one extra root named by the environment) and, when the bridge
    /// id is known, carry that id as its subject common name. Hostnames are never checked:
    /// bridges are addressed by IP and their certificates name the bridge id.
    /// </summary>
    internal static class BridgeCertificateValidator
    {
        /// <summary>Signify's private root for Hue bridges (C=NL, O=Philips Hue, CN=root-bridge; ECDSA P-256; valid to 2038).</summary>
        internal const string SignifyRootPem = @"-----BEGIN CERTIFICATE-----
MIICMjCCAdigAwIBAgIUO7FSLbaxikuXAljzVaurLXWmFw4wCgYIKoZIzj0EAwIw
OTELMAkGA1UEBhMCTkwxFDASBgNVBAoMC1BoaWxpcHMgSHVlMRQwEgYDVQQDDAty
b290LWJyaWRnZTAiGA8yMDE3MDEwMTAwMDAwMFoYDzIwMzgwMTE5MDMxNDA3WjA5
MQswCQYDVQQGEwJOTDEUMBIGA1UECgwLUGhpbGlwcyBIdWUxFDASBgNVBAMMC3Jv
b3QtYnJpZGdlMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEjNw2tx2AplOf9x86
aTdvEcL1FU65QDxziKvBpW9XXSIcibAeQiKxegpq8Exbr9v6LBnYbna2VcaK0G22
jOKkTqOBuTCBtjAPBgNVHRMBAf8EBTADAQH/MA4GA1UdDwEB/wQEAwIBhjAdBgNV
HQ4EFgQUZ2ONTFrDT6o8ItRnKfqWKnHFGmQwdAYDVR0jBG0wa4AUZ2ONTFrDT6o8
ItRnKfqWKnHFGmShPaQ7MDkxCzAJBgNVBAYTAk5MMRQwEgYDVQQKDAtQaGlsaXBz
IEh1ZTEUMBIGA1UEAwwLcm9vdC1icmlkZ2WCFDuxUi22sYpLlwJY81Wrqy11phcO
MAoGCCqGSM49BAMCA0gAMEUCIEBYYEOsa07TH7E5MJnGw557lVkORgit2Rm1h3B2
sFgDAiEA1Fj/C3AN5psFMjo0//mrQebo0eKd3aWRx+pQY08mk48=
-----END CERTIFICATE-----";

        internal enum Verdict
        {
            Trusted,
            UntrustedChain,
            SubjectMismatch
        }

        /// <summary>The Signify root plus, when a readable file is named, one extra root. A bad path is logged and skipped.</summary>
        internal static IReadOnlyList<X509Certificate2> LoadRoots(string? extraRootPemPath, ILogger logger)
        {
            var roots = new List<X509Certificate2> { X509Certificate2.CreateFromPem(SignifyRootPem) };

            if (!string.IsNullOrWhiteSpace(extraRootPemPath))
            {
                try
                {
                    // Cert-only PEM: CreateFromPem needs no private key, unlike CreateFromPemFile.
                    var extra = X509Certificate2.CreateFromPem(System.IO.File.ReadAllText(extraRootPemPath));
                    roots.Add(extra);
                    logger.LogInformation("Trusting extra bridge root {Subject} from {Path}", extra.Subject, extraRootPemPath);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not load extra bridge root from {Path}", extraRootPemPath);
                }
            }

            return roots;
        }

        /// <summary>
        /// Builds a chain from the presented certificate with custom root trust (the given roots,
        /// no revocation check) and, when expectedBridgeId is not null, compares the subject CN
        /// with it case-insensitively.
        /// </summary>
        internal static Verdict Validate(X509Certificate2 certificate, string? expectedBridgeId, IReadOnlyList<X509Certificate2> roots)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            foreach (var root in roots)
            {
                chain.ChainPolicy.CustomTrustStore.Add(root);
            }

            if (!chain.Build(certificate))
            {
                return Verdict.UntrustedChain;
            }

            if (expectedBridgeId == null)
            {
                return Verdict.Trusted;
            }

            return string.Equals(SubjectCommonName(certificate), expectedBridgeId, StringComparison.OrdinalIgnoreCase)
                ? Verdict.Trusted
                : Verdict.SubjectMismatch;
        }

        internal static string SubjectCommonName(X509Certificate2 certificate)
            => certificate.GetNameInfo(X509NameType.SimpleName, false);
    }
}
