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

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Once every test has run: take every test authority of this run out of
    /// the Windows certificate stores, whether its test disposed of it or not.
    /// </summary>
    /// <remarks>
    /// Disposing a <see cref="TestCA"/> already does this for its own
    /// certificates. This is for the ones whose fixture never got to its
    /// teardown, and for a TLS context built again after a test had disposed
    /// of its authority - see <see cref="TestCA"/> for why .NET puts them
    /// there at all.
    /// </remarks>
    [SetUpFixture]
    public sealed class TestCAStoreSweep
    {

        [OneTimeTearDown]
        public void RemoveEveryTestAuthority()

            => TestCA.RemoveAllFromWindowsStore();

    }

}
