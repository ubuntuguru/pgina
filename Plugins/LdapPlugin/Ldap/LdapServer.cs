﻿/*
	Copyright (c) 2011, pGina Team
	All rights reserved.

	Redistribution and use in source and binary forms, with or without
	modification, are permitted provided that the following conditions are met:
		* Redistributions of source code must retain the above copyright
		  notice, this list of conditions and the following disclaimer.
		* Redistributions in binary form must reproduce the above copyright
		  notice, this list of conditions and the following disclaimer in the
		  documentation and/or other materials provided with the distribution.
		* Neither the name of the pGina Team nor the names of its contributors 
		  may be used to endorse or promote products derived from this software without 
		  specific prior written permission.

	THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
	ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
	WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
	DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY
	DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
	(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
	LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
	ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
	(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
	SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.DirectoryServices.Protocols;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;

namespace pGina.Plugin.Ldap
{
    public class LdapServer : IDisposable
    {
        private log4net.ILog m_logger = log4net.LogManager.GetLogger("LdapServer");
        
        /// <summary>
        /// The connection object.
        /// </summary>
        private LdapConnection m_conn = null;

        /// <summary>
        /// The server identification (host,port)
        /// </summary>
        private LdapDirectoryIdentifier m_serverIdentifier;

        /// <summary>
        /// Whether or not to use SSL
        /// </summary>
        private bool m_useSsl;

        /// <summary>
        /// Whether or not to verify the SSL certificate
        /// </summary>
        private bool m_verifyCert;

        /// <summary>
        /// The SSL certificate to verify against (if required)
        /// </summary>
        private X509Certificate2 m_cert;

        /// <summary>
        /// The number of seconds to wait for a connection before giving up.
        /// </summary>
        public int Timeout { get; set; }

        /// <summary>
        /// Create a connection to the LDAP server at the given host and port.
        /// </summary>
        /// <param name="host">The FQDN or IP of the LDAP host.</param>
        /// <param name="port">The port number of the LDAP host.</param>
        /// <param name="useSsl">Whether or not to use SSL.</param>
        /// <param name="verifyCert">Whether or not to verify the server certificate.</param>
        /// <param name="cert">A certificate to verify against the server's certificate (can be null).  If this is null,
        /// and verifyCert is true, then the server's cert is verified with the Windows certificate store.</param>
        public LdapServer(string[] hosts, int port, bool useSsl, bool verifyCert, X509Certificate2 cert) 
        {
            m_logger.DebugFormat("Initializing LdapServer host(s): [{0}], port: {1}, useSSL = {2}, verifyCert = {3}",
                string.Join(", ", hosts), port, useSsl, verifyCert);
            m_conn = null;
            m_serverIdentifier = new LdapDirectoryIdentifier(hosts, port, false, false);
            m_useSsl = useSsl;
            m_verifyCert = verifyCert;
            m_cert = cert;
        }

        public void Connect(int timeout)
        {
            // Are we re-connecting?  If so, close the previous connection.
            if (m_conn != null)
            {
                this.Close();
            }

            m_conn = new LdapConnection(m_serverIdentifier);
            m_conn.Timeout = new System.TimeSpan(0,0,timeout);
            m_logger.DebugFormat("Timeout set to {0} seconds.", timeout);
            m_conn.SessionOptions.ProtocolVersion = 3;
            m_conn.SessionOptions.SecureSocketLayer = m_useSsl;
            if( m_useSsl )
                m_conn.SessionOptions.VerifyServerCertificate = this.VerifyCert;
        }

        /// <summary>
        /// This is the verify certificate callback method used when initially binding to the
        /// LDAP server.  This manages all certificate validation.
        /// </summary>
        /// <param name="conn">The LDAP connection.</param>
        /// <param name="cert">The server's certificate</param>
        /// <returns>true if verification succeeds, false otherwise.</returns>
        private bool VerifyCert(LdapConnection conn, X509Certificate cert)
        {
            m_logger.Debug("VerifyCert(...)");
            m_logger.DebugFormat("Verifying certificate from host: {0}", conn.SessionOptions.HostName);

            // Convert to X509Certificate2
            X509Certificate2 serverCert = new X509Certificate2(cert);

            // If we don't need to verify the cert, the verification succeeds
            if (!m_verifyCert)
            {
                m_logger.Debug("Server certificate accepted without verification.");
                return true;
            }

            // If the certificate is null, then we verify against the machine's/user's certificate store
            if (m_cert == null)
            {
                m_logger.Debug("Verifying server cert with Windows store.");
                // Use default policy
                X509ChainPolicy policy = new X509ChainPolicy();

                // Validation against the user's certificate store
                X509CertificateValidator validator = X509CertificateValidator.CreateChainTrustValidator(false, policy);
                try
                {
                    validator.Validate(serverCert);

                    // If we get here, validation succeeded.
                    m_logger.Debug("Server certificate verification succeeded.");
                    return true;
                }
                catch (SecurityTokenValidationException)
                {
                    m_logger.Debug("Server certificate validation failed.");
                    return false;
                }
            }
            else
            {
                m_logger.Debug("Validating server certificate with provided certificate.");

                // Verify against the provided cert by comparing the thumbprint
                bool result = m_cert.Thumbprint == serverCert.Thumbprint;
                if (result) m_logger.Debug("Server certificate validated.");
                else m_logger.Debug("Server certificate validation failed.");
                return result;
            }
        }

        /// <summary>
        /// Tries to bind to the server anonymously.  Throws LdapException if the
        /// bind fails.
        /// </summary>
        public void Bind()
        {
            if (m_conn == null)
                throw new LdapException("Bind attempted when server is not connected.");

            m_logger.DebugFormat("Attempting anonymous bind", m_conn.SessionOptions.HostName);

            m_conn.AuthType = AuthType.Anonymous;
            m_conn.Credential = null;
            try
            {
                m_conn.Bind();
                m_logger.DebugFormat("Successful bind to {0}", m_conn.SessionOptions.HostName);
            }
            catch (LdapException e)
            {
                m_logger.ErrorFormat("LdapException: {0} {1}", e.Message, e.ServerErrorMessage);
                throw e;
            }
            catch (InvalidOperationException e)
            {
                // This shouldn't happen, but log it and re-throw
                m_logger.ErrorFormat("InvalidOperationException: {0}", e.Message);
                throw e;
            }
        }

        /// <summary>
        /// Try to bind to the LDAP server with the given credentials.  This uses
        /// basic authentication.  Throws LdapException if the bind fails.
        /// </summary>
        /// <param name="creds">The credentials to use when binding.</param>
        public void Bind(NetworkCredential creds)
        {
            if (m_conn == null)
                throw new LdapException("Bind attempted when server is not connected.");

            m_logger.DebugFormat("Attempting bind as {0}", creds.UserName);

            m_conn.AuthType = AuthType.Basic;

            try
            {
                m_conn.Bind(creds);
                m_logger.DebugFormat("Successful bind to {0} as {1}", m_conn.SessionOptions.HostName, creds.UserName);
            }
            catch (LdapException e)
            {
                m_logger.ErrorFormat("LdapException: {0} {1}", e.Message, e.ServerErrorMessage);
                throw e;
            }
            catch (InvalidOperationException e)
            {
                // This shouldn't happen, but log it and re-throw
                m_logger.ErrorFormat("InvalidOperationException: {0}", e.Message);
                throw e;
            }
        }

        public void Close()
        {
            if (m_conn != null)
            {
                m_logger.DebugFormat("Closing LDAP connection to {0}.", m_conn.SessionOptions.HostName);
                m_conn.Dispose();
                m_conn = null;
            }
        }

        /// <summary>
        /// Does a search in the subtree at searchBase, using the filter provided and 
        /// returns the DN of the first match.
        /// </summary>
        /// <param name="searchBase">The DN of the root of the subtree for the search (search context).</param>
        /// <param name="filter">The search filter.</param>
        /// <returns>The DN of the first match, or null if no matches are found.</returns>
        public string FindFirstDN(string searchBase, string filter)
        {
            SearchRequest req = new SearchRequest(searchBase, filter, System.DirectoryServices.Protocols.SearchScope.Subtree, null);
            SearchResponse resp = (SearchResponse)m_conn.SendRequest(req);

            if (resp.Entries.Count > 0)
            {
                return resp.Entries[0].DistinguishedName;
            }

            return null;
        }

        public void Dispose()
        {
            this.Close();
        }
    }
}
