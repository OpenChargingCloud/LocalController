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

using System.Runtime.CompilerServices;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The line up to the CSMS of a controller under test: the first
    /// connection of its store, made and changed as a test says it - its
    /// address, what it signs in with, whom it believes - in whatever order
    /// the test says it.
    /// </summary>
    /// <remarks>
    /// A test says the password before it knows the port its CSMS listens
    /// on, and how the CSMS is believed before either. What is said before
    /// there is a connection is kept here and given to the connection when
    /// its address is.
    /// </remarks>
    internal static class TestUplinks
    {

        #region Data

        private sealed class Waiting
        {
            public String?                AuthenticationId;
            public ServerKeyPinSettings?  Trust;
            public String?                Pins;
        }

        private static readonly ConditionalWeakTable<LocalController, Waiting> waiting = new();

        #endregion


        #region Uplink(this Controller) / UplinkId(this Controller) / UplinkJSON(this Controller) / UplinkTrust(this Controller)

        /// <summary>
        /// The line up to the CSMS: the first connection of the store.
        /// </summary>
        public static ConnectionEntry? Uplink(this LocalController Controller)
            => Controller.Connections.Connections.FirstOrDefault();

        public static String UplinkId(this LocalController Controller)
            => Controller.Uplink()?.Id ?? throw new InvalidOperationException("This controller has no line up to a CSMS.");

        /// <summary>
        /// The line as it is written down.
        /// </summary>
        public static JObject UplinkJSON(this LocalController Controller)
            => Controller.Uplink()?.ToJSON() ?? [];

        /// <summary>
        /// How the server of the line is believed by its key pins, once it
        /// was dialled.
        /// </summary>
        public static ServerKeyPinTrust? UplinkTrust(this LocalController Controller)
            => Controller.Uplink() is ConnectionEntry line
                   ? Controller.KeyPinTrustOf(line.Id)
                   : null;

        #endregion

        #region TrySetUplink(this Controller, URL, out Error, HTTPVersion = null)

        /// <summary>
        /// Give the line its address: a connection made where there is none,
        /// with what was said of it before, and changed where there is one.
        /// </summary>
        public static Boolean TrySetUplink(this LocalController  Controller,
                                           String                URL,
                                           out String?           Error,
                                           String?               HTTPVersion   = null)
        {

            var kept = waiting.GetOrCreateValue(Controller);

            if (Controller.Uplink() is not ConnectionEntry line)
                return Controller.Connections.TryAddConnection(
                           "CSMS", URL, "CSMS", true,
                           kept.AuthenticationId,
                           null,
                           out _, out Error,
                           ServerKeyPins:  kept.Pins,
                           KeyPinTrust:    kept.Trust,
                           HTTPVersion:    HTTPVersion
                       );

            return Controller.Connections.TryUpdateConnection(
                       line.Id, line.Description, URL, line.ConnectionType.ToString(), line.AutoConnect,
                       line.AuthenticationId ?? kept.AuthenticationId,
                       line.CertificateId,
                       out Error,
                       HTTPVersion:  HTTPVersion
                   );

        }

        #endregion

        #region TryConfigureUplink(this Controller, JSON, out Error)

        /// <summary>
        /// What a test says of the CSMS in one object, as the section "csms"
        /// held it: the address and the version of HTTP go to the line, and
        /// the rest to the section.
        /// </summary>
        public static Boolean TryConfigureUplink(this LocalController  Controller,
                                                 JObject               JSON,
                                                 out String?           Error)
        {

            var rest         = (JObject) JSON.DeepClone();
            var url          = rest.Value<String>("url");
            var httpVersion  = rest.Value<String>("httpVersion");

            rest.Remove("url");
            rest.Remove("httpVersion");
            rest.Remove("securityProfile");

            // The section first: whether this controller reports to a CSMS
            // at all decides whether a change of the line is dialled.
            if (rest.Count > 0 && !Controller.TryUpdateCSMSConfiguration(rest, out Error))
                return false;

            Error = null;

            if (url is not null)
                return Controller.TrySetUplink(url, out Error, httpVersion);

            if (httpVersion is not null && Controller.Uplink() is ConnectionEntry line)
                return Controller.TrySetUplink(line.URL.ToString(), out Error, httpVersion);

            return true;

        }

        #endregion

        #region TrySetUplinkPassword(this Controller, Login, Password, out Error)

        /// <summary>
        /// The line signs in with a password.
        /// </summary>
        public static Boolean TrySetUplinkPassword(this LocalController  Controller,
                                                   String?               Login,
                                                   String?               Password,
                                                   out String?           Error)

            => Controller.Connections.TryAddAuthentication("The password of the line up", "basic", Login, Password, out var id, out Error) &&
               Controller.SignsInWith(id, out Error);

        #endregion

        #region TrySetUplinkTOTP(this Controller, Login, SharedSecret, Validity, Length, Alphabet, HashAlgorithm, out Error [, out NotSaved, Bound])

        /// <summary>
        /// The line signs in with a one-time token.
        /// </summary>
        public static Boolean TrySetUplinkTOTP(this LocalController  Controller,
                                               String?               Login,
                                               String?               SharedSecret,
                                               TimeSpan?             Validity,
                                               UInt32?               Length,
                                               String?               Alphabet,
                                               TOTPHashAlgorithm?    HashAlgorithm,
                                               out String?           Error)

            => Controller.TrySetUplinkTOTP(Login, SharedSecret, Validity, Length, Alphabet, HashAlgorithm, out Error, out _);

        public static Boolean TrySetUplinkTOTP(this LocalController  Controller,
                                               String?               Login,
                                               String?               SharedSecret,
                                               TimeSpan?             Validity,
                                               UInt32?               Length,
                                               String?               Alphabet,
                                               TOTPHashAlgorithm?    HashAlgorithm,
                                               out String?           Error,
                                               out Boolean           NotSaved,
                                               Boolean?              Bound   = null)

            => Controller.Connections.TryAddAuthentication("The one-time token of the line up", "totp", Login, SharedSecret, out var id, out Error, out NotSaved,
                                                           ValiditySeconds:    Validity?.TotalSeconds,
                                                           Length:             Length,
                                                           Alphabet:           Alphabet,
                                                           HashAlgorithm:      HashAlgorithm?.ToString(),
                                                           TLSChannelBinding:  Bound) &&
               Controller.SignsInWith(id, out Error);

        #endregion

        #region TrySetUplinkKeyPinTrust(this Controller, Settings, out Error) / TrySetUplinkKeyPins(this Controller, Pins, out Error)

        /// <summary>
        /// How the line believes its server beyond a CA.
        /// </summary>
        public static Boolean TrySetUplinkKeyPinTrust(this LocalController  Controller,
                                                      ServerKeyPinSettings  Settings,
                                                      out String?           Error)
        {

            if (Controller.Uplink() is ConnectionEntry line)
                return Controller.Connections.TryChangeKeyPinTrust(line.Id, Settings, out _, out _, out Error);

            waiting.GetOrCreateValue(Controller).Trust = Settings;
            Error = null;
            return true;

        }

        /// <summary>
        /// The key pins of the keys the server of the line may show.
        /// </summary>
        public static Boolean TrySetUplinkKeyPins(this LocalController  Controller,
                                                  String?               Pins,
                                                  out String?           Error)
        {

            if (Controller.Uplink() is ConnectionEntry line)
                return Controller.Connections.TryUpdateConnection(
                           line.Id, line.Description, line.URL.ToString(), line.ConnectionType.ToString(), line.AutoConnect,
                           line.AuthenticationId, line.CertificateId,
                           out Error,
                           ServerKeyPins:  Pins ?? ""
                       );

            waiting.GetOrCreateValue(Controller).Pins = Pins;
            Error = null;
            return true;

        }

        #endregion


        #region (private) SignsInWith(this Controller, AuthenticationId, out Error)

        private static Boolean SignsInWith(this LocalController  Controller,
                                           String                AuthenticationId,
                                           out String?           Error)
        {

            if (Controller.Uplink() is ConnectionEntry line)
                return Controller.Connections.TryUpdateConnection(
                           line.Id, line.Description, line.URL.ToString(), line.ConnectionType.ToString(), line.AutoConnect,
                           AuthenticationId,
                           line.CertificateId,
                           out Error
                       );

            waiting.GetOrCreateValue(Controller).AuthenticationId = AuthenticationId;
            Error = null;
            return true;

        }

        #endregion

    }

}
