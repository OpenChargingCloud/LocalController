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
    /// What the csms section says about the TLS identity security profile 3
    /// signs in with: a handle of the certificate store, none said out loud,
    /// and nothing said at all - three different things to a save that merges
    /// what it is sent into what was there.
    /// </summary>
    public class CSMSConfigurationTests
    {

        #region (private static) Read(JSON)

        private static CSMSConfiguration Read(String JSON)
        {

            Assert.That(CSMSConfiguration.TryParse(JObject.Parse(JSON), out var configuration, out var error), Is.True, error);

            return configuration!;

        }

        #endregion


        #region AClientCertificateIsAHandleOfTheStore()

        [Test]
        public void AClientCertificateIsAHandleOfTheStore()
        {

            var configuration = Read("""{ "clientCertificate": " 0E016B2245B3797C " }""");

            Assert.Multiple(() => {
                Assert.That(configuration.ClientCertificate,        Is.EqualTo("0e016b2245b3797c"), "the store's handles are lower case");
                Assert.That(configuration.ChosenClientCertificate,  Is.EqualTo("0e016b2245b3797c"));
                Assert.That(configuration.ToJSON()["clientCertificate"]!.Value<String>(), Is.EqualTo("0e016b2245b3797c"));
            });

        }

        #endregion

        #region WhatIsNotAHandleIsRefused()

        [TestCase("abc",                   Description = "too short to be one")]
        [TestCase("zzzzzzzzzzzzzzzz",      Description = "not hexadecimal")]
        [TestCase("Towards the CSMS",      Description = "a label, not a handle")]
        public void WhatIsNotAHandleIsRefused(String Written)
        {

            var read = CSMSConfiguration.TryParse(new JObject(new JProperty("clientCertificate", Written)), out _, out var error);

            Assert.Multiple(() => {
                Assert.That(read,   Is.False);
                Assert.That(error,  Does.Contain("'csms.clientCertificate'").And.Contain("the handle of a TLS identity"));
            });

        }

        #endregion

        #region NoneIsSaidAsNullOrEmptyAndWritesOverTheChosenOne()

        [TestCase("""{ "clientCertificate": null }""")]
        [TestCase("""{ "clientCertificate": "" }""")]
        public void NoneIsSaidAsNullOrEmptyAndWritesOverTheChosenOne(String JSON)
        {

            var chosen   = Read("""{ "clientCertificate": "0e016b2245b3797c" }""");
            var none     = Read(JSON);
            var merged   = chosen.Merge(none);

            Assert.Multiple(() => {
                Assert.That(none.ClientCertificate,          Is.EqualTo(""),  "none, said out loud");
                Assert.That(merged.ChosenClientCertificate,  Is.Null,         "none written over the one that was chosen");
                Assert.That(merged.ToJSON()["clientCertificate"]!.Value<String>(), Is.EqualTo(""),
                            "a file the section is merged into needs something to write over the old handle with");
            });

        }

        #endregion

        #region SayingNothingLeavesTheChosenOneAlone()

        [Test]
        public void SayingNothingLeavesTheChosenOneAlone()
        {

            var chosen   = Read("""{ "clientCertificate": "0e016b2245b3797c" }""");
            var merged   = chosen.Merge(Read("""{ "pingEvery": 60 }"""));

            Assert.That(merged.ChosenClientCertificate, Is.EqualTo("0e016b2245b3797c"));

        }

        #endregion

        #region AnotherIdentityWaitsForTheLineToBeDialledAgain()

        [Test]
        public void AnotherIdentityWaitsForTheLineToBeDialledAgain()
        {

            var dialled  = Read("""{ "clientCertificate": "0e016b2245b3797c" }""").Effective();
            var now      = dialled.Merge(Read("""{ "clientCertificate": "9bcb4a2c7c25f614" }"""));

            Assert.That(now.NeedsARestartFor(dialled), Does.Contain("clientCertificate"),
                        "a client dialled with one identity presents it until it is dialled again");

        }

        #endregion

    }

}
