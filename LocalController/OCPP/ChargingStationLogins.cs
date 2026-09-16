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

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.LocalController.Logging;
using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// Which charging stations may sign in to this local controller, and with
    /// what.
    /// </summary>
    /// <remarks>
    /// <b>Its own file, with its own permissions.</b> These are passwords, and
    /// the configuration file beside this one is an ordinary file that anybody
    /// who can read the directory may read. So they live here, written the way
    /// the web login is written - PBKDF2 over a random salt - and a password
    /// that has been set is not readable again, not by this controller and not
    /// by whoever set it.
    ///
    /// <b>A list, not a door.</b> A charging station that is not in this file
    /// cannot sign in under OCPP security profiles 1 and 2, whatever it calls
    /// itself. Adding one is a deliberate act, which is the only way the list
    /// means anything.
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
        private const String TimingEqualiser = "no such charging station";

        private readonly Object                                     updateLock  = new ();
        private readonly Dictionary<String, ChargingStationLogin>   logins      = [];

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
        /// How many of them may sign in at the moment.
        /// </summary>
        public Int32 EnabledCount
        {
            get
            {
                lock (updateLock)
                    return logins.Values.Count(login => login.Enabled);
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
        /// </remarks>
        public Boolean TryLoad([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            lock (updateLock)
            {

                logins.Clear();

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

                    logins[login.Id] = login;

                }

            }

            return true;

        }

        #endregion

        #region TrySetPassword(Id, Password, Note, out Generated, out Error)

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
        /// <param name="Note">What this station is, for whoever reads the list later.</param>
        /// <param name="Generated">The password, when it was made up here.</param>
        public Boolean TrySetPassword(String                            Id,
                                      String?                           Password,
                                      String?                           Note,
                                      out String?                       Generated,
                                      [NotNullWhen(false)] out String?  Error)
        {

            Generated  = null;
            Error      = null;

            var id = Id?.Trim() ?? "";

            if (id.Length == 0)
            {
                Error = "A charging station needs the identification it signs in with.";
                return false;
            }

            if (id.Length > MaxIdLength)
            {
                Error = $"A charging station identification may be at most {MaxIdLength} characters long.";
                return false;
            }

            var note = Note?.Trim() ?? "";

            if (note.Length > MaxNoteLength)
            {
                Error = $"The note may be at most {MaxNoteLength} characters long.";
                return false;
            }

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

            lock (updateLock)
            {

                if (!logins.ContainsKey(id) && logins.Count >= MaxStations)
                {
                    Error = $"At most {MaxStations} charging stations may be listed here.";
                    return false;
                }

                var existing = logins.GetValueOrDefault(id);

                logins[id] = new ChargingStationLogin(
                                 id,
                                 SecurePassword.Create(password),
                                 existing?.Enabled ?? true,
                                 existing?.AddedAt ?? TimeProvider.GetUtcNow(),
                                 note.Length > 0 ? note : existing?.Note
                             );

                if (!TrySave(out Error))
                {

                    if (existing is not null)
                        logins[id] = existing;
                    else
                        logins.Remove(id);

                    Generated = null;
                    return false;

                }

                OnNotice?.Invoke(
                    LogLevel.Notice,
                    existing is null
                        ? $"The charging station '{id}' may now sign in to this local controller."
                        : $"The password of the charging station '{id}' was changed."
                );

            }

            OnChanged?.Invoke();

            return true;

        }

        #endregion

        #region TrySetEnabled(Id, Enabled, out Error)

        /// <summary>
        /// Let a charging station in, or stop letting it in, without throwing
        /// its password away.
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
        /// exist.
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
            if (login is null || !login.Enabled)
            {
                SecurePassword.Create(TimingEqualiser).Verify(TimingEqualiser);
                return false;
            }

            return login.Password.Verify(Password);

        }

        #endregion

        #region SecurePasswords()

        /// <summary>
        /// The stations that may sign in, in the shape the WebSocket server
        /// keeps them in.
        /// </summary>
        public IReadOnlyDictionary<String, SecurePassword> SecurePasswords()
        {
            lock (updateLock)
                return logins.Values.Where(login => login.Enabled).
                                     ToDictionary(login => login.Id, login => login.Password);
        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The charging stations, as the web interface reads them.
        /// </summary>
        /// <remarks>
        /// Without the hashes. A page showing who may sign in is a page that
        /// whoever may read the configuration may open, and a PBKDF2 hash on a
        /// screen is a PBKDF2 hash somebody can take away and attack offline.
        /// </remarks>
        public JObject ToJSON()
        {

            lock (updateLock)

                return new JObject(

                    new JProperty("file",            Path),
                    new JProperty("enabled",         logins.Values.Count(login => login.Enabled)),
                    new JProperty("maxStations",     MaxStations),
                    new JProperty("minPasswordLength", MinPasswordLength),

                    new JProperty("stations",        new JArray(
                        logins.Values.OrderBy(login => login.Id, StringComparer.OrdinalIgnoreCase).
                                      Select(login => login.ToJSON())
                    ))

                );

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
                        new JProperty("stations", new JArray(
                            logins.Values.OrderBy(login => login.Id, StringComparer.OrdinalIgnoreCase).
                                          Select(login => login.ToJSON(WithPassword: true))
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
                return $"{logins.Values.Count(login => login.Enabled)} of {logins.Count} charging station(s) may sign in";
        }

        #endregion

    }


    /// <summary>
    /// One charging station that may sign in to this local controller.
    /// </summary>
    /// <param name="Id">What it calls itself in OCPP.</param>
    /// <param name="Password">Its password, hashed.</param>
    /// <param name="Enabled">Whether it may sign in at the moment.</param>
    /// <param name="AddedAt">When it was added.</param>
    /// <param name="Note">What it is, for whoever reads the list later.</param>
    public sealed record ChargingStationLogin(String          Id,
                                              SecurePassword  Password,
                                              Boolean         Enabled,
                                              DateTimeOffset  AddedAt,
                                              String?         Note)
    {

        #region (static) TryParse(JSON, out Login, out Error)

        public static Boolean TryParse(JObject                                       JSON,
                                       [NotNullWhen(true)]  out ChargingStationLogin? Login,
                                       [NotNullWhen(false)] out String?               Error)
        {

            Login  = null;
            Error  = null;

            var id = JSON.Value<String>("id")?.Trim() ?? "";

            if (id.Length == 0)
            {
                Error = "every charging station needs an 'id'.";
                return false;
            }

            if (SecurePassword.TryParse(JSON.Value<String>("password") ?? "") is not SecurePassword password)
            {
                Error = $"the password of '{id}' is not a hash this local controller can read.";
                return false;
            }

            var addedAt = DateTimeOffset.MinValue;

            DateTimeOffset.TryParse(JSON.Value<String>("addedAt"),
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind,
                                    out addedAt);

            Login = new ChargingStationLogin(
                        id,
                        password,
                        JSON.Value<Boolean?>("enabled") ?? true,
                        addedAt,
                        JSON.Value<String>("note")?.Trim() is { Length: > 0 } note ? note : null
                    );

            return true;

        }

        #endregion

        #region ToJSON(WithPassword = false)

        /// <summary>
        /// This charging station, with its hashed password only where the file
        /// is being written.
        /// </summary>
        public JObject ToJSON(Boolean WithPassword = false)
        {

            var json = new JObject(
                           new JProperty("id",       Id),
                           new JProperty("enabled",  Enabled),
                           new JProperty("addedAt",  AddedAt.ToString("o"))
                       );

            if (Note is not null)
                json.Add("note", Note);

            if (WithPassword)
                json.Add("password", Password.ToString());

            return json;

        }

        #endregion

    }

}
