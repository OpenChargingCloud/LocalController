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

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.LocalController.OCPP;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The two stores of the charging station server - the keys its port
    /// presents, and the chains it accepts from charging stations - kept from
    /// every account but the one the controller runs under, as the node keeps
    /// its accounts, its logs and its own certificates.
    /// </summary>
    /// <remarks>
    /// A key read by somebody else is somebody else's server; a chain added by
    /// somebody else lets their charging stations in. On Windows a controller
    /// kept below a folder at the root of a drive inherits read access for
    /// every authenticated user - which is what OpenToEverybody() makes here.
    /// </remarks>
    public class OwnerOnlyStoreTests
    {

        #region Data

        private String directory = default!;

        private static readonly String[] Stores = [ ServerCertificateStore.DefaultDirectoryName,
                                                    ClientTrustStore.DefaultDirectoryName ];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("owner");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
            => TestControllers.Remove(directory);

        #endregion


        #region WhatAnOlderControllerLeftOpenIsTakenBack()

        /// <summary>
        /// Both directories there already, open to everybody, with a file
        /// written then: the controller made on them takes both back - the
        /// file on Windows by what it inherits.
        /// </summary>
        [Test]
        public async Task WhatAnOlderControllerLeftOpenIsTakenBack()
        {

            foreach (var store in Stores)
            {
                OwnerOnlyChecks.OpenToEverybody(Path.Combine(directory, store));
                File.WriteAllText(Path.Combine(directory, store, "left-behind.txt"), "an older controller's");
                Assume.That(OwnerOnlyChecks.OthersMayGetIn(Path.Combine(directory, store)), Is.Not.Empty);
            }

            await using var controller = TestControllers.New(directory, TestControllers.Offline);

            Assert.Multiple(() => {
                foreach (var store in Stores)
                {

                    Assert.That(OwnerOnlyChecks.OthersMayGetIn(Path.Combine(directory, store)), Is.Empty,
                                $"others may still get into {store}/");

                    if (OperatingSystem.IsWindows())
                        Assert.That(OwnerOnlyChecks.OthersMayGetIn(Path.Combine(directory, store, "left-behind.txt")), Is.Empty,
                                    $"others may still read what is in {store}/");

                }
            });

        }

        #endregion

        #region TheStoresAreMadeForTheirOwnerAlone()

        /// <summary>
        /// Below a directory open to everybody, the stores made by their first
        /// key and their first chain are their owner's alone, and so is the key.
        /// </summary>
        [Test]
        public async Task TheStoresAreMadeForTheirOwnerAlone()
        {

            OwnerOnlyChecks.OpenToEverybody(directory);

            await using var controller = TestControllers.New(directory, TestControllers.Offline);

            foreach (var store in Stores)
                Assume.That(Directory.Exists(Path.Combine(directory, store)), Is.False, $"{store}/ is there before anything is put in");

            using var ca = TestCA.Create("Some Charging Network");

            Assert.That(controller.ServerCertificates.TryCreateKey("lc001.example.org", [ "lc001.example.org" ], null, out var key, out _, out var keyError),
                        Is.True, keyError);

            Assert.That(controller.ClientTrust.TryAdd(ca.ChainPEM(ca.Certificate), "network", out _, out _, out var chainError),
                        Is.True, chainError);

            Assert.Multiple(() => {

                foreach (var store in Stores)
                    Assert.That(OwnerOnlyChecks.OthersMayGetIn(Path.Combine(directory, store)), Is.Empty,
                                $"others may get into {store}/");

                var keyFile = Path.Combine(directory, ServerCertificateStore.DefaultDirectoryName, $"{key}.key.pem");

                Assert.That(File.Exists(keyFile),                      Is.True,  "the key is not where it is looked for");
                Assert.That(OwnerOnlyChecks.OthersMayGetIn(keyFile),   Is.Empty, "others may read the key");

            });

        }

        #endregion

    }

}
