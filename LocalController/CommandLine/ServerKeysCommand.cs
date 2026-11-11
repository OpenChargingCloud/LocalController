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
    /// The TLS server key of the charging station port and its key pins: shown,
    /// made, announced for the next rollover, and switched to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// serverKeys lists the key pin the charging stations pin - the current key
    /// first, then the announced next one - and every key with its own;
    /// serverKeys create [&lt;algorithm&gt;] makes the server key where nothing
    /// is presented yet; serverKeys next [&lt;algorithm&gt;] announces the next
    /// key, whose pin is handed to the stations before the switch; serverKeys
    /// switch presents it from now on (EVQI Transport, key pins, 5.5 and 7.4).
    /// </para>
    /// <para>
    /// What the web interface asks of the same account is asked here: reading
    /// the certificates to list, changing them for the rest. The console may do
    /// everything.
    /// </para>
    /// </remarks>
    /// <param name="CLI">The command line.</param>
    public class ServerKeysCommand(ControllerCLI CLI) : ACLICommand<ControllerCLI>(CLI),
                                                        ICLICommand
    {

        #region Data

        /// <summary>
        /// The name this is typed as.
        /// </summary>
        public const String CommandName = "serverKeys";

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
            var verb  = rest.Length == 0 ? "list" : rest[0].ToLowerInvariant();

            var needs = verb == "list"
                            ? Permission.Read(NodeResources.Certificates)
                            : Permission.Edit(NodeResources.Certificates);

            if (!cli.MayDo(needs, CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            return Task.FromResult(
                       verb switch {
                           "list"   when rest.Length <= 1  => Listed(),
                           "create" when rest.Length <= 2  => Create(rest.ElementAtOrDefault(1), AsNext: false),
                           "next"   when rest.Length <= 2  => Create(rest.ElementAtOrDefault(1), AsNext: true),
                           "switch" when rest.Length == 1  => Switch(),
                           _                               => [ $"Usage: {Help()}" ]
                       }
                   );

        }

        #endregion

        #region Help()

        public override String Help()

            => $"{CommandName} [list | create [<algorithm>] | next [<algorithm>] | switch] - the key the charging stations pin, and its next one";

        #endregion


        #region (private) Listed()

        private String[] Listed()
        {

            var store    = cli.Controller.ServerCertificates;
            var current  = store.GetCurrentKey(out var presentedAs);
            var next     = store.NextKey;
            var lines    = new List<String>();

            if (current is null)
                lines.Add("No key is presented to the charging stations. 'serverKeys create' makes one for them to pin.");

            else
            {

                lines.Add($"key pin       {current.KeyPin}  {current.Id}, {current.Algorithm}, made {current.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}, " +
                          (presentedAs == ServerCertificateStore.PresentedAsCarrier
                               ? "in its carrier certificate"
                               : "in the certificate an authority issued for it"));

                if (!cli.Controller.OCPPServerTLS)
                    lines.Add("              The charging station port is not encrypted, so it presents no key; it is from the next start.");

            }

            if (next is not null)
                lines.Add($"next key pin  {next.KeyPin}  {next.Id}, {next.Algorithm}, made {next.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} - " +
                           "hand it to the charging stations, then 'serverKeys switch'");

            var others = store.Entries.Where(entry => entry.Id != current?.Id && entry.Id != next?.Id).ToArray();

            if (others.Length > 0)
            {
                lines.Add("other keys:");
                lines.AddRange(others.Select(entry => $"  {entry.Id}  {entry.Algorithm}  key pin {entry.KeyPin}  made {entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                                                      (entry.Certificate is not null ? $", certificate until {entry.NotAfter:yyyy-MM-dd}"
                                                           : entry.HasCarrier       ? ", no longer presented"
                                                           : ", waiting for a certificate")));
            }

            return [ .. lines ];

        }

        #endregion

        #region (private) Create(Algorithm, AsNext)

        private String[] Create(String?  Algorithm,
                                Boolean  AsNext)
        {

            if (!cli.Controller.ServerCertificates.TryCreateServerKey(Algorithm, AsNext, out var id, out var keyPin, out var error, out _))
                return [ $"No key was made: {error}" ];

            cli.Node.Log.Notice(
                $"{cli.Caller.Asker} had this local controller generate the {(AsNext ? "next " : "")}server key '{id}' with the key pin {keyPin}.",
                cli.Caller.Tags("ocpp", "tls")
            );

            return AsNext
                       ? [ $"next key pin  {keyPin}  {id}",
                           "Hand it to the charging stations; 'serverKeys switch' presents it once they all have it." ]
                       : [ $"key pin       {keyPin}  {id}",
                           .. cli.Controller.OCPPServerTLS
                                  ? Array.Empty<String>()
                                  : [ "The charging station port is not encrypted yet; it is from the next start, in this key's carrier certificate." ] ];

        }

        #endregion

        #region (private) Switch()

        private String[] Switch()
        {

            if (!cli.Controller.ServerCertificates.TrySwitchToNextKey(out var id, out var error, out _))
                return [ $"Nothing was switched: {error}" ];

            cli.Node.Log.Notice(
                $"{cli.Caller.Asker} switched the charging station server to the server key '{id}'.",
                cli.Caller.Tags("ocpp", "tls")
            );

            return [ $"The charging station server presents the key '{id}' from the next handshake on, with the key pin {cli.Controller.ServerCertificates.CurrentKey?.KeyPin}.",
                     "Charging stations that pinned only the key before are refused from then on." ];

        }

        #endregion

    }

}
