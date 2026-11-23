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

using System.Collections.Concurrent;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OpenADRv3;
using cloud.charging.open.protocols.OpenADRv3.Messaging;
using cloud.charging.open.protocols.WWCP.Node.OpenADR;
using cloud.charging.open.protocols.WWCP.Node.Streams;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Connections;

using cloud.charging.open.LocalController.OpenADR;
using cloud.charging.open.LocalController.Configuration;

using OpenADRValueType = cloud.charging.open.protocols.OpenADRv3.ValueType;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The local controller as a VEN in the binding of the JSON messages of
    /// OpenADR, over its connection to the grid operator: it reads its limits
    /// and reports on them over the messages, subscribes to the events once
    /// over the connection, and asks at once when it is told of a change -
    /// against a VTN of messages that answers as the grid operator's does.
    /// </summary>
    public class OpenADRMessagesTests
    {

        #region (private sealed class) AVTNOfMessages

        /// <summary>
        /// A VTN of the JSON messages of OpenADR on a WebSocket port of its own,
        /// letting in whoever comes: it answers what a local controller asks,
        /// and tells every connection of a change where asked.
        /// </summary>
        private sealed class AVTNOfMessages : IAsyncDisposable
        {

            private readonly StreamWebSocketServer                                               server;
            private readonly ConcurrentDictionary<WebSocketServerConnection, OpenADRMessageEndpoint> ends   = new ();
            private readonly Lock                                                                stateLock = new ();
            private readonly List<JObject>                                                       events        = [];
            private readonly List<JObject>                                                       reports       = [];
            private readonly List<JObject>                                                       subscriptions = [];

            public IPPort                   Port            { get; }
            public String                   URL             => $"ws://127.0.0.1:{Port.ToUInt16()}/lc-1";
            public ConcurrentQueue<String>  Asked           { get; } = new ();
            public Int32?                   FailEventsWith  { get; set; }

            public IReadOnlyList<JObject>   Reports         { get { lock (stateLock) return [ .. reports ];       } }
            public IReadOnlyList<JObject>   Subscriptions   { get { lock (stateLock) return [ .. subscriptions ]; } }
            public Int32                    Connected       => ends.Count;

            private AVTNOfMessages(IPPort Port)
            {

                this.Port = Port;

                server = new StreamWebSocketServer(IPv4Address.Localhost,
                                                   Port,
                                                   [],
                                                   "A VTN of messages",
                                                   I18NString.Create("A VTN of messages"),
                                                   null,
                                                   null,
                                                   null,
                                                   null,
                                                   MessageSubprotocols: [ OpenADRWireNames.Subprotocol ]) {
                             AcceptHTTP3 = false
                         };

                server.OnMessageConnection       += connection => ends[connection] = new OpenADRMessageEndpoint(
                                                                                         "the VTN",
                                                                                         async (text, cancellationToken) => await server.SendTextMessage(connection, text, CancellationToken: cancellationToken) == SentStatus.Success,
                                                                                         Answer);
                server.OnTextMessageOf            = (connection, text) => ends.TryGetValue(connection, out var end) ? end.ReceiveAsync(text) : Task.CompletedTask;
                server.OnMessageConnectionEnded  += connection => { if (ends.TryRemove(connection, out var end)) end.End("gone"); };

            }

            public static async Task<AVTNOfMessages> Started()
            {
                var vtn = new AVTNOfMessages(TestPorts.Free());
                await vtn.server.Start();
                return vtn;
            }

            /// <summary>
            /// A limit for the local controller, as the grid operator sends one.
            /// </summary>
            public JObject Send(Double KW)
            {

                var start = DateTimeOffset.UtcNow.AddMinutes(-1);

                var json  = new Event(Object_Id.Parse("grid-limits"),
                                      [ new Interval(Interval_Id.Parse(0),
                                                     [ new ValuesMap(OpenADRValueType.Parse(GridLimit.ImportCapacityLimit), [ KW ]) ]) ],
                                      Targets:             [ "ven:lc-paradies" ],
                                      PayloadDescriptors:  [ new EventPayloadDescriptor(PayloadType.Parse(GridLimit.ImportCapacityLimit), UnitType.Parse("KW")) ],
                                      IntervalPeriod:      new IntervalPeriod(start, TimeSpan.FromHours(1)),
                                      Id:                  Event_Id.NewRandom,
                                      Created:             start,
                                      LastModification:    start).ToJSON();

                json["objectType"] = "EVENT";

                lock (stateLock)
                    events.Add(json);

                return json;

            }

            public void Withdraw(JObject Event)
            {
                lock (stateLock)
                    events.RemoveAll(json => json.Value<String>("id") == Event.Value<String>("id"));
            }

            /// <summary>
            /// Tell every connection of what was done to the given event.
            /// </summary>
            public async Task<IReadOnlyList<OpenADRCallOutcome>> Notify(String Operation, JObject Event)

                => await Task.WhenAll(ends.Values.Select(end => end.AskAsync(OpenADRActions.Notify,
                                                                             new JObject(new JProperty("objectType",  "EVENT"),
                                                                                         new JProperty("operation",   Operation),
                                                                                         new JProperty("object",      Event)),
                                                                             TimeSpan.FromSeconds(5))));

            private Task<OpenADRCallOutcome> Answer(String Action, JObject Payload, CancellationToken CancellationToken)
            {

                Asked.Enqueue(Action);

                lock (stateLock)
                {
                    switch (Action)
                    {

                        case OpenADRActions.GetEvents:
                            if (FailEventsWith is Int32 status)
                                return Task.FromResult(OpenADRCallOutcome.Fail(OpenADRActions.ErrorCodeOf(status), "Refused.", new JObject(new JProperty("status", status))));
                            return Task.FromResult(OpenADRCallOutcome.Ok(new JObject(new JProperty("events", new JArray(events)))));

                        case OpenADRActions.GetReports:
                            return Task.FromResult(OpenADRCallOutcome.Ok(new JObject(new JProperty("reports", new JArray(reports)))));

                        case OpenADRActions.CreateReport:
                            var report = (JObject) Payload["report"]!.DeepClone();
                            report["id"]       = Guid.NewGuid().ToString();
                            report["clientID"] = "lc-paradies";
                            reports.Add(report);
                            return Task.FromResult(OpenADRCallOutcome.Ok(report));

                        case OpenADRActions.GetSubscriptions:
                            return Task.FromResult(OpenADRCallOutcome.Ok(new JObject(new JProperty("subscriptions",
                                                                                     new JArray(subscriptions.Where(subscription => subscription.Value<String>("clientName") == Payload.Value<String>("clientName")))))));

                        case OpenADRActions.CreateSubscription:
                            var subscription = (JObject) Payload["subscription"]!.DeepClone();
                            subscription["id"] = Guid.NewGuid().ToString();
                            subscriptions.Add(subscription);
                            return Task.FromResult(OpenADRCallOutcome.Ok(subscription));

                        default:
                            return Task.FromResult(OpenADRCallOutcome.Fail(OpenADRMessageErrors.NotImplemented, $"Not '{Action}'."));

                    }
                }

            }

            public async ValueTask DisposeAsync()
            {
                foreach (var end in ends.Values)
                    end.End("stopped");
                await server.Shutdown("Done.");
            }

        }

        #endregion

        #region Data

        private String            directory   = default!;
        private AVTNOfMessages    vtn         = default!;
        private LocalController?  controller;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartAVTN()
        {
            directory  = TestControllers.TemporaryDirectory("openadr-messages");
            vtn        = await AVTNOfMessages.Started();
        }

        [TearDown]
        public async Task StopThem()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            controller = null;

            await vtn.DisposeAsync();

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private) AController(URL = null)

        /// <summary>
        /// A local controller connected to the VTN in the binding of messages,
        /// which asks it through that connection every hour - so that what it
        /// reads before then, it was told of.
        /// </summary>
        private async Task<LocalController> AController(String? URL = null)
        {

            Directory.CreateDirectory(directory);

            var url            = URL ?? vtn.URL;
            var configuration  = TestControllers.Offline;

            configuration.Add(OpenADRConfiguration.SectionName, new JObject(
                new JProperty("enabled",         true),
                new JProperty("vtnURL",          url),
                new JProperty("pollEvery",       3600),
                new JProperty("requestTimeout",  3)
            ));

            controller = await TestPorts.StartedOnFreshPorts(() => TestControllers.New(directory, configuration));

            Assert.That(controller.Connections.TryAddConnection(I18NString.Create("The grid operator"),
                                                                org.GraphDefined.Vanaheimr.Hermod.HTTP.URL.Parse(url),
                                                                "GridOperator",
                                                                true,
                                                                null,
                                                                null,
                                                                out _,
                                                                out var error,
                                                                OpenADRBinding: "messages"),
                        Is.True, error);

            return controller;

        }

        #endregion

        #region (private static) Until(Condition, What, Seconds = 10)

        private static async Task Until(Func<Boolean> Condition, String What, Double Seconds = 10)
        {

            var until = DateTimeOffset.UtcNow.AddSeconds(Seconds);

            while (DateTimeOffset.UtcNow < until)
            {
                if (Condition())
                    return;
                await Task.Delay(50);
            }

            Assert.That(Condition(), Is.True, $"Waited in vain for {What}.");

        }

        #endregion


        #region ReadsAndReportsOverTheMessages_AndSubscribesOnce()

        /// <summary>
        /// Once its connection is up the controller asks at once: it subscribes
        /// to the events with the URL of the connection as the callback, reads
        /// its limit and reports on it - and asked again, subscribes not again
        /// and reports not twice.
        /// </summary>
        [Test]
        public async Task ReadsAndReportsOverTheMessages_AndSubscribesOnce()
        {

            vtn.Send(44);

            var lc = await AController();

            await Until(() => lc.ImportLimitNow?.KW == 44 && vtn.Reports.Count == 1, "the limit read and reported on");

            Assert.That(await lc.PollOpenADR(), Is.Null);

            var subscriptions = vtn.Subscriptions;

            Assert.Multiple(() => {
                Assert.That(subscriptions,                                                                   Has.Count.EqualTo(1), "The controller subscribed more than once.");
                Assert.That(subscriptions[0]["objectOperations"]?[0]?["callbackUrl"]?.Value<String>(),       Is.EqualTo(vtn.URL));
                Assert.That(subscriptions[0]["objectOperations"]?[0]?["objects"]?.Values<String>(),          Is.EqualTo(new[] { "EVENT" }));
                Assert.That(vtn.Reports,                                                                     Has.Count.EqualTo(1), "The controller reported twice.");
                Assert.That(lc.OpenADRSignedIn,                                                              Is.True);
                Assert.That(lc.OpenADRToken.TokenURL,                                                        Is.Null, "A token was asked for, which the connection makes needless.");
                Assert.That(vtn.Asked,                                                                       Does.Not.Contain("Notify"));
            });

        }

        #endregion

        #region ANotify_IsAskedAtOnce()

        /// <summary>
        /// Told of a new limit, the controller asks at once and holds to it -
        /// an hour before its next poll; told of it withdrawn, it lets go of it.
        /// </summary>
        [Test]
        public async Task ANotify_IsAskedAtOnce()
        {

            var lc = await AController();

            await Until(() => vtn.Subscriptions.Count == 1 && lc.OpenADRLastPoll.Read is not null, "the controller subscribing and reading");

            var limit  = vtn.Send(22);
            var told   = await vtn.Notify("CREATE", limit);

            await Until(() => lc.ImportLimitNow?.KW == 22, "the limit told of to be read");

            vtn.Withdraw(limit);
            await vtn.Notify("DELETE", limit);

            await Until(() => lc.ImportLimitNow is null, "the limit withdrawn to be let go of");

            Assert.Multiple(() => {
                Assert.That(told.Single().Succeeded,              Is.True, "The controller did not answer Notify.");
                Assert.That(lc.OpenADRNotificationsReceived,      Is.EqualTo(2));
            });

        }

        #endregion

        #region AnErrorOfTheVTN_IsSaid_WithWhatItSaid()

        /// <summary>
        /// The VTN answering the events with an error is what the poll says,
        /// with the code it answered with.
        /// </summary>
        [Test]
        public async Task AnErrorOfTheVTN_IsSaid_WithWhatItSaid()
        {

            var lc = await AController();

            await Until(() => vtn.Connected == 1, "the controller connecting");

            vtn.FailEventsWith = 404;

            var said = await lc.PollOpenADR();

            Assert.That(said, Does.Contain(OpenADRMessageErrors.NotFound));

        }

        #endregion

        #region AConnectionThatIsNotUp_IsSaid()

        /// <summary>
        /// A VTN to be asked over a connection that is not up - nothing listens
        /// at its port - is not asked, and the poll says so.
        /// </summary>
        [Test]
        public async Task AConnectionThatIsNotUp_IsSaid()
        {

            var lc   = await AController($"ws://127.0.0.1:{TestPorts.Free().ToUInt16()}/lc-1");

            var said = await lc.PollOpenADR();

            Assert.Multiple(() => {
                Assert.That(said,               Does.Contain("is not up"));
                Assert.That(lc.GridLimits,      Is.Empty);
            });

        }

        #endregion

    }

}
