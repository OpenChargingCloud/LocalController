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

using System.Globalization;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

using cloud.charging.open.LocalController.OpenADR;

#endregion

namespace cloud.charging.open.LocalController.CommandLine
{

    /// <summary>
    /// The grid operator's OpenADR 3 VTN this local controller reads its limits
    /// from: the connection, the limits, asking now, switching it on and off,
    /// and taking in the credentials the grid operator gave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// openADR shows the connection; openADR limits the limits that are not
    /// over, and which of them holds now; openADR poll asks the VTN at once;
    /// openADR on and openADR off switch asking it on and off.
    /// </para>
    /// <para>
    /// openADR import &lt;file&gt; takes in what the grid operator's
    /// "localControllers add" printed - the token URL, the client ID and the
    /// client secret, as those lines or as JSON - from a file, so that the
    /// secret is never typed where a command line is kept: in a history, a
    /// log, or the list of the machine's processes. It is read at the console
    /// alone: a file of this machine is no business of an account signed in
    /// over SSH.
    /// </para>
    /// </remarks>
    /// <param name="CLI">The command line.</param>
    public class OpenADRCommand(ControllerCLI CLI) : ACLICommand<ControllerCLI>(CLI),
                                                     ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "openADR";

        private static readonly String[] Verbs = [ "limits", "poll", "on", "off", "import" ];

