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

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.protocols.WWCP.Node.Logging;
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

            if (csmsSettings.WantsClientCertificate)
            {
                // Profile 3 needs a certificate of this controller's own, and
                // that store is not built yet. Said plainly rather than dialled
                // without one, which would be refused at the other end with a
                // message nobody here could act on.
                csmsLastProblem = "Security profile 3 needs a client certificate of this local controller's own, " +
                                  "and there is no store for one yet. Use security profile 2 until there is.";
                Log.Critical(csmsLastProblem, "ocpp", "csms", "tls");
                return;
            }

            var authentication = CSMSLogin.HTTPAuthentication();
            var totpConfig     = CSMSLogin.TOTPConfig();

            if (authentication is null && totpConfig is null)
            {
                csmsLastProblem = $"This local controller has no credentials to sign in to {url} with. " +
                                   "Set them under Configuration.";
                Log.Warning(csmsLastProblem, "ocpp", "csms", "auth");
                return;
            }

            #endregion

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
                           $"({(totpConfig is not null ? "with a one-time token" : "with a password")}, " +
                           $"security profile {csmsSettings.SecurityProfile}).";

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
                    // the CSMS's: nobody refused anything.
                    var why          = response?.HTTPBodyAsJSONObject?["message"]?.Value<String>();

                    csmsLastProblem  = (response?.HTTPRequest is null
                                           ? $"The CSMS at {url} could not be reached{(why is null ? "" : $": {why.TrimEnd('.')}")}."
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
                    // A close asked for here ends the client's dialling as well:
                    // it is not a loss to come back from.
                    if (client is org.GraphDefined.Vanaheimr.Hermod.WebSocket.WebSocketClient webSocketClient)
                        await webSocketClient.Close();
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

            }

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
