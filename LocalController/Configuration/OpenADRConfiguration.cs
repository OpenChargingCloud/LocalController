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
    /// The OpenADR 3 VTN of the grid operator this local controller's charging
    /// pool is connected behind, and how this controller - a VEN of it - asks it
    /// for the limits it sends.
    /// </summary>
    /// <remarks>
    /// <b>Switched off unless somebody says otherwise</b>, as the CSMS line is:
    /// a controller that began asking a grid operator for limits merely because
    /// it was updated to a version that can would be making a connection nobody
    /// asked for.
    ///
    /// <b>Nothing secret lives here.</b> The client ID is what the grid
    /// operator knows this controller by, and is no secret; the client secret
    /// it signs in with is, and lives beside this file in one of its own,
    /// readable by its owner alone - see <see cref="OpenADR.VTNCredentials"/>.
    /// </remarks>
    /// <param name="Enabled">Whether this local controller asks a VTN for limits at all.</param>
    /// <param name="VTNURL">Where the VTN is: the base URL its programs, events and reports are below, e.g. "https://grid.example/openadr3/".</param>
    /// <param name="ClientID">What the grid operator knows this local controller by: its VEN name, the client ID of its client credentials.</param>
    /// <param name="PollEvery">How often the VTN is asked for the limits meant for this controller.</param>
    /// <param name="RequestTimeout">How long one request to the VTN may take.</param>
    public sealed record OpenADRConfiguration(Boolean?   Enabled          = null,
                                              String?    VTNURL           = null,
                                              String?    ClientID         = null,
                                              TimeSpan?  PollEvery        = null,
                                              TimeSpan?  RequestTimeout   = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName  = "openADR";

        /// <summary>
        /// How often the VTN is asked when nothing says otherwise.
        /// </summary>
        /// <remarks>
        /// Half a minute: a limit a grid operator sends for "now" is in force
        /// here within that, and a VTN asked twice a minute by every charging
        /// pool behind it is not a VTN that is asked too often.
        /// </remarks>
        public static readonly TimeSpan  DefaultPollEvery       = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How long a request may take when nothing says otherwise.
        /// </summary>
        public static readonly TimeSpan  DefaultRequestTimeout  = TimeSpan.FromSeconds(20);

        /// <summary>
        /// The longest a VTN URL may be.
        /// </summary>
        public const Int32  MaxURLLength       = 400;

        /// <summary>
        /// The longest a client ID may be.
        /// </summary>
        public const Int32  MaxClientIDLength  = 64;

        #endregion

        #region Properties

        /// <summary>
        /// Whether the VTN is spoken to unencrypted, so that the client secret
        /// and the bearer token cross the network as they are - unless the VTN
        /// is on this machine.
        /// </summary>
        public Boolean InTheClear
            => VTNURL is String url &&
               Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
               parsed.Scheme == Uri.UriSchemeHttp &&
               !parsed.IsLoopback;

        #endregion


        #region Effective()

        /// <summary>
        /// The same settings with every field filled in.
        /// </summary>
        public OpenADRConfiguration Effective()

            => new (Enabled         ?? false,
                    VTNURL,
                    ClientID,
                    PollEvery       ?? DefaultPollEvery,
                    RequestTimeout  ?? DefaultRequestTimeout);

        #endregion

        #region Merge(Changes)

        /// <summary>
        /// These settings with whatever the given ones mention written over
        /// them, and everything they do not mention left alone.
        /// </summary>
        public OpenADRConfiguration Merge(OpenADRConfiguration Changes)

            => new (Changes.Enabled         ?? Enabled,
                    Changes.VTNURL          ?? VTNURL,
                    Changes.ClientID        ?? ClientID,
                    Changes.PollEvery       ?? PollEvery,
                    Changes.RequestTimeout  ?? RequestTimeout);

        #endregion


        #region (static) NormalizedURL(URL)

        /// <summary>
        /// A VTN URL as it is kept: with a slash at the end, so that "events"
        /// is below it and does not replace its last segment.
        /// </summary>
        public static String NormalizedURL(String URL)

            => URL.EndsWith('/') ? URL : URL + "/";

        #endregion

        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// This section, as it stands in the configuration file - or a change to
        /// it, which may switch it on without saying again where the VTN is.
        /// </summary>
        /// <param name="JSON">The section, or the fields of it to change.</param>
        /// <param name="Configuration">What it says.</param>
        /// <param name="Error">What is wrong with it.</param>
        /// <param name="AChange">True for a change to the settings there are, whose whole is checked once it is merged.</param>
        public static Boolean TryParse(JObject                                       JSON,
                                       [NotNullWhen(true)]  out OpenADRConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?                Error,
                                       Boolean                                       AChange   = false)
        {

            Configuration = null;

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled",         SectionName,                        out var enabled,         out Error) ||
                !ConfigurationReader.TryReadString (JSON, "vtnURL",          SectionName, MaxURLLength,          out var url,             out Error) ||
                !ConfigurationReader.TryReadString (JSON, "clientID",        SectionName, MaxClientIDLength,     out var clientId,        out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "pollEvery",       SectionName, 5, 3600,               out var pollEvery,       out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "requestTimeout",  SectionName, 1, 120,                out var requestTimeout,  out Error))
            {
                return false;
            }

            #region The URL has to be one of HTTP

            if (url is not null)
            {

                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                    (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    Error = $"'{SectionName}.vtnURL' must be an HTTP URL, i.e. begin with 'https://' or 'http://': where the VTN of the grid operator is, " +
                             "e.g. 'https://grid.example/openadr3/'.";
                    return false;
                }

                if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
                {
                    Error = $"'{SectionName}.vtnURL' is where the programs, events and reports of the VTN are below, and has no query or fragment.";
                    return false;
                }

                url = NormalizedURL(url);

            }

            #endregion

            #region The client ID is one word

            if (clientId is not null && clientId.Any(Char.IsWhiteSpace))
            {
                Error = $"'{SectionName}.clientID' is the VEN name the grid operator gave this local controller, and has no spaces in it.";
                return false;
            }

            #endregion

            #region Switched on needs somewhere to ask, and somebody to ask as

            if (!AChange && enabled == true && url is null)
            {
                Error = $"'{SectionName}.enabled' is true, but there is no 'vtnURL' to ask.";
                return false;
            }

            if (!AChange && enabled == true && clientId is null)
            {
                Error = $"'{SectionName}.enabled' is true, but there is no 'clientID' to sign in as.";
                return false;
            }

            #endregion

            Configuration = new OpenADRConfiguration(enabled,
                                                     url,
                                                     clientId,
                                                     pollEvery,
                                                     requestTimeout);

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// This section, for the configuration file and for the web interface.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Enabled         is not null)  json.Add("enabled",         Enabled);
            if (VTNURL          is not null)  json.Add("vtnURL",          VTNURL);
            if (ClientID        is not null)  json.Add("clientID",        ClientID);
            if (PollEvery       is not null)  json.Add("pollEvery",       PollEvery.     Value.TotalSeconds);
            if (RequestTimeout  is not null)  json.Add("requestTimeout",  RequestTimeout.Value.TotalSeconds);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => Enabled != true
                   ? "no OpenADR VTN"
                   : $"OpenADR VTN {VTNURL ?? "nowhere"} as '{ClientID}'";

        #endregion

    }

}
