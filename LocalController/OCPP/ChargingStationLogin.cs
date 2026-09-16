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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// One charging station that may sign in to this local controller, and
    /// what it may sign in with.
    /// </summary>
    /// <remarks>
    /// <b>A login may carry both credentials, or neither.</b> Both, because a
    /// station being moved from a password to a TOTP has to be able to use
    /// either while it is being moved. Neither, because a station that comes in
    /// on a client certificate proves who it is without one - and may still
    /// want to be listed here, so that it belongs to a group that decides
    /// something about it.
    ///
    /// <b>What is stored differs between the two, and has to.</b> The password
    /// is a PBKDF2 hash and cannot be read back. The TOTP shared secret is the
    /// secret itself, because verifying a token means computing it. See
    /// <see cref="TOTPSettings"/>.
    /// </remarks>
    /// <param name="Id">What the charging station calls itself in OCPP.</param>
    /// <param name="GroupId">The group that decides what it may do.</param>
    /// <param name="Password">Its password, hashed, or null when it has none.</param>
    /// <param name="TOTP">What it needs to be let in with a one-time token, or null when it has none.</param>
    /// <param name="Enabled">Whether it may sign in at the moment.</param>
    /// <param name="AddedAt">When it was added.</param>
    /// <param name="Note">What it is, for whoever reads the list later.</param>
    public sealed record ChargingStationLogin(String           Id,
                                              String           GroupId,
                                              SecurePassword?  Password,
                                              TOTPSettings?    TOTP,
                                              Boolean          Enabled,
                                              DateTimeOffset   AddedAt,
                                              String?          Note)
    {

        #region Properties

        /// <summary>
        /// Whether a password has been set for this login.
        /// </summary>
        public Boolean HasPassword
            => Password.HasValue;

        /// <summary>
        /// Whether this login can be let in with a one-time token.
        /// </summary>
        public Boolean HasTOTP
            => TOTP is not null;

        /// <summary>
        /// The ways this login could prove who it is, before the group it
        /// belongs to has had its say.
        /// </summary>
        /// <remarks>
        /// A client certificate is not in here. It is not something the login
        /// carries - it is something the station brings, checked against the
        /// accepted chains - so whether it counts is a question for the group
        /// alone.
        /// </remarks>
        public IEnumerable<AuthMethod> Credentials
        {
            get
            {

                if (HasPassword)
                    yield return AuthMethod.Basic;

                if (HasTOTP)
                    yield return AuthMethod.TOTP;

            }
        }

        #endregion


        #region (static) TryParse(JSON, out Login, out Error)

        /// <summary>
        /// One login, as it stands in the file.
        /// </summary>
        /// <remarks>
        /// A file written before this controller had heard of groups holds no
        /// 'group' on any login, and that has to keep working: those logins
        /// join the group that behaves the way they did.
        /// </remarks>
        public static Boolean TryParse(JObject                                        JSON,
                                       [NotNullWhen(true)]  out ChargingStationLogin?  Login,
                                       [NotNullWhen(false)] out String?                Error)
        {

            Login  = null;
            Error  = null;

            var id = JSON.Value<String>("id")?.Trim() ?? "";

            if (id.Length == 0)
            {
                Error = "every charging station needs an 'id'.";
                return false;
            }

            #region Its password, when it has one

            SecurePassword? password = null;

            var passwordText = JSON.Value<String>("password");

            if (passwordText is not null && passwordText.Length > 0)
            {

                if (SecurePassword.TryParse(passwordText) is not SecurePassword parsed)
                {
                    Error = $"the password of '{id}' is not a hash this local controller can read.";
                    return false;
                }

                password = parsed;

            }

            #endregion

            #region Its TOTP settings, when it has any

            TOTPSettings? totp = null;

            if (JSON["totp"] is JObject totpJSON)
            {

                if (!TOTPSettings.TryParse(totpJSON, out totp, out var problem))
                {
                    Error = $"the TOTP configuration of '{id}': {problem}";
                    return false;
                }

            }

            #endregion

            var addedAt = DateTimeOffset.MinValue;

            DateTimeOffset.TryParse(JSON.Value<String>("addedAt"),
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind,
                                    out addedAt);

            var group = JSON.Value<String>("group")?.Trim().ToLowerInvariant();

            Login = new ChargingStationLogin(
                        id,
                        group is { Length: > 0 } ? group : LoginGroup.DefaultId,
                        password,
                        totp,
                        JSON.Value<Boolean?>("enabled") ?? true,
                        addedAt,
                        JSON.Value<String>("note")?.Trim() is { Length: > 0 } note ? note : null
                    );

            return true;

        }

        #endregion

        #region ToJSON(WithSecrets = false)

        /// <summary>
        /// This charging station, with the hash and the shared secret only
        /// where the file is being written.
        /// </summary>
        public JObject ToJSON(Boolean WithSecrets = false)
        {

            var json = new JObject(
                           new JProperty("id",           Id),
                           new JProperty("group",        GroupId),
                           new JProperty("enabled",      Enabled),
                           new JProperty("addedAt",      AddedAt.ToString("o")),
                           new JProperty("hasPassword",  HasPassword),
                           new JProperty("hasTOTP",      HasTOTP)
                       );

            if (Note is not null)
                json.Add("note", Note);

            if (TOTP is not null)
                json.Add("totp", TOTP.ToJSON(WithSecret: WithSecrets));

            if (WithSecrets && Password.HasValue)
                json.Add("password", Password.Value.ToString());

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Id} in '{GroupId}' ({(Enabled ? "on" : "off")}): " +
               $"{(HasPassword || HasTOTP ? String.Join(" and ", Credentials.Select(method => method.AsText())) : "no credentials")}";

        #endregion

    }

}
