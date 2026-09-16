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

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// A way a charging station can prove who it is.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same thing as an OCPP security profile. A profile
    /// says what the transport looks like - profile 1 is a password on a plain
    /// port, profile 2 the same password over TLS, profile 3 a client
    /// certificate. TOTP is not in OCPP at all; it is what this controller
    /// accepts beside them. So a group says both: which of these it accepts,
    /// and over which profiles.
    /// </remarks>
    public enum AuthMethod
    {

        /// <summary>
        /// HTTP Basic Authentication: the identification and a password.
        /// </summary>
        Basic,

        /// <summary>
        /// HTTP TOTP Authentication: the identification and a token derived
        /// from a shared secret and the time.
        /// </summary>
        TOTP,

        /// <summary>
        /// A TLS client certificate, held against the accepted chains.
        /// </summary>
        Certificate

    }


    /// <summary>
    /// Extension methods for authentication methods.
    /// </summary>
    public static class AuthMethodExtensions
    {

        /// <summary>
        /// What this method is called in the configuration file and over HTTP.
        /// </summary>
        public static String AsText(this AuthMethod Method)

            => Method switch {
                   AuthMethod.Basic        => "basic",
                   AuthMethod.TOTP         => "totp",
                   AuthMethod.Certificate  => "certificate",
                   _                       => Method.ToString().ToLowerInvariant()
               };

        /// <summary>
        /// Read one back, or null when it is not a method this controller knows.
        /// </summary>
        public static AuthMethod? TryParseAuthMethod(String? Text)

            => Text?.Trim().ToLowerInvariant() switch {
                   "basic"        => AuthMethod.Basic,
                   "totp"         => AuthMethod.TOTP,
                   "certificate"  => AuthMethod.Certificate,
                   _              => null
               };

    }


    /// <summary>
    /// A group of logins, and what its members are allowed to do.
    /// </summary>
    /// <remarks>
    /// <b>The group decides, the login belongs.</b> Which ways of signing in
    /// are acceptable, and over which OCPP security profiles, is said once for
    /// a whole group rather than on every login. That is the point of it: a
    /// site with forty charging stations in a field test can be stopped in one
    /// move, and a group that is only ever allowed in on profile 3 stays that
    /// way when somebody adds the forty-first.
    ///
    /// <b>It can only narrow.</b> Everything here is held against the settings
    /// of the server as a whole, and the smaller of the two wins. A group that
    /// allows profile 1 on a controller which does not allows nothing; the
    /// group cannot hand out what the server does not offer.
    ///
    /// <b>Empty means nobody.</b> A group with no authentication method left
    /// lets nobody in. That is the same choice the accepted certificate chains
    /// make, and for the same reason: a list that means "everything" when it is
    /// empty is a list that opens the door when somebody deletes the last row.
    /// </remarks>
    /// <param name="Id">What the group is called, in the file and in every login that points at it.</param>
    /// <param name="Name">What it is called on screen.</param>
    /// <param name="Enabled">Whether its members may sign in at the moment.</param>
    /// <param name="AuthMethods">The ways its members may prove who they are.</param>
    /// <param name="SecurityProfiles">The OCPP security profiles its members may come in on.</param>
    /// <param name="AddedAt">When the group was made.</param>
    /// <param name="Note">What it is for, for whoever reads the list later.</param>
    public sealed record LoginGroup(String                    Id,
                                    String                    Name,
                                    Boolean                   Enabled,
                                    IReadOnlySet<AuthMethod>  AuthMethods,
                                    IReadOnlySet<Byte>        SecurityProfiles,
                                    DateTimeOffset            AddedAt,
                                    String?                   Note)
    {

        #region Data

        /// <summary>
        /// The group every login belongs to when nobody said otherwise. It
        /// cannot be removed, because a login without a group would be a login
        /// nothing decides about.
        /// </summary>
        public const String  DefaultId      = "default";

        /// <summary>
        /// The longest a group identification may be.
        /// </summary>
        public const Int32   MaxIdLength    = 32;

        /// <summary>
        /// The longest a group name may be.
        /// </summary>
        public const Int32   MaxNameLength  = 60;

        /// <summary>
        /// The longest a note beside a group may be.
        /// </summary>
        public const Int32   MaxNoteLength  = 200;

        /// <summary>
        /// How many groups may be listed.
        /// </summary>
        public const Int32   MaxGroups      = 100;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this is the group that is always there.
        /// </summary>
        public Boolean IsBuiltIn
            => Id == DefaultId;

        #endregion


        #region (static) Default(Now)

        /// <summary>
        /// The group a controller that has never heard of groups behaves like:
        /// everything its server allows, allowed.
        /// </summary>
        /// <remarks>
        /// Deliberately permissive, and only here. A charging station that
        /// signed in yesterday must sign in today as well, and the file of a
        /// controller updated to a version that knows about groups says nothing
        /// about them. So the group those logins land in has to mean "as
        /// before" - and the narrowing happens where somebody decided it, not
        /// behind their back.
        /// </remarks>
        public static LoginGroup Default(DateTimeOffset Now)

            => new (DefaultId,
                    "Default",
                    true,
                    new HashSet<AuthMethod> { AuthMethod.Basic, AuthMethod.TOTP, AuthMethod.Certificate },
                    new HashSet<Byte>       { 1, 2, 3 },
                    Now,
                    null);

        #endregion

        #region (static) TryCreate(Id, Name, Enabled, AuthMethods, SecurityProfiles, Note, AddedAt, out Group, out Error)

        /// <summary>
        /// A group, with everything about it checked.
        /// </summary>
        public static Boolean TryCreate(String?                              Id,
                                        String?                              Name,
                                        Boolean                              Enabled,
                                        IEnumerable<AuthMethod>?             AuthMethods,
                                        IEnumerable<Byte>?                   SecurityProfiles,
                                        String?                              Note,
                                        DateTimeOffset                       AddedAt,
                                        [NotNullWhen(true)]  out LoginGroup?  Group,
                                        [NotNullWhen(false)] out String?      Error)
        {

            Group  = null;
            Error  = null;

            var id = Id?.Trim().ToLowerInvariant() ?? "";

            if (id.Length == 0)
            {
                Error = "A login group needs an identification.";
                return false;
            }

            if (id.Length > MaxIdLength)
            {
                Error = $"A login group identification may be at most {MaxIdLength} characters long.";
                return false;
            }

            // Kept to what can be put in a URL without escaping, because that
            // is where it ends up: the route that changes one group.
            if (!id.All(character => (character >= 'a' && character <= 'z') ||
                                     (character >= '0' && character <= '9') ||
                                      character == '-'))
            {
                Error = "A login group identification may hold lower-case letters, digits and hyphens only.";
                return false;
            }

            var name = Name?.Trim() ?? "";

            if (name.Length == 0)
                name = id;

            if (name.Length > MaxNameLength)
            {
                Error = $"A login group name may be at most {MaxNameLength} characters long.";
                return false;
            }

            var note = Note?.Trim() ?? "";

            if (note.Length > MaxNoteLength)
            {
                Error = $"The note may be at most {MaxNoteLength} characters long.";
                return false;
            }

            var profiles = new HashSet<Byte>(SecurityProfiles ?? []);

            foreach (var profile in profiles)
                if (profile < 1 || profile > 3)
                {
                    Error = $"'{profile}' is not an OCPP security profile; there are 1, 2 and 3.";
                    return false;
                }

            Group = new LoginGroup(
                        id,
                        name,
                        Enabled,
                        new HashSet<AuthMethod>(AuthMethods ?? []),
                        profiles,
                        AddedAt,
                        note.Length > 0 ? note : null
                    );

            return true;

        }

        #endregion

        #region (static) TryParse(JSON, out Group, out Error)

        /// <summary>
        /// One group, as it stands in the file.
        /// </summary>
        public static Boolean TryParse(JObject                              JSON,
                                       [NotNullWhen(true)]  out LoginGroup?  Group,
                                       [NotNullWhen(false)] out String?      Error)
        {

            Group  = null;
            Error  = null;

            var methods = new List<AuthMethod>();

            if (JSON["authMethods"] is JArray methodArray)
                foreach (var token in methodArray)
                {

                    var method = AuthMethodExtensions.TryParseAuthMethod(token.Value<String>());

                    if (method is null)
                    {
                        Error = $"'{token}' is not a way of signing in that this local controller knows.";
                        return false;
                    }

                    methods.Add(method.Value);

                }

            var profiles = new List<Byte>();

            if (JSON["securityProfiles"] is JArray profileArray)
                foreach (var token in profileArray)
                {

                    if (token.Type != JTokenType.Integer)
                    {
                        Error = $"'{token}' is not an OCPP security profile.";
                        return false;
                    }

                    profiles.Add(token.Value<Byte>());

                }

            var addedAt = DateTimeOffset.MinValue;

            DateTimeOffset.TryParse(JSON.Value<String>("addedAt"),
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind,
                                    out addedAt);

            return TryCreate(
                       JSON.Value<String>("id"),
                       JSON.Value<String>("name"),
                       JSON.Value<Boolean?>("enabled") ?? true,
                       methods,
                       profiles,
                       JSON.Value<String>("note"),
                       addedAt,
                       out Group,
                       out Error
                   );

        }

        #endregion

        #region Allows(Method) / Allows(SecurityProfile)

        /// <summary>
        /// Whether a member of this group may sign in this way.
        /// </summary>
        public Boolean Allows(AuthMethod Method)
            => Enabled && AuthMethods.Contains(Method);

        /// <summary>
        /// Whether a member of this group may come in on this OCPP security
        /// profile.
        /// </summary>
        public Boolean Allows(Byte SecurityProfile)
            => Enabled && SecurityProfiles.Contains(SecurityProfile);

        #endregion

        #region ToJSON(Members = null)

        /// <summary>
        /// This group, for the file and for the web interface alike - there is
        /// nothing secret in it.
        /// </summary>
        /// <param name="Members">How many logins point at it, when the caller counted.</param>
        public JObject ToJSON(Int32? Members = null)
        {

            var json = new JObject(
                           new JProperty("id",                Id),
                           new JProperty("name",              Name),
                           new JProperty("enabled",           Enabled),
                           new JProperty("builtIn",           IsBuiltIn),
                           new JProperty("addedAt",           AddedAt.ToString("o")),
                           new JProperty("authMethods",       new JArray(AuthMethods.Select(method => method.AsText()).Order())),
                           new JProperty("securityProfiles",  new JArray(SecurityProfiles.Order().Select(profile => (Int32) profile)))
                       );

            if (Note is not null)
                json.Add("note", Note);

            if (Members.HasValue)
                json.Add("members", Members.Value);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Id} ({(Enabled ? "on" : "off")}): " +
               $"{(AuthMethods.Count == 0 ? "nothing" : String.Join(", ", AuthMethods.Select(method => method.AsText()).Order()))}, " +
               $"profile(s) {(SecurityProfiles.Count == 0 ? "none" : String.Join(", ", SecurityProfiles.Order()))}";

        #endregion

    }

}
