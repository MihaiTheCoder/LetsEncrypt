﻿// Copyright (c) Nate McMaster.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using McMaster.AspNetCore.LetsEncrypt.Internal.IO;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Asn1;

namespace McMaster.AspNetCore.LetsEncrypt.Internal
{
    /// <summary>
    /// Implements https://tools.ietf.org/html/rfc8737. This validates domain ownership by responding to
    /// TLS requests using a special self-signed certificate.
    /// </summary>
    internal class TlsAlpnChallengeResponder
    {
        // See RFC8737 section 6.1
        private static readonly Oid s_acmeExtensionOid = new Oid("1.3.6.1.5.5.7.1.31");
        private const string PROTOCOL_NAME = "acme-tls/1";
#if NETCOREAPP3_0
        private static readonly SslApplicationProtocol s_acmeTlsProtocol = new SslApplicationProtocol(PROTOCOL_NAME);
#endif
        private readonly IClock _clock;
        private readonly ILogger<TlsAlpnChallengeResponder> _logger;
        private readonly CertificateSelector _certificateSelector;
        private int _openChallenges = 0;

        public TlsAlpnChallengeResponder(
            CertificateSelector certificateSelector,
            IClock clock,
            ILogger<TlsAlpnChallengeResponder> logger)
        {
            _certificateSelector = certificateSelector ?? throw new ArgumentNullException(nameof(certificateSelector));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

#if NETSTANDARD2_0

        // TLS ALPN not supported on .NET Standard. Requires .NET Core 3
        public bool IsEnabled => false;

        public void PrepareChallengeCert(string domainName, string keyAuthorization)
        {
            throw new PlatformNotSupportedException();
        }

#elif NETCOREAPP3_0
        public bool IsEnabled => true;

        public void OnSslAuthenticate(ConnectionContext context, SslServerAuthenticationOptions options)
        {
            if (_openChallenges > 0)
            {
                options.ApplicationProtocols.Add(s_acmeTlsProtocol);
            }
        }

        /// <summary>
        /// Generates a self-signed cert per RFC 8737 spec.
        /// </summary>
        /// <param name="domainName">the domain name</param>
        /// <param name="keyAuthorization">token to be included in self-signed cert</param>
        public void PrepareChallengeCert(string domainName, string keyAuthorization)
        {
            _logger.LogTrace("Creating ALPN self-signed cert for {domainName} and key authz {keyAuth}",
                domainName, keyAuthorization);

            var key = RSA.Create(2048);
            var csr = new CertificateRequest(
                "CN=" + domainName,
                key,
                HashAlgorithmName.SHA512,
                RSASignaturePadding.Pkcs1);

            /*
            RFC 8737 Section 3

            The client prepares for validation by constructing a self-signed
            certificate that MUST contain an acmeIdentifier extension and a
            subjectAlternativeName extension [RFC5280].  The
            subjectAlternativeName extension MUST contain a single dNSName entry
            where the value is the domain name being validated.  The
            acmeIdentifier extension MUST contain the SHA-256 digest [FIPS180-4]
            of the key authorization [RFC8555] for the challenge.  The
            acmeIdentifier extension MUST be critical so that the certificate
            isn't inadvertently used by non-ACME software.
            */

            // adds subjectAlternativeName
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(domainName);
            csr.CertificateExtensions.Add(sanBuilder.Build());

            // adds acmeIdentifier extension (critical = true)
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyAuthorization));
            var extensionData = new DerOctetString(hash).GetDerEncoded();
            var acmeIdentifierExtension = new X509Extension(s_acmeExtensionOid, extensionData, critical: true);
            csr.CertificateExtensions.Add(acmeIdentifierExtension);

            // This cert is ephemeral and does not need to be stored for reuse later
            var cert = csr.CreateSelfSigned(_clock.Now.AddDays(-1), _clock.Now.AddDays(1));

            Interlocked.Increment(ref _openChallenges);
            _certificateSelector.AddChallengeCert(cert);
        }
#else
#error Update TFMs
#endif

        public void DiscardChallenge(string domainName)
        {
            Interlocked.Decrement(ref _openChallenges);

            _logger.LogTrace("Clearing ALPN cert for {domainName}", domainName);

            _certificateSelector.ClearChallengeCert(domainName);
        }
    }
}
