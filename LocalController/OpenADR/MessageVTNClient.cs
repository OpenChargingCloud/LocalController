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

using System.Net;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.OpenADRv3;
using cloud.charging.open.protocols.OpenADRv3.Messaging;
using cloud.charging.open.protocols.WWCP.Node.Connections;

#endregion

namespace cloud.charging.open.LocalController.OpenADR
{

    /// <summary>
    /// What this local controller asks the VTN of its grid operator in the JSON
    /// messages of OpenADR, over its connection to the grid operator of the
    /// given URL in that binding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection is looked up at every request, as the tunnel of the REST
    /// binding is: one written down, changed or made anew after this client was
    /// made is the one asked over. Who this VEN is, is said by the connection:
    /// no token, no secret.
    /// </para>
    /// <para>
    /// Over every connection it asks over, it subscribes to the events first -
    /// once, where the VTN holds no subscription of it over the connection yet
    /// -, with the URL of the connection as the callback: the VTN then tells it
    /// of every new, changed or removed event by Notify over the connection.
    /// A subscription that fails is said, and the poll goes on without it.
    /// </para>
    /// <para>
    /// What the VTN answers with an error is a <see cref="VTNException"/> with
    /// the HTTP status the REST API would have answered with, so that the poll
    /// judges it as it judges one over HTTP.
    /// </para>
    /// </remarks>
    /// <param name="Controller">The local controller.</param>
    /// <param name="URL">The URL of its connection to the grid operator.</param>
    /// <param name="ClientID">The name it reports and subscribes under.</param>
    /// <param name="RequestTimeout">How long an answer is waited for.</param>
    public sealed class MessageVTNClient(LocalController  Controller,
                                         String           URL,
                                         String           ClientID,
                                         TimeSpan         RequestTimeout) : IVTNClient
    {

        #region Data

        /// <summary>
        /// How many of a list are asked for at once.
        /// </summary>
        public const Int32  PageSize  = VENClient.PageSize;

        /// <summary>
        /// The most pages of a list asked for.
        /// </summary>
        public const Int32  MaxPages  = VENClient.MaxPages;

        private OpenADRMessageEndpoint?  subscribedOver;

        #endregion

        #region Properties

        public Uri              VTNURL                   { get; } = new (URL);
        public String           ClientID                 { get; } = ClientID;
        public Uri?             TokenURL                 => null;
        public DateTimeOffset?  TokenExpires             => null;
        public Boolean          SignedInByTheConnection  => true;

        /// <summary>
        /// Whether a subscription of the events is held over the connection asked over last.
        /// </summary>
        public Boolean          Subscribed
            => subscribedOver is { HasEnded: false };

        #endregion


        #region GetEvents(CancellationToken)

        public async Task<IReadOnlyList<JObject>> GetEvents(CancellationToken CancellationToken = default)
        {

            var endpoint = EndpointNow();

            await SubscribeOnce(endpoint, CancellationToken).ConfigureAwait(false);

            return await GetAll(endpoint, OpenADRActions.GetEvents, "events", [ new ("active", true) ], CancellationToken).ConfigureAwait(false);

        }

        #endregion

        #region GetReports(CancellationToken)

        public async Task<IReadOnlyList<JObject>> GetReports(CancellationToken CancellationToken = default)

            => await GetAll(EndpointNow(), OpenADRActions.GetReports, "reports", [], CancellationToken).ConfigureAwait(false);

        #endregion

        #region PostReport(Report, CancellationToken)

        public async Task<JObject> PostReport(Report             Report,
                                              CancellationToken  CancellationToken = default)
        {

            var json = Report.ToJSON();
            json["objectType"] = "REPORT";

            var outcome = await Ask(EndpointNow(),
                                    OpenADRActions.CreateReport,
                                    OpenADRActions.Request(OpenADRActions.CreateReport, Body: json),
                                    $"the report on the event '{Report.EventId}'",
                                    CancellationToken).ConfigureAwait(false);

            return outcome;

        }

        #endregion

        #region SignOut()

        /// <summary>
        /// Nothing to forget: the connection says who this VEN is.
        /// </summary>
        public void SignOut()
        { }

        #endregion


        #region (private) EndpointNow()

        private OpenADRMessageEndpoint EndpointNow()

            => Controller.GridOperatorEndpointOf(URL)
                   ?? throw new VTNException(
                          Controller.GridOperatorConnectionOf(URL, OpenADRBinding.Messages) is null
                              ? $"There is no connection to a grid operator of the URL {URL} in the binding 'messages' to ask the VTN over."
                              : $"The connection to the grid operator {URL} is not up; the VTN is asked again once it is."
                      );

        #endregion

        #region (private) SubscribeOnce(Endpoint, CancellationToken)

        /// <summary>
        /// Subscribe to the events over the given connection, where that was not
        /// done yet and the VTN holds no subscription of this VEN's over it.
        /// </summary>
        private async Task SubscribeOnce(OpenADRMessageEndpoint  Endpoint,
                                         CancellationToken       CancellationToken)
        {

            if (ReferenceEquals(subscribedOver, Endpoint))
                return;

            try
            {

                var held = await GetAll(Endpoint,
                                        OpenADRActions.GetSubscriptions,
                                        "subscriptions",
                                        [ new ("clientName", ClientID) ],
                                        CancellationToken).ConfigureAwait(false);

                var over = held.Any(subscription => subscription["objectOperations"] is JArray objectOperations &&
                                                    objectOperations.OfType<JObject>().Any(objectOperation => String.Equals(objectOperation["callbackUrl"]?.Value<String>(),
                                                                                                                            URL,
                                                                                                                            StringComparison.OrdinalIgnoreCase)));

                if (!over)
                {

                    await Ask(Endpoint,
                              OpenADRActions.CreateSubscription,
                              OpenADRActions.Request(
                                  OpenADRActions.CreateSubscription,
                                  Body: new JObject(
                                            new JProperty("clientName",        ClientID),
                                            new JProperty("objectOperations",  new JArray(
                                                new JObject(
                                                    new JProperty("objects",      new JArray("EVENT")),
                                                    new JProperty("operations",   new JArray("CREATE", "UPDATE", "DELETE")),
                                                    new JProperty("callbackUrl",  URL)
                                                )
                                            ))
                                        )
                              ),
                              "a subscription of the events",
                              CancellationToken).ConfigureAwait(false);

                    Controller.Log.Info($"This local controller subscribed to the events of its grid operator over {URL}: it is told of every change at once.", "openadr", "messages");

                }

                subscribedOver = Endpoint;

            }
            catch (VTNException e)
            {
                Controller.Log.Warning($"This local controller could not subscribe to the events of its grid operator over {URL}, and asks every so often only: {e.Message}", "openadr", "messages");
            }

        }

        #endregion

        #region (private) GetAll(Endpoint, Action, What, Query, CancellationToken)

        private async Task<IReadOnlyList<JObject>> GetAll(OpenADRMessageEndpoint                  Endpoint,
                                                          String                                  Action,
                                                          String                                  What,
                                                          IEnumerable<KeyValuePair<String, JToken?>>  Query,
                                                          CancellationToken                       CancellationToken)
        {

            var all = new List<JObject>();

            for (var page = 0; page < MaxPages; page++)
            {

                var answer = await Ask(Endpoint,
                                       Action,
                                       OpenADRActions.Request(Action, Query: [ .. Query, new ("skip", page * PageSize), new ("limit", PageSize) ]),
                                       $"its {What}",
                                       CancellationToken).ConfigureAwait(false);

                var list = OpenADRActions.ListOf(Action, answer);

                all.AddRange(list);

                if (list.Count < PageSize)
                    return all;

            }

            throw new VTNException($"The VTN listed more than {MaxPages * PageSize} {What}; this local controller stopped reading there.");

        }

        #endregion

        #region (private) Ask(Endpoint, Action, Payload, What, CancellationToken)

        private async Task<JObject> Ask(OpenADRMessageEndpoint  Endpoint,
                                        String                  Action,
                                        JObject                 Payload,
                                        String                  What,
                                        CancellationToken       CancellationToken)
        {

            OpenADRCallOutcome outcome;

            try
            {
                outcome = await Endpoint.AskAsync(Action, Payload, RequestTimeout, CancellationToken).ConfigureAwait(false);
            }
            catch (OpenADRMessageException e)
            {
                throw new VTNException($"The VTN over {URL} was not asked for {What}: {e.Message}", null, e);
            }

            if (outcome.Succeeded)
                return outcome.Payload;

            throw new VTNException($"The VTN over {URL} did not answer '{Action}' for {What}: {outcome.ErrorCode}: {outcome.ErrorDescription}",
                                   outcome.Status is Int32 status ? (HttpStatusCode) status : null);

        }

        #endregion


        #region Dispose()

        public void Dispose()
        { }

        #endregion

    }


