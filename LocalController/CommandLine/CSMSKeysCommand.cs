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

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

#endregion

namespace cloud.charging.open.LocalController.CommandLine
{

    /// <summary>
    /// How this local controller believes its CSMS beyond what a CA says: the key pins of the line up to
    /// it, the one learned on first use, and the signers of pin sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// csmsKeys lists them; csmsKeys pins &lt;key pin&gt;[,...] | none pins the CSMS's key, configured
    /// and not signed; csmsKeys tofu on | off learns the key pin of the first key shown, on probation;
    /// csmsKeys probation &lt;seconds&gt; says how long a learned one waits for a signed pin set;
    /// csmsKeys raw off | prefer | require offers raw public keys; csmsKeys signer add &lt;public key&gt;
    /// &lt;description&gt; and csmsKeys signer remove &lt;public key&gt; say whose pin sets are believed
    /// ("Key Pins and Raw Public Keys for Local Controllers", sections 5.4 to 5.7).
    /// </para>
    /// <para>
    /// What the web interface asks of the same account is asked here: reading the line to list, changing
    /// it for the rest. Every change applies from the next dial of the line.
    /// </para>
    /// </remarks>
    /// <param name="CLI">The command line.</param>
    public class CSMSKeysCommand(ControllerCLI CLI) : ACLICommand<ControllerCLI>(CLI),
                                                      ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "csmsKeys";

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            var rest   = Arguments.Skip(1).ToArray();
            var verb   = rest.Length == 0 ? "list" : rest[0].ToLowerInvariant();

            var needs  = verb == "list"
                             ? Permission.Read(LocalControllerAccess.CSMS)
                             : Permission.Edit(LocalControllerAccess.CSMS);

            if (!cli.MayDo(needs, CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            return Task.FromResult(
                       verb switch {
                           "list"       when rest.Length == 1 || rest.Length == 0  => Listed(),
                           "pins"       when rest.Length == 2                      => Pins(rest[1]),
                           "tofu"       when rest.Length == 2                      => Trust(new ServerKeyPinSettings(null, rest[1].ToLowerInvariant() switch { "on" => "Key", "off" => "None", _ => rest[1] }, null)),
                           "probation"  when rest.Length == 2                      => Int64.TryParse(rest[1], out var seconds)
                                                                                          ? Trust(new ServerKeyPinSettings(null, null, seconds))
                                                                                          : [ $"'{rest[1]}' is no number of seconds." ],
                           "raw"        when rest.Length == 2                      => Trust(new ServerKeyPinSettings(rest[1], null, null)),
                           "signer"     when rest.Length >= 4 && rest[1].Equals("add",    StringComparison.OrdinalIgnoreCase)  => AddSigner(rest[2], String.Join(' ', rest.Skip(3))),
                           "signer"     when rest.Length == 3 && rest[1].Equals("remove", StringComparison.OrdinalIgnoreCase)  => RemoveSigner(rest[2]),
                           _                                                       => [ $"Usage: {Help()}" ]
                       }
                   );

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [list | pins <key pin>[,...]|none | tofu on|off | probation <seconds> | raw off|prefer|require | " +
                "signer add <public key> <description> | signer remove <public key>] - how this local controller believes its CSMS";

        #endregion


        #region (private) Listed()

        private String[] Listed()
        {

            var json   = cli.Controller.CSMSKeyPinsJSON();
            var lines  = new List<String>();

            var pins   = (json["serverKeyPins"] as Newtonsoft.Json.Linq.JArray ?? []).Select(pin => pin.ToString()).ToArray();

            lines.Add(json.Value<Boolean>("overTLS")
                          ? json.Value<Boolean>("keyPinned")
                                ? "The CSMS is believed by the key pin of its key, and by nothing else of its certificate."
                                : "The CSMS is believed by its certificate, as a CA vouches for it."
                          : "The line up to the CSMS does not go over TLS (wss://): there is no key to pin.");

            lines.Add($"key pins          {(pins.Length > 0 ? String.Join(", ", pins) : "none")}" +
                      (pins.Length > 0 ? $"  ({json.Value<String>("serverKeyPinState")}, version {json.Value<UInt64>("serverKeyPinsVersion")})" : ""));

            if (json.Value<String>("serverKeyPinLearned") is String learned)
                lines.Add($"learned           {learned}  on first use{(json.Value<Boolean>("onProbation") ? ", on probation: nothing the CSMS asks is carried out or sent on to a station but a signed pin set" : "")}");

            if (json.Value<String>("keyPinShown") is String shown)
                lines.Add($"shown last        {shown}");

            lines.Add($"trust on first use {(json.Value<String>("trustOnFirstUse") == "Key" ? "on" : "off")}, probation {json.Value<Int64>("serverKeyPinProbationTime")} s, " +
                      $"raw public keys {json.Value<String>("rawPublicKeys")?.ToLowerInvariant()}");

            var signers = (json["pinSetSigners"] as Newtonsoft.Json.Linq.JArray ?? []).OfType<Newtonsoft.Json.Linq.JObject>().ToArray();

            if (signers.Length == 0)
                lines.Add("No signer of pin sets is believed: a pin set sent over OCPP is refused.");

            else
            {
                lines.Add("signers of pin sets:");
                lines.AddRange(signers.Select(signer => $"  {signer.Value<String>("publicKey")}  {signer.Value<String>("description")}"));
            }

            return [ .. lines ];

        }

        #endregion

        #region (private) Pins(Text)

        private String[] Pins(String Text)
        {

            var pins = Text.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : Text;

            if (!cli.Controller.CSMSPins.TrySetPins(pins, out var error))
                return [ $"Nothing was changed: {error}" ];

            cli.Node.Log.Notice($"{cli.Caller.Asker} set the key pins of the line up to the CSMS: {(pins.Length > 0 ? pins : "none")}.",
                                cli.Caller.Tags("ocpp", "csms", "keypins", "security"));

            return [ .. Listed(), "It applies from the next dial of the line." ];

        }

        #endregion

        #region (private) Trust(Settings)

        private String[] Trust(ServerKeyPinSettings Settings)
        {

            if (!cli.Controller.CSMSPins.TrySetTrust(Settings, out var error))
                return [ $"Nothing was changed: {error}" ];

            cli.Node.Log.Notice($"{cli.Caller.Asker} changed how the line up to the CSMS believes its server.",
                                cli.Caller.Tags("ocpp", "csms", "keypins", "security"));

            return [ .. Listed(), "It applies from the next dial of the line." ];

        }

        #endregion

        #region (private) AddSigner(PublicKey, Description) / RemoveSigner(PublicKey)

        private String[] AddSigner(String PublicKey, String Description)
        {

            if (!cli.Controller.CSMSPins.TryAddPinSetSigner(PublicKey, Description, out var error))
                return [ $"Nothing was changed: {error}" ];

            cli.Node.Log.Notice($"{cli.Caller.Asker} added a signer of pin sets for the line up to the CSMS: '{Description}'.",
                                cli.Caller.Tags("ocpp", "csms", "keypins", "security"));

            return Listed();

        }

        private String[] RemoveSigner(String PublicKey)
        {

            if (!cli.Controller.CSMSPins.TryRemovePinSetSigner(PublicKey, out var error))
                return [ $"Nothing was changed: {error}" ];

            cli.Node.Log.Notice($"{cli.Caller.Asker} removed a signer of pin sets for the line up to the CSMS.",
                                cli.Caller.Tags("ocpp", "csms", "keypins", "security"));

            return Listed();

        }

        #endregion

    }

}
