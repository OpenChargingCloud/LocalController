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

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Configuration
{

    /// <summary>
    /// The "ocpp" section of the configuration file: who this local controller
    /// says it is when it speaks OCPP.
    /// </summary>
    /// <remarks>
    /// As in the DNS and NTS sections, null means "the file does not say": what
    /// is missing keeps whatever the controller was given at construction.
    ///
    /// Read once, at the start, and not changeable while running - unlike the
    /// name servers and the time server. An identification is what a CSMS knows
    /// this controller by and what every charging station below it was told to
    /// address; changing it under a live connection would not rename the
    /// controller, it would make it a second one that nobody is talking to.
    /// </remarks>
    /// <param name="NodeId">The networking node identification, e.g. "lc001".</param>
    /// <param name="VendorName">Who made this local controller.</param>
    /// <param name="Model">What model it is.</param>
    /// <param name="SerialNumber">Its serial number, or null when it has none to give.</param>
    /// <param name="SoftwareVersion">The version it reports, or null to report the version of this assembly.</param>
    public sealed record OCPPConfiguration(String?  NodeId            = null,
                                           String?  VendorName        = null,
                                           String?  Model             = null,
                                           String?  SerialNumber      = null,
                                           String?  SoftwareVersion   = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName            = "ocpp";

        /// <summary>
        /// What this local controller calls itself when nothing says otherwise.
        /// </summary>
        public const String  DefaultNodeId          = "lc001";

        /// <summary>
        /// The vendor reported when nothing says otherwise.
        /// </summary>
        public const String  DefaultVendorName      = "GraphDefined GmbH";

        /// <summary>
        /// The model reported when nothing says otherwise.
        /// </summary>
        public const String  DefaultModel           = "Local Controller";

        /// <summary>
        /// The longest a networking node identification may be written. OCPP
        /// 2.1 allows 48 characters for an identifier of this kind.
        /// </summary>
        public const Int32   MaxNodeIdLength        = 48;

        /// <summary>
        /// The longest the vendor, the model, the serial number and the
        /// software version may be written; what OCPP 2.1 allows for each.
        /// </summary>
        public const Int32   MaxVendorNameLength    = 50;

        /// <summary>
        /// The longest a model may be written.
        /// </summary>
        public const Int32   MaxModelLength         = 20;

        /// <summary>
        /// The longest a serial number may be written.
        /// </summary>
        public const Int32   MaxSerialNumberLength  = 25;

        /// <summary>
        /// The longest a software version may be written.
        /// </summary>
        public const Int32   MaxVersionLength       = 50;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "ocpp" section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                     JSON,
                                       [NotNullWhen(true)]  out OCPPConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadString(JSON, "nodeId",           SectionName, MaxNodeIdLength,       out var nodeId,          out Error) ||
                !ConfigurationReader.TryReadString(JSON, "vendorName",       SectionName, MaxVendorNameLength,   out var vendorName,      out Error) ||
                !ConfigurationReader.TryReadString(JSON, "model",            SectionName, MaxModelLength,        out var model,           out Error) ||
                !ConfigurationReader.TryReadString(JSON, "serialNumber",     SectionName, MaxSerialNumberLength, out var serialNumber,    out Error) ||
                !ConfigurationReader.TryReadString(JSON, "softwareVersion",  SectionName, MaxVersionLength,      out var softwareVersion, out Error))
            {
                return false;
            }

            // Whether the text is a networking node identification is decided
            // where one is made - this section only promises that the file said
            // something, and OCPP decides whether it can be addressed by it.
            if (nodeId is not null && nodeId.Any(Char.IsWhiteSpace))
            {
                Error = $"'{SectionName}.nodeId' must not contain spaces.";
                return false;
            }

            Configuration = new OCPPConfiguration(
                                nodeId,
                                vendorName,
                                model,
                                serialNumber,
                                softwareVersion
                            );

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this local controller
        /// was not told about is not written, so that the file keeps saying
        /// "the default decides" rather than freezing today's default.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (NodeId          is not null)  json.Add("nodeId",           NodeId);
            if (VendorName      is not null)  json.Add("vendorName",       VendorName);
            if (Model           is not null)  json.Add("model",            Model);
            if (SerialNumber    is not null)  json.Add("serialNumber",     SerialNumber);
            if (SoftwareVersion is not null)  json.Add("softwareVersion",  SoftwareVersion);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => String.Concat(
                   NodeId ?? DefaultNodeId,
                   " (", VendorName ?? DefaultVendorName, " ", Model ?? DefaultModel, ")"
               );

        #endregion

    }

}
