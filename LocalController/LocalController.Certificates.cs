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

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// What a local controller says about its certificate store beyond what
    /// every node says - see <see cref="protocols.WWCP.Node.WWCPNode.CertificatesJSON"/>:
    /// the identity its CSMS connection signs in with.
    /// </summary>
    public partial class LocalController
    {

        #region (protected override) CompleteCertificatesJSON(JSON)

        /// <summary>
        /// Which handle the CSMS connection signs in with, so that the page can
        /// mark it without also reading the CSMS settings.
        /// </summary>
        protected override void CompleteCertificatesJSON(JObject JSON)
        {

            JSON["chosen"] = new JObject(
                                 new JProperty("csmsClientCertificate",  csmsSettings.ChosenClientCertificate)
                             );

        }

        #endregion

        #region (override) WhatUses(Handle)

        /// <summary>
        /// The CSMS connection, where it signs in with the certificate of this
        /// handle - as the sentence a refusal to delete it says.
        /// </summary>
        /// <remarks>
        /// Deleting it anyway would leave a controller configured to sign in
        /// with something that is not there, which is discovered at the next
        /// dialling rather than here - and switching it off is what somebody
        /// taking a certificate out of service usually meant.
        /// </remarks>
        public override String? WhatUses(String Handle)

            => UsedByCSMS(Handle) is String field
                   ? $"That certificate is what 'csms.{field}' names. Choose another one for the CSMS " +
                      "connection first, or switch this one off instead of deleting it."
                   : null;

        #endregion

    }

}
