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
using System.Security.Authentication;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.LocalController.Configuration
{

    /// <summary>
    /// The charging station management system this local controller reports to,
    /// and how it dials it.
    /// </summary>
    /// <remarks>
    /// <b>The mirror image of <see cref="OCPPServerConfiguration"/>.</b> Down
    /// below this controller is a server that charging stations sign in to; up
    /// here it is a client that signs in to somebody else. The same three OCPP
    /// security profiles, the same three ways of proving who you are - only the
    /// direction differs, and with it which end has to keep the secret readable.
    ///
    /// <b>Switched off unless somebody says otherwise.</b> A controller that
    /// began dialling a backend merely because it was updated to a version that
    /// can would be making a connection nobody asked for, out of a network
    /// somebody else operates.
    ///
    /// <b>Nothing secret lives here.</b> The password and the TOTP shared
    /// secret this controller signs in with are secrets, and this file is an
    /// ordinary one that anybody who can read the directory may read - see
    /// <see cref="ControllerConfigFile"/>. They live beside it in a file of
    /// their own, with permissions of its own. Unlike the passwords of the
    /// charging stations below, these cannot be hashed: proving who you are to
    /// somebody else means being able to say the secret.
    /// </remarks>
    /// <param name="Enabled">Whether this local controller dials a CSMS at all.</param>
    /// <param name="URL">Where the CSMS is, as a WebSocket URL.</param>
    /// <param name="SecurityProfile">Which OCPP security profile is used to sign in.</param>
    /// <param name="NextHopNodeId">What the node on the other end calls itself; everything not meant for this controller is routed there.</param>
    /// <param name="Subprotocols">Which OCPP versions are offered, as WebSocket subprotocols.</param>
    /// <param name="MinimumTLSVersion">The oldest version of TLS this controller will speak upwards.</param>
    /// <param name="CheckCertificateRevocation">Whether the CSMS certificate is checked against its issuer's revocation list.</param>
    /// <param name="PingEvery">How often a silent connection is pinged, which is how a CSMS that went away is noticed.</param>
    /// <param name="RequestTimeout">How long a request upwards may take before it is given up on.</param>
    /// <param name="ReconnectInitialDelay">How long to wait before the first attempt to dial again.</param>
    /// <param name="ReconnectMaxDelay">The longest the waiting between attempts may grow to.</param>
    public sealed record CSMSConfiguration(Boolean?                Enabled                      = null,
                                           String?                 URL                          = null,
                                           Byte?                   SecurityProfile              = null,
                                           String?                 NextHopNodeId                = null,
                                           IReadOnlyList<String>?  Subprotocols                 = null,
                                           SslProtocols?           MinimumTLSVersion            = null,
                                           Boolean?                CheckCertificateRevocation   = null,
                                           TimeSpan?               PingEvery                    = null,
                                           TimeSpan?               RequestTimeout               = null,
                                           TimeSpan?               ReconnectInitialDelay        = null,
                                           TimeSpan?               ReconnectMaxDelay            = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName  = "csms";

        /// <summary>
        /// What the node above is called when nothing says otherwise - the
        /// identification OCPP reserves for a charging station management
        /// system.
        /// </summary>
        public const String  DefaultNextHopNodeId  = "CSMS";

        /// <summary>
        /// Which OCPP versions are offered upwards when nothing says otherwise.
        /// </summary>
        /// <remarks>
        /// The same two as downwards, and for the same reason: OCPP 1.6 is a
        /// decision about what this site speaks, not a default to arrive at by
        /// accident.
        /// </remarks>
        public static readonly String[]  DefaultSubprotocols   = [ "ocpp2.1", "ocpp2.0.1" ];

        /// <summary>
        /// The OCPP versions that may be offered at all.
        /// </summary>
        public static readonly String[]  KnownSubprotocols     = [ "ocpp1.6", "ocpp2.0.1", "ocpp2.1" ];

        /// <summary>
        /// Which security profile is used when nothing says otherwise.
        /// </summary>
        /// <remarks>
        /// Profile 2 - a password over TLS. Not 1: an unencrypted connection to
        /// a backend crosses networks this site does not own, and a password on
        /// such a wire is a password somebody else has. Not 3 either, because
        /// it needs a client certificate that has to be arranged first, and a
        /// default that cannot work without further setup is not a default.
        /// </remarks>
        public const Byte  DefaultSecurityProfile  = 2;

        /// <summary>
        /// The oldest TLS version spoken upwards unless something older is
        /// insisted upon.
        /// </summary>
        public const SslProtocols  DefaultMinimumTLSVersion  = SslProtocols.Tls12 | SslProtocols.Tls13;

        public static readonly TimeSpan  DefaultPingEvery              = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan  DefaultRequestTimeout         = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan  DefaultReconnectInitialDelay  = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan  DefaultReconnectMaxDelay      = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The longest a CSMS URL may be.
        /// </summary>
        public const Int32  MaxURLLength         = 400;

        /// <summary>
        /// The longest the identification of the node above may be.
        /// </summary>
        public const Int32  MaxNodeIdLength      = 48;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this profile needs the connection to be encrypted.
        /// </summary>
        public Boolean WantsTLS
            => (SecurityProfile ?? DefaultSecurityProfile) >= 2;

        /// <summary>
        /// Whether this profile signs in with a client certificate rather than
        /// with a password or a token.
        /// </summary>
        public Boolean WantsClientCertificate
            => (SecurityProfile ?? DefaultSecurityProfile) == 3;

        #endregion


        #region Effective()

        /// <summary>
        /// The same settings with every field filled in.
        /// </summary>
        public CSMSConfiguration Effective()

            => new (Enabled                     ?? false,
                    URL,
                    SecurityProfile             ?? DefaultSecurityProfile,
                    NextHopNodeId               ?? DefaultNextHopNodeId,
                    Subprotocols                ?? DefaultSubprotocols,
                    MinimumTLSVersion           ?? DefaultMinimumTLSVersion,
                    CheckCertificateRevocation  ?? true,
                    PingEvery                   ?? DefaultPingEvery,
                    RequestTimeout              ?? DefaultRequestTimeout,
                    ReconnectInitialDelay       ?? DefaultReconnectInitialDelay,
                    ReconnectMaxDelay           ?? DefaultReconnectMaxDelay);

        #endregion

        #region Merge(Changes)

        /// <summary>
        /// These settings with whatever the given ones mention written over
        /// them, and everything they do not mention left alone.
        /// </summary>
        public CSMSConfiguration Merge(CSMSConfiguration Changes)

            => new (Changes.Enabled                     ?? Enabled,
                    Changes.URL                         ?? URL,
                    Changes.SecurityProfile             ?? SecurityProfile,
                    Changes.NextHopNodeId               ?? NextHopNodeId,
                    Changes.Subprotocols                ?? Subprotocols,
                    Changes.MinimumTLSVersion           ?? MinimumTLSVersion,
                    Changes.CheckCertificateRevocation  ?? CheckCertificateRevocation,
                    Changes.PingEvery                   ?? PingEvery,
                    Changes.RequestTimeout              ?? RequestTimeout,
                    Changes.ReconnectInitialDelay       ?? ReconnectInitialDelay,
                    Changes.ReconnectMaxDelay           ?? ReconnectMaxDelay);

        #endregion

        #region NeedsARestartFor(AsBuilt)

        /// <summary>
        /// Which of these settings were decided when the connection was made
        /// and cannot be changed under it.
        /// </summary>
        /// <remarks>
        /// Everything about the socket, which is all of it: a WebSocket client
        /// is dialled once with an address, a profile and a set of credentials,
        /// and none of those can be exchanged while it is connected. So a change
        /// here means hanging up and dialling again - which is offered as an
        /// action rather than done silently, because it drops every charging
        /// station's messages that were in flight.
        /// </remarks>
        public IEnumerable<String> NeedsARestartFor(CSMSConfiguration AsBuilt)
        {

            if (URL                != AsBuilt.URL)                 yield return "url";
            if (SecurityProfile    != AsBuilt.SecurityProfile)     yield return "securityProfile";
            if (NextHopNodeId      != AsBuilt.NextHopNodeId)       yield return "nextHopNodeId";
            if (MinimumTLSVersion  != AsBuilt.MinimumTLSVersion)   yield return "minimumTLSVersion";
            if (PingEvery          != AsBuilt.PingEvery)           yield return "pingEvery";
            if (RequestTimeout     != AsBuilt.RequestTimeout)      yield return "requestTimeout";

            if (!(Subprotocols ?? []).SequenceEqual(AsBuilt.Subprotocols ?? []))
                yield return "subprotocols";

        }

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// This section, as it stands in the configuration file.
        /// </summary>
        public static Boolean TryParse(JObject                                    JSON,
                                       [NotNullWhen(true)]  out CSMSConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Configuration = null;

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled",                     SectionName,                   out var enabled,        out Error) ||
                !ConfigurationReader.TryReadString (JSON, "url",                         SectionName, MaxURLLength,     out var url,            out Error) ||
                !ConfigurationReader.TryReadByte   (JSON, "securityProfile",             SectionName,                   out var profile,        out Error) ||
                !ConfigurationReader.TryReadString (JSON, "nextHopNodeId",               SectionName, MaxNodeIdLength,  out var nextHop,        out Error) ||
                !ConfigurationReader.TryReadStrings(JSON, "subprotocols",                SectionName, 8, 32,            out var subprotocols,   out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "checkCertificateRevocation",  SectionName,                   out var revocation,     out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "pingEvery",                   SectionName, 5, 3600,                   out var pingEvery,      out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "requestTimeout",              SectionName, 1, 600,                   out var requestTimeout, out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "reconnectInitialDelay",       SectionName, 1, 600,                   out var initialDelay,   out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "reconnectMaxDelay",           SectionName, 1, 3600,                   out var maxDelay,       out Error))
            {
                return false;
            }

            #region The security profile has to be one of the three

            if (profile.HasValue && (profile.Value < 1 || profile.Value > 3))
            {
                Error = $"'{SectionName}.securityProfile' must be 1, 2 or 3.";
                return false;
            }

            #endregion

            #region The OCPP versions have to be ones this controller speaks

            if (subprotocols is not null)
            {

                foreach (var subprotocol in subprotocols)
                    if (!KnownSubprotocols.Contains(subprotocol))
                    {
                        Error = $"'{SectionName}.subprotocols' names '{subprotocol}', which this local controller does not speak. " +
                                $"It knows {String.Join(", ", KnownSubprotocols)}.";
                        return false;
                    }

                if (subprotocols.Count == 0)
                {
                    Error = $"'{SectionName}.subprotocols' must name at least one OCPP version, or a connection could never be agreed on.";
                    return false;
                }

            }

            #endregion

            #region The URL has to be one, and its scheme has to match the profile

            if (url is not null && url.Length > 0)
            {

                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                    (parsed.Scheme != "ws" && parsed.Scheme != "wss"))
                {
                    Error = $"'{SectionName}.url' must be a WebSocket URL, i.e. begin with 'ws://' or 'wss://'.";
                    return false;
                }

                var wantsTLS = (profile ?? DefaultSecurityProfile) >= 2;

                // Said here rather than discovered at the moment of dialling.
                // A profile 2 connection to a ws:// address is not a connection
                // with a small problem, it is a password sent in the clear.
                if (wantsTLS && parsed.Scheme == "ws")
                {
                    Error = $"'{SectionName}': security profile {profile ?? DefaultSecurityProfile} is encrypted, but the URL is 'ws://'. " +
                             "Use 'wss://', or security profile 1 if this connection really is meant to be unencrypted.";
                    return false;
                }

                if (!wantsTLS && parsed.Scheme == "wss")
                {
                    Error = $"'{SectionName}': security profile 1 is unencrypted, but the URL is 'wss://'. " +
                             "Use security profile 2 or 3 for an encrypted connection.";
                    return false;
                }

            }

            #endregion

            #region Switched on needs somewhere to dial

            if (enabled == true && (url is null || url.Length == 0))
            {
                Error = $"'{SectionName}.enabled' is true, but there is no 'url' to dial.";
                return false;
            }

            #endregion

            Configuration = new CSMSConfiguration(
                                enabled,
                                url is { Length: > 0 } ? url : null,
                                profile,
                                nextHop is { Length: > 0 } ? nextHop : null,
                                subprotocols,
                                null,
                                revocation,
                                pingEvery,
                                requestTimeout,
                                initialDelay,
                                maxDelay
                            );

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

            if (Enabled                    is not null)  json.Add("enabled",                     Enabled);
            if (URL                        is not null)  json.Add("url",                         URL);
            if (SecurityProfile            is not null)  json.Add("securityProfile",             SecurityProfile);
            if (NextHopNodeId              is not null)  json.Add("nextHopNodeId",               NextHopNodeId);
            if (Subprotocols               is not null)  json.Add("subprotocols",                new JArray(Subprotocols));
            if (CheckCertificateRevocation is not null)  json.Add("checkCertificateRevocation",  CheckCertificateRevocation);
            if (PingEvery                  is not null)  json.Add("pingEvery",                   PingEvery.Value.TotalSeconds);
            if (RequestTimeout             is not null)  json.Add("requestTimeout",              RequestTimeout.Value.TotalSeconds);
            if (ReconnectInitialDelay      is not null)  json.Add("reconnectInitialDelay",       ReconnectInitialDelay.Value.TotalSeconds);
            if (ReconnectMaxDelay          is not null)  json.Add("reconnectMaxDelay",           ReconnectMaxDelay.Value.TotalSeconds);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => Enabled != true
                   ? "no CSMS connection"
                   : $"{URL ?? "nowhere"} (security profile {SecurityProfile ?? DefaultSecurityProfile})";

        #endregion

    }

}
