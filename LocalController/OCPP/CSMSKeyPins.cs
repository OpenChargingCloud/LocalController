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
using System.Globalization;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// How this local controller believes the CSMS it reports to, beyond what a CA says: the key pins of
    /// the CSMS's key, one learned on first use and on probation, and the signers whose pin sets it takes
    /// ("Key Pins and Raw Public Keys for Local Controllers", sections 5.4 to 5.7) - its one line up to
    /// the CSMS, as WWCP_OCPP_Node's key-pin core asks for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file of its own beside the configuration file, as the credentials of the line have one: what is
    /// learned, and what a signed pin set brings, is written here by the controller itself, and is no
    /// setting somebody wrote. Every change is written whole, and said not to be where it could not be.
    /// </para>
    /// <para>
    /// The line is key-pinned where it has key pins or trusts on first use; otherwise its CSMS is judged
    /// by its certificate, as it always was. Where it goes is the configuration's (csms.url); a key pin is
    /// learned only for the address it was dialled at.
    /// </para>
    /// </remarks>
    public sealed class CSMSKeyPins : IPinnedTLSEndpoints
    {

        #region Data

        /// <summary>
        /// The file's name, beside the configuration file.
        /// </summary>
        public const String  DefaultFileName    = "csms-keypins.json";

        /// <summary>
        /// The identification of the line: the instance of TLSClientController that shows it.
        /// </summary>
        public const String  LineId             = "csms";

        /// <summary>
        /// How long a key pin learned waits for a signed pin set, where nothing says otherwise.
        /// </summary>
        public static readonly TimeSpan DefaultProbationTime = TimeSpan.FromMinutes(10);

        private readonly Object                 updateLock     = new ();
        private readonly Func<String?>          url;
        private readonly Action                 dialAgain;
        private readonly TimeProvider           clock;

        private List<KeyPin>                    pins           = [];
        private UInt64                          version;
        private Boolean                         confirmed;
        private RawPublicKeyMode                rawPublicKeys  = RawPublicKeyMode.Off;
        private KeyTrustOnFirstUse              trustOnFirstUse = KeyTrustOnFirstUse.None;
        private TimeSpan                        probationTime  = DefaultProbationTime;
        private KeyPin?                         learned;
        private DateTimeOffset?                 learnedAt;
        private List<PinSetSigner>              signers        = [];

        #endregion

        #region Properties

        /// <summary>
        /// Where it is written down.
        /// </summary>
        public String  Path  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// How this local controller believes its CSMS.
        /// </summary>
        /// <param name="Path">The file.</param>
        /// <param name="URL">Where the line goes now: the configuration's.</param>
        /// <param name="DialAgain">Hang the line up and dial it again.</param>
        /// <param name="Clock">The clock that says when a key pin was learned.</param>
        public CSMSKeyPins(String         Path,
                           Func<String?>  URL,
                           Action         DialAgain,
                           TimeProvider?  Clock   = null)
        {
            this.Path       = Path;
            this.url        = URL;
            this.dialAgain  = DialAgain;
            this.clock      = Clock ?? TimeProvider.System;
        }

        #endregion


        #region (record) Line

        /// <summary>
        /// The line up to the CSMS as it stands: a copy, which nothing changes.
        /// </summary>
        private sealed record Line(URL                    URL,
                                   IReadOnlyList<KeyPin>  ServerKeyPins,
                                   UInt64                 ServerKeyPinsVersion,
                                   Boolean                ServerKeyPinsConfirmed,
                                   RawPublicKeyMode       RawPublicKeys,
                                   KeyTrustOnFirstUse     TrustOnFirstUse,
                                   KeyPin?                ServerKeyPinLearned,
                                   DateTimeOffset?        ServerKeyPinLearnedAt,
                                   TimeSpan               ServerKeyPinProbationTime) : IPinnedTLSEndpoint
        {

            public String       Id            => LineId;
            public String       Description   => "The line up to the CSMS";

            public Boolean      IsSecure      => URL.Scheme == URIScheme.wss || URL.Scheme == URIScheme.https;

            public Boolean      IsKeyPinned   => IsSecure &&
                                                 (ServerKeyPins.Count > 0 || TrustOnFirstUse == KeyTrustOnFirstUse.Key);

            public KeyPinState  ServerKeyPinState

                => ServerKeyPins.Count == 0 && ServerKeyPinLearned is not null
                       ? KeyPinState.Learned
                       : ServerKeyPins.Count > 0 && ServerKeyPinsConfirmed
                             ? KeyPinState.Confirmed
                             : KeyPinState.Configured;

        }

        #endregion

        #region (private) Now()

        /// <summary>
        /// The line as it stands now; null where no address is configured.
        /// </summary>
        private Line? Now()
        {

            if (url() is not String text || !URL.TryParse(text, out var parsed))
                return null;

            lock (updateLock)
                return new Line(parsed, [.. pins], version, confirmed, rawPublicKeys, trustOnFirstUse, learned, learnedAt, probationTime);

        }

        #endregion


        #region IPinnedTLSEndpoints

        public IEnumerable<IPinnedTLSEndpoint> Secure

            => Now() is Line line && line.IsSecure
                   ? [ line ]
                   : [];

        public IReadOnlyList<PinSetSigner> PinSetSigners
        {
            get
            {
                lock (updateLock)
                    return [.. signers];
            }
        }

        public IPinnedTLSEndpoint? CopyOf(String Id)

            => Id == LineId ? Now() : null;

        /// <summary>
        /// The line up to the CSMS as it stands now; null where no address is configured.
        /// </summary>
        public IPinnedTLSEndpoint? Current
            => Now();

        public Boolean TryLearnServerKeyPin(String       Id,
                                            URL          URL,
                                            KeyPin       Pin,
                                            out String?  NotWritten)
        {

            NotWritten = null;

            if (Id != LineId || Now() is not Line line || line.URL != URL || !line.IsSecure)
                return false;

            lock (updateLock)
            {

                if (trustOnFirstUse != KeyTrustOnFirstUse.Key || pins.Count > 0 || learned is not null)
                    return false;

                learned    = Pin;
                learnedAt  = clock.GetUtcNow();

                if (TrySave(out var error))
                    return true;

                learned     = null;
                learnedAt   = null;
                NotWritten  = error;
                return false;

            }

        }

        public Boolean TryForgetLearnedServerKeyPin(String  Id,
                                                    KeyPin  Pin)
        {

            if (Id != LineId)
                return false;

            lock (updateLock)
            {

                if (learned != Pin)
                    return false;

                var (wasLearned, wasAt) = (learned, learnedAt);

                learned    = null;
                learnedAt  = null;

                if (TrySave(out _))
                    return true;

                (learned, learnedAt) = (wasLearned, wasAt);
                return false;

            }

        }

        public Boolean TryTakeSignedServerKeyPins(String                            Id,
                                                  String?                           Pins,
                                                  UInt64                            Version,
                                                  out KeyPin?                       Learned,
                                                  out Boolean                       Confirmed,
                                                  [NotNullWhen(false)] out String?  Error)
        {

            Learned    = null;
            Confirmed  = false;

            if (Id != LineId)
            {
                Error = $"This local controller has no line '{Id}'.";
                return false;
            }

            if (!TryParsePins(Pins, out var newPins, out Error))
                return false;

            lock (updateLock)
            {

                if (Version <= version)
                {
                    Error = $"The pin set has version {Version}, and this local controller holds version {version} already: only a higher one is taken.";
                    return false;
                }

                var before = (pins, version, confirmed, learned, learnedAt);

                Learned    = learned;
                Confirmed  = learned is not null && newPins.Contains(learned);

                pins       = [.. newPins];
                version    = Version;
                confirmed  = Confirmed;
                learned    = null;
                learnedAt  = null;

                if (TrySave(out Error))
                    return true;

                (pins, version, confirmed, learned, learnedAt) = before;
                Learned    = null;
                Confirmed  = false;
                return false;

            }

        }

        public Boolean TryChangeKeyPinTrust(String                            Id,
                                            ServerKeyPinSettings              Settings,
                                            out KeyPin?                       Forgotten,
                                            out Boolean                       KeyPinnedChanged,
                                            [NotNullWhen(false)] out String?  Error)
        {

            Forgotten         = null;
            KeyPinnedChanged  = false;

            if (Id != LineId)
            {
                Error = $"This local controller has no line '{Id}'.";
                return false;
            }

            if (!TryParseTrust(Settings, out var raw, out var trust, out var probation, out Error))
                return false;

            lock (updateLock)
            {

                var before      = (rawPublicKeys, trustOnFirstUse, probationTime, learned, learnedAt);
                var wasPinned   = Now() is Line line && line.IsKeyPinned;

                rawPublicKeys   = raw         ?? rawPublicKeys;
                trustOnFirstUse = trust       ?? trustOnFirstUse;
                probationTime   = probation   ?? probationTime;

                if (trustOnFirstUse == KeyTrustOnFirstUse.None && learned is not null)
                {
                    Forgotten  = learned;
                    learned    = null;
                    learnedAt  = null;
                }

                if (!TrySave(out Error))
                {
                    (rawPublicKeys, trustOnFirstUse, probationTime, learned, learnedAt) = before;
                    Forgotten = null;
                    return false;
                }

                KeyPinnedChanged = wasPinned != (Now() is Line now && now.IsKeyPinned);
                return true;

            }

        }

        public void DialAgain(String Id)
        {
            if (Id == LineId)
                dialAgain();
        }

        #endregion


        #region TrySetPins(Pins, out Error)

        /// <summary>
        /// Pin the CSMS's key by somebody who may change the line - on the page, at the command line:
        /// configured, not from a signed pin set, so its version stays as it is. No pins and no trust on
        /// first use: its certificate is judged by a CA again.
        /// </summary>
        public Boolean TrySetPins(String?                           Pins,
                                  [NotNullWhen(false)] out String?  Error)
        {

            if (!TryParsePins(Pins, out var newPins, out Error, MayBeEmpty: true))
                return false;

            lock (updateLock)
            {

                var before = (pins, confirmed, learned, learnedAt);

                pins       = [.. newPins];
                confirmed  = false;

                // A key pin set by hand ends a probation as a signed pin set does:
                // nothing learned is believed beside the pins.
                if (pins.Count > 0)
                {
                    learned    = null;
                    learnedAt  = null;
                }

                if (TrySave(out Error))
                    return true;

                (pins, confirmed, learned, learnedAt) = before;
                return false;

            }

        }

        #endregion

        #region TrySetTrust(Settings, out Error)

        /// <summary>
        /// How the line believes its CSMS beyond its pins, set by somebody who may change the line.
        /// </summary>
        public Boolean TrySetTrust(ServerKeyPinSettings              Settings,
                                   [NotNullWhen(false)] out String?  Error)

            => TryChangeKeyPinTrust(LineId, Settings, out _, out _, out Error);

        #endregion

        #region TryAddPinSetSigner(PublicKey, Description, out Error) / TryRemovePinSetSigner(PublicKey, out Error)

        /// <summary>
        /// Believe the pin sets the given key signs: the CSMS's signing anchor, from commissioning.
        /// </summary>
        public Boolean TryAddPinSetSigner(String                            PublicKey,
                                          String                            Description,
                                          [NotNullWhen(false)] out String?  Error)
        {

            if (!PinSetSigner.TryNormalize(PublicKey, out var normalized, out Error))
                return false;

            lock (updateLock)
            {

                if (signers.Any(signer => signer.PublicKey == normalized))
                {
                    Error = "That key signs pin sets for this local controller already.";
                    return false;
                }

                signers.Add(new PinSetSigner(normalized, Description.Trim(), clock.GetUtcNow()));

                if (TrySave(out Error))
                    return true;

                signers.RemoveAt(signers.Count - 1);
                return false;

            }

        }

        /// <summary>
        /// Believe the pin sets the given key signs no longer.
        /// </summary>
        public Boolean TryRemovePinSetSigner(String                            PublicKey,
                                             [NotNullWhen(false)] out String?  Error)
        {

            if (!PinSetSigner.TryNormalize(PublicKey, out var normalized, out Error))
                return false;

            lock (updateLock)
            {

                var index = signers.FindIndex(signer => signer.PublicKey == normalized);

                if (index < 0)
                {
                    Error = "That key signs no pin sets for this local controller.";
                    return false;
                }

                var removed = signers[index];
                signers.RemoveAt(index);

                if (TrySave(out Error))
                    return true;

                signers.Insert(index, removed);
                return false;

            }

        }

        #endregion


        #region (static) TryParsePins(Text, out Pins, out Error, MayBeEmpty = false)

        /// <summary>
        /// Key pins as a CSMS or a person writes them: separated by commas, 64 hexadecimal digits each,
        /// with or without colons.
        /// </summary>
        public static Boolean TryParsePins(String?                                       Text,
                                           [NotNullWhen(true)]  out IReadOnlyList<KeyPin>?  Pins,
                                           [NotNullWhen(false)] out String?                 Error,
                                           Boolean                                          MayBeEmpty   = false)
        {

            Pins   = null;
            Error  = null;

            var parts  = (Text ?? "").Split([ ',', ';', ' ', '\t', '\n', '\r' ], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parsed = new List<KeyPin>();

            foreach (var part in parts)
            {

                if (!KeyPin.TryParse(part, out var pin))
                {
                    Error = $"'{part}' is no key pin: 64 hexadecimal digits, SHA-256 over the key's SubjectPublicKeyInfo, with or without colons.";
                    return false;
                }

                if (!parsed.Contains(pin))
                    parsed.Add(pin);

            }

            if (parsed.Count == 0 && !MayBeEmpty)
            {
                Error = "A pin set has at least one key pin.";
                return false;
            }

            if (parsed.Count > KeyPinPolicy.MaxPins)
            {
                Error = $"At most {KeyPinPolicy.MaxPins} key pins.";
                return false;
            }

            Pins = parsed;
            return true;

        }

        #endregion

        #region (private static) TryParseTrust(Settings, out Raw, out Trust, out Probation, out Error)

        private static Boolean TryParseTrust(ServerKeyPinSettings              Settings,
                                             out RawPublicKeyMode?             Raw,
                                             out KeyTrustOnFirstUse?           Trust,
                                             out TimeSpan?                     Probation,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Raw        = null;
            Trust      = null;
            Probation  = null;
            Error      = null;

            if (Settings.RawPublicKeys is String rawText)
            {
                if (!Enum.TryParse<RawPublicKeyMode>(rawText.Trim(), true, out var raw) || !Enum.IsDefined(raw))
                {
                    Error = $"'{rawText}' is no setting of raw public keys: Off, Prefer or Require.";
                    return false;
                }
                Raw = raw;
            }

            if (Settings.TrustOnFirstUse is String trustText)
            {
                if (!Enum.TryParse<KeyTrustOnFirstUse>(trustText.Trim(), true, out var trust) || !Enum.IsDefined(trust))
                {
                    Error = $"'{trustText}' is no setting of trust on first use: None or Key.";
                    return false;
                }
                Trust = trust;
            }

            if (Settings.ProbationSeconds is Int64 seconds)
            {
                if (seconds < 1 || seconds > 604800)
                {
                    Error = $"A probation of {seconds} s: between 1 s and a week.";
                    return false;
                }
                Probation = TimeSpan.FromSeconds(seconds);
            }

            return true;

        }

        #endregion


        #region TryLoad(out Error)

        /// <summary>
        /// Read what was written down; nothing where nothing was.
        /// </summary>
        public Boolean TryLoad([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            if (!File.Exists(Path))
                return true;

            try
            {

                var json = JObject.Parse(File.ReadAllText(Path));

                lock (updateLock)
                {

                    pins             = [.. (json["serverKeyPins"] as JArray ?? []).Select(pin => pin.Value<String>()).
                                                                                Select(text => KeyPin.TryParse(text, out var pin) ? pin : null).
                                                                                OfType<KeyPin>()];
                    version          = pins.Count > 0 ? json.Value<UInt64?>("serverKeyPinsVersion") ?? 0 : 0;
                    confirmed        = pins.Count > 0 && json.Value<String>("serverKeyPinState") == nameof(KeyPinState.Confirmed);
                    rawPublicKeys    = Enum.TryParse<RawPublicKeyMode>  (json.Value<String>("rawPublicKeys"),   true, out var raw)   ? raw   : RawPublicKeyMode.Off;
                    trustOnFirstUse  = Enum.TryParse<KeyTrustOnFirstUse>(json.Value<String>("trustOnFirstUse"), true, out var trust) ? trust : KeyTrustOnFirstUse.None;
                    probationTime    = json.Value<Int64?>("serverKeyPinProbationTime") is Int64 seconds && seconds > 0
                                           ? TimeSpan.FromSeconds(seconds)
                                           : DefaultProbationTime;
                    learned          = pins.Count == 0 && KeyPin.TryParse(json.Value<String>("serverKeyPinLearned"), out var pin) ? pin : null;
                    learnedAt        = learned is not null ? TimeOf(json["serverKeyPinLearnedAt"]) : null;
                    signers          = [.. (json["pinSetSigners"] as JArray ?? []).OfType<JObject>().Select(PinSetSigner.TryParse).OfType<PinSetSigner>()];

                }

                return true;

            }
            catch (Exception e)
            {
                Error = $"'{Path}' could not be read: {e.Message}";
                return false;
            }

        }

        /// <summary>
        /// A point in time as it was written down, read in no culture of the machine's.
        /// </summary>
        private static DateTimeOffset? TimeOf(JToken? Token)

            => Token is JValue { Value: DateTimeOffset dateTimeOffset }
                   ? dateTimeOffset
                   : Token is JValue { Value: DateTime dateTime }
                         ? new DateTimeOffset(dateTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc) : dateTime.ToUniversalTime())
                         : Token is JValue { Type: JTokenType.String, Value: String text } &&
                           DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                               ? parsed
                               : null;

        #endregion

        #region ToJSON()

        /// <summary>
        /// How the line believes its CSMS, as it is written down, and as the page sees it.
        /// </summary>
        public JObject ToJSON()
        {

            lock (updateLock)
            {

                var json = new JObject(
                               new JProperty("serverKeyPins",              new JArray(pins.Select(pin => pin.ToHex()))),
                               new JProperty("serverKeyPinsVersion",       version),
                               new JProperty("serverKeyPinState",          (pins.Count == 0 && learned is not null
                                                                                ? KeyPinState.Learned
                                                                                : pins.Count > 0 && confirmed
                                                                                      ? KeyPinState.Confirmed
                                                                                      : KeyPinState.Configured).ToString()),
                               new JProperty("rawPublicKeys",              rawPublicKeys.  ToString()),
                               new JProperty("trustOnFirstUse",            trustOnFirstUse.ToString()),
                               new JProperty("serverKeyPinProbationTime",  (Int64) probationTime.TotalSeconds),
                               new JProperty("pinSetSigners",              new JArray(signers.Select(signer => signer.ToJSON())))
                           );

                if (learned is not null)
                {
                    json.Add(new JProperty("serverKeyPinLearned",    learned.ToHex()));
                    json.Add(new JProperty("serverKeyPinLearnedAt",  learnedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
                }

                return json;

            }

        }

        #endregion

        #region (private) TrySave(out Error)

        /// <summary>
        /// Write everything down, whole: to a file beside it first, then in its place.
        /// </summary>
        /// <remarks>Called under the update lock.</remarks>
        private Boolean TrySave([NotNullWhen(false)] out String? Error)
        {

            Error = null;

            try
            {

                var directory = System.IO.Path.GetDirectoryName(Path);

                if (!String.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var temporary = Path + ".tmp";

                OwnerOnlyFile.Write(temporary, ToJSON().ToString(Formatting.Indented) + Environment.NewLine);

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

    }

}
