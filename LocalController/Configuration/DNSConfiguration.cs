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
    /// The "dns" section of the configuration file: how this local controller
    /// resolves names.
    /// </summary>
    /// <remarks>
    /// Every field is optional, and null means "the file does not say" rather
    /// than "the file says nothing": what is missing keeps whatever the local controller
    /// was given at construction, and a local controller given nothing keeps the system
    /// default. That is what makes a half-written file a legitimate thing to
    /// have - somebody who only cares about the name servers should not have to
    /// write down the retry count to say so.
    /// </remarks>
    /// <param name="Enabled">Whether this local controller resolves names at all.</param>
    /// <param name="Servers">The name servers to ask; an empty list is not the same as none given.</param>
    /// <param name="QueryTimeout">How long one query may take.</param>
    /// <param name="RecursionDesired">Whether the RD bit is set; null leaves it to the server.</param>
    /// <param name="UseCache">Whether answers are remembered for as long as their time to live says.</param>
    /// <param name="DnssecOK">Whether the DO bit is set, i.e. whether signatures are asked for.</param>
    /// <param name="FollowCNAMEs">Whether a CNAME chain is followed.</param>
    /// <param name="MaxCNAMEFollows">How far.</param>
    /// <param name="MaxRetries">How often a server that answered SERVFAIL is asked again.</param>
    public sealed record DNSConfiguration(Boolean?                        Enabled               = null,
                                          IReadOnlyList<DNSServerConfig>? Servers               = null,
                                          TimeSpan?                       QueryTimeout          = null,
                                          Boolean?                        RecursionDesired      = null,
                                          Boolean?                        UseCache              = null,
                                          Boolean?                        DnssecOK              = null,
                                          Boolean?                        FollowCNAMEs          = null,
                                          Byte?                           MaxCNAMEFollows       = null,
                                          Byte?                           MaxRetries            = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName          = "dns";

        /// <summary>
        /// The most name servers one local controller may be given. Not a rule of DNS -
        /// a query goes to all of them at once, and a hundred of them would be
        /// a hundred packets for every name this local controller ever looks up.
        /// </summary>
        public const Int32   MaxServers           = 16;

        /// <summary>
        /// The longest a query may be allowed to take, in seconds.
        /// </summary>
        public const Double  MaxQueryTimeoutSeconds = 120;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "dns" section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                     JSON,
                                       [NotNullWhen(true)]  out DNSConfiguration?   Configuration,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Configuration  = null;
            Error          = null;

            #region The name servers

            IReadOnlyList<DNSServerConfig>? servers = null;

            if (JSON["servers"] is JToken serversToken && serversToken.Type != JTokenType.Null)
            {

                if (serversToken is not JArray serverArray)
                {
                    Error = "'dns.servers' must be an array of name servers.";
                    return false;
                }

                if (serverArray.Count > MaxServers)
                {
                    Error = $"'dns.servers' may name at most {MaxServers} servers.";
                    return false;
                }

                var parsed = new List<DNSServerConfig>();

                foreach (var token in serverArray)
                {

                    if (!TryParseServer(token, out var server, out Error))
                        return false;

                    parsed.Add(server);

                }

                servers = parsed;

            }

            #endregion

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled",              "dns", out var enabled,          out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "recursionDesired",     "dns", out var recursionDesired, out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "useCache",             "dns", out var useCache,         out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "dnssecOK",             "dns", out var dnssecOK,         out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "followCNAMEs",         "dns", out var followCNAMEs,     out Error) ||
                !ConfigurationReader.TryReadByte   (JSON, "maxCNAMEFollows",      "dns", out var maxCNAMEFollows,  out Error) ||
                !ConfigurationReader.TryReadByte   (JSON, "maxRetries",           "dns", out var maxRetries,       out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "queryTimeoutSeconds",  "dns", 0.1, MaxQueryTimeoutSeconds, out var queryTimeout, out Error))
            {
                return false;
            }

            Configuration = new DNSConfiguration(
                                enabled,
                                servers,
                                queryTimeout,
                                recursionDesired,
                                useCache,
                                dnssecOK,
                                followCNAMEs,
                                maxCNAMEFollows,
                                maxRetries
                            );

            return true;

        }

        #endregion

        #region (static) TryParseServer(JSON, out Server, out Error)

        /// <summary>
        /// One name server: an address or a domain name, a port, a transport.
        /// </summary>
        /// <remarks>
        /// A plain string is accepted as well as an object, because "8.8.8.8"
        /// is what somebody writes when they mean a name server and nothing
        /// else about it.
        /// </remarks>
        public static Boolean TryParseServer(JToken                                   JSON,
                                             [NotNullWhen(true)]  out DNSServerConfig? Server,
                                             [NotNullWhen(false)] out String?           Error)
        {

            Server  = null;
            Error   = null;

            var json = JSON as JObject;

            var address = json is null
                              ? JSON.Value<String>()?.Trim()
                              : (json.Value<String>("address") ?? json.Value<String>("domainName"))?.Trim();

            if (String.IsNullOrEmpty(address))
            {
                Error = "Every name server needs an 'address' or a 'domainName'.";
                return false;
            }

            #region The transport, which decides the default port

            var transport = DNSTransport.UDP;

            var transportText = json?.Value<String>("transport")?.Trim();

            if (!String.IsNullOrEmpty(transportText))
            {

                if (!Enum.TryParse(transportText, ignoreCase: true, out transport))
                {
                    Error = $"'{transportText}' is not a DNS transport. Known transports: {String.Join(", ", Enum.GetNames<DNSTransport>())}.";
                    return false;
                }

            }

            #endregion

            #region The port, and the per-server timeout

            // Left at null when the file does not say, so that DNSServerConfig
            // picks the port of the transport - 53, 853, 443 - instead of this
            // file keeping its own second list of which port means what.
            var portNumber = json?.Value<UInt16?>("port");

            var port       = portNumber.HasValue
                                 ? IPPort.Parse(portNumber.Value)
                                 : (IPPort?) null;

            TimeSpan? queryTimeout = null;

            if (json is not null &&
                !ConfigurationReader.TryReadSeconds(json, "queryTimeoutSeconds", "dns.servers[]", 0.1, MaxQueryTimeoutSeconds, out queryTimeout, out Error))
            {
                return false;
            }

            #endregion

            // An address first, because a name server named by a name is a name
            // that something has to resolve, and the thing that would resolve it
            // is this client.
            if (org.GraphDefined.Vanaheimr.Hermod.IPAddress.TryParse(address, out var ipAddress))
                Server = new DNSServerConfig(ipAddress, port, transport, QueryTimeout: queryTimeout);

            else if (DomainName.TryParse(address, out var domainName, out var problem))
                Server = new DNSServerConfig(domainName, port, transport, QueryTimeout: queryTimeout);

            else
            {
                Error = $"'dns.servers': \"{address}\" is neither an IP address nor a domain name: {problem}";
                return false;
            }

            return true;

        }

        #endregion

        #region (static) ServerJSON(Server)

        /// <summary>
        /// One name server as the file keeps it and the web interface reads it.
        /// </summary>
        public static JObject ServerJSON(DNSServerConfig Server)

            => new (
                   new JProperty("address",               Server.IPAddress?.ToString() ?? Server.DomainName?.ToString()),
                   new JProperty("port",                  Server.Port.ToUInt16()),
                   new JProperty("transport",             Server.Transport.ToString()),
                   new JProperty("queryTimeoutSeconds",   Server.QueryTimeout?.TotalSeconds)
               );

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this local controller was not
        /// told about is not written, so that the file keeps saying "the system
        /// decides" rather than freezing today's system default.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Enabled.            HasValue)  json.Add("enabled",              Enabled.            Value);
            if (Servers is not null)           json.Add("servers",              new JArray(Servers.Select(ServerJSON)));
            if (QueryTimeout.       HasValue)  json.Add("queryTimeoutSeconds",  QueryTimeout.       Value.TotalSeconds);
            if (RecursionDesired.   HasValue)  json.Add("recursionDesired",     RecursionDesired.   Value);
            if (UseCache.           HasValue)  json.Add("useCache",             UseCache.           Value);
            if (DnssecOK.           HasValue)  json.Add("dnssecOK",             DnssecOK.           Value);
            if (FollowCNAMEs.       HasValue)  json.Add("followCNAMEs",         FollowCNAMEs.       Value);
            if (MaxCNAMEFollows.    HasValue)  json.Add("maxCNAMEFollows",      MaxCNAMEFollows.    Value);
            if (MaxRetries.         HasValue)  json.Add("maxRetries",           MaxRetries.         Value);

            return json;

        }

        #endregion

    }

}
