/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of LocalController <https://github.com/OpenChargingCloud/LocalController>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What a local controller says of itself to the dialling it shares with
    /// every node that dials a CSMS: that it is a local controller, that it
    /// speaks OCPP 2.1 upwards and no OCPP 1.6, where its connections, its
    /// client certificates and its SSH key are kept, and what it says of a
    /// line beyond what a charging station says - its networking mode, its
    /// pings and timeouts, its versions of TLS.
    /// </summary>
    /// <remarks>
    /// The line up to the CSMS is a connection of the store, as a charging
    /// station's are: several of them, the next one where one cannot be
    /// reached. What the section "csms" of the configuration file still
    /// says is what a connection does not: whether this controller reports
    /// to a CSMS at all, and how every line of it is kept.
    /// </remarks>
    public partial class LocalController
    {

        #region (static) Constructor

        /// <summary>
        /// What the connections of this process say of the node they are
        /// the connections of: it is a local controller.
        /// </summary>
        static LocalController()
        {
            NodeWording.Is("local controller");
        }

        #endregion

        #region Node16 / Node21

        /// <summary>
        /// A local controller speaks no OCPP 1.6 upwards.
        /// </summary>
        protected override OCPPv1_6.NetworkingNode.AOCPPNetworkingNode? Node16
            => null;

        /// <summary>
        /// The node that speaks OCPP 2.1 for this controller.
        /// </summary>
        protected override OCPPv2_1.NetworkingNode.AOCPPNetworkingNode Node21
            => lc01;

        #endregion

        #region ConnectionsCertificateUsage

        /// <summary>
        /// The certificate of a CSMS is judged for the use this controller
        /// has always kept its roots for: "csms".
        /// </summary>
        public override String ConnectionsCertificateUsage
            => CSMSService;

        #endregion

        #region (private) BuildConnectionStores()

        /// <summary>
        /// The stores of what this controller dials - the connections and
        /// their credentials, the keys and certificates it holds up, the key
        /// it signs in with over SSH - beside the configuration file, and
        /// how it believes the servers of its connections by their key pins.
        /// </summary>
        private void BuildConnectionStores()
        {

            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ConfigFile.Path)) ?? ".";

            ClientCertificates = new ClientCertificateStore(
                                     System.IO.Path.Combine(directory, ClientCertificateStore.DefaultDirectoryName),
                                     TimeProvider
                                 );

            ClientCertificates.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "certificates");

            Connections = new ConnectionStore(
                              System.IO.Path.Combine(directory, ConnectionStore.DefaultDirectoryName),
                              TimeProvider
                          );

            keyPinning       = NewKeyPinning();
            tlsClientSource  = NewTLSClientSource();

            Connections.KnownCertificateIds   = () => ClientCertificates.Entries.Select(entry => entry.Id);
            Connections.OnNotice             += (level, message) => Log.Log(level, message, "ocpp", "connections");

            // After every change the store wrote: the device model first, so
            // that a connection dialled again has its TLSClientController by
            // then, and then what is dialled.
            Connections.OnConnectionsChanged += () => FollowTLSClientComponents(lc01);
            Connections.OnConnectionsChanged += () => {
                                                    if (csmsSettings.Enabled == true && started)
                                                        _ = ApplyConnectionChanges();
                                                };

            SshKey = new SshClientKey(
                         System.IO.Path.Combine(Connections.Path, SshClientKey.DirectoryName),
                         $"localcontroller@{Environment.MachineName}"
                     );

            SshKey.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "connections", "ssh", "security");

        }

        #endregion

        #region (private) TakeOverTheLineOfBefore(Written, Directory)

        /// <summary>
        /// A controller configured before its lines were connections of the
        /// store has one line in the section "csms" of its configuration
        /// file, its credentials in csms-credentials.json and how it
        /// believes its CSMS in csms-keypins.json. Once, where the store has
        /// no connection yet, they become a connection of the store, with
        /// what it proves itself with and whom it believes.
        /// </summary>
        /// <remarks>
        /// A connection names one set of credentials: a controller that had
        /// a password and a one-time token signs in with the password, and
        /// keeps the token as credentials of their own, to be chosen instead.
        ///
        /// A key pin that was learned and not confirmed yet is not taken
        /// over: the connection learns it again on its first handshake, and
        /// is on probation from then - which it was.
        ///
        /// The two files are put away once what they held is written: the
        /// credentials removed, because nothing should keep a secret twice,
        /// the key pins under another name, for whoever wants to look.
        /// </remarks>
        private void TakeOverTheLineOfBefore(CSMSConfiguration?  Written,
                                             String              Directory)
        {

            if (Connections.Connections.Count > 0)
                return;

            var credentialsFile  = System.IO.Path.Combine(Directory, CSMSCredentials.DefaultFileName);
            var keyPinsFile      = System.IO.Path.Combine(Directory, CSMSKeyPins.    DefaultFileName);
            var url              = Written?.URL?.Trim() is { Length: > 0 } address ? address : null;

            if (url is null && !File.Exists(credentialsFile))
                return;

            String? withPassword  = null;
            String? withToken     = null;

            #region What it proved itself with

            if (File.Exists(credentialsFile))
            {

                var credentials = new CSMSCredentials(credentialsFile, TimeProvider);

                if (!credentials.TryLoad(out var unread))
                {
                    Log.Critical($"The line up to the CSMS of before is not taken over: its credentials could not be read: {unread}", "ocpp", "connections", "auth");
                    return;
                }

                if (credentials.Username is String login)
                {

                    if (credentials.PasswordOfBefore is String password &&
                        !Connections.TryAddAuthentication("The password this local controller signs in to its CSMS with", "basic", login, password,
                                                          out withPassword, out var refusedPassword))
                    {
                        Log.Critical($"The line up to the CSMS of before is not taken over: its password is refused: {refusedPassword}", "ocpp", "connections", "auth");
                        return;
                    }

                    if (credentials.TOTPOfBefore is TOTPSettings token &&
                        !Connections.TryAddAuthentication("The one-time token this local controller signs in to its CSMS with", "totp", login, token.SharedSecret,
                                                          out withToken, out var refusedToken,
                                                          ValiditySeconds:    token.ValidityTime.TotalSeconds,
                                                          Length:             token.Length,
                                                          Alphabet:           token.Alphabet,
                                                          HashAlgorithm:      token.HashAlgorithm.ToString(),
                                                          TLSChannelBinding:  token.TLSChannelBinding))
                    {
                        Log.Critical($"The line up to the CSMS of before is not taken over: its one-time token is refused: {refusedToken}", "ocpp", "connections", "auth");
                        return;
                    }

                    if (withPassword is not null && withToken is not null)
                        Log.Notice("This local controller had a password and a one-time token for its CSMS. A connection names one: it signs in with the " +
                                   "password, and the token is kept as credentials of their own, to be chosen for the connection instead.",
                                   "ocpp", "connections", "auth");

                }

            }

            #endregion

            #region The line, and whom it believed

            if (url is not null)
            {

                String?                    keyPins         = null;
                UInt64                     keyPinsVersion  = 0;
                ServerKeyPinSettings?      trust           = null;
                IEnumerable<PinSetSigner>  signers         = [];

                if (File.Exists(keyPinsFile))
                {

                    var pins = new CSMSKeyPins(keyPinsFile, () => url, DialAgain: () => { }, TimeProvider);

                    if (!pins.TryLoad(out var unread))
                    {
                        Log.Critical($"The line up to the CSMS of before is not taken over: how it believed its CSMS could not be read: {unread}", "ocpp", "connections", "keypins", "security");
                        return;
                    }

                    if (pins.Current is IPinnedTLSEndpoint line)
                    {

                        if (line.ServerKeyPins.Count > 0)
                        {
                            keyPins         = String.Join(",", line.ServerKeyPins.Select(pin => pin.ToHex()));
                            keyPinsVersion  = line.ServerKeyPinsVersion;
                        }

                        trust = new ServerKeyPinSettings(line.RawPublicKeys.  ToString(),
                                                         line.TrustOnFirstUse.ToString(),
                                                         (Int64) line.ServerKeyPinProbationTime.TotalSeconds);

                        if (line.ServerKeyPinLearned is not null && line.ServerKeyPins.Count == 0)
                            Log.Notice("The key pin this local controller had learned of its CSMS and not had confirmed is not taken over: its connection " +
                                       "learns it again on its first handshake, and is on probation from then.",
                                       "ocpp", "connections", "keypins", "security");

                    }

                    signers = pins.PinSetSigners;

                }

                if (!Connections.TryAddConnection("CSMS", url, "CSMS", true,
                                                  withPassword ?? withToken,
                                                  null,
                                                  out var connectionId, out var refused,
                                                  ServerKeyPins:  keyPins,
                                                  KeyPinTrust:    trust,
                                                  HTTPVersion:    Written?.HTTPVersion is null ? null : Written.HTTPVersionAsText))
                {
                    Log.Critical($"The line up to the CSMS at {url} of before is not taken over: {refused}", "ocpp", "connections");
                    return;
                }

                // The version of a pin set that was signed comes with it: an
                // older one, kept by whoever heard it, must not come back as
                // if it were the first.
                if (keyPins is not null && keyPinsVersion > 0 &&
                    !Connections.TryTakeSignedServerKeyPins(connectionId, keyPins, keyPinsVersion, out _, out _, out var refusedVersion))
                {
                    Log.Warning($"The version {keyPinsVersion} of the pin set of before is not taken over: {refusedVersion} " +
                                "A pin set of an older version than it would be taken.",
                                "ocpp", "connections", "keypins", "security");
                }

                foreach (var signer in signers)
                    if (!Connections.TryAddPinSetSigner(signer.PublicKey, signer.Description, out var refusedSigner, out _))
                        Log.Warning($"A signer of pin sets of before is not taken over: {refusedSigner}", "ocpp", "connections", "keypins", "security");

                // What the file said the security profile was, and what the
                // connection is: the connection says it by its address and by
                // whether a certificate is held up, and nothing else decides.
                if (Written?.SecurityProfile is Byte said)
                {

                    var isNow = !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                                    ? 1
                                    : Written.ChosenClientCertificate is not null ? 3 : 2;

                    if (said != isNow)
                        Log.Warning($"The configuration file says security profile {said} for the line up to the CSMS at {url}, which is security profile " +
                                    $"{isNow} by its address{(isNow == 3 ? " and the certificate it holds up" : "")}: a connection is what its address says, " +
                                    "and 'csms.securityProfile' is no longer read.",
                                    "ocpp", "connections");

                }

                Log.Notice($"The line up to the CSMS at {url} is the connection '{connectionId}' of this local controller now, with what it signs in with and " +
                           "whom it believes: it is changed under Connections, and no longer in the section 'csms'.",
                           "ocpp", "connections");

            }

            #endregion

            #region The files of before

            try
            {

                if (File.Exists(credentialsFile))
                    File.Delete(credentialsFile);

                if (File.Exists(keyPinsFile))
                    File.Move(keyPinsFile, keyPinsFile + ".takenOver", overwrite: true);

            }
            catch (Exception e)
            {
                Log.Warning($"The files of the line up to the CSMS of before could not be put away: {e.Message} They are not read again.", "ocpp", "connections");
            }

            #endregion

        }

        #endregion

        #region (protected override) ConnectOverWebSocket(Connection, Basic, Validator, Certificates, TOTP, TOTPLogin, CancellationToken)

        /// <summary>
        /// Open the WebSocket of a connection as a local controller keeps its
        /// lines: in the networking mode the section "csms" says - which is
        /// what lets a CSMS reach the stations behind this controller -, with
        /// its pings and its timeout, over its versions of TLS, and with the
        /// TLS identity of this controller's own certificate store where the
        /// connection holds up no certificate of its own and one is chosen.
        /// </summary>
        protected override Task<HTTPResponse> ConnectOverWebSocket(ConnectionEntry                                                 Connection,
                                                                   IHTTPAuthentication?                                            Basic,
                                                                   RemoteTLSServerCertificateValidationHandler<IWebSocketClient>?  Validator,
                                                                   X509Certificate2[]?                                             Certificates,
                                                                   TOTPConfig?                                                     TOTP,
                                                                   String?                                                         TOTPLogin,
                                                                   CancellationToken                                               CancellationToken)
        {

            SslStreamCertificateContext? identity = null;

            if (Certificates is null && Connection.IsSecure && csmsSettings.ChosenClientCertificate is not null &&
                !TryCSMSIdentity(out identity, out var identityProblem))
            {
                throw new InvalidOperationException(identityProblem);
            }

            // The primary under the name the section "csms" gives the CSMS as
            // well, where it gives another than "CSMS".
            var names = CSMSNamesOf(Connection);

            if (names.Length > 0 &&
                csmsSettings.NextHopNodeId is String nextHop &&
                NetworkingNode_Id.TryParse(nextHop, out var named) &&
                !names.Contains(named))
            {
                names = [.. names, named];
            }

            lc01.ReconnectPolicy = new WebSocketClientReconnectPolicy(
                                       InitialDelay:  csmsSettings.ReconnectInitialDelay,
                                       MaxDelay:      csmsSettings.ReconnectMaxDelay
                                   );

            return lc01.ConnectOCPPWebSocketClient(

                       RemoteURL:                   Connection.URL,
                       NextHopNetworkingNodeId:     NextHopOf(Connection),
                       RoutingNetworkingNodeIds:    names,

                       NetworkingMode:              csmsSettings.NetworkingMode ?? CSMSConfiguration.DefaultNetworkingMode,

                       SecWebSocketProtocols:       SubProtocolsOf(Connection),
                       HTTPAuthentication:          Basic,
                       TOTPConfig:                  TOTP,
                       TOTPLogin:                   TOTPLogin,
                       WebSocketOver:               Connection.HTTPVersion,

                       DisableWebSocketPings:       false,
                       WebSocketPingEvery:          csmsSettings.PingEvery,
                       RequestTimeout:              csmsSettings.RequestTimeout,

                       TLSProtocols:                csmsSettings.MinimumTLSVersion,
                       RemoteCertificateValidator:  Validator,
                       ClientCertificates:          Certificates,
                       ClientCertificateContext:    identity,

                       DNSClient:                   DNSClient,
                       CancellationToken:           CancellationToken

                   );

        }

        #endregion

        #region (protected override) ClientKeyPins()

        /// <summary>
        /// The key pins of the TLS client keys this controller presents:
        /// those of the certificates its connections name, and that of the
        /// TLS identity of its own certificate store, where one is chosen.
        /// </summary>
        protected override IEnumerable<KeyPin> ClientKeyPins()
        {

            var pins = base.ClientKeyPins().ToList();

            if (csmsSettings.ChosenClientCertificate is not null &&
                TryCSMSIdentity(out var identity, out _) &&
                identity?.TargetCertificate is not null)
            {
                try
                {

                    var pin = KeyPin.FromCertificate(identity.TargetCertificate);

                    if (!pins.Contains(pin))
                        pins.Add(pin);

                }
                catch
                {
                    // A key this runtime cannot read is no key it presents either.
                }
            }

            return pins;

        }

        #endregion

        #region (protected override) FlushOfflineQueueOf(Version) / QueueSecurityEvent(Version, Type, TechInfo)

        /// <summary>
        /// Send what this controller kept for its CSMS while it was out of
        /// reach.
        /// </summary>
        protected override Task FlushOfflineQueueOf(OCPPVersion Version)

            => lc01.FlushOfflineQueueAsync();

        /// <summary>
        /// Tell the CSMS of a security event, now or once it is in reach.
        /// </summary>
        protected override void QueueSecurityEvent(OCPPVersion  Version,
                                                   String       Type,
                                                   String       TechInfo)
        {
            _ = lc01.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type), TechInfo);
        }

        #endregion

    }

}
