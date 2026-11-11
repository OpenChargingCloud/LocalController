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

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.CommandLine
{

    /// <summary>
    /// The key pins charging stations are let in by on the station port:
    /// listed, given, and taken away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// stationPins lists every station's key pins; stationPins &lt;station&gt;
    /// one station's; stationPins &lt;station&gt; add &lt;pin&gt; [&lt;label&gt;]
    /// lets it in by the key pin of its TLS client key - adding the station
    /// where it is not listed yet - and stationPins &lt;station&gt; remove
    /// &lt;pin&gt; takes one away. A pin is SHA-256 over the station's public
    /// key, as 64 hexadecimal digits, with or without colons, or as curl writes
    /// it, "sha256//" and base64 (EVQI Transport, key pins, 5.1 and 5.8).
    /// </para>
    /// <para>
    /// A command of its own rather than a verb of stationKeys, because the two
    /// are different keys for different doors: an SSH key for OCPP over SSH,
    /// a TLS client key for the WebSocket port. One key is never both
    /// (EVQI Transport, key pins, 5.10).
    /// </para>
    /// <para>
    /// What the web interface asks of the same account is asked here: reading
    /// the charging stations to list, changing them to give or take a pin. The
    /// console may do everything.
    /// </para>
    /// </remarks>
    /// <param name="CLI">The command line.</param>
    public class StationPinsCommand(ControllerCLI CLI) : ACLICommand<ControllerCLI>(CLI),
                                                         ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "stationPins";

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

            var rest  = Arguments.Skip(1).ToArray();
            var verb  = rest.Length < 2 ? "list" : rest[1].ToLowerInvariant();

            var needs = verb == "list"
                            ? Permission.Read(LocalControllerAccess.Stations)
                            : Permission.Edit(LocalControllerAccess.Stations);

            if (!cli.MayDo(needs, CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            return Task.FromResult(
                       rest.Length switch {
                           0 => EveryStationsPins(),
                           1 => Listed(rest[0]),
                           _ => verb switch {
                                    "add"    when rest.Length >= 3  => Add   (rest[0], rest[2], rest.Length > 3 ? String.Join(" ", rest[3..]) : null),
                                    "remove" when rest.Length == 3  => Remove(rest[0], rest[2]),
                                    _                               => [ $"Usage: {Help()}" ]
                                }
                       }
                   );

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [<station> [add <key pin> [<label>] | remove <key pin>]] - the key pins charging stations are let in by on the station port";

        #endregion


        #region (private) EveryStationsPins() / Listed(Station)

        private String[] EveryStationsPins()
        {

            var lines = new List<String>();

            foreach (var login in cli.Controller.StationLogins.Logins.Where(login => login.HasKeyPin).OrderBy(login => login.Id, StringComparer.Ordinal))
            {
                lines.Add($"{login.Id}:");
                lines.AddRange(login.Pins.Select(pin => "  " + Line(pin)));
                lines.AddRange(WhyNoPinLetsIn(login).Select(why => "  " + why));
            }

            if (lines.Count == 0)
                lines.Add("No charging station of this local controller has a key pin.");

            lines.AddRange(WhyNoStationSignsIn());

            return [ .. lines ];

        }

        private String[] Listed(String Station)
        {

            if (!cli.Controller.StationLogins.TryGet(Station, out var login))
                return [ $"'{Station}' is no charging station of this local controller." ];

            if (!login.HasKeyPin)
                return [ $"'{login.Id}' has no key pin." ];

            return [ .. login.Pins.Select(Line),
                     .. WhyNoPinLetsIn(login),
                     .. WhyNoStationSignsIn() ];

        }

        /// <summary>
        /// One key pin, as a line: the pin, when and by whom it was given, and
        /// what it is called.
        /// </summary>
        private static String Line(StationKeyPin Pin)

            => $"{Pin.Pin}  {Pin.Added.ToLocalTime():yyyy-MM-dd HH:mm}" +
               (Pin.AddedBy is not null ? $" by {Pin.AddedBy}" : "") +
               (Pin.Label   is not null ? $"  {Pin.Label}"     : "");

        #endregion

        #region (private) Add(Station, Pin, Label)

        private String[] Add(String   Station,
                             String   Pin,
                             String?  Label)
        {

            var listed = cli.Controller.StationLogins.TryGet(Station, out _);

            if (!cli.Controller.StationLogins.TryAddKeyPin(Station,
                                                           Pin,
                                                           Label,
                                                           GroupId:  null,
                                                           Note:     null,
                                                           AddedBy:  cli.Caller.Account?.Id.ToString() ?? "the console",
                                                           out var keyPin,
                                                           out var error,
                                                           out _))
            {
                return [ $"The key pin was not given: {error}" ];
            }

            cli.Node.Log.Notice(
                $"{cli.Caller.Asker} gave the charging station '{Station}' the key pin {keyPin.Pin}.",
                cli.Caller.Tags("ocpp", "station", "auth", "tls")
            );

            cli.Controller.StationLogins.TryGet(Station, out var login);

            return [ listed
                         ? $"'{Station}' is let in by the key pin {keyPin.Pin} now."
                         : $"'{Station}' is a charging station of this local controller now, in the group '{login?.GroupId}', and is let in by the key pin {keyPin.Pin}.",
                     .. login is not null ? WhyNoPinLetsIn(login) : [],
                     .. WhyNoStationSignsIn() ];

        }

        #endregion

        #region (private) Remove(Station, Pin)

        private String[] Remove(String  Station,
                                String  Pin)
        {

            if (!cli.Controller.StationLogins.TryGet(Station, out var login))
                return [ $"'{Station}' is no charging station of this local controller." ];

            if (!org.GraphDefined.Vanaheimr.Hermod.KeyPin.TryParse(Pin, out var pin) ||
                login.FindKeyPin(pin) is null)
                return [ $"'{login.Id}' has no key pin {Pin}." ];

            if (!cli.Controller.StationLogins.TryRemoveKeyPin(login.Id, pin.ToHex(), out var error, out _))
                return [ $"The key pin was not taken away: {error}" ];

            cli.Node.Log.Notice(
                $"{cli.Caller.Asker} took the key pin {pin} of the charging station '{login.Id}' away.",
                cli.Caller.Tags("ocpp", "station", "auth", "tls")
            );

            return [ $"The key pin {pin} lets '{login.Id}' in no more." ];

        }

        #endregion


        #region (private) WhyNoPinLetsIn(Login) / WhyNoStationSignsIn()

        /// <summary>
        /// What keeps this station out whatever pin it has - the station, or its
        /// group.
        /// </summary>
        private IEnumerable<String> WhyNoPinLetsIn(ChargingStationLogin Login)
        {

            if (!Login.Enabled)
                yield return $"'{Login.Id}' is switched off: no key pin lets it in.";

            var group = cli.Controller.StationLogins.GroupOf(Login);

            if (group is null)
                yield return $"The group '{Login.GroupId}' of '{Login.Id}' is gone: no key pin lets it in.";

            else if (!group.Enabled)
                yield return $"The group '{group.Id}' of '{Login.Id}' is switched off: no key pin lets it in.";

            else if (!group.Allows(AuthMethod.KeyPin))
                yield return $"The group '{group.Id}' of '{Login.Id}' does not accept key pins: no key pin lets it in.";

            else if (!group.Allows((Byte) 3))
                yield return $"The group '{group.Id}' of '{Login.Id}' does not accept security profile 3, which a client key is: no key pin lets it in.";

        }

        /// <summary>
        /// What keeps every station out - the station port.
        /// </summary>
        private IEnumerable<String> WhyNoStationSignsIn()
        {

            var profiles = cli.Controller.OCPPServerSettings.SecurityProfiles ?? [];

            if (!cli.Controller.OCPPServerEnabled)
                yield return "The charging station port is switched off: no charging station signs in.";

            else if (!profiles.Contains<Byte>(3))
                yield return "Security profile 3 is not allowed on the charging station port, and a client key is security profile 3: no key pin lets a station in.";

            else if (cli.Controller.OCPPServerRunning && !cli.Controller.OCPPServerTLS)
                yield return "The charging station port is not encrypted: no key pin lets a station in until it is.";

        }

        #endregion

    }

}
