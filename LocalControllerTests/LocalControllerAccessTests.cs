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
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.LocalController.Tests
{

    /// <summary>
    /// Who may do what on a local controller: its two resources beside the
    /// node's, its CPO beside the node's viewer and administrators - and a
    /// role from the configuration file, heard by the API like every other.
    /// </summary>
    public class LocalControllerAccessTests
    {

        #region Data

        private String            directory   = "";
        private LocalController?  controller;
        private Uri?              address;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestControllers.TemporaryDirectory("access");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (controller is not null)
                await controller.DisposeAsync();

            controller = null;

            TestControllers.Remove(directory);

        }

        #endregion


        #region (private) AController(Configuration = null)

        /// <summary>
        /// A local controller with the given configuration file, off the
        /// network: made, and not yet started.
        /// </summary>
        private LocalController AController(JObject? Configuration = null)
        {

            controller  = TestControllers.New(directory, Configuration ?? TestControllers.Offline);
            address     = new Uri(controller.WebInterfaceURL.ToString());

            return controller;

        }

        #endregion

        #region (private) SignedInAs(Name, Role)

        /// <summary>
        /// A browser signed in with a password as an account of the given name,
        /// made for the purpose and put in the group of the given role, or in
        /// none - made the way the controller makes its first one, so that it
        /// may sign in.
        /// </summary>
        private async Task<HttpClient> SignedInAs(String   Name,
                                                  String?  Role)
        {

            var password = "correct-horse-battery-" + Guid.NewGuid().ToString("N")[..8];

            Assert.That(controller!.ExtAPI.TryGetOrganization(Organization_Id.Parse("LocalController"), out var organization) &&
                        organization is Organization, Is.True, "the controller's organization is not there");

            var account = await controller.ExtAPI.CreateUser(
                                    User_Id.Parse(Name),
                                    I18NString.Create(Languages.en, Name),
                                    SimpleEMailAddress.Parse($"{Name}@localhost"),
                                    User2OrganizationEdgeLabel.IsMember,
                                    (Organization) organization!,
                                    Password:                  password,
                                    SkipDefaultNotifications:  true,
                                    SkipNewUserEMail:          true,
                                    SkipNewUserNotifications:  true,
                                    AcceptedEULA:              DateTimeOffset.UtcNow.AddSeconds(-1),
                                    IsAuthenticated:           true
                                );

            Assert.That(account,                                                          Is.Not.Null, $"the account '{Name}' was not made");
            Assert.That(controller.ExtAPI.TryGetUser(User_Id.Parse(Name), out var stored),  Is.True);

            if (Role is not null)
            {

                Assert.That(controller.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group), Is.True,
                            $"the controller has no group '{Role}'");

                var joined = await controller.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!);

                Assert.That(joined.IsSuccess, Is.True, $"'{Name}' could not be put in '{Role}'");

            }

            // Signed in the way a browser is, at the HTTPExt API, and carrying
            // the session cookie from there on: one password check rather than
            // one per request.
            var client   = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                               BaseAddress  = address,
                               Timeout      = TimeSpan.FromSeconds(30)
                           };

            var signedIn = await client.PostAsync($"{LocalController.ExtAPIPath.ToString().TrimEnd('/')}/login",
                                                  new FormUrlEncodedContent([
                                                      new KeyValuePair<String, String>("login",     Name),
                                                      new KeyValuePair<String, String>("password",  password)
                                                  ]));

            Assert.That(signedIn.IsSuccessStatusCode, Is.True, $"'{Name}' could not sign in: {(Int32) signedIn.StatusCode}");

            return client;

        }

        #endregion

        #region (private static) Post(Client, Path, JSON)

        private static Task<HttpResponseMessage> Post(HttpClient  Client,
                                                      String      Path,
                                                      String      JSON)

            => Client.PostAsync(Path, new StringContent(JSON, Encoding.UTF8, "application/json"));

        #endregion

        #region (private static) LocalControllerAccessControl()

        /// <summary>
        /// The controller's resources and roles, as a node told nothing else
        /// puts them together.
        /// </summary>
        private static AccessControl LocalControllerAccessControl()
        {

            Assert.That(AccessControl.TryCombine(LocalControllerAccess.Resources, LocalControllerAccess.Roles, null, null,
                                                 "local controller", out var access, out _, out var error),
                        Is.True, error);

            return access!;

        }

        #endregion


        #region ALocalControllerKnowsItsResourcesAndItsFourRoles()

        /// <summary>
        /// The node brings the viewer, the support and the administrators, and
        /// the local controller its CPO - and the line up to the CSMS and the
        /// charging stations as resources beside the node's.
        /// </summary>
        [Test]
        public void ALocalControllerKnowsItsResourcesAndItsFourRoles()
        {

            var lc = AController();

            Assert.Multiple(() => {
                Assert.That(lc.Roles,             Is.EqualTo(new[] { "viewer", "support", "cpo", WWCPNode.AdminRole }));
                Assert.That(lc.Access.Resources,  Is.EqualTo(new[] { "configuration", "dns", "nts", "certificates", "ssh", "log",
                                                                     "node", "reset", "reset-immediate", "state", "firmware", "signatures",
                                                                     "csms", "stations", "openadr" }));
            });

        }

        #endregion

        #region EachRoleMayDoWhatItAlwaysMayDo(Role, Permission, Allowed)

        /// <summary>
        /// What each role could do before roles were data, permission by
        /// permission: the viewer looks, the CPO runs the site - its name and
        /// time servers, the line up to the CSMS and the charging stations -
        /// and only the administrators touch the certificates.
        /// </summary>
        [TestCase("viewer",       "configuration:read",  true)]
        [TestCase("viewer",       "dns:read",            true)]
        [TestCase("viewer",       "csms:read",           true)]
        [TestCase("viewer",       "stations:read",       true)]
        [TestCase("viewer",       "certificates:read",   true)]
        [TestCase("viewer",       "dns:edit",            false)]
        [TestCase("viewer",       "dns:run",             false)]
        [TestCase("viewer",       "csms:edit",           false)]
        [TestCase("viewer",       "stations:edit",       false)]
        [TestCase("viewer",       "openadr:read",        true)]
        [TestCase("viewer",       "openadr:edit",        false)]
        [TestCase("viewer",       "certificates:edit",   false)]

        [TestCase("cpo",          "certificates:read",   true)]
        [TestCase("cpo",          "dns:edit",            true)]
        [TestCase("cpo",          "dns:run",             true)]
        [TestCase("cpo",          "nts:edit",            true)]
        [TestCase("cpo",          "nts:run",             true)]
        [TestCase("cpo",          "csms:edit",           true)]
        [TestCase("cpo",          "stations:edit",       true)]
        [TestCase("cpo",          "openadr:edit",        true)]
        [TestCase("cpo",          "certificates:edit",   false)]
        [TestCase("cpo",          "configuration:edit",  false)]
        [TestCase("cpo",          "node:run",            true)]
        [TestCase("cpo",          "reset:run",           true)]
        [TestCase("cpo",          "reset-immediate:run", true)]
        [TestCase("cpo",          "state:run",           true)]
        [TestCase("cpo",          "firmware:edit",       true)]
        [TestCase("cpo",          "signatures:edit",     false)]
        [TestCase("cpo",          "log:read",            true)]

        [TestCase("viewer",       "log:read",            true)]
        [TestCase("viewer",       "state:read",          true)]
        [TestCase("viewer",       "reset:run",           false)]

        [TestCase("support",      "state:read",          true)]
        [TestCase("support",      "state:run",           true)]
        [TestCase("support",      "reset:run",           true)]
        [TestCase("support",      "reset-immediate:run", false)]
        [TestCase("support",      "log:read",            false)]
        [TestCase("support",      "configuration:read",  false)]
        [TestCase("support",      "csms:read",           false)]
        [TestCase("support",      "stations:read",       false)]

        [TestCase("systemadmin",  "certificates:edit",   true)]
        [TestCase("systemadmin",  "stations:edit",       true)]
        [TestCase("systemadmin",  "csms:edit",           true)]
        public void EachRoleMayDoWhatItAlwaysMayDo(String Role, String Permission, Boolean Allowed)
        {

            Assert.That(protocols.WWCP.Node.Web.Permission.TryParse(Permission, out var permission, out var error), Is.True, error);

            Assert.That(LocalControllerAccessControl().RoleNamed(Role)!.Allows(permission.Resource, permission.Operation), Is.EqualTo(Allowed));

        }

        #endregion


        #region ACPOMayLookAtTheCertificatesAndIsToldWhoMayChangeThem()

        /// <summary>
        /// Over the wire, as a browser signed in as a CPO sees it: the page
        /// opens, a signing request is refused with the role to ask for, and
        /// what the browser is told it may do says the same beforehand.
        /// </summary>
        [Test]
        public async Task ACPOMayLookAtTheCertificatesAndIsToldWhoMayChangeThem()
        {

            await TestPorts.StartedOnFreshPorts(() => AController());

            using var cpo    = await SignedInAs("cpo1", "cpo");

            var looked       = await cpo.GetAsync("api/v1/configuration/ocpp-server/certificates");
            var requested    = await Post(cpo, "api/v1/configuration/ocpp-server/certificates", "{}");
            var refusal      = await requested.Content.ReadAsStringAsync();
            var me           = JObject.Parse(await (await cpo.GetAsync("api/v1/auth/me")).Content.ReadAsStringAsync());
            var permissions  = me["permissions"]!.Values<String>().OfType<String>().ToArray();

            Assert.Multiple(() => {
                Assert.That(looked.StatusCode,               Is.EqualTo(HttpStatusCode.OK));
                Assert.That(requested.StatusCode,            Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                         Does.Contain("This needs the systemadmin role."));
                Assert.That(me["roles"]!.Values<String>(),   Is.EqualTo(new[] { "cpo" }));
                Assert.That(permissions,                     Does.Contain("stations:edit").And.Contain("csms:edit").And.Contain("dns:run").And.Contain("certificates:read"));
                Assert.That(permissions,                     Does.Not.Contain("certificates:edit").And.Not.Contain("configuration:edit"));
                Assert.That(permissions.Any(permission => permission.StartsWith('*')),
                            Is.False,
                            "spelt out resource by resource, so that a page asking \"dns:read\" need not know what \"*\" is");
            });

        }

        #endregion

        #region ARoleFromTheConfigurationFileIsHeardByTheAPI()

        /// <summary>
        /// A role nobody compiled in: the file names it - with a resource of
        /// the local controller's own beside one of the node's - the start
        /// makes its group, and a route asking for a permission lets it in or
        /// not by what the file says it carries.
        /// </summary>
        [Test]
        public async Task ARoleFromTheConfigurationFileIsHeardByTheAPI()
        {

            await TestPorts.StartedOnFreshPorts(() => AController(new JObject(
                                                                      new JProperty("nts",    new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                                      new JProperty("roles",  new JObject(
                                                                          new JProperty("helpdesk", new JArray("dns:read", "stations:read"))
                                                                      ))
                                                                  )));

            using var helpdesk = await SignedInAs("helper", "helpdesk");

            var dns            = await helpdesk.GetAsync("api/v1/configuration/dns");
            var stations       = await helpdesk.GetAsync("api/v1/configuration/ocpp-server/stations");
            var csms           = await helpdesk.GetAsync("api/v1/configuration/csms");
            var refusal        = await csms.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(controller!.Roles,   Is.EqualTo(new[] { "viewer", "support", "cpo", "helpdesk", WWCPNode.AdminRole }));
                Assert.That(dns.StatusCode,      Is.EqualTo(HttpStatusCode.OK));
                Assert.That(stations.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(csms.StatusCode,     Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,             Does.Contain("This needs the viewer or cpo or systemadmin role."),
                            "the file's role carries dns:read and stations:read and nothing else, so it is not among the ones to ask for");
            });

        }

        #endregion

        #region ARoleNamingAResourceThisControllerDoesNotHaveStopsTheStart()

        /// <summary>
        /// A role in the file that names a resource the local controller does
        /// not have - a vehicle's, say - stops it being built, and says which
        /// resources it does have: the node's, and the two it hands in.
        /// </summary>
        [Test]
        public void ARoleNamingAResourceThisControllerDoesNotHaveStopsTheStart()
        {

            var refused = Assert.Throws<InvalidOperationException>(() => AController(new JObject(
                              new JProperty("nts",    new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                              new JProperty("roles",  new JObject(
                                  new JProperty("support", new JArray("vehicle:read"))
                              ))
                          )));

            Assert.That(refused!.Message, Does.Contain("'vehicle'").And.Contain("csms").And.Contain("stations"));

        }

        #endregion

        #region EveryRouteAsksForItsOwnPermission(Method, Path, Permission)

        /// <summary>
        /// Every route of the API, and the one permission it asks for - read
        /// off the refusal an account in no role at all is given, which names
        /// every role that may. The configuration file adds a role for each
        /// operation on each resource, "r-dns-edit" and so on, so that the
        /// refusal names exactly one of them: the permission the route asks.
        /// </summary>
        /// <remarks>
        /// Refusals only: an account in no role is turned away before anything
        /// is read, so a route that would synchronise the clock or remove a
        /// certificate does neither here.
        /// </remarks>
        [TestCase("GET",     "api/v1/configuration",                                     "configuration:read")]
        [TestCase("GET",     "api/v1/configuration/dns",                                 "dns:read")]
        [TestCase("PUT",     "api/v1/configuration/dns",                                 "dns:edit")]
        [TestCase("POST",    "api/v1/configuration/dns/query",                           "dns:run")]
        [TestCase("GET",     "api/v1/configuration/nts",                                 "nts:read")]
        [TestCase("PUT",     "api/v1/configuration/nts",                                 "nts:edit")]
        [TestCase("POST",    "api/v1/configuration/nts/sync",                            "nts:run")]
        [TestCase("POST",    "api/v1/configuration/nts/test",                            "nts:run")]
        [TestCase("GET",     "api/v1/configuration/csms",                                "csms:read")]
        [TestCase("PUT",     "api/v1/configuration/csms",                                "csms:edit")]
        [TestCase("PUT",     "api/v1/configuration/csms/credentials",                    "csms:edit")]
        [TestCase("DELETE",  "api/v1/configuration/csms/credentials",                    "csms:edit")]
        [TestCase("GET",     "api/v1/configuration/openADR",                             "openadr:read")]
        [TestCase("PUT",     "api/v1/configuration/openADR",                             "openadr:edit")]
        [TestCase("PUT",     "api/v1/configuration/openADR/credentials",                 "openadr:edit")]
        [TestCase("DELETE",  "api/v1/configuration/openADR/credentials",                 "openadr:edit")]
        [TestCase("POST",    "api/v1/configuration/openADR/poll",                        "openadr:run")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server",                         "stations:read")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server",                         "stations:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/stations",                "stations:read")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/stations",                "stations:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/stations/cs001",          "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/stations/cs001",          "stations:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/stations/cs001/totp",     "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/stations/cs001/totp",     "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/stations/cs001/password", "stations:edit")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/stations/cs001/keyPins",  "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/stations/cs001/keyPins/" + "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff", "stations:edit")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/groups",                  "stations:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/groups/site",             "stations:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/groups/site",             "stations:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/certificates",            "certificates:read")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/certificates",            "certificates:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/certificates/k1/csr",     "certificates:read")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/certificates/k1",         "certificates:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/certificates/k1",         "certificates:edit")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/serverKeys",              "certificates:edit")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/serverKeys/switch",       "certificates:edit")]
        [TestCase("GET",     "api/v1/configuration/ocpp-server/trust",                   "certificates:read")]
        [TestCase("POST",    "api/v1/configuration/ocpp-server/trust",                   "certificates:edit")]
        [TestCase("PUT",     "api/v1/configuration/ocpp-server/trust/t1",                "certificates:edit")]
        [TestCase("DELETE",  "api/v1/configuration/ocpp-server/trust/t1",                "certificates:edit")]
        [TestCase("GET",     "api/v1/configuration/ssh",                                 "ssh:read")]
        [TestCase("PUT",     "api/v1/configuration/ssh",                                 "ssh:edit")]
        [TestCase("GET",     "api/v1/certificates",                                      "certificates:read")]
        [TestCase("POST",    "api/v1/certificates",                                      "certificates:edit")]
        [TestCase("POST",    "api/v1/certificates/reload",                               "certificates:edit")]
        [TestCase("GET",     "api/v1/certificates/c1",                                   "certificates:read")]
        [TestCase("PATCH",   "api/v1/certificates/c1",                                   "certificates:edit")]
        [TestCase("DELETE",  "api/v1/certificates/c1",                                   "certificates:edit")]
        [TestCase("GET",     "api/v1/logs",                                              "log:read")]
        [TestCase("GET",     "api/v1/state",                                             "state:read")]
        public async Task EveryRouteAsksForItsOwnPermission(String Method, String Path, String Permission)
        {

            var roles = new JObject();

            foreach (var resource in new[] { "configuration", "dns", "nts", "certificates", "ssh", "log",
                                               "node", "reset", "reset-immediate", "state", "firmware", "signatures",
                                               "csms", "stations", "openadr" })
                foreach (var operation in new[] { "read", "edit", "run" })
                    roles.Add($"r-{resource}-{operation}", new JArray($"{resource}:{operation}"));

            await TestPorts.StartedOnFreshPorts(() => AController(new JObject(
                                                                      new JProperty("nts",    new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                                      new JProperty("roles",  roles)
                                                                  )));

            using var nobody  = await SignedInAs("nobody1", null);

            var response      = await nobody.SendAsync(new HttpRequestMessage(new HttpMethod(Method), Path) {
                                                           Content = Method == "GET"
                                                                         ? null
                                                                         : new StringContent("{}", Encoding.UTF8, "application/json")
                                                       });

            var refusal       = await response.Content.ReadAsStringAsync();

            // "This needs the r-dns-edit or cpo or systemadmin role." - the roles
            // of the file's that may, of which there has to be exactly one.
            var named         = System.Text.RegularExpressions.Regex.Matches(refusal, @"\br-[a-z]+-[a-z]+\b").
                                                                 Select(match => match.Value).
                                                                 Distinct().
                                                                 ToArray();

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,  Is.EqualTo(HttpStatusCode.Forbidden), refusal);
                Assert.That(named,                Is.EqualTo(new[] { "r-" + Permission.Replace(':', '-') }),
                            $"{Method} {Path} asks for something else than {Permission}: {refusal}");
            });

        }

        #endregion

        #region TheClockIsForAnybodySignedIn()

        /// <summary>
        /// Whether the time here is worth anything is for anybody signed in -
        /// an account in no role at all included. Everything that is a
        /// resource is not, the log included.
        /// </summary>
        [Test]
        public async Task TheClockIsForAnybodySignedIn()
        {

            await TestPorts.StartedOnFreshPorts(() => AController());

            using var nobody   = await SignedInAs("nobody1", null);

            var clock          = await nobody.GetAsync("api/v1/clock");
            var configuration  = await nobody.GetAsync("api/v1/configuration");
            var refusal        = await configuration.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(clock.StatusCode,          Is.EqualTo(HttpStatusCode.OK));
                Assert.That(configuration.StatusCode,  Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(refusal,                   Does.Contain("This needs the viewer or cpo or systemadmin role."));
            });

        }

        #endregion

        #region TheLogIsForWhoeverMayReadIt()

        /// <summary>
        /// The log shows the stations and their connections: it needs
        /// "log:read", which the viewer has and the support has not - nor an
        /// account in no role, which once read it like the clock. The session
        /// tells the page so, as it tells the command line over SSH.
        /// </summary>
        [Test]
        public async Task TheLogIsForWhoeverMayReadIt()
        {

            await TestPorts.StartedOnFreshPorts(() => AController());

            using var nobody   = await SignedInAs("nobody2",  null);
            using var support  = await SignedInAs("support1", "support");
            using var viewer   = await SignedInAs("viewer1",  "viewer");

            var ofNobody       = await nobody. GetAsync("api/v1/logs");
            var ofSupport      = await support.GetAsync("api/v1/logs");
            var ofViewer       = await viewer. GetAsync("api/v1/logs");
            var refusal        = await ofSupport.Content.ReadAsStringAsync();

            var supportMe      = JObject.Parse(await support.GetStringAsync("api/v1/auth/me"));
            var viewerMe       = JObject.Parse(await viewer. GetStringAsync("api/v1/auth/me"));

            Assert.Multiple(() => {
                Assert.That(ofNobody.StatusCode,                   Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(ofSupport.StatusCode,                  Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(ofViewer.StatusCode,                   Is.EqualTo(HttpStatusCode.OK));
                Assert.That(refusal,                               Does.Contain("This needs the viewer or cpo or systemadmin role."));
                Assert.That(supportMe["mayReadTheLog"]?.Value<Boolean>(), Is.False);
                Assert.That(viewerMe ["mayReadTheLog"]?.Value<Boolean>(), Is.True);
            });

        }

        #endregion

        #region TheSupportSeesWhatIsGoingOnAndNoneOfTheConfiguration()

        /// <summary>
        /// "state:read" shows whether the line up to the CSMS stands and which
        /// charging stations were seen when - and nothing of where the line
        /// goes, whom the station port lets in or how: the support reads it,
        /// and is refused the configuration it is apart from. A station seen
        /// stays seen after it went away, no longer connected.
        /// </summary>
        [Test]
        public async Task TheSupportSeesWhatIsGoingOnAndNoneOfTheConfiguration()
        {

            const String stationPassword = "a-password-long-enough-for-ocpp";

            await TestPorts.StartedOnFreshPorts(() => AController(new JObject(
                                                                      new JProperty("nts",         new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),
                                                                      new JProperty("csms",        new JObject(
                                                                          new JProperty("enabled",           false),
                                                                          new JProperty("url",               "wss://csms.example.org/ocpp")
                                                                      )),
                                                                      new JProperty("ocppServer",  new JObject(
                                                                          new JProperty("enabled",           true),
                                                                          new JProperty("address",           "127.0.0.1"),
                                                                          new JProperty("port",              TestPorts.Free()),
                                                                          new JProperty("securityProfiles",  new JArray(1)),
                                                                          new JProperty("subprotocols",      new JArray("ocpp2.1"))
                                                                      ))
                                                                  )));

            Assert.That(controller!.StationLogins.TrySetPassword("cs001", stationPassword, null, "Ladepunkt 1", out _, out var error), Is.True, error);

            using var support  = await SignedInAs("support2", "support");
            using var station  = new System.Net.WebSockets.ClientWebSocket();
            using var timeout  = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            station.Options.AddSubProtocol("ocpp2.1");
            station.Options.SetRequestHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"cs001:{stationPassword}")));

            await station.ConnectAsync(new Uri($"ws://127.0.0.1:{controller.OCPPServerSettings.TCPPort}"), timeout.Token);

            var whileThere     = await support.GetAsync("api/v1/state");
            var text           = await whileThere.Content.ReadAsStringAsync();
            var state          = JObject.Parse(text);
            var seen           = state["stations"]?["seen"] as JArray;

            var csms           = await support.GetAsync("api/v1/configuration/csms");
            var stations       = await support.GetAsync("api/v1/configuration/ocpp-server/stations");

            Assert.Multiple(() => {
                Assert.That(whileThere.StatusCode,                         Is.EqualTo(HttpStatusCode.OK), text);
                Assert.That(state["csms"]?["connected"]?.Value<Boolean>(), Is.False);
                Assert.That(state["stations"]?["connected"]?.Value<Int32>(), Is.EqualTo(1));
                Assert.That(seen?.Select(entry => entry["id"]?.Value<String>()), Is.EqualTo(new[] { "cs001" }));
                Assert.That(seen?[0]?["connected"]?.Value<Boolean>(),      Is.True);
                Assert.That(seen?[0]?["over"]?.Value<String>(),            Is.EqualTo("WebSocket"));
                Assert.That(seen?[0]?["lastSeen"]?.Value<String>(),        Is.Not.Null);
                Assert.That(text,                                          Does.Not.Contain("csms.example.org").And.Not.Contain("127.0.0.1").And.Not.Contain(stationPassword),
                            "the state says something of the configuration");
                Assert.That(csms.StatusCode,                               Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(stations.StatusCode,                           Is.EqualTo(HttpStatusCode.Forbidden));
            });

            try
            {
                await station.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            catch (Exception)
            { }

            JObject? afterwards = null;

            for (var attempt = 0; attempt < 50; attempt++)
            {

                afterwards = JObject.Parse(await support.GetStringAsync("api/v1/state"));

                if (afterwards["stations"]?["connected"]?.Value<Int32>() == 0)
                    break;

                await Task.Delay(100);

            }

            Assert.Multiple(() => {
                Assert.That(afterwards!["stations"]?["connected"]?.Value<Int32>(),          Is.EqualTo(0), "the station that went away is still connected");
                Assert.That(afterwards!["stations"]?["seen"]?[0]?["id"]?.Value<String>(),   Is.EqualTo("cs001"));
                Assert.That(afterwards!["stations"]?["seen"]?[0]?["connected"]?.Value<Boolean>(), Is.False);
            });

        }

        #endregion

    }

}
