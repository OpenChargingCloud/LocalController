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
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// The key pins over the JSON API, as the web interface uses it: the
    /// server key made and shown, a next key announced and switched to, and
    /// key pins given to charging stations and taken away again.
    /// </summary>
    public class KeyPinAPITests : ALocalControllerTests
    {

        #region TheAPI_MakesTheServerKey_AndShowsItsPins()

        [Test]
        public async Task TheAPI_MakesTheServerKey_AndShowsItsPins()
        {

            using var http = await SignedIn();

            var root      = "/api/v1/configuration/ocpp-server";

            var made      = await http.PostAsync($"{root}/serverKeys", JSONBody());
            var madeJSON  = JObject.Parse(await made.Content.ReadAsStringAsync());

            Assert.That(made.StatusCode, Is.EqualTo(HttpStatusCode.Created), madeJSON.ToString());

            var second    = await http.PostAsync($"{root}/serverKeys", JSONBody());
            var next      = await http.PostAsync($"{root}/serverKeys", JSONBody(new JProperty("next", true)));
            var nextJSON  = JObject.Parse(await next.Content.ReadAsStringAsync());

            var before    = await GetJSON(http, $"{root}/certificates");

            var switched  = await http.PostAsync($"{root}/serverKeys/switch", JSONBody());
            var after     = JObject.Parse(await switched.Content.ReadAsStringAsync());

            Assert.Multiple(() => {

                Assert.That(madeJSON.Value<String>("keyPin"),                               Does.Match("^[0-9a-f]{64}$"));
                Assert.That(Controller.ServerCertificates.Entries.Single(entry => entry.Id == madeJSON.Value<String>("id")).KeyPin.ToHex(),
                                                                                            Is.EqualTo(madeJSON.Value<String>("keyPin")));

                Assert.That(second.StatusCode,                                              Is.EqualTo(HttpStatusCode.BadRequest), "a second server key took the place of the first");
                Assert.That(next.StatusCode,                                                Is.EqualTo(HttpStatusCode.Created));

                Assert.That(before["keyPins"]?["current"]?.Value<String>("keyPin"),         Is.EqualTo(madeJSON.Value<String>("keyPin")));
                Assert.That(before["keyPins"]?["next"]?.   Value<String>("keyPin"),         Is.EqualTo(nextJSON.Value<String>("keyPin")));

                Assert.That(switched.StatusCode,                                            Is.EqualTo(HttpStatusCode.OK));
                Assert.That(after["keyPins"]?["current"]?.Value<String>("keyPin"),          Is.EqualTo(nextJSON.Value<String>("keyPin")));
                Assert.That(after["keyPins"]?["next"]?.Type,                                Is.EqualTo(JTokenType.Null));

            });

        }

        #endregion

        #region TheAPI_GivesAndTakesKeyPins()

        [Test]
        public async Task TheAPI_GivesAndTakesKeyPins()
        {

            using var http = await SignedIn();
            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var pin      = KeyPin.FromKey(key);
            var root     = "/api/v1/configuration/ocpp-server/stations";

            var given    = await http.PostAsync($"{root}/cs001/keyPins", JSONBody(new JProperty("pin",    pin.ToHexWithColons()),
                                                                                   new JProperty("label",  "bay 1")));

            Assert.That(given.StatusCode, Is.EqualTo(HttpStatusCode.OK), await given.Content.ReadAsStringAsync());

            var stations = await GetJSON(http, root);
            var cs001    = stations["stations"]!.OfType<JObject>().Single(station => station.Value<String>("id") == "cs001");

            var shared   = await http.PostAsync($"{root}/cs002/keyPins", JSONBody(new JProperty("pin", pin.ToHex())));
            var bad      = await http.PostAsync($"{root}/cs002/keyPins", JSONBody(new JProperty("pin", "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU")));

            Assert.Multiple(() => {
                Assert.That(cs001.Value<Boolean>("hasKeyPin"),              Is.True);
                Assert.That(cs001["keyPins"]?[0]?.Value<String>("pin"),     Is.EqualTo(pin.ToHex()));
                Assert.That(cs001["keyPins"]?[0]?.Value<String>("label"),   Is.EqualTo("bay 1"));
                Assert.That(cs001["keyPins"]?[0]?.Value<String>("addedBy"), Is.Not.Null);
                Assert.That(shared.StatusCode,                              Is.EqualTo(HttpStatusCode.BadRequest), "one pin was given to two stations");
                Assert.That(bad.StatusCode,                                 Is.EqualTo(HttpStatusCode.BadRequest), "an SSH fingerprint was taken for a key pin");
            });

            var taken    = await http.DeleteAsync($"{root}/cs001/keyPins/{pin.ToHex()}");

            Assert.That(taken.StatusCode, Is.EqualTo(HttpStatusCode.OK), await taken.Content.ReadAsStringAsync());
            Assert.That(Controller.StationLogins.TryGet("cs001", out var login) && !login.HasKeyPin, Is.True);

            var again    = await http.DeleteAsync($"{root}/cs001/keyPins/{pin.ToHex()}");

            Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        }

        #endregion

    }

}
