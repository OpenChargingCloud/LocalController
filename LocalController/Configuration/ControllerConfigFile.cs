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

#endregion

namespace cloud.charging.open.LocalController.Configuration
{

    /// <summary>
    /// The configuration of this local controller, in a file beside it.
    /// </summary>
    /// <remarks>
    /// What a local controller is - what it is called in OCPP, where it
    /// resolves names and reads the time - does not change between starts and
    /// should not have to be repeated at every one. So it is written down, and
    /// the web interface edits the file rather than a copy of it in memory.
    ///
    /// Nothing in here is secret, so it is an ordinary file that anybody who
    /// can read the directory may read. The one secret this local controller
    /// has, the web login, stays in its own file with its own permissions.
    /// </remarks>
    public sealed class ControllerConfigFile
    {

        #region Data

        /// <summary>
        /// The default file name, beside the process.
        /// </summary>
        public const String DefaultFileName = "configuration.json";

        #endregion

        #region Properties

        /// <summary>
        /// The full path of the file.
        /// </summary>
        public String   Path      { get; }

        /// <summary>
        /// Whether the file exists.
        /// </summary>
        public Boolean  Exists
            => File.Exists(Path);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The configuration file at the given path; it need not exist yet.
        /// </summary>
        public ControllerConfigFile(String Path)
        {

            if (String.IsNullOrWhiteSpace(Path))
                throw new ArgumentException("The path of the configuration file must not be empty!", nameof(Path));

            this.Path = System.IO.Path.GetFullPath(Path);

        }

        #endregion


        #region TryLoad(out Configuration, out Error)

        /// <summary>
        /// What the file says. False without an error when there is no file -
        /// which is a legitimate state and means "nothing is configured" - and
        /// false with one when the file is there but cannot be read.
        /// </summary>
        public Boolean TryLoad([NotNullWhen(true)] out ControllerConfiguration?  Configuration,
                                                   out String?                Error)
        {

            Configuration = null;

            if (!TryLoadDocument(out var document, out Error))
                return false;

            if (!ControllerConfiguration.TryParse(document, out Configuration, out var problem))
            {
                Error = $"'{Path}': {problem}";
                return false;
            }

            return true;

        }

        #endregion

        #region TryLoadDocument(out Document, out Error)

        /// <summary>
        /// The file as it stands, section by section, without asking what any
        /// of it means.
        /// </summary>
        /// <remarks>
        /// This is what makes a partial save safe: a local controller that only
        /// understands three sections still writes back the fourth one it found
        /// there, instead of quietly deleting the configuration of something it
        /// has not heard of yet.
        /// </remarks>
        public Boolean TryLoadDocument([NotNullWhen(true)] out JObject?  Document,
                                                           out String?   Error)
        {

            Document  = null;
            Error     = null;

            if (!File.Exists(Path))
            {
                // No file is not a failure, it is a local controller nobody has
                // configured yet - so an empty document, and no error.
                Document = [];
                return true;
            }

            try
            {

                var text = File.ReadAllText(Path);

                if (String.IsNullOrWhiteSpace(text))
                {
                    Document = [];
                    return true;
                }

                Document = JObject.Parse(text);
                return true;

            }
            catch (Exception e)
            {
                Error = $"'{Path}' could not be read: {e.Message}";
                return false;
            }

        }

        #endregion

        #region TryReplaceSection(Name, Value, out Error)

        /// <summary>
        /// Write one section, leaving every other one exactly as it was.
        /// </summary>
        /// <remarks>
        /// Read, change, write - and the read is deliberately done here rather
        /// than from something kept in memory: the file may have been edited by
        /// hand since this process started, and a save that wrote out a stale
        /// copy of the whole document would undo that edit without anybody
        /// asking for it.
        ///
        /// Written through a temporary file and then moved into place, so that
        /// a process which dies mid-write leaves the old configuration behind
        /// rather than half of the new one. A local controller that cannot read
        /// its own configuration does not start.
        /// </remarks>
        /// <param name="Name">The section, e.g. "dns".</param>
        /// <param name="Value">What it should now say.</param>
        /// <param name="Error">What went wrong, when something did.</param>
        public Boolean TryReplaceSection(String                            Name,
                                         JToken                            Value,
                                         [NotNullWhen(false)] out String?  Error)
        {

            if (!TryLoadDocument(out var document, out Error))
                return false;

            document[Name] = Value;

            return TryWrite(document, out Error);

        }

        #endregion

        #region TryMergeSection(Name, Values, out Error)

        /// <summary>
        /// Write the given fields into one section, leaving the fields of that
        /// section it does not mention - and every other section - alone.
        /// </summary>
        /// <remarks>
        /// What a page does not offer, it must not be able to delete. A form
        /// with six checkboxes on it sends six checkboxes, and a save that
        /// replaced the whole section would take the name servers with it
        /// because the form had nothing to say about them.
        ///
        /// Arrays are replaced rather than merged: a list of name servers is
        /// one value, and merging the old one into the new by position would
        /// produce a list nobody wrote.
        /// </remarks>
        /// <param name="Name">The section, e.g. "dns".</param>
        /// <param name="Values">The fields to write into it.</param>
        /// <param name="Error">What went wrong, when something did.</param>
        public Boolean TryMergeSection(String                            Name,
                                       JObject                           Values,
                                       [NotNullWhen(false)] out String?  Error)
        {

            if (!TryLoadDocument(out var document, out Error))
                return false;

            if (document[Name] is JObject section)
            {

                section.Merge(
                    Values,
                    new JsonMergeSettings {
                        MergeArrayHandling = MergeArrayHandling.Replace
                    }
                );

            }

            else
                document[Name] = Values;

            return TryWrite(document, out Error);

        }

        #endregion

        #region TryWrite(Document, out Error)

        /// <summary>
        /// Write the whole document.
        /// </summary>
        public Boolean TryWrite(JObject                           Document,
                                [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            try
            {

                var directory = System.IO.Path.GetDirectoryName(Path);

                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var temporary = Path + ".tmp";

                File.WriteAllText(
                    temporary,
                    Document.ToString(Formatting.Indented) + Environment.NewLine
                );

                // Move, not copy: on every file system this local controller runs on
                // this replaces the old file in one step, so there is never a
                // moment at which the configuration is half of each.
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
            => Path;

        #endregion

    }

}