        #endregion

        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)
        {

            if (Arguments.Length == 1)
                return CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                           ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                           : [];

            if (Arguments.Length == 2 && Arguments[0].Equals(CommandName, StringComparison.OrdinalIgnoreCase))
                return Verbs.Where (verb => verb.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).
                             Select(verb => SuggestionResponse.ParameterCompleted($"{CommandName} {verb}")).
                             ToArray();

            return [];

        }

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override async Task<String[]> Execute(String[]           Arguments,
                                                     CancellationToken  CancellationToken)
        {

            var rest  = Arguments.Skip(1).ToArray();
            var verb  = rest.Length == 0 ? "" : rest[0].ToLowerInvariant();

            var needs = verb switch {
                            "" or "limits"            => Permission.Read(LocalControllerAccess.OpenADR),
                            "poll"                    => Permission.Run (LocalControllerAccess.OpenADR),
                            _                         => Permission.Edit(LocalControllerAccess.OpenADR)
                        };

            if (!cli.MayDo(needs, CommandName, out var refused))
                return [ refused ];

            return verb switch {

                       "limits"  when rest.Length == 1  => Limits(),
                       "poll"    when rest.Length == 1  => await Poll(CancellationToken),
                       "on"      when rest.Length == 1  => await Switch(true),
                       "off"     when rest.Length == 1  => await Switch(false),
                       "import"  when rest.Length >= 2  => await Import(String.Join(" ", rest[1..])),
                       ""                               => Connection(),
                       _                                => [ $"Usage: {Help()}" ]
                   };

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [limits | poll | on | off | import <file>] - the grid operator's OpenADR 3 VTN and the limits it sent";

        #endregion


        #region (private) Connection()

        private String[] Connection()
        {

            var controller  = cli.Controller;
            var settings    = controller.OpenADRSettings;
            var (asked, read) = controller.OpenADRLastPoll;
            var (tokenURL, expires) = controller.OpenADRToken;

            var lines = new List<String>();

            if (settings.Enabled != true)
                lines.Add(settings.VTNURL is null
                              ? "This local controller asks no OpenADR VTN. 'openADR import <file>' takes in the credentials the grid operator gave."
                              : $"Asking the OpenADR VTN at {settings.VTNURL} is switched off; 'openADR on' switches it on.");
            else
                lines.Add($"OpenADR VTN     {settings.VTNURL}{(settings.InTheClear ? "  (unencrypted)" : "")}");

            if (settings.VTNURL is not null)
            {
                lines.Add($"  client ID     {settings.ClientID ?? "none"}");
                lines.Add($"  secret        {(controller.OpenADRLogin.HasSecret ? "set" : "none")}, in {controller.OpenADRLogin.Path}");
                lines.Add($"  asked every   {(settings.PollEvery ?? TimeSpan.Zero).TotalSeconds:0} s");
            }

            if (settings.Enabled == true)
            {

                lines.Add($"  token URL     {tokenURL?.ToString() ?? "not asked yet"}");
                lines.Add($"  signed in     {(controller.OpenADRSignedIn && expires is DateTimeOffset until ? $"yes, until {Local(until)}" : "no")}");
                lines.Add($"  last asked    {(asked is DateTimeOffset at ? Local(at) : "never")}");
                lines.Add($"  last read     {(read  is DateTimeOffset at2 ? Local(at2) : "never")}");

                if (controller.OpenADRLastProblem is String problem)
                    lines.Add($"  problem       {problem}");

                lines.Add($"  limits        {controller.GridLimits.Count} not over; " + InForce());

            }

            return [ .. lines ];

        }

        #endregion

        #region (private) Limits()

        private String[] Limits()
        {

            var controller  = cli.Controller;
            var now         = controller.TimeProvider.GetUtcNow();
            var limits      = controller.GridLimits;

            if (limits.Count == 0)
                return [ controller.OpenADREnabled
                             ? "The grid operator sent no limit that is not over."
                             : "This local controller asks no OpenADR VTN for limits." ];

            return [ .. limits.Select(limit => $"{limit.StateAt(now),-10} {limit.Kind,-22} {KW(limit.KW),10} kW  " +
                                               $"{Local(limit.Start)} - {(limit.End is DateTimeOffset end ? Local(end) : "until withdrawn")}  " +
                                               $"event {limit.EventId}" +
                                               (limit.Interval > 0 ? $"/{limit.Interval}" : "") +
                                               (limit.EventName is not null ? $" '{limit.EventName}'" : "")),
                     InForce() ];

        }

        #endregion

        #region (private) Poll(CancellationToken)

        private async Task<String[]> Poll(CancellationToken CancellationToken)
        {

            var problem = await cli.Controller.PollOpenADR(CancellationToken);

            return problem is null
                       ? [ $"The VTN answered: {cli.Controller.GridLimits.Count} limit(s) not over; {InForce()}" ]
                       : [ problem ];

        }

        #endregion

        #region (private) Switch(On)

        private async Task<String[]> Switch(Boolean On)
        {

            var (changed, error, _) = await cli.Controller.TryUpdateOpenADRConfiguration(new JObject(new JProperty("enabled", On)));

            if (!changed)
                return [ $"Asking the OpenADR VTN was not switched {(On ? "on" : "off")}: {error}" ];

            cli.Node.Log.Notice($"{cli.Caller.Asker} switched asking the OpenADR VTN {(On ? "on" : "off")}.",
                                cli.Caller.Tags("openadr", "config"));

            return On
                       ? [ $"This local controller asks the OpenADR VTN at {cli.Controller.OpenADRSettings.VTNURL} now; 'openADR' shows how it goes." ]
                       : [ "This local controller asks no OpenADR VTN any more." ];

        }

        #endregion

        #region (private) Import(File)

        /// <summary>
        /// Take in the credentials of a file: the lines the grid operator's
        /// "localControllers add" printed, or JSON.
        /// </summary>
        private async Task<String[]> Import(String File)
        {

            if (!cli.Caller.AtTheConsole)
                return [ $"'{CommandName} import' reads a file of this machine, and is typed at its console alone." ];

            String text;

            try
            {
                text = System.IO.File.ReadAllText(File.Trim('"'));
            }
            catch (Exception e)
            {
                return [ $"'{File}' could not be read: {e.Message}" ];
            }

            if (!TryCredentials(text, out var vtnURL, out var clientId, out var clientSecret, out var missing))
                return [ $"'{File}' has no {missing}: it is what the grid operator's 'localControllers add' printed - the lines " +
                         "'token URL', 'client ID' and 'client secret' - or JSON with tokenURL or vtnURL, clientID and clientSecret." ];

            var (changed, error, _) = await cli.Controller.TryUpdateOpenADRConfiguration(
                                                new JObject(
                                                    new JProperty("enabled",   true),
                                                    new JProperty("vtnURL",    vtnURL),
                                                    new JProperty("clientID",  clientId)
                                                ),
                                                clientSecret
                                            );

            if (!changed)
                return [ $"The credentials were not taken in: {error}" ];

            cli.Node.Log.Notice($"{cli.Caller.Asker} took in the OpenADR credentials of '{clientId}' for the VTN at {vtnURL}.",
                                cli.Caller.Tags("openadr", "config", "auth"));

            var problem = await cli.Controller.PollOpenADR();

            return [ $"This local controller signs in at the OpenADR VTN at {vtnURL} as '{clientId}' now; the secret is in {cli.Controller.OpenADRLogin.Path}.",
                     problem is null
                         ? $"The VTN answered: {cli.Controller.GridLimits.Count} limit(s) not over; {InForce()}"
                         : problem,
                     $"'{File}' still holds the secret: delete it." ];

        }

        #endregion

        #region (internal static) TryCredentials(Text, out VTNURL, out ClientId, out ClientSecret, out Missing)

        /// <summary>
        /// The VTN URL, the client ID and the secret of what the grid operator
        /// printed - or the name of what is missing.
        /// </summary>
        internal static Boolean TryCredentials(String      Text,
                                               out String  VTNURL,
                                               out String  ClientId,
                                               out String  ClientSecret,
                                               out String  Missing)
        {

            String? vtn       = null;
            String? token     = null;
            String? clientId  = null;
            String? secret    = null;

            var trimmed = Text.Trim();

            if (trimmed.StartsWith('{'))
            {
                try
                {
                    var json  = JObject.Parse(trimmed);
                    vtn       = json.Value<String>("vtnURL");
                    token     = json.Value<String>("tokenURL");
                    clientId  = json.Value<String>("clientID")     ?? json.Value<String>("client_id");
                    secret    = json.Value<String>("clientSecret") ?? json.Value<String>("client_secret");
                }
                catch (Newtonsoft.Json.JsonException)
                { }
            }
            else
            {
                foreach (var line in trimmed.Split('\n').Select(line => line.Trim()))
                {
                    if      (line.StartsWith("VTN URL ",       StringComparison.OrdinalIgnoreCase))  vtn       = line[8..] .Trim();
                    else if (line.StartsWith("token URL ",     StringComparison.OrdinalIgnoreCase))  token     = line[10..].Trim();
                    else if (line.StartsWith("client ID ",     StringComparison.OrdinalIgnoreCase))  clientId  = line[10..].Trim();
                    else if (line.StartsWith("client secret ", StringComparison.OrdinalIgnoreCase))  secret    = line[14..].Trim();
                }
            }

            // The VTN is what the token URL is "auth/token" below.
            if (vtn is null && token is not null)
            {
                var path  = token.TrimEnd('/');
                vtn       = path.EndsWith("/auth/token", StringComparison.OrdinalIgnoreCase)
                                ? path[..^"auth/token".Length]
                                : null;
            }

            VTNURL        = vtn      ?? "";
            ClientId      = clientId ?? "";
            ClientSecret  = secret   ?? "";
            Missing       = vtn      is null or { Length: 0 } ? "VTN URL or token URL ending in auth/token"
                          : clientId is null or { Length: 0 } ? "client ID"
                          : secret   is null or { Length: 0 } ? "client secret"
                          : "";

            return Missing.Length == 0;

        }

        #endregion

        #region (private) InForce() / KW(Value) / Local(Timestamp)

        /// <summary>
        /// What holds now: the import limit and the export limit.
        /// </summary>
        private String InForce()
        {

            var import = cli.Controller.ImportLimitNow;
            var export = cli.Controller.ExportLimitNow;

            if (import is null && export is null)
                return "none holds now.";

            return "now: " + String.Join(", ", new[] {
                                 import is not null ? $"draw at most {KW(import.KW)} kW" : null,
                                 export is not null ? $"feed in at most {KW(export.KW)} kW" : null
                             }.OfType<String>()) + ".";

        }

        private static String KW(Double Value)

            => Value.ToString("0.###", CultureInfo.InvariantCulture);

        private static String Local(DateTimeOffset Timestamp)

            => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        #endregion

    }

}
