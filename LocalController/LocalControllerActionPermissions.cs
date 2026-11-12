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

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What each OCPP 2.1 action addressed to this local controller itself
    /// needs, as permissions of the controller's role model: the table a local
    /// controller ships (white paper "Signed OCPP Messages and Signer Roles",
    /// section 5.3, Q12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The local controller's own resources, as on its web page and at its
    /// command line: what signs for the line up to the CSMS needs "csms:edit",
    /// what signs for its certificates "certificates:edit". A signer holds a
    /// role of this controller, and what that role may do here is all it may
    /// sign here.
    /// </para>
    /// <para>
    /// What the controller forwards to a charging station is not decided here:
    /// the station holds it against its own table, and the controller's role is
    /// no power of attorney for the station. The forwarding has a signature
    /// policy of its own, which this table is not put into.
    /// </para>
    /// <para>
    /// The policy table of the controller can say otherwise for any action.
    /// Nothing of it is looked at while the mode of the policy is "Off", the
    /// default.
    /// </para>
    /// </remarks>
    public static class LocalControllerActionPermissions
    {

        private const String RequestedMessage = "requestedMessage";

        /// <summary>
        /// The table.
        /// </summary>
        public static IEnumerable<ActionPermission> Default()

            => [

                   // Restarting: once idle, or at once - which on a local controller
                   // cuts every station behind it off from the CSMS.
                   ActionPermission.Needs  ("Reset",                 Run (NodeResources.Reset),           FieldCondition.Equal("type", "OnIdle")),
                   ActionPermission.Needs  ("Reset",                 Run (NodeResources.ResetImmediate)),

                   // A message asked for: a new certificate, or anything else.
                   ActionPermission.Needs  ("TriggerMessage",        Edit(NodeResources.Certificates),    FieldCondition.In   (RequestedMessage, "SignChargingStationCertificate",
                                                                                                                                                 "SignV2GCertificate",
                                                                                                                                                 "SignV2G20Certificate",
                                                                                                                                                 "SignCombinedCertificate")),
                   ActionPermission.Needs  ("TriggerMessage",        Run (NodeResources.Node)),

                   ActionPermission.Needs  ("InstallCertificate",    Edit(NodeResources.Certificates)),
                   ActionPermission.Needs  ("DeleteCertificate",     Edit(NodeResources.Certificates)),
                   ActionPermission.Needs  ("CertificateSigned",     Edit(NodeResources.Certificates)),

                   // The line up to the CSMS: where it goes and what it proves itself with.
                   ActionPermission.Needs  ("SetNetworkProfile",     Edit(LocalControllerAccess.CSMS)),

                   ActionPermission.Needs  ("GetLog",                Read(NodeResources.Log)),

                   ActionPermission.Needs  ("SyncNetworkTime",       Run (NodeResources.NTS)),
                   ActionPermission.Needs  ("TestTimeServer",        Run (NodeResources.NTS)),

                   ActionPermission.Needs  ("UpdateFirmware",        Edit(NodeResources.Firmware)),
                   ActionPermission.Needs  ("PublishFirmware",       Edit(NodeResources.Firmware)),

                   // The SSH keys of the charging stations, kept with their logins
                   // (white paper "OCPP over SSH", section 9.3, decision 7): what the
                   // page and the command line change with "stations:edit".
                   ActionPermission.Needs  ("InstallSSHKey",         Edit(LocalControllerAccess.Stations)),
                   ActionPermission.Needs  ("DeleteSSHKey",          Edit(LocalControllerAccess.Stations)),
                   ActionPermission.Needs  ("GetInstalledSSHKeys",   Read(LocalControllerAccess.Stations)),

                   // The controller's files.
                   ActionPermission.Needs  ("GetFile",               Read(NodeResources.Configuration)),
                   ActionPermission.Needs  ("ListDirectory",         Read(NodeResources.Configuration)),
                   ActionPermission.Needs  ("SendFile",              Edit(NodeResources.Configuration)),
                   ActionPermission.Needs  ("DeleteFile",            Edit(NodeResources.Configuration)),

                   // SetVariables and GetVariables have no row: the controller's own
                   // device model decides each of their entries by the resource of
                   // its component (LocalController.SignaturePolicy.cs).

                   // The roles and the signature policy: phase 4 renames these messages.
                   ActionPermission.Needs  ("AddUserRole",           Edit(NodeResources.Signatures)),
                   ActionPermission.Needs  ("UpdateUserRole",        Edit(NodeResources.Signatures)),
                   ActionPermission.Needs  ("DeleteUserRole",        Edit(NodeResources.Signatures)),
                   ActionPermission.Needs  ("AddSignaturePolicy",    Edit(NodeResources.Signatures)),
                   ActionPermission.Needs  ("UpdateSignaturePolicy", Edit(NodeResources.Signatures)),
                   ActionPermission.Needs  ("DeleteSignaturePolicy", Edit(NodeResources.Signatures))

               ];

        private static String Read(String Resource)
            => Permission.Read(Resource).ToString();

        private static String Edit(String Resource)
            => Permission.Edit(Resource).ToString();

        private static String Run (String Resource)
            => Permission.Run (Resource).ToString();

    }

}
