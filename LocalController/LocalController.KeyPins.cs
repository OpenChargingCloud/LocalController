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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using OCPPv2_1    = cloud.charging.open.protocols.OCPPv2_1;
using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The key pins of the station port as the CSMS sees them: EVQICtrlr[TLSServer] of this local
    /// controller's own Device Model (white paper "Key Pins and TLS 1.3 Raw Public Keys for Local
    /// Controllers", sections 5.5 and 7.4) - the pins of the current and of the announced next key,
    /// when the current one was made, and the switch to the next key when the CSMS writes its pin into
    /// ActiveKeyPin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The keys are the store's, <see cref="ServerCertificates"/>, and nothing else's: every read asks
    /// the store, so that a next key made or a switch done on the command line or the page is what the
    /// CSMS reads next, and the CSMS's switch is the store's own <c>TrySwitchToNextKey</c>, the path of
    /// "serverKeys switch" and of the page. The Device Model holds no value of its own.
    /// </para>
    /// <para>
    /// A switch takes effect at the next TLS handshake: the station port chooses what it presents per
    /// connection, so nothing is closed or restarted, and a station connected with the key before stays
    /// connected.
    /// </para>
    /// <para>
    /// Every switch, whoever asked for it, is told to the CSMS as the security event
    /// ReconfigurationOfSecurityParameters with both key pins: the CSMS is the one that hands the stations
    /// their pins, and has to hear of a switch it did not ask for most of all.
    /// </para>
    /// <para>
    /// Only the CSMS writes: a SetVariables addressed to this controller that comes from a charging
    /// station connected to it is refused at the door, with everything else only the CSMS asks, see
    /// <see cref="CSMSOnlyActions"/>.
    /// Whether the CSMS's signer may switch the key ("certificates:edit") is not checked yet, see
    /// <see cref="TLSServerKeys"/>.
    /// </para>
    /// </remarks>
    public partial class LocalController
    {

        #region (private) AttachKeyPins(Node)

        /// <summary>
        /// Give the node EVQICtrlr[TLSServer], read from and switched through the station port's key
        /// store.
        /// </summary>
        /// <remarks>
        /// Before the store is made, which is fine: the component reads it at each request, and none
        /// arrives before the controller has started.
        /// </remarks>
        private void AttachKeyPins(OCPPv2_1_LC.TestLocalControllerNode Node)
        {

            // One of its kind: one made for an earlier node is not this controller's.
            Node.RemoveComponent(nameof(OCPPv2_1.EVQICtrlr), OCPPv2_1.EVQICtrlr.TLSServer);

            if (!Node.AddComponent(new OCPPv2_1.EVQICtrlr(new TLSServerKeys(this))))
                Log.Warning($"The OCPP node '{Node.Id}' did not take EVQICtrlr[{OCPPv2_1.EVQICtrlr.TLSServer}]: the CSMS cannot read the key pins of the station port.",
                            "ocpp", "tls");

        }

        #endregion

        #region (private) ReportServerKeySwitch(Before, After)

        /// <summary>
        /// Tell the CSMS that the station port presents another key now: the security event
        /// ReconfigurationOfSecurityParameters, with the key pin before and the one now.
        /// </summary>
        /// <remarks>
        /// Queued by the node, and sent at once where the CSMS is connected. Not waited for: the switch
        /// is done, and whoever asked for it - a command, a page, the CSMS's own SetVariables - is not
        /// held up by a CSMS that is slow to answer. Where the CSMS cannot be reached the event waits in
        /// the node's queue, and the log says what happened in any case.
        /// </remarks>
        private void ReportServerKeySwitch(KeyPin?  Before,
                                           KeyPin   After)
        {

            var techInfo = $"TLS server key switched: key pin {Before?.ToHex() ?? "none"} -> {After.ToHex()}";

            _ = Task.Run(async () => {
                    try
                    {
                        await lc01.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.ReconfigurationOfSecurityParameters, techInfo);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"The switch of the server key to the key pin {After} could not be told to the CSMS: {e.Message}",
                                    "ocpp", "tls", "csms");
                    }
                });

        }

        #endregion


        #region (private class) TLSServerKeys

        /// <summary>
        /// EVQICtrlr[TLSServer] over the station port's key store.
        /// </summary>
        /// <remarks>
        /// Internal for the tests, which hand the switch a key pin that stopped being the next key's
        /// between EVQICtrlr's look and the switch: nothing over OCPP can stop the request there.
        /// </remarks>
        internal sealed class TLSServerKeys(LocalController Controller) : OCPPv2_1.IEVQICtrlrSource,
                                                                          OCPPv2_1.ITLSServerKeySwitch
        {

            #region TLSServerVariables

            /// <summary>
            /// All of them: ActiveKeyPin among them, which makes the switch writable.
            /// </summary>
            public IEnumerable<String> TLSServerVariables

                => [ "KeyPins", "NextKeyPin", "KeyCreated", "RawPublicKeys", "ActiveKeyPin" ];

            #endregion

            #region TLSServerValue(Variable)

            /// <summary>
            /// What the store says now. The current key is the one presented at the next handshake, as
            /// "serverKeys" and the page show it - on a port that is not encrypted yet, the one presented
            /// from the next start.
            /// </summary>
            public String? TLSServerValue(String Variable)
            {

                ServerCertificateEntry? current  = null;
                ServerCertificateEntry? next     = null;

                // Read as one pair: a switch in between would otherwise show the
                // key before it without the next one, or the new key twice.
                if (Controller.ServerCertificates is ServerCertificateStore store)
                    current = store.GetCurrentAndNextKey(out next);

                return Variable switch {

                           // The current key first (paper, 7.4).
                           "KeyPins"        => String.Join(",", new[] { current?.KeyPin, next?.KeyPin }.
                                                                    OfType<KeyPin>().
                                                                    Select(keyPin => keyPin.ToHex())),

                           "NextKeyPin"     => next?.KeyPin.ToHex() ?? "",
                           "KeyCreated"     => current?.CreatedAt.ToISO8601(),
                           "ActiveKeyPin"   => current?.KeyPin.ToHex(),

                           // SslStream offers no raw public keys (paper, 10.3).
                           "RawPublicKeys"  => "Off",

                           _                => null

                       };

            }

            #endregion

            #region SwitchToNextKeyAsync(NextKeyPin, Request, CancellationToken)

            /// <summary>
            /// Switch the station port to its announced next key, where that is still the key with the
            /// given pin - through the store, as "serverKeys switch" and the page do.
            /// </summary>
            public Task<OCPPv2_1.StatusInfo?> SwitchToNextKeyAsync(KeyPin                              NextKeyPin,
                                                                   OCPPv2_1.CSMS.SetVariablesRequest?  Request,
                                                                   CancellationToken                   CancellationToken)
            {

                // Who, as the page and the command line say it: whom the request
                // says it comes from, over the line up to the CSMS.
                var who = Request is not null
                              ? $"'{Request.NetworkPath.Source}'"
                              : "The CSMS";

                if (RefusalOfTheRight(Request) is OCPPv2_1.StatusInfo refusal)
                    return Task.FromResult<OCPPv2_1.StatusInfo?>(refusal);

                if (Controller.ServerCertificates is not ServerCertificateStore store)
                    return Task.FromResult<OCPPv2_1.StatusInfo?>(
                               new OCPPv2_1.StatusInfo("InternalError", "This local controller has no station port, and no key to switch.")
                           );

                // Compared with the next key under the store's own lock: a switch
                // made meanwhile, and a new next key after it, cannot turn this
                // into a switch to a key the CSMS never handed out.
                if (store.TrySwitchToNextKey(NextKeyPin, out var id, out var error, out var notSaved))
                {

                    Controller.Log.Notice($"{who} switched the charging station server to the server key '{id}' over OCPP, " +
                                          $"writing its key pin {NextKeyPin} into EVQICtrlr[TLSServer].ActiveKeyPin.",
                                          "ocpp", "tls", "csms");

                    return Task.FromResult<OCPPv2_1.StatusInfo?>(null);

                }

                var current = store.GetCurrentAndNextKey(out var next);

                // Switched meanwhile, on the command line, on the page or by a
                // request before this one: what was asked for is what is there.
                if (current?.KeyPin == NextKeyPin)
                    return Task.FromResult<OCPPv2_1.StatusInfo?>(null);

                Controller.Log.Warning($"{who} asked over OCPP to switch the charging station server to the key pin {NextKeyPin}; nothing was switched: {error}",
                                       "ocpp", "tls", "csms");

                return Task.FromResult<OCPPv2_1.StatusInfo?>(
                           new OCPPv2_1.StatusInfo(
                               notSaved ? "InternalError" : "InvalidValue",
                               $"'EVQICtrlr[TLSServer].ActiveKeyPin': nothing was switched: {error} " +
                               $"The current key pin is {current?.KeyPin.ToString() ?? "none"}, the announced next one {next?.KeyPin.ToString() ?? "none"}."
                           )
                       );

            }

            #endregion

            #region (private static) RefusalOfTheRight(Request)

            /// <summary>
            /// Why the sender of the given request may not switch the server key - or null, where it may.
            /// </summary>
            /// <remarks>
            /// <para>
            /// Where the right to switch is to be checked, before the store is touched: a switch changes
            /// whom every station of the site trusts, a trust decision like a change of the certificate
            /// store, and is meant to need the right "certificates:edit" of the request's signer
            /// (white paper "Signed OCPP Messages and Signer Roles", Q12, phase 3 - messages to the local
            /// controller itself). Refused then with "NotAuthorized", as a request without that right.
            /// </para>
            /// <para>
            /// Checked before it gets here: the node's signature policy decides each entry of a
            /// SetVariables by the resource of its component, and EVQICtrlr is "certificates"
            /// (LocalController.SignaturePolicy.cs) - in the mode "Enforce" an entry whose signers may not
            /// "certificates:edit" is refused with "NotAuthorized" and never reaches the switch. In the
            /// mode "Off", the default, every SetVariables that reaches this node over its line up to the
            /// CSMS may switch - one from a station connected to it never gets here, see CSMSOnlyActions -
            /// and only to the announced next key, which EVQICtrlr and the store both hold it to.
            /// </para>
            /// </remarks>
            private static OCPPv2_1.StatusInfo? RefusalOfTheRight(OCPPv2_1.CSMS.SetVariablesRequest? Request)

                => null;

            #endregion

        }

        #endregion

    }

}
