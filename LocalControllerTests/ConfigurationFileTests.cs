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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.LocalController.Configuration;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The file a local controller writes itself down in.
    /// </summary>
    public class ConfigurationFileTests
    {

        #region Data

        private String                directory   = default!;
        private ControllerConfigFile  file        = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAFile()
        {
            directory = TestControllers.TemporaryDirectory("config");
            Directory.CreateDirectory(directory);
            file      = new ControllerConfigFile(Path.Combine(directory, "configuration.json"));
        }

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region NoFileIsAControllerNobodyHasConfigured()

        /// <summary>
        /// Not a failure: an empty document, and no error.
        /// </summary>
        [Test]
        public void NoFileIsAControllerNobodyHasConfigured()
        {

            Assert.Multiple(() => {

                Assert.That(file.Exists, Is.False);

                Assert.That(file.TryLoadDocument(out var document, out var error), Is.True, error);
                Assert.That(document,                                              Is.Empty);

                Assert.That(file.TryLoad(out var configuration, out var problem),  Is.True, problem);
                Assert.That(configuration!.IsEmpty,                                Is.True);

            });

        }

        #endregion

        #region AnEmptyFileIsTheSameAsNoFile()

        [Test]
        public void AnEmptyFileIsTheSameAsNoFile()
        {

            File.WriteAllText(file.Path, "   \n  ");

            Assert.That(file.TryLoadDocument(out var document, out var error), Is.True, error);
            Assert.That(document, Is.Empty);

        }

        #endregion

        #region AFileThatIsNotJSONIsRefused()

        /// <summary>
        /// Somebody wrote down what their controller is and got it wrong.
        /// Running as something else instead would be worse than stopping.
        /// </summary>
        [Test]
        public void AFileThatIsNotJSONIsRefused()
        {

            File.WriteAllText(file.Path, "{ dns: [ unquoted");

            Assert.Multiple(() => {
                Assert.That(file.TryLoadDocument(out _, out var error), Is.False);
                Assert.That(error,                                      Does.Contain(file.Path));
            });

        }

        #endregion

        #region ASectionOfTheWrongKindIsRefused()

        /// <summary>
        /// "dns": null is a file with nothing to say about DNS. "dns": "google"
        /// is a file whose author believed they had configured something.
        /// </summary>
        [Test]
        public void ASectionOfTheWrongKindIsRefused()
        {

            File.WriteAllText(file.Path, new JObject(new JProperty("dns", "google")).ToString());

            Assert.Multiple(() => {
                Assert.That(file.TryLoad(out _, out var error), Is.False);
                Assert.That(error,                              Does.Contain("dns"));
            });

        }

        #endregion

        #region AnExplicitNullSectionSaysNothingRatherThanNothingAtAll()

        [Test]
        public void AnExplicitNullSectionSaysNothingRatherThanNothingAtAll()
        {

            File.WriteAllText(file.Path, new JObject(new JProperty("dns", JValue.CreateNull())).ToString());

            Assert.Multiple(() => {
                Assert.That(file.TryLoad(out var configuration, out var error), Is.True, error);
                Assert.That(configuration!.DNS,                                 Is.Null);
                Assert.That(configuration.IsEmpty,                              Is.True);
            });

        }

        #endregion


        #region WritingOneSectionLeavesTheOthersAlone()

        [Test]
        public void WritingOneSectionLeavesTheOthersAlone()
        {

            file.TryWrite(
                new JObject(
                    new JProperty("dns", new JObject(new JProperty("enabled", true))),
                    new JProperty("nts", new JObject(new JProperty("hostname", "ptbtime1.ptb.de")))
                ),
                out _
            );

            file.TryReplaceSection("dns", new JObject(new JProperty("enabled", false)), out var error);

            file.TryLoadDocument(out var document, out _);

            Assert.Multiple(() => {
                Assert.That(error,                                          Is.Null);
                Assert.That(document!["dns"]?.Value<Boolean>("enabled"),    Is.False);
                Assert.That(document["nts"]?.Value<String>("hostname"),     Is.EqualTo("ptbtime1.ptb.de"),
                            "Writing the DNS section took the NTS section with it.");
            });

        }

        #endregion

        #region MergingASectionKeepsTheFieldsItDoesNotMention()

        /// <summary>
        /// What a page does not offer, it must not be able to delete. A form
        /// with six checkboxes sends six checkboxes, and a save that replaced
        /// the whole section would take the name servers with it.
        /// </summary>
        [Test]
        public void MergingASectionKeepsTheFieldsItDoesNotMention()
        {

            file.TryWrite(
                new JObject(
                    new JProperty("dns", new JObject(
                        new JProperty("enabled",  true),
                        new JProperty("servers",  new JArray("9.9.9.9"))
                    ))
                ),
                out _
            );

            file.TryMergeSection("dns", new JObject(new JProperty("useCache", false)), out var error);

            file.TryLoadDocument(out var document, out _);

            Assert.Multiple(() => {
                Assert.That(error,                                                Is.Null);
                Assert.That(document!["dns"]?.Value<Boolean>("useCache"),         Is.False);
                Assert.That(document["dns"]?.Value<Boolean>("enabled"),           Is.True);
                Assert.That((document["dns"]?["servers"] as JArray)?.Count,       Is.EqualTo(1),
                            "A merge that said nothing about the name servers took them away.");
            });

        }

        #endregion

        #region MergingReplacesAListRatherThanGrowingIt()

        /// <summary>
        /// A list of name servers is one value. Merging the old into the new by
        /// position would produce a list nobody wrote.
        /// </summary>
        [Test]
        public void MergingReplacesAListRatherThanGrowingIt()
        {

            file.TryWrite(
                new JObject(new JProperty("dns", new JObject(
                    new JProperty("servers", new JArray("1.1.1.1", "8.8.8.8"))
                ))),
                out _
            );

            file.TryMergeSection("dns", new JObject(new JProperty("servers", new JArray("9.9.9.9"))), out _);

            file.TryLoadDocument(out var document, out _);

            var servers = (document!["dns"]?["servers"] as JArray)?.Select(server => server.Value<String>()).ToArray();

            Assert.That(servers, Is.EqualTo(new[] { "9.9.9.9" }));

        }

        #endregion

        #region ASectionThisControllerDoesNotKnowIsKept()

        /// <summary>
        /// A file written by a newer controller should still start an older
        /// one, and saving from the older one must not quietly delete the
        /// configuration of something it has not heard of yet.
        /// </summary>
        [Test]
        public void ASectionThisControllerDoesNotKnowIsKept()
        {

            file.TryWrite(
                new JObject(
                    new JProperty("dns",       new JObject(new JProperty("enabled", true))),
                    new JProperty("fromTheFuture", new JObject(new JProperty("something", 42)))
                ),
                out _
            );

            // Read as a configuration: the unknown section is passed over
            // without a word.
            Assert.That(file.TryLoad(out var configuration, out var error), Is.True, error);
            Assert.That(configuration!.DNS, Is.Not.Null);

            // Written back: it is still there.
            file.TryMergeSection("dns", new JObject(new JProperty("useCache", false)), out _);

            file.TryLoadDocument(out var document, out _);

            Assert.That(document!["fromTheFuture"]?.Value<Int32>("something"), Is.EqualTo(42),
                        "A section this controller does not know was deleted by saving one it does.");

        }

        #endregion

        #region MergingIntoAFileThatHasNoSuchSectionCreatesIt()

        [Test]
        public void MergingIntoAFileThatHasNoSuchSectionCreatesIt()
        {

            file.TryMergeSection("nts", new JObject(new JProperty("enabled", false)), out var error);

            file.TryLoadDocument(out var document, out _);

            Assert.Multiple(() => {
                Assert.That(error,                                      Is.Null);
                Assert.That(document!["nts"]?.Value<Boolean>("enabled"), Is.False);
            });

        }

        #endregion

        #region NothingIsLeftBehindBesideTheFile()

        /// <summary>
        /// The write goes through a temporary file and is moved into place, so
        /// that a process which dies mid-write leaves the old configuration
        /// rather than half of the new one. What it must not leave is the
        /// temporary file.
        /// </summary>
        [Test]
        public void NothingIsLeftBehindBesideTheFile()
        {

            file.TryWrite(new JObject(new JProperty("dns", new JObject())), out _);

            Assert.Multiple(() => {
                Assert.That(File.Exists(file.Path),           Is.True);
                Assert.That(File.Exists(file.Path + ".tmp"),  Is.False,
                            "The temporary file used for the atomic write was left behind.");
            });

        }

        #endregion

        #region WhatIsWrittenComesBackAsWhatWasMeant()

        [Test]
        public void WhatIsWrittenComesBackAsWhatWasMeant()
        {

            var written = new ControllerConfiguration(
                              DNS:   new DNSConfiguration(Enabled: false),
                              NTS:   new NTSConfiguration(Enabled: true),
                              OCPP:  new OCPPConfiguration(NodeId: "lc042", VendorName: "ACME")
                          );

            file.TryWrite(written.ToJSON(), out _);

            Assert.That(file.TryLoad(out var read, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(read!.DNS?.Enabled,        Is.False);
                Assert.That(read.NTS?.Enabled,         Is.True);
                Assert.That(read.OCPP?.NodeId,         Is.EqualTo("lc042"));
                Assert.That(read.OCPP?.VendorName,     Is.EqualTo("ACME"));
                Assert.That(read.IsEmpty,              Is.False);
            });

        }

        #endregion

    }

}
