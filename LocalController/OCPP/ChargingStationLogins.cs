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
using System.Security.Cryptography;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// Which charging stations may sign in to this local controller, with
    /// what, and under which group's rules.
    /// </summary>
    /// <remarks>
    /// <b>Its own file, with its own permissions.</b> These are credentials,
    /// and the configuration file beside this one is an ordinary file that
    /// anybody who can read the directory may read. So they live here, and the
    /// file is written for its owner alone. Passwords are written the way the
    /// web login is written - PBKDF2 over a random salt - and a password that
    /// has been set is not readable again, not by this controller and not by
    /// whoever set it. A TOTP shared secret is the one thing here that cannot
    /// be hashed, because verifying a token means computing it.
    ///
    /// <b>A list, not a door.</b> A charging station that is not in this file
    /// cannot sign in with a password or a token, whatever it calls itself.
    /// Adding one is a deliberate act, which is the only way the list means
    /// anything.
    ///
    /// <b>Groups decide, logins belong.</b> What a station may do is said by
    /// the group it is in, so that it is said once for a site rather than
    /// forty times. See <see cref="LoginGroup"/>.
    /// </remarks>
    public sealed class ChargingStationLogins
    {

        #region Data

        /// <summary>
        /// The file beside the process, when nobody says otherwise.
        /// </summary>
        public const String  DefaultFileName    = "ocpp-stations.json";

        /// <summary>
        /// The longest a charging station identification may be; what OCPP 2.1
        /// allows for one.
        /// </summary>
        public const Int32   MaxIdLength        = 48;

        /// <summary>
        /// The longest a note beside a station may be.
        /// </summary>
        public const Int32   MaxNoteLength      = 200;

        /// <summary>
        /// The shortest password that may be set by hand. OCPP asks for at
        /// least 16 characters of entropy in the password of a charging
        /// station, and a controller that accepted "1234" would be the weakest
        /// part of a system built around that rule.
        /// </summary>
        public const Int32   MinPasswordLength  = 16;

        /// <summary>
        /// The longest one may be; what OCPP allows for the field.
        /// </summary>
        public const Int32   MaxPasswordLength  = 64;

        /// <summary>
        /// How many charging stations may be listed.
        /// </summary>
        public const Int32   MaxStations        = 1000;

        /// <summary>
        /// Something to hash when there is nothing to compare against; see
        /// <see cref="Verify"/>.
        /// </summary>
        private const String TimingEqualiser       = "no such charging station";

        /// <summary>
        /// Something to derive a token from when there is nothing to compare
        /// against; see <see cref="VerifyTOTP"/>.
        /// </summary>
        private const String TOTPTimingEqualiser   = "no-such-charging-station-at-all";

        private readonly Object                                    updateLock  = new ();
        private readonly Dictionary<String, ChargingStationLogin>  logins      = [];
        private readonly Dictionary<String, LoginGroup>            groups      = [];

        #endregion

        #region Properties

        /// <summary>
        /// The file the logins live in.
        /// </summary>
        public String   Path     { get; }

        /// <summary>
        /// Whether the file exists.
        /// </summary>
        public Boolean  Exists
            => File.Exists(Path);

        /// <summary>
        /// The clock this controller reads.
        /// </summary>
        public TimeProvider  TimeProvider  { get; }

        /// <summary>
        /// Every charging station in the file, by identification.
        /// </summary>
        public IReadOnlyList<ChargingStationLogin> Logins
        {
            get
            {
                lock (updateLock)
                    return [.. logins.Values.OrderBy(login => login.Id, StringComparer.OrdinalIgnoreCase)];
            }
        }

        /// <summary>
        /// Every group, the built-in one first and the rest by name.
        /// </summary>
        public IReadOnlyList<LoginGroup> Groups
        {
            get
            {
                lock (updateLock)
                    return [.. groups.Values.OrderByDescending(group => group.IsBuiltIn).
                                             ThenBy          (group => group.Id, StringComparer.OrdinalIgnoreCase)];
            }
        }

        /// <summary>
        /// How many of them may sign in at the moment.
        /// </summary>
        /// <remarks>
        /// A station in a group that is switched off may not, however switched
        /// on the station itself is - so the group has its say here too, and
        /// the number on the page is the number that could actually come in.
        /// </remarks>
        public Int32 EnabledCount
        {
            get
            {
                lock (updateLock)
                    return logins.Values.Count(login => login.Enabled &&
                                                        groups.GetValueOrDefault(login.GroupId)?.Enabled == true);
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Something happened here that belongs in the log of the controller.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        /// <summary>
        /// The list changed, and whatever is serving charging stations has to
        /// be told - a station whose password was taken away must stop being
        /// able to use it now and not at the next start.
        /// </summary>
        public event Action? OnChanged;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The logins in the given file; it need not exist yet.
        /// </summary>
        public ChargingStationLogins(String?        Path          = null,
                                     TimeProvider?  TimeProvider  = null)
        {

            this.Path          = System.IO.Path.GetFullPath(Path ?? DefaultFileName);
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;

            // Before anything is read, so that a controller with no file at all
            // still has the group every login falls back to.
            this.groups[LoginGroup.DefaultId] = LoginGroup.Default(this.TimeProvider.GetUtcNow());

        }

        #endregion


        #region TryLoad(out Error)

        /// <summary>
        /// Read the file. No file is not a failure - it is a local controller
        /// that has not been told about any charging station yet.
        /// </summary>
        /// <remarks>
        /// A file that is there but unreadable <em>is</em> a failure, and a loud
        /// one: carrying on with an empty list would turn every charging
        /// station on the site away while looking like nothing was wrong.
        ///
        /// A login pointing at a group that is not in the file is a failure of
        /// the same kind, and deliberately not repaired by moving it to the
        /// default group: that group is the permissive one, so the repair would
        /// quietly hand a station more than whoever wrote the file meant it to
        /// have.
        /// </remarks>
        public Boolean TryLoad([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            lock (updateLock)
            {

                logins.Clear();
                groups.Clear();
                groups[LoginGroup.DefaultId] = LoginGroup.Default(TimeProvider.GetUtcNow());

                if (!File.Exists(Path))
                    return true;

                JObject document;

                try
                {

                    var text = File.ReadAllText(Path);

                    if (String.IsNullOrWhiteSpace(text))
                        return true;

                    document = JObject.Parse(text);

                }
                catch (Exception e)
                {
                    Error = $"'{Path}' could not be read: {e.Message}";
                    return false;
                }

                #region The groups, when the file knows about any

                if (document["groups"] is JToken groupsToken && groupsToken.Type != JTokenType.Null)
                {

                    if (groupsToken is not JArray groupArray)
                    {
                        Error = $"'{Path}': 'groups' must be a JSON array.";
                        return false;
                    }

                    foreach (var token in groupArray)
                    {

                        if (token is not JObject json)
                        {
                            Error = $"'{Path}': every entry of 'groups' must be a JSON object.";
                            return false;
                        }

                        if (!LoginGroup.TryParse(json, out var group, out var problem))
                        {
                            Error = $"'{Path}': {problem}";
                            return false;
                        }

                        groups[group.Id] = group;

                    }

                }

                #endregion

                #region The logins

                if (document["stations"] is not JArray stations)
                {
                    Error = $"'{Path}' must hold a 'stations' array.";
                    return false;
                }

                foreach (var token in stations)
                {

                    if (token is not JObject json)
                    {
                        Error = $"'{Path}': every entry of 'stations' must be a JSON object.";
                        return false;
                    }

                    if (!ChargingStationLogin.TryParse(json, out var login, out var problem))
                    {
                        Error = $"'{Path}': {problem}";
                        return false;
                    }

                    if (!groups.ContainsKey(login.GroupId))
                    {
                        Error = $"'{Path}': the charging station '{login.Id}' belongs to the group '{login.GroupId}', which is not in this file.";
                        return false;
                    }

                    logins[login.Id] = login;

                }

                #endregion

            }

            return true;

        }

        #endregion


        #region TryAddOrUpdateGroup(Id, Name, Enabled, AuthMethods, SecurityProfiles, Note, out Error)

        /// <summary>
        /// Make a group, or change what one allows.
        /// </summary>
        public Boolean TryAddOrUpdateGroup(String?                           Id,
                                           String?                           Name,
                                           Boolean                           Enabled,
                                           IEnumerable<AuthMethod>?          AuthMethods,
                                           IEnumerable<Byte>?                SecurityProfiles,
                                           String?                           Note,
                                           [NotNullWhen(false)] out String?  Error)
        {

            lock (updateLock)
            {

                // Normalised the same way TryCreate will normalise it, so that
                // "Field-Test" and "field-test" are the same group rather than
                // a second one that shadows the first.
                var existing = groups.GetValueOrDefault(Id?.Trim().ToLowerInvariant() ?? "");

                if (!LoginGroup.TryCreate(Id,
                                          Name,
                                          Enabled,
                                          AuthMethods,
                                          SecurityProfiles,
                                          Note,
                                          existing?.AddedAt ?? TimeProvider.GetUtcNow(),
                                          out var group,
                                          out Error))
                {
                    return false;
                }

                if (existing is null && groups.Count >= LoginGroup.MaxGroups)
                {
                    Error = $"At most {LoginGroup.MaxGroups} login groups may be listed here.";
                    return false;
                }

                groups[group.Id] = group;

                if (!TrySave(out Error))
                {

                    if (existing is not null)
                        groups[group.Id] = existing;
                    else
                        groups.Remove(group.Id);

                    return false;

                }

                OnNotice?.Invoke(
                    LogLevel.Notice,
                    existing is null
                        ? $"The login group '{group.Id}' was added: {group}."
                        : $"The login group '{group.Id}' was changed: {group}."
                );

            }

            OnChanged?.Invoke();

            return true;

        }

        #endregion

        #region TryRemoveGroup(Id, out Error)

        /// <summary>
        /// Forget a group nobody is in.
        /// </summary>
        /// <remarks>
        /// A group with members is not removed and its members are not moved
        /// to the default group behind somebody's back: that group allows more
        /// than most, so the tidying would be a quiet widening. Whoever wants
        /// the group gone moves its members first, and sees where they land.
        /// </remarks>
        public Boolean TryRemoveGroup(String                            Id,
                                      [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            var id = Id?.Trim().ToLowerInvariant() ?? "";

            lock (updateLock)
            {

                if (!groups.TryGetValue(id, out var group))
                {
                    Error = $"This local controller has never heard of a login group called '{Id}'.";
                    return false;
                }

                if (group.IsBuiltIn)
                {
                    Error = "The default login group cannot be removed; it is what a login without a group of its own belongs to.";
                    return false;
                }

                var members = logins.Values.Count(login => login.GroupId == id);

                if (members > 0)
                {
                    Error = $"The login group '{id}' still holds {members} charging station(s). Move them to another group first.";
                    return false;
                }

                groups.Remove(id);

                if (!TrySave(out Error))
                {
                    groups[id] = group;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, $"The login group '{id}' was removed.");

            }

            OnChanged?.Invoke();

            return true;

        }

        #endregion

        #region TrySetGroup(Id, GroupId, out Error)

        /// <summary>
        /// Move a charging station into another group.
        /// </summary>
        public Boolean TrySetGroup(String                            Id,
                                   String?                           GroupId,
                                   [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            var groupId = GroupId?.Trim().ToLowerInvariant() ?? "";

            if (groupId.Length == 0)
                groupId = LoginGroup.DefaultId;

            lock (updateLock)
            {

                if (!logins.TryGetValue(Id, out var login))
                {
                    Error = $"This local controller has never heard of a charging station called '{Id}'.";
                    return false;
                }

                if (!groups.ContainsKey(groupId))
                {
                    Error = $"This local controller has never heard of a login group called '{GroupId}'.";
                    return false;
                }

                if (login.GroupId == groupId)
                    return true;

                logins[Id] = login with { GroupId = groupId };

                if (!TrySave(out Error))
                {
                    logins[Id] = login;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, $"The charging station '{Id}' is now in the login group '{groupId}'.");

            }

            OnChanged?.Invoke();

            return true;

        }

        #endregion


        #region TrySetPassword(Id, Password, GroupId, Note, out Generated, out Error)

        /// <summary>
        /// Add a charging station, or give one that is already here a new
        /// password.
        /// </summary>
        /// <remarks>
        /// A password of null means "make one up", and the made-up one comes
        /// back here once - to be shown to whoever is standing in front of the
        /// charging station about to type it in. It is kept nowhere but in its
        /// hash, so a second look is a second password.
        /// </remarks>
        /// <param name="Id">What the charging station calls itself in OCPP.</param>
        /// <param name="Password">The password, or null to have one made up.</param>
        /// <param name="GroupId">The group it should belong to, or null for the one it is in already.</param>
        /// <param name="Note">What this station is, for whoever reads the list later.</param>
        /// <param name="Generated">The password, when it was made up here.</param>
        public Boolean TrySetPassword(String                            Id,
                                      String?                           Password,
                                      String?                           GroupId,
                                      String?                           Note,
                                      out String?                       Generated,
                                      [NotNullWhen(false)] out String?  Error)
        {

            Generated  = null;
            Error      = null;

            if (!TryCheckIdAndNote(Id, Note, out var id, out var note, out Error))
                return false;

            var password = Password?.Trim();

            if (password is null || password.Length == 0)
            {
                // 32 characters of Base64Url: what OCPP asks for, and typable
                // off a screen by somebody standing at a charging station.
                password   = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).
                                     Replace('+', '-').
                                     Replace('/', '_').
                                     TrimEnd('=');
                Generated  = password;
            }

            else if (password.Length < MinPasswordLength)
            {
                Error = $"A charging station password must be at least {MinPasswordLength} characters long; OCPP asks for that much. " +
                         "Leave it empty to have one made up.";
                return false;
            }

            else if (password.Length > MaxPasswordLength)
            {
                Error = $"A charging station password may be at most {MaxPasswordLength} characters long.";
                return false;
            }

            if (!TryChange(id,
                           note,
                           GroupId,
                           login => login with { Password = SecurePassword.Create(password) },
                           existed => existed
                                          ? $"The password of the charging station '{id}' was changed."
                                          : $"The charging station '{id}' may now sign in to this local controller.",
                           out Error))
            {
                Generated = null;
                return false;
            }

            return true;

        }

        #endregion

        #region TrySetTOTP(Id, SharedSecret, ValidityTime, Length, Alphabet, HashAlgorithm, GroupId, Note, out Generated, out Error)

        /// <summary>
        /// Add a charging station, or give one that is already here what it
        /// needs to be let in with a one-time token.
        /// </summary>
        /// <remarks>
        /// A shared secret of null means "make one up", and unlike a password
        /// the made-up one is not the only time it can be read: the secret has
        /// to stay readable for the controller to compute tokens from, so it
        /// can be looked at again by whoever may manage certificates. It still
        /// comes back here once, because that is the moment somebody is
        /// standing in front of the charging station.
        /// </remarks>
        /// <param name="Generated">The shared secret, when it was made up here.</param>
        public Boolean TrySetTOTP(String                            Id,
                                  String?                           SharedSecret,
                                  TimeSpan?                         ValidityTime,
                                  UInt32?                           Length,
                                  String?                           Alphabet,
                                  TOTPHashAlgorithm?                HashAlgorithm,
                                  String?                           GroupId,
                                  String?                           Note,
                                  out String?                       Generated,
                                  [NotNullWhen(false)] out String?  Error)
        {

            Generated  = null;
            Error      = null;

            if (!TryCheckIdAndNote(Id, Note, out var id, out var note, out Error))
                return false;

            if (!TOTPSettings.TryCreate(SharedSecret,
                                        ValidityTime,
                                        Length,
                                        Alphabet,
                                        HashAlgorithm,
                                        out var settings,
                                        out var generated,
                                        out Error))
            {
                return false;
            }

            if (!TryChange(id,
                           note,
                           GroupId,
                           login => login with { TOTP = settings },
                           existed => existed
                                          ? $"The TOTP configuration of the charging station '{id}' was changed: {settings}."
                                          : $"The charging station '{id}' may now sign in to this local controller with a one-time token.",
                           out Error))
            {
                return false;
            }

            Generated = generated;

            return true;

        }

        #endregion

        #region TryClearPassword(Id, out Error) / TryClearTOTP(Id, out Error)

        /// <summary>
        /// Take the password away from a charging station, leaving everything
        /// else about it alone.
        /// </summary>
        public Boolean TryClearPassword(String                            Id,
                                        [NotNullWhen(false)] out String?  Error)

            => TryClear(Id,
                        login => login.HasPassword,
                        login => login with { Password = null },
                        $"The charging station '{Id}' can no longer sign in with a password.",
                        $"The charging station '{Id}' has no password to take away.",
                        out Error);

        /// <summary>
        /// Take the one-time token away from a charging station, leaving
        /// everything else about it alone.
        /// </summary>
        public Boolean TryClearTOTP(String                            Id,
                                    [NotNullWhen(false)] out String?  Error)

            => TryClear(Id,
                        login => login.HasTOTP,
                        login => login with { TOTP = null },
                        $"The charging station '{Id}' can no longer sign in with a one-time token.",
                        $"The charging station '{Id}' has no TOTP configuration to take away.",
                        out Error);

        #endregion

        #region TrySetEnabled(Id, Enabled, out Error)

        /// <summary>
        /// Let a charging station in, or stop letting it in, without throwing
        /// its credentials away.
        /// </summary>
        public Boolean TrySetEnabled(String                            Id,
                                     Boolean                           Enabled,
                                     [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            lock (updateLock)
            {

                if (!logins.TryGetValue(Id, out var login))
                {
                    Error = $"This local controller has never heard of a charging station called '{Id}'.";
                    return false;
                }

                if (login.Enabled == Enabled)
                    return true;

                logins[Id] = login with { Enabled = Enabled };

                if (!TrySave(out Error))
                {
                    logins[Id] = login;
                    return false;
                }

                OnNotice?.Invoke(
                    LogLevel.Notice,
                    Enabled
                        ? $"The charging station '{Id}' may sign in again."
                        : $"The charging station '{Id}' may no longer sign in."
                );

            }

            OnChanged?.Invoke();

            return true;

        }

        #endregion

        #region TryRemove(Id, out Error)

        /// <summary>
        /// Forget a charging station entirely.
        /// </summary>
        public Boolean TryRemove(String                            Id,
                                 [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            lock (updateLock)
            {

                if (!logins.TryGetValue(Id, out var login))
                {
                    Error = $"This local controller has never heard of a charging station called '{Id}'.";
                    return false;
                }

                logins.Remove(Id);

                if (!TrySave(out Error))
                {
                    logins[Id] = login;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, $"The charging station '{Id}' was removed; it can no longer sign in.");

            }

            OnChanged?.Invoke();

            return true;

        }

        #endregion


        #region Verify(Id, Password)

        /// <summary>
        /// Whether this pair may sign in.
        /// </summary>
        /// <remarks>
        /// A station that is switched off is refused as surely as one that was
        /// never here; the two are not told apart on the way out, because
        /// whoever is asking has no business learning which identifications
        /// exist. What the group allows is decided by the caller, which knows
        /// what the transport looked like.
        /// </remarks>
        public Boolean Verify(String Id, String Password)
        {

            ChargingStationLogin? login;

            lock (updateLock)
                login = logins.GetValueOrDefault(Id);

            // Still hashed when there is nothing to compare against, so that an
            // unknown identification does not answer faster than a known one.
            // A fixed word rather than the one that was sent: the work is the
            // same either way, and hashing what arrived would mean an empty
            // password throws here instead of being refused.
            if (login is null || !login.Enabled || login.Password is not SecurePassword password)
            {
                SecurePassword.Create(TimingEqualiser).Verify(TimingEqualiser);
                return false;
            }

            return password.Verify(Password);

        }

        #endregion

        #region VerifyTOTP(Id, Token)

        /// <summary>
        /// Whether this is the one-time token this charging station should be
        /// showing right now.
        /// </summary>
        /// <remarks>
        /// The same silence as <see cref="Verify"/>, and the same equalising:
        /// a token is derived for an identification that is not here either,
        /// so that the answer takes the same shape of time whether or not the
        /// station exists.
        /// </remarks>
        public Boolean VerifyTOTP(String Id, String Token)
        {

            ChargingStationLogin? login;

            lock (updateLock)
                login = logins.GetValueOrDefault(Id);

            if (login is null || !login.Enabled || login.TOTP is not TOTPSettings settings)
            {

                TOTPGenerator.GenerateTOTPs(
                    TOTPTimingEqualiser,
                    TOTPSettings.DefaultValidityTime,
                    TOTPSettings.DefaultLength,
                    TOTPSettings.DefaultAlphabet,
                    TimeProvider.GetUtcNow(),
                    null,
                    TOTPHashAlgorithm.SHA256
                );

                return false;

            }

            return settings.Matches(Token, TimeProvider.GetUtcNow());

        }

        #endregion

        #region TryGet(Id, out Login) / GroupOf(Login)

        /// <summary>
        /// One charging station, when it is listed here.
        /// </summary>
        public Boolean TryGet(String                                        Id,
                              [NotNullWhen(true)] out ChargingStationLogin?  Login)
        {
            lock (updateLock)
            {
                Login = logins.GetValueOrDefault(Id);
                return Login is not null;
            }
        }

        /// <summary>
        /// The group a login belongs to.
        /// </summary>
        /// <remarks>
        /// Never null for a login that came from this store: a login whose
        /// group is missing is refused while the file is read. A login from
        /// anywhere else falls back to nothing, and the caller must treat that
        /// as "not allowed" rather than as "no restrictions".
        /// </remarks>
        public LoginGroup? GroupOf(ChargingStationLogin Login)
        {
            lock (updateLock)
                return groups.GetValueOrDefault(Login.GroupId);
        }

        /// <summary>
        /// One group by its identification.
        /// </summary>
        public LoginGroup? GetGroup(String Id)
        {
            lock (updateLock)
                return groups.GetValueOrDefault(Id?.Trim().ToLowerInvariant() ?? "");
        }

        #endregion

        #region SecurePasswords()

        /// <summary>
        /// The stations that may sign in with a password, in the shape the
        /// WebSocket server keeps them in.
        /// </summary>
        /// <remarks>
        /// Only those whose group still allows a password at all - the server
        /// keeps this list to answer with, and a station whose group has had
        /// Basic Authentication taken away should not be in it.
        /// </remarks>
        public IReadOnlyDictionary<String, SecurePassword> SecurePasswords()
        {
            lock (updateLock)
                return logins.Values.Where(login => login.Enabled &&
                                                    login.Password.HasValue &&
                                                    groups.GetValueOrDefault(login.GroupId)?.Allows(AuthMethod.Basic) == true).
                                     ToDictionary(login => login.Id, login => login.Password!.Value);
        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The charging stations and their groups, as the web interface reads
        /// them.
        /// </summary>
        /// <remarks>
        /// Without the hashes and without the shared secrets. A page showing
        /// who may sign in is a page that whoever may read the configuration
        /// may open; a PBKDF2 hash on a screen is a hash somebody can take away
        /// and attack offline, and a shared secret on a screen is a charging
        /// station somebody can impersonate outright.
        /// </remarks>
        public JObject ToJSON()
        {

            lock (updateLock)

                return new JObject(

                    new JProperty("file",               Path),
                    new JProperty("enabled",            logins.Values.Count(login => login.Enabled &&
                                                                                     groups.GetValueOrDefault(login.GroupId)?.Enabled == true)),
                    new JProperty("maxStations",        MaxStations),
                    new JProperty("maxGroups",          LoginGroup.MaxGroups),
                    new JProperty("minPasswordLength",  MinPasswordLength),
                    new JProperty("minSecretLength",    TOTPSettings.MinSecretLength),
                    new JProperty("defaultGroup",       LoginGroup.DefaultId),

                    new JProperty("groups",             new JArray(
                        groups.Values.OrderByDescending(group => group.IsBuiltIn).
                                      ThenBy          (group => group.Id, StringComparer.OrdinalIgnoreCase).
                                      Select          (group => group.ToJSON(logins.Values.Count(login => login.GroupId == group.Id)))
                    )),

                    new JProperty("stations",           new JArray(
                        logins.Values.OrderBy(login => login.Id, StringComparer.OrdinalIgnoreCase).
                                      Select (login => login.ToJSON())
                    ))

                );

        }

        #endregion


        #region (private) TryCheckIdAndNote(...) / TryChange(...) / TryClear(...)

        /// <summary>
        /// What every way of adding a charging station has to check first.
        /// </summary>
        private static Boolean TryCheckIdAndNote(String?                           Id,
                                                 String?                           Note,
                                                 out String                        CheckedId,
                                                 out String                        CheckedNote,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            CheckedId    = Id?.Trim()   ?? "";
            CheckedNote  = Note?.Trim() ?? "";
            Error        = null;

            if (CheckedId.Length == 0)
            {
                Error = "A charging station needs the identification it signs in with.";
                return false;
            }

            if (CheckedId.Length > MaxIdLength)
            {
                Error = $"A charging station identification may be at most {MaxIdLength} characters long.";
                return false;
            }

            if (CheckedNote.Length > MaxNoteLength)
            {
                Error = $"The note may be at most {MaxNoteLength} characters long.";
                return false;
            }

            return true;

        }

        /// <summary>
        /// Add a charging station or change one, write the file, and put
        /// everything back where it was when the file could not be written.
        /// </summary>
        private Boolean TryChange(String                                              Id,
                                  String                                              Note,
                                  String?                                             GroupId,
                                  Func<ChargingStationLogin, ChargingStationLogin>    Change,
                                  Func<Boolean, String>                               Notice,
                                  [NotNullWhen(false)] out String?                    Error)
        {

            Error = null;

            lock (updateLock)
            {

                if (!logins.ContainsKey(Id) && logins.Count >= MaxStations)
                {
                    Error = $"At most {MaxStations} charging stations may be listed here.";
                    return false;
                }

                var existing  = logins.GetValueOrDefault(Id);

                var groupId   = GroupId?.Trim().ToLowerInvariant() is { Length: > 0 } wanted
                                    ? wanted
                                    : existing?.GroupId ?? LoginGroup.DefaultId;

                if (!groups.ContainsKey(groupId))
                {
                    Error = $"This local controller has never heard of a login group called '{groupId}'.";
                    return false;
                }

                logins[Id] = Change(
                                 existing
                                     ?? new ChargingStationLogin(
                                            Id,
                                            groupId,
                                            null,
                                            null,
                                            true,
                                            TimeProvider.GetUtcNow(),
                                            null
                                        )
                             ) with {
                                 GroupId  = groupId,
                                 Note     = Note.Length > 0 ? Note : existing?.Note
                             };

                if (!TrySave(out Error))
                {

                    if (existing is not null)
                        logins[Id] = existing;
                    else
                        logins.Remove(Id);

                    return false;

                }

                OnNotice?.Invoke(LogLevel.Notice, Notice(existing is not null));

            }

            OnChanged?.Invoke();

            return true;

        }

        /// <summary>
        /// Take one credential away from a charging station.
        /// </summary>
        private Boolean TryClear(String                                            Id,
                                 Func<ChargingStationLogin, Boolean>               Has,
                                 Func<ChargingStationLogin, ChargingStationLogin>  Change,
                                 String                                            Notice,
                                 String                                            NothingThere,
                                 [NotNullWhen(false)] out String?                  Error)
        {

            Error = null;

            lock (updateLock)
            {

                if (!logins.TryGetValue(Id, out var login))
                {
                    Error = $"This local controller has never heard of a charging station called '{Id}'.";
                    return false;
                }

                if (!Has(login))
                {
                    Error = NothingThere;
                    return false;
                }

                logins[Id] = Change(login);

                if (!TrySave(out Error))
                {
                    logins[Id] = login;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, Notice);

            }

            OnChanged?.Invoke();

            return true;

        }

        #endregion

        #region (private) TrySave(out Error)

        /// <summary>
        /// Write the file, readable by its owner alone.
        /// </summary>
        private Boolean TrySave([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            try
            {

                var directory = System.IO.Path.GetDirectoryName(Path);

                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var temporary = Path + ".tmp";

                OwnerOnlyFile.Write(
                    temporary,
                    new JObject(

                        new JProperty("groups",   new JArray(
                            groups.Values.OrderByDescending(group => group.IsBuiltIn).
                                          ThenBy          (group => group.Id, StringComparer.OrdinalIgnoreCase).
                                          Select          (group => group.ToJSON())
                        )),

                        new JProperty("stations", new JArray(
                            logins.Values.OrderBy(login => login.Id, StringComparer.OrdinalIgnoreCase).
                                          Select (login => login.ToJSON(WithSecrets: true))
                        ))

                    ).ToString(Formatting.Indented) + Environment.NewLine
                );

                File.Move(temporary, Path, overwrite: true);

                return true;

            }
            catch (Exception e)
            {
                Error = $"'{Path}' could not be written: {e.Message}";
                return false;
            }

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
        {
            lock (updateLock)
                return $"{logins.Values.Count(login => login.Enabled)} of {logins.Count} charging station(s) " +
                       $"in {groups.Count} group(s) may sign in";
        }

        #endregion

    }

}
