using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using JellyfinHuePlugin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyfinHuePlugin.Tests.Services
{
    /// <summary>
    /// A throwaway CA and leaves are generated per test class, shaped like the real ones:
    /// ECDSA P-256, subject "CN=&lt;bridge id&gt;, O=Philips Hue, C=NL".
    /// </summary>
    public class BridgeCertificateValidatorTests : IDisposable
    {
        private const string BridgeId = "001788fffe123456";

        private readonly X509Certificate2 _root;
        private readonly X509Certificate2 _otherRoot;
        private readonly X509Certificate2 _leaf;

        public BridgeCertificateValidatorTests()
        {
            _root = MakeRoot("test-root-bridge");
            _otherRoot = MakeRoot("someone-else");
            _leaf = MakeLeaf(_root, BridgeId);
        }

        public void Dispose()
        {
            _root.Dispose();
            _otherRoot.Dispose();
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

        [Fact]
        public void ChainAndSubjectMatch_IsTrusted()
        {
            BridgeCertificateValidator.Validate(_leaf, BridgeId, Roots).Should().Be(BridgeCertificateValidator.Verdict.Trusted);
        }

        [Fact]
        public void ExpectedIdInUpperCase_IsTrusted()
        {
            BridgeCertificateValidator.Validate(_leaf, BridgeId.ToUpperInvariant(), Roots).Should().Be(BridgeCertificateValidator.Verdict.Trusted);
        }

        [Fact]
        public void SubjectDiffers_IsSubjectMismatch()
        {
            BridgeCertificateValidator.Validate(_leaf, "001788fffe000000", Roots).Should().Be(BridgeCertificateValidator.Verdict.SubjectMismatch);
        }

        [Fact]
        public void NoExpectedId_IsTrustedOnChainAlone()
        {
            BridgeCertificateValidator.Validate(_leaf, null, Roots).Should().Be(BridgeCertificateValidator.Verdict.Trusted);
        }

        [Fact]
        public void LeafFromAnotherCa_IsUntrustedChain()
        {
            using var stranger = MakeLeaf(_otherRoot, BridgeId);

            BridgeCertificateValidator.Validate(stranger, BridgeId, Roots).Should().Be(BridgeCertificateValidator.Verdict.UntrustedChain);
        }

        [Fact]
        public void SelfSignedLeaf_IsUntrustedChain()
        {
            using var selfSigned = MakeRoot(BridgeId);

            BridgeCertificateValidator.Validate(selfSigned, BridgeId, Roots).Should().Be(BridgeCertificateValidator.Verdict.UntrustedChain);
        }

        [Fact]
        public void ExtraRoot_IsTrusted()
        {
            using var stranger = MakeLeaf(_otherRoot, BridgeId);

            BridgeCertificateValidator.Validate(stranger, BridgeId, new[] { _root, _otherRoot }).Should().Be(BridgeCertificateValidator.Verdict.Trusted);
        }

        [Fact]
        public void SignifyRootPem_ParsesWithTheExpectedSubject()
        {
            using var root = X509Certificate2.CreateFromPem(BridgeCertificateValidator.SignifyRootPem);

            root.Subject.Should().Be("CN=root-bridge, O=Philips Hue, C=NL");
            root.Issuer.Should().Be(root.Subject);
            root.NotAfter.Year.Should().Be(2038);
        }

        [Fact]
        public void LoadRoots_WithoutExtraPath_ReturnsSignifyRootOnly()
        {
            var roots = BridgeCertificateValidator.LoadRoots(null, NullLogger.Instance);

            roots.Should().ContainSingle().Which.Subject.Should().Be("CN=root-bridge, O=Philips Hue, C=NL");
        }

        [Fact]
        public void LoadRoots_WithMissingFile_SkipsTheExtraRoot()
        {
            var roots = BridgeCertificateValidator.LoadRoots(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".pem"), NullLogger.Instance);

            roots.Should().HaveCount(1);
        }

        [Fact]
        public void LoadRoots_WithPemFile_AddsTheExtraRoot()
        {
            var path = Path.Combine(Path.GetTempPath(), "hue-test-root-" + Guid.NewGuid() + ".pem");
            File.WriteAllText(path, _otherRoot.ExportCertificatePem());
            try
            {
                var roots = BridgeCertificateValidator.LoadRoots(path, NullLogger.Instance);

                roots.Should().HaveCount(2);
                roots[1].Subject.Should().Be(_otherRoot.Subject);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void SubjectCommonName_ReturnsTheCn()
        {
            BridgeCertificateValidator.SubjectCommonName(_leaf).Should().Be(BridgeId);
        }
    }
}
