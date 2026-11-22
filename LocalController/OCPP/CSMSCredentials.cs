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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.LocalController.Web;
using cloud.charging.open.protocols.OCPP.Node.Serving;
using cloud.charging.open.protocols.WWCP.Node.Logins;

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// What this local controller says to prove who it is when it connects to the
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
        public const Int32   MaxLoginLength  = 48;

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

        private User_Id?       login;
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
        public User_Id? Login
        {
            get { lock (updateLock) return login; }
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
        /// <summary>
        /// The password of before, for what takes the line of before over
        /// into the store of connections - see LocalController.Connecting.cs.
        /// </summary>
        internal String? PasswordOfBefore
        {
            get { lock (updateLock) return password; }
        }

        /// <summary>
        /// The one-time token of before, for the same.
        /// </summary>
        internal TOTPSettings? TOTPOfBefore
        {
            get { lock (updateLock) return totp; }
        }

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

                login     = null;
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

                login = User_Id.TryParse(document.Value<String>("login"));
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

        #region TrySetPassword(Login, Password, out Error [, out NotSaved])

        /// <summary>
        /// What this controller signs in with under security profiles 1 and 2.
        /// </summary>
        /// <remarks>
        /// Unlike the passwords of the charging stations below, this one is not
        /// made up here: it is issued by whoever runs the CSMS, and typed in.
        /// </remarks>
        public Boolean TrySetPassword(User_Id?                          Login,
                                      String?                           Password,
                                      [NotNullWhen(false)] out String?  Error)

            => TrySetPassword(Login, Password, out Error, out _);

        /// <summary>
        /// What this controller signs in with under security profiles 1 and 2 -
        /// and say whether a refusal was the file's rather than the password's.
        /// </summary>
        /// <param name="NotSaved">True where the file could not be written: the credentials are what they were.</param>
        public Boolean TrySetPassword(User_Id?                          Login,
                                      String?                           Password,
                                      [NotNullWhen(false)] out String?  Error,
                                      out Boolean                       NotSaved)
        {

            Error     = null;
            NotSaved  = false;

            var name = Login?.ToString() ?? "";

            if (name.Length == 0)
            {
                Error = "This local controller needs an identification to sign in to the CSMS with.";
                return false;
            }

            if (name.Length > MaxLoginLength)
            {
                Error = $"The identification may be at most {MaxLoginLength} characters long.";
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

                var (wasName, wasSecret) = (login, password);

                login     = User_Id.Parse(name);
                password  = secret;

                if (!TrySave(out Error))
                {
                    (login, password) = (wasName, wasSecret);
                    NotSaved             = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, $"The credentials this local controller signs in to the CSMS with were set for '{name}'.");

            }

            return true;

        }

        #endregion

        #region TrySetTOTP(Login, SharedSecret, ValidityTime, Length, Alphabet, HashAlgorithm, out Error [, out NotSaved])

        /// <summary>
        /// What this controller signs in with when the CSMS expects a one-time
        /// token instead of, or beside, a password.
        /// </summary>
        public Boolean TrySetTOTP(User_Id?                          Login,
                                  String?                           SharedSecret,
                                  TimeSpan?                         ValidityTime,
                                  UInt32?                           Length,
                                  String?                           Alphabet,
                                  TOTPHashAlgorithm?                HashAlgorithm,
                                  [NotNullWhen(false)] out String?  Error)

            => TrySetTOTP(Login, SharedSecret, ValidityTime, Length, Alphabet, HashAlgorithm, out Error, out _);

        /// <summary>
        /// What this controller signs in with when the CSMS expects a one-time
        /// token - and say whether a refusal was the file's rather than the
        /// token's.
        /// </summary>
        /// <param name="NotSaved">True where the file could not be written: the credentials are what they were.</param>
        /// <param name="TLSChannelBinding">Whether the token is to be bound to the TLS session of the line; null leaves it as it was.</param>
        public Boolean TrySetTOTP(User_Id?                          Login,
                                  String?                           SharedSecret,
                                  TimeSpan?                         ValidityTime,
                                  UInt32?                           Length,
                                  String?                           Alphabet,
                                  TOTPHashAlgorithm?                HashAlgorithm,
                                  [NotNullWhen(false)] out String?  Error,
                                  out Boolean                       NotSaved,
                                  Boolean?                          TLSChannelBinding  = null)
        {

            Error     = null;
            NotSaved  = false;

            var name = Login?.ToString() ?? "";

            if (name.Length == 0)
            {
                Error = "This local controller needs an identification to sign in to the CSMS with.";
                return false;
            }

            if (name.Length > MaxLoginLength)
            {
                Error = $"The identification may be at most {MaxLoginLength} characters long.";
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

                var (wasName, wasTOTP) = (login, totp);

                login     = User_Id.Parse(name);

                // A new secret does not undo the binding that was asked for:
                // that would be less than configured, quietly.
                totp      = settings with { TLSChannelBinding = TLSChannelBinding ?? wasTOTP?.TLSChannelBinding ?? false };

                if (!TrySave(out Error))
                {
                    (login, totp) = (wasName, wasTOTP);
                    NotSaved         = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice, $"This local controller will sign in to the CSMS as '{name}' with a one-time token: {settings}.");

            }

            return true;

        }

        #endregion

        #region TrySetTOTPBinding(Bound, out Error [, out NotSaved])

        /// <summary>
        /// Say whether this controller binds its one-time token to the TLS
        /// session of its line to the CSMS, leaving the shared secret alone.
        /// </summary>
        public Boolean TrySetTOTPBinding(Boolean                           Bound,
                                         [NotNullWhen(false)] out String?  Error)

            => TrySetTOTPBinding(Bound, out Error, out _);

        /// <summary>
        /// Say whether this controller binds its one-time token to the TLS
        /// session of its line to the CSMS - and say whether a refusal was
        /// the file's.
        /// </summary>
        /// <param name="NotSaved">True where the file could not be written: the credentials are what they were.</param>
        public Boolean TrySetTOTPBinding(Boolean                           Bound,
                                         [NotNullWhen(false)] out String?  Error,
                                         out Boolean                       NotSaved)
        {

            Error     = null;
            NotSaved  = false;

            lock (updateLock)
            {

                if (totp is null)
                {
                    Error = "This local controller has no one-time token to bind.";
                    return false;
                }

                var was  = totp;

                totp     = totp with { TLSChannelBinding = Bound };

                if (!TrySave(out Error))
                {
                    totp      = was;
                    NotSaved  = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice,
                                 Bound
                                     ? "This local controller will bind its one-time token to the TLS session of its line to the CSMS: it signs in over HTTP/3 only."
                                     : "This local controller will no longer bind its one-time token to the TLS session of its line to the CSMS.");

            }

            return true;

        }

        #endregion

        #region TryClear(out Error [, out NotSaved])

        /// <summary>
        /// Forget everything this controller signs in with.
        /// </summary>
        public Boolean TryClear([NotNullWhen(false)] out String? Error)

            => TryClear(out Error, out _);

        /// <summary>
        /// Forget everything this controller signs in with - and say whether a
        /// refusal was the file's.
        /// </summary>
        /// <param name="NotSaved">True where the file could not be written: the credentials are what they were. Nothing else refuses this.</param>
        public Boolean TryClear([NotNullWhen(false)] out String?  Error,
                                out Boolean                       NotSaved)
        {

            Error     = null;
            NotSaved  = false;

            lock (updateLock)
            {

                var was = (login, password, totp);

                login     = null;
                password  = null;
                totp      = null;

                if (!TrySave(out Error))
                {
                    (login, password, totp) = was;
                    NotSaved                   = true;
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
                return login is User_Id known && password is not null
                           ? HTTPBasicAuthentication.Create(known.ToString(), password)
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
                               new JProperty("login",        login?.ToString()),
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

                if (login is User_Id known)  json.Add("login",    known.ToString());
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
                return login is null
                           ? "no CSMS credentials"
                           : $"'{login}'{(password is not null ? " with a password" : "")}{(totp is not null ? " with a one-time token" : "")}";
        }

        #endregion

    }

}
