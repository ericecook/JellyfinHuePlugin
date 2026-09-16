using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using FluentAssertions;
using JellyfinHuePlugin.Configuration;
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

        // chainOnly: true -- the bridge-info handler's mode, routed through the same function.

        [Fact]
        public void ChainOnly_TrustedChain_AcceptsWithNoOptionAtAll()
        {
            HueService.ShouldAcceptBridgeCertificate(RequestWithoutOption(), _leaf, SslPolicyErrors.RemoteCertificateChainErrors, Roots, NullLogger.Instance, chainOnly: true)
                .Should().BeTrue();
        }

        [Fact]
        public void ChainOnly_IgnoresAPinnedIdEvenWhenPresentAndMismatched()
        {
            // If this id were honored, the mismatch would reject; chainOnly must ignore it entirely.
            HueService.ShouldAcceptBridgeCertificate(RequestWithOption("001788fffe000000"), _leaf, SslPolicyErrors.None, Roots, NullLogger.Instance, chainOnly: true)
                .Should().BeTrue();
        }

        [Fact]
        public void ChainOnly_OptionPresentButEmpty_StillAccepts()
        {
            // Pins the distinction the merge must preserve: this is exactly what NewRequest stores
            // for the one call that runs through the bridge-info handler (expectedBridgeId ?? "").
            // Under the main handler's mode (chainOnly: false) the identical input is rejected --
            // see OptionPresentButEmpty_Rejects above -- but chain-only mode must not treat an empty
            // pinned id as "a pinned request lost its id", because there was never anything to pin.
            HueService.ShouldAcceptBridgeCertificate(RequestWithOption(string.Empty), _leaf, SslPolicyErrors.None, Roots, NullLogger.Instance, chainOnly: true)
                .Should().BeTrue();
        }

        [Fact]
        public void ChainOnly_UntrustedChain_StillRejects()
        {
            using var otherRoot = MakeRoot("someone-else");
            using var stranger = MakeLeaf(otherRoot, BridgeId);

            HueService.ShouldAcceptBridgeCertificate(RequestWithoutOption(), stranger, SslPolicyErrors.None, Roots, NullLogger.Instance, chainOnly: true)
                .Should().BeFalse();
        }

        [Fact]
        public void ChainOnly_CertificateMissing_Rejects()
        {
            HueService.ShouldAcceptBridgeCertificate(RequestWithoutOption(), null, SslPolicyErrors.None, Roots, NullLogger.Instance, chainOnly: true)
                .Should().BeFalse();
        }

        /// <summary>
        /// Spec Fact 9, proven end to end through the production pipeline rather than by calling
        /// ShouldAcceptBridgeCertificate directly: a loopback SslStream server presents a
        /// self-signed certificate no root in HueService trusts, over a real TLS handshake, to a
        /// HueService built with its real (non-mock) constructor -- the same HttpClientHandler and
        /// ServerCertificateCustomValidationCallback a deployed plugin uses. If SendWithRetryAsync's
        /// verdict-tagging (CertificateRejectionOption -> Exception.Data -> DescribeThrownRequest)
        /// did not actually work, this would surface as the generic "didn't answer" reason instead.
        /// </summary>
        [Fact]
        public async Task PinnedRequest_RealHandshakeRejectsTheCertificate_ReasonNamesTheStoredCertificate()
        {
            using var serverCert = MakeSelfSigned("not-a-trusted-hue-root");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var serverTask = Task.Run(async () =>
            {
                using var tcpClient = await listener.AcceptTcpClientAsync();
                using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false);
                try
                {
                    await sslStream.AuthenticateAsServerAsync(serverCert, false, SslProtocols.None, false);
                }
                catch (Exception)
                {
                    // Expected: the client aborts the handshake once its callback rejects the cert.
                }
            });

            var mdns = new MdnsBridgeDiscovery(NullLogger<MdnsBridgeDiscovery>.Instance);
            using var service = new HueService(NullLogger<HueService>.Instance, mdns);
            var bridge = new HueBridge
            {
                Id = "b1",
                Name = "Loopback",
                IpAddress = $"127.0.0.1:{port}",
                Username = "0123456789012345678901234567890123456789",
                HardwareId = BridgeId // already known, so PrepareAsync skips straight to the pinned request
            };

            var result = await service.GetGroupsAsync(bridge, new[] { "room" });

            await serverTask;
            result.Ok.Should().BeFalse();
            result.Reason.Should().Be("The bridge's certificate doesn't match the stored one. Authenticate again to store the new one.");
        }

        private static X509Certificate2 MakeSelfSigned(string cn)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        }
    }
}
