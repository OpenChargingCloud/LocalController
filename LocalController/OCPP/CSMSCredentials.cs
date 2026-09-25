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
    /// What this local controller says to prove who it is when it dials the
    /// charging station management system above it.
    /// </summary>
    /// <remarks>
    /// <b>These cannot be hashed, and that is the whole difference from the
    /// logins below.</b> A charging station's password is checked here, so a
    /// hash is enough. This one is <em>said</em> to somebody else, so the
    /// controller has to be able to read it back. The file therefore holds it
    /// as it is - and is written for its owner alone, the same treatment the
    /// private keys get, for the same reason.
    ///
    /// <b>One set, not a list.</b> A local controller reports to one CSMS.
    /// Whether it should be able to fail over to a second is a question about
    /// OCPP network profiles, which is a larger thing than a second row here.
    /// </remarks>
    public sealed class CSMSCredentials
    {

        #region Data

        /// <summary>
        /// The file beside the process, when nobody says otherwise.
        /// </summary>
        public const String  DefaultFileName    = "csms-credentials.json";

        /// <summary>
        /// The longest identification this controller may sign in under.
        /// </summary>
        public const Int32   MaxUsernameLength  = 48;

        /// <summary>
        /// The shortest password that may be set. The same floor OCPP asks of a
        /// charging station, applied to ourselves.
        /// </summary>
        public const Int32   MinPasswordLength  = 16;

        /// <summary>
        /// The longest one may be.
        /// </summary>
        public const Int32   MaxPasswordLength  = 64;

        private readonly Object  updateLock  = new ();

        private String?        username;
        private String?        password;
        private TOTPSettings?  totp;

        #endregion

        #region Properties

        /// <summary>
        /// The file the credentials live in.
        /// </summary>
        public String   Path    { get; }

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
        /// What this controller calls itself upwards, or null when nothing has
        /// been set.
        /// </summary>
        public String? Username
        {
            get { lock (updateLock) return username; }
        }

        /// <summary>
        /// Whether a password has been set.
        /// </summary>
        public Boolean HasPassword
        {
            get { lock (updateLock) return password is not null; }
        }

        /// <summary>
        /// Whether a one-time token can be derived.
        /// </summary>
        public Boolean HasTOTP
        {
            get { lock (updateLock) return totp is not null; }
        }

        #endregion

        #region Events

        /// <summary>
        /// Something happened here that belongs in the log of the controller.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The credentials in the given file; it need not exist yet.
        /// </summary>
        public CSMSCredentials(String?        Path          = null,
                               TimeProvider?  TimeProvider  = null)
        {

            this.Path          = System.IO.Path.GetFullPath(Path ?? DefaultFileName);
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;

        }

        #endregion


        #region TryLoad(out Error)

        /// <summary>
        /// Read the file. No file is not a failure - it is a controller nobody
        /// has given credentials to yet.
        /// </summary>
        public Boolean TryLoad([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            lock (updateLock)
            {

                username  = null;
                password  = null;
                totp      = null;

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

                username = document.Value<String>("username")?.Trim() is { Length: > 0 } name ? name : null;
                password = document.Value<String>("password") is { Length: > 0 } secret ? secret : null;

                if (document["totp"] is JObject totpJSON)
                {

                    if (!TOTPSettings.TryParse(totpJSON, out totp, out var problem))
                    {
                        Error = $"'{Path}': the TOTP configuration: {problem}";
                        return false;
                    }

                }

            }

            return true;

        }

        #endregion

        #region TrySetPassword(Username, Password, out Error)

        /// <summary>
        /// What this controller signs in with under security profiles 1 and 2.
        /// </summary>
        /// <remarks>
        /// Unlike the passwords of the charging stations below, this one is not
        /// made up here: it is issued by whoever runs the CSMS, and typed in.
        /// </remarks>
        public Boolean TrySetPassword(String?                           Username,
                                      String?                           Password,
                                      [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            var name = Username?.Trim() ?? "";

            if (name.Length == 0)
            {
                Error = "This local controller needs an identification to sign in to the CSMS with.";
                return false;
            }

            if (name.Length > MaxUsernameLength)
            {
                Error = $"The identification may be at most {MaxUsernameLength} characters long.";
                return false;
            }

            var secret = Password ?? "";

            if (secret.Length < MinPasswordLength)
            {
                Error = $"The CSMS password must be at least {MinPasswordLength} characters long; OCPP asks for that much.";
                return false;
            }

            if (secret.Length > MaxPasswordLength)
            {
                Error = $"The CSMS password may be at most {MaxPasswordLength} characters long.";
                return false;
            }

            lock (updateLock)
            {

                var (wasName, wasSecret) = (username, password);

                username  = name;
                password  = secret;

                if (!TrySave(out Error))
                {
                    (username, password) = (wasName, wasSecret);
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, $"The credentials this local controller signs in to the CSMS with were set for '{name}'.");

            }

            return true;

        }

        #endregion

        #region TrySetTOTP(Username, SharedSecret, ValidityTime, Length, Alphabet, HashAlgorithm, out Error)

        /// <summary>
        /// What this controller signs in with when the CSMS expects a one-time
        /// token instead of, or beside, a password.
        /// </summary>
        public Boolean TrySetTOTP(String?                           Username,
                                  String?                           SharedSecret,
                                  TimeSpan?                         ValidityTime,
                                  UInt32?                           Length,
                                  String?                           Alphabet,
                                  TOTPHashAlgorithm?                HashAlgorithm,
                                  [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            var name = Username?.Trim() ?? "";

            if (name.Length == 0)
            {
                Error = "This local controller needs an identification to sign in to the CSMS with.";
                return false;
            }

            if (name.Length > MaxUsernameLength)
            {
                Error = $"The identification may be at most {MaxUsernameLength} characters long.";
                return false;
            }

            if (SharedSecret is null || SharedSecret.Trim().Length == 0)
            {
                // Not made up here: the other end has to know it, and only the
                // other end can say what it is.
                Error = "The CSMS shared secret has to be the one the CSMS expects; it cannot be made up here.";
                return false;
            }

            if (!TOTPSettings.TryCreate(SharedSecret,
                                        ValidityTime,
                                        Length,
                                        Alphabet,
                                        HashAlgorithm,
                                        out var settings,
                                        out _,
                                        out Error))
            {
                return false;
            }

            lock (updateLock)
            {

                var (wasName, wasTOTP) = (username, totp);

                username  = name;
                totp      = settings;

                if (!TrySave(out Error))
                {
                    (username, totp) = (wasName, wasTOTP);
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, $"This local controller will sign in to the CSMS as '{name}' with a one-time token: {settings}.");

            }

            return true;

        }

        #endregion

        #region TryClear(out Error)

        /// <summary>
        /// Forget everything this controller signs in with.
        /// </summary>
        public Boolean TryClear([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            lock (updateLock)
            {

                var was = (username, password, totp);

                username  = null;
                password  = null;
                totp      = null;

                if (!TrySave(out Error))
                {
                    (username, password, totp) = was;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, "The credentials this local controller signs in to the CSMS with were removed.");

            }

            return true;

        }

        #endregion


        #region HTTPAuthentication() / TOTPConfig()

        /// <summary>
        /// The Authorization header this controller signs in with, or null when
        /// it has no password.
        /// </summary>
        /// <remarks>
        /// Basic and not TOTP even when both are set: a one-time token is not
        /// carried in a header made once, it is derived per request from the
        /// configuration handed to the client - see <see cref="TOTPConfig"/>.
        /// </remarks>
        public IHTTPAuthentication? HTTPAuthentication()
        {
            lock (updateLock)
                return username is not null && password is not null
                           ? HTTPBasicAuthentication.Create(username, password)
                           : null;
        }

        /// <summary>
        /// What the client derives its one-time tokens from, or null when this
        /// controller has none.
        /// </summary>
        public TOTPConfig? TOTPConfig()
        {
            lock (updateLock)
                return totp?.ToTOTPConfig();
        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The credentials, as the web interface reads them.
        /// </summary>
        /// <remarks>
        /// The identification but neither secret. What somebody needs from a
        /// page is whether this controller has what it takes to sign in and
        /// under what name - not the password itself, which is the one thing
        /// that would let a reader of that page become this controller.
        /// </remarks>
        public JObject ToJSON()
        {

            lock (updateLock)
            {

                var json = new JObject(
                               new JProperty("file",         Path),
                               new JProperty("username",     username),
                               new JProperty("hasPassword",  password is not null),
                               new JProperty("hasTOTP",      totp is not null),
                               new JProperty("minPasswordLength", MinPasswordLength)
                           );

                if (totp is not null)
                    json.Add("totp", totp.ToJSON());

                return json;

            }

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

                var json = new JObject();

                if (username is not null)  json.Add("username", username);
                if (password is not null)  json.Add("password", password);
                if (totp     is not null)  json.Add("totp",     totp.ToJSON(WithSecret: true));

                var temporary = Path + ".tmp";

                OwnerOnlyFile.Write(temporary, json.ToString(Formatting.Indented) + Environment.NewLine);

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
                return username is null
                           ? "no CSMS credentials"
                           : $"'{username}'{(password is not null ? " with a password" : "")}{(totp is not null ? " with a one-time token" : "")}";
        }

        #endregion

    }

}