    /// <summary>
    /// What this local controller asks the VTN through its connection to the
    /// grid operator of the given URL, in whichever binding that connection is
    /// written down with at the time: the REST API in its tunnel, or the JSON
    /// messages of OpenADR.
    /// </summary>
    /// <param name="Controller">The local controller.</param>
    /// <param name="URL">The URL of its connection to the grid operator.</param>
    /// <param name="REST">The client of the REST binding.</param>
    /// <param name="Messages">The client of the binding of messages.</param>
    public sealed class ThroughAConnection(LocalController   Controller,
                                           String            URL,
                                           IVTNClient        REST,
                                           MessageVTNClient  Messages) : IVTNClient
    {

        /// <summary>
        /// The client of the binding of the connection of the URL now.
        /// </summary>
        public IVTNClient       Now
            => Controller.GridOperatorConnectionOf(URL)?.OpenADRBinding == OpenADRBinding.Messages
                   ? Messages
                   : REST;

        /// <summary>
        /// Whether a subscription of the events is held over the connection
        /// asked over last - in the binding of messages only; the REST binding
        /// subscribes to nothing.
        /// </summary>
        public Boolean          Subscribed
            => Messages.Subscribed;

        public Uri              VTNURL                   { get; } = new (URL);
        public String           ClientID                 => REST.ClientID;
        public Uri?             TokenURL                 => null;
        public DateTimeOffset?  TokenExpires             => null;
        public Boolean          SignedInByTheConnection  => true;

        public Task<IReadOnlyList<JObject>> GetEvents(CancellationToken CancellationToken = default)
            => Now.GetEvents(CancellationToken);

        public Task<IReadOnlyList<JObject>> GetReports(CancellationToken CancellationToken = default)
            => Now.GetReports(CancellationToken);

        public Task<JObject> PostReport(Report Report, CancellationToken CancellationToken = default)
            => Now.PostReport(Report, CancellationToken);

        public void SignOut()
            => REST.SignOut();

        public void Dispose()
        {
            REST.    Dispose();
            Messages.Dispose();
        }

    }

}
