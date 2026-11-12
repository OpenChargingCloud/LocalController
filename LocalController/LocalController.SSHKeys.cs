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

using org.GraphDefined.Vanaheimr.Hermod;

using OCPPv2_1    = cloud.charging.open.protocols.OCPPv2_1;
using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// The SSH keys of the charging stations, as the CSMS installs, lists and deletes them over OCPP
    /// (white paper "OCPP over SSH", section 9.3, decision 7): InstallSSHKey, DeleteSSHKey and
    /// GetInstalledSSHKeys, kept with the stations' logins, as the page and the command line keep them.
    /// </summary>
    public partial class LocalController
    {

        #region (private) HostKeyFingerprints

        /// <summary>
        /// The SHA-256 fingerprints of this local controller's host keys: what the CSMS gives its stations
        /// as pins.
        /// </summary>
        private String[] HostKeyFingerprints

            => SSHHostKey is String hostKey && hostKey.Split(' ', 2) is [ _, var fingerprint ]
                   ? [ fingerprint ]
                   : [];

        #endregion

        #region (private) AttachSSHKeyMessages(Node)

        /// <summary>
        /// Answer the CSMS's SSH key messages from the stations' logins. Only keys of OCPP are taken: this
        /// local controller serves its stations ocpp@open.charging.cloud and nothing else over SSH.
        /// </summary>
        private void AttachSSHKeyMessages(OCPPv2_1_LC.TestLocalControllerNode Node)
        {

            Node.OCPP.IN.OnInstallSSHKey += (timestamp, sender, connection, request, cancellationToken) => {

                if (request.Purpose != OCPPv2_1.SSHKeyPurpose.OCPP)
                    return Task.FromResult(new OCPPv2_1.CS.InstallSSHKeyResponse(request, OCPPv2_1.GenericStatus.Rejected,
                                                                                 new OCPPv2_1.StatusInfo("NotSupported", "This local controller lets its stations in over SSH for OCPP only."),
                                                                                 HostKeys: HostKeyFingerprints));

                if (!StationLogins.TryAddSSHKey(request.NetworkingNodeId.ToString(),
                                                request.PublicKey,
                                                "Installed by the CSMS",
                                                null,
                                                null,
                                                $"OCPP {request.NetworkPath.Source}",
                                                out _,
                                                out var error))
                {

                    Log.Log(protocols.WWCP.Node.Logging.LogLevel.Warning,
                            $"The CSMS's SSH key for the charging station '{request.NetworkingNodeId}' was not taken: {error}",
                            "ocpp", "station", "ssh");

                    return Task.FromResult(new OCPPv2_1.CS.InstallSSHKeyResponse(request,
                                                                                 OCPPv2_1.GenericStatus.Rejected,
                                                                                 new OCPPv2_1.StatusInfo(ReasonOf(error), error),
                                                                                 HostKeys: HostKeyFingerprints));

                }

                Log.Log(protocols.WWCP.Node.Logging.LogLevel.Info,
                        $"The CSMS installed an SSH key for the charging station '{request.NetworkingNodeId}'.",
                        "ocpp", "station", "ssh");

                return Task.FromResult(new OCPPv2_1.CS.InstallSSHKeyResponse(request,
                                                                             OCPPv2_1.GenericStatus.Accepted,
                                                                             null,
                                                                             HostKeys: HostKeyFingerprints));

            };

            Node.OCPP.IN.OnDeleteSSHKey += (timestamp, sender, connection, request, cancellationToken) => {

                var deleted = StationLogins.TryRemoveSSHKey(request.NetworkingNodeId.ToString(), request.KeyFingerprint, out var error);

                return Task.FromResult(deleted
                                           ? new OCPPv2_1.CS.DeleteSSHKeyResponse(request, OCPPv2_1.GenericStatus.Accepted)
                                           : new OCPPv2_1.CS.DeleteSSHKeyResponse(request, OCPPv2_1.GenericStatus.Rejected, new OCPPv2_1.StatusInfo("NotFound", error)));

            };

            Node.OCPP.IN.OnGetInstalledSSHKeys += (timestamp, sender, connection, request, cancellationToken) => {

                var keys = request.Purpose is OCPPv2_1.SSHKeyPurpose purpose && purpose != OCPPv2_1.SSHKeyPurpose.OCPP
                               ? []
                               : StationLogins.Logins.
                                     Where     (login => request.NetworkingNodeId is null || login.Id == request.NetworkingNodeId.Value.ToString()).
                                     SelectMany(login => login.Keys.Select(key => new OCPPv2_1.InstalledSSHKey(protocols.WWCP.NetworkingNode.NetworkingNode_Id.Parse(login.Id),
                                                                                                               OCPPv2_1.SSHKeyPurpose.OCPP,
                                                                                                               key.Line,
                                                                                                               key.Fingerprint))).
                                     ToArray   ();

                return Task.FromResult(keys.Length > 0 || request.NetworkingNodeId is null
                                           ? new OCPPv2_1.CS.GetInstalledSSHKeysResponse(request, OCPPv2_1.GenericStatus.Accepted, Keys: keys, HostKeys: HostKeyFingerprints)
                                           : new OCPPv2_1.CS.GetInstalledSSHKeysResponse(request, OCPPv2_1.GenericStatus.Rejected, new OCPPv2_1.StatusInfo("NotFound", $"No SSH key of '{request.NetworkingNodeId}' is installed here."),
                                                                                         HostKeys: HostKeyFingerprints));

            };

        }

        /// <summary>
        /// The reason code of a key that was not taken, from the sentence that says why.
        /// </summary>
        private static String ReasonOf(String Error)

            => Error.Contains("already lets the charging station", StringComparison.Ordinal) ? "KeyOfOtherStation"
             : Error.Contains("cert-authority",                    StringComparison.Ordinal) ? "CertAuthority"
             : Error.Contains("at most",                           StringComparison.Ordinal) ? "TooManyKeys"
             : "InvalidValue";

        #endregion

    }

}
