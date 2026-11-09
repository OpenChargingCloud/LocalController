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

using cloud.charging.open.protocols.WWCP.Node.Logging;

using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.OpenADR
{

    /// <summary>
    /// The client secret this local controller signs in at the OpenADR 3 VTN of
    /// its grid operator with.
    /// </summary>
    /// <remarks>
    /// <b>Kept as the CSMS password is, and for the same reason.</b> It is said
    /// to somebody else, so it cannot be hashed; the file holds it as it is, and
    /// is written for its owner alone. The client ID beside it is no secret and
    /// lives in the configuration file - see
    /// <see cref="Configuration.OpenADRConfiguration"/>.
    ///
    /// <b>Never a switch of the program.</b> What a command line is started
    /// with is read by everybody who lists the processes of the machine. It is
    /// set with the JSON API, with "openADR import" at the console, or by
    /// writing this file.
    /// </remarks>
    public sealed class VTNCredentials
    {

        #region Data

        /// <summary>
        /// The file beside the configuration file, when nobody says otherwise.
        /// </summary>
        public const String  DefaultFileName  = "openadr-credentials.json";

        /// <summary>
        /// The longest a client secret may be.
        /// </summary>
        public const Int32   MaxSecretLength  = 256;

        private readonly Object  updateLock  = new ();
        private          String? secret;

        #endregion

        #region Properties

        /// <summary>
        /// The file the secret lives in.
        /// </summary>
        public String   Path    { get; }

        /// <summary>
        /// Whether a secret has been set.
        /// </summary>
        public Boolean  HasSecret
        {
            get { lock (updateLock) return secret is not null; }
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
        /// The secret in the given file; it need not exist yet.
        /// </summary>
        public VTNCredentials(String? Path = null)
        {
            this.Path = System.IO.Path.GetFullPath(Path ?? DefaultFileName);
        }

        #endregion


        #region TryLoad(out Error)

        /// <summary>
        /// Read the file. No file is not a failure - it is a controller nobody
        /// has given a secret to yet.
        /// </summary>
        public Boolean TryLoad([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            lock (updateLock)
            {

                secret = null;

                if (!File.Exists(Path))
                    return true;

                try
                {

                    var text = File.ReadAllText(Path);

                    if (String.IsNullOrWhiteSpace(text))
                        return true;

                    secret = JObject.Parse(text).Value<String>("clientSecret")?.Trim() is { Length: > 0 } read
                                 ? read
                                 : null;

                }
                catch (Exception e)
                {
                    Error = $"'{Path}' could not be read: {e.Message}";
                    return false;
                }

            }

            return true;

        }

        #endregion

        #region TrySetSecret(Secret, out Error [, out NotSaved])

        /// <summary>
        /// The client secret the grid operator gave this local controller.
        /// </summary>
        public Boolean TrySetSecret(String?                           Secret,
                                    [NotNullWhen(false)] out String?  Error)

            => TrySetSecret(Secret, out Error, out _);

        /// <summary>
        /// The client secret the grid operator gave this local controller - and
        /// say whether a refusal was the file's rather than the secret's.
        /// </summary>
        /// <param name="NotSaved">True where the file could not be written: the secret is what it was.</param>
        public Boolean TrySetSecret(String?                           Secret,
                                    [NotNullWhen(false)] out String?  Error,
                                    out Boolean                       NotSaved)
        {

            NotSaved = false;

            var given = Secret?.Trim() ?? "";

            if (given.Length == 0)
            {
                // Not made up here: the grid operator has to know it, and only
                // the grid operator can say what it is.
                Error = "The client secret is the one the grid operator gave this local controller; it cannot be made up here.";
                return false;
            }

            if (given.Length > MaxSecretLength)
            {
                Error = $"The client secret may be at most {MaxSecretLength} characters long.";
                return false;
            }

            if (given.Any(Char.IsControl))
            {
                Error = "The client secret has a control character in it, which no secret of a grid operator has: a line copied with more than the secret?";
                return false;
            }

            lock (updateLock)
            {

                var was = secret;

                secret = given;

                if (!TrySave(out Error))
                {
                    secret    = was;
                    NotSaved  = true;
                    return false;
                }

            }

            OnNotice?.Invoke(LogLevel.Notice, "The client secret this local controller signs in at the OpenADR VTN with was set.");

            return true;

        }

        #endregion

        #region TryClear(out Error [, out NotSaved])

        /// <summary>
        /// Forget the secret.
        /// </summary>
        public Boolean TryClear([NotNullWhen(false)] out String? Error)

            => TryClear(out Error, out _);

        /// <summary>
        /// Forget the secret - and say whether a refusal was the file's.
        /// </summary>
        /// <param name="NotSaved">True where the file could not be written: the secret is what it was.</param>
        public Boolean TryClear([NotNullWhen(false)] out String?  Error,
                                out Boolean                       NotSaved)
        {

            NotSaved = false;

            lock (updateLock)
            {

                var was = secret;

                secret = null;

                if (!TrySave(out Error))
                {
                    secret    = was;
                    NotSaved  = true;
                    return false;
                }

            }

            OnNotice?.Invoke(LogLevel.Notice, "The client secret this local controller signs in at the OpenADR VTN with was removed.");

            return true;

        }

        #endregion

        #region (internal) Secret()

        /// <summary>
        /// The secret itself, for the token request and for nothing else.
        /// </summary>
        internal String? Secret()
        {
            lock (updateLock)
                return secret;
        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// What the web interface reads of it: where it is and whether there is
        /// one - never the secret itself, which is the one thing that would let a
        /// reader of the page become this controller at the grid operator.
        /// </summary>
        public JObject ToJSON()

            => new (new JProperty("file",       Path),
                    new JProperty("hasSecret",  HasSecret));

        #endregion


        #region (private) TrySave(out Error)

        /// <summary>
        /// Write the file, readable by its owner alone. Called under the lock.
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

                if (secret is not null)
                    json.Add("clientSecret", secret);

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

            => HasSecret
                   ? "an OpenADR client secret"
                   : "no OpenADR client secret";

        #endregion

    }

}
