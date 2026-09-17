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

using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.LocalController.Logging;
using cloud.charging.open.LocalController.OCPP;

using OCPPWebSockets = cloud.charging.open.protocols.OCPP.WebSockets;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The HTTP WebSocket server the charging stations below this local
    /// controller connect to: its socket, the certificate it presents, who it
    /// lets in and what it says about them.
    /// </summary>
    /// <remarks>
    /// <b>A second server and not a second path on the first one.</b> The web
    /// interface and this one face different networks, present different
    /// certificates and let in different callers - a person with a password and
    /// a machine with one. One socket would have made one set of rules out of
    /// two, and the looser of the two would have won.
    ///
    /// <b>What changes while running and what waits for a restart.</b> The
    /// certificate, the accepted chains, the charging station logins and the
    /// logging switches are read afresh whenever they are needed, so a change
    /// to them is in effect by the time the page has reloaded. The socket - the
    /// interface, the port, the OCPP versions offered, the TLS versions, and
    /// whether a client certificate is asked for - is decided when the server
    /// is built, and changing it waits for the next start. Which of the two a
    /// change was is reported back rather than left to be discovered.
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        /// <summary>
        /// What the charging station server is configured as, with every field
        /// filled in.
        /// </summary>
        /// <remarks>
        /// Started at the defaults rather than left to the constructor: these
        /// are filled in by <see cref="BuildOCPPServer"/>, which the
        /// constructor calls, and a controller read before that has happened
        /// should say "switched off" rather than throw.
        /// </remarks>
        private OCPPServerConfiguration                    ocppServerSettings  = new OCPPServerConfiguration().Effective();

        /// <summary>
        /// What it was configured as when it was built, to tell a change that
        /// is in effect from one that is waiting for a restart.
        /// </summary>
        private OCPPServerConfiguration                    ocppServerAsBuilt   = new OCPPServerConfiguration().Effective();

        private OCPPWebSockets.OCPPWebSocketServer?        ocppWebSocketServer;

        /// <summary>
        /// Whether this server is speaking TLS, decided once when it started.
        /// </summary>
        /// <remarks>
        /// Once, and not per connection, although the certificate is chosen per
        /// connection: a port either encrypts or it does not, and a certificate
        /// uploaded while the server runs must not turn half the charging
        /// stations on a site into TLS clients and leave the other half plain.
        /// So a first certificate is a restart, and every one after it is not.
        /// </remarks>
        private Boolean                                    ocppServerTLS;

        private Boolean                                    ocppServerStarted;

        /// <summary>
        /// What makes this controller look at its certificates without being
        /// asked.
        /// </summary>
        /// <remarks>
        /// The whole value of uploading a replacement weeks early is somebody
        /// being told weeks early. Checking only when a page is opened would
        /// mean the warning arrives when somebody was already looking - which
        /// is the one moment it is not needed.
        ///
        /// On this controller's own <see cref="TimeProvider"/> rather than a
        /// bare timer, so that a test which moves the clock moves this too.
        /// </remarks>
        private ITimer?                                    certificateCheckTimer;

        /// <summary>
        /// How often the certificates are looked at. Daily: what is being
        /// watched for moves at the speed of a calendar.
        /// </summary>
        public static readonly TimeSpan CertificateCheckEvery = TimeSpan.FromHours(24);

        #endregion

        #region Properties

        /// <summary>
        /// The keys and certificates this local controller presents to the
        /// charging stations.
        /// </summary>
        public ServerCertificateStore   ServerCertificates    { get; private set; } = default!;

        /// <summary>
        /// Which chains a charging station's own certificate may lead to.
        /// </summary>
        public ClientTrustStore         ClientTrust           { get; private set; } = default!;

        /// <summary>
        /// Which charging stations may sign in, and with what.
        /// </summary>
        public ChargingStationLogins    StationLogins         { get; private set; } = default!;

        /// <summary>
        /// The server itself, or null when this controller was built without
        /// one.
        /// </summary>
        public OCPPWebSockets.OCPPWebSocketServer?  StationServer
            => ocppWebSocketServer;

        /// <summary>
        /// What the charging station server is configured as.
        /// </summary>
        public OCPPServerConfiguration  OCPPServerSettings
            => ocppServerSettings;

        /// <summary>
        /// Whether this local controller is meant to listen for charging
        /// stations at all.
        /// </summary>
        public Boolean OCPPServerEnabled
            => ocppServerSettings.Enabled == true;

        /// <summary>
        /// Whether it is listening right now.
        /// </summary>
        public Boolean OCPPServerRunning
            => ocppServerStarted;

        /// <summary>
        /// Whether the charging stations are talking to it over TLS.
        /// </summary>
        public Boolean OCPPServerTLS
            => ocppServerTLS;

        /// <summary>
        /// Where the charging stations are told to connect.
        /// </summary>
        public String OCPPServerURL

            => $"{(ocppServerTLS ? "wss" : "ws")}://" +
               $"{(ocppServerSettings.ReachableAs is { Count: > 0 } names ? names[0] : ocppServerSettings.Address?.ToString() ?? "0.0.0.0")}:" +
               $"{ocppServerSettings.TCPPort ?? OCPPServerConfiguration.DefaultTCPPort}";

        #endregion


        #region (private) BuildOCPPServer(Configuration)

        /// <summary>
        /// The stores this server reads from, and the server itself. Nothing
        /// listens yet.
        /// </summary>
        /// <remarks>
        /// Everything lives beside the configuration file rather than beside
        /// the process: a controller pointed at another configuration is a
        /// different controller, and it should not inherit the keys and the
        /// charging stations of the one whose directory the process happens to
        /// sit in.
        /// </remarks>
        private void BuildOCPPServer(OCPPServerConfiguration? Configuration)
        {

            var directory = Path.GetDirectoryName(ConfigFile.Path) ?? ".";

            ocppServerSettings  = (Configuration ?? new OCPPServerConfiguration()).Effective();
            ocppServerAsBuilt   = ocppServerSettings;

            #region The three stores, each saying what it finds

            ServerCertificates = new ServerCertificateStore(
                                     Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName),
                                     TimeProvider
                                 );

            ServerCertificates.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "tls");

            ClientTrust        = new ClientTrustStore(
                                     Path.Combine(directory, ClientTrustStore.DefaultDirectoryName),
                                     TimeProvider
                                 );

            ClientTrust.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "tls", "trust");

            StationLogins      = new ChargingStationLogins(
                                     Path.Combine(directory, ChargingStationLogins.DefaultFileName),
                                     TimeProvider
                                 );

            StationLogins.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "station", "auth");

            // A file that is there but unreadable is not papered over with an
            // empty list: that would turn every charging station on the site
            // away while looking like nothing was wrong.
            if (!StationLogins.TryLoad(out var loginsError))
                throw new InvalidOperationException($"{loginsError} Repair or remove it and start again.");

            StationLogins.OnChanged += () => {
                if (ocppWebSocketServer is not null)
                    ApplyStationLogins(ocppWebSocketServer);
            };

            #endregion

            #region The server

            var settings = ocppServerSettings;

            ocppWebSocketServer = lc01.AttachWebSocketServer(

                                      HTTPServiceName:              $"OpenChargingCloud LocalController v{Version}",
                                      IPAddress:                    settings.Address,
                                      TCPPort:                      settings.TCPPort,
                                      Description:                  I18NString.Create("The charging stations of this local controller"),

                                      // Off, because this server does the
                                      // deciding itself: a station on security
                                      // profile 3 authenticates with its
                                      // certificate and has no password to send,
                                      // and the check built in here cannot tell
                                      // the two apart. See ValidateStation.
                                      RequireAuthentication:        false,
                                      SecWebSocketProtocols:        settings.Subprotocols,
                                      DisableWebSocketPings:        false,
                                      WebSocketPingEvery:           settings.PingEvery,

                                      ClientCertificateValidator:   ValidateStationCertificate,
                                      AllowedTLSProtocols:          settings.MinimumTLSVersion,

                                      // "Ask for one", not "insist on one": a
                                      // station on profile 2 sends none, and
                                      // whether that is acceptable is decided
                                      // in the validator, which knows which
                                      // profiles are allowed.
                                      //
                                      // Hermod asks for one whenever a validator
                                      // is set, which it always is here, so this
                                      // says out loud what is happening rather
                                      // than deciding it.
                                      ClientCertificateRequired:    true,
                                      CheckCertificateRevocation:   settings.CheckCertificateRevocation,

                                      MaxClientConnections:         settings.MaxConnections,

                                      AutoStart:                    false

                                  );

            ocppWebSocketServer.OnValidateWebSocketConnection += ValidateStation;

            ApplyStationLogins(ocppWebSocketServer);
            WireOCPPServerLogging(ocppWebSocketServer);

            #endregion

            Log.Info(
                settings.Enabled == true
                    ? $"The charging station server will listen on {settings.Address}:{settings.TCPPort}, " +
                      $"speaking {String.Join(" and ", settings.Subprotocols ?? [])}, " +
                      $"security profile(s) {String.Join(", ", settings.SecurityProfiles ?? [])}."
                    : "The charging station server is switched off; no charging station can connect. " +
                      "Switch it on under Configuration to let them in.",
                "ocpp", "station"
            );

        }

        #endregion

        #region (private) StartOCPPServer() / StopOCPPServer()

        /// <summary>
        /// Start listening for charging stations, when this controller is
        /// meant to.
        /// </summary>
        private async Task StartOCPPServer()
        {

            if (ocppWebSocketServer is null || ocppServerStarted)
                return;

            if (!OCPPServerEnabled)
                return;

            var settings   = ocppServerSettings;
            var wantsTLS   = settings.SecurityProfiles?.Any(profile => profile > 1) == true;

            #region Encrypted or not, decided here and for good

            ocppServerTLS = wantsTLS && ServerCertificates.HasCertificate;

            if (ocppServerTLS)
            {

                ocppWebSocketServer.ServerCertificateChainSelector = (server, client) => ServerCertificates.Select();

                #region Can the chain that would be presented actually be presented?

                // Asked now rather than found out per connection. .NET builds
                // the chain of the server's own certificate before it can
                // present it, and refuses when it cannot reach a root this
                // machine has - a private authority whose root was never
                // installed, most often. What a charging station then sees is a
                // TLS handshake reset with nothing said, and this port would
                // turn every single one of them away while reporting itself as
                // running and encrypted.
                try
                {

                    var chain = ServerCertificates.Select();

                    if (chain is null)
                        Log.Critical(
                            "The charging station port is meant to be encrypted, but no certificate could be chosen for it. " +
                            "No charging station will be able to connect.",
                            "ocpp", "station", "tls"
                        );

                    else if (!chain.TryCreateContext(out _, out var why))
                        Log.Critical(
                            $"The charging station port is encrypted, but the certificate it would present cannot be served: {why} " +
                             "Until this is put right every charging station is turned away during the TLS handshake, " +
                             "which looks to them like the port simply dropping the connection.",
                            "ocpp", "station", "tls"
                        );

                }
                catch (Exception e)
                {
                    Log.Critical(
                        $"The certificate for the charging station port could not be examined: {e.Message} " +
                         "Charging stations may be turned away during the TLS handshake.",
                        "ocpp", "station", "tls"
                    );
                }

                #endregion

            }

            else
            {

                ocppWebSocketServer.ServerCertificateChainSelector = null;

                if (wantsTLS)
                    Log.Warning(
                        "Security profile 2 or 3 is configured, but there is no server certificate - so the charging station port is " +
                        "unencrypted and only security profile 1 can be used on it. Make a signing request and upload the certificate " +
                        "that answers it; the port encrypts from the next start.",
                        "ocpp", "station", "tls"
                    );

                else if (ServerCertificates.HasCertificate)
                    Log.Notice(
                        "Only security profile 1 is allowed, so the charging station port is unencrypted and the server certificate is not presented.",
                        "ocpp", "station", "tls"
                    );

            }

            #endregion

            await ocppWebSocketServer.Start();

            ocppServerStarted = true;

            Log.Notice(
                $"The charging stations connect to {OCPPServerURL} " +
                $"({(ocppServerTLS ? "TLS" : "unencrypted")}, security profile(s) {String.Join(", ", settings.SecurityProfiles ?? [])}, " +
                $"{StationLogins.EnabledCount} station login(s), {ClientTrust.EnabledCount} accepted chain(s)).",
                "ocpp", "station"
            );

            #region What is about to run out, now and every day after this

            CheckCertificates();

            certificateCheckTimer?.Dispose();
            certificateCheckTimer = TimeProvider.CreateTimer(
                                        _ => CheckCertificates(),
                                        null,
                                        CertificateCheckEvery,
                                        CertificateCheckEvery
                                    );

            if (ocppServerTLS)
                ServerCertificates.Select();

            #endregion

            #region The two ways to have a port nobody can come through

            if (!ocppServerTLS && StationLogins.EnabledCount == 0)
                Log.Warning(
                    "No charging station has a login here, so none can sign in. Add them under Configuration.",
                    "ocpp", "station", "auth"
                );

            if (settings.SecurityProfiles?.Contains<Byte>(3) == true && ClientTrust.EnabledCount == 0)
                Log.Warning(
                    "Security profile 3 is allowed, but no certificate authority has been named to accept charging stations from - " +
                    "so no station can connect with a certificate. An empty list is 'nobody', not 'everybody'.",
                    "ocpp", "station", "tls"
                );

            #endregion

        }

        /// <summary>
        /// Stop listening for charging stations.
        /// </summary>
        private async Task StopOCPPServer()
        {

            if (ocppWebSocketServer is null || !ocppServerStarted)
                return;

            ocppServerStarted = false;

            certificateCheckTimer?.Dispose();
            certificateCheckTimer = null;

            await ocppWebSocketServer.Shutdown("The local controller is shutting down.");

            Log.Info("The charging station server has stopped listening.", "ocpp", "station");

        }

        #endregion

        #region (private) CheckCertificates()

        /// <summary>
        /// Look at every certificate and every accepted chain, and say what is
        /// about to run out.
        /// </summary>
        /// <remarks>
        /// Both sides, because both sides take a site down when they expire:
        /// this controller's own certificate stops being believed by the
        /// charging stations, and an expired trust anchor stops this controller
        /// believing them.
        ///
        /// Whatever goes wrong in here is caught: this runs on a timer, and a
        /// timer callback that throws takes the process with it. A controller
        /// that stopped charging cars because it could not read a certificate
        /// file would be the worst possible answer to "your certificate expires
        /// next month".
        /// </remarks>
        private void CheckCertificates()
        {

            try
            {
                ServerCertificates.CheckExpiry(ocppServerSettings.ReachableAs ?? []);
                ClientTrust.       CheckExpiry();
            }
            catch (Exception e)
            {
                Log.Exception(e, "The certificates of the charging station server could not be checked.", "ocpp", "tls");
            }

        }

        #endregion

        #region (private) ValidateStation(...)

        /// <summary>
        /// Whether this charging station may come in - the OCPP security
        /// profiles and the login groups, decided in one place.
        /// </summary>
        /// <remarks>
        /// <b>Three ways in, and each is checked twice.</b> A certificate, a
        /// password or a one-time token; and whichever it is, the server as a
        /// whole has to allow the security profile it arrives on, and the group
        /// the login belongs to has to allow both the profile and the method.
        /// The group can only narrow what the server allows, never widen it.
        ///
        /// <b>Profile 3 first.</b> A station that authenticated with a
        /// certificate has already been checked against the accepted chains by
        /// the time this runs; it needs no password and is not asked for one.
        /// If it is also listed as a login, its group has its say - which is
        /// how a certificate station is put under the same rules as the rest.
        /// If it is not listed, the accepted chains are the whole of the
        /// decision, as they were before there were groups.
        ///
        /// <b>Then a password or a token</b>, which are the same question over
        /// the same transport: is this the credential this identification
        /// should be showing? What decides the profile is whether the port is
        /// encrypted, and that was settled when the server started. TOTP is not
        /// an OCPP security profile at all, so it is judged under the profile
        /// its transport would have had.
        ///
        /// A refusal says as little as it can to the station and as much as it
        /// can to the log. "Unauthorized" back over the wire; which
        /// identification it was, where it came from and what was wrong with it
        /// into the log, where somebody who is allowed to know may read it.
        /// </remarks>
        private Task<HTTPResponse?> ValidateStation(DateTimeOffset             Timestamp,
                                                    AWebSocketServer           Server,
                                                    WebSocketServerConnection  Connection,
                                                    EventTracking_Id           EventTrackingId,
                                                    CancellationToken          CancellationToken)
        {

            var profiles  = ocppServerSettings.SecurityProfiles ?? OCPPServerConfiguration.DefaultSecurityProfiles;
            var from      = Connection.RemoteSocket.ToString();

            #region Security profile 3: it brought a certificate

            if (Connection.ClientCertificate is not null)
            {

                if (!profiles.Contains<Byte>(3))
                    return Refuse(Connection, "it authenticated with a certificate, and security profile 3 is not allowed here");

                // The certificate itself was held against the accepted chains
                // during the handshake; a connection that got this far with one
                // has passed.
                var named  = Connection.ClientCertificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                var who    = named.IsNullOrEmpty() ? Connection.ClientCertificate.Subject : named;

                // Listed as a login as well? Then it is in a group, and the
                // group decides. Not listed is not an error: the chain was the
                // whole decision before groups existed and still is.
                if (!named.IsNullOrEmpty() &&
                    StationLogins.TryGet(named, out var certificateLogin))
                {

                    if (!certificateLogin.Enabled)
                        return Refuse(Connection, $"the charging station '{named}' is switched off here");

                    if (!GroupAllows(certificateLogin, AuthMethod.Certificate, 3, out var why))
                        return Refuse(Connection, $"the charging station '{named}' {why}");

                }

                if (ocppServerSettings.Logging?.Authentication != false)
                    Log.Info($"The charging station '{who}' signed in from {from} with a certificate (security profile 3).",
                             "ocpp", "station", "auth");

                return Task.FromResult<HTTPResponse?>(null);

            }

            if (!profiles.Contains<Byte>(1) && !profiles.Contains<Byte>(2))
                return Refuse(Connection, "only security profile 3 is allowed here and it presented no certificate");

            #endregion

            #region Which profile a credential on this port would be

            var profile = (Byte) (ocppServerTLS ? 2 : 1);

            if (!profiles.Contains(profile))
                return Refuse(
                           Connection,
                           profile == 1
                               ? "the charging station port is unencrypted, so this would be security profile 1, which is not allowed here"
                               : "the charging station port is encrypted, so this would be security profile 2, which is not allowed here"
                       );

            #endregion

            #region A password

            if (Connection.HTTPRequest?.Authorization is HTTPBasicAuthentication basicAuthentication)
            {

                var id = basicAuthentication.Username;

                // Verified before the group is consulted, and always. The
                // store hashes a fixed word for an identification it does not
                // have, so that an unknown one does not answer faster than a
                // known one - and asking the group first would put that back:
                // a station in a group that forbids passwords would be refused
                // without the hashing, and the difference is measurable.
                var right = StationLogins.Verify(id, basicAuthentication.Password);

                if (!StationLogins.TryGet(id, out var login) || !login.Enabled || !right)
                    return Refuse(Connection, $"'{id}' is not a charging station that may sign in, or the password is wrong");

                if (!GroupAllows(login, AuthMethod.Basic, profile, out var why))
                    return Refuse(Connection, $"the charging station '{id}' {why}");

                if (ocppServerSettings.Logging?.Authentication != false)
                    Log.Info($"The charging station '{id}' signed in from {from} with a password (security profile {profile}).",
                             "ocpp", "station", "auth");

                return Task.FromResult<HTTPResponse?>(null);

            }

            #endregion

            #region A one-time token

            if (Connection.HTTPRequest?.Authorization is HTTPTOTPAuthentication totpAuthentication)
            {

                var id = totpAuthentication.Login;

                // Hermod's default is the TLS-bound kind, which needs TLS
                // exporter material - .NET does not offer that on an SslStream,
                // so a token of that kind cannot be checked here at all. Said
                // out loud, because the alternative is a station whose token is
                // computed correctly and refused anyway, with nothing in the
                // log to explain it.
                if (totpAuthentication.Type != TOTPHTTPHeaderType.RAW)
                    return Refuse(Connection, $"'{id}' sent a TLS-bound one-time token, which this server cannot check: " +
                                               "it would need TLS exporter material, which .NET does not offer. The station has to send tlscb=false");

                // Derived before the group is consulted, and always, for the
                // same reason as the password above.
                var right = StationLogins.VerifyTOTP(id, totpAuthentication.TOTP);

                if (!StationLogins.TryGet(id, out var login) || !login.Enabled || !right)
                    return Refuse(Connection, $"'{id}' is not a charging station that may sign in, or the one-time token is wrong");

                if (!GroupAllows(login, AuthMethod.TOTP, profile, out var why))
                    return Refuse(Connection, $"the charging station '{id}' {why}");

                if (ocppServerSettings.Logging?.Authentication != false)
                    Log.Info($"The charging station '{id}' signed in from {from} with a one-time token (security profile {profile}" +
                             $"{(ocppServerTLS ? "" : ", on an unencrypted port, so the token is replayable while it stands")}).",
                             "ocpp", "station", "auth");

                return Task.FromResult<HTTPResponse?>(null);

            }

            #endregion

            return Refuse(Connection, "it sent no credentials");


            #region (local) GroupAllows(Login, Method, Profile, out Why)

            // Whether the group of this login lets it in this way, on this
            // security profile.
            Boolean GroupAllows(ChargingStationLogin  Login,
                                AuthMethod            Method,
                                Byte                  Profile,
                                out String            Why)
            {

                Why = "";

                var group = StationLogins.GroupOf(Login);

                // A login whose group has gone missing is refused rather than
                // waved through: the group is what decides about it, and a
                // decision that cannot be found is not a yes.
                if (group is null)
                {
                    Why = $"belongs to the login group '{Login.GroupId}', which this controller does not have";
                    return false;
                }

                if (!group.Enabled)
                {
                    Why = $"is in the login group '{group.Id}', which is switched off";
                    return false;
                }

                if (!group.Allows(Method))
                {
                    Why = $"is in the login group '{group.Id}', which does not accept {Method.AsText()}";
                    return false;
                }

                if (!group.Allows(Profile))
                {
                    Why = $"is in the login group '{group.Id}', which does not accept security profile {Profile}";
                    return false;
                }

                return true;

            }

            #endregion


            Task<HTTPResponse?> Refuse(WebSocketServerConnection Connection, String Because)
            {

                if (ocppServerSettings.Logging?.Authentication != false)
                    Log.Warning($"A charging station at {Connection.RemoteSocket} was turned away: {Because}.",
                                "ocpp", "station", "auth");

                // Hermod sets the request on the connection before it asks any
                // validator, so this is never null in practice. If it ever is,
                // the answer is still no: returning null here would mean "let
                // them in", and a refusal that cannot be written is a refusal
                // all the same.
                if (Connection.HTTPRequest is null)
                    throw new InvalidOperationException(
                              "A charging station connection reached the validator without an HTTP request; refusing it."
                          );

                return Task.FromResult<HTTPResponse?>(
                           new HTTPResponse.Builder(Connection.HTTPRequest) {
                               HTTPStatusCode  = HTTPStatusCode.Unauthorized,
                               Server          = Server.HTTPServiceName,
                               WWWAuthenticate = WWWAuthenticate.Basic("OCPP"),
                               Connection      = ConnectionType.Close
                           }.AsImmutable
                       );

            }

        }

        #endregion

        #region (private) ValidateStationCertificate(...)

        /// <summary>
        /// Whether the certificate a charging station presented during the TLS
        /// handshake leads to a chain this local controller accepts.
        /// </summary>
        /// <remarks>
        /// A station that presents no certificate is let through here and
        /// judged by <see cref="ValidateStation"/>, which knows whether a
        /// password would do instead.
        ///
        /// A station that presents one this controller does not accept is
        /// turned away here rather than allowed to fall back to a password: a
        /// station sends a certificate because it was configured for security
        /// profile 3, so it has no password to fall back to, and a certificate
        /// that does not check out is worth a line in the log rather than a
        /// quiet second attempt.
        /// </remarks>
        private TLSValidationResult ValidateStationCertificate(Object                                                             Sender,
                                                               System.Security.Cryptography.X509Certificates.X509Certificate2?    Certificate,
                                                               System.Security.Cryptography.X509Certificates.X509Chain?           Chain,
                                                               org.GraphDefined.Vanaheimr.Hermod.TCP.ITCPServer                   Server,
                                                               System.Net.Security.SslPolicyErrors                                PolicyErrors)
        {

            if (Certificate is null)
                return new TLSValidationResult(IsValid: true, Errors: null, Warnings: null);

            var result = ClientTrust.Validate(
                             Certificate,
                             Chain,
                             ocppServerSettings.CheckCertificateRevocation == true
                         );

            if (ocppServerSettings.Logging?.Authentication != false)
                Log.Log(
                    result.Accepted ? LogLevel.Debug : LogLevel.Warning,
                    result.Accepted
                        ? $"The certificate of '{result.Subject}' was {result.Reason}."
                        : $"The certificate of '{result.Subject}' was refused: {result.Reason}.",
                    "ocpp", "station", "tls"
                );

            return new TLSValidationResult(
                       IsValid:   result.Accepted,
                       Errors:    result.Accepted ? null : [ Error.Create(result.Reason) ],
                       Warnings:  null
                   );

        }

        #endregion

        #region (private) ApplyStationLogins(Server) / WireOCPPServerLogging(Server)

        /// <summary>
        /// Put the charging station logins into the server, and take out the
        /// ones that are no longer there.
        /// </summary>
        /// <remarks>
        /// Removed and not only added: a station whose password was taken away
        /// must stop being able to use it now, and a dictionary that is only
        /// ever added to would let it in until the next start.
        /// </remarks>
        private void ApplyStationLogins(AWebSocketServer Server)
        {

            var wanted = StationLogins.SecurePasswords();

            foreach (var gone in Server.ClientLogins.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
                Server.ClientLogins.TryRemove(gone, out _);

            foreach (var login in wanted)
                Server.ClientLogins[login.Key] = login.Value;

        }

        /// <summary>
        /// What of this server ends up in the event log, and what does not.
        /// </summary>
        /// <remarks>
        /// The switches are read inside the handlers rather than used to attach
        /// and detach them, so that changing one takes effect on the next
        /// message instead of the next start - and so that there is one place
        /// to look for what is logged rather than a set of subscriptions to
        /// reason about.
        /// </remarks>
        private void WireOCPPServerLogging(OCPPWebSockets.OCPPWebSocketServer Server)
        {

            #region Charging stations coming and going

            Server.OnNewWebSocketConnection += (timestamp, server, connection, sharedSubprotocols, selectedSubprotocol, eventTrackingId, cancellationToken) => {

                if (ocppServerSettings.Logging?.Connections != false)
                    Log.Notice(
                        $"The charging station '{connection.Login ?? "?"}' is connected from {connection.RemoteSocket}" +
                        $" ({selectedSubprotocol ?? "no subprotocol"}).",
                        "ocpp", "station"
                    );

                return Task.CompletedTask;

            };

            Server.OnCloseMessageReceived += (timestamp, server, connection, frame, eventTrackingId, statusCode, reason, cancellationToken) => {

                if (ocppServerSettings.Logging?.Connections != false)
                    Log.Notice(
                        $"The charging station '{connection.Login ?? "?"}' went away ({statusCode}{(reason.IsNullOrEmpty() ? "" : $": {reason}")}).",
                        "ocpp", "station"
                    );

                return Task.CompletedTask;

            };

            // A connection that never became one. Not behind the logging
            // switches: what arrives here is a charging station being dropped
            // before it could say anything, and that is never noise. The
            // commonest cause is a TLS handshake that could not be started at
            // all, which from the other end is a bare reset with nothing to
            // explain it.
            Server.OnTCPConnectionFailed += (server, timestamp, eventTrackingId, remoteSocket, connectionId, exception) => {

                Log.Warning(
                    $"A charging station at {remoteSocket} was dropped before it could sign in: {exception.Message}",
                    "ocpp", "station", "tls"
                );

                return Task.CompletedTask;

            };

            #endregion

            #region The OCPP messages themselves

            Server.OnJSONMessageReceived += (timestamp, server, connection, messageTimestamp, eventTrackingId, sourceNodeId, message, cancellationToken) => {

                LogOCPPMessage("<-", connection, message);
                return Task.CompletedTask;

            };

            Server.OnJSONMessageSent += (timestamp, server, connection, messageTimestamp, eventTrackingId, message, sentStatus, cancellationToken) => {

                LogOCPPMessage("->", connection, message);
                return Task.CompletedTask;

            };

            #endregion

            #region The pings that keep a connection open

            Server.OnPingMessageReceived += (timestamp, server, connection, frame, eventTrackingId, pingMessage, cancellationToken) => {

                if (ocppServerSettings.Logging?.Pings == true)
                    Log.Debug($"A ping came from the charging station '{connection.Login ?? "?"}'.", "ocpp", "station", "ping");

                return Task.CompletedTask;

            };

            #endregion

        }

        #endregion

        #region (private) LogOCPPMessage(Direction, Connection, Message)

        /// <summary>
        /// One OCPP message in the log - by name only, or with what it carries.
        /// </summary>
        /// <remarks>
        /// <b>The contents are a separate switch, and a window rather than a
        /// setting.</b> An OCPP message carries identification tokens, RFID
        /// card numbers and meter readings: who charged, where and how much.
        /// The window running out is what switches it off again, and it is
        /// checked here, on every message, so that it goes off whether or not
        /// anybody is watching.
        ///
        /// An OCPP message is an array: the kind, the identification, the
        /// action, and then what it carries. So the first three can be logged
        /// without the fourth, which is the whole point of having two switches.
        /// </remarks>
        private void LogOCPPMessage(String                     Direction,
                                    WebSocketServerConnection  Connection,
                                    JArray                     Message)
        {

            var logging = ocppServerSettings.Logging;

            if (logging?.Messages != true)
                return;

            var action  = Message.Count > 2 ? Message[2]?.Value<String>() : null;
            var id      = Message.Count > 1 ? Message[1]?.Value<String>() : null;
            var station = Connection.Login ?? Connection.RemoteSocket.ToString();

            if (logging.PayloadsAt(TimeProvider.GetUtcNow()))
                Log.Log(
                    LogLevel.Debug,
                    $"{Direction} {station}: {action ?? "?"} [{id ?? "?"}]",
                    new JObject(new JProperty("message", Message)),
                    "ocpp", "station", "message", "payload"
                );

            else
                Log.Debug(
                    $"{Direction} {station}: {action ?? "?"} [{id ?? "?"}]",
                    "ocpp", "station", "message"
                );

        }

        #endregion


        #region OCPPServerConfigurationJSON()

        /// <summary>
        /// The charging station server, as its Configuration page reads it.
        /// </summary>
        public JObject OCPPServerConfigurationJSON()
        {

            var settings = ocppServerSettings;
            var waiting  = ocppServerAsBuilt.NeedsARestartFor(settings);

            return new JObject(

                new JProperty("enabled",                     settings.Enabled              == true),
                new JProperty("address",                     settings.Address?.ToString()),
                new JProperty("port",                        (settings.TCPPort ?? OCPPServerConfiguration.DefaultTCPPort).ToUInt16()),
                new JProperty("securityProfiles",            new JArray((settings.SecurityProfiles ?? []).Select(profile => (Int32) profile))),
                new JProperty("subprotocols",                new JArray(settings.Subprotocols ?? [])),
                new JProperty("reachableAs",                 new JArray(settings.ReachableAs  ?? [])),
                new JProperty("minTLSVersion",               OCPPServerConfiguration.TLSVersionText(settings.MinimumTLSVersion ?? OCPPServerConfiguration.DefaultMinimumTLSVersion)),
                new JProperty("checkCertificateRevocation",  settings.CheckCertificateRevocation == true),
                new JProperty("maxConnections",              settings.MaxConnections ?? OCPPServerConfiguration.DefaultMaxConnections),
                new JProperty("pingEverySeconds",            (settings.PingEvery ?? OCPPServerConfiguration.DefaultPingEvery).TotalSeconds),

                new JProperty("logging",                     new JObject(
                    new JProperty("connections",             settings.Logging?.Connections    != false),
                    new JProperty("authentication",          settings.Logging?.Authentication != false),
                    new JProperty("messages",                settings.Logging?.Messages       == true),
                    new JProperty("payloads",                settings.Logging?.Payloads       == true),
                    new JProperty("payloadsUntil",           settings.Logging?.PayloadsUntil?.ToString("o")),
                    new JProperty("payloadsNow",             settings.Logging?.PayloadsAt(TimeProvider.GetUtcNow()) == true),
                    new JProperty("pings",                   settings.Logging?.Pings          == true)
                )),

                new JProperty("state",                       new JObject(
                    new JProperty("running",                 ocppServerStarted),
                    new JProperty("tls",                     ocppServerTLS),
                    new JProperty("url",                     OCPPServerURL),
                    new JProperty("connections",             ocppWebSocketServer?.WebSocketConnections.Count() ?? 0),
                    new JProperty("stationLogins",           StationLogins.EnabledCount),
                    new JProperty("trustedChains",           ClientTrust.EnabledCount),
                    new JProperty("hasCertificate",          ServerCertificates.HasCertificate),

                    // What a change to the socket is waiting for, said rather
                    // than left to be discovered.
                    new JProperty("waitingForARestart",      new JArray(waiting))
                )),

                new JProperty("limits",                      new JObject(
                    new JProperty("subprotocols",            new JArray(OCPPServerConfiguration.KnownSubprotocols)),
                    new JProperty("securityProfiles",        new JArray(1, 2, 3)),
                    new JProperty("tlsVersions",             new JArray("1.2", "1.3")),
                    new JProperty("maxConnections",          OCPPServerConfiguration.MaxMaxConnections),
                    new JProperty("maxReachableAs",          OCPPServerConfiguration.MaxReachableAs),
                    new JProperty("suggestedPayloadWindowSeconds", OCPPServerLogging.SuggestedPayloadWindow.TotalSeconds)
                )),

                new JProperty("file",                        ConfigFile.Path)

            );

        }

        #endregion

        #region TryUpdateOCPPServerConfiguration(JSON, out Error)

        /// <summary>
        /// Change the charging station server.
        /// </summary>
        /// <remarks>
        /// Written to the file first and applied afterwards, as everywhere
        /// else here: a change that was applied but not written down is a
        /// change that disappears at the next start without anybody noticing.
        ///
        /// What can be put into effect now is; what cannot is reported through
        /// <c>waitingForARestart</c> rather than quietly doing nothing.
        /// </remarks>
        public Boolean TryUpdateOCPPServerConfiguration(JObject                           JSON,
                                                        [NotNullWhen(false)] out String?  Error)
        {

            if (!OCPPServerConfiguration.TryParse(JSON, out var update, out Error))
                return false;

            #region What cannot be saved because it would be a port nobody can use

            if (update.SecurityProfiles is { Count: > 0 } profiles &&
                profiles.All(profile => profile > 1) &&
                !ServerCertificates.HasCertificate)
            {
                Error = "Security profiles 2 and 3 both need TLS, and there is no server certificate yet. " +
                        "Make a signing request and upload the certificate that answers it, or allow security profile 1 as well.";
                return false;
            }

            if (update.Logging?.Payloads == true &&
                update.Logging.PayloadsUntil <= TimeProvider.GetUtcNow())
            {
                Error = $"The window for logging message contents ends at {update.Logging.PayloadsUntil:yyyy-MM-dd HH:mm}'Z', " +
                        $"which by this controller's clock has already passed - it is now {TimeProvider.GetUtcNow():yyyy-MM-dd HH:mm}'Z'.";
                return false;
            }

            #endregion

            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(OCPPServerConfiguration.SectionName, update.ToJSON(), out Error))
                    return false;

                var before = ocppServerSettings;

                ocppServerSettings = before.Merge(update).Effective();

                #region What takes effect at once

                if (ocppWebSocketServer is not null &&
                    before.Enabled != ocppServerSettings.Enabled)
                {

                    if (ocppServerSettings.Enabled == true)
                        StartOCPPServer().GetAwaiter().GetResult();

                    else
                        StopOCPPServer(). GetAwaiter().GetResult();

                }

                #endregion

                var waiting = ocppServerAsBuilt.NeedsARestartFor(ocppServerSettings);

                Log.Notice(
                    $"The charging station server was reconfigured: {ocppServerSettings}." +
                    (waiting.Count > 0
                         ? $" {String.Join(", ", waiting)} take effect at the next start."
                         : ""),
                    "ocpp", "station", "config"
                );

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

    }

}
