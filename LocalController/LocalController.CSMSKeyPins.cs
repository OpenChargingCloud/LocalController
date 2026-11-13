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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using OCPPv2_1    = cloud.charging.open.protocols.OCPPv2_1;
using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The line up to the CSMS, key-pinned as a charging station's wss:// connections are ("Key Pins and
    /// Raw Public Keys for Local Controllers", sections 5.4 to 5.7): its CSMS known by the key pin of its
    /// key, learned on first use where that is configured and on probation until a signed pin set
    /// confirms it - decided by WWCP_OCPP_Node's key-pin core, which the station uses as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While the line is on probation nothing that comes over it is carried out or sent on: not what is
    /// for this controller, and not what is for a station behind it - a server nobody confirmed could
    /// otherwise reach every station of the site. The pin set for this controller's own
    /// TLSClientController, and reads of it, are let through, as at a station.
    /// </para>
    /// <para>
    /// The security events of section 7.5 go to the CSMS as this controller's own.
    /// </para>
    /// </remarks>
    public partial class LocalController
    {

        #region Data

        // The connections on probation, and those not believed any more: one
        // registry, asked by both gates of the OCPP node.
        private readonly Probations         csmsProbations      = new ();

        // When the response to a SetVariables request is out.
        private readonly ResponsesSent      csmsResponsesSent   = new ();

        private          ServerKeyPinning?           csmsKeyPinning;
        private          TLSClientControllerSource?  csmsTLSClientSource;

        #endregion

        #region Properties

        /// <summary>
        /// How this local controller believes its CSMS beyond what a CA says: its key pins, one learned,
        /// and the signers of pin sets.
        /// </summary>
        public CSMSKeyPins  CSMSPins  { get; private set; } = null!;

        /// <summary>
        /// The connections on probation and those not believed any more, for a test that asks what the
        /// gate would say at a given moment.
        /// </summary>
        internal Probations  CSMSProbations
            => csmsProbations;

        /// <summary>
        /// The key pins of the line up to the CSMS, for a test.
        /// </summary>
        internal ServerKeyPinning?  CSMSKeyPinning
            => csmsKeyPinning;

        #endregion


        #region (private) BuildCSMSKeyPins(Directory)

        /// <summary>
        /// How this controller believes its CSMS: written down beside the configuration file, and decided
        /// by WWCP_OCPP_Node's key-pin core.
        /// </summary>
        private void BuildCSMSKeyPins(String Directory)
        {

            CSMSPins = new CSMSKeyPins(
                           System.IO.Path.Combine(Directory, CSMSKeyPins.DefaultFileName),
                           () => csmsSettings.URL,
                           DialAgain: () => _ = Task.Run(async () => {
                                                    await DisconnectCSMS();
                                                    await ConnectCSMS();
                                                }),
                           TimeProvider
                       );

            if (!CSMSPins.TryLoad(out var error))
                Log.Critical($"How this local controller believes its CSMS could not be read: {error}", "ocpp", "csms", "keypins", "security");

            csmsKeyPinning = new ServerKeyPinning(
                                 CSMSPins,
                                 csmsProbations,
                                 Notice:         text                       => Log.Notice (text, "ocpp", "csms", "keypins", "security"),
                                 Warning:        text                       => Log.Warning(text, "ocpp", "csms", "keypins", "security"),
                                 SecurityEvent:  (endpoint, type, techInfo) => KeyPinSecurityEvent(type, techInfo),
                                 Failed:         (endpoint, status, what)   => {
                                                                                   csmsConnected    = false;
                                                                                   csmsLastProblem  = what;
                                                                                   Log.Warning(what, "ocpp", "csms", "keypins", "security");
                                                                               },
                                 TimeProvider:   TimeProvider,
                                 NodeName:       "local controller"
                             );

            csmsTLSClientSource = new TLSClientControllerSource(
                                      csmsKeyPinning,
                                      ClientKeyPins,
                                      SignedByPinSetSigner,
                                      csmsResponsesSent,
                                      "local controller"
                                  );

        }

        #endregion

        #region (private) AttachCSMSKeyPins(Node)

        /// <summary>
        /// What the OCPP node answers of the line up to the CSMS: TLSClientController - an instance for the
        /// line, one for this controller's own key - when the answer to a SetVariables request is out, and
        /// a pin set held to the signers of pin sets rather than to a role.
        /// </summary>
        private void AttachCSMSKeyPins(OCPPv2_1_LC.TestLocalControllerNode Node)
        {

            FollowCSMSKeyPinComponents();

            Node.OCPP.OUT.OnSetVariablesResponseSent += (timestamp, sender, connection, request, response, runtime, sentMessageResult, cancellationToken) => {
                csmsResponsesSent.Sent(request);
                return Task.CompletedTask;
            };

            // A pin set is signed by the CSMS's signing anchor, which holds no
            // role: the policy leaves it to the signers of pin sets, which the
            // line's own source holds it to whatever the mode.
            Node.OCPP.SignaturePolicy.AddPolicyEntries(
                new[] { "ServerKeyPins", "ServerKeyPinsVersion" }.Select(variable => new MessagePolicyEntry(
                    Id:           $"pin set TLSClientController.{variable}",
                    Action:       "SetVariables",
                    Require:      SignerRequirement.Unsigned,
                    Direction:    "CSMS->CS",
                    Component:    nameof(OCPPv2_1.TLSClientController),
                    Variable:     variable,
                    Description:  "A pin set of the line up to the CSMS: held to the signers of pin sets by the local controller itself"
                ))
            );

        }

        #endregion

        #region (internal) FollowCSMSKeyPinComponents()

        /// <summary>
        /// A TLSClientController instance for the line where it goes over TLS, and none where it does not:
        /// asked again whenever the line's configuration changed.
        /// </summary>
        internal void FollowCSMSKeyPinComponents()

            => csmsTLSClientSource?.FollowComponents(lc01, warning => Log.Warning(warning, "ocpp", "csms", "keypins"));

        #endregion


        #region CSMSKeyPinsJSON()

        /// <summary>
        /// How the line believes its CSMS, as the page and the command line see it: what is written down,
        /// and what its trust says now - the key pin shown last, whether it is on probation.
        /// </summary>
        public JObject CSMSKeyPinsJSON()
        {

            var json   = CSMSPins.ToJSON();
            var line   = CSMSPins.Current;
            var trust  = csmsKeyPinning?.TrustOf(CSMSKeyPins.LineId);

            json.Add("keyPinned",    line?.IsKeyPinned ?? false);
            json.Add("overTLS",      line?.IsSecure    ?? false);
            json.Add("onProbation",  trust?.OnProbation ?? false);

            if (trust?.Shown is KeyPin shown)
                json.Add("keyPinShown", shown.ToHex());

            // Learned while it could not be written down: held by the trust alone.
            if (json["serverKeyPinLearned"] is null && trust?.Learned is KeyPin learned)
                json.Add("serverKeyPinLearned", learned.ToHex());

            return json;

        }

        #endregion

        #region (private) KeyPinTrustOfTheCSMS(out NotDialled)

        /// <summary>
        /// The trust the line up to the CSMS is dialled with where it is key-pinned; null where its CSMS is
        /// judged by its certificate, as it always was.
        /// </summary>
        private ServerKeyPinTrust? KeyPinTrustOfTheCSMS(out IPinnedTLSEndpoint? Line,
                                                        out String?             NotDialled)
        {

            NotDialled  = null;
            Line        = CSMSPins.Current;

            return Line is { IsKeyPinned: true } && csmsKeyPinning is not null
                       ? csmsKeyPinning.TrustFor(Line, out NotDialled)
                       : null;

        }

        #endregion

        #region (private) KeyPinSecurityEvent(Type, TechInfo)

        /// <summary>
        /// A security event of the line, sent to the CSMS as this controller's own.
        /// </summary>
        private void KeyPinSecurityEvent(String  Type,
                                         String  TechInfo)
        {
            try
            {
                _ = lc01.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type), TechInfo);
            }
            catch (Exception e)
            {
                Log.Warning($"The security event '{Type}' could not be queued: {e.Message}", "ocpp", "csms", "keypins", "security");
            }
        }

        #endregion

        #region (private) ClientKeyPins()

        /// <summary>
        /// The key pin of the TLS identity this controller shows its CSMS, where it shows one.
        /// </summary>
        private IEnumerable<KeyPin> ClientKeyPins()
        {

            if (!csmsSettings.WantsClientCertificate || !TryCSMSIdentity(out var identity, out _) || identity?.TargetCertificate is null)
                return [];

            try
            {
                return [ KeyPin.FromCertificate(identity.TargetCertificate) ];
            }
            catch
            {
                return [];
            }

        }

        #endregion

        #region (private) SignedByPinSetSigner(Request, Line, WithoutTimeWindow, out Signer, out Why)

        /// <summary>
        /// Whether a signer of pin sets this controller believes signed the given SetVariables request: by
        /// this controller's id, as its CSMS signs for it.
        /// </summary>
        private Boolean SignedByPinSetSigner(OCPPv2_1.CSMS.SetVariablesRequest?  Request,
                                             IPinnedTLSEndpoint                  Line,
                                             Boolean                             WithoutTimeWindow,
                                             [NotNullWhen(true)]  out String?    Signer,
                                             [NotNullWhen(false)] out String?    Why)
        {

            var json = Request?.ToJSON(true,
                                       lc01.OCPP.CustomSetVariablesRequestSerializer,
                                       lc01.OCPP.CustomSetVariableDataSerializer,
                                       lc01.OCPP.CustomComponentSerializer,
                                       lc01.OCPP.CustomEVSESerializer,
                                       lc01.OCPP.CustomVariableSerializer,
                                       lc01.OCPP.CustomSignatureSerializer,
                                       lc01.OCPP.CustomCustomDataSerializer);

            return PinSetSignatures.SignedBy(Request,
                                             json,
                                             CSMSPins.PinSetSigners,
                                             lc01.Id,
                                             OCPPv2_1.Version.WebSocketSubProtocolId,
                                             "TLS",
                                             WithoutTimeWindow,
                                             () => TimeProvider.GetUtcNow(),
                                             out Signer,
                                             out Why,
                                             "local controller");

        }

        #endregion


        #region (private) GateOnCSMSProbation(Connection, Action, Payload)

        /// <summary>
        /// Why a request that came over the line up to the CSMS is not carried out here: the line is on
        /// probation, and it is no pin set for this controller - or its CSMS is not believed any more.
        /// </summary>
        private String? GateOnCSMSProbation(IMessageConnection  Connection,
                                            String              Action,
                                            JObject?            Payload)

            => csmsProbations.Gate(Connection, Action, Payload, (probation, action, payload) => KeyPinGate.Rules(probation, action, payload, "local controller"));

        #endregion

        #region (private) WhyNotForwardOnProbation(Connection, Action)

        /// <summary>
        /// Why a request that came over the line up to the CSMS is not sent on to a station: the line is on
        /// probation, or its CSMS is not believed any more. Nothing for a station passes then - not even a
        /// pin set, which is for the station's own connection and not this line's.
        /// </summary>
        private String? WhyNotForwardOnProbation(IMessageConnection  Connection,
                                                 String              Action)

            => csmsProbations.Gate(Connection, Action, null, (probation, action, payload) =>
                   $"This local controller believes its CSMS by the key pin {(probation.Trust as ServerKeyPinTrust)?.Learned}, which it learned on its first " +
                   $"connection and which is on probation: until a signed pin set confirms it, nothing it sends is passed on to a station ('{action}').");

        #endregion

    }

}
