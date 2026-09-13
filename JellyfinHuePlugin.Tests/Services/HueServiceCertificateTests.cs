using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    /// <summary>
    /// Exercises HueService.ShouldAcceptBridgeCertificate directly -- the decision extracted from
    /// the main client's ServerCertificateCustomValidationCallback lambda so it can be unit tested
    /// without going through an actual TLS handshake (which is how both the null-sentinel bug and
    /// the connection-pooling bug previously escaped coverage). Certs are minted the same way as
    /// BridgeCertificateValidatorTests; the minting helpers are duplicated here rather than shared,
    /// to keep the two test files independent.
    /// </summary>
    public class HueServiceCertificateTests : IDisposable
    {
        private const string BridgeId = "001788fffe123456";

        private readonly X509Certificate2 _root;
        private readonly X509Certificate2 _leaf;

        public HueServiceCertificateTests()
        {
            _root = MakeRoot("test-root-bridge");
            _leaf = MakeLeaf(_root, BridgeId);
        }

        public void Dispose()
        {
            _root.Dispose();
            _leaf.Dispose();
        }

        private static X509Certificate2 MakeRoot(string cn)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={cn}, O=Philips Hue, C=NL", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        }

        private static X509Certificate2 MakeLeaf(X509Certificate2 issuer, string cn)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={cn}, O=Philips Hue, C=NL", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), serial);
        }

        private IReadOnlyList<X509Certificate2> Roots => new[] { _root };

        private static HttpRequestMessage RequestWithoutOption() =>
            new(HttpMethod.Get, "https://discovery.meethue.com/");

        private static HttpRequestMessage RequestWithOption(string expectedBridgeId)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://192.168.1.50/clip/v2/resource/light");
            request.Options.Set(HueService.ExpectedBridgeIdOption, expectedBridgeId);
            return request;
        }

        [Fact]
        public void OptionAbsent_NoTlsErrors_Accepts()
        {
            HueService.ShouldAcceptBridgeCertificate(RequestWithoutOption(), _leaf, SslPolicyErrors.None, Roots, NullLogger.Instance)
                .Should().BeTrue();
        }

        [Fact]
        public void OptionAbsent_TlsError_Rejects()
        {
            HueService.ShouldAcceptBridgeCertificate(RequestWithoutOption(), _leaf, SslPolicyErrors.RemoteCertificateChainErrors, Roots, NullLogger.Instance)
                .Should().BeFalse();
        }

        [Fact]
        public void OptionPresentAndMatches_AcceptsRegardlessOfSystemTlsErrors()
        {
            // Deliberately a "bad" system errors value: once the option is present, the chain and
            // subject are judged solely by BridgeCertificateValidator against our own roots, not by
            // the OS's opinion of the certificate.
            HueService.ShouldAcceptBridgeCertificate(RequestWithOption(BridgeId), _leaf, SslPolicyErrors.RemoteCertificateChainErrors, Roots, NullLogger.Instance)
                .Should().BeTrue();
        }

        [Fact]
        public void OptionPresentButSubjectMismatches_Rejects()
        {
            HueService.ShouldAcceptBridgeCertificate(RequestWithOption("001788fffe000000"), _leaf, SslPolicyErrors.None, Roots, NullLogger.Instance)
                .Should().BeFalse();
        }

        [Fact]
        public void OptionPresentButEmpty_Rejects()
        {
            // This should be unreachable in real use (the one caller that had no id yet now uses a
            // separate client that ignores the option entirely), but the callback must fail closed
            // rather than silently falling back to chain-only trust if it ever happens.
            HueService.ShouldAcceptBridgeCertificate(RequestWithOption(string.Empty), _leaf, SslPolicyErrors.None, Roots, NullLogger.Instance)
                .Should().BeFalse();
        }

        [Fact]
        public void OptionPresentButCertificateMissing_Rejects()
        {
            HueService.ShouldAcceptBridgeCertificate(RequestWithOption(BridgeId), null, SslPolicyErrors.None, Roots, NullLogger.Instance)
                .Should().BeFalse();
        }
    }
}
