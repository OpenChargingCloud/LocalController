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
using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.LocalController.Configuration
{

    /// <summary>
    /// The "nts" section of the configuration file: where this local controller
    /// reads the time, and how it proves that the answer came from there.
    /// </summary>
    /// <remarks>
    /// As in the DNS section, null means "the file does not say": what is
    /// missing keeps whatever the local controller was given at construction.
    /// </remarks>
    /// <param name="Enabled">Whether this local controller asks a time server at all.</param>
    /// <param name="Hostname">The NTS server.</param>
    /// <param name="NTSKEPort">Where its key exchange listens; 4460 unless said otherwise.</param>
    /// <param name="NTPPort">Where its NTP service listens; 123 unless said otherwise.</param>
    /// <param name="Timeout">How long one exchange may take.</param>
    /// <param name="CheckEvery">How often this local controller checks its clock against that server.</param>
    /// <param name="LegalTimeAuthority">Who stands behind that server's time, e.g. "PTB" - the operator saying so, because this local controller cannot find out by itself.</param>
    /// <param name="LegalTimeTolerance">How far this local controller's own clock may be from it and still count.</param>
    /// <param name="LegalTimeMaxAge">How old the last check may be and still count.</param>
    public sealed record NTSConfiguration(Boolean?     Enabled               = null,
                                          DomainName?  Hostname              = null,
                                          IPPort?      NTSKEPort             = null,
                                          IPPort?      NTPPort               = null,
                                          TimeSpan?    Timeout               = null,
                                          TimeSpan?    CheckEvery            = null,
                                          String?      LegalTimeAuthority    = null,
                                          TimeSpan?    LegalTimeTolerance    = null,
                                          TimeSpan?    LegalTimeMaxAge       = null)
    {

        #region Data

        /// <summary>
        /// How often this local controller checks its clock against its time server,
        /// when nobody says otherwise.
        /// </summary>
        /// <remarks>
        /// Often enough that a clock drifting at the rate a cheap oscillator
        /// drifts is caught long before it matters, and rarely enough that a
        /// public time server does not notice this local controller at all.
        /// </remarks>
        public static readonly TimeSpan  DefaultCheckEvery        = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How far this local controller's clock may be from the time it was checked
        /// against and still be called legal time, when nobody says otherwise.
        /// </summary>
        public static readonly TimeSpan  DefaultLegalTolerance    = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How old the last check may be and still count, when nobody says
        /// otherwise.
        /// </summary>
        public static readonly TimeSpan  DefaultLegalMaxAge       = TimeSpan.FromHours(1);

        /// <summary>
        /// The longest the name of a time authority may be written.
        /// </summary>
        public const           Int32     MaxAuthorityLength       = 80;

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName          = "nts";

        /// <summary>
        /// The time server this local controller asks when nothing says otherwise.
        /// </summary>
        /// <remarks>
        /// The Physikalisch-Technische Bundesanstalt, which is one of the few
        /// public NTS servers that is also a legal time source somewhere.
        /// </remarks>
        public const String  DefaultHostname      = "ptbtime1.ptb.de";

        /// <summary>
        /// The longest an exchange may be allowed to take, in seconds. An hour
        /// is not a timeout any more, and zero is not one either.
        /// </summary>
        public const Double  MaxTimeoutSeconds    = 3600;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "nts" section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                    JSON,
                                       [NotNullWhen(true)]  out NTSConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled",         "nts",      out var enabled,   out Error) ||
                !ConfigurationReader.TryReadString (JSON, "hostname",        "nts", 253, out var hostname,  out Error) ||
                !ConfigurationReader.TryReadPort   (JSON, "ntsKEPort",       "nts",      out var ntsKEPort, out Error) ||
                !ConfigurationReader.TryReadPort   (JSON, "ntpPort",         "nts",      out var ntpPort,   out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "timeoutSeconds",  "nts", 0.1, MaxTimeoutSeconds, out var timeout, out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "checkEverySeconds", "nts", 10, 86400, out var checkEvery, out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "legalTimeToleranceSeconds", "nts", 0.001, 60, out var tolerance, out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "legalTimeMaxAgeSeconds", "nts", 10, 86400, out var maxAge, out Error) ||
                !ConfigurationReader.TryReadString (JSON, "legalTimeAuthority", "nts", MaxAuthorityLength, out var authority, out Error))
            {
                return false;
            }

            DomainName? domainName = null;

            if (hostname is not null && !DomainName.TryParse(hostname, out domainName, out var problem))
            {
                Error = $"'nts.hostname': {problem}";
                return false;
            }

            Configuration = new NTSConfiguration(
                                enabled,
                                domainName,
                                ntsKEPort,
                                ntpPort,
                                timeout,
                                checkEvery,
                                authority,
                                tolerance,
                                maxAge
                            );

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this local controller was not
        /// told about is not written.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Enabled.HasValue)      json.Add("enabled",         Enabled.  Value);
            if (Hostname is not null)  json.Add("hostname",        Hostname. ToString());
            if (NTSKEPort.HasValue)    json.Add("ntsKEPort",       NTSKEPort.Value.ToUInt16());
            if (NTPPort.  HasValue)    json.Add("ntpPort",         NTPPort.  Value.ToUInt16());
            if (Timeout.  HasValue)    json.Add("timeoutSeconds",  Timeout.  Value.TotalSeconds);

            if (CheckEvery.HasValue)          json.Add("checkEverySeconds",           CheckEvery.        Value.TotalSeconds);
            if (LegalTimeAuthority is not null) json.Add("legalTimeAuthority",        LegalTimeAuthority);
            if (LegalTimeTolerance.HasValue)  json.Add("legalTimeToleranceSeconds",   LegalTimeTolerance.Value.TotalSeconds);
            if (LegalTimeMaxAge.   HasValue)  json.Add("legalTimeMaxAgeSeconds",      LegalTimeMaxAge.   Value.TotalSeconds);

            return json;

        }

        #endregion

    }

}
