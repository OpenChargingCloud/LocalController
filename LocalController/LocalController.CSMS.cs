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
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The charging station management system above this local controller: how
    /// it is dialled, what this controller says to prove who it is, and what
    /// happens when the line goes down.
    /// </summary>
    /// <remarks>
    /// <b>The other direction of the same thing.</b> Below, this controller is
    /// a server that charging stations sign in to; here it is a client signing
    /// in to somebody else. The same OCPP security profiles, the same three
    /// ways of proving who you are - and the node in between already knows how
    /// to forward messages both ways, so what is built here is the line, not
    /// the routing.
    ///
    /// <b>Dialled once, kept up by the client.</b> Hermod's WebSocket client
    /// dials again on its own when a connection is lost or could not be made
    /// in the first place, with an exponential backoff this controller
    /// configures. So there is no loop here that dials again: there is one
    /// that is told when the client is about to, and when it has got through.
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        /// <summary>
        /// What the CSMS is to this controller where the node judges its
        /// certificate: a service of its own, whose TLS roots of the store are
        /// the ones kept for every use.
        /// </summary>
        public const String        CSMSService        = "csms";

        /// <summary>
        /// What the CSMS connection is configured as, with every field filled in.
        /// </summary>
        private CSMSConfiguration  csmsSettings       = new CSMSConfiguration().Effective();

        /// <summary>
        /// What it was configured as when the line was dialled, to tell a change
        /// that is in effect from one that waits for the next dial.
        /// </summary>
        private CSMSConfiguration  csmsAsBuilt        = new CSMSConfiguration().Effective();

        private Boolean            csmsConnected;
        private DateTimeOffset?    csmsConnectedSince;
        private String?            csmsLastProblem;

        #endregion

        #region Properties

        /// <summary>
        /// What this local controller says to prove who it is upwards.
        /// </summary>
        public CSMSCredentials     CSMSLogin      { get; private set; } = default!;

        /// <summary>
        /// What the CSMS connection is configured as.
        /// </summary>
        public CSMSConfiguration   CSMSSettings
            => csmsSettings;

        /// <summary>
        /// Whether this local controller is meant to dial a CSMS at all.
        /// </summary>
        public Boolean             CSMSEnabled
            => csmsSettings.Enabled == true;

        /// <summary>
        /// Whether the line is up right now.
        /// </summary>
        public Boolean             CSMSConnected
            => csmsConnected;

        /// <summary>
        /// Since when, or null while it is down.
        /// </summary>
        public DateTimeOffset?     CSMSConnectedSince
            => csmsConnectedSince;

        /// <summary>
        /// What went wrong the last time, or null when nothing has.
        /// </summary>
        public String?             CSMSLastProblem
            => csmsLastProblem;

        #endregion


        #region (private) BuildCSMSConnection(Configuration)

        /// <summary>
        /// Take in the settings and the credentials. Nothing is dialled here:
        /// building and connecting are two different things, and the second is
        /// not something a constructor should do on the way past.
        /// </summary>
        private void BuildCSMSConnection(CSMSConfiguration? Configuration)
        {

            csmsSettings  = (Configuration ?? new CSMSConfiguration()).Effective();
            csmsAsBuilt   = csmsSettings;

            // Beside the configuration file, like every other file this
            // controller keeps: whoever moved the configuration moved the
            // installation, and the credentials belong with it rather than
            // with whichever directory the process happens to start in.
            var directory = System.IO.Path.GetDirectoryName(ConfigFile.Path) ?? ".";

            CSMSLogin     = new CSMSCredentials(
                                System.IO.Path.Combine(directory, CSMSCredentials.DefaultFileName),
                                TimeProvider
                            );

            CSMSLogin.OnNotice += (level, message) => Log.Log(level, message, "ocpp", "csms", "auth");

            if (!CSMSLogin.TryLoad(out var error))
                Log.Critical($"The CSMS credentials could not be read: {error}", "ocpp", "csms", "auth");

            Log.Info(
                csmsSettings.Enabled == true
                    ? $"This local controller reports to {csmsSettings.URL} (security profile {csmsSettings.SecurityProfile})."
                    : "This local controller reports to no CSMS; it is switched off under Configuration.",
                "ocpp", "csms"
            );

        }

        #endregion

        #region (private) ConnectCSMS() / DisconnectCSMS()

        /// <summary>
        /// Dial the CSMS, when this controller is meant to have one.
        /// </summary>
        /// <remarks>
        /// A refusal up here is not a reason to refuse to start. A local
        /// controller whose backend is unreachable still has charging stations
        /// below it that have to be let in, and the client keeps trying in the
        /// background - so what a failed first attempt produces is a line in the
        /// log and a state the page can show, not a controller that will not run.
        /// </remarks>
        private async Task ConnectCSMS()
        {

            if (csmsSettings.Enabled != true)
                return;

            #region What has to be there before anything is dialled

            if (csmsSettings.URL is not String url || url.Length == 0)
            {
                csmsLastProblem = "There is no CSMS address to dial.";
                Log.Warning(csmsLastProblem, "ocpp", "csms");
                return;
            }

            // Profile 3 signs in with a TLS identity of this controller's own
            // certificate store. One that is not there, not usable or not an
            // identity is said plainly rather than dialled without, which the
            // other end would refuse with a message nobody here could act on.
            System.Net.Security.SslStreamCertificateContext? identity = null;

            if (csmsSettings.WantsClientCertificate &&
                !TryCSMSIdentity(out identity, out var identityProblem))
            {
                csmsLastProblem = identityProblem;
                Log.Critical(csmsLastProblem, "ocpp", "csms", "tls");
                return;
            }

            var authentication = CSMSLogin.HTTPAuthentication();
            var totpConfig     = CSMSLogin.TOTPConfig();

            // A password or a token is how profiles 1 and 2 say who is dialling.
            // Profile 3 says it with the certificate; one given beside it is
            // sent as well, for a CSMS that asks for both.
            if (!csmsSettings.WantsClientCertificate &&
                authentication is null && totpConfig is null)
            {
                csmsLastProblem = $"This local controller has no credentials to sign in to {url} with. " +
                                   "Set them under Configuration.";
                Log.Warning(csmsLastProblem, "ocpp", "csms", "auth");
                return;
            }

            #endregion

            // The CSMS's certificate is judged by the node, as a time server's
            // and a name server's are: issued for the name it is dialled at,
            // chaining to a root this machine trusts or to a TLS root of this
            // controller's store kept for every use - and remembered, so that
            // one turning up with another certificate than before is said.
            var host = new Uri(url).Host;

            RemoteTLSServerCertificateValidationHandler<org.GraphDefined.Vanaheimr.Hermod.WebSocket.IWebSocketClient>? judgeTheCSMS = null;

            if (csmsSettings.WantsTLS)
                judgeTheCSMS = (sender, certificate, chain, client, errors) => {

                    var judgement = JudgeServer(CSMSService, host, null, certificate, chain, errors, Evidence: false);

                    return judgement.Accepted
                               ? TLSValidationResult.Success()
                               : TLSValidationResult.Failed($"The CSMS's certificate was refused: {OutcomeSaid(judgement.Outcome)}.");

                };

            // Before the first attempt, and by the node, which gives it to the
            // client it makes: a client whose first attempt failed had ended by
            // the time anything out here could give it a policy, so a controller
            // started while its CSMS was down stayed away from it until it was
            // started again. The first attempt is answered at once all the same;
            // the start is not held while the client goes on trying.
            lc01.ReconnectPolicy = new org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClientReconnectPolicy(
                                       InitialDelay:  csmsSettings.ReconnectInitialDelay,
                                       MaxDelay:      csmsSettings.ReconnectMaxDelay
                                   );

            var signedIn = $"This local controller signed in to the CSMS at {url} " +
                           $"({(csmsSettings.WantsClientCertificate ? "with its certificate" : totpConfig is not null ? "with a one-time token" : "with a password")}, " +
                           $"security profile {csmsSettings.SecurityProfile}).";

            var attempted = TimeProvider.GetUtcNow();

            try
            {

                var response = await lc01.ConnectOCPPWebSocketClient(

                                         RemoteURL:                 URL.Parse(url),
                                         NextHopNetworkingNodeId:   NetworkingNode_Id.Parse(csmsSettings.NextHopNodeId ?? CSMSConfiguration.DefaultNextHopNodeId),

                                         HTTPAuthentication:        authentication,
                                         TOTPConfig:                totpConfig,
                                         SecWebSocketProtocols:     csmsSettings.Subprotocols,

                                         DisableWebSocketPings:     false,
                                         WebSocketPingEvery:        csmsSettings.PingEvery,
                                         RequestTimeout:            csmsSettings.RequestTimeout,

                                         TLSProtocols:              csmsSettings.MinimumTLSVersion,

                                         RemoteCertificateValidator: judgeTheCSMS,
                                         ClientCertificateContext:   identity,

                                         DNSClient:                 DNSClient

                                     );

                csmsConnected = response?.HTTPStatusCode == HTTPStatusCode.SwitchingProtocols;

                if (csmsConnected)
                {

                    csmsConnectedSince  = TimeProvider.GetUtcNow();
                    csmsLastProblem     = null;

                    Log.Notice(signedIn, "ocpp", "csms", "auth");

                }

                else
                {

                    // An attempt that found nothing listening, or no address for
                    // the name, has made no request, and the answer to it is not
                    // the CSMS's: nobody refused anything. Nor has a CSMS that
                    // answers "not yet" - 408, 429, a 5xx from a proxy whose CSMS
                    // is still starting - which the client comes back from.
                    var why          = response?.HTTPBodyAsJSONObject?["message"]?.Value<String>();

                    // Nor has one whose certificate this controller did not
                    // believe - but that is worth saying in its own words
                    // rather than as a CSMS that could not be reached.
                    var judged       = LastJudgementOf(CSMSService, host);
                    var unbelieved   = judged is { Accepted: false } && judged.At >= attempted;

                    csmsLastProblem  = (response?.HTTPRequest is null
                                           ? unbelieved
                                                 ? $"The CSMS at {url} showed a certificate this local controller does not believe: {OutcomeSaid(judged!.Outcome)}."
                                                 : $"The CSMS at {url} could not be reached{(why is null ? "" : $": {why.TrimEnd('.')}")}."
                                           : TheCSMSClient()?.KeepsTrying == true
                                                 ? $"The CSMS at {url} cannot let this local controller in yet: {response.HTTPStatusCode}."
                                                 : $"The CSMS at {url} refused this local controller: {response.HTTPStatusCode}.") +
                                       WhatComesNext();

                    Log.Warning(csmsLastProblem, "ocpp", "csms", "auth");

                }

            }
            catch (Exception e)
            {

                csmsConnected    = false;
                csmsLastProblem  = $"The CSMS at {url} could not be reached: {e.Message.TrimEnd('.')}." + WhatComesNext();

                Log.Warning(csmsLastProblem, "ocpp", "csms");

            }

            // Whether the first attempt got through or not: the client is the
            // node's either way, and goes on by itself.
            WireCSMSLogging(url, signedIn);

            csmsAsBuilt = csmsSettings;


            // Asked of the client rather than read from the answer: a CSMS that
            // is not there yet, or not ready, is dialled again by itself; an
            // answer that means no - a wrong password, a wrong address - is an
            // answer, and is not.
            String WhatComesNext()

                => TheCSMSClient()?.KeepsTrying == true
                       ? " It is dialled again by itself."
                       : " It is not dialled again before this local controller is restarted.";

        }

        /// <summary>
        /// Hang up, if there is anything to hang up.
        /// </summary>
        private async Task DisconnectCSMS()
        {

            foreach (var client in lc01.OCPPWebSocketClients.ToArray())
            {
                try
                {
                    if (client is org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient webSocketClient)
                    {

                        // Its policy first. A close asked for here ends the
                        // client's dialling anyway; without a policy it is also
                        // known afterwards to have been hung up here, so that an
                        // attempt it cuts off is not taken for the CSMS's no.
                        webSocketClient.ReconnectPolicy = null;

                        await webSocketClient.Close();

                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"The connection to the CSMS did not close cleanly: {e.Message}", "ocpp", "csms");
                }
            }

            if (csmsConnected)
                Log.Notice("This local controller signed out of the CSMS.", "ocpp", "csms");

            csmsConnected       = false;
            csmsConnectedSince  = null;

        }

        #endregion

        #region (private) TheCSMSClient()

        /// <summary>
        /// The client the node made for the line when it was dialled: made
        /// inside the call, and kept by the node whether it got through or not.
        /// </summary>
        private org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient? TheCSMSClient()

            => lc01.OCPPWebSocketClients.
                   OfType<org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient>().
                   LastOrDefault();

        #endregion

        #region (private) WireCSMSLogging(URL, SignedIn)

        /// <summary>
        /// What of the line upwards ends up in the event log.
        /// </summary>
        /// <remarks>
        /// A backend that comes and goes is the thing an operator most needs to
        /// see, so none of this sits behind a switch: these are a handful of
        /// lines a day on a healthy connection, and the ones on an unhealthy one
        /// are the whole point.
        /// </remarks>
        /// <param name="URL">Where the line goes.</param>
        /// <param name="SignedIn">What is said when it gets through.</param>
        private void WireCSMSLogging(String  URL,
                                     String  SignedIn)
        {

            foreach (var client in lc01.OCPPWebSocketClients)
            {

                if (client is not org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient webSocketClient)
                    continue;

                // Whether the line has been up, so that one that never was is
                // not said to be lost - and keeps what its first attempt said,
                // which is more than "not yet".
                var wasUp = csmsConnected;

                // Told when the line is about to be dialled again rather than
                // dialling again here: the client already does the waiting, the
                // backing off and the counting.
                webSocketClient.OnReconnecting += (timestamp, sender, attempt, delay, cancellationToken) => {

                    csmsConnected       = false;
                    csmsConnectedSince  = null;

                    if (wasUp)
                        csmsLastProblem = $"The connection to the CSMS was lost; trying again in {delay.TotalSeconds:0} second(s).";

                    Log.Warning((wasUp
                                     ? "The connection to the CSMS was lost. "
                                     : $"The CSMS at {URL} was not reached. ") +
                                $"Attempt {attempt} follows in {delay.TotalSeconds:0} second(s).",
                                "ocpp", "csms");

                    return Task.CompletedTask;

                };

                // And told when it has got through: to a CSMS that was not there
                // when this controller started, or back to one that went away.
                // Said as a line that got through at once is, or the page went
                // on saying "lost" of a line long since back.
                webSocketClient.OnWebSocketConnectionAccepted += (timestamp, sender, connection, response, cancellationToken) => {

                    csmsConnected       = true;
                    csmsConnectedSince  = TimeProvider.GetUtcNow();
                    csmsLastProblem     = null;

                    Log.Notice(wasUp
                                   ? $"This local controller is connected to the CSMS at {URL} again."
                                   : SignedIn,
                               "ocpp", "csms", "auth");

                    wasUp = true;

                    return Task.CompletedTask;

                };

                // And told of every answer, for the one that ends the dialling: a
                // CSMS that turns this controller away - a password changed at
                // the other end, an address that is no longer there - is not
                // asked again, and the line went on saying "trying again" of a
                // client that had stopped. Not for a client this controller has
                // hung up, which it took the policy away from first: an attempt
                // that is cut off ends, possibly after the controller has
                // stopped, with an answer of the client's own making, which is
                // nobody's refusal.
                webSocketClient.ResponseLogDelegate += (timestamp, sender, request, response) => {

                    if (webSocketClient.ReconnectPolicy is null                       ||
                        response.HTTPStatusCode == HTTPStatusCode.SwitchingProtocols  ||
                        webSocketClient.KeepsTrying)
                    {
                        return Task.CompletedTask;
                    }

                    var refused = $"The CSMS at {URL} refused this local controller: {response.HTTPStatusCode}. " +
                                   "It is not dialled again before this local controller is restarted.";

                    csmsConnected       = false;
                    csmsConnectedSince  = null;

                    // Said once: the answer to the first attempt may arrive here
                    // as well, after ConnectCSMS() has said the same of it.
                    if (csmsLastProblem != refused)
                    {
                        csmsLastProblem = refused;
                        Log.Warning(refused, "ocpp", "csms", "auth");
                    }

                    return Task.CompletedTask;

                };

            }

        }

        #endregion


        #region (private) TryCSMSIdentity(out Identity, out Problem)

        /// <summary>
        /// The TLS identity chosen to sign in to the CSMS with under security
        /// profile 3, with the certificates that travel with it - or why there
        /// is none to sign in with.
        /// </summary>
        /// <remarks>
        /// Read from the certificate store at every dialling rather than kept:
        /// an identity switched off, renewed or deleted on the store's page is
        /// what the next attempt goes by.
        /// </remarks>
        private Boolean TryCSMSIdentity([NotNullWhen(true)]  out SslStreamCertificateContext?  Identity,
                                        [NotNullWhen(false)] out String?                       Problem)
        {

            Identity  = null;
            Problem   = null;

            if (csmsSettings.ChosenClientCertificate is not String handle)
            {
                Problem = "Security profile 3 signs in with a TLS identity of this local controller's own, and none is chosen. " +
                          "Import one on the certificate store's page and choose it for the CSMS connection.";
                return false;
            }

            var entry = Certificates.Get(handle) ?? Certificates.ByFingerprint(handle);

            if (entry is null)
            {
                Problem = $"The TLS identity {handle} chosen for the CSMS connection is not in the certificate store.";
                return false;
            }

            if (entry.Kind != CertificateKind.TLSIdentity)
            {
                Problem = $"'{entry.Label}' ({entry.Id}), chosen for the CSMS connection, is not a TLS identity " +
                          $"this local controller could sign in with, but a {entry.Kind.Describe()}.";
                return false;
            }

            if (!entry.IsUsable)
            {
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection is " +
                          (!entry.IsActive     ? "switched off"
                         : entry.IsExpired     ? $"expired since {entry.NotAfter:yyyy-MM-dd}"
                         : entry.IsNotYetValid ? $"not valid before {entry.NotBefore:yyyy-MM-dd}"
                         :                       "not usable") + ".";
                return false;
            }

            if (!Certificates.TryLoad(entry, out var certificate, out var error))
            {
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection could not be read: {error.TrimEnd('.')}.";
                return false;
            }

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection has no private key to sign in with.";
                return false;
            }

            // Loaded once more, with a key the platform keeps for as long as the
            // certificate is used - as the station port's own certificates are.
            // The store reads its keys as ephemeral, and SChannel on Windows
            // takes no key it cannot find again: the first handshake with one
            // failed with "the credentials supplied to the package were not
            // recognized".
            X509Certificate2 presented;

            try
            {
                var password = Guid.NewGuid().ToString("N");
                presented    = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12, password),
                                                                password,
                                                                X509KeyStorageFlags.Exportable);
            }
            catch (Exception e)
            {
                Problem = $"The TLS identity '{entry.Label}' ({entry.Id}) chosen for the CSMS connection could not be made ready to present: {e.Message.TrimEnd('.')}.";
                return false;
            }
            finally
            {
                certificate.Dispose();
            }

            // The certificates in its file besides its own - the sub-CAs a
            // CSMS needs to build its chain up to a root it trusts.
            var chain = new X509Certificate2Collection();

            try
            {

                var file   = Certificates.FullPath(entry);
                var others = Path.GetExtension(file).ToLowerInvariant() is ".p12" or ".pfx"
                                 ? X509CertificateLoader.LoadPkcs12CollectionFromFile(file, null, X509KeyStorageFlags.EphemeralKeySet)
                                 : LoadPEM(file);

                foreach (var other in others)
                    if (!String.Equals(other.Thumbprint, presented.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        chain.Add(other);

                static X509Certificate2Collection LoadPEM(String File)
                {
                    var collection = new X509Certificate2Collection();
                    collection.ImportFromPemFile(File);
                    return collection;
                }

            }
            catch
            {
                // A file that yields its certificate but no collection has no
                // sub-CAs worth sending: the certificate alone is what there is.
            }

            Identity = SslStreamCertificateContext.Create(presented, chain, offline: true);
            return true;

        }

        #endregion

        #region (private static) OutcomeSaid(Outcome)

        /// <summary>
        /// What the node made of a server's certificate, in a few words - the
        /// words of the pages, which say the same of a time server's.
        /// </summary>
        private static String OutcomeSaid(String Outcome)

            => Outcome switch {
                   "accepted"       => "believed",
                   "recorded"       => "used, although not what it is held to - recorded",
                   "tolerated"      => "used, although not what it is held to",
                   "pinMismatch"    => "not what it is held to",
                   "untrusted"      => "it chains to no root that is trusted",
                   "wrongName"      => "it was not issued for its name",
                   "noCertificate"  => "it showed no certificate",
                   _                => Outcome
               };

        #endregion

        #region UsedByCSMS(Handle)

        /// <summary>
        /// The setting of the CSMS connection that names this certificate, or
        /// null where none does.
        /// </summary>
        public String? UsedByCSMS(String? Handle)
        {

            if (Handle is null or { Length: 0 } || csmsSettings.ChosenClientCertificate is not String chosen)
                return null;

            var entry = Certificates.Get(Handle);

            return String.Equals(chosen, Handle,          StringComparison.OrdinalIgnoreCase) ||
                   String.Equals(chosen, entry?.Thumbprint, StringComparison.OrdinalIgnoreCase)
                       ? "clientCertificate"
                       : null;

        }

        #endregion


        #region CSMSConfigurationJSON()

        /// <summary>
        /// The CSMS connection as the web interface reads it: what it is
        /// configured as, what it is doing, and what it signs in with.
        /// </summary>
        public JObject CSMSConfigurationJSON()
        {

            var json = csmsSettings.ToJSON();

            // The TLS identity chosen for security profile 3, resolved against
            // the store, so that the page can say what is chosen - and that it
            // is missing, or switched off - without reading the store, which not
            // everybody who may look at this page may do.
            if (csmsSettings.ChosenClientCertificate is String handle)
            {

                var entry = Certificates.Get(handle) ?? Certificates.ByFingerprint(handle);

                json.Add("clientCertificateIs", entry is null
                                                    ? new JObject(
                                                          new JProperty("id",       handle),
                                                          new JProperty("missing",  true)
                                                      )
                                                    : new JObject(
                                                          new JProperty("id",        entry.Id),
                                                          new JProperty("missing",   false),
                                                          new JProperty("label",     entry.Label),
                                                          new JProperty("subject",   entry.Subject),
                                                          new JProperty("notAfter",  entry.NotAfter.ToString("o")),
                                                          new JProperty("usable",    entry.IsUsable && entry.Kind == CertificateKind.TLSIdentity)
                                                      ));

            }

            json.Add("state", new JObject(
                new JProperty("connected",           csmsConnected),
                new JProperty("connectedSince",      csmsConnectedSince?.ToString("o")),
                new JProperty("lastProblem",         csmsLastProblem),
                new JProperty("hasCredentials",      CSMSLogin.HasPassword || CSMSLogin.HasTOTP),
                new JProperty("waitingForARestart",  new JArray(csmsSettings.NeedsARestartFor(csmsAsBuilt)))
            ));

            json.Add("credentials", CSMSLogin.ToJSON());

            return json;

        }

        #endregion

        #region TryUpdateCSMSConfiguration(JSON, out Error)

        /// <summary>
        /// Change the CSMS connection, and write it to the configuration file.
        /// </summary>
        public Boolean TryUpdateCSMSConfiguration(JObject                           JSON,
                                                  [NotNullWhen(false)] out String?  Error)
        {

            if (!CSMSConfiguration.TryParse(JSON, out var changes, out Error))
                return false;

            var wanted = csmsSettings.Merge(changes).Effective();

            if (!ConfigFile.TryMergeSection(CSMSConfiguration.SectionName, changes.ToJSON(), out Error))
                return false;

            csmsSettings = wanted;

            return true;

        }

        #endregion

    }

}
