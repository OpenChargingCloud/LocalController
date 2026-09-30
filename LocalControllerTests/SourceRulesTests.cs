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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// What the kit holds every kind's C# source to, asked of this local
    /// controller's.
    /// </summary>
    public class SourceRulesTests
    {

        #region NoTextOfThisLocalControllerPutsAnArticleBeforeAName()

        /// <summary>
        /// Nothing this local controller says puts an article in front of a
        /// name it interpolates. It said "A {algorithm.Name} key could not be
        /// generated", which is wrong for every key it makes - "A ECDSA P-256",
        /// "A RSA 2048" - and refused a certificate chosen for the CSMS that is
        /// no TLS identity as "but a TLS root - what a server this node
        /// connects to may chain to: a time server, a backend".
        /// </summary>
        [Test]
        public void NoTextOfThisLocalControllerPutsAnArticleBeforeAName()
        {

            var repository = SourceRules.RepositoryAbove(AppContext.BaseDirectory, "LocalController/LocalController.csproj",
                                                                                   "LocalControllerTests/LocalControllerTests.csproj");

            Assert.That(SourceRules.ArticlesBeforeANameIn(Path.Combine(repository, "LocalController")), Is.Empty);

        }

        #endregion

    }

}
